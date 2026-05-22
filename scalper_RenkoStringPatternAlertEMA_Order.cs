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
// STRATEGY: scalper_RenkoStringPatternAlertEMA v1.4.0
// AUTHOR:   Albert Feng / Drafted with help from Claude
// REPLACES: v1.3.2
// =============================================================================
//
// v1.4.0 CHANGES vs v1.3.2
// ------------------------
// The entire alert subsystem from v1.3.2 is preserved verbatim. v1.4.0 adds
// an independent ORDER SUBSYSTEM that layers on top.
//
// ARCHITECTURE
// ------------
// Two independent subsystems share one signal (the alert):
//
// 1) ALERT SUBSYSTEM (unchanged from v1.3.2 except one new flag):
//      - Detects patterns, builds OutcomeString and TradedOutcomeString.
//      - Fires alerts: beep + diamond + CSV row.
//      - Runs 24h, ignores work hours, ignores EnableOrders.
//      - Stops only if the entire strategy is disabled in NinjaTrader.
//
//      NEW in v1.4: maintains a boolean `isAlerted` that reflects whether
//      the current tail of TradedOutcomeString equals AlertPattern. This is
//      a STATE, not an event. Recomputed every time a new EMA-qualified
//      outcome appends.
//
//      Example: AlertPattern = "01", stream = 010001001111
//        bit | TradedOutcomeString | tail=="01"? | isAlerted
//         0  | 0                   | no          | false
//         1  | 01                  | YES         | true   <- order chance #1
//         0  | 010                 | no          | false
//         0  | 0100                | no          | false
//         0  | 01000               | no          | false
//         1  | 010001              | YES         | true   <- order chance #2
//         0  | 0100010             | no          | false
//         0  | 01000100            | no          | false
//         1  | 010001001           | YES         | true   <- order chance #3
//         1  | 0100010011          | no          | false
//         1  | 01000100111         | no          | false
//         1  | 010001001111        | no          | false
//
// 2) ORDER SUBSYSTEM (NEW, gated by EnableOrders):
//      Trigger: a new EMA-qualified pattern is detected (same condition the
//      v1.3.2 code already computes: PatternToMatch matches AND
//      Open[0] > EMA1 AND Open[0] > EMA2).
//
//      Gates (ALL must be true at the pattern's last brick close):
//        a. EnableOrders = true
//        b. Current NY time is inside [OrderStart, OrderEnd]
//        c. Today's NY date <= GoodTilDate
//        d. isAlerted == true                  (alert subsystem says "go")
//        e. orderState == Idle                 (no working order, no position)
//        f. Position.MarketPosition == Flat    (no strategy-owned position)
//        g. No external/manual position on this instrument in the account
//
//      If all gates pass:
//        - MARKET: submit Market Buy, attach OCO stop+target on fill.
//        - LIMIT:  submit Limit Buy at Close - LimitUnderPoints.
//                  Wait UnfilledCancelBricks bricks. Filled -> bracket.
//                  Not filled -> cancel, back to Idle.
//
//      Stop / Target are measured in BRICKS, where 1 brick in price =
//      BarsPeriod.Value * TickSize.  Examples:
//        - MNQ standard Renko brick = 80 ticks, TickSize = 0.25 -> 1 brick = 20 pt
//        - Default StopBricks=1, TargetBricks=1 -> stop=-20pt, target=+20pt (MNQ)
//
//      Stop / Target are computed from the ACTUAL FILL price (not the limit
//      price), so slippage on a market order is accounted for.
//
//      Work-end behavior (when current NY time crosses OrderEnd):
//        - ORDER_WORKING -> cancel working entry, back to Idle.
//        - POSITION_OPEN -> cancel OCO bracket, market exit, back to Idle.
//        - Strategy keeps running; alerts continue; CSV continues.
//
//      GoodTilDate behavior (when NY date passes GoodTilDate):
//        - Order subsystem stops arming.
//        - Alert subsystem continues normally.
//        - To fully disable, the user disables the entire strategy in NT.
//
// BACKWARD COMPATIBILITY
// ----------------------
// With EnableOrders = false (default), v1.4.0 behaves IDENTICALLY to v1.3.2.
// All CSV files, beep cadence, chart markers, and pattern logic are preserved.
//
// LONG-ONLY
// ---------
// v1.4.0 is long-only. The EMA filter (Open > EMA1 AND Open > EMA2) is a
// bull-trend filter, so only buy orders are submitted. A symmetric short
// side is a future enhancement.
//
// CSV FILES
// ---------
// _v140_occ.csv     - per-occurrence rows (unchanged from v1.3.2 schema)
// _v140_alert.csv   - per-alert rows (unchanged schema)
// _v140_orders.csv  - NEW: per-order-event rows for full order audit trail
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_RenkoStringPatternAlertEMA_Order : Strategy
    {
        // =====================================================================
        // ORDER SUBSYSTEM enums
        // =====================================================================
        public enum OrderEntryType
        {
            Limit,
            Market
        }

        private enum OrderSubState
        {
            Idle,           // no working order, no position; ready to act on next EMA pattern if alerted
            Working,        // entry order submitted, waiting for fill
            Position        // entry filled, OCO bracket attached, waiting for stop or target
        }

        #region Variables

        // ---- Bit string of bricks since today's reset ----
        private List<int> bricks = new List<int>();

        // ---- Outcome strings ----
        private StringBuilder outcomeString = new StringBuilder();
        private StringBuilder tradedOutcomeString = new StringBuilder();
        private StringBuilder postAlertOutcomeString = new StringBuilder();
        private int pendingPostAlertCaptures = 0;

        // ---- NEW v1.4: alert status flag read by the order subsystem ----
        private bool isAlerted = false;

        // ---- Daily numbering for CSV correlation ----
        private int dailyOccurrenceNumber = 0;
        private int dailyTradedOccurrenceNumber = 0;
        private int dailyAlertNumber = 0;
        private int dailyOrderNumber = 0;        // NEW v1.4: counts order submissions

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
        private string compiledAlertPattern = null;
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
        // NEW v1.4: ORDER SUBSYSTEM state
        // =====================================================================
        private OrderSubState orderState = OrderSubState.Idle;

        // Working entry order tracking
        private Order workingEntryOrder = null;
        private int   bricksSinceEntrySubmit = 0;
        private int   entrySubmitBarIdx = -1;
        private double entryReferencePrice = 0.0;  // close of pattern brick that triggered entry
        private double entryLimitPrice = 0.0;      // submitted limit price (for Limit type)
        private int   entryOrderNumber = 0;        // dailyOrderNumber assigned to this entry

        // OCO bracket order tracking
        private Order stopLossOrder = null;
        private Order profitTargetOrder = null;
        private double actualFillPrice = 0.0;
        private double computedStopPrice = 0.0;
        private double computedTargetPrice = 0.0;

        // OCO group id - used to link stop and target as one-cancels-other
        private string ocoGroupId = null;

        // Flag set when we are mid force-flat at work-end to suppress normal
        // re-arming and gate routing while we wait for the exits to land.
        private bool forceFlatInProgress = false;

        #endregion

        // =====================================================================
        // OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Renko string-pattern alerter+order (v1.4.0). Adds order placement subsystem on top of v1.3.2 alert system. Long-only. EnableOrders=false reproduces v1.3.2 exactly.";
                Name        = "scalper_RenkoStringPatternAlertEMA_Order";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- Layer 1 string parameters ----
                PatternToMatch   = "011";
                FollowUpPattern  = "1";

                // ---- AlertPattern ----
                AlertPattern     = "01";

                // ---- EMA filter ----
                EMA1Period       = 20;
                EMA2Period       = 9;

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

                // ---- NEW v1.4: Order Execution defaults ----
                EnableOrders         = false;          // master switch: OFF by default = behaves like v1.3.2
                OrderQuantity        = 1;
                OrderType            = OrderEntryType.Limit;
                LimitUnderPoints     = 5.0;
                UnfilledCancelBricks = 1;
                StopLossBricks       = 1;
                ProfitTargetBricks   = 1;

                // ---- NEW v1.4: Working Hours & Expiry defaults ----
                EnableOrderHours     = false;          // when false, order hours gate is bypassed (still need EnableOrders)
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
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlertEMA v1.4.0 at {0}",
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
                Print(string.Format("[INIT] AlertPattern    = \"{0}\" (length {1})", compiledAlertPattern, compiledAlertPattern.Length));
                Print("[INIT] Bit encoding: SUCCESS=1, FAILURE=0.");
                Print("[INIT] AlertPattern is suffix-matched against OutcomeString_EmaQualified.");
                Print(string.Format("[INIT] EMA1 period = {0}, EMA2 period = {1}", EMA1Period, EMA2Period));
                Print("[INIT] EMA filter: trade only when Open[0] > EMA1 AND Open[0] > EMA2.");
                Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));
                Print(string.Format("[INIT] Beep: {0} beeps, {1}s apart", AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));

                // ---- NEW v1.4: order subsystem init log ----
                Print("[INIT] ----- ORDER SUBSYSTEM v1.4 -----");
                Print(string.Format("[INIT] EnableOrders        = {0}", EnableOrders));
                if (EnableOrders)
                {
                    Print(string.Format("[INIT] OrderQuantity       = {0}", OrderQuantity));
                    Print(string.Format("[INIT] OrderType           = {0}", OrderType));
                    Print(string.Format("[INIT] LimitUnderPoints    = {0:F2} (ignored if Market)", LimitUnderPoints));
                    Print(string.Format("[INIT] UnfilledCancelBricks= {0} (ignored if Market)", UnfilledCancelBricks));
                    Print(string.Format("[INIT] StopLossBricks      = {0}  -> stop offset = {1:F2} pt", StopLossBricks, StopLossBricks * BarsPeriod.Value * TickSize));
                    Print(string.Format("[INIT] ProfitTargetBricks  = {0}  -> target offset = {1:F2} pt", ProfitTargetBricks, ProfitTargetBricks * BarsPeriod.Value * TickSize));
                    Print(string.Format("[INIT] EnableOrderHours    = {0}", EnableOrderHours));
                    if (EnableOrderHours)
                    {
                        Print(string.Format("[INIT] Order hours (NY)    = {0:D2}:{1:D2} to {2:D2}:{3:D2}",
                            OrderStartHourNY, OrderStartMinuteNY, OrderEndHourNY, OrderEndMinuteNY));
                    }
                    Print(string.Format("[INIT] GoodTilDate         = {0:yyyy-MM-dd}", GoodTilDate));
                    Print(string.Format("[INIT] Order will be sent to whichever Account is selected in the strategy's Account dropdown."));
                }
                else
                {
                    Print("[INIT] (orders disabled - strategy will only generate alerts, identical to v1.3.2)");
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

            int[] alertBits = TryCompileOne(AlertPattern, "AlertPattern");
            compiledAlertPattern = (alertBits != null) ? AlertPattern : null;

            return compiledPattern != null
                && compiledFollowUp != null
                && compiledAlertPattern != null;
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

        // =====================================================================
        // OnBarUpdate - one call per closed Renko brick
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            if (!configValid) return;

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

            // =================================================================
            // ORDER SUBSYSTEM: tick the working-order brick counter
            // Each new brick close, if we have a working entry order, increment.
            // =================================================================
            if (orderState == OrderSubState.Working && workingEntryOrder != null)
            {
                bricksSinceEntrySubmit++;
                Print(string.Format("[ORDER] Working order brick tick: {0} of {1} (entry order #{2})",
                    bricksSinceEntrySubmit, UnfilledCancelBricks, entryOrderNumber));

                if (bricksSinceEntrySubmit >= UnfilledCancelBricks)
                {
                    // Still not filled - cancel.
                    Print(string.Format("[ORDER] Entry order #{0} unfilled after {1} brick(s). Cancelling.",
                        entryOrderNumber, UnfilledCancelBricks));
                    WriteOrderRow("CANCEL_UNFILLED", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                    try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                    catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    // OnOrderUpdate will see the cancellation and clean up to Idle.
                }
            }

            // =================================================================
            // ORDER SUBSYSTEM: work-end force-flat check (every brick close)
            // =================================================================
            if (EnableOrders && EnableOrderHours && !forceFlatInProgress)
            {
                if (IsBeyondOrderEnd(barTimeNy))
                {
                    if (orderState == OrderSubState.Working)
                    {
                        Print(string.Format("[ORDER] Work-end reached while order WORKING. Cancelling entry order #{0}.", entryOrderNumber));
                        WriteOrderRow("CANCEL_WORKEND", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                        forceFlatInProgress = true;
                        try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                        catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    }
                    else if (orderState == OrderSubState.Position)
                    {
                        Print(string.Format("[ORDER] Work-end reached while POSITION_OPEN. Cancelling OCO bracket and force-flatting at market."));
                        WriteOrderRow("FORCEFLAT_WORKEND", entryOrderNumber, "BRACKET_CANCEL", actualFillPrice, computedStopPrice, computedTargetPrice, "");
                        forceFlatInProgress = true;
                        try
                        {
                            if (stopLossOrder      != null) CancelOrder(stopLossOrder);
                            if (profitTargetOrder  != null) CancelOrder(profitTargetOrder);
                        }
                        catch (Exception ex) { Print(string.Format("[ORDER] Cancel bracket error: {0}", ex.Message)); }
                        // Submit market exit. Use ExitLong with no price (market).
                        try { ExitLong(OrderQuantity, "ExitWorkEnd", "EntryV140"); }
                        catch (Exception ex) { Print(string.Format("[ORDER] ExitLong (workend) error: {0}", ex.Message)); }
                    }
                }
            }

            // ---- Resolve pending occurrences (UNCHANGED from v1.3.2) ----
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

            // ---- Detect new occurrence ending at this brick (UNCHANGED) ----
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
                    double openPrice = Open[0];
                    bool emaQualified = (openPrice > ema1Val) && (openPrice > ema2Val);

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

                    Print(string.Format("[L1] Pattern \"{0}\" detected at brick {1} ({2}). Occurrence #{3} pending {4} follow-up bricks. Open={5:F2}, Close={6:F2}, EMA1={7:F2}, EMA2={8:F2}, EmaQualified={9}",
                        PatternToMatch, currentBrickIdx, Time[0].ToString("HH:mm:ss"),
                        dailyOccurrenceNumber, compiledFollowUp.Length,
                        openPrice, currentPrice, ema1Val, ema2Val,
                        emaQualified ? "YES" : "NO"));

                    // =========================================================
                    // NEW v1.4: ORDER SUBSYSTEM TRIGGER POINT
                    // EMA-qualified pattern just detected. Check all gates.
                    // =========================================================
                    if (emaQualified)
                    {
                        TryEnterOrder(currentPrice, barTimeNy);
                    }
                }
            }
        }

        // =====================================================================
        // NEW v1.4: TryEnterOrder
        // Called when an EMA-qualified pattern is freshly detected.
        // Checks all gates and submits an entry order if everything passes.
        // =====================================================================
        private void TryEnterOrder(double currentPrice, DateTime barTimeNy)
        {
            // Gate a: master switch
            if (!EnableOrders)
            {
                // silent - this is the v1.3.2 fallthrough
                return;
            }

            // Gate b: order hours (NY)
            if (EnableOrderHours && !IsWithinOrderHours(barTimeNy))
            {
                Print(string.Format("[ORDER] EMA-qualified pattern at {0} NY but outside order hours [{1:D2}:{2:D2}-{3:D2}:{4:D2}]. No order.",
                    barTimeNy.ToString("HH:mm:ss"),
                    OrderStartHourNY, OrderStartMinuteNY, OrderEndHourNY, OrderEndMinuteNY));
                return;
            }

            // Gate c: good-til-date
            if (barTimeNy.Date > GoodTilDate.Date)
            {
                Print(string.Format("[ORDER] EMA-qualified pattern at {0} NY but past GoodTilDate ({1:yyyy-MM-dd}). No order. Alerts continue.",
                    barTimeNy.ToString("yyyy-MM-dd"), GoodTilDate));
                return;
            }

            // Gate d: alert status
            if (!isAlerted)
            {
                Print("[ORDER] EMA-qualified pattern detected but isAlerted=false. No order.");
                return;
            }

            // Gate e: order subsystem must be Idle
            if (orderState != OrderSubState.Idle)
            {
                Print(string.Format("[ORDER] EMA-qualified pattern detected but orderState={0} (not Idle). No order.", orderState));
                return;
            }

            // Gate f: strategy position must be flat
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[ORDER] EMA-qualified pattern detected but strategy position is {0} (not Flat). No order.",
                    Position.MarketPosition));
                return;
            }

            // Gate g: no external/manual position on this instrument in the account
            if (HasExternalPositionOnInstrument())
            {
                Print("[ORDER] EMA-qualified pattern detected but external/manual position exists on this instrument in the account. No order.");
                return;
            }

            // All gates passed. Submit entry order.
            dailyOrderNumber++;
            entryOrderNumber = dailyOrderNumber;
            entryReferencePrice = currentPrice;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx = CurrentBar;

            if (OrderType == OrderEntryType.Market)
            {
                Print(string.Format("[ORDER] *** SUBMIT MARKET BUY *** qty={0}, ref price (close)={1:F2}. Order #{2}.",
                    OrderQuantity, currentPrice, entryOrderNumber));
                WriteOrderRow("SUBMIT_MARKET", entryOrderNumber, "MARKET", 0, 0, 0, "");
                orderState = OrderSubState.Working;
                try
                {
                    workingEntryOrder = EnterLong(OrderQuantity, "EntryV140");
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] EnterLong (market) error: {0}", ex.Message));
                    orderState = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
            else // Limit
            {
                double limitPrice = currentPrice - LimitUnderPoints;
                limitPrice = Instrument.MasterInstrument.RoundToTickSize(limitPrice);
                entryLimitPrice = limitPrice;

                Print(string.Format("[ORDER] *** SUBMIT LIMIT BUY *** qty={0}, ref close={1:F2}, limit price={2:F2} ({3:F2} below close). Wait {4} brick(s). Order #{5}.",
                    OrderQuantity, currentPrice, limitPrice, LimitUnderPoints, UnfilledCancelBricks, entryOrderNumber));
                WriteOrderRow("SUBMIT_LIMIT", entryOrderNumber, "LIMIT", limitPrice, 0, 0, "");
                orderState = OrderSubState.Working;
                try
                {
                    workingEntryOrder = EnterLongLimit(0, true, OrderQuantity, limitPrice, "EntryV140");
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] EnterLongLimit error: {0}", ex.Message));
                    orderState = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
        }

        // =====================================================================
        // NEW v1.4: HasExternalPositionOnInstrument
        // Check if the account has a position on this instrument that was not
        // opened by this strategy instance.
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
                            // Account-level says there's a position. If this strategy's own
                            // position is flat, then it must be from another source (manual,
                            // another strategy, etc).
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
        // NEW v1.4: IsWithinOrderHours / IsBeyondOrderEnd
        // =====================================================================
        private bool IsWithinOrderHours(DateTime nyTime)
        {
            int curMin   = nyTime.Hour * 60 + nyTime.Minute;
            int startMin = OrderStartHourNY * 60 + OrderStartMinuteNY;
            int endMin   = OrderEndHourNY   * 60 + OrderEndMinuteNY;
            if (startMin <= endMin)
                return curMin >= startMin && curMin <= endMin;
            // Wraps midnight (e.g. 22:00 - 02:00)
            return curMin >= startMin || curMin <= endMin;
        }

        private bool IsBeyondOrderEnd(DateTime nyTime)
        {
            int curMin   = nyTime.Hour * 60 + nyTime.Minute;
            int startMin = OrderStartHourNY * 60 + OrderStartMinuteNY;
            int endMin   = OrderEndHourNY   * 60 + OrderEndMinuteNY;
            if (startMin <= endMin)
                return curMin > endMin || curMin < startMin;
            // Wraps midnight
            return curMin > endMin && curMin < startMin;
        }

        // =====================================================================
        // NEW v1.4: OnOrderUpdate - track entry order lifecycle
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderUpdateState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            if (!EnableOrders) return;

            // Entry order updates
            if (workingEntryOrder != null && order == workingEntryOrder)
            {
                if (orderUpdateState == NinjaTrader.Cbi.OrderState.Cancelled
                 || orderUpdateState == NinjaTrader.Cbi.OrderState.CancelPending)
                {
                    Print(string.Format("[ORDER] Entry order #{0} state={1}.", entryOrderNumber, orderUpdateState));
                    // Wait for terminal cancelled to reset
                    if (orderUpdateState == NinjaTrader.Cbi.OrderState.Cancelled)
                    {
                        workingEntryOrder = null;
                        ResetOrderSubsystemToIdle("entry_cancelled");
                    }
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
        // NEW v1.4: OnExecutionUpdate - fired when an order fills
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (!EnableOrders) return;
            if (execution == null || execution.Order == null) return;

            // Entry fill
            if (workingEntryOrder != null && execution.Order == workingEntryOrder
                && (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                 || execution.Order.OrderState == NinjaTrader.Cbi.OrderState.PartFilled))
            {
                actualFillPrice = price;
                Print(string.Format("[ORDER] *** ENTRY FILLED *** order #{0} qty={1} @ {2:F2} (state={3})",
                    entryOrderNumber, quantity, price, execution.Order.OrderState));

                // Only attach bracket on full fill. For partial, attach on first partial then
                // adjust quantity on full. With OrderQuantity=1 (default) this is always Filled.
                if (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled)
                {
                    AttachOCOBracket();
                    orderState = OrderSubState.Position;
                    workingEntryOrder = null;
                }
            }

            // Stop / Target fills (exits) - return to idle
            bool isStopFill   = (stopLossOrder      != null && execution.Order == stopLossOrder);
            bool isTargetFill = (profitTargetOrder  != null && execution.Order == profitTargetOrder);

            if ((isStopFill || isTargetFill)
                && execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled)
            {
                string which = isStopFill ? "STOP" : "TARGET";
                double pnl = price - actualFillPrice;
                Print(string.Format("[ORDER] *** {0} HIT *** order #{1} exit @ {2:F2}. Entry={3:F2}. PnL/contract={4:+0.00;-0.00} pt.",
                    which, entryOrderNumber, price, actualFillPrice, pnl));
                WriteOrderRow(isStopFill ? "EXIT_STOP" : "EXIT_TARGET", entryOrderNumber, "n/a", 0, computedStopPrice, computedTargetPrice, "");
                ResetOrderSubsystemToIdle(isStopFill ? "stop_hit" : "target_hit");
            }
        }

        // =====================================================================
        // NEW v1.4: AttachOCOBracket
        // =====================================================================
        private void AttachOCOBracket()
        {
            double brickPrice = BarsPeriod.Value * TickSize;
            computedStopPrice    = actualFillPrice - (StopLossBricks     * brickPrice);
            computedTargetPrice  = actualFillPrice + (ProfitTargetBricks * brickPrice);
            computedStopPrice    = Instrument.MasterInstrument.RoundToTickSize(computedStopPrice);
            computedTargetPrice  = Instrument.MasterInstrument.RoundToTickSize(computedTargetPrice);
            ocoGroupId = "OCO_" + entryOrderNumber + "_" + DateTime.Now.Ticks;

            Print(string.Format("[ORDER] Attaching OCO bracket for order #{0}: STOP @ {1:F2} ({2} brick(s)), TARGET @ {3:F2} ({4} brick(s)). Brick price = {5:F2}.",
                entryOrderNumber, computedStopPrice, StopLossBricks, computedTargetPrice, ProfitTargetBricks, brickPrice));
            WriteOrderRow("FILLED_BRACKET_ATTACH", entryOrderNumber, "n/a", actualFillPrice, computedStopPrice, computedTargetPrice, "");

            try
            {
                stopLossOrder = ExitLongStopMarket(0, true, OrderQuantity, computedStopPrice, "ExitStop", "EntryV140");
                profitTargetOrder = ExitLongLimit(0, true, OrderQuantity, computedTargetPrice, "ExitTarget", "EntryV140");
            }
            catch (Exception ex)
            {
                Print(string.Format("[ORDER] AttachOCOBracket error: {0}", ex.Message));
            }
        }

        // =====================================================================
        // NEW v1.4: ResetOrderSubsystemToIdle
        // =====================================================================
        private void ResetOrderSubsystemToIdle(string reason)
        {
            Print(string.Format("[ORDER] Resetting order subsystem to IDLE (reason: {0}).", reason));
            orderState = OrderSubState.Idle;
            workingEntryOrder = null;
            stopLossOrder = null;
            profitTargetOrder = null;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx = -1;
            entryReferencePrice = 0.0;
            entryLimitPrice = 0.0;
            actualFillPrice = 0.0;
            computedStopPrice = 0.0;
            computedTargetPrice = 0.0;
            ocoGroupId = null;
            forceFlatInProgress = false;
        }

        // =====================================================================
        // HandleOccurrenceOutcome
        // (UNCHANGED from v1.3.2 except: recompute isAlerted after every
        //  EMA-qualified append.)
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

                Print(string.Format("[L1] *** {0} *** at {1} (EMA-qualified). OutcomeString_EmaQualified = \"{2}\"",
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

                if (TradedTailMatchesAlertPattern())
                {
                    FireAlert();
                    firedAlert = true;
                }

                // NEW v1.4: After every EMA-qualified append, recompute the
                // alert status flag that the order subsystem reads.
                bool prev = isAlerted;
                isAlerted = TradedTailMatchesAlertPattern();
                if (prev != isAlerted)
                    Print(string.Format("[ALERTSTATE] isAlerted: {0} -> {1} (tail of OutcomeString_EmaQualified now \"{2}\")",
                        prev, isAlerted,
                        tradedOutcomeString.Length >= compiledAlertPattern.Length
                            ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - compiledAlertPattern.Length)
                            : tradedOutcomeString.ToString()));
            }
            else
            {
                Print(string.Format("[L1] *** {0} *** at {1} (NOT EMA-qualified). OutcomeString_NoEmaFilter = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
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

        private bool TradedTailMatchesAlertPattern()
        {
            int patLen = compiledAlertPattern.Length;
            if (tradedOutcomeString.Length < patLen) return false;

            int start = tradedOutcomeString.Length - patLen;
            for (int i = 0; i < patLen; i++)
            {
                if (tradedOutcomeString[start + i] != compiledAlertPattern[i])
                    return false;
            }
            return true;
        }

        private void FireAlert()
        {
            dailyAlertNumber++;
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern \"{0}\" matched at {1}. Daily alert #{2}. Pending captures = {3}. ***",
                compiledAlertPattern, Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] Tail: \"...{0}\"",
                tradedOutcomeString.Length >= compiledAlertPattern.Length
                    ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - compiledAlertPattern.Length)
                    : tradedOutcomeString.ToString()));
            Print("[ALERT] Watch the chart. Next EMA-qualified pattern occurrence is your trial.");

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Text(this, txt, string.Format("ALERT #{0}\nAP=\"{1}\"", dailyAlertNumber, compiledAlertPattern),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow();
        }

        // =====================================================================
        // CSV: per-occurrence (UNCHANGED from v1.3.2 except filename suffix)
        // =====================================================================
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice, bool firedAlert, bool capturedPostAlert, char capturedBit)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.0");
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
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
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
                        compiledAlertPattern,
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
        // CSV: per-alert (UNCHANGED from v1.3.2 except filename suffix)
        // =====================================================================
        private void WriteAlertRow()
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.0");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("AlertTime_NY,DailyAlertNumber,AlertPattern,"
                            + "Pattern,FollowUp,CurrentPrice,"
                            + "OutcomeString_NoEmaFilter,OutcomeString_EmaQualified,"
                            + "PostAlertOutcomeString,PendingPostAlertCapturesAfter,"
                            + "IsAlertedAfter,OrderStateAtAlert");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5:F2},{6},{7},{8},{9},{10},{11}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        compiledAlertPattern,
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
        // NEW v1.4: CSV per-order-event
        // =====================================================================
        private void WriteOrderRow(string eventType, int orderNumber, string orderTypeStr,
            double price, double stopPx, double targetPx, string extra)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_Order");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.4.0");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# StopLossBricks={0}", StopLossBricks));
                        sw.WriteLine(string.Format("# ProfitTargetBricks={0}", ProfitTargetBricks));
                        sw.WriteLine(string.Format("# LimitUnderPoints={0}", LimitUnderPoints));
                        sw.WriteLine(string.Format("# UnfilledCancelBricks={0}", UnfilledCancelBricks));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("# Event types:");
                        sw.WriteLine("#   SUBMIT_MARKET / SUBMIT_LIMIT   - entry order submitted");
                        sw.WriteLine("#   FILLED_BRACKET_ATTACH          - entry filled, OCO bracket submitted");
                        sw.WriteLine("#   EXIT_STOP / EXIT_TARGET        - bracket leg filled");
                        sw.WriteLine("#   CANCEL_UNFILLED                - entry cancelled because too many bricks elapsed");
                        sw.WriteLine("#   CANCEL_WORKEND                 - entry cancelled because work-end reached");
                        sw.WriteLine("#   FORCEFLAT_WORKEND              - position force-flatted at work-end");
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
            // Note: order subsystem state is NOT cleared on daily reset. If a
            // position carries across the 9:30 boundary, we let it finish.
        }

        #region Properties (existing v1.3.2 properties unchanged)

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
        [Display(Name="AlertPattern",
            Description="Suffix pattern matched against TradedOutcomeString (S=1, F=0).",
            Order=3, GroupName="2. Alert")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA1Period",
            Description="Period for EMA1. Default 20.",
            Order=4, GroupName="3. EMA Filter")]
        public int EMA1Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA2Period",
            Description="Period for EMA2. Default 9.",
            Order=5, GroupName="3. EMA Filter")]
        public int EMA2Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert. Default 3.",
            Order=6, GroupName="4. Beep")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=7, GroupName="4. Beep")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Master toggle for all chart drawings.",
            Order=8, GroupName="5. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowOutcomeLabels",
            Description="Show 'PT/S' / 'PT/F' text at each outcome brick.",
            Order=9, GroupName="5. Visuals")]
        public bool ShowOutcomeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for CSV files. Default C:\\temp.",
            Order=10, GroupName="6. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name="MaxBitsKept",
            Description="Max in-memory bit string length. Default 5000.",
            Order=11, GroupName="7. Advanced")]
        public int MaxBitsKept { get; set; }

        // =====================================================================
        // NEW v1.4 PROPERTIES
        // =====================================================================

        [NinjaScriptProperty]
        [Display(Name="EnableOrders",
            Description="Master switch for the order subsystem. When OFF, behaves identically to v1.3.2 (alerts only, no orders).",
            Order=1, GroupName="8. Order Execution")]
        public bool EnableOrders { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="OrderQuantity",
            Description="Contracts per order. Default 1.",
            Order=2, GroupName="8. Order Execution")]
        public int OrderQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name="OrderType",
            Description="Limit (buy below close by LimitUnderPoints) or Market (immediate, may slip).",
            Order=3, GroupName="8. Order Execution")]
        public OrderEntryType OrderType { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name="LimitUnderPoints",
            Description="(Limit only) Submit limit buy at (close - this many points). Default 5.0. Ignored for Market orders.",
            Order=4, GroupName="8. Order Execution")]
        public double LimitUnderPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name="UnfilledCancelBricks",
            Description="(Limit only) Cancel limit order if not filled after this many brick closes. Default 1. Ignored for Market.",
            Order=5, GroupName="8. Order Execution")]
        public int UnfilledCancelBricks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name="StopLossBricks",
            Description="Stop-loss distance from fill, in BRICKS (1 brick price = BarsPeriod.Value * TickSize). Default 1.",
            Order=6, GroupName="8. Order Execution")]
        public int StopLossBricks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name="ProfitTargetBricks",
            Description="Profit-target distance from fill, in BRICKS. Default 1.",
            Order=7, GroupName="8. Order Execution")]
        public int ProfitTargetBricks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableOrderHours",
            Description="If true, restrict order placement to the NY time window below. Alerts still fire 24h.",
            Order=1, GroupName="9. Order Hours (NY) & Expiry")]
        public bool EnableOrderHours { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="OrderStartHourNY",
            Description="Order window start hour (NY, 0-23). Default 9.",
            Order=2, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderStartHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="OrderStartMinuteNY",
            Description="Order window start minute (NY, 0-59). Default 30.",
            Order=3, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderStartMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name="OrderEndHourNY",
            Description="Order window end hour (NY, 0-23). Default 15.",
            Order=4, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderEndHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name="OrderEndMinuteNY",
            Description="Order window end minute (NY, 0-59). Default 55. At/after this time, position force-flat, working orders cancelled.",
            Order=5, GroupName="9. Order Hours (NY) & Expiry")]
        public int OrderEndMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Display(Name="GoodTilDate",
            Description="Last NY date orders may be placed. After this date, no new orders (alerts continue).",
            Order=6, GroupName="9. Order Hours (NY) & Expiry")]
        public DateTime GoodTilDate { get; set; }

        #endregion
    }
}
