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
// STRATEGY: scalper_RenkoStringPatternAlertEMA_Order v1.4.5
// AUTHOR:   Albert Feng / Drafted with help from Claude
// REPLACES: v1.4.4
// =============================================================================
//
// v1.4.5 CHANGES vs v1.4.4 — direction + filter mode
// ---------------------------------------------------
//
// CHANGE 1 — NEW: EmaFilterMode dropdown (3 modes)
//   Old (v1.4.4): EMA filter was hard-coded as "Open of last brick in
//                 pattern > EMA1 AND Open > EMA2".
//   New (v1.4.5): user-selectable filter mode:
//     • NoFilter   → every pattern is qualified (no EMA check at all)
//     • OpenAbove  → qualified only when Open > EMA1 AND Open > EMA2
//                    (back-compat with v1.4.4 behavior — pick this to
//                    reproduce v1.4.4 alerts/orders exactly)
//     • OpenBelow  → qualified only when Open < EMA1 AND Open < EMA2
//                    (mirror; for downtrend-aligned strategies)
//
//   The "Open" used is `PatternEndOpen` — the OPEN price of the LAST
//   brick in the search pattern (the brick that completes the match).
//   This is the same field v1.4.4 used; only the comparison changes.
//
//   When NoFilter is selected, the alert state machine receives EVERY
//   pattern outcome (alerts fire much more frequently — be aware).
//   When OpenAbove or OpenBelow, the state machine receives only
//   filter-passing outcomes (same density as v1.4.4 in OpenAbove mode).
//
//   Backward compat: a v1.4.4 user simply picks OpenAbove and gets
//   identical alert/order behavior. Default is OpenAbove for safety.
//
// CHANGE 2 — NEW: TradeDirection dropdown (Long / Short)
//   Old (v1.4.4): Long-only. All entries via EnterLong/EnterLongLimit.
//                 Stop = entry − N*brick. Target = entry + N*brick.
//   New (v1.4.5): Long-only or Short-only, selected via dropdown.
//     • Long  → EnterLong / EnterLongLimit;   stop below, target above
//     • Short → EnterShort / EnterShortLimit; stop above, target below
//   Default is Long for backward compatibility.
//
//   Limit-entry offset semantics:
//     • Long  + Limit: limit = close − LimitUnderPoints (buy below)
//     • Short + Limit: limit = close + LimitUnderPoints (sell above)
//
//   The OCO bracket via SetStopLoss/SetProfitTarget (Ticks mode) is
//   direction-agnostic in NT's API — it computes the bracket from the
//   fill price plus tick offsets, applied correctly for whichever
//   direction we entered. So the bracket setup line is unchanged.
//
// CHANGE 3 — Six combinations allowed independently
//   All 6 combinations of EmaFilterMode × TradeDirection are accepted
//   with NO restrictions. The user is free to e.g. trade SHORT with
//   OpenAbove filter, which is the configuration that backtested as
//   profitable on our research data.
//
// CHANGE 4 — CSV schema bumped to 1.4.5
//   File names unchanged. Header now includes:
//     # EmaFilterMode=...
//     # TradeDirection=...
//   so each row's run config is unambiguous.
//   COLUMN LAYOUT in all three CSVs is UNCHANGED — same column order,
//   same names. Only the "EmaQualified" semantics now depends on the
//   selected filter mode (NoFilter → always YES; OpenAbove → as before;
//   OpenBelow → YES when Open < BOTH EMAs).
//
// CHANGE 5 — INIT log updated
//   Now prints the active filter mode and trade direction so the user
//   sees the run config at startup. Print at strategy enable, before
//   the first bar is processed.
//
// CHANGE 6 — Internal name of the entry signal varies by direction
//   v1.4.4 used "EntryV140" for the long entry signal name. v1.4.5
//   uses "EntryV145Long" for longs and "EntryV145Short" for shorts.
//   This lets us distinguish in the broker's order log and avoids any
//   name collision if a workspace has both an old v1.4.4 entry name in
//   history and a new v1.4.5 entry. All Exit*/Stop*/Target* recognition
//   in OnOrderUpdate / OnExecutionUpdate is by string-search on the
//   order Name; the dispatch handles either prefix.
//
// CHANGE 7 — Safety brake is direction-agnostic
//   The MaxConsecutiveLosses counter increments on every EXIT_STOP
//   regardless of long/short. This is correct behavior — a stop hit
//   is a stop hit. No code change to the brake.
//
// =============================================================================
//
// (v1.4.4 changes and prior versions documented in v1.4.4 header — preserved
//  conceptually but trimmed here for readability. See v1.4.4 source for
//  full revision history of v1.4.3, v1.4.2, v1.4.1, v1.4.0.)
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

        // [v1.4.5] NEW: EMA filter mode
        public enum EmaFilterModeEnum
        {
            NoFilter,     // every pattern is qualified, no EMA check
            OpenAbove,    // qualified when Open > EMA1 AND Open > EMA2 (v1.4.4 default)
            OpenBelow     // qualified when Open < EMA1 AND Open < EMA2 (mirror)
        }

        // [v1.4.5] NEW: Trade direction
        public enum TradeDirectionEnum
        {
            Long,         // EnterLong / EnterLongLimit, stop below, target above
            Short         // EnterShort / EnterShortLimit, stop above, target below
        }

        private enum OrderSubState
        {
            Idle,
            Working,
            Position
        }

        #region Variables

        // ---- Bit string of bricks since today's reset ----
        private List<int> bricks = new List<int>();

        // ---- Outcome strings ----
        private StringBuilder outcomeString = new StringBuilder();
        private StringBuilder tradedOutcomeString = new StringBuilder();
        private StringBuilder postAlertOutcomeString = new StringBuilder();
        private int pendingPostAlertCaptures = 0;

        // ---- Alert status flag read by the order subsystem (recomputed after every qualified append) ----
        private bool isAlerted = false;

        // ---- Daily numbering ----
        private int dailyOccurrenceNumber = 0;
        private int dailyTradedOccurrenceNumber = 0;
        private int dailyAlertNumber = 0;
        private int dailyOrderNumber = 0;

        // ---- Beep cadence ----
        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        // ---- Daily reset state ----
        private DateTime currentTradingDateNy = DateTime.MinValue;
        private TimeZoneInfo nyTz;
        private const int RESET_HOUR_NY   = 9;
        private const int RESET_MINUTE_NY = 30;

        // ---- Compiled patterns ----
        private int[] compiledPattern  = null;
        private int[] compiledFollowUp = null;
        private List<string> compiledAlertPatterns = null;

        private bool configValid = false;

        // ---- EMA indicator references ----
        private EMA ema1;
        private EMA ema2;

        // ---- Pending occurrences awaiting follow-up resolution ----
        private class PendingOccurrence
        {
            public int       PatternStartIndex;
            public int       PatternEndIndex;
            public int       BricksWatched;
            public bool      FollowUpMismatch;
            public DateTime  OccurrenceTime;
            public double    PatternEndPrice;

            public bool      EmaQualified;
            public double    Ema1AtDetect;
            public double    Ema2AtDetect;
            public double    OpenAtDetect;

            public int       OccurrenceNumberAtDetect;
            public int       TradedOccurrenceNumberAtDetect;
        }

        private List<PendingOccurrence> pendingOccurrences = new List<PendingOccurrence>();

        // =====================================================================
        // ORDER SUBSYSTEM state
        // =====================================================================
        private OrderSubState orderState = OrderSubState.Idle;

        private Order workingEntryOrder = null;
        private int   bricksSinceEntrySubmit = 0;
        private int   entrySubmitBarIdx = -1;
        private double entryReferencePrice = 0.0;
        private double entryLimitPrice = 0.0;
        private int   entryOrderNumber = 0;

        // [v1.4.5] which direction did this order go?
        private bool  entryIsLong = true;

        private bool cancelRequested = false;
        private int  bricksInCurrentState = 0;

        private OrderEntryType currentWorkingOrderType = OrderEntryType.Limit;
        private DateTime marketSubmitWallClock = DateTime.MinValue;
        private bool marketTimeoutLogged = false;
        private int consecutiveLosses = 0;
        private bool safetyBrakeTripped = false;
        private int lastEntryOrderNumber = 0;
        private DateTime lastDesyncLogTime = DateTime.MinValue;

        private double actualFillPrice = 0.0;
        private double computedStopPrice = 0.0;
        private double computedTargetPrice = 0.0;

        private bool forceFlatInProgress = false;

        // [v1.4.5] entry signal names dispatched per direction
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
                Description = "Renko string-pattern alerter+order (v1.4.5). User-selectable EMA filter mode (NoFilter/OpenAbove/OpenBelow) and trade direction (Long/Short). All 6 combinations allowed. Order subsystem retains v1.4.3 safety architecture (broker = source of truth, server-side bracket, safety brake). EnableOrders=false reproduces alert-only behavior.";
                Name        = "scalper_RenkoStringPatternAlertEMA_Order";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = true;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- Pattern parameters ----
                PatternToMatch   = "011";
                FollowUpPattern  = "1";

                // ---- Alert ----
                AlertPattern     = "01,011,0111";

                // ---- EMA filter ----
                EMA1Period       = 20;
                EMA2Period       = 9;
                EmaFilterMode    = EmaFilterModeEnum.OpenAbove;   // [v1.4.5] default = v1.4.4 behavior

                // ---- Beep ----
                AlertSoundCount   = 3;
                AlertReminderSecs = 1;

                // ---- Visuals ----
                EnableChartMarkers = true;
                ShowOutcomeLabels  = true;

                // ---- Logging ----
                AuditLogPath = @"C:\temp";

                // ---- Memory cap ----
                MaxBitsKept = 5000;

                // ---- Order Execution defaults ----
                EnableOrders         = false;
                TradeDirection       = TradeDirectionEnum.Long;   // [v1.4.5] default = long-only (back-compat)
                OrderQuantity        = 1;
                OrderType            = OrderEntryType.Limit;
                LimitUnderPoints     = 5.0;
                UnfilledCancelBricks = 1;
                StopLossBricks       = 1.0;
                ProfitTargetBricks   = 1.0;
                MaxConsecutiveLosses = 99;

                // ---- Working Hours & Expiry defaults ----
                EnableOrderHours     = false;
                OrderStartHourNY     = 9;
                OrderStartMinuteNY   = 30;
                OrderEndHourNY       = 15;
                OrderEndMinuteNY     = 55;
                GoodTilDate          = DateTime.Today.AddDays(30);
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
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlertEMA_Order v1.4.5 at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));

                Print(string.Format("[INIT] BarsPeriod.BarsPeriodType = {0}", BarsPeriod.BarsPeriodType));
                Print(string.Format("[INIT] BarsPeriod.Value  = {0}  (for standard Renko: brick size in ticks)", BarsPeriod.Value));
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

                Print(string.Format("[INIT] AlertPattern (raw input) = \"{0}\"", AlertPattern));
                Print(string.Format("[INIT] AlertPattern (parsed list, {0} pattern(s)): [{1}]",
                    compiledAlertPatterns.Count, string.Join(", ", compiledAlertPatterns)));

                CheckForSuffixOverlaps();

                Print("[INIT] Bit encoding: SUCCESS=1, FAILURE=0.");
                Print("[INIT] Each pattern in AlertPattern list is suffix-matched against tradedOutcomeString.");
                Print("[INIT] Rule: at each qualified brick close, if ANY pattern matches the tail, fire ONE alert.");

                // [v1.4.5] NEW: print active EMA filter mode and trade direction
                Print("[INIT]");
                Print(string.Format("[INIT] *** EmaFilterMode = {0} ***", EmaFilterMode));
                switch (EmaFilterMode)
                {
                    case EmaFilterModeEnum.NoFilter:
                        Print("[INIT]   -> NO EMA filter. EVERY pattern is qualified.");
                        Print("[INIT]   -> Expect significantly more alerts/trades vs OpenAbove mode.");
                        break;
                    case EmaFilterModeEnum.OpenAbove:
                        Print("[INIT]   -> Qualified when PatternEndOpen > EMA1 AND PatternEndOpen > EMA2.");
                        Print("[INIT]   -> Matches v1.4.4 behavior exactly.");
                        break;
                    case EmaFilterModeEnum.OpenBelow:
                        Print("[INIT]   -> Qualified when PatternEndOpen < EMA1 AND PatternEndOpen < EMA2.");
                        Print("[INIT]   -> Mirror of OpenAbove; useful for downtrend-aligned setups.");
                        break;
                }
                Print(string.Format("[INIT] EMA1 period = {0}, EMA2 period = {1}", EMA1Period, EMA2Period));
                Print(string.Format("[INIT] *** TradeDirection = {0} ***", TradeDirection));
                if (TradeDirection == TradeDirectionEnum.Long)
                    Print("[INIT]   -> Entries via EnterLong/EnterLongLimit. Stop below entry, Target above.");
                else
                    Print("[INIT]   -> Entries via EnterShort/EnterShortLimit. Stop above entry, Target below.");

                Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));
                Print(string.Format("[INIT] Beep: {0} beeps, {1}s apart", AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));

                Print("[INIT] ----- ORDER SUBSYSTEM -----");
                Print(string.Format("[INIT] EnableOrders        = {0}", EnableOrders));
                if (EnableOrders)
                {
                    Print(string.Format("[INIT] OrderQuantity       = {0} contract(s)", OrderQuantity));
                    Print(string.Format("[INIT] OrderType           = {0}", OrderType));
                    Print(string.Format("[INIT] LimitUnderPoints    = {0:F2} pt (ignored if Market)", LimitUnderPoints));
                    Print(string.Format("[INIT] UnfilledCancelBricks= {0} (LIMIT only - market orders never cancelled)", UnfilledCancelBricks));

                    double brickTicks = BarsPeriod.Value;
                    double brickPoints = brickTicks * TickSize;
                    double stopTicks = StopLossBricks * brickTicks;
                    double stopPoints = stopTicks * TickSize;
                    double targetTicks = ProfitTargetBricks * brickTicks;
                    double targetPoints = targetTicks * TickSize;

                    double pointValue = 0.0;
                    try { pointValue = Instrument.MasterInstrument.PointValue; }
                    catch { pointValue = 0.0; }

                    Print("[INIT] === Order sizing summary ===");
                    Print(string.Format("[INIT]   Brick size:     {0:F1} ticks = {1:F2} points{2}",
                        brickTicks, brickPoints,
                        pointValue > 0 ? string.Format(" = ${0:F2}/contract", brickPoints * pointValue) : ""));
                    Print(string.Format("[INIT]   Stop loss:      {0:F2} bricks = {1:F1} ticks = {2:F2} points{3}",
                        StopLossBricks, stopTicks, stopPoints,
                        pointValue > 0 ? string.Format(" = ${0:F2} RISK/contract", stopPoints * pointValue) : ""));
                    Print(string.Format("[INIT]   Profit target:  {0:F2} bricks = {1:F1} ticks = {2:F2} points{3}",
                        ProfitTargetBricks, targetTicks, targetPoints,
                        pointValue > 0 ? string.Format(" = ${0:F2} REWARD/contract", targetPoints * pointValue) : ""));
                    Print(string.Format("[INIT]   Risk:Reward = 1:{0:F2}", ProfitTargetBricks / StopLossBricks));
                    if (OrderType == OrderEntryType.Limit)
                    {
                        if (TradeDirection == TradeDirectionEnum.Long)
                            Print(string.Format("[INIT]   Limit offset:   {0:F2} pt BELOW close (buy on retrace)", LimitUnderPoints));
                        else
                            Print(string.Format("[INIT]   Limit offset:   {0:F2} pt ABOVE close (sell on retrace)", LimitUnderPoints));
                    }
                    Print("[INIT] ============================");

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
                    Print(string.Format("[INIT] Order routes to account selected in strategy's Account dropdown."));

                    Print("[INIT] Safety layers active:");
                    Print("[INIT]   Layer 1: work-end logic at OrderEndHourNY");
                    Print("[INIT]   Layer 2: NT session-close auto-flat (IsExitOnSessionCloseStrategy=true)");
                    Print("[INIT]   Layer 3: broker server-side bracket (SetStopLoss/SetProfitTarget)");
                    Print("[INIT]   Defensive: position-sync check every brick");
                    Print("[INIT]   Defensive: market orders NEVER cancelled (use heartbeat instead)");

                    Print("[INIT] Order sounds:");
                    Print("[INIT]   Entry filled        -> Alert4.wav");
                    Print("[INIT]   Profit target hit   -> Boxing Bell.wav");
                    Print("[INIT]   Stop loss hit       -> Glass Break.wav");
                    Print("[INIT]   Entry cancelled     -> Alert3.wav");
                    Print("[INIT]   Force-flat workend  -> Alert2.wav");
                    Print("[INIT]   Connection lost     -> Alert2.wav");
                }
                else
                {
                    Print("[INIT] (orders disabled - strategy will only generate alerts)");
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

            return compiledPattern != null
                && compiledFollowUp != null
                && compiledAlertPatterns != null
                && compiledAlertPatterns.Count > 0;
        }

        private List<string> ParseAlertPatternList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Print("[VALIDATE] *** AlertPattern is empty. Must contain at least one pattern (1-10 chars of '0' or '1'). ***");
                return null;
            }

            var result = new List<string>();
            var rawParts = raw.Split(',');
            foreach (var rawPart in rawParts)
            {
                string p = rawPart.Trim();
                if (p.Length == 0) continue;
                if (p.Length > 10)
                {
                    Print(string.Format("[VALIDATE] *** AlertPattern entry \"{0}\" is {1} chars; max allowed is 10. Skipping. ***",
                        p, p.Length));
                    continue;
                }

                bool ok = true;
                for (int i = 0; i < p.Length; i++)
                {
                    if (p[i] != '0' && p[i] != '1')
                    {
                        Print(string.Format("[VALIDATE] *** AlertPattern entry \"{0}\" contains invalid char '{1}' at position {2}. Only '0' and '1' allowed. Skipping. ***",
                            p, p[i], i));
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;

                if (result.Contains(p))
                {
                    Print(string.Format("[VALIDATE] WARN: duplicate AlertPattern entry \"{0}\" — keeping only first occurrence.", p));
                    continue;
                }

                result.Add(p);
            }

            if (result.Count == 0)
            {
                Print("[VALIDATE] *** AlertPattern list produced 0 valid entries after parsing. Strategy cannot start. ***");
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
                            "AlertPattern overlap: \"{0}\" is a suffix of \"{1}\". Both will match the same brick when the tail ends in \"{1}\", but only ONE alert fires per brick (no extra information). Consider removing \"{0}\" if you don't need it.",
                            a, b);
                        Print(string.Format("[VALIDATE] WARN: {0}", msg));
                        try { Log(msg, LogLevel.Warning); } catch { }
                        warnCount++;
                    }
                }
            }
            if (warnCount == 0)
                Print("[VALIDATE] AlertPattern list: no suffix overlaps detected.");
            else
                Print(string.Format("[VALIDATE] AlertPattern list: {0} suffix-overlap warning(s) above. Strategy will still run.", warnCount));
        }

        private int[] TryCompileOne(string s, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                Print(string.Format("[VALIDATE] *** {0} is empty. Must be 1-10 chars of '0' or '1' only. ***", fieldName));
                return null;
            }
            if (s.Length > 10)
            {
                Print(string.Format("[VALIDATE] *** {0} is {1} chars; max allowed is 10. Pattern: \"{2}\" ***",
                    fieldName, s.Length, s));
                return null;
            }
            int[] result = new int[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '0') result[i] = 0;
                else if (c == '1') result[i] = 1;
                else
                {
                    Print(string.Format("[VALIDATE] *** {0} contains invalid char '{1}' at position {2}. ***",
                        fieldName, c, i));
                    return null;
                }
            }
            return result;
        }

        // [v1.4.5] Compute "qualified" boolean from the selected filter mode.
        // Uses the OPEN price of the last brick in the pattern (passed in).
        private bool EvaluateFilter(double patternEndOpen, double ema1Val, double ema2Val)
        {
            switch (EmaFilterMode)
            {
                case EmaFilterModeEnum.NoFilter:
                    return true;
                case EmaFilterModeEnum.OpenAbove:
                    return (patternEndOpen > ema1Val) && (patternEndOpen > ema2Val);
                case EmaFilterModeEnum.OpenBelow:
                    return (patternEndOpen < ema1Val) && (patternEndOpen < ema2Val);
                default:
                    return false;
            }
        }

        // =====================================================================
        // OnBarUpdate
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            if (!configValid) return;

            // DEFENSIVE POSITION-SYNC CHECK
            if (EnableOrders)
                CheckPositionSync();

            // ---- Daily reset check ----
            DateTime barTimeNy = TimeZoneInfo.ConvertTime(Time[0], nyTz);
            int minuteOfDayNy = barTimeNy.Hour * 60 + barTimeNy.Minute;
            int resetMin = RESET_HOUR_NY * 60 + RESET_MINUTE_NY;

            DateTime effectiveTradingDate;
            if (minuteOfDayNy >= resetMin)
                effectiveTradingDate = barTimeNy.Date;
            else
                effectiveTradingDate = barTimeNy.Date.AddDays(-1);

            if (effectiveTradingDate != currentTradingDateNy)
            {
                if (currentTradingDateNy != DateTime.MinValue)
                {
                    Print(string.Format("[RESET] New trading day at 9:30 NY ({0:yyyy-MM-dd}). Bricks: {1}, occurrences: {2}, traded: {3}, alerts: {4}, orders: {5}",
                        effectiveTradingDate, bricks.Count, dailyOccurrenceNumber, dailyTradedOccurrenceNumber, dailyAlertNumber, dailyOrderNumber));
                }
                else
                {
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}", effectiveTradingDate));
                }
                currentTradingDateNy = effectiveTradingDate;
                ResetState();
            }

            // ---- Append this brick's bit ----
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

            int currentBrickIdx = bricks.Count - 1;
            double currentPrice = Close[0];

            // ---- Working-state handling ----
            if (orderState == OrderSubState.Working && workingEntryOrder != null)
            {
                bricksSinceEntrySubmit++;

                if (currentWorkingOrderType == OrderEntryType.Limit)
                {
                    Print(string.Format("[ORDER] Working LIMIT brick tick: {0} of {1} (entry order #{2}, cancelRequested={3})",
                        bricksSinceEntrySubmit, UnfilledCancelBricks, entryOrderNumber, cancelRequested));

                    if (bricksSinceEntrySubmit >= UnfilledCancelBricks && !cancelRequested)
                    {
                        Print(string.Format("[ORDER] LIMIT entry order #{0} unfilled after {1} brick(s). Cancelling.",
                            entryOrderNumber, UnfilledCancelBricks));
                        WriteOrderRow("CANCEL_UNFILLED", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                        cancelRequested = true;
                        try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                        catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    }
                    else if (cancelRequested && bricksSinceEntrySubmit >= UnfilledCancelBricks + 2)
                    {
                        Print(string.Format("[ORDER] *** LIMIT GRACE PERIOD EXPIRED *** Entry #{0}. Verifying broker position before reset.",
                            entryOrderNumber));
                        WriteOrderRow("FORCE_RESET_STUCK", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "callback_never_arrived");
                        if (Position.MarketPosition == MarketPosition.Flat)
                        {
                            ResetOrderSubsystemToIdle("force_reset_stuck_limit_broker_flat");
                        }
                        else
                        {
                            Print(string.Format("[CRITICAL] LIMIT grace-period expired but Position={0} (not Flat). Force-flattening at market.",
                                Position.MarketPosition));
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
                        Print(string.Format("[CRITICAL] MARKET order #{0} no fill confirmation after {1:F1}s. Broker may be slow or disconnected. NOT cancelling. Manual check recommended.",
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
                {
                    Print(string.Format("[HEARTBEAT] orderState={0} for {1} bricks. workingEntryOrder={2}, cancelRequested={3}, bricksSinceEntrySubmit={4}, brokerPosition={5}.",
                        orderState, bricksInCurrentState,
                        workingEntryOrder == null ? "null" : "set",
                        cancelRequested, bricksSinceEntrySubmit,
                        Position.MarketPosition));
                }
            }
            else
            {
                bricksInCurrentState = 0;
            }

            // ---- Work-end force-flat check ----
            if (EnableOrders && EnableOrderHours && !forceFlatInProgress)
            {
                if (IsBeyondOrderEnd(barTimeNy))
                {
                    if (orderState == OrderSubState.Working)
                    {
                        Print(string.Format("[ORDER] Work-end reached while order WORKING. Cancelling entry order #{0}.", entryOrderNumber));
                        WriteOrderRow("CANCEL_WORKEND", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                        PlayOrderSound("Alert2.wav", "Force-flat work-end (cancel working entry)");
                        forceFlatInProgress = true;
                        try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                        catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    }
                    else if (orderState == OrderSubState.Position)
                    {
                        Print(string.Format("[ORDER] Work-end reached while POSITION_OPEN. Submitting market exit (bracket auto-cancels)."));
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
                    else if (orderState == OrderSubState.Idle
                             && Position.MarketPosition != MarketPosition.Flat)
                    {
                        Print(string.Format("[CRITICAL] Work-end reached: orderState=Idle but Position={0}. Force-flatten as part of work-end.",
                            Position.MarketPosition));
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
            List<PendingOccurrence> resolved = new List<PendingOccurrence>();

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

            // ---- Detect new occurrence ending at this brick ----
            int patternLen = compiledPattern.Length;
            if (bricks.Count >= patternLen)
            {
                int patternStartIdx = bricks.Count - patternLen;
                bool isMatch = true;
                for (int i = 0; i < patternLen; i++)
                {
                    if (bricks[patternStartIdx + i] != compiledPattern[i])
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    double ema1Val = ema1[0];
                    double ema2Val = ema2[0];
                    double openPrice = Open[0];  // OPEN of last brick in pattern = PatternEndOpen

                    // [v1.4.5] Apply the selected filter mode
                    bool emaQualified = EvaluateFilter(openPrice, ema1Val, ema2Val);

                    var po = new PendingOccurrence
                    {
                        PatternStartIndex = patternStartIdx,
                        PatternEndIndex   = patternStartIdx + patternLen - 1,
                        BricksWatched     = 0,
                        FollowUpMismatch  = false,
                        OccurrenceTime    = Time[0],
                        PatternEndPrice   = currentPrice,
                        EmaQualified      = emaQualified,
                        Ema1AtDetect      = ema1Val,
                        Ema2AtDetect      = ema2Val,
                        OpenAtDetect      = openPrice
                    };
                    pendingOccurrences.Add(po);

                    dailyOccurrenceNumber++;
                    if (emaQualified) dailyTradedOccurrenceNumber++;

                    po.OccurrenceNumberAtDetect = dailyOccurrenceNumber;
                    po.TradedOccurrenceNumberAtDetect = emaQualified ? dailyTradedOccurrenceNumber : 0;

                    Print(string.Format("[L1] Pattern \"{0}\" detected at brick {1} ({2}). Occurrence #{3} pending {4} follow-up bricks. Open={5:F2}, Close={6:F2}, EMA1={7:F2}, EMA2={8:F2}, FilterMode={9}, Qualified={10}",
                        PatternToMatch, currentBrickIdx, Time[0].ToString("HH:mm:ss"),
                        dailyOccurrenceNumber, compiledFollowUp.Length,
                        openPrice, currentPrice, ema1Val, ema2Val,
                        EmaFilterMode,
                        emaQualified ? "YES" : "NO"));

                    // [v1.4.5] ORDER trigger: qualified pattern + isAlerted
                    if (emaQualified)
                    {
                        TryEnterOrder(currentPrice, barTimeNy);
                    }
                }
            }
        }

        // =====================================================================
        // TryEnterOrder
        // [v1.4.5] Now dispatches Long vs Short via TradeDirection.
        // =====================================================================
        private void TryEnterOrder(double currentPrice, DateTime barTimeNy)
        {
            if (!EnableOrders)
            {
                return;
            }

            if (EnableOrderHours && !IsWithinOrderHours(barTimeNy))
            {
                Print(string.Format("[ORDER] Qualified pattern at {0} NY but outside order hours [{1:D2}:{2:D2}-{3:D2}:{4:D2}]. No order.",
                    barTimeNy.ToString("HH:mm:ss"),
                    OrderStartHourNY, OrderStartMinuteNY, OrderEndHourNY, OrderEndMinuteNY));
                return;
            }

            if (barTimeNy.Date > GoodTilDate.Date)
            {
                Print(string.Format("[ORDER] Qualified pattern at {0} NY but past GoodTilDate ({1:yyyy-MM-dd}). No order. Alerts continue.",
                    barTimeNy.ToString("yyyy-MM-dd"), GoodTilDate));
                return;
            }

            if (!isAlerted)
            {
                Print("[ORDER] Qualified pattern detected but isAlerted=false. No order.");
                return;
            }

            if (orderState != OrderSubState.Idle)
            {
                Print(string.Format("[ORDER] Qualified pattern detected but orderState={0} (not Idle). No order.", orderState));
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[ORDER] Qualified pattern detected but strategy position is {0} (not Flat). No order.",
                    Position.MarketPosition));
                return;
            }

            if (HasExternalPositionOnInstrument())
            {
                Print("[ORDER] Qualified pattern detected but external/manual position exists on this instrument in the account. No order.");
                return;
            }

            if (safetyBrakeTripped)
            {
                Print(string.Format("[ORDER] Qualified pattern detected but SAFETY BRAKE is tripped ({0} consecutive losses >= {1}). No order. Disable+re-enable strategy to reset.",
                    consecutiveLosses, MaxConsecutiveLosses));
                return;
            }

            dailyOrderNumber++;
            entryOrderNumber = dailyOrderNumber;
            lastEntryOrderNumber = entryOrderNumber;
            entryReferencePrice = currentPrice;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx = CurrentBar;
            currentWorkingOrderType = OrderType;
            cancelRequested = false;
            marketTimeoutLogged = false;

            // [v1.4.5] Record direction for this entry so OnExecutionUpdate
            // and work-end handlers can dispatch the correct exit calls.
            entryIsLong = (TradeDirection == TradeDirectionEnum.Long);
            string entrySignal = entryIsLong ? ENTRY_LONG_SIGNAL : ENTRY_SHORT_SIGNAL;

            double brickTicks = BarsPeriod.Value;
            int stopTicks   = (int)Math.Round(StopLossBricks    * brickTicks);
            int targetTicks = (int)Math.Round(ProfitTargetBricks * brickTicks);

            // SetStopLoss/SetProfitTarget in Ticks mode is direction-agnostic.
            // NT computes the correct side relative to the fill once direction is known.
            try
            {
                SetStopLoss(entrySignal, CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget(entrySignal, CalculationMode.Ticks, targetTicks);
                Print(string.Format("[ORDER] Pre-set OCO bracket for {0}: stop={1} ticks, target={2} ticks (server-side).",
                    entryIsLong ? "LONG" : "SHORT", stopTicks, targetTicks));
            }
            catch (Exception ex)
            {
                Print(string.Format("[CRITICAL] SetStopLoss/SetProfitTarget failed: {0}. ABORTING ENTRY.", ex.Message));
                return;
            }

            if (OrderType == OrderEntryType.Market)
            {
                Print(string.Format("[ORDER] *** SUBMIT MARKET {0} *** qty={1}, ref price (close)={2:F2}. Order #{3}.",
                    entryIsLong ? "BUY" : "SELL",
                    OrderQuantity, currentPrice, entryOrderNumber));
                WriteOrderRow("SUBMIT_MARKET", entryOrderNumber, "MARKET", 0, 0, 0, entryIsLong ? "LONG" : "SHORT");
                Print(string.Format("[ORDER] *** STATE: Idle -> Working *** (market entry submitted)"));
                orderState = OrderSubState.Working;
                bricksInCurrentState = 0;
                marketSubmitWallClock = DateTime.Now;
                try
                {
                    if (entryIsLong)
                        workingEntryOrder = EnterLong(OrderQuantity, entrySignal);
                    else
                        workingEntryOrder = EnterShort(OrderQuantity, entrySignal);
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] Enter{0} (market) error: {1}",
                        entryIsLong ? "Long" : "Short", ex.Message));
                    orderState = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
            else
            {
                // Limit: long buys BELOW close, short sells ABOVE close.
                double limitPrice = entryIsLong
                    ? (currentPrice - LimitUnderPoints)
                    : (currentPrice + LimitUnderPoints);
                limitPrice = Instrument.MasterInstrument.RoundToTickSize(limitPrice);
                entryLimitPrice = limitPrice;

                Print(string.Format("[ORDER] *** SUBMIT LIMIT {0} *** qty={1}, ref close={2:F2}, limit={3:F2} ({4:F2} pt {5} close). Wait {6} brick(s). Order #{7}.",
                    entryIsLong ? "BUY" : "SELL",
                    OrderQuantity, currentPrice, limitPrice,
                    LimitUnderPoints,
                    entryIsLong ? "below" : "above",
                    UnfilledCancelBricks, entryOrderNumber));
                WriteOrderRow("SUBMIT_LIMIT", entryOrderNumber, "LIMIT", limitPrice, 0, 0, entryIsLong ? "LONG" : "SHORT");
                Print(string.Format("[ORDER] *** STATE: Idle -> Working *** (limit entry submitted)"));
                orderState = OrderSubState.Working;
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
                    Print(string.Format("[ORDER] Enter{0}Limit error: {1}",
                        entryIsLong ? "Long" : "Short", ex.Message));
                    orderState = OrderSubState.Idle;
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
                        {
                            if (Position.MarketPosition == MarketPosition.Flat)
                                return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[ORDER] HasExternalPositionOnInstrument check error: {0}. Treating as no external position.", ex.Message));
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
        // OnOrderUpdate — recognize either Long or Short entry signal name.
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderUpdateState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            if (!EnableOrders) return;

            string oName = order.Name ?? "";
            bool isOurEntry = (workingEntryOrder != null && order == workingEntryOrder)
                              || ((oName == ENTRY_LONG_SIGNAL || oName == ENTRY_SHORT_SIGNAL)
                                  && orderState == OrderSubState.Working);

            if (isOurEntry)
            {
                Print(string.Format("[ORDER] OnOrderUpdate: entry order #{0}, name={1}, state={2}.",
                    entryOrderNumber, order.Name, orderUpdateState));

                if (orderUpdateState == NinjaTrader.Cbi.OrderState.Cancelled)
                {
                    if (!forceFlatInProgress)
                        PlayOrderSound("Alert3.wav", "Entry cancelled (unfilled)");
                    workingEntryOrder = null;
                    ResetOrderSubsystemToIdle("entry_cancelled_callback");
                }
                else if (orderUpdateState == NinjaTrader.Cbi.OrderState.Rejected)
                {
                    Print(string.Format("[ORDER] Entry order #{0} REJECTED: {1}", entryOrderNumber, error));
                    WriteOrderRow("REJECTED", entryOrderNumber, order.OrderType.ToString(), limitPrice, 0, 0, error.ToString());
                    workingEntryOrder = null;
                    ResetOrderSubsystemToIdle("entry_rejected");
                }
            }
        }

        // =====================================================================
        // OnExecutionUpdate
        // [v1.4.5] Recognizes either ENTRY_LONG_SIGNAL or ENTRY_SHORT_SIGNAL.
        //          Bracket leg fills are recognized by string-search for
        //          "Stop"/"Profit"/"Target" in the order name (NT auto-names
        //          bracket legs e.g. "Stop loss EntryV145Long").
        //          PnL sign is computed correctly per direction.
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (!EnableOrders) return;
            if (execution == null || execution.Order == null) return;

            string oName = execution.Order.Name ?? "";

            // Entry fill recognition (either direction)
            if ((oName == ENTRY_LONG_SIGNAL || oName == ENTRY_SHORT_SIGNAL)
                && (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                 || execution.Order.OrderState == NinjaTrader.Cbi.OrderState.PartFilled))
            {
                actualFillPrice = price;
                Print(string.Format("[ORDER] *** ENTRY FILLED *** order #{0} ({1}) qty={2} @ {3:F2} (state={4})",
                    entryOrderNumber, oName, quantity, price, execution.Order.OrderState));

                if (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled)
                {
                    PlayOrderSound("Alert4.wav", "Entry filled");
                    double brickPrice = BarsPeriod.Value * TickSize;
                    if (entryIsLong)
                    {
                        computedStopPrice   = actualFillPrice - (StopLossBricks     * brickPrice);
                        computedTargetPrice = actualFillPrice + (ProfitTargetBricks * brickPrice);
                    }
                    else
                    {
                        // SHORT: stop ABOVE entry, target BELOW entry
                        computedStopPrice   = actualFillPrice + (StopLossBricks     * brickPrice);
                        computedTargetPrice = actualFillPrice - (ProfitTargetBricks * brickPrice);
                    }
                    computedStopPrice   = Instrument.MasterInstrument.RoundToTickSize(computedStopPrice);
                    computedTargetPrice = Instrument.MasterInstrument.RoundToTickSize(computedTargetPrice);
                    Print(string.Format("[ORDER] Bracket auto-attached server-side ({0}): STOP ~{1:F2} ({2:F2} brick(s)), TARGET ~{3:F2} ({4:F2} brick(s)).",
                        entryIsLong ? "LONG" : "SHORT",
                        computedStopPrice, StopLossBricks, computedTargetPrice, ProfitTargetBricks));
                    WriteOrderRow("FILLED_BRACKET_ATTACH", entryOrderNumber, "n/a", actualFillPrice, computedStopPrice, computedTargetPrice, entryIsLong ? "LONG" : "SHORT");
                    Print(string.Format("[ORDER] *** STATE: Working -> Position *** (order #{0} filled)", entryOrderNumber));
                    orderState = OrderSubState.Position;
                    bricksInCurrentState = 0;
                    workingEntryOrder = null;
                    marketTimeoutLogged = false;
                }
                return;
            }

            // Bracket leg recognition (works for both directions — NT's auto-bracket
            // names contain "Stop" or "Profit"/"Target" regardless of direction).
            bool isStopFill   = oName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill)
                && execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                && orderState == OrderSubState.Position)
            {
                string which = isStopFill ? "STOP" : "TARGET";
                // PnL sign depends on direction:
                //   Long:  pnl = exit - entry
                //   Short: pnl = entry - exit
                double pnl = entryIsLong ? (price - actualFillPrice) : (actualFillPrice - price);
                Print(string.Format("[ORDER] *** {0} HIT *** ({1}) order #{2} ({3}) exit @ {4:F2}. Entry={5:F2}. PnL/contract={6:+0.00;-0.00} pt.",
                    which, entryIsLong ? "LONG" : "SHORT",
                    entryOrderNumber, oName, price, actualFillPrice, pnl));
                WriteOrderRow(isStopFill ? "EXIT_STOP" : "EXIT_TARGET", entryOrderNumber, "n/a", 0, computedStopPrice, computedTargetPrice, entryIsLong ? "LONG" : "SHORT");

                if (isStopFill)
                {
                    consecutiveLosses++;
                    PlayOrderSound("Glass Break.wav", "Stop loss hit");
                    Print(string.Format("[ORDER] Consecutive losses now = {0} of {1}.", consecutiveLosses, MaxConsecutiveLosses));
                    if (consecutiveLosses >= MaxConsecutiveLosses && !safetyBrakeTripped)
                    {
                        safetyBrakeTripped = true;
                        Print(string.Format("[CRITICAL] *** SAFETY BRAKE TRIPPED *** {0} consecutive losses >= MaxConsecutiveLosses={1}. ALL FURTHER ORDERS HALTED. Disable+re-enable strategy to reset.",
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
                        Print(string.Format("[ORDER] Win resets consecutive losses counter (was {0}).", consecutiveLosses));
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
                        Print(string.Format("[CRITICAL] *** DESYNC DETECTED *** orderState=Idle but broker Position={0} (qty={1}). Force-flattening at market.",
                            brokerPos, Position.Quantity));
                        WriteOrderRow("DESYNC_FORCE_FLAT", lastEntryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("orderState=Idle, brokerPosition={0}", brokerPos));
                        PlayOrderSound("Alert2.wav", "Desync ghost position detected");
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
                        Print(string.Format("[CRITICAL] *** DESYNC DETECTED *** orderState={0} but broker Position=Flat. Resetting to Idle.",
                            orderState));
                        WriteOrderRow("DESYNC_RESET_IDLE", lastEntryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("orderState={0}, brokerPosition=Flat", orderState));
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
            Print(string.Format("[CONN] Connection status update: {0}", status));

            if (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Disconnecting)
            {
                Print("[CRITICAL] *** BROKER CONNECTION LOST *** Server-side bracket should still protect open positions, but no new orders can be placed.");
                try { PlayOrderSound("Alert2.wav", "Broker connection lost"); }
                catch { }
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
            orderState = OrderSubState.Idle;
            workingEntryOrder = null;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx = -1;
            entryReferencePrice = 0.0;
            entryLimitPrice = 0.0;
            actualFillPrice = 0.0;
            computedStopPrice = 0.0;
            computedTargetPrice = 0.0;
            forceFlatInProgress = false;
            cancelRequested = false;
            bricksInCurrentState = 0;
            currentWorkingOrderType = OrderEntryType.Limit;
            marketSubmitWallClock = DateTime.MinValue;
            marketTimeoutLogged = false;
            // entryIsLong is NOT reset; it's set fresh at next TryEnterOrder.
        }

        // =====================================================================
        // HandleOccurrenceOutcome
        // =====================================================================
        private void HandleOccurrenceOutcome(PendingOccurrence po, string outcome, double currentPrice)
        {
            outcomeString.Append(outcome == "S" ? '1' : '0');

            bool firedAlert = false;
            bool capturedPostAlert = false;
            char capturedBit = '\0';

            if (po.EmaQualified)
            {
                tradedOutcomeString.Append(outcome == "S" ? '1' : '0');

                Print(string.Format("[L1] *** {0} *** at {1} (qualified). tradedOutcomeString = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
                    tradedOutcomeString.ToString()));

                if (pendingPostAlertCaptures > 0)
                {
                    capturedBit = outcome == "S" ? '1' : '0';
                    postAlertOutcomeString.Append(capturedBit);
                    pendingPostAlertCaptures--;
                    capturedPostAlert = true;
                    Print(string.Format("[POSTALERT] Captured outcome bit '{0}'. PostAlertOutcomeString = \"{1}\". Pending = {2}.",
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
                    Print(string.Format("[ALERTSTATE] isAlerted: {0} -> {1} (tail of tradedOutcomeString now ends in a pattern from the list = {2})",
                        prev, isAlerted, isAlerted ? "YES" : "no"));
            }
            else
            {
                Print(string.Format("[L1] *** {0} *** at {1} (NOT qualified, filter={2}). outcomeString = \"{3}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
                    EmaFilterMode,
                    outcomeString.ToString()));
            }

            WriteOccurrenceRow(po, outcome, currentPrice, firedAlert, capturedPostAlert, capturedBit);

            if (EnableChartMarkers && ShowOutcomeLabels)
            {
                string tag = "RSP_OUT_" + CurrentBar + "_" + po.OccurrenceNumberAtDetect;
                string prefix = po.EmaQualified ? "###" : "";
                string label = prefix + (outcome == "S" ? "PT/S" : "PT/F");
                Brush color = outcome == "S" ? Brushes.LimeGreen : Brushes.Red;

                double yOffset = outcome == "S" ? (3 * TickSize) : -(3 * TickSize);
                double yPos = outcome == "S" ? (High[0] + yOffset) : (Low[0] + yOffset);

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
                int start = tail.Length - p.Length;
                for (int i = 0; i < p.Length; i++)
                {
                    if (tail[start + i] != p[i])
                    {
                        match = false;
                        break;
                    }
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
            Print(string.Format("[ALERT] *** AlertPattern list matched the tail at {0}. Daily alert #{1}. Pending captures = {2}. ***",
                Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] Tail (last 20): \"...{0}\"",
                tradedOutcomeString.Length > 20
                    ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - 20)
                    : tradedOutcomeString.ToString()));
            Print("[ALERT] Watch the chart. Next qualified pattern occurrence is your trial.");

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Text(this, txt, string.Format("ALERT #{0}\nAP=\"{1}\"\n{2}/{3}", dailyAlertNumber, AlertPattern, EmaFilterMode, TradeDirection),
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
                Print(string.Format("[SOUND] {0} -> playing {1}", eventLabel, wavFileName));
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
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice, bool firedAlert, bool capturedPostAlert, char capturedBit)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.5");
                        sw.WriteLine(string.Format("# file_created_NY={0}",
                            TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# file_created_UTC={0}",
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# bar_type={0}", BarsPeriod.BarsPeriodType));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# PatternToMatch={0}", PatternToMatch));
                        sw.WriteLine(string.Format("# FollowUpPattern={0}", FollowUpPattern));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
                        sw.WriteLine(string.Format("# EmaFilterMode={0}", EmaFilterMode));
                        sw.WriteLine(string.Format("# TradeDirection={0}", TradeDirection));
                        sw.WriteLine(string.Format("# EnableOrders={0}", EnableOrders));
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
                    int startIdx = po.PatternEndIndex + 1;
                    for (int i = 0; i < compiledFollowUp.Length && (startIdx + i) < bricks.Count; i++)
                        actualFollow += bricks[startIdx + i].ToString();

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13:F2},{14:F2},{15},{16},{17},{18},{19},{20},{21},{22},{23},{24}",
                        TimeZoneInfo.ConvertTime(po.OccurrenceTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        po.PatternEndIndex,
                        PatternToMatch,
                        FollowUpPattern,
                        actualFollow,
                        outcome,
                        po.OpenAtDetect,
                        po.PatternEndPrice,
                        endPrice,
                        apForCsv,
                        po.OccurrenceNumberAtDetect,
                        outcomeString.ToString(),
                        po.Ema1AtDetect,
                        po.Ema2AtDetect,
                        po.EmaQualified ? "YES" : "NO",
                        po.TradedOccurrenceNumberAtDetect,
                        tradedOutcomeString.ToString(),
                        firedAlert ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",
                        capturedPostAlert ? capturedBit.ToString() : "",
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
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

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order_alert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.5");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine(string.Format("# EmaFilterMode={0}", EmaFilterMode));
                        sw.WriteLine(string.Format("# TradeDirection={0}", TradeDirection));
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
                        dailyAlertNumber,
                        apForCsv,
                        PatternToMatch,
                        FollowUpPattern,
                        Close[0],
                        outcomeString.ToString(),
                        tradedOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState));
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

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order_orders.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.5");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# StopLossBricks={0}", StopLossBricks));
                        sw.WriteLine(string.Format("# ProfitTargetBricks={0}", ProfitTargetBricks));
                        sw.WriteLine(string.Format("# LimitUnderPoints={0}", LimitUnderPoints));
                        sw.WriteLine(string.Format("# UnfilledCancelBricks={0}", UnfilledCancelBricks));
                        sw.WriteLine(string.Format("# EmaFilterMode={0}", EmaFilterMode));
                        sw.WriteLine(string.Format("# TradeDirection={0}", TradeDirection));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("# Event types:");
                        sw.WriteLine("#   SUBMIT_MARKET / SUBMIT_LIMIT   - entry order submitted (Extra='LONG' or 'SHORT')");
                        sw.WriteLine("#   FILLED_BRACKET_ATTACH          - entry filled, server-side bracket active (Extra='LONG'/'SHORT')");
                        sw.WriteLine("#   EXIT_STOP / EXIT_TARGET        - bracket leg filled (Extra='LONG'/'SHORT')");
                        sw.WriteLine("#   CANCEL_UNFILLED                - LIMIT entry cancelled (too many bricks elapsed)");
                        sw.WriteLine("#   CANCEL_WORKEND                 - entry cancelled because work-end reached");
                        sw.WriteLine("#   FORCEFLAT_WORKEND              - position force-flatted at work-end");
                        sw.WriteLine("#   FORCEFLAT_WORKEND_GHOST        - ghost position flatted at work-end");
                        sw.WriteLine("#   FORCE_RESET_STUCK              - LIMIT grace expired, callback never arrived");
                        sw.WriteLine("#   DESYNC_FORCE_FLAT              - orderState=Idle but broker has position");
                        sw.WriteLine("#   DESYNC_RESET_IDLE              - orderState=Working/Position but broker Flat");
                        sw.WriteLine("#   MARKET_HEARTBEAT_WARN          - market order no fill confirm after 30s");
                        sw.WriteLine("#   SAFETY_BRAKE_TRIPPED           - consecutive losses limit reached");
                        sw.WriteLine("#   REJECTED                       - entry order rejected");
                        sw.WriteLine("#");
                        sw.WriteLine("EventTime_NY,EventType,EntryOrderNumber,OrderTypeStr,Price,StopPrice,TargetPrice,OrderState,Extra");
                    }
                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        eventType,
                        orderNumber,
                        orderTypeStr,
                        price,
                        stopPx,
                        targetPx,
                        orderState,
                        extra));
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
            dailyOccurrenceNumber = 0;
            dailyTradedOccurrenceNumber = 0;
            dailyAlertNumber = 0;
            dailyOrderNumber = 0;
            beepCount = 0;
            outcomeString.Clear();
            tradedOutcomeString.Clear();
            postAlertOutcomeString.Clear();
            pendingPostAlertCaptures = 0;
            isAlerted = false;
            // Note: order subsystem state and consecutiveLosses are NOT cleared on daily reset.
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name="PatternToMatch",
            Description="Bit pattern to detect, 1-10 chars of '0' (red) or '1' (green). Default \"011\".",
            Order=1, GroupName="1. Layer 1 - Pattern")]
        public string PatternToMatch { get; set; }

        [NinjaScriptProperty]
        [Display(Name="FollowUpPattern",
            Description="Required follow-up bricks for SUCCESS. Default \"1\".",
            Order=2, GroupName="1. Layer 1 - Pattern")]
        public string FollowUpPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AlertPattern (comma-separated list OK)",
            Description="One or more suffix patterns, comma-separated. Each entry 1-10 chars of '0'/'1'. At each qualified brick close, if ANY pattern matches the tail of tradedOutcomeString, ONE alert fires and isAlerted becomes true. Examples: \"01\" (single), \"01, 011, 0111\" (any of three). Strategy warns if any entry is a suffix of another (redundant under the one-alert-per-brick rule).",
            Order=3, GroupName="2. Alert")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA1Period",
            Description="Period for EMA1. Default 20. Used by OpenAbove / OpenBelow filter modes; ignored when EmaFilterMode=NoFilter.",
            Order=4, GroupName="3. EMA Filter")]
        public int EMA1Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA2Period",
            Description="Period for EMA2. Default 9. Used by OpenAbove / OpenBelow filter modes; ignored when EmaFilterMode=NoFilter.",
            Order=5, GroupName="3. EMA Filter")]
        public int EMA2Period { get; set; }

        // [v1.4.5] NEW: EMA filter mode dropdown
        [NinjaScriptProperty]
        [Display(Name="EMA Filter Mode",
            Description="[v1.4.5] How the EMA filter qualifies pattern occurrences. NoFilter = every pattern qualifies (no EMA check). OpenAbove = qualify only when last-brick Open > BOTH EMAs (v1.4.4 default; uptrend-aligned). OpenBelow = qualify only when last-brick Open < BOTH EMAs (downtrend-aligned). All three modes are valid with any TradeDirection.",
            Order=6, GroupName="3. EMA Filter")]
        public EmaFilterModeEnum EmaFilterMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert. Default 3.",
            Order=7, GroupName="4. Beep")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=8, GroupName="4. Beep")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Master toggle for all chart drawings.",
            Order=9, GroupName="5. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowOutcomeLabels",
            Description="Show 'PT/S' / 'PT/F' text at each outcome brick.",
            Order=10, GroupName="5. Visuals")]
        public bool ShowOutcomeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for CSV files. Default C:\\temp.",
            Order=11, GroupName="6. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name="MaxBitsKept",
            Description="Max in-memory bit string length. Default 5000.",
            Order=12, GroupName="7. Advanced")]
        public int MaxBitsKept { get; set; }

        // =====================================================================
        // ORDER PROPERTIES
        // =====================================================================

        [NinjaScriptProperty]
        [Display(Name="EnableOrders",
            Description="Master switch for the order subsystem. When OFF, behaves like alert-only (no real orders).",
            Order=1, GroupName="8. Order Execution")]
        public bool EnableOrders { get; set; }

        // [v1.4.5] NEW: Trade direction dropdown
        [NinjaScriptProperty]
        [Display(Name="Trade Direction",
            Description="[v1.4.5] Long = EnterLong/EnterLongLimit (stop below entry, target above). Short = EnterShort/EnterShortLimit (stop above entry, target below). Independent of EmaFilterMode — all 6 combinations allowed.",
            Order=2, GroupName="8. Order Execution")]
        public TradeDirectionEnum TradeDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Order quantity (CONTRACTS)",
            Description="Number of contracts per entry. Default 1.",
            Order=3, GroupName="8. Order Execution")]
        public int OrderQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Order type (Market or Limit)",
            Description="Limit: long buys BELOW close, short sells ABOVE close. Market = immediate fill (may slip).",
            Order=4, GroupName="8. Order Execution")]
        public OrderEntryType OrderType { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name="Limit offset from close (POINTS)",
            Description="(LIMIT only) Distance in points. For Long: limit = close - this (buy below). For Short: limit = close + this (sell above). Default 5.0. Ignored if Market.",
            Order=5, GroupName="8. Order Execution")]
        public double LimitUnderPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name="Limit unfilled cancel after N (BRICKS)",
            Description="(LIMIT only) Cancel limit if not filled after this many brick closes. Default 1. Market orders never cancelled.",
            Order=6, GroupName="8. Order Execution")]
        public int UnfilledCancelBricks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50.0)]
        [Display(Name="Stop loss distance (BRICKS, decimal OK)",
            Description="Stop-loss distance from fill, in BRICKS. Default 1.0. Applied as -bricks for Long (stop below), +bricks for Short (stop above). Server-side via SetStopLoss.",
            Order=7, GroupName="8. Order Execution")]
        public double StopLossBricks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50.0)]
        [Display(Name="Profit target distance (BRICKS, decimal OK)",
            Description="Profit-target distance from fill, in BRICKS. Default 1.0. Applied as +bricks for Long (target above), -bricks for Short (target below). Server-side via SetProfitTarget.",
            Order=8, GroupName="8. Order Execution")]
        public double ProfitTargetBricks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Max consecutive losses before halt (99 = OFF)",
            Description="Halt new orders after this many losing trades in a row. Default 99 (effectively off). Counts EXIT_STOP regardless of direction. Disable+re-enable strategy to reset.",
            Order=9, GroupName="8. Order Execution")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Order Hours filter",
            Description="If true, restrict order placement to the NY time window below. Alerts still fire 24h.",
            Order=1, GroupName="9. Order Hours (NY) & Expiry")]
        public bool EnableOrderHours { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="Order start HOUR (NY, 0-23)",
            Description="Order window start hour, NY time. Default 9.",
            Order=2, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderStartHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="Order start MINUTE (NY, 0-59)",
            Description="Order window start minute, NY time. Default 30.",
            Order=3, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderStartMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="Order end HOUR (NY, 0-23)",
            Description="Order window end hour, NY time. Default 15.",
            Order=4, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderEndHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="Order end MINUTE (NY, 0-59)",
            Description="Order window end minute, NY time. Default 55. At/after this time: position force-flat at market, working orders cancelled.",
            Order=5, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderEndMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Good til DATE (NY)",
            Description="Last NY date orders may be placed. After this date, no new orders (alerts continue).",
            Order=6, GroupName="9. Order Hours (NY) & Expiry")]
        public DateTime GoodTilDate { get; set; }

        #endregion
    }
}
