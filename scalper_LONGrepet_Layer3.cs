#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// scalper_LONGrepeat_Layer3  v3  (reconnect-survival + file-per-session + EOD)
//
// ============================================================================
// WHAT CHANGED IN v3  (vs v2) — SAME design as Layer2 v4
// ============================================================================
//  "The file boundary IS the pipeline-continuity boundary."
//  On startup the strategy reads its OWN most-recent log and decides, from the
//  gap since the last recorded bit, to RESUME (small gap / maintenance break)
//  or FRESH-start (real outage / weekend / over ceiling).
//
//    Decision rule (in order):
//      no log / empty                         -> FRESH
//      a weekend (Sat/Sun) falls in the gap   -> FRESH
//      wall-clock gap > GapCeilingHours (4h)  -> FRESH
//      MARKET-OPEN minutes in gap > GapToleranceMinutes (5) -> FRESH
//      otherwise                              -> RESUME
//
//    RESUME restores rawString, filter1Outcome, filter2Outcome, realTradeOutcome
//    from the last log row, re-derives the pipeline flags, and restores
//    realLossesInARow so the breaker survives the reconnect. Same file kept.
//    FRESH opens a new timestamped file with an empty pipeline; trades naturally
//    when it re-arms (no special warm-up gate).
//
//    Market-open minutes use SessionIterator.IsInSession (reads the data series
//    Trading Hours template), so the ~1h maintenance break = 0 open-minutes and
//    is always crossed; a real mid-session outage is caught.
//
//  REMOVED: StartMode, RawStringFilePath, MaxFileAgeMinutes, LoadAndReplayRawString.
//    The strategy's OWN log is the single source of truth.
//
//  EOD: with IsExitOnSessionCloseStrategy=true, an exit that flattens the
//    position but is NOT a recognized stop/target/force-close is treated as an
//    EOD/session-close flatten -> recorded as a LOSS ('0') in BOTH rawString and
//    realTradeOutcome (conservative; we don't know the true fill outcome).
//
//  STRATEGY TAB CANNOT TELL FRESH vs RESUME: read the log; a clear
//    [FRESH START] / [RESUME] line is written at every startup.
//
// ============================================================================
// PIPELINE (unchanged — 3 layers, matches Python trade_filter.py):
//   1. append bit to rawString
//   2. rawString tail matches Filter1 -> next bit feeds filter1Outcome
//   3. filter1Outcome tail matches Filter2 -> next f1-digit feeds filter2Outcome
//   4. filter2Outcome tail matches Filter3 -> isArmed
//   5. isArmed AND waitingForF2Outcome AND waitingForF1Outcome -> next slice money
//   TARGET = 1 = price UP (LONG).
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    // #####################################################################
    // ## READ ME — HOW TO INTERPRET THE LOG (esp. if verifying in Python) ##
    // #####################################################################
    //
    // 1) rawString RESETS EVERY TRADING DAY (3:00 PM PT -> 3:00 PM PT).
    //    The research/backtest starts each trading day with an EMPTY pipeline,
    //    warms up through the overnight bits, and fires in the morning window.
    //    This strategy does the same, so live reproduces the backtest.
    //    => In the CSV rawString grows through the session then STARTS OVER at
    //       ~15:00 PT. That is CORRECT, not a bug.
    //    => If you copy a rawString out of this log to re-check a filter in
    //       Python, use bits from ONE trading day only. Do NOT concatenate
    //       across the 15:00 PT boundary.
    //
    // 2) slice_num is PER STRATEGY INSTANCE, not global.
    //    NinjaTrader creates a NEW instance on every disable/re-enable —
    //    including its own automatic restarts after a lost price connection.
    //    Each new instance restarts slice_num at 1 while rawString is RESUMED
    //    from the log, so slice_num WILL jump back to 1 mid-file. Expected.
    //    Order events by TIMESTAMP, not slice_num.
    //
    // 3) THIS STRATEGY'S rawString WILL NOT MATCH THE STANDALONE RECORDER'S
    //    BIT STRING, BIT FOR BIT — AND THAT IS FINE.
    //    Both slice with identical logic, but they are INDEPENDENT slicers with
    //    independent 1-second throttle phases, so their slice boundaries (and
    //    therefore their bits) differ. Each string is internally consistent;
    //    neither is "wrong". Do not try to reconcile them slice-by-slice.
    //
    // 4) The "side" column tells you WHY a slice did not trade:
    //       Short / Long        -> a REAL order was placed
    //       FAKE_Short/_Long    -> ordinary observation slice (filter not armed)
    //       OBS_OUTSIDE_HOURS   -> armed, but outside the trading window
    //       OBS_ACCOUNT_BUSY    -> armed, but another strategy/manual held it
    //       OBS_QTY_SKIP        -> armed, but the qty rule returned 0
    //       WOULDBE_TRADE       -> filter ARMED, but EnableRealOrder=false
    //                              (observation mode: this is the trade it WOULD
    //                               have taken — use these to forward-log a book
    //                               you are not trading live yet)
    //    The OBS_* rows are trades the strategy WOULD have taken but did not.
    //
    // 5) Resets per trading day: rawString, filter1Outcome, filter2Outcome, isArmed,
    //    waiting flags, sessionRealOutcome (qty history).
    //    Does NOT reset: realTradeOutcome (audit), realLossesInARow (breaker).
    //
    // #####################################################################

    public class scalper_LONGrepeat_Layer3 : Strategy
    {
        // ── strategy lifecycle ────────────────────────────────────────────────
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private DateTime lastCheckTime = DateTime.MinValue;
        private int      sliceCount    = 0;

        // ── pipeline strings ─────────────────────────────────────────────────
        private StringBuilder rawString        = new StringBuilder();
        private StringBuilder filter1Outcome   = new StringBuilder();
        private StringBuilder filter2Outcome   = new StringBuilder();
        private StringBuilder realTradeOutcome = new StringBuilder();

        // ── pipeline state ───────────────────────────────────────────────────
        private bool isArmed             = false;
        private bool waitingForF1Outcome = false;
        private bool waitingForF2Outcome = false;
        private bool nextIsMoney         = false;

        // ── slice state ──────────────────────────────────────────────────────
        private bool   inSlice         = false;
        private bool   isMoneySlice    = false;
        private double sliceEntryPrice = 0.0;
        private double sliceStopPrice  = 0.0;
        private double sliceTargetPrice= 0.0;

        // ── real order state ─────────────────────────────────────────────────
        private bool   entryInFlight     = false;
        private bool   awaitingClose     = false;
        private Order  workingEntryOrder = null;
        private double entryFillPrice    = 0.0;
        private int    entryFillQty      = 0;

        // ── real loss streak ─────────────────────────────────────────────────
        private int realLossesInARow = 0;

        // ── session iterator (for market-open-minutes gap measure) ────────────
        private SessionIterator sessionIter = null;

        // ── active log file path (timestamped; chosen fresh or resumed) ───────
        private string activeLogFilePath = null;

        // ── qty multiplier table ──────────────────────────────────────────────
        // LONGEST-MATCHING pattern wins (order-independent; see CalcQty). Matched
        // against the tail of the PER-DAY session real outcome (sessionRealOutcome),
        // so the qty rule resets every trading day. NO wildcards here.
        //   Every pattern starts with '1' (a WIN) — bug-guard: losses-from-open
        //   (e.g. "0000..." from a bug) match nothing -> stay at base qty, never
        //   double into a disaster. Hard stop = MaxRealLossInARow.
        //   "10","100","1000" -> x2 ; "10000","100000","1000000" -> 0 (SKIP trade).
        //   qty 0 == DO NOT place a real order (observe only). See CalcQty caller.
        // *** COUPLING WARNING: review table when MaxRealLossInARow changes.
        //   L3 filter params are NOT researched yet; table carried over from L2. ***
        // NOTE: default below is overwritten at startup by ParseQtyRule() from the
        // UI-editable QtyRuleText parameter (no recompile needed to change it).
        private (string pattern, int multiplier)[] qtyTable =
            new (string pattern, int multiplier)[] {
                ("10", 2), ("100", 2), ("1000", 2),
                ("10000", 0), ("100000", 0), ("1000000", 0) };

        private int currentQty = 1;

        // Why was a would-be money slice demoted to an observation slice?
        // Written into the log's "side" column so the CSV can distinguish:
        //   null                  -> ordinary fake slice (filter simply not armed)
        //   "OBS_OUTSIDE_HOURS"   -> armed, but outside the trading window
        //   "OBS_ACCOUNT_BUSY"    -> armed, but another strategy/manual held the instrument
        //   "OBS_QTY_SKIP"        -> armed, but the qty rule returned 0
        // This matters for the forward-log: it tells you which trades the strategy
        // WOULD have taken but did not, which is otherwise unmeasurable.
        private string suppressReason = null;

        // ── per-day (session) real outcome, drives the qty rule; resets daily ──
        private StringBuilder sessionRealOutcome = new StringBuilder();
        private int sessionDayKey = -1;          // yyyymmdd (trading day) of the qty session
        private int currentTradingDayKey = -1;   // trading day the pipeline belongs to

        // ── shutdown ─────────────────────────────────────────────────────────
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "LR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "LONG scalper v3 Layer3. Reconnect-survival: reloads its own log "
                            + "and RESUMES the 3-layer pipeline across reconnect / maintenance break, "
                            + "or FRESH-starts a new file when the gap is too big. Target=1=price UP.";
                Name        = "scalper_LONGrepeat_Layer3";

                Calculate                    = Calculate.OnEachTick;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;   // EOD flatten ON (see reminder)
                ExitOnSessionCloseSeconds    = 30;
                IsFillLimitOnTouch           = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade          = 0;
                IsUnmanaged                  = false;

                // ── defaults ──────────────────────────────────────────────────
                EnableTradingHours   = false;
                TradingStartHour     = 9;
                TradingStartMinute   = 30;
                TradingEndHour       = 16;
                TradingEndMinute     = 0;
                StrategyLifeMinutes  = 1440;   // 24h; lifetime no longer the main control
                CheckIntervalSeconds = 1;
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 20;
                ProfitTargetPoints   = 20;
                EnableTrailingStop   = false;
                TrailDistancePoints  = 10;
                EnableRealOrder      = false;
                Filter1Pattern       = "011";
                Filter2Pattern       = "01";
                Filter3Pattern       = "01";
                BaseQuantity         = 1;
                EnableQtyIncrement   = false;
                QtyRuleText          = "(\"10\":2),(\"100\":2),(\"1000\":2),(\"10000\":0),(\"100000\":0),(\"1000000\":0)";
                EnableTradeOutcomeExit  = false;
                TradeOutcomeExitPattern = "1111111";
                MaxTotalSliceCount   = 100000;
                MaxRealLossInARow    = 3;

                LogFolder            = @"C:\temp";
                LogBaseName          = "scalper_LONGrepeat_Layer3";

                GapToleranceMinutes  = 7;
                GapCeilingHours      = 4;
            }
            else if (State == State.Configure)
            {
                if (EnableTrailingStop)
                    SetTrailStop  (ENTRY_SIGNAL, CalculationMode.Ticks,
                        (int)Math.Round(TrailDistancePoints / TickSize), false);
                else
                    SetStopLoss   (ENTRY_SIGNAL, CalculationMode.Ticks,
                        (int)Math.Round(StopLossPoints / TickSize), false);

                SetProfitTarget(ENTRY_SIGNAL, CalculationMode.Ticks,
                    (int)Math.Round(ProfitTargetPoints / TickSize));
            }
            else if (State == State.DataLoaded)
            {
                if (BarsArray != null && BarsArray.Length > 0)
                    sessionIter = new SessionIterator(BarsArray[0]);
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    strategyStartUtc = DateTime.UtcNow;
                    lifeStarted      = true;

                    if (sessionIter == null && BarsArray != null && BarsArray.Length > 0)
                        sessionIter = new SessionIterator(BarsArray[0]);

                    StartupDecideAndLoad();
                    ParseQtyRule();   // after log path is set, so [QTY RULE] logs to the right file
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;
            if (!lifeStarted) return;

            if (disabledSelf || pendingFlatten)
            {
                ProcessShutdown();
                return;
            }

            if (inSlice && !isMoneySlice)
            {
                CheckFakeSlice();
                return;
            }

            if (inSlice && isMoneySlice)
                return;

            DateTime nowUtc = DateTime.UtcNow;

            if ((nowUtc - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached");
                return;
            }

            if (sliceCount >= MaxTotalSliceCount)
            {
                BeginShutdown("MaxTotalSliceCount (" + MaxTotalSliceCount + ") reached");
                return;
            }

            if (realLossesInARow >= MaxRealLossInARow)
            {
                BeginShutdown("MaxRealLossInARow (" + MaxRealLossInARow
                    + ") reached. realLossesInARow=" + realLossesInARow);
                return;
            }

            if ((DateTime.Now - lastCheckTime).TotalSeconds < CheckIntervalSeconds)
                return;
            lastCheckTime = DateTime.Now;

            // Reset the pipeline at the 3:00 PM PT trading-day boundary (matches the
            // research). Checked BEFORE a new slice starts, so a slice straddling the
            // boundary completes and is attributed to the day it ENTERED in.
            CheckTradingDayRollover();

            // *** DO NOT GATE SLICING ON TRADING HOURS ***
            // Slices must run 24h so the pipeline gets its OVERNIGHT WARM-UP.
            // The research/backtest feeds the filter the FULL day sequence
            // (overnight + morning) and only COUNTS fires in the trading window.
            // If we skipped slicing outside the window, rawString would start
            // empty at 09:30 ET with no warm-up, the filter would arm differently,
            // and the live strategy would NOT reproduce the backtest.
            //
            // The trading-hours check now lives in StartNextSlice(), where it
            // demotes a money slice to an OBSERVATION slice (records the bit,
            // places no order) — same mechanism as [ACCOUNT BUSY] / [QTY SKIP].

            if (!ReadyForNewSlice())
                return;

            StartNextSlice();
        }

        // =====================================================================
        // STARTUP: decide FRESH vs RESUME, set active log file, restore/init.
        // =====================================================================
        private void StartupDecideAndLoad()
        {
            isArmed             = false;
            waitingForF1Outcome = false;
            waitingForF2Outcome = false;
            nextIsMoney         = false;
            realLossesInARow    = 0;
            currentQty          = BaseQuantity;
            rawString.Clear();
            filter1Outcome.Clear();
            filter2Outcome.Clear();
            realTradeOutcome.Clear();

            string latest = FindMostRecentLogFile();

            bool   doFresh = true;
            string reason  = "no prior log file";
            DateTime lastBitLocal = DateTime.MinValue;

            if (!string.IsNullOrEmpty(latest))
            {
                PipelineSnapshot snap = ReadLastSnapshot(latest);
                if (snap == null || !snap.valid)
                {
                    doFresh = true;
                    reason  = "prior log unreadable/empty";
                }
                else
                {
                    lastBitLocal = snap.lastBitLocal;
                    GapDecision gd = DecideGap(snap.lastBitLocal, DateTime.Now);
                    if (gd.fresh)
                    {
                        doFresh = true;
                        reason  = gd.reason;
                    }
                    else
                    {
                        doFresh = false;
                        rawString.Append(snap.rawString);
                        filter1Outcome.Append(snap.filter1Outcome);
                        filter2Outcome.Append(snap.filter2Outcome);
                        realTradeOutcome.Append(snap.realTradeOutcome);
                        // BREAKER RESTORE (day-aware): count only TODAY's trailing real
                        // losses. realTradeOutcome is cumulative and spans days, so using it
                        // directly would drag yesterday's losses in and re-trip the breaker
                        // on every restart.
                        realLossesInARow = CountTodaysTrailingLosses(latest, CurrentTradingDayKey());
                        ReDerivePipelineFlags();
                        activeLogFilePath = latest;
                        reason = gd.reason;

                        // Per-day qty session starts FRESH on RESUME (see Layer 2).
                        // If the restored snapshot belongs to a PREVIOUS trading day,
                        // the pipeline must start EMPTY for the new day (research resets
                        // every trading day). Otherwise we drag yesterday's bits into today.
                        int snapKey = TradingDayKeyOfLocal(snap.lastBitLocal);
                        int nowKey  = CurrentTradingDayKey();
                        if (snapKey > 0 && nowKey > 0 && snapKey != nowKey)
                        {
                            DiagLog(string.Format(
                                "[RESUME ACROSS DAY BOUNDARY] snapshot is trading day {0}, now {1}. "
                                + "Discarding restored pipeline; new day starts EMPTY (matches research).",
                                snapKey, nowKey));
                            rawString.Clear();
                            filter1Outcome.Clear();
                            filter2Outcome.Clear();
                            isArmed             = false;
                            waitingForF1Outcome = false;
                            waitingForF2Outcome = false;
                            nextIsMoney         = false;
                        }
                        currentTradingDayKey = nowKey;

                        // Per-day qty session: on RESUME, REBUILD today's qty history
                        // from the log (same source the breaker uses) instead of
                        // clearing it -> the qty rule survives disable/enable & reconnect.
                        // New day / no real trades today -> "" -> clean start.
                        sessionRealOutcome.Clear();
                        sessionRealOutcome.Append(ReadTodaysRealOutcomes(latest, nowKey));
                        sessionDayKey = nowKey;
                        DiagLog("[QTY RESUME] rebuilt today's qty history from log: sessionReal='"
                            + sessionRealOutcome.ToString() + "'");
                    }
                }
            }

            if (doFresh)
            {
                activeLogFilePath = BuildNewLogFilePath();
                EnsureLogHeader(activeLogFilePath);
                DiagLog("[FRESH START] " + reason
                    + " | new log file = " + activeLogFilePath
                    + " | pipeline EMPTY, will arm naturally (no real trades until armed)."
                    + " NOTE: strategy tab shows 'enabled' even though this is a fresh "
                    + "instance — this log line is the only way to know.");
            }
            else
            {
                DiagLog("[RESUME] " + reason
                    + " | continuing log file = " + activeLogFilePath
                    + " | restored rawString.len=" + rawString.Length
                    + " filter1Outcome.len=" + filter1Outcome.Length
                    + " filter2Outcome.len=" + filter2Outcome.Length
                    + " realTradeOutcome=" + realTradeOutcome.ToString()
                    + " realLossesInARow=" + realLossesInARow
                    + " isArmed=" + isArmed
                    + " waitingForF1Outcome=" + waitingForF1Outcome
                    + " waitingForF2Outcome=" + waitingForF2Outcome
                    + " nextIsMoney=" + nextIsMoney
                    + " | last bit was " + lastBitLocal.ToString("yyyy-MM-dd HH:mm:ss")
                    + ". Breaker intact across reconnect.");
            }

            DiagLog("[NOTE] rawString RESETS at 3:00 PM PT each trading day (matches research). "
                  + "slice_num restarts at 1 on every strategy instance. "
                  + "This string will NOT match the standalone recorder bit-for-bit "
                  + "(independent slicers / throttle phase) — that is expected.");
            DiagLog(Name + " ready (LONG). EnableRealOrder=" + EnableRealOrder
                + ", Filter1=[" + Filter1Pattern + "], Filter2=[" + Filter2Pattern + "], Filter3=[" + Filter3Pattern + "]"
                + ", MaxRealLossInARow=" + MaxRealLossInARow
                + ", Stop=" + StopLossPoints + "pt, Target=" + ProfitTargetPoints + "pt"
                + ", GapTolerance=" + GapToleranceMinutes + "min, GapCeiling=" + GapCeilingHours + "h");
        }

        // ── gap decision (identical to Layer 2) ────────────────────────────────
        private class GapDecision { public bool fresh; public string reason; }

        private GapDecision DecideGap(DateTime lastBitLocal, DateTime nowLocal)
        {
            var d = new GapDecision();

            if (WeekendInGap(lastBitLocal, nowLocal))
            {
                d.fresh = true;
                d.reason = "weekend fell within the gap (Fri pipeline not continuous with reopen)";
                return d;
            }

            double wallHours = (nowLocal - lastBitLocal).TotalHours;
            if (wallHours > GapCeilingHours)
            {
                d.fresh = true;
                d.reason = "wall-clock gap " + wallHours.ToString("F1")
                         + "h exceeds ceiling " + GapCeilingHours + "h";
                return d;
            }

            int openMin = MarketOpenMinutesInGap(lastBitLocal, nowLocal);
            if (openMin > GapToleranceMinutes)
            {
                d.fresh = true;
                d.reason = "market-open minutes in gap = " + openMin
                         + " > tolerance " + GapToleranceMinutes + "min (real hole in string)";
                return d;
            }

            d.fresh = false;
            d.reason = "gap small: " + openMin + " market-open min (<= " + GapToleranceMinutes
                     + "min), wall-clock " + wallHours.ToString("F2") + "h, no weekend";
            return d;
        }

        private bool WeekendInGap(DateTime a, DateTime b)
        {
            if (b <= a) return false;
            DateTime cur = a.Date;
            while (cur <= b.Date)
            {
                if (cur.DayOfWeek == DayOfWeek.Saturday || cur.DayOfWeek == DayOfWeek.Sunday)
                    return true;
                cur = cur.AddDays(1);
            }
            return false;
        }

        private int MarketOpenMinutesInGap(DateTime a, DateTime b)
        {
            try
            {
                if (sessionIter == null || b <= a) return 0;
                int openCount = 0;
                DateTime t = a;
                int safety = GapCeilingHours * 60 + 5;
                while (t < b && safety-- > 0)
                {
                    if (IsMarketOpenAt(t))
                        openCount++;
                    t = t.AddMinutes(1);
                }
                return openCount;
            }
            catch (Exception ex)
            {
                DiagLog("MarketOpenMinutesInGap error: " + ex.Message
                    + " -> treating as OPEN (conservative -> fresh).");
                return GapToleranceMinutes + 9999;
            }
        }

        private bool IsMarketOpenAt(DateTime localTime)
        {
            try
            {
                if (sessionIter == null) return true;
                return sessionIter.IsInSession(localTime, true, true);
            }
            catch
            {
                return true;
            }
        }

        // =====================================================================
        // CalcQty (unchanged)
        // =====================================================================
        // Parse QtyRuleText into qtyTable. Lenient: strips ( ) " then scans for every
        // "<pattern>:<qty>" pair (pattern = run of 0/1, qty >= 0) and ignores the rest.
        // Warns on duplicate-length patterns (longest-match keeps the first at a length).
        // On any failure keeps the built-in default and logs.
        private void ParseQtyRule()
        {
            try
            {
                string cleaned = (QtyRuleText ?? "").Replace("(", "").Replace(")", "").Replace("\"", "");
                var list = new System.Collections.Generic.List<(string pattern, int multiplier)>();
                foreach (System.Text.RegularExpressions.Match mm in
                         System.Text.RegularExpressions.Regex.Matches(cleaned, @"([01]+)\s*:\s*(\d+)"))
                {
                    string pat = mm.Groups[1].Value;
                    int    q   = int.Parse(mm.Groups[2].Value,
                                     System.Globalization.CultureInfo.InvariantCulture);
                    list.Add((pat, q));
                }
                if (list.Count > 0)
                {
                    var seen = new System.Collections.Generic.HashSet<int>();
                    foreach (var e in list)
                        if (!seen.Add(e.pattern.Length))
                            DiagLog("[QTY RULE] WARNING duplicate-length pattern '" + e.pattern
                                + "' -> shadowed by an earlier same-length rule.");
                    qtyTable = list.ToArray();
                    DiagLog("[QTY RULE] parsed " + list.Count + " rule(s) from '" + QtyRuleText + "'");
                }
                else DiagLog("[QTY RULE] no valid pairs in '" + QtyRuleText + "' -> keeping default.");
            }
            catch (Exception ex)
            {
                DiagLog("[QTY RULE] parse error: " + ex.Message + " -> keeping default.");
            }
        }

        // Trade-outcome exit: HALT the session (same shutdown as the breaker -> manual
        // re-enable) when the REAL trade-outcome tail (sessionRealOutcome; 1=win,0=loss)
        // ends with TradeOutcomeExitPattern (plain endsWith, NO wildcard). Called ONLY on
        // a CLEAN stop/target resolution -- not on forced-close or EOD paths. Slice
        // default pattern "1111111" + disabled = effectively inert.
        private void CheckTradeOutcomeExit()
        {
            if (EnableTradeOutcomeExit
                && !string.IsNullOrEmpty(TradeOutcomeExitPattern)
                && sessionRealOutcome.ToString().EndsWith(TradeOutcomeExitPattern))
            {
                DiagLog("[OUTCOME EXIT] real-outcome tail matched '" + TradeOutcomeExitPattern
                    + "' -> halting session. session=" + sessionRealOutcome.ToString());
                BeginShutdown("trade-outcome exit '" + TradeOutcomeExitPattern + "' matched");
            }
        }

        private int CalcQty()
        {
            if (!EnableQtyIncrement) return BaseQuantity;

            string outcome = sessionRealOutcome.ToString();
            if (outcome.Length == 0) return BaseQuantity;

            int  bestLen  = -1;
            int  bestMult = 1;
            bool matched  = false;
            foreach (var entry in qtyTable)
            {
                if (entry.pattern.Length > bestLen && TailMatches(outcome, entry.pattern))
                {
                    bestLen  = entry.pattern.Length;
                    bestMult = entry.multiplier;
                    matched  = true;
                }
            }

            if (!matched) return BaseQuantity;

            int qty = BaseQuantity * bestMult;   // bestMult may be 0 -> skip
            DiagLog(string.Format("[QTY] longest match len={0} -> x{1} -> qty={2} (sessionReal={3})",
                bestLen, bestMult, qty, outcome));
            return qty;
        }

        // =====================================================================
        // TRADING-DAY KEY  (3:00 PM Pacific -> 3:00 PM Pacific)
        // =====================================================================
        // MUST match the research slicer exactly:
        //     trading_day(t) = date(t) if t >= 15:00 PT, else date(t) - 1
        // Everything that "resets per trading day" (the filter pipeline AND the qty
        // session) rolls on THIS boundary, so live reproduces the backtest.
        private TimeZoneInfo PacificZone()
        {
            try   { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch { try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { return null; } }
        }

        private int TradingDayKeyOfLocal(DateTime localTime)
        {
            try
            {
                TimeZoneInfo pt = PacificZone();
                DateTime ptTime;
                if (pt == null) { ptTime = localTime; }
                else
                {
                    DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), TimeZoneInfo.Local);
                    ptTime = TimeZoneInfo.ConvertTimeFromUtc(utc, pt);
                }
                DateTime d = (ptTime.TimeOfDay >= new TimeSpan(15, 0, 0))
                             ? ptTime.Date : ptTime.Date.AddDays(-1);
                return d.Year * 10000 + d.Month * 100 + d.Day;
            }
            catch { return -1; }
        }

        private int CurrentTradingDayKey() { return TradingDayKeyOfLocal(DateTime.Now); }

        // =====================================================================
        // CheckTradingDayRollover — RESET THE PIPELINE AT 3:00 PM PT
        // =====================================================================
        // The research starts EVERY trading day with an EMPTY pipeline, warms up
        // through the overnight bits, and fires in the morning window. Every
        // published number was produced that way. If the live strategy carried
        // yesterday's bits into today it would arm differently and would NOT
        // reproduce the backtest — with no visible symptom. So we clear here.
        //
        // RESETS: rawString, filter1Outcome, filter2Outcome, isArmed, waiting flags, nextIsMoney,
        //         sessionRealOutcome (qty history)
        // KEPT  : realTradeOutcome (audit trail), realLossesInARow (safety breaker)
        private void CheckTradingDayRollover()
        {
            int key = CurrentTradingDayKey();
            if (key < 0) return;

            if (currentTradingDayKey == -1) { currentTradingDayKey = key; return; }

            if (key != currentTradingDayKey)
            {
                DiagLog(string.Format(
                    "[TRADING DAY ROLLOVER] {0} -> {1} (3:00 PM PT boundary). "
                    + "Clearing pipeline to match the research. prev raw({2}). "
                    + "realTradeOutcome and realLossesInARow are KEPT.",
                    currentTradingDayKey, key, rawString.Length));

                rawString.Clear();
                filter1Outcome.Clear();
                filter2Outcome.Clear();
                isArmed             = false;
                waitingForF1Outcome = false;
                waitingForF2Outcome = false;
                nextIsMoney         = false;

                sessionRealOutcome.Clear();
                sessionDayKey = key;

                // DAILY CIRCUIT BREAKER: a new trading day is a brand-new day for
                // EVERYTHING except realTradeOutcome (the cumulative audit trail).
                // If we blew through MaxRealLossInARow yesterday, that is over — today
                // starts clean. Without this the streak would carry forever and, on
                // restart, the breaker would re-trip instantly, bricking the strategy.
                if (realLossesInARow > 0)
                    DiagLog("[BREAKER RESET] new trading day -> realLossesInARow "
                          + realLossesInARow + " -> 0");
                realLossesInARow = 0;

                currentTradingDayKey = key;
            }
        }

        private void RecordSessionOutcome(int bit)
        {
            int key = CurrentTradingDayKey();   // same 3PM-PT boundary as the pipeline
            if (key != sessionDayKey)
            {
                if (sessionDayKey != -1)
                    DiagLog(string.Format("[QTY SESSION ROLL] new trading day {0} (was {1}) -> qty session reset. "
                        + "prev sessionReal={2}", key, sessionDayKey, sessionRealOutcome.ToString()));
                sessionDayKey = key;
                sessionRealOutcome.Clear();
            }
            sessionRealOutcome.Append(bit.ToString());
        }

        // =====================================================================
        // StartNextSlice (unchanged logic)
        // =====================================================================
        private void StartNextSlice()
        {
            bool startMoney = nextIsMoney;
            nextIsMoney = false;

            sliceCount++;
            double refPrice = GetCurrentBid();
            if (refPrice <= 0) { sliceCount--; return; }

            if (!UseMarketEntry)
                refPrice = Instrument.MasterInstrument.RoundToTickSize(refPrice - LimitOffsetPoints);

            sliceEntryPrice  = refPrice;
            sliceStopPrice   = Instrument.MasterInstrument.RoundToTickSize(sliceEntryPrice - StopLossPoints);
            sliceTargetPrice = Instrument.MasterInstrument.RoundToTickSize(sliceEntryPrice + ProfitTargetPoints);
            inSlice          = true;
            isMoneySlice     = startMoney && EnableRealOrder;
            suppressReason   = null;   // reset; set by the guards below if demoted

            // OBSERVATION MODE: when EnableRealOrder=false, isMoneySlice is always false,
            // so none of the guards below fire and every row would log as a plain FAKE_*.
            // That would hide the whole point of observation mode — WHICH SLICES THE
            // FILTER ARMED FOR. 'startMoney' holds that, so mark it explicitly.
            if (startMoney && !EnableRealOrder)
            {
                suppressReason = "WOULDBE_TRADE";
                DiagLog(string.Format(
                    "[WOULD-BE TRADE] Slice #{0} the filter ARMED and this WOULD have been a "
                    + "real order, but EnableRealOrder=false. Bit recorded; no order placed.",
                    sliceCount));
            }

            // ── GUARD 1: TRADING HOURS (order-only gate) ───────────────────────
            // Outside the trading window we still SLICE and RECORD the bit (the
            // pipeline needs 24h data for its overnight warm-up — see OnBarUpdate),
            // but we place NO order. Demote to an observation slice.
            if (isMoneySlice && EnableTradingHours && !WithinTradingHours())
            {
                DiagLog(string.Format(
                    "[OUTSIDE HOURS] Slice #{0} -> NO real order, OBSERVATION ONLY "
                    + "(outside {1:00}:{2:00}-{3:00}:{4:00} NY/Eastern). Bit still recorded.",
                    sliceCount, TradingStartHour, TradingStartMinute,
                    TradingEndHour, TradingEndMinute));
                isMoneySlice   = false;
                suppressReason = "OBS_OUTSIDE_HOURS";
            }

            // ── GUARD 2: MULTI-STRATEGY (same instrument only) ─────────────────
            // If ANY position or live order exists on THIS instrument — placed by
            // another strategy (e.g. the other direction's book), or by you manually
            // / via ATM — then we do NOT place an order. We demote this money slice
            // to an OBSERVATION slice: it still runs, still resolves, and still feeds
            // the pipeline, so the bit string stays continuous and the filters stay
            // valid. Only the ORDER is suppressed.
            //
            // This is what lets LONG and SHORT run on the same account/instrument
            // simultaneously without ever double-positioning. First to fire wins;
            // the other records the bit but sits out that trade.
            if (isMoneySlice && AccountBusyOnThisInstrument())
            {
                DiagLog(string.Format(
                    "[ACCOUNT BUSY] Slice #{0} -> NO real order, OBSERVATION ONLY "
                    + "(instrument already has a position/order from another strategy or manual). "
                    + "Bit will still be recorded.", sliceCount));
                isMoneySlice   = false;   // run as a fake/observation slice
                suppressReason = "OBS_ACCOUNT_BUSY";
            }

            // qty of 0 means the qty rule says SKIP: demote to an observation slice
            // (no real order placed) but still let it resolve & feed the pipeline.
            if (isMoneySlice)
            {
                currentQty = CalcQty();
                if (currentQty <= 0)
                {
                    DiagLog(string.Format(
                        "[QTY SKIP] Slice #{0} qty rule returned 0 -> NO real order, observe only. sessionReal={1}",
                        sliceCount, sessionRealOutcome.ToString()));
                    isMoneySlice   = false;
                    suppressReason = "OBS_QTY_SKIP";
                }
            }

            if (isMoneySlice)
            {
                awaitingClose     = true;
                entryInFlight     = true;
                workingEntryOrder = null;
                try
                {
                    if (UseMarketEntry)
                    {
                        workingEntryOrder = EnterLong(currentQty, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SLICE #{0} MARKET qty={1} entry~{2:F2} stop={3:F2} target={4:F2} | raw={5} | f1={6} | f2={7} | real={8}",
                            sliceCount, currentQty, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8), TailOf(filter2Outcome, 8), TailOf(realTradeOutcome, 8)));
                    }
                    else
                    {
                        double limitPx = Instrument.MasterInstrument.RoundToTickSize(
                            GetCurrentBid() - LimitOffsetPoints);
                        workingEntryOrder = EnterLongLimit(0, true, currentQty, limitPx, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SLICE #{0} LIMIT qty={1} limit={2:F2} | raw={3} | f1={4} | f2={5} | real={6}",
                            sliceCount, currentQty, limitPx,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8), TailOf(filter2Outcome, 8), TailOf(realTradeOutcome, 8)));
                    }
                }
                catch (Exception ex)
                {
                    DiagLog("StartNextSlice money error: " + ex.Message);
                    sliceCount--;
                    inSlice = false; isMoneySlice = false;
                    awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
                }
            }
            else
            {
                DiagLog(string.Format(
                    "FAKE SLICE #{0} entry={1:F2} stop={2:F2} target={3:F2} | isArmed={4} | rawTail={5} | f1={6} | f2={7}",
                    sliceCount, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                    isArmed, TailOf(rawString, 8),
                    TailOf(filter1Outcome, 8), TailOf(filter2Outcome, 8)));
            }
        }

        // =====================================================================
        // CheckFakeSlice (unchanged logic)
        // =====================================================================
        private void CheckFakeSlice()
        {
            try
            {
                double bid = GetCurrentBid();
                double ask = GetCurrentAsk();
                if (bid <= 0 || ask <= 0) return;

                bool stopHit   = bid <= sliceStopPrice;
                bool targetHit = ask >= sliceTargetPrice;
                if (!stopHit && !targetHit) return;

                int    bit       = stopHit ? 0 : 1;
                double exitPrice = stopHit ? sliceStopPrice : sliceTargetPrice;
                double pnl       = stopHit
                    ? -(StopLossPoints     * Instrument.MasterInstrument.PointValue)
                    : +(ProfitTargetPoints * Instrument.MasterInstrument.PointValue);

                bool wasMoneySlice = isMoneySlice;
                inSlice      = false;
                isMoneySlice = false;

                double logEntryPrice = sliceEntryPrice;
                sliceEntryPrice = 0.0; sliceStopPrice = 0.0; sliceTargetPrice = 0.0;

                if (wasMoneySlice)
                {
                    if (Position.MarketPosition == MarketPosition.Flat
                        && workingEntryOrder != null
                        && (workingEntryOrder.OrderState == OrderState.Working
                            || workingEntryOrder.OrderState == OrderState.Accepted
                            || workingEntryOrder.OrderState == OrderState.Submitted))
                    {
                        DiagLog(string.Format("[BRICK CLEANUP Case1] Slice #{0} order never filled. Cancel. No bit.", sliceCount));
                        try { CancelOrder(workingEntryOrder); } catch (Exception ex) { DiagLog("CancelOrder error: " + ex.Message); }
                        workingEntryOrder = null; entryInFlight = false; awaitingClose = false;
                        sliceCount--;
                        WriteLogRowCancelled(logEntryPrice);
                        return;
                    }

                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        DiagLog(string.Format("[BRICK CLEANUP Case2] Slice #{0} position still open. Force close. bit={1}", sliceCount, bit));
                        try { ExitLong(Math.Abs(Position.Quantity), "LR_ForceClose", ENTRY_SIGNAL); }
                        catch (Exception ex) { DiagLog("ForceClose error: " + ex.Message); }
                        awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
                        realTradeOutcome.Append(bit.ToString());
                        RecordSessionOutcome(bit);   // per-day qty session
                        if (bit == 0) { realLossesInARow++; DiagLog("[REAL LOSS forced] realLossesInARow=" + realLossesInARow); }
                        else { realLossesInARow = 0; DiagLog("[REAL WIN forced] realLossesInARow reset 0"); }
                        UpdatePipeline(bit);
                        WriteLogRow(entryFillPrice > 0 ? entryFillPrice : logEntryPrice,
                            exitPrice, pnl, bit, entryFillQty > 0 ? entryFillQty : currentQty, DateTime.Now);
                        entryFillPrice = 0.0; entryFillQty = 0;
                        return;
                    }

                    if (Position.MarketPosition == MarketPosition.Flat && !awaitingClose)
                    {
                        DiagLog(string.Format("[BRICK CLEANUP Case3] Slice #{0} already closed by bracket.", sliceCount));
                        return;
                    }
                }

                DiagLog(string.Format("FAKE SLICE #{0} {1}: entry={2:F2} exit={3:F2} pnl={4:0.00} bit={5}",
                    sliceCount, stopHit ? "LOSS" : "WIN", logEntryPrice, exitPrice, pnl, bit));

                UpdatePipeline(bit);
                WriteLogRowFake(logEntryPrice, exitPrice, pnl, bit);
            }
            catch (Exception ex)
            {
                DiagLog("CheckFakeSlice error: " + ex.Message);
                inSlice = false; isMoneySlice = false;
            }
        }

        // =====================================================================
        // UpdatePipeline (unchanged 3-layer logic)
        // =====================================================================
        private void UpdatePipeline(int bit)
        {
            rawString.Append(bit.ToString());
            string raw = rawString.ToString();

            if (waitingForF1Outcome)
            {
                waitingForF1Outcome = false;
                bool consumeF2 = waitingForF2Outcome;

                filter1Outcome.Append(bit.ToString());
                string f1str = filter1Outcome.ToString();
                DiagLog(string.Format("[F1 COLLECT] digit after F1='{0}' is '{1}' -> f1={2}", Filter1Pattern, bit, f1str));

                if (consumeF2)
                {
                    waitingForF2Outcome = false;
                    filter2Outcome.Append(bit.ToString());
                    string f2str = filter2Outcome.ToString();
                    DiagLog(string.Format("[F2 COLLECT] digit after F2='{0}' is '{1}' -> f2={2}", Filter2Pattern, bit, f2str));

                    isArmed = TailMatches(f2str, Filter3Pattern);
                    DiagLog(isArmed ? "[F3 MATCH] isArmed=true" : "[F3 NO MATCH] isArmed=false");
                }

                if (TailMatches(f1str, Filter2Pattern))
                {
                    waitingForF2Outcome = true;
                    DiagLog("[F2 MATCH] filter1Outcome tail matches Filter2 -> next f1-digit feeds filter2Outcome");
                }
            }

            if (TailMatches(raw, Filter1Pattern))
            {
                waitingForF1Outcome = true;
                DiagLog("[F1 MATCH] rawString tail matches Filter1 -> next raw bit feeds filter1Outcome");
            }

            nextIsMoney = isArmed && waitingForF2Outcome && waitingForF1Outcome;

            DiagLog(string.Format(
                "[PIPELINE] raw({0})={1} | f1({2})={3} | f2({4})={5} | waitF1={6} | waitF2={7} | isArmed={8} | nextIsMoney={9} | realLossRow={10}",
                rawString.Length, TailOf(rawString, 8),
                filter1Outcome.Length, TailOf(filter1Outcome, 8),
                filter2Outcome.Length, TailOf(filter2Outcome, 8),
                waitingForF1Outcome, waitingForF2Outcome, isArmed, nextIsMoney, realLossesInARow));
        }

        // Re-derive flags from loaded strings on RESUME (3-layer chain).
        // Each flag is "does string X currently end with pattern Y".
        private void ReDerivePipelineFlags()
        {
            string raw   = rawString.ToString();
            string f1str = filter1Outcome.ToString();
            string f2str = filter2Outcome.ToString();

            waitingForF1Outcome = TailMatches(raw, Filter1Pattern);
            waitingForF2Outcome = TailMatches(f1str, Filter2Pattern);
            isArmed             = TailMatches(f2str, Filter3Pattern);
            nextIsMoney         = isArmed && waitingForF2Outcome && waitingForF1Outcome;
        }

        // =====================================================================
        // OnExecutionUpdate — real money fills + EOD-flatten handling
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;

            string oName  = execution.Order.Name ?? "";
            bool   isFull = execution.Order.OrderState == OrderState.Filled;
            bool   isPart = execution.Order.OrderState == OrderState.PartFilled;

            if (oName == ENTRY_SIGNAL && (isFull || isPart))
            {
                if (entryFillPrice == 0.0) entryFillPrice = price;
                entryFillQty += quantity;
                DiagLog(string.Format("ENTRY {0} fill: qty={1} @ {2:F2} total={3}",
                    isFull ? "FULL" : "PARTIAL", quantity, price, entryFillQty));
                if (isFull) { entryInFlight = false; workingEntryOrder = null; }
                return;
            }

            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("StopCancelClose", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            // ── EOD / forced flatten detection ────────────────────────────────
            bool isOurForceClose = oName.IndexOf("LR_ForceClose", StringComparison.OrdinalIgnoreCase) >= 0
                                 || oName.IndexOf("LR_Flatten",    StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitFill = !(oName == ENTRY_SIGNAL);

            if (isFull && isExitFill && !isStopFill && !isTargetFill && !isOurForceClose
                && Position.MarketPosition == MarketPosition.Flat
                && awaitingClose)
            {
                int bit = 0;   // assume LOSS
                DiagLog(string.Format(
                    "[EOD FLATTEN] Slice #{0} closed by session-close/forced exit (name='{1}'). "
                    + "Recording as LOSS (bit=0) in BOTH rawString and realTradeOutcome (conservative).",
                    sliceCount, oName));

                realTradeOutcome.Append("0");
                RecordSessionOutcome(0);   // per-day qty session (EOD = conservative loss)
                realLossesInARow++;
                DiagLog("[REAL LOSS eod] realLossesInARow=" + realLossesInARow
                    + " | realTradeOutcome=" + realTradeOutcome.ToString());

                awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
                inSlice = false; isMoneySlice = false;

                double logFillPrice = entryFillPrice;
                int    logFillQty   = entryFillQty;
                entryFillPrice = 0.0; entryFillQty = 0;

                UpdatePipeline(bit);
                WriteLogRow(logFillPrice, price, 0.0, bit, logFillQty, time);
                return;
            }

            if ((isStopFill || isTargetFill) && isFull)
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    double pnl = isStopFill
                        ? -(StopLossPoints     * entryFillQty * Instrument.MasterInstrument.PointValue)
                        : +(ProfitTargetPoints * entryFillQty * Instrument.MasterInstrument.PointValue);
                    int bit = isStopFill ? 0 : 1;

                    DiagLog(string.Format("MONEY SLICE #{0} CLOSED {1}: entry={2:F2} exit={3:F2} qty={4} pnl={5:0.00} bit={6}",
                        sliceCount, isStopFill ? "STOP" : "TARGET", entryFillPrice, price, entryFillQty, pnl, bit));

                    realTradeOutcome.Append(bit.ToString());
                    RecordSessionOutcome(bit);   // per-day qty session
                    if (bit == 0) { realLossesInARow++; DiagLog("[REAL LOSS] realLossesInARow=" + realLossesInARow); }
                    else { if (realLossesInARow > 0) DiagLog("[REAL WIN] reset " + realLossesInARow + "->0"); realLossesInARow = 0; }
                    CheckTradeOutcomeExit();

                    awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
                    inSlice = false; isMoneySlice = false;

                    double logFillPrice = entryFillPrice;
                    int    logFillQty   = entryFillQty;
                    entryFillPrice = 0.0; entryFillQty = 0;

                    UpdatePipeline(bit);
                    WriteLogRow(logFillPrice, price, pnl, bit, logFillQty, time);
                }
            }
        }

        // =====================================================================
        // OnOrderUpdate (unchanged)
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;
            string oName = order.Name ?? "";

            if (oName == ENTRY_SIGNAL
                && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                DiagLog(string.Format("Entry order {0} (filled={1}). Resetting.", orderState, filled));
                if (filled == 0)
                {
                    sliceCount--;
                    entryInFlight = false; awaitingClose = false; workingEntryOrder = null;
                    entryFillPrice = 0.0; entryFillQty = 0; inSlice = false; isMoneySlice = false;
                }
                else
                {
                    entryInFlight = false; workingEntryOrder = null;
                }
                return;
            }

            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
                DiagLog(string.Format("ORDER WARN: {0} state={1} err={2} native={3}",
                    oName, orderState, error, string.IsNullOrEmpty(nativeError) ? "-" : nativeError));
        }

        // =====================================================================
        // ReadyForNewSlice  — "is THIS strategy free to start a new slice?"
        // =====================================================================
        // *** MULTI-STRATEGY CHANGE — READ THIS ***
        // This method now checks ONLY THIS STRATEGY'S OWN state. It deliberately
        // does NOT look at the account, at other strategies, or at manual trades.
        //
        // WHY: a slice must ALWAYS be allowed to start, because a slice is how we
        // record a bit. If we blocked the slice when another strategy held a
        // position, this strategy would record NOTHING for that period and its
        // rawString would develop a HOLE — which silently corrupts every filter
        // (the pipeline state is computed from an unbroken string).
        //
        // The account-level check has MOVED to StartNextSlice(), where it demotes
        // a money slice to an OBSERVATION slice (records the bit, places no order)
        // instead of skipping the slice entirely. See [ACCOUNT BUSY] there.
        //
        // NOTE: 'Position' in NinjaScript is THIS STRATEGY's position, not the
        // account's. That is exactly what we want here.
        private bool ReadyForNewSlice()
        {
            if (inSlice)       return false;   // this strategy is already in a slice
            if (awaitingClose) return false;   // this strategy's money trade is still closing
            if (entryInFlight) return false;   // this strategy has an entry order in flight
            if (Position.MarketPosition != MarketPosition.Flat) return false;  // THIS strategy holds a position

            return true;
        }

        // =====================================================================
        // AccountBusyOnThisInstrument  — "does ANYONE hold this instrument now?"
        // =====================================================================
        // *** MULTI-STRATEGY CHANGE — READ THIS ***
        // Returns TRUE if the ACCOUNT has, on THIS INSTRUMENT (same instrument
        // only — option (a)):
        //     - any open position (from ANY strategy, or a manual/ATM trade), OR
        //     - any working / accepted / submitted / part-filled order.
        //
        // When this returns TRUE, StartNextSlice() demotes the money slice to an
        // OBSERVATION slice: NO real order is placed, but the slice still runs,
        // still resolves, and still feeds the pipeline — so the bit string stays
        // continuous and the filters remain valid.
        //
        // PURPOSE: allows LONG and SHORT (and any other strategies) to run on the
        // SAME account and SAME instrument at once without ever double-positioning.
        // First strategy to fire wins the slot; the others record but do not trade.
        //
        // IMPORTANT CONSEQUENCE: whichever strategy fires first takes the trade.
        // The others will MISS that trade even if they were armed. This means live
        // fire counts will be LOWER than each strategy's standalone backtest, and
        // that interaction was never backtested. If you run a weaker book alongside
        // a stronger one, the weaker one can STEAL a slot from the stronger one.
        // Recommended: keep EnableRealOrder=false on the weaker book.
        //
        // ON ERROR: fail SAFE -> return true (treat as busy -> observation only,
        // no order). Never risk placing an order when we cannot verify the account.
        private bool AccountBusyOnThisInstrument()
        {
            try
            {
                if (Account == null) return true;   // cannot verify -> do not trade

                // 1) any open position on this instrument (any strategy / manual)?
                lock (Account.Positions)
                {
                    foreach (Position p in Account.Positions)
                    {
                        if (p.Instrument == Instrument
                            && p.MarketPosition != MarketPosition.Flat)
                        {
                            DiagLog(string.Format(
                                "[ACCOUNT BUSY] open position on {0}: {1} qty={2} (another strategy or manual).",
                                Instrument.FullName, p.MarketPosition, p.Quantity));
                            return true;
                        }
                    }
                }

                // 2) any live order on this instrument (any strategy / manual)?
                lock (Account.Orders)
                {
                    foreach (Order ord in Account.Orders)
                    {
                        if (ord.Instrument == Instrument
                            && (ord.OrderState == OrderState.Working
                                || ord.OrderState == OrderState.Accepted
                                || ord.OrderState == OrderState.Submitted
                                || ord.OrderState == OrderState.PartFilled))
                        {
                            DiagLog(string.Format(
                                "[ACCOUNT BUSY] live order on {0}: '{1}' state={2} (another strategy or manual).",
                                Instrument.FullName, ord.Name ?? "", ord.OrderState));
                            return true;
                        }
                    }
                }

                return false;   // account is clear on this instrument
            }
            catch (Exception ex)
            {
                DiagLog("[ACCOUNT BUSY] scan error: " + ex.Message
                    + " -> treating as BUSY (observation only, no order). Fail-safe.");
                return true;
            }
        }

        // =====================================================================
        // WithinTradingHours (unchanged)
        // =====================================================================
        private bool WithinTradingHours()
        {
            if (!EnableTradingHours) return true;
            TimeZoneInfo et;
            try   { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { return true; } }
            DateTime nyNow    = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et);
            int      curMin   = nyNow.Hour * 60 + nyNow.Minute;
            int      startMin = TradingStartHour * 60 + TradingStartMinute;
            int      endMin   = TradingEndHour   * 60 + TradingEndMinute;
            return curMin >= startMin && curMin <= endMin;
        }

        // =====================================================================
        // Shutdown (unchanged)
        // =====================================================================
        private void BeginShutdown(string reason)
        {
            if (disabledSelf || pendingFlatten) return;
            pendingReason  = reason;
            pendingFlatten = true;
            DiagLog(Name + " shutdown requested: " + reason
                + " | sliceCount=" + sliceCount + " | realLossesInARow=" + realLossesInARow);
        }

        private void ProcessShutdown()
        {
            if (entryInFlight && workingEntryOrder != null)
            {
                OrderState os = workingEntryOrder.OrderState;
                if (os == OrderState.Working || os == OrderState.Accepted || os == OrderState.Submitted)
                {
                    try { DiagLog("Shutdown: cancelling entry order (state=" + os + ")."); CancelOrder(workingEntryOrder); }
                    catch (Exception ex) { DiagLog("Shutdown CancelOrder error: " + ex.Message);
                        entryInFlight = false; awaitingClose = false; workingEntryOrder = null; }
                }
                return;
            }

            if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight) { FinalizeTermination(); return; }

            if (Position.MarketPosition == MarketPosition.Long)
            {
                try { ExitLong(Math.Abs(Position.Quantity), "LR_Flatten", ENTRY_SIGNAL);
                    DiagLog("Shutdown: ExitLong submitted for " + Math.Abs(Position.Quantity) + "."); }
                catch (Exception ex) { DiagLog("Shutdown ExitLong error: " + ex.Message); }
            }
        }

        private void FinalizeTermination()
        {
            if (disabledSelf) return;
            disabledSelf   = true;
            pendingFlatten = false;
            DiagLog(Name + " terminated. Reason: " + pendingReason
                + " | sliceCount=" + sliceCount + " | realLossesInARow=" + realLossesInARow
                + " | isArmed=" + isArmed + " | waitingForF1Outcome=" + waitingForF1Outcome
                + " | waitingForF2Outcome=" + waitingForF2Outcome + " | nextIsMoney=" + nextIsMoney
                + " | rawString=" + rawString.ToString()
                + " | filter1Outcome=" + filter1Outcome.ToString()
                + " | filter2Outcome=" + filter2Outcome.ToString()
                + " | realTradeOutcome=" + realTradeOutcome.ToString());
            try { SetState(State.Terminated); } catch { }
        }

        // =====================================================================
        // Helpers
        // =====================================================================
        private string TailOf(StringBuilder sb, int n)
        {
            string s = sb.ToString();
            return s.Length <= n ? s : "..." + s.Substring(s.Length - n);
        }

        // ===== WILDCARD PATTERN MATCHING (same as Layer 2) =====
        //   '*'->one-or-more 0s, '?'->one-or-more 1s, expand in place, suffix match.
        private static bool PatternHasWildcard(string pattern)
        {
            return pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
        }

        private static bool TailMatches(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (text.Length == 0) return false;
            if (!PatternHasWildcard(pattern))
                return text.Length >= pattern.Length && text.EndsWith(pattern);
            for (int start = text.Length - 1; start >= 0; start--)
            {
                if (MatchHere(text, start, pattern, 0))
                    return true;
            }
            return false;
        }

        private static bool MatchHere(string text, int ti, string pattern, int pi)
        {
            while (pi < pattern.Length)
            {
                char pc = pattern[pi];
                if (pc == '*' || pc == '?')
                {
                    char want = (pc == '*') ? '0' : '1';
                    if (ti >= text.Length || text[ti] != want) return false;
                    ti++;
                    int maxConsume = ti;
                    while (maxConsume < text.Length && text[maxConsume] == want) maxConsume++;
                    for (int consume = maxConsume; consume >= ti; consume--)
                    {
                        if (MatchHere(text, consume, pattern, pi + 1))
                            return true;
                    }
                    return false;
                }
                else
                {
                    if (ti >= text.Length || text[ti] != pc) return false;
                    ti++; pi++;
                }
            }
            return ti == text.Length;
        }

        // =====================================================================
        // CountTodaysTrailingLosses — the breaker's streak, for TODAY only
        // =====================================================================
        // WHY: realTradeOutcome is CUMULATIVE and never resets, so counting its trailing
        // zeros would reach BACK ACROSS the trading-day boundary into yesterday's losses.
        // On RESUME that restores a bogus streak and re-trips the breaker immediately —
        // bricking the strategy permanently.
        //
        // Instead we walk the LOG backwards and count consecutive losing REAL trades that
        // belong to the CURRENT trading day only. We stop at:
        //     - a winning real trade (streak broken), or
        //     - a real trade from a PREVIOUS trading day (new day = clean slate).
        // Observation rows (FAKE_* / OBS_* / CANCELLED_*) are skipped: they are not real
        // trades and must never affect the breaker.
        // =====================================================================
        // ReadTodaysRealOutcomes — the ONE shared reader of today's real trades
        // =====================================================================
        // Returns TODAY's real-trade outcome bits, oldest-first (e.g. "1101").
        // SINGLE SOURCE OF TRUTH used by BOTH the qty rule (rebuilds
        // sessionRealOutcome on RESUME) and the breaker (trailing-loss count), so
        // the two can never disagree. "Real trade" = side is exactly "Long"
        // (not FAKE_/OBS_/WOULDBE_/CANCELLED_). "Today" = same 3 PM PT trading day.
        // Returns "" on error / no real trades today (both callers treat as clean).
        private string ReadTodaysRealOutcomes(string path, int todayKey)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";

                string[] lines = File.ReadAllLines(path);
                var rev = new System.Collections.Generic.List<char>();

                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("timestamp")) continue;

                    string[] p = line.Split(',');
                    if (p.Length < 8) continue;

                    string sideCol = p[2].Trim();
                    string bitCol  = p[7].Trim();

                    if (sideCol != "Long") continue;          // real trades only
                    if (bitCol != "0" && bitCol != "1") continue;

                    DateTime ts;
                    if (!DateTime.TryParse(p[0].Trim(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out ts))
                        continue;

                    if (TradingDayKeyOfLocal(ts) != todayKey) break;

                    rev.Add(bitCol[0]);
                }

                rev.Reverse();
                return new string(rev.ToArray());
            }
            catch (Exception ex)
            {
                DiagLog("ReadTodaysRealOutcomes error: " + ex.Message + " -> returning \"\" (clean start).");
                return "";
            }
        }

        // Trailing losses in TODAY's real-outcome string (breaker), from the shared reader.
        private int CountTodaysTrailingLosses(string path, int todayKey)
        {
            string today = ReadTodaysRealOutcomes(path, todayKey);
            int streak = 0;
            for (int i = today.Length - 1; i >= 0; i--)
            {
                if (today[i] == '0') streak++;
                else break;
            }
            return streak;
        }

        private int CountTrailingLosses(string realOutcome)
        {
            int c = 0;
            for (int i = realOutcome.Length - 1; i >= 0; i--)
            {
                if (realOutcome[i] == '0') c++;
                else break;
            }
            return c;
        }

        // ── file naming / discovery ────────────────────────────────────────────
        private string BuildNewLogFilePath()
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string fname = LogBaseName + "_" + stamp + ".csv";
            return Path.Combine(LogFolder, fname);
        }

        private string FindMostRecentLogFile()
        {
            try
            {
                if (!Directory.Exists(LogFolder)) return null;
                string pattern = LogBaseName + "_*.csv";
                var files = Directory.GetFiles(LogFolder, pattern);
                if (files == null || files.Length == 0) return null;
                // EXCLUDE diag-log files (they end in "-diagLog.csv"). The diag log
                // is written on every startup/slice so its LastWriteTime is newer than
                // the data file, and it has no 12-column data rows — picking it would
                // make the reader see "unreadable/empty" and wrongly FRESH-start.
                var dataFiles = files
                    .Where(p => !Path.GetFileNameWithoutExtension(p)
                                     .EndsWith("-diagLog", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (dataFiles.Length == 0) return null;
                return dataFiles.OrderByDescending(p => File.GetLastWriteTime(p)).First();
            }
            catch (Exception ex)
            {
                DiagLog("FindMostRecentLogFile error: " + ex.Message);
                return null;
            }
        }

        // ── snapshot read (last valid data row's cumulative columns) ───────────
        private class PipelineSnapshot
        {
            public bool valid;
            public DateTime lastBitLocal;
            public string rawString = "";
            public string filter1Outcome = "";
            public string filter2Outcome = "";
            public string realTradeOutcome = "";
        }

        // Layer 3 log row format (12 columns):
        // timestamp,slice_num,side,quantity,entry_price,exit_price,realized_pnl,
        //   win_loss_bit,rawString(8),filter1Outcome(9),filter2Outcome(10),realTradeOutcome(11)
        private PipelineSnapshot ReadLastSnapshot(string path)
        {
            try
            {
                var snap = new PipelineSnapshot { valid = false };
                string[] lines = File.ReadAllLines(path);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("timestamp")) continue;
                    string[] p = line.Split(',');
                    if (p.Length < 12) continue;
                    string ts   = p[0].Trim();
                    string raw  = p[8].Trim();
                    string f1   = p[9].Trim();
                    string f2   = p[10].Trim();
                    string real = p[11].Trim();

                    DateTime tparsed;
                    if (!DateTime.TryParse(ts,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out tparsed)) continue;   // BUG FIX: we WRITE with InvariantCulture

                    snap.lastBitLocal     = tparsed;
                    snap.rawString        = raw;
                    snap.filter1Outcome   = f1;
                    snap.filter2Outcome   = f2;
                    snap.realTradeOutcome = real;
                    snap.valid            = raw.Length > 0;
                    return snap;
                }
                return snap;
            }
            catch (Exception ex)
            {
                DiagLog("ReadLastSnapshot error: " + ex.Message);
                return null;
            }
        }

        // =====================================================================
        // Logging
        // =====================================================================
        private void EnsureLogHeader(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "timestamp(machine_local_time),slice_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,rawString,filter1Outcome,filter2Outcome,realTradeOutcome\n");
                }
            }
            catch (Exception ex) { Print("Log header error: " + ex.Message); }
        }

        // =====================================================================
        // SafeAppend — concurrency-tolerant file append
        // =====================================================================
        // BUG FIX: File.AppendAllText opens the file WITHOUT sharing, so if two
        // instances of this strategy are alive at once (which HAPPENS during
        // NinjaTrader's auto-restart churn on a lost price connection), the second
        // writer throws IOException and the old code SILENTLY LOST the row while the
        // bit had already been appended to the in-memory rawString. Log and memory
        // then diverge and the next RESUME restores a WRONG pipeline state.
        private bool logWriteFailed = false;

        private void SafeAppend(string path, string text)
        {
            if (string.IsNullOrEmpty(path))
            {
                DiagLog("[LOG ERROR] no active log file path — row DROPPED: " + text.TrimEnd());
                logWriteFailed = true;
                return;
            }

            const int MAX_TRIES = 5;
            for (int attempt = 1; attempt <= MAX_TRIES; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                                                   FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.Write(text);
                    }
                    return;
                }
                catch (IOException)
                {
                    if (attempt == MAX_TRIES) break;
                    System.Threading.Thread.Sleep(20 * attempt);
                }
                catch (Exception ex)
                {
                    DiagLog("[LOG ERROR] append failed: " + ex.Message + " — row DROPPED: " + text.TrimEnd());
                    logWriteFailed = true;
                    return;
                }
            }

            DiagLog("[LOG ERROR] append failed after " + MAX_TRIES
                + " tries (file locked by another instance?) — row DROPPED: " + text.TrimEnd());
            logWriteFailed = true;
        }

        private void WriteLogRow(double entryPrice, double exitPrice, double pnl, int bit, int qty, DateTime exitTime)
        {
            try
            {
                // BUG FIX (timestamp consistency): ALL rows now use DateTime.Now — the
                // SAME clock DecideGap() compares against on RESUME. Mixing the execution
                // 'time' here with DateTime.Now elsewhere can make the RESUME gap wrong
                // and trigger a SPURIOUS FRESH START that WIPES rawString.
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10},{11}\n",
                    DateTime.Now, sliceCount, "Long", qty, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch (Exception ex) { DiagLog("[LOG ERROR] WriteLogRow: " + ex.Message); logWriteFailed = true; }
        }

        private void WriteLogRowFake(double entryPrice, double exitPrice, double pnl, int bit)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10},{11}\n",
                    DateTime.Now, sliceCount,
                    (suppressReason ?? "FAKE_Long"),   // distinguishes suppressed trades
                    0, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch (Exception ex) { DiagLog("[LOG ERROR] WriteLogRowFake: " + ex.Message); logWriteFailed = true; }
        }

        private void WriteLogRowCancelled(double entryPrice)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}\n",
                    DateTime.Now, sliceCount, "CANCELLED_no_fill", 0, entryPrice, 0, 0, "-",
                    rawString.ToString(), filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch (Exception ex) { DiagLog("[LOG ERROR] WriteLogRowCancelled: " + ex.Message); logWriteFailed = true; }
        }

        private void DiagLog(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg;
            Print(line);
            try
            {
                string dir = Path.GetDirectoryName(activeLogFilePath ?? "");
                if (string.IsNullOrEmpty(dir)) dir = LogFolder;
                if (string.IsNullOrEmpty(dir)) dir = @"C:\temp";
                string baseName = Path.GetFileNameWithoutExtension(activeLogFilePath ?? (LogBaseName + ".csv"));
                string diagPath = Path.Combine(dir, baseName + "-diagLog.csv");

                // Concurrency-safe. NOTE: do NOT call SafeAppend here — it calls
                // DiagLog on failure, which would recurse forever.
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(diagPath, FileMode.Append, FileAccess.Write,
                                                       FileShare.ReadWrite))
                        using (var sw = new StreamWriter(fs))
                        {
                            sw.Write(line + "\n");
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt == 3) break;
                        System.Threading.Thread.Sleep(10 * attempt);
                    }
                }
            }
            catch { }
        }

        // =====================================================================
        // Properties
        // =====================================================================
        #region Properties

        // ---- REQUIRED SETUP REMINDERS (read-only) -----------------------------
        [Display(Name = "Template: CME US Index Futures ETH",
            Description = "REQUIRED. Set the data series Trading Hours template (e.g. "
                        + "'CME US Index Futures ETH') so the strategy can measure market-open "
                        + "minutes correctly and cross the maintenance break. Search the template "
                        + "name in NinjaTrader to see the session times.",
            Order = 1, GroupName = "0. REQUIRED SETUP — read me")]
        [ReadOnly(true)]
        public string TemplateReminder { get { return "Set data series Trading Hours = CME US Index Futures ETH"; } set { } }

        [Display(Name = "Enable EOD break on data series",
            Description = "Keep IsExitOnSessionCloseStrategy ON (default). At the session close NT "
                        + "flattens any open position. We CANNOT know that fill's outcome, so it is "
                        + "recorded as a LOSS (conservative) in both rawString and realTradeOutcome.",
            Order = 2, GroupName = "0. REQUIRED SETUP — read me")]
        [ReadOnly(true)]
        public string EodReminder { get { return "EOD flatten ON; flattened trade recorded as loss"; } set { } }

        [Display(Name = "Tab shows 'enabled' even after a silent FRESH start — CHECK THE LOG",
            Description = "After a big gap the strategy WIPES its pipeline and warms up again, but the "
                        + "Strategies tab still shows 'enabled'. The tab CANNOT tell you fresh vs resume. "
                        + "Read the log: a [FRESH START] or [RESUME] line is written at every startup.",
            Order = 3, GroupName = "0. REQUIRED SETUP — read me")]
        [ReadOnly(true)]
        public string GapReminder { get { return "Big gap = silent fresh start; verify via log, not the tab"; } set { } }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading Hours filter", Order = 1, GroupName = "1. Hours")]
        public bool EnableTradingHours { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start hour (NY, 24h)", Order = 2, GroupName = "1. Hours")]
        public int TradingStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start minute (NY)", Order = 3, GroupName = "1. Hours")]
        public int TradingStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End hour (NY, 24h)", Order = 4, GroupName = "1. Hours")]
        public int TradingEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End minute (NY)", Order = 5, GroupName = "1. Hours")]
        public int TradingEndMinute { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Strategy life (minutes)", Order = 1, GroupName = "2. Timing")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Check interval (seconds)", Order = 2, GroupName = "2. Timing")]
        public int CheckIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Gap tolerance (market-open minutes)", Order = 3, GroupName = "2. Timing",
            Description = "If MORE than this many MARKET-OPEN minutes were missed since the last "
                        + "recorded bit, the pipeline is wiped (fresh start). The ~1h maintenance "
                        + "break has 0 open-minutes so it is always crossed. Default 5.")]
        public int GapToleranceMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 48)]
        [Display(Name = "Gap ceiling (wall-clock hours)", Order = 4, GroupName = "2. Timing",
            Description = "Absolute safety ceiling. If the wall-clock gap exceeds this many hours, "
                        + "fresh start regardless of open-minutes. Default 4.")]
        public int GapCeilingHours { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use market entry (else limit)", Order = 1, GroupName = "3. Entry")]
        public bool UseMarketEntry { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Limit offset (points)", Order = 2, GroupName = "3. Entry")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Stop loss (points)", Order = 1, GroupName = "4. Bracket")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Profit target (points)", Order = 2, GroupName = "4. Bracket")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trailing Stop", Order = 3, GroupName = "4. Bracket")]
        public bool EnableTrailingStop { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Trail distance (points)", Order = 4, GroupName = "4. Bracket")]
        public double TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Real Order", Order = 1, GroupName = "5. Filter & Real Order",
            Description = "FALSE = observation only. TRUE = real order fires when armed and F1 matches.")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 1 Pattern", Order = 2, GroupName = "5. Filter & Real Order",
            Description = "Tail pattern on rawString. Match -> next raw bit feeds filter1Outcome. "
                        + "Wildcards: '*'=one-or-more 0s, '?'=one-or-more 1s, expand in place "
                        + "('0*'='00+', '10?'='101+'). NOTE: L3 params not researched yet.")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 Pattern", Order = 3, GroupName = "5. Filter & Real Order",
            Description = "Tail pattern on filter1Outcome. Match -> next f1-digit feeds filter2Outcome. "
                        + "Same wildcards as Filter 1 ('*'=0+, '?'=1+, expand in place).")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 3 Pattern", Order = 4, GroupName = "5. Filter & Real Order",
            Description = "Tail pattern on filter2Outcome. Match -> isArmed=true. "
                        + "Same wildcards as Filter 1 ('*'=0+, '?'=1+, expand in place).")]
        public string Filter3Pattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 1, GroupName = "6. Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Qty Increment (see QtyMultiplierTable in code)",
            Order = 2, GroupName = "6. Quantity",
            Description = "FALSE = always BaseQuantity. TRUE = per-day loss-run rule (see QtyMultiplierTable): "
                        + "after a win, losses 1-3 => x2; losses 4-6 => SKIP. Resets every NY day. "
                        + "REVIEW the table whenever MaxRealLossInARow changes.")]
        public bool EnableQtyIncrement { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Qty rule (pattern:qty, comma-sep)", Order = 3, GroupName = "6. Quantity",
            Description = "Applied only when Enable Qty Increment is ON. Loss-ratchet on the "
                        + "REAL trade-outcome string (1=win,0=loss). Format pattern:qty pairs, e.g. "
                        + "(\"10\":2),(\"100\":2),(\"10000\":0) . qty 0 = SKIP. Longest tail wins; "
                        + "parens / quotes / spaces / trailing comma optional.")]
        public string QtyRuleText { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Total Slice Count", Order = 1, GroupName = "7. Limits",
            Description = "Stop after this many total slices (fake + real).")]
        public int MaxTotalSliceCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Real Loss In A Row", Order = 2, GroupName = "7. Limits",
            Description = "Stop after this many consecutive real losses. Default 3. Survives reconnect (restored on resume).")]
        public int MaxRealLossInARow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable trade-outcome exit", Order = 3, GroupName = "7. Limits")]
        public bool EnableTradeOutcomeExit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade-outcome exit pattern (halt session)", Order = 4, GroupName = "7. Limits",
            Description = "When enabled, HALT the session (manual re-enable) once the REAL "
                        + "trade-outcome tail (1=win,0=loss) ends with this PLAIN pattern (no "
                        + "wildcard). Slice default '1111111' + disabled = effectively inert.")]
        public string TradeOutcomeExitPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Folder", Order = 1, GroupName = "8. Logging",
            Description = "Folder for log files. A timestamp is appended per pipeline session: "
                        + "<base>_<YYYY-MM-DD_HH-mm-ss>.csv. A FRESH start makes a new file; a "
                        + "RESUME continues the most recent file.")]
        public string LogFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Base Name", Order = 2, GroupName = "8. Logging",
            Description = "Base file name (no extension / no date). The session timestamp and .csv are appended.")]
        public string LogBaseName { get; set; }

        #endregion
    }
}
