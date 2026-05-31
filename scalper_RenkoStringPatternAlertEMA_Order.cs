#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion


// =============================================================================
// STRATEGY: scalper_RenkoStringPatternAlertEMA_Order v1.4.6
// AUTHOR:   Albert Feng / Drafted with help from Claude
// REPLACES: v1.4.5
// =============================================================================
//
// v1.4.6 CHANGES vs v1.4.5 — trailing stop + partial fill cancel
// --------------------------------------------------------------
//
// CHANGE 1 — NEW: EnableTrailingStop (default false)
//   When false: behavior is identical to v1.4.5.
//     SetStopLoss(StopLossBricks) — fixed hard stop.
//     SetProfitTarget(ProfitTargetBricks) — fixed target.
//
//   When true:
//     SetTrailStop(TrailStopBricks) replaces SetStopLoss entirely.
//     SetStopLoss and SetTrailStop CANNOT coexist on the same signal
//     (SetStopLoss always takes precedence per NT docs). Therefore
//     when trail is enabled, only SetTrailStop + SetProfitTarget are set.
//     The trail IS the hard stop — it just moves in your favor.
//
//   Live trading: SetTrailStop updates tick-by-tick (real broker order).
//   Backtest:     SetTrailStop updates on Renko bar close only.
//                 TickReplay is NOT compatible with Renko bars.
//                 Use Playback Connection for true live-equivalent backtest.
//                 High Order Fill Resolution (Tick=1) improves entry/exit
//                 fill accuracy but does NOT improve SetTrailStop granularity
//                 in backtest (set methods are tied to primary series).
//                 Backtest trail results are therefore conservative — real
//                 live performance will be equal or better.
//
// CHANGE 2 — NEW: TrailStopBricks parameter (default = StopLossBricks value)
//   Trail distance in bricks. Only used when EnableTrailingStop=true.
//   Converted to ticks: ticks = TrailStopBricks * BarsPeriod.Value.
//   Independent of StopLossBricks — user may set different values.
//   Default intentionally mirrors StopLossBricks so existing configs
//   that simply enable the trail get familiar risk behavior.
//
// CHANGE 3 — StopLossBricks display label renamed to "Hard Stop Loss (BRICKS)"
//   Internal variable name StopLossBricks is UNCHANGED — no code refactor,
//   no CSV column rename, no backcompat break. Only the NT parameter dialog
//   label and description text are updated to reflect:
//     EnableTrailingStop=false → StopLossBricks is the fixed hard stop.
//     EnableTrailingStop=true  → StopLossBricks is unused (trail takes over).
//                                TrailStopBricks controls the stop distance.
//
// CHANGE 4 — NEW: Partial fill cancel in OnExecutionUpdate
//   On the FIRST PartFilled callback for our entry order:
//     → CancelOrder(workingEntryOrder) is called immediately.
//     → cancelSentOnPartial flag set to prevent duplicate cancel calls.
//     → actualFilledQty accumulates across all fill callbacks.
//     → Trail/stop/target bracket is sized to actualFilledQty, not OrderQuantity.
//   This protects against bracket inversion when:
//     (a) First batch fills, trail activates and moves with price,
//     (b) Price pulls back and fills remaining contracts at a price
//         that is now below/above the moved trail level.
//   Cancelling on first partial eliminates the late-fill bracket risk.
//   The race window (fills arriving before cancel reaches broker) is
//   milliseconds — acceptable and physically irreducible.
//
//   INIT log warns when OrderQuantity > 30 with trail enabled:
//     At that size, partial fills may result in fewer contracts than
//     requested. Remainder is cancelled on first fill for bracket safety.
//     Consider NQ (3-5 contracts) for equivalent MNQ exposure with
//     cleaner single fills.
//
// CHANGE 5 — actualFilledQty tracking
//   New private field: int actualFilledQty = 0.
//   Incremented in OnExecutionUpdate on every entry fill callback.
//   Reset to 0 in ResetOrderSubsystemToIdle.
//   Used in INIT log, CSV rows, and Print statements for full traceability.
//
// CHANGE 6 — CSV schema bumped to 1.4.6
//   File names unchanged. Header now includes:
//     # EnableTrailingStop=...
//     # TrailStopBricks=...
//   COLUMN LAYOUT in all three CSVs is UNCHANGED.
//   Orders CSV: SUBMIT rows now include trail config in Extra field.
//   Orders CSV: new event type PARTIAL_FILL_CANCEL_REMAINDER.
//
// CHANGE 7 — INIT log updated
//   Prints EnableTrailingStop, TrailStopBricks, and qty warning when
//   OrderQuantity > 30 with trail enabled.
//   Prints backtest accuracy advisory for trail.
//
// CHANGE 8 — Internal signal names unchanged from v1.4.5
//   ENTRY_LONG_SIGNAL  = "EntryV145Long"
//   ENTRY_SHORT_SIGNAL = "EntryV145Short"
//   (kept as v1.4.5 to avoid order log collisions with existing history)
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_RenkoStringPatternAlertEMA_Order : Strategy
    {
        // =====================================================================
        // ENUMS
        // =====================================================================
        public enum OrderEntryType
        {
            Limit,
            Market
        }

        public enum EmaFilterModeEnum
        {
            NoFilter,
            OpenAbove,
            OpenBelow
        }

        public enum TradeDirectionEnum
        {
            Long,
            Short
        }

        private enum OrderSubState
        {
            Idle,
            Working,
            Position
        }

        #region Variables

        private List<int> bricks = new List<int>();

        private StringBuilder outcomeString         = new StringBuilder();
        private StringBuilder tradedOutcomeString   = new StringBuilder();
        private StringBuilder postAlertOutcomeString = new StringBuilder();
        private int pendingPostAlertCaptures = 0;

        private bool isAlerted = false;

        private int dailyOccurrenceNumber       = 0;
        private int dailyTradedOccurrenceNumber = 0;
        private int dailyAlertNumber            = 0;
        private int dailyOrderNumber            = 0;

        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        private DateTime currentTradingDateNy = DateTime.MinValue;
        private TimeZoneInfo nyTz;
        private const int RESET_HOUR_NY   = 9;
        private const int RESET_MINUTE_NY = 30;

        private int[]        compiledPattern      = null;
        private int[]        compiledFollowUp     = null;
        private List<string> compiledAlertPatterns = null;

        private bool configValid = false;

        private EMA ema1;
        private EMA ema2;

        private class PendingOccurrence
        {
            public int      PatternStartIndex;
            public int      PatternEndIndex;
            public int      BricksWatched;
            public bool     FollowUpMismatch;
            public DateTime OccurrenceTime;
            public double   PatternEndPrice;

            public bool   EmaQualified;
            public double Ema1AtDetect;
            public double Ema2AtDetect;
            public double OpenAtDetect;

            public int OccurrenceNumberAtDetect;
            public int TradedOccurrenceNumberAtDetect;
        }

        private List<PendingOccurrence> pendingOccurrences = new List<PendingOccurrence>();

        // =====================================================================
        // ORDER SUBSYSTEM
        // =====================================================================
        private OrderSubState orderState            = OrderSubState.Idle;

        private Order workingEntryOrder             = null;
        private int   bricksSinceEntrySubmit        = 0;
        private int   entrySubmitBarIdx             = -1;
        private double entryReferencePrice          = 0.0;
        private double entryLimitPrice              = 0.0;
        private int   entryOrderNumber              = 0;

        private bool  entryIsLong                  = true;

        private bool cancelRequested               = false;
        private bool cancelSentOnPartial           = false;   // [v1.4.6] prevent duplicate cancel on partial
        private int  bricksInCurrentState          = 0;

        private OrderEntryType currentWorkingOrderType = OrderEntryType.Limit;
        private DateTime marketSubmitWallClock     = DateTime.MinValue;
        private bool marketTimeoutLogged           = false;
        private int consecutiveLosses              = 0;
        private bool safetyBrakeTripped            = false;
        private int lastEntryOrderNumber           = 0;
        private DateTime lastDesyncLogTime         = DateTime.MinValue;

        private double actualFillPrice             = 0.0;
        private double computedStopPrice           = 0.0;
        private double computedTargetPrice         = 0.0;
        private double actualExitPrice             = 0.0;

        // [v1.4.6] actual filled qty — may be less than OrderQuantity on partial
        private int    actualFilledQty             = 0;

        private bool forceFlatInProgress           = false;

        private const string ENTRY_LONG_SIGNAL  = "EntryV145Long";
        private const string ENTRY_SHORT_SIGNAL = "EntryV145Short";

        #endregion

        // =====================================================================
        // OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Renko string-pattern alerter+order (v1.4.6). Adds EnableTrailingStop with tick-by-tick live trail (SetTrailStop), partial fill remainder cancel on first fill, and Hard Stop Loss label. All v1.4.5 features retained.";
                Name        = "scalper_RenkoStringPatternAlertEMA_Order";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = true;
                BarsRequiredToTrade                       = 5;
                IsInstantiatedOnEachOptimizationIteration = true;

                PatternToMatch   = "10";
                FollowUpPattern  = "0";
                AlertPattern     = "0,1";

                EMA1Period       = 20;
                EMA2Period       = 9;
                EmaFilterMode    = EmaFilterModeEnum.NoFilter;

                AlertSoundCount   = 3;
                AlertReminderSecs = 1;

                EnableChartMarkers = true;
                ShowOutcomeLabels  = true;

                AuditLogPath = @"C:\temp";
                MaxBitsKept  = 5000;

                EnableOrders         = false;
                TradeDirection       = TradeDirectionEnum.Short;
                OrderQuantity        = 1;
                OrderType            = OrderEntryType.Market;
                LimitUnderPoints     = 5.0;
                UnfilledCancelBricks = 1;
                StopLossBricks       = 0.2;    // Hard stop (fixed) when trail disabled
                ProfitTargetBricks   = 0.9;

                // [v1.4.6] Trailing stop defaults
                EnableTrailingStop   = false;
                TrailStopBricks      = 0.2;    // Default mirrors StopLossBricks

                MaxConsecutiveLosses = 99;

                EnableOrderHours   = false;
                OrderStartHourNY   = 9;
                OrderStartMinuteNY = 30;
                OrderEndHourNY     = 15;
                OrderEndMinuteNY   = 55;
                GoodTilDate        = DateTime.Today.AddDays(30);
            }
            else if (State == State.DataLoaded)
            {
                try
                {
                    nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                }
                catch (Exception ex)
                {
                    Print(string.Format("[INIT] WARN: NY timezone load failed: {0}. Using local.", ex.Message));
                    nyTz = TimeZoneInfo.Local;
                }

                ema1 = EMA(EMA1Period);
                ema2 = EMA(EMA2Period);
                configValid = TryCompilePatterns();
                ResetState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlertEMA_Order v1.4.6 at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));

                Print(string.Format("[INIT] BarsPeriod.BarsPeriodType = {0}", BarsPeriod.BarsPeriodType));
                Print(string.Format("[INIT] BarsPeriod.Value  = {0}  (brick size in ticks)", BarsPeriod.Value));
                Print(string.Format("[INIT] BarsPeriod.Value2 = {0}  (UniRenko/BetterRenko only)", BarsPeriod.Value2));
                Print(string.Format("[INIT] Instrument TickSize = {0}  -> brick size in price = {1}",
                    TickSize, BarsPeriod.Value * TickSize));
                Print(string.Format("[INIT] Run start (local): {0}   (UTC: {1})   (NY: {2})",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));

                if (!configValid)
                {
                    Print("[INIT] *** CONFIGURATION INVALID *** Strategy will not process bricks.");
                    Print("[INIT] Fix the parameter strings and re-enable.");
                    return;
                }

                Print(string.Format("[INIT] PatternToMatch  = \"{0}\" (length {1})", PatternToMatch, compiledPattern.Length));
                Print(string.Format("[INIT] FollowUpPattern = \"{0}\" (length {1})", FollowUpPattern, compiledFollowUp.Length));
                Print(string.Format("[INIT] AlertPattern (raw) = \"{0}\"", AlertPattern));
                Print(string.Format("[INIT] AlertPattern (parsed, {0} pattern(s)): [{1}]",
                    compiledAlertPatterns.Count, string.Join(", ", compiledAlertPatterns)));

                CheckForSuffixOverlaps();

                Print(string.Format("[INIT] *** EmaFilterMode = {0} ***", EmaFilterMode));
                switch (EmaFilterMode)
                {
                    case EmaFilterModeEnum.NoFilter:
                        Print("[INIT]   -> NO EMA filter. EVERY pattern is qualified.");
                        break;
                    case EmaFilterModeEnum.OpenAbove:
                        Print("[INIT]   -> Qualified when PatternEndOpen > EMA1 AND PatternEndOpen > EMA2.");
                        break;
                    case EmaFilterModeEnum.OpenBelow:
                        Print("[INIT]   -> Qualified when PatternEndOpen < EMA1 AND PatternEndOpen < EMA2.");
                        break;
                }
                Print(string.Format("[INIT] EMA1 period = {0}, EMA2 period = {1}", EMA1Period, EMA2Period));
                Print(string.Format("[INIT] *** TradeDirection = {0} ***", TradeDirection));
                if (TradeDirection == TradeDirectionEnum.Long)
                    Print("[INIT]   -> Entries via EnterLong/EnterLongLimit. Stop below entry, Target above.");
                else
                    Print("[INIT]   -> Entries via EnterShort/EnterShortLimit. Stop above entry, Target below.");

                Print("[INIT] ----- ORDER SUBSYSTEM -----");
                Print(string.Format("[INIT] EnableOrders        = {0}", EnableOrders));
                if (EnableOrders)
                {
                    Print(string.Format("[INIT] OrderQuantity       = {0} contract(s)", OrderQuantity));
                    Print(string.Format("[INIT] OrderType           = {0}", OrderType));
                    Print(string.Format("[INIT] LimitUnderPoints    = {0:F2} pt (ignored if Market)", LimitUnderPoints));
                    Print(string.Format("[INIT] UnfilledCancelBricks= {0} (LIMIT only)", UnfilledCancelBricks));

                    double brickTicks  = BarsPeriod.Value;
                    double brickPoints = brickTicks * TickSize;

                    double pointValue = 0.0;
                    try { pointValue = Instrument.MasterInstrument.PointValue; } catch { }

                    // [v1.4.6] Stop display depends on trail mode
                    Print("[INIT] === Order sizing summary ===");
                    Print(string.Format("[INIT]   Brick size:     {0:F1} ticks = {1:F2} points{2}",
                        brickTicks, brickPoints,
                        pointValue > 0 ? string.Format(" = ${0:F2}/contract", brickPoints * pointValue) : ""));

                    if (EnableTrailingStop)
                    {
                        double trailTicks  = TrailStopBricks * brickTicks;
                        double trailPoints = trailTicks * TickSize;
                        Print(string.Format("[INIT]   Trail stop:     {0:F2} bricks = {1:F1} ticks = {2:F2} points{3}",
                            TrailStopBricks, trailTicks, trailPoints,
                            pointValue > 0 ? string.Format(" = ${0:F2} initial RISK/contract", trailPoints * pointValue) : ""));
                        Print("[INIT]   Hard stop:     DISABLED (trail replaces fixed stop)");
                        Print("[INIT]   Trail updates: TICK-BY-TICK in live trading (real broker order).");
                        Print("[INIT]   Trail updates: BAR CLOSE ONLY in backtest (NT platform limitation).");
                        Print("[INIT]   Backtest advisory: TickReplay NOT compatible with Renko bars.");
                        Print("[INIT]   Use Playback Connection for live-equivalent trail backtest.");
                        Print("[INIT]   High Order Fill Resolution (Tick=1) improves entry/exit fills");
                        Print("[INIT]   but does NOT improve SetTrailStop granularity in backtest.");

                        if (OrderQuantity > 30)
                        {
                            Print("[INIT]");
                            Print(string.Format("[INIT] *** QTY WARNING *** OrderQuantity={0} with EnableTrailingStop=true.", OrderQuantity));
                            Print("[INIT]   Partial fills may result in fewer contracts than requested.");
                            Print("[INIT]   Remainder is cancelled on FIRST fill callback for bracket safety.");
                            Print("[INIT]   You may receive significantly fewer contracts than ordered.");
                            Print(string.Format("[INIT]   Consider NQ ({0}-{1} contracts) for equivalent MNQ exposure",
                                (int)Math.Ceiling(OrderQuantity / 10.0),
                                (int)Math.Ceiling(OrderQuantity / 10.0) + 1));
                            Print("[INIT]   with cleaner single-execution fills.");
                            Print("[INIT]");
                        }
                    }
                    else
                    {
                        double stopTicks   = StopLossBricks * brickTicks;
                        double stopPoints  = stopTicks * TickSize;
                        Print(string.Format("[INIT]   Hard stop loss: {0:F2} bricks = {1:F1} ticks = {2:F2} points{3}",
                            StopLossBricks, stopTicks, stopPoints,
                            pointValue > 0 ? string.Format(" = ${0:F2} RISK/contract", stopPoints * pointValue) : ""));
                    }

                    double targetTicks  = ProfitTargetBricks * brickTicks;
                    double targetPoints = targetTicks * TickSize;
                    Print(string.Format("[INIT]   Profit target:  {0:F2} bricks = {1:F1} ticks = {2:F2} points{3}",
                        ProfitTargetBricks, targetTicks, targetPoints,
                        pointValue > 0 ? string.Format(" = ${0:F2} REWARD/contract", targetPoints * pointValue) : ""));

                    double riskBricks = EnableTrailingStop ? TrailStopBricks : StopLossBricks;
                    if (riskBricks > 0)
                        Print(string.Format("[INIT]   Risk:Reward = 1:{0:F2}", ProfitTargetBricks / riskBricks));

                    if (OrderType == OrderEntryType.Limit)
                    {
                        if (TradeDirection == TradeDirectionEnum.Long)
                            Print(string.Format("[INIT]   Limit offset:   {0:F2} pt BELOW close (buy on retrace)", LimitUnderPoints));
                        else
                            Print(string.Format("[INIT]   Limit offset:   {0:F2} pt ABOVE close (sell on retrace)", LimitUnderPoints));
                    }
                    Print("[INIT] ============================");

                    Print(string.Format("[INIT] EnableTrailingStop  = {0}", EnableTrailingStop));
                    if (EnableTrailingStop)
                        Print(string.Format("[INIT] TrailStopBricks     = {0:F2} bricks", TrailStopBricks));

                    Print(string.Format("[INIT] MaxConsecutiveLosses = {0}{1}",
                        MaxConsecutiveLosses,
                        MaxConsecutiveLosses >= 99 ? " (effectively OFF)" : " (safety brake ACTIVE)"));

                    Print(string.Format("[INIT] EnableOrderHours    = {0}", EnableOrderHours));
                    if (EnableOrderHours)
                    {
                        Print(string.Format("[INIT] Order hours (NY)    = {0:D2}:{1:D2} to {2:D2}:{3:D2}",
                            OrderStartHourNY, OrderStartMinuteNY, OrderEndHourNY, OrderEndMinuteNY));
                    }
                    Print(string.Format("[INIT] GoodTilDate         = {0:yyyy-MM-dd}", GoodTilDate));

                    Print("[INIT] Safety layers active:");
                    Print("[INIT]   Layer 1: work-end logic at OrderEndHourNY");
                    Print("[INIT]   Layer 2: NT session-close auto-flat (IsExitOnSessionCloseStrategy=true)");
                    if (EnableTrailingStop)
                        Print("[INIT]   Layer 3: broker trail stop (SetTrailStop) — tick-by-tick in live trading");
                    else
                        Print("[INIT]   Layer 3: broker server-side bracket (SetStopLoss/SetProfitTarget)");
                    Print("[INIT]   Defensive: position-sync check every brick");
                    Print("[INIT]   Defensive: partial fill remainder cancelled on first fill");

                    Print("[INIT] Order sounds:");
                    Print("[INIT]   Entry filled        -> Alert4.wav");
                    Print("[INIT]   Profit target hit   -> Boxing Bell.wav");
                    Print("[INIT]   Stop/trail hit      -> Glass Break.wav");
                    Print("[INIT]   Entry cancelled     -> Alert3.wav");
                    Print("[INIT]   Force-flat workend  -> Alert2.wav");
                    Print("[INIT]   Connection lost     -> Alert2.wav");
                }
                else
                {
                    Print("[INIT] (orders disabled - alert-only mode)");
                }
                Print("[INIT] ---------------------------------");
            }
        }

        // =====================================================================
        // TryCompilePatterns
        // =====================================================================
        private bool TryCompilePatterns()
        {
            compiledPattern  = TryCompileOne(PatternToMatch,  "PatternToMatch");
            compiledFollowUp = TryCompileOne(FollowUpPattern, "FollowUpPattern");
            compiledAlertPatterns = ParseAlertPatternList(AlertPattern);

            return compiledPattern  != null
                && compiledFollowUp != null
                && compiledAlertPatterns != null
                && compiledAlertPatterns.Count > 0;
        }

        private List<string> ParseAlertPatternList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Print("[VALIDATE] *** AlertPattern is empty. Must contain at least one pattern. ***");
                return null;
            }

            var result   = new List<string>();
            var rawParts = raw.Split(',');
            foreach (var rawPart in rawParts)
            {
                string p = rawPart.Trim();
                if (p.Length == 0) continue;
                if (p.Length > 10)
                {
                    Print(string.Format("[VALIDATE] *** AlertPattern entry \"{0}\" is {1} chars; max 10. Skipping. ***", p, p.Length));
                    continue;
                }
                bool ok = true;
                for (int i = 0; i < p.Length; i++)
                {
                    if (p[i] != '0' && p[i] != '1')
                    {
                        Print(string.Format("[VALIDATE] *** AlertPattern \"{0}\" invalid char '{1}' at pos {2}. Skipping. ***", p, p[i], i));
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;
                if (result.Contains(p))
                {
                    Print(string.Format("[VALIDATE] WARN: duplicate AlertPattern \"{0}\" — keeping first.", p));
                    continue;
                }
                result.Add(p);
            }

            if (result.Count == 0)
            {
                Print("[VALIDATE] *** AlertPattern list produced 0 valid entries. Strategy cannot start. ***");
                return null;
            }
            return result;
        }

        private void CheckForSuffixOverlaps()
        {
            if (compiledAlertPatterns == null || compiledAlertPatterns.Count < 2) return;
            int warnCount = 0;
            for (int i = 0; i < compiledAlertPatterns.Count; i++)
            {
                for (int j = 0; j < compiledAlertPatterns.Count; j++)
                {
                    if (i == j) continue;
                    string a = compiledAlertPatterns[i];
                    string b = compiledAlertPatterns[j];
                    if (a.Length < b.Length && b.EndsWith(a))
                    {
                        string msg = string.Format(
                            "AlertPattern overlap: \"{0}\" is a suffix of \"{1}\". Both match the same brick tail.", a, b);
                        Print(string.Format("[VALIDATE] WARN: {0}", msg));
                        try { Log(msg, LogLevel.Warning); } catch { }
                        warnCount++;
                    }
                }
            }
            if (warnCount == 0)
                Print("[VALIDATE] AlertPattern list: no suffix overlaps detected.");
            else
                Print(string.Format("[VALIDATE] AlertPattern list: {0} suffix-overlap warning(s).", warnCount));
        }

        private int[] TryCompileOne(string s, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                Print(string.Format("[VALIDATE] *** {0} is empty. ***", fieldName));
                return null;
            }
            if (s.Length > 10)
            {
                Print(string.Format("[VALIDATE] *** {0} is {1} chars; max 10. ***", fieldName, s.Length));
                return null;
            }
            int[] result = new int[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                if      (s[i] == '0') result[i] = 0;
                else if (s[i] == '1') result[i] = 1;
                else
                {
                    Print(string.Format("[VALIDATE] *** {0} invalid char '{1}' at pos {2}. ***", fieldName, s[i], i));
                    return null;
                }
            }
            return result;
        }

        private bool EvaluateFilter(double patternEndOpen, double ema1Val, double ema2Val)
        {
            switch (EmaFilterMode)
            {
                case EmaFilterModeEnum.NoFilter:   return true;
                case EmaFilterModeEnum.OpenAbove:  return (patternEndOpen > ema1Val) && (patternEndOpen > ema2Val);
                case EmaFilterModeEnum.OpenBelow:  return (patternEndOpen < ema1Val) && (patternEndOpen < ema2Val);
                default: return false;
            }
        }

        // =====================================================================
        // OnBarUpdate
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            if (!configValid) return;

            if (EnableOrders)
                CheckPositionSync();

            // ---- Daily reset ----
            DateTime barTimeNy   = TimeZoneInfo.ConvertTime(Time[0], nyTz);
            int minuteOfDayNy    = barTimeNy.Hour * 60 + barTimeNy.Minute;
            int resetMin         = RESET_HOUR_NY * 60 + RESET_MINUTE_NY;

            DateTime effectiveTradingDate = minuteOfDayNy >= resetMin
                ? barTimeNy.Date
                : barTimeNy.Date.AddDays(-1);

            if (effectiveTradingDate != currentTradingDateNy)
            {
                if (currentTradingDateNy != DateTime.MinValue)
                    Print(string.Format("[RESET] New trading day {0:yyyy-MM-dd}. Bricks:{1} occ:{2} traded:{3} alerts:{4} orders:{5}",
                        effectiveTradingDate, bricks.Count, dailyOccurrenceNumber,
                        dailyTradedOccurrenceNumber, dailyAlertNumber, dailyOrderNumber));
                else
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}", effectiveTradingDate));

                currentTradingDateNy = effectiveTradingDate;
                ResetState();
            }

            // ---- Append brick bit ----
            int thisBit = (Close[0] > Open[0]) ? 1 : 0;
            bricks.Add(thisBit);

            if (bricks.Count > MaxBitsKept)
            {
                int removeCount = bricks.Count - MaxBitsKept;
                bricks.RemoveRange(0, removeCount);
                foreach (var po in pendingOccurrences)
                {
                    po.PatternStartIndex -= removeCount;
                    po.PatternEndIndex   -= removeCount;
                }
            }

            int    currentBrickIdx = bricks.Count - 1;
            double currentPrice    = Close[0];

            // ---- Working-state handling ----
            if (orderState == OrderSubState.Working && workingEntryOrder != null)
            {
                bricksSinceEntrySubmit++;

                if (currentWorkingOrderType == OrderEntryType.Limit)
                {
                    Print(string.Format("[ORDER] Working LIMIT brick tick: {0} of {1} (order #{2}, cancelRequested={3}, cancelSentOnPartial={4})",
                        bricksSinceEntrySubmit, UnfilledCancelBricks, entryOrderNumber, cancelRequested, cancelSentOnPartial));

                    if (bricksSinceEntrySubmit >= UnfilledCancelBricks && !cancelRequested && !cancelSentOnPartial)
                    {
                        Print(string.Format("[ORDER] LIMIT entry #{0} unfilled after {1} brick(s). Cancelling.",
                            entryOrderNumber, UnfilledCancelBricks));
                        WriteOrderRow("CANCEL_UNFILLED", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                        cancelRequested = true;
                        try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                        catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    }
                    else if ((cancelRequested || cancelSentOnPartial) && bricksSinceEntrySubmit >= UnfilledCancelBricks + 2)
                    {
                        Print(string.Format("[ORDER] *** LIMIT GRACE PERIOD EXPIRED *** Entry #{0}.", entryOrderNumber));
                        WriteOrderRow("FORCE_RESET_STUCK", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "callback_never_arrived");
                        if (Position.MarketPosition == MarketPosition.Flat)
                        {
                            ResetOrderSubsystemToIdle("force_reset_stuck_limit_broker_flat");
                        }
                        else
                        {
                            Print(string.Format("[CRITICAL] Grace expired but Position={0}. Force-flattening.", Position.MarketPosition));
                            WriteOrderRow("DESYNC_FORCE_FLAT", entryOrderNumber, "n/a", 0, 0, 0, "limit_grace_broker_not_flat");
                            try
                            {
                                if (Position.MarketPosition == MarketPosition.Long)
                                    ExitLong(Math.Abs(Position.Quantity), "ExitDesync", ENTRY_LONG_SIGNAL);
                                else if (Position.MarketPosition == MarketPosition.Short)
                                    ExitShort(Math.Abs(Position.Quantity), "ExitDesyncShort", ENTRY_SHORT_SIGNAL);
                            }
                            catch (Exception ex) { Print(string.Format("[CRITICAL] Exit (desync) failed: {0}", ex.Message)); }
                        }
                    }
                }
                else
                {
                    TimeSpan elapsed = DateTime.Now - marketSubmitWallClock;
                    if (elapsed.TotalSeconds > 30 && !marketTimeoutLogged)
                    {
                        Print(string.Format("[CRITICAL] MARKET order #{0} no fill after {1:F1}s. NOT cancelling.",
                            entryOrderNumber, elapsed.TotalSeconds));
                        WriteOrderRow("MARKET_HEARTBEAT_WARN", entryOrderNumber, "MARKET", 0, 0, 0,
                            string.Format("elapsed_seconds={0:F1}", elapsed.TotalSeconds));
                        marketTimeoutLogged = true;
                    }
                }
            }

            if (orderState != OrderSubState.Idle)
            {
                bricksInCurrentState++;
                if (bricksInCurrentState % 10 == 0)
                    Print(string.Format("[HEARTBEAT] orderState={0} for {1} bricks. working={2} cancelReq={3} cancelPartial={4} bricksSinceSubmit={5} broker={6} filledQty={7}",
                        orderState, bricksInCurrentState,
                        workingEntryOrder == null ? "null" : "set",
                        cancelRequested, cancelSentOnPartial,
                        bricksSinceEntrySubmit, Position.MarketPosition, actualFilledQty));
            }
            else
            {
                bricksInCurrentState = 0;
            }

            // ---- Work-end force-flat ----
            if (EnableOrders && EnableOrderHours && !forceFlatInProgress)
            {
                if (IsBeyondOrderEnd(barTimeNy))
                {
                    if (orderState == OrderSubState.Working)
                    {
                        Print(string.Format("[ORDER] Work-end: cancelling working entry #{0}.", entryOrderNumber));
                        WriteOrderRow("CANCEL_WORKEND", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                        PlayOrderSound("Alert2.wav", "Force-flat work-end (cancel working entry)");
                        forceFlatInProgress = true;
                        try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                        catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    }
                    else if (orderState == OrderSubState.Position)
                    {
                        Print("[ORDER] Work-end: submitting market exit.");
                        WriteOrderRow("FORCEFLAT_WORKEND", entryOrderNumber, "MARKET_EXIT", actualFillPrice, computedStopPrice, computedTargetPrice, "");
                        PlayOrderSound("Alert2.wav", "Force-flat work-end (exit position)");
                        forceFlatInProgress = true;
                        try
                        {
                            if (entryIsLong)
                                ExitLong(OrderQuantity, "ExitWorkEnd", ENTRY_LONG_SIGNAL);
                            else
                                ExitShort(OrderQuantity, "ExitWorkEndShort", ENTRY_SHORT_SIGNAL);
                        }
                        catch (Exception ex) { Print(string.Format("[ORDER] Exit (workend) error: {0}", ex.Message)); }
                    }
                    else if (orderState == OrderSubState.Idle && Position.MarketPosition != MarketPosition.Flat)
                    {
                        Print(string.Format("[CRITICAL] Work-end: Idle but Position={0}. Force-flatten ghost.", Position.MarketPosition));
                        WriteOrderRow("FORCEFLAT_WORKEND_GHOST", lastEntryOrderNumber, "MARKET_EXIT", 0, 0, 0, "ghost_position_at_workend");
                        PlayOrderSound("Alert2.wav", "Force-flat ghost position at work-end");
                        forceFlatInProgress = true;
                        try
                        {
                            if (Position.MarketPosition == MarketPosition.Long)
                                ExitLong(Math.Abs(Position.Quantity), "ExitWorkEndGhost", ENTRY_LONG_SIGNAL);
                            else if (Position.MarketPosition == MarketPosition.Short)
                                ExitShort(Math.Abs(Position.Quantity), "ExitWorkEndGhostShort", ENTRY_SHORT_SIGNAL);
                        }
                        catch (Exception ex) { Print(string.Format("[CRITICAL] Ghost work-end exit failed: {0}", ex.Message)); }
                    }
                }
            }

            // ---- Resolve pending occurrences ----
            var resolved = new List<PendingOccurrence>();
            foreach (var po in pendingOccurrences)
            {
                if (currentBrickIdx <= po.PatternEndIndex) continue;
                int expectedBit = compiledFollowUp[po.BricksWatched];
                if (thisBit != expectedBit)
                    po.FollowUpMismatch = true;
                po.BricksWatched++;
                if (po.BricksWatched >= compiledFollowUp.Length)
                {
                    string outcome = po.FollowUpMismatch ? "F" : "S";
                    HandleOccurrenceOutcome(po, outcome, currentPrice);
                    resolved.Add(po);
                }
            }
            foreach (var po in resolved)
                pendingOccurrences.Remove(po);

            // ---- Detect new occurrence ----
            int patternLen = compiledPattern.Length;
            if (bricks.Count >= patternLen)
            {
                int  patternStartIdx = bricks.Count - patternLen;
                bool isMatch         = true;
                for (int i = 0; i < patternLen; i++)
                {
                    if (bricks[patternStartIdx + i] != compiledPattern[i]) { isMatch = false; break; }
                }

                if (isMatch)
                {
                    double ema1Val   = ema1[0];
                    double ema2Val   = ema2[0];
                    double openPrice = Open[0];
                    bool   emaQual   = EvaluateFilter(openPrice, ema1Val, ema2Val);

                    var po = new PendingOccurrence
                    {
                        PatternStartIndex = patternStartIdx,
                        PatternEndIndex   = patternStartIdx + patternLen - 1,
                        BricksWatched     = 0,
                        FollowUpMismatch  = false,
                        OccurrenceTime    = Time[0],
                        PatternEndPrice   = currentPrice,
                        EmaQualified      = emaQual,
                        Ema1AtDetect      = ema1Val,
                        Ema2AtDetect      = ema2Val,
                        OpenAtDetect      = openPrice
                    };
                    pendingOccurrences.Add(po);

                    dailyOccurrenceNumber++;
                    if (emaQual) dailyTradedOccurrenceNumber++;

                    po.OccurrenceNumberAtDetect       = dailyOccurrenceNumber;
                    po.TradedOccurrenceNumberAtDetect = emaQual ? dailyTradedOccurrenceNumber : 0;

                    Print(string.Format("[L1] Pattern \"{0}\" at brick {1} ({2}). Occ #{3} pending {4} follow-up bricks. Open={5:F2} Close={6:F2} EMA1={7:F2} EMA2={8:F2} Filter={9} Qualified={10}",
                        PatternToMatch, currentBrickIdx, Time[0].ToString("HH:mm:ss"),
                        dailyOccurrenceNumber, compiledFollowUp.Length,
                        openPrice, currentPrice, ema1Val, ema2Val,
                        EmaFilterMode, emaQual ? "YES" : "NO"));

                    if (emaQual)
                        TryEnterOrder(currentPrice, barTimeNy);
                }
            }
        }

        // =====================================================================
        // TryEnterOrder
        // [v1.4.6] Sets SetTrailStop when EnableTrailingStop=true,
        //          SetStopLoss when false. Both are mutually exclusive per NT.
        // =====================================================================
        private void TryEnterOrder(double currentPrice, DateTime barTimeNy)
        {
            if (!EnableOrders) return;

            if (EnableOrderHours && !IsWithinOrderHours(barTimeNy))
            {
                Print(string.Format("[ORDER] Qualified pattern at {0} NY but outside order hours. No order.",
                    barTimeNy.ToString("HH:mm:ss")));
                return;
            }

            if (barTimeNy.Date > GoodTilDate.Date)
            {
                Print(string.Format("[ORDER] Past GoodTilDate ({0:yyyy-MM-dd}). No order.", GoodTilDate));
                return;
            }

            if (!isAlerted)
            {
                Print("[ORDER] Qualified pattern but isAlerted=false. No order.");
                return;
            }

            if (orderState != OrderSubState.Idle)
            {
                Print(string.Format("[ORDER] Qualified pattern but orderState={0} (not Idle). No order.", orderState));
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[ORDER] Position={0} (not Flat). No order.", Position.MarketPosition));
                return;
            }

            if (HasExternalPositionOnInstrument())
            {
                Print("[ORDER] External position on this instrument. No order.");
                return;
            }

            if (safetyBrakeTripped)
            {
                Print(string.Format("[ORDER] SAFETY BRAKE tripped ({0} consecutive losses). No order.", consecutiveLosses));
                return;
            }

            dailyOrderNumber++;
            entryOrderNumber        = dailyOrderNumber;
            lastEntryOrderNumber    = entryOrderNumber;
            entryReferencePrice     = currentPrice;
            bricksSinceEntrySubmit  = 0;
            entrySubmitBarIdx       = CurrentBar;
            currentWorkingOrderType = OrderType;
            cancelRequested         = false;
            cancelSentOnPartial     = false;   // [v1.4.6] reset per order
            actualFilledQty         = 0;       // [v1.4.6] reset per order
            marketTimeoutLogged     = false;

            entryIsLong = (TradeDirection == TradeDirectionEnum.Long);
            string entrySignal = entryIsLong ? ENTRY_LONG_SIGNAL : ENTRY_SHORT_SIGNAL;

            double brickTicks   = BarsPeriod.Value;
            int    targetTicks  = (int)Math.Round(ProfitTargetBricks * brickTicks);

            // [v1.4.6] Set stop: trail OR fixed hard stop — mutually exclusive
            try
            {
                if (EnableTrailingStop)
                {
                    int trailTicks = (int)Math.Round(TrailStopBricks * brickTicks);
                    SetTrailStop(entrySignal, CalculationMode.Ticks, trailTicks, false);
                    SetProfitTarget(entrySignal, CalculationMode.Ticks, targetTicks);
                    Print(string.Format("[ORDER] Pre-set bracket for {0}: TRAIL={1} ticks (tick-by-tick live), TARGET={2} ticks.",
                        entryIsLong ? "LONG" : "SHORT", trailTicks, targetTicks));
                }
                else
                {
                    int stopTicks = (int)Math.Round(StopLossBricks * brickTicks);
                    SetStopLoss(entrySignal, CalculationMode.Ticks, stopTicks, false);
                    SetProfitTarget(entrySignal, CalculationMode.Ticks, targetTicks);
                    Print(string.Format("[ORDER] Pre-set bracket for {0}: HARD STOP={1} ticks, TARGET={2} ticks.",
                        entryIsLong ? "LONG" : "SHORT", stopTicks, targetTicks));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CRITICAL] SetStopLoss/SetTrailStop/SetProfitTarget failed: {0}. ABORTING.", ex.Message));
                return;
            }

            string trailInfo = EnableTrailingStop
                ? string.Format("trail={0:F2}bricks", TrailStopBricks)
                : string.Format("hardstop={0:F2}bricks", StopLossBricks);

            if (OrderType == OrderEntryType.Market)
            {
                Print(string.Format("[ORDER] *** SUBMIT MARKET {0} *** qty={1} ref={2:F2} order#{3} {4}",
                    entryIsLong ? "BUY" : "SELL", OrderQuantity, currentPrice, entryOrderNumber, trailInfo));
                WriteOrderRow("SUBMIT_MARKET", entryOrderNumber, "MARKET", 0, 0, 0,
                    string.Format("{0},{1}", entryIsLong ? "LONG" : "SHORT", trailInfo));
                orderState             = OrderSubState.Working;
                bricksInCurrentState   = 0;
                marketSubmitWallClock  = DateTime.Now;
                try
                {
                    if (entryIsLong)
                        workingEntryOrder = EnterLong(OrderQuantity, entrySignal);
                    else
                        workingEntryOrder = EnterShort(OrderQuantity, entrySignal);
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] Enter{0} (market) error: {1}", entryIsLong ? "Long" : "Short", ex.Message));
                    orderState        = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
            else
            {
                double limitPrice = entryIsLong
                    ? (currentPrice - LimitUnderPoints)
                    : (currentPrice + LimitUnderPoints);
                limitPrice      = Instrument.MasterInstrument.RoundToTickSize(limitPrice);
                entryLimitPrice = limitPrice;

                Print(string.Format("[ORDER] *** SUBMIT LIMIT {0} *** qty={1} ref={2:F2} limit={3:F2} ({4:F2}pt {5} close) wait={6}bricks order#{7} {8}",
                    entryIsLong ? "BUY" : "SELL",
                    OrderQuantity, currentPrice, limitPrice, LimitUnderPoints,
                    entryIsLong ? "below" : "above",
                    UnfilledCancelBricks, entryOrderNumber, trailInfo));
                WriteOrderRow("SUBMIT_LIMIT", entryOrderNumber, "LIMIT", limitPrice, 0, 0,
                    string.Format("{0},{1}", entryIsLong ? "LONG" : "SHORT", trailInfo));
                orderState           = OrderSubState.Working;
                bricksInCurrentState = 0;
                try
                {
                    if (entryIsLong)
                        workingEntryOrder = EnterLongLimit(0, true, OrderQuantity, limitPrice, entrySignal);
                    else
                        workingEntryOrder = EnterShortLimit(0, true, OrderQuantity, limitPrice, entrySignal);
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] Enter{0}Limit error: {1}", entryIsLong ? "Long" : "Short", ex.Message));
                    orderState        = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
        }

        // =====================================================================
        // HasExternalPositionOnInstrument
        // =====================================================================
        private bool HasExternalPositionOnInstrument()
        {
            try
            {
                if (Account == null) return false;
                lock (Account.Positions)
                {
                    foreach (var pos in Account.Positions)
                    {
                        if (pos.Instrument == Instrument && pos.MarketPosition != MarketPosition.Flat)
                            if (Position.MarketPosition == MarketPosition.Flat)
                                return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[ORDER] HasExternalPosition check error: {0}", ex.Message));
            }
            return false;
        }

        // =====================================================================
        // IsWithinOrderHours / IsBeyondOrderEnd
        // =====================================================================
        private bool IsWithinOrderHours(DateTime nyTime)
        {
            int curMin   = nyTime.Hour * 60 + nyTime.Minute;
            int startMin = OrderStartHourNY * 60 + OrderStartMinuteNY;
            int endMin   = OrderEndHourNY   * 60 + OrderEndMinuteNY;
            if (startMin <= endMin)
                return curMin >= startMin && curMin <= endMin;
            return curMin >= startMin || curMin <= endMin;
        }

        private bool IsBeyondOrderEnd(DateTime nyTime)
        {
            int curMin   = nyTime.Hour * 60 + nyTime.Minute;
            int startMin = OrderStartHourNY * 60 + OrderStartMinuteNY;
            int endMin   = OrderEndHourNY   * 60 + OrderEndMinuteNY;
            if (startMin <= endMin)
                return curMin > endMin || curMin < startMin;
            return curMin > endMin && curMin < startMin;
        }

        // =====================================================================
        // OnOrderUpdate
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderUpdateState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            if (!EnableOrders) return;

            string oName      = order.Name ?? "";
            bool   isOurEntry = (workingEntryOrder != null && order == workingEntryOrder)
                             || ((oName == ENTRY_LONG_SIGNAL || oName == ENTRY_SHORT_SIGNAL)
                                 && orderState == OrderSubState.Working);

            if (isOurEntry)
            {
                Print(string.Format("[ORDER] OnOrderUpdate: entry #{0} name={1} state={2} filled={3}",
                    entryOrderNumber, oName, orderUpdateState, filled));

                if (orderUpdateState == NinjaTrader.Cbi.OrderState.Cancelled)
                {
                    // Cancelled with some fills already recorded — position is live, move to Position state
                    if (actualFilledQty > 0)
                    {
                        Print(string.Format("[ORDER] Entry #{0} cancelled with {1} contracts already filled. Position is live.",
                            entryOrderNumber, actualFilledQty));
                        // State was already moved to Position in OnExecutionUpdate on first fill.
                        // Just clear the working order reference.
                        workingEntryOrder = null;
                    }
                    else
                    {
                        // Cancelled with zero fills — clean idle reset
                        if (!forceFlatInProgress)
                            PlayOrderSound("Alert3.wav", "Entry cancelled (unfilled)");
                        workingEntryOrder = null;
                        ResetOrderSubsystemToIdle("entry_cancelled_no_fills");
                    }
                }
                else if (orderUpdateState == NinjaTrader.Cbi.OrderState.Rejected)
                {
                    Print(string.Format("[ORDER] Entry #{0} REJECTED: {1}", entryOrderNumber, error));
                    WriteOrderRow("REJECTED", entryOrderNumber, order.OrderType.ToString(), limitPrice, 0, 0, error.ToString());
                    workingEntryOrder = null;
                    ResetOrderSubsystemToIdle("entry_rejected");
                }
            }
        }

        // =====================================================================
        // OnExecutionUpdate
        // [v1.4.6] KEY CHANGES:
        //   - Tracks actualFilledQty across all partial fill callbacks.
        //   - On FIRST PartFilled: immediately cancels remainder (once only).
        //   - Transitions to Position state on first full or partial fill.
        //   - Bracket sizing uses actualFilledQty for accurate logging.
        //   - Trail stop direction-aware price logging (stop ABOVE for short).
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (!EnableOrders) return;
            if (execution == null || execution.Order == null) return;

            string oName = execution.Order.Name ?? "";

            // ---- Entry fill recognition ----
            if ((oName == ENTRY_LONG_SIGNAL || oName == ENTRY_SHORT_SIGNAL)
                && (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                 || execution.Order.OrderState == NinjaTrader.Cbi.OrderState.PartFilled))
            {
                // Accumulate filled qty
                actualFilledQty += quantity;

                bool isPartial = (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.PartFilled);

                Print(string.Format("[ORDER] *** ENTRY {0} *** order #{1} ({2}) this_qty={3} total_filled={4}/{5} @ {6:F2}",
                    isPartial ? "PART-FILLED" : "FILLED",
                    entryOrderNumber, oName,
                    quantity, actualFilledQty, OrderQuantity, price));

                // [v1.4.6] On FIRST partial fill: cancel remainder immediately (once only)
                if (isPartial && !cancelSentOnPartial)
                {
                    cancelSentOnPartial = true;
                    Print(string.Format("[ORDER] *** PARTIAL FILL DETECTED *** Sending cancel for remainder immediately. filled={0} of {1}.",
                        actualFilledQty, OrderQuantity));
                    WriteOrderRow("PARTIAL_FILL_CANCEL_REMAINDER", entryOrderNumber, "n/a", price, 0, 0,
                        string.Format("filled={0},ordered={1},direction={2}", actualFilledQty, OrderQuantity, entryIsLong ? "LONG" : "SHORT"));
                    try
                    {
                        if (workingEntryOrder != null)
                            CancelOrder(workingEntryOrder);
                    }
                    catch (Exception ex)
                    {
                        Print(string.Format("[ORDER] CancelOrder (partial remainder) error: {0}", ex.Message));
                    }
                }

                // On first fill (partial or full): record fill price, compute bracket, move to Position state
                if (actualFilledQty == quantity)  // this is the first fill callback
                {
                    actualFillPrice = price;

                    double brickPrice = BarsPeriod.Value * TickSize;
                    if (entryIsLong)
                    {
                        computedStopPrice   = EnableTrailingStop
                            ? (actualFillPrice - (TrailStopBricks  * brickPrice))
                            : (actualFillPrice - (StopLossBricks   * brickPrice));
                        computedTargetPrice = actualFillPrice + (ProfitTargetBricks * brickPrice);
                    }
                    else
                    {
                        computedStopPrice   = EnableTrailingStop
                            ? (actualFillPrice + (TrailStopBricks  * brickPrice))
                            : (actualFillPrice + (StopLossBricks   * brickPrice));
                        computedTargetPrice = actualFillPrice - (ProfitTargetBricks * brickPrice);
                    }
                    computedStopPrice   = Instrument.MasterInstrument.RoundToTickSize(computedStopPrice);
                    computedTargetPrice = Instrument.MasterInstrument.RoundToTickSize(computedTargetPrice);
                }

                // On fully resolved fill (complete fill OR first partial — partial triggers cancel so
                // this is our "position is live" transition regardless of remaining contracts)
                if (!isPartial || cancelSentOnPartial)
                {
                    if (!isPartial)
                        PlayOrderSound("Alert4.wav", "Entry filled");
                    else
                        PlayOrderSound("Alert4.wav", "Entry partial fill — remainder cancelled");

                    Print(string.Format("[ORDER] Bracket attached ({0}): {1}~{2:F2} ({3:F2}bricks), TARGET~{4:F2} ({5:F2}bricks). actualFilledQty={6}.",
                        entryIsLong ? "LONG" : "SHORT",
                        EnableTrailingStop ? "TRAIL" : "STOP",
                        computedStopPrice,
                        EnableTrailingStop ? TrailStopBricks : StopLossBricks,
                        computedTargetPrice, ProfitTargetBricks,
                        actualFilledQty));

                    WriteOrderRow("FILLED_BRACKET_ATTACH", entryOrderNumber, "n/a", actualFillPrice,
                        computedStopPrice, computedTargetPrice,
                        string.Format("{0},filledQty={1},ordered={2},trail={3}",
                            entryIsLong ? "LONG" : "SHORT",
                            actualFilledQty, OrderQuantity,
                            EnableTrailingStop ? "YES" : "NO"));

                    if (orderState != OrderSubState.Position)
                    {
                        Print(string.Format("[ORDER] *** STATE: Working -> Position *** (order #{0})", entryOrderNumber));
                        orderState           = OrderSubState.Position;
                        bricksInCurrentState = 0;
                    }
                    workingEntryOrder   = null;
                    marketTimeoutLogged = false;
                }
                return;
            }

            // ---- Bracket leg recognition ----
            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Trail",  StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill)
                && execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                && orderState == OrderSubState.Position)
            {
                string which    = isStopFill ? (EnableTrailingStop ? "TRAIL STOP" : "HARD STOP") : "TARGET";
                actualExitPrice = price;
                double pnl      = entryIsLong
                    ? (price - actualFillPrice)
                    : (actualFillPrice - price);

                Print(string.Format("[ORDER] *** {0} HIT *** ({1}) order #{2} ({3}) exit @ {4:F2}. Entry={5:F2}. PnL/contract={6:+0.00;-0.00} pt. filledQty={7}.",
                    which, entryIsLong ? "LONG" : "SHORT",
                    entryOrderNumber, oName, price, actualFillPrice, pnl, actualFilledQty));

                WriteOrderRow(isStopFill ? "EXIT_STOP" : "EXIT_TARGET",
                    entryOrderNumber, "n/a", actualExitPrice,
                    computedStopPrice, computedTargetPrice,
                    string.Format("{0},filledQty={1},trail={2}",
                        entryIsLong ? "LONG" : "SHORT",
                        actualFilledQty,
                        EnableTrailingStop ? "YES" : "NO"));

                if (isStopFill)
                {
                    consecutiveLosses++;
                    PlayOrderSound("Glass Break.wav", EnableTrailingStop ? "Trail stop hit" : "Hard stop hit");
                    Print(string.Format("[ORDER] Consecutive losses = {0} of {1}.", consecutiveLosses, MaxConsecutiveLosses));
                    if (consecutiveLosses >= MaxConsecutiveLosses && !safetyBrakeTripped)
                    {
                        safetyBrakeTripped = true;
                        Print(string.Format("[CRITICAL] *** SAFETY BRAKE TRIPPED *** {0} consecutive losses >= {1}. ALL ORDERS HALTED.",
                            consecutiveLosses, MaxConsecutiveLosses));
                        WriteOrderRow("SAFETY_BRAKE_TRIPPED", entryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("consecutive_losses={0}", consecutiveLosses));
                        PlayOrderSound("Alert2.wav", "Safety brake tripped");
                    }
                }
                else
                {
                    PlayOrderSound("Boxing Bell.wav", "Profit target hit");
                    if (consecutiveLosses > 0)
                        Print(string.Format("[ORDER] Win resets consecutive losses (was {0}).", consecutiveLosses));
                    consecutiveLosses = 0;
                }

                ResetOrderSubsystemToIdle(isStopFill ? "stop_hit" : "target_hit");
            }
        }

        // =====================================================================
        // CheckPositionSync
        // =====================================================================
        private void CheckPositionSync()
        {
            try
            {
                MarketPosition brokerPos = Position.MarketPosition;

                if (orderState == OrderSubState.Idle && brokerPos != MarketPosition.Flat)
                {
                    bool shouldLog = (DateTime.Now - lastDesyncLogTime).TotalSeconds > 30;
                    if (shouldLog)
                    {
                        Print(string.Format("[CRITICAL] *** DESYNC *** Idle but broker Position={0} qty={1}. Force-flatten.",
                            brokerPos, Position.Quantity));
                        WriteOrderRow("DESYNC_FORCE_FLAT", lastEntryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("orderState=Idle,brokerPosition={0}", brokerPos));
                        PlayOrderSound("Alert2.wav", "Desync ghost position");
                        lastDesyncLogTime = DateTime.Now;
                    }
                    try
                    {
                        if (brokerPos == MarketPosition.Long)
                            ExitLong(Math.Abs(Position.Quantity), "ExitDesync", ENTRY_LONG_SIGNAL);
                        else if (brokerPos == MarketPosition.Short)
                            ExitShort(Math.Abs(Position.Quantity), "ExitDesyncShort", ENTRY_SHORT_SIGNAL);
                    }
                    catch (Exception ex)
                    {
                        if (shouldLog)
                            Print(string.Format("[CRITICAL] Desync force-flat failed: {0}", ex.Message));
                    }
                }
                else if ((orderState == OrderSubState.Working || orderState == OrderSubState.Position)
                         && brokerPos == MarketPosition.Flat)
                {
                    if ((DateTime.Now - lastDesyncLogTime).TotalSeconds > 5)
                    {
                        Print(string.Format("[CRITICAL] *** DESYNC *** orderState={0} but broker=Flat. Resetting.", orderState));
                        WriteOrderRow("DESYNC_RESET_IDLE", lastEntryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("orderState={0},brokerPosition=Flat", orderState));
                        lastDesyncLogTime = DateTime.Now;
                        ResetOrderSubsystemToIdle("desync_broker_flat");
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[ORDER] CheckPositionSync error: {0}", ex.Message));
            }
        }

        // =====================================================================
        // OnConnectionStatusUpdate
        // =====================================================================
        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
        {
            if (connectionStatusUpdate == null) return;
            ConnectionStatus status = connectionStatusUpdate.Status;
            Print(string.Format("[CONN] Connection status: {0}", status));
            if (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Disconnecting)
            {
                Print("[CRITICAL] *** BROKER CONNECTION LOST *** Bracket/trail should still protect open positions.");
                try { PlayOrderSound("Alert2.wav", "Broker connection lost"); } catch { }
            }
            else if (status == ConnectionStatus.Connected)
            {
                Print("[CONN] Broker connection (re)established.");
            }
        }

        // =====================================================================
        // ResetOrderSubsystemToIdle
        // =====================================================================
        private void ResetOrderSubsystemToIdle(string reason)
        {
            Print(string.Format("[ORDER] *** STATE -> IDLE *** (reason: {0}).", reason));
            orderState             = OrderSubState.Idle;
            workingEntryOrder      = null;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx      = -1;
            entryReferencePrice    = 0.0;
            entryLimitPrice        = 0.0;
            actualFillPrice        = 0.0;
            computedStopPrice      = 0.0;
            computedTargetPrice    = 0.0;
            actualExitPrice        = 0.0;
            actualFilledQty        = 0;       // [v1.4.6]
            cancelSentOnPartial    = false;   // [v1.4.6]
            forceFlatInProgress    = false;
            cancelRequested        = false;
            bricksInCurrentState   = 0;
            currentWorkingOrderType    = OrderEntryType.Limit;
            marketSubmitWallClock  = DateTime.MinValue;
            marketTimeoutLogged    = false;
        }

        // =====================================================================
        // HandleOccurrenceOutcome
        // =====================================================================
        private void HandleOccurrenceOutcome(PendingOccurrence po, string outcome, double currentPrice)
        {
            outcomeString.Append(outcome == "S" ? '1' : '0');

            bool firedAlert       = false;
            bool capturedPostAlert = false;
            char capturedBit      = '\0';

            if (po.EmaQualified)
            {
                tradedOutcomeString.Append(outcome == "S" ? '1' : '0');
                Print(string.Format("[L1] *** {0} *** at {1} (qualified). tradedOutcomeString = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"), tradedOutcomeString.ToString()));

                if (pendingPostAlertCaptures > 0)
                {
                    capturedBit = outcome == "S" ? '1' : '0';
                    postAlertOutcomeString.Append(capturedBit);
                    pendingPostAlertCaptures--;
                    capturedPostAlert = true;
                    Print(string.Format("[POSTALERT] Captured '{0}'. PostAlert=\"{1}\" Pending={2}.",
                        capturedBit, postAlertOutcomeString.ToString(), pendingPostAlertCaptures));
                }

                if (AnyAlertPatternMatchesTail())
                {
                    FireAlert();
                    firedAlert = true;
                }

                bool prev = isAlerted;
                isAlerted = AnyAlertPatternMatchesTail();
                if (prev != isAlerted)
                    Print(string.Format("[ALERTSTATE] isAlerted: {0} -> {1}", prev, isAlerted));
            }
            else
            {
                Print(string.Format("[L1] *** {0} *** at {1} (NOT qualified, filter={2}). outcomeString=\"{3}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"), EmaFilterMode, outcomeString.ToString()));
            }

            WriteOccurrenceRow(po, outcome, currentPrice, firedAlert, capturedPostAlert, capturedBit);

            if (EnableChartMarkers && ShowOutcomeLabels)
            {
                string tag    = "RSP_OUT_" + CurrentBar + "_" + po.OccurrenceNumberAtDetect;
                string prefix = po.EmaQualified ? "###" : "";
                string label  = prefix + (outcome == "S" ? "PT/S" : "PT/F");
                Brush  color  = outcome == "S" ? Brushes.LimeGreen : Brushes.Red;
                double yOffset = outcome == "S" ? (3 * TickSize) : -(3 * TickSize);
                double yPos    = outcome == "S" ? (High[0] + yOffset) : (Low[0] + yOffset);
                Draw.Text(this, tag, label, 0, yPos, color);
            }
        }

        // =====================================================================
        // AnyAlertPatternMatchesTail
        // =====================================================================
        private bool AnyAlertPatternMatchesTail()
        {
            string tail = tradedOutcomeString.ToString();
            foreach (var p in compiledAlertPatterns)
            {
                if (tail.Length < p.Length) continue;
                bool match = true;
                int  start = tail.Length - p.Length;
                for (int i = 0; i < p.Length; i++)
                {
                    if (tail[start + i] != p[i]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        // =====================================================================
        // FireAlert
        // =====================================================================
        private void FireAlert()
        {
            dailyAlertNumber++;
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern matched at {0}. Daily alert #{1}. PendingCaptures={2}. ***",
                Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] Tail (last 20): \"...{0}\"",
                tradedOutcomeString.Length > 20
                    ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - 20)
                    : tradedOutcomeString.ToString()));

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount         = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Text(this, txt,
                    string.Format("ALERT #{0}\nAP=\"{1}\"\n{2}/{3}", dailyAlertNumber, AlertPattern, EmaFilterMode, TradeDirection),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow();
        }

        // =====================================================================
        // PlayOrderSound
        // =====================================================================
        private void PlayOrderSound(string wavFileName, string eventLabel)
        {
            try
            {
                string fullPath = NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + wavFileName;
                Print(string.Format("[SOUND] {0} -> {1}", eventLabel, wavFileName));
                PlaySound(fullPath);
            }
            catch (Exception ex)
            {
                Print(string.Format("[SOUND] ERROR playing {0}: {1}", wavFileName, ex.Message));
            }
        }

        // =====================================================================
        // CSV: per-occurrence
        // =====================================================================
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice,
            bool firedAlert, bool capturedPostAlert, char capturedBit)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile   = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order_occ.csv");
                bool   fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.6");
                        sw.WriteLine(string.Format("# file_created_NY={0}",
                            TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# file_created_UTC={0}",
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# instrument={0}",    Instrument.FullName));
                        sw.WriteLine(string.Format("# bar_type={0}",      BarsPeriod.BarsPeriodType));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}",     TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# PatternToMatch={0}",  PatternToMatch));
                        sw.WriteLine(string.Format("# FollowUpPattern={0}", FollowUpPattern));
                        sw.WriteLine(string.Format("# AlertPattern={0}",    AlertPattern));
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine(string.Format("# EMA1Period={0}",      EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}",      EMA2Period));
                        sw.WriteLine(string.Format("# EmaFilterMode={0}",   EmaFilterMode));
                        sw.WriteLine(string.Format("# TradeDirection={0}",  TradeDirection));
                        sw.WriteLine(string.Format("# EnableOrders={0}",    EnableOrders));
                        sw.WriteLine(string.Format("# EnableTrailingStop={0}", EnableTrailingStop));  // [v1.4.6]
                        sw.WriteLine(string.Format("# TrailStopBricks={0}", TrailStopBricks));        // [v1.4.6]
                        sw.WriteLine("# encoding: SUCCESS=1, FAILURE=0");
                        sw.WriteLine("# all timestamps in this file are NEW YORK time");
                        sw.WriteLine("#");
                        sw.WriteLine("OccurrenceTime_NY,OutcomeTime_NY,BrickIndexAtPatternEnd,Pattern,FollowUp,"
                            + "ActualFollowUp,Outcome,PatternEndOpen,PatternEndClose,OutcomePrice,"
                            + "AlertPattern,DailyOccurrenceNumber,OutcomeString_NoEmaFilter,"
                            + "Ema1AtDetect,Ema2AtDetect,EmaQualified,"
                            + "DailyEmaQualifiedOccurrenceNumber,OutcomeString_EmaQualified,"
                            + "FiredAlertOnThisRow,"
                            + "CapturedPostAlert,CapturedBit,PostAlertOutcomeString,PendingPostAlertCapturesAfter,"
                            + "IsAlertedAfter,OrderStateAfter");
                    }

                    string actualFollow = "";
                    int    startIdx     = po.PatternEndIndex + 1;
                    for (int i = 0; i < compiledFollowUp.Length && (startIdx + i) < bricks.Count; i++)
                        actualFollow += bricks[startIdx + i].ToString();

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13:F2},{14:F2},{15},{16},{17},{18},{19},{20},{21},{22},{23},{24}",
                        TimeZoneInfo.ConvertTime(po.OccurrenceTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        TimeZoneInfo.ConvertTime(Time[0],           nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        po.PatternEndIndex,
                        PatternToMatch, FollowUpPattern, actualFollow, outcome,
                        po.OpenAtDetect, po.PatternEndPrice, endPrice,
                        apForCsv,
                        po.OccurrenceNumberAtDetect,
                        outcomeString.ToString(),
                        po.Ema1AtDetect, po.Ema2AtDetect,
                        po.EmaQualified ? "YES" : "NO",
                        po.TradedOccurrenceNumberAtDetect,
                        tradedOutcomeString.ToString(),
                        firedAlert       ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",
                        capturedPostAlert ? capturedBit.ToString() : "",
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted   ? "YES" : "NO",
                        orderState));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-OCC] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // CSV: per-alert
        // =====================================================================
        private void WriteAlertRow()
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile   = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order_alert.csv");
                bool   fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.6");
                        sw.WriteLine(string.Format("# instrument={0}",      Instrument.FullName));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}",        TickSize));
                        sw.WriteLine(string.Format("# AlertPattern={0}",     AlertPattern));
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine(string.Format("# EmaFilterMode={0}",    EmaFilterMode));
                        sw.WriteLine(string.Format("# TradeDirection={0}",   TradeDirection));
                        sw.WriteLine(string.Format("# EnableTrailingStop={0}", EnableTrailingStop));  // [v1.4.6]
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("AlertTime_NY,DailyAlertNumber,AlertPattern,"
                            + "Pattern,FollowUp,CurrentPrice,"
                            + "OutcomeString_NoEmaFilter,OutcomeString_EmaQualified,"
                            + "PostAlertOutcomeString,PendingPostAlertCapturesAfter,"
                            + "IsAlertedAfter,OrderStateAtAlert");
                    }

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5:F2},{6},{7},{8},{9},{10},{11}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber, apForCsv,
                        PatternToMatch, FollowUpPattern, Close[0],
                        outcomeString.ToString(), tradedOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(), pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO", orderState));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ALERT] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // CSV: per-order-event
        // =====================================================================
        private void WriteOrderRow(string eventType, int orderNumber, string orderTypeStr,
            double price, double stopPx, double targetPx, string extra)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile   = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order_orders.csv");
                bool   fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.6");
                        sw.WriteLine(string.Format("# instrument={0}",         Instrument.FullName));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}",   BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}",          TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}",   BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# StopLossBricks={0}",     StopLossBricks));
                        sw.WriteLine(string.Format("# ProfitTargetBricks={0}", ProfitTargetBricks));
                        sw.WriteLine(string.Format("# EnableTrailingStop={0}", EnableTrailingStop));  // [v1.4.6]
                        sw.WriteLine(string.Format("# TrailStopBricks={0}",    TrailStopBricks));     // [v1.4.6]
                        sw.WriteLine(string.Format("# LimitUnderPoints={0}",   LimitUnderPoints));
                        sw.WriteLine(string.Format("# UnfilledCancelBricks={0}", UnfilledCancelBricks));
                        sw.WriteLine(string.Format("# EmaFilterMode={0}",      EmaFilterMode));
                        sw.WriteLine(string.Format("# TradeDirection={0}",     TradeDirection));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("# Event types:");
                        sw.WriteLine("#   SUBMIT_MARKET / SUBMIT_LIMIT          - entry submitted (Extra includes LONG/SHORT and trail config)");
                        sw.WriteLine("#   FILLED_BRACKET_ATTACH                 - entry filled, bracket active (Extra includes filledQty, ordered, trail)");
                        sw.WriteLine("#   PARTIAL_FILL_CANCEL_REMAINDER         - [v1.4.6] partial fill detected, remainder cancel sent");
                        sw.WriteLine("#   EXIT_STOP / EXIT_TARGET               - bracket/trail leg filled (Extra includes filledQty, trail)");
                        sw.WriteLine("#   CANCEL_UNFILLED                       - LIMIT entry cancelled (brick timeout)");
                        sw.WriteLine("#   CANCEL_WORKEND                        - entry cancelled at work-end");
                        sw.WriteLine("#   FORCEFLAT_WORKEND                     - position force-flatted at work-end");
                        sw.WriteLine("#   FORCEFLAT_WORKEND_GHOST               - ghost position flatted at work-end");
                        sw.WriteLine("#   FORCE_RESET_STUCK                     - LIMIT grace expired");
                        sw.WriteLine("#   DESYNC_FORCE_FLAT                     - orderState=Idle but broker has position");
                        sw.WriteLine("#   DESYNC_RESET_IDLE                     - orderState=Working/Position but broker Flat");
                        sw.WriteLine("#   MARKET_HEARTBEAT_WARN                 - market order no fill confirm after 30s");
                        sw.WriteLine("#   SAFETY_BRAKE_TRIPPED                  - consecutive losses limit reached");
                        sw.WriteLine("#   REJECTED                              - entry order rejected");
                        sw.WriteLine("#");
                        sw.WriteLine("EventTime_NY,EventType,EntryOrderNumber,OrderTypeStr,Price,StopPrice,TargetPrice,OrderState,Extra");
                    }

                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        eventType, orderNumber, orderTypeStr,
                        price, stopPx, targetPx, orderState, extra));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ORDER] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // ResetState (daily reset at 9:30 NY)
        // =====================================================================
        private void ResetState()
        {
            bricks.Clear();
            pendingOccurrences.Clear();
            dailyOccurrenceNumber       = 0;
            dailyTradedOccurrenceNumber = 0;
            dailyAlertNumber            = 0;
            dailyOrderNumber            = 0;
            beepCount                   = 0;
            outcomeString.Clear();
            tradedOutcomeString.Clear();
            postAlertOutcomeString.Clear();
            pendingPostAlertCaptures = 0;
            isAlerted                = false;
            // Order subsystem state and consecutiveLosses are NOT cleared on daily reset.
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name="PatternToMatch",
            Description="Bit pattern to detect, 1-10 chars of '0' (red) or '1' (green).",
            Order=1, GroupName="1. Layer 1 - Pattern")]
        public string PatternToMatch { get; set; }

        [NinjaScriptProperty]
        [Display(Name="FollowUpPattern",
            Description="Required follow-up bricks for SUCCESS.",
            Order=2, GroupName="1. Layer 1 - Pattern")]
        public string FollowUpPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AlertPattern (comma-separated list OK)",
            Description="One or more suffix patterns, comma-separated. At each qualified brick close, if ANY pattern matches the tail of tradedOutcomeString, ONE alert fires.",
            Order=3, GroupName="2. Alert")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA1Period",
            Description="Period for EMA1. Used by OpenAbove/OpenBelow filter modes.",
            Order=4, GroupName="3. EMA Filter")]
        public int EMA1Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA2Period",
            Description="Period for EMA2. Used by OpenAbove/OpenBelow filter modes.",
            Order=5, GroupName="3. EMA Filter")]
        public int EMA2Period { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EMA Filter Mode",
            Description="NoFilter=every pattern qualifies. OpenAbove=qualify when Open > both EMAs. OpenBelow=qualify when Open < both EMAs.",
            Order=6, GroupName="3. EMA Filter")]
        public EmaFilterModeEnum EmaFilterMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert.",
            Order=7, GroupName="4. Beep")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps.",
            Order=8, GroupName="4. Beep")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Master toggle for all chart drawings.",
            Order=9, GroupName="5. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowOutcomeLabels",
            Description="Show PT/S / PT/F text at each outcome brick.",
            Order=10, GroupName="5. Visuals")]
        public bool ShowOutcomeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for CSV files.",
            Order=11, GroupName="6. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name="MaxBitsKept",
            Description="Max in-memory bit string length.",
            Order=12, GroupName="7. Advanced")]
        public int MaxBitsKept { get; set; }

        // =====================================================================
        // ORDER PROPERTIES
        // =====================================================================

        [NinjaScriptProperty]
        [Display(Name="EnableOrders",
            Description="Master switch for order subsystem.",
            Order=1, GroupName="8. Order Execution")]
        public bool EnableOrders { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Trade Direction",
            Description="Long = EnterLong/EnterLongLimit. Short = EnterShort/EnterShortLimit.",
            Order=2, GroupName="8. Order Execution")]
        public TradeDirectionEnum TradeDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Order quantity (CONTRACTS)",
            Description="Number of contracts per entry. NOTE: when EnableTrailingStop=true and qty>30, consider NQ instead of MNQ for cleaner fills.",
            Order=3, GroupName="8. Order Execution")]
        public int OrderQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Order type (Market or Limit)",
            Description="Limit: long buys BELOW close, short sells ABOVE close. Market = immediate fill.",
            Order=4, GroupName="8. Order Execution")]
        public OrderEntryType OrderType { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name="Limit offset from close (POINTS)",
            Description="(LIMIT only) For Long: limit = close - this. For Short: limit = close + this.",
            Order=5, GroupName="8. Order Execution")]
        public double LimitUnderPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name="Limit unfilled cancel after N (BRICKS)",
            Description="(LIMIT only) Cancel limit if not filled after N brick closes.",
            Order=6, GroupName="8. Order Execution")]
        public int UnfilledCancelBricks { get; set; }

        // [v1.4.6] Renamed display label to "Hard Stop Loss"
        [NinjaScriptProperty]
        [Range(0.1, 50.0)]
        [Display(Name="Hard Stop Loss (BRICKS)",
            Description="Fixed stop-loss distance in bricks. Used when EnableTrailingStop=false. When EnableTrailingStop=true this parameter is unused — TrailStopBricks controls the stop distance instead. Applied as stop below entry for Long, stop above entry for Short.",
            Order=7, GroupName="8. Order Execution")]
        public double StopLossBricks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50.0)]
        [Display(Name="Profit target distance (BRICKS)",
            Description="Profit-target distance from fill in bricks. Always active regardless of trail setting.",
            Order=8, GroupName="8. Order Execution")]
        public double ProfitTargetBricks { get; set; }

        // [v1.4.6] NEW: Enable trailing stop
        [NinjaScriptProperty]
        [Display(Name="Enable Trailing Stop",
            Description="[v1.4.6] When true: SetTrailStop(TrailStopBricks) replaces SetStopLoss entirely. Trail updates TICK-BY-TICK in live trading. Backtest: bar-close only (TickReplay not compatible with Renko; use Playback Connection). When false: fixed hard stop (StopLossBricks) used — v1.4.5 behavior.",
            Order=9, GroupName="8. Order Execution")]
        public bool EnableTrailingStop { get; set; }

        // [v1.4.6] NEW: Trail stop distance
        [NinjaScriptProperty]
        [Range(0.1, 50.0)]
        [Display(Name="Trail Stop distance (BRICKS)",
            Description="[v1.4.6] Trail stop distance in bricks. Only used when EnableTrailingStop=true. Default mirrors StopLossBricks. For Long: trail below highest price. For Short: trail above lowest price.",
            Order=10, GroupName="8. Order Execution")]
        public double TrailStopBricks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Max consecutive losses before halt (99=OFF)",
            Description="Halt new orders after N consecutive losses. 99=effectively off.",
            Order=11, GroupName="8. Order Execution")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Order Hours filter",
            Description="Restrict order placement to NY time window. Alerts still fire 24h.",
            Order=1, GroupName="9. Order Hours (NY) & Expiry")]
        public bool EnableOrderHours { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="Order start HOUR (NY, 0-23)", Order=2, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderStartHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="Order start MINUTE (NY, 0-59)", Order=3, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderStartMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="Order end HOUR (NY, 0-23)", Order=4, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderEndHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="Order end MINUTE (NY, 0-59)",
            Description="At/after this time: position force-flat at market, working orders cancelled.",
            Order=5, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderEndMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Good til DATE (NY)",
            Description="Last NY date orders may be placed. After this date alerts continue but no new orders.",
            Order=6, GroupName="9. Order Hours (NY) & Expiry")]
        public DateTime GoodTilDate { get; set; }

        #endregion
    }
}
