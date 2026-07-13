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

// scalper_SHORTrepeat_Layer2  v4  (reconnect-survival + file-per-session + EOD)
//
// ============================================================================
// WHAT CHANGED IN v4  (vs v3)
// ============================================================================
//  PROBLEM v3 had:
//    On any new strategy instance (NT auto-disables+re-enables on every
//    connection drop, or you manually re-enable), ALL in-memory pipeline
//    state was lost: rawString, filter1Outcome, isArmed, realLossesInARow
//    reset to empty/zero, and the log file was OVERWRITTEN. The pipeline had
//    to warm up from scratch every time, and the loss-streak breaker was
//    silently bypassed. A 3 AM connection blip wiped the whole pipeline.
//
//  FIX in v4 — "the file boundary IS the pipeline-continuity boundary":
//    On startup, the strategy reads its OWN most-recent log file and decides,
//    from the GAP since the last recorded bit, whether to:
//
//      RESUME  — the gap was small / only the maintenance break: reload
//                rawString, filter1Outcome, realTradeOutcome, re-derive the
//                pipeline flags, and RESTORE realLossesInARow (breaker
//                survives the reconnect). Keep appending to the SAME file.
//                Crosses the daily maintenance break with no warm-up.
//
//      FRESH   — the gap was big (a real outage, a weekend, or > ceiling):
//                the old string has a HOLE and cannot be trusted, so start a
//                BRAND-NEW timestamped file with an empty pipeline. Trades
//                naturally as soon as the pipeline re-arms (no special gate).
//
//    Decision rule (in order):
//      no log / empty                         -> FRESH
//      a weekend (Sat/Sun) falls in the gap   -> FRESH
//      wall-clock gap > GapCeilingHours (4h)  -> FRESH
//      MARKET-OPEN minutes in gap > GapToleranceMinutes (5) -> FRESH
//      otherwise                              -> RESUME
//
//    "Market-open minutes" is computed via the data series' Trading Hours
//    template (SessionIterator), so the ~1h maintenance break counts as 0
//    open-minutes and is always crossable, while a real mid-session outage
//    is caught.
//
//  REMOVED in v4:
//    StartMode, RawStringFilePath, MaxFileAgeMinutes, LoadAndReplayRawString.
//    The strategy's OWN log is now the single source of truth. It no longer
//    depends on the separate recorder file.
//
//  EOD handling in v4:
//    With IsExitOnSessionCloseStrategy=true, NT flattens any open position at
//    the session close. We do NOT know if that flatten was a win or loss, so
//    CONSERVATIVELY we record it as a LOSS ('0') in BOTH rawString and
//    realTradeOutcome, increment the loss streak, and run the pipeline so the
//    string stays continuous. (A rare EOD trade that was actually a small win
//    will show as a loss — accepted, conservative, infrequent.)
//
//  IMPORTANT — STRATEGY TAB CANNOT TELL YOU FRESH vs RESUME:
//    After a big-gap wipe, the strategy still shows "enabled" in the tab even
//    though its pipeline silently reset and is warming up. To know the true
//    state you MUST read the log: a clear [FRESH START] or [RESUME] line is
//    written at startup with the gap details.
//
// ============================================================================
// PIPELINE (unchanged — matches Python trade_filter.py exactly):
//   Every slice closes:
//     1. append bit to rawString
//     2. rawString tail matches Filter1Pattern? YES -> append bit to filter1Outcome
//     3. filter1Outcome tail matches Filter2Pattern? YES -> isArmed=true else false
//     4. isArmed AND rawString tail matches Filter1Pattern? YES -> next slice = money
//   TARGET = 1 = price DOWN (SHORT).
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Scalper_Shortrepeat_Layer2 : Strategy
    {
        // ── strategy lifecycle ────────────────────────────────────────────────
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private DateTime lastCheckTime = DateTime.MinValue;
        private int      sliceCount    = 0;   // all slices (fake + real)

        // ── pipeline strings ─────────────────────────────────────────────────
        private StringBuilder rawString         = new StringBuilder(); // Layer 0: all bricks
        private StringBuilder filter1Outcome    = new StringBuilder(); // Layer 1: after F1 match
        private StringBuilder realTradeOutcome  = new StringBuilder(); // real money trade results only

        // ── pipeline state ───────────────────────────────────────────────────
        private bool isArmed             = false;
        private bool waitingForF1Outcome = false;
        private bool nextIsMoney         = false;

        // ── slice state ──────────────────────────────────────────────────────
        private bool   inSlice        = false;
        private bool   isMoneySlice   = false;
        private double sliceEntryPrice = 0.0;
        private double sliceStopPrice  = 0.0;
        private double sliceTargetPrice= 0.0;

        // ── real order state ─────────────────────────────────────────────────
        private bool   entryInFlight      = false;
        private bool   awaitingClose      = false;
        private Order  workingEntryOrder  = null;
        private double entryFillPrice     = 0.0;
        private int    entryFillQty       = 0;

        // ── real loss streak ─────────────────────────────────────────────────
        private int realLossesInARow = 0;

        // ── session iterator (for market-open-minutes gap measure) ────────────
        private SessionIterator sessionIter = null;

        // ── active log file path (timestamped; chosen fresh or resumed) ───────
        private string activeLogFilePath = null;

        // ── qty multiplier table ──────────────────────────────────────────────
        // LONGEST-MATCHING pattern wins (order-independent; see CalcQty). Patterns
        // are matched against the tail of the PER-DAY session real outcome string
        // (sessionRealOutcome), so the qty rule resets every trading day.
        // NO wildcards here — literal 0/1 only.
        //
        // DESIGN (researched {'0':2} extended into a layered loss-run rule):
        //   Every pattern starts with '1' (a WIN) on purpose — this is a bug-guard.
        //   Sizing only kicks in on a loss run that FOLLOWED a win in the session.
        //   If the day opens with losses (e.g. "0000..." from a data/logic bug),
        //   NONE of these match, so those trades stay at BASE qty (x1) and never
        //   double into a disaster. The hard stop is MaxRealLossInARow.
        //
        //   "10","100","1000"        -> x2  (win then 1-3 losses: our validated edge)
        //   "10000","100000","1000000" -> 0 (win then 4-6 losses: SKIP the trade)
        //
        //   qty 0 == DO NOT place a real order (observe only). See CalcQty caller.
        //
        // *** COUPLING WARNING ***
        //   This table must be reviewed whenever MaxRealLossInARow changes. The x0
        //   "skip" lines only cover loss-runs up to their length; a run LONGER than
        //   the longest pattern reverts to base qty (the leading '1' scrolls out of
        //   range and nothing matches). With MaxRealLossInARow=3 the x0 lines are
        //   dormant (breaker halts first). If you raise the breaker (e.g. to 7),
        //   extend the x0 lines so every loss up to the breaker is covered.
        private static readonly (string pattern, int multiplier)[] QtyMultiplierTable =
        {
            ("10",      2),
            ("100",     2),
            ("1000",    2),
            ("10000",   0),
            ("100000",  0),
            ("1000000", 0),
        };

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
        private int sessionDayKey = -1;   // yyyymmdd of the current NY session day

        // ── shutdown ─────────────────────────────────────────────────────────
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "SR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "SHORT scalper v4. Reconnect-survival: reloads its own log "
                            + "and RESUMES the pipeline across reconnect / maintenance break, "
                            + "or FRESH-starts a new file when the gap is too big. "
                            + "Pipeline matches Python trade_filter.py. Target=1=price DOWN.";
                Name        = "Scalper_Shortrepeat_Layer2";

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

                // ── defaults (researched SHORT 32/19 primary) ──────────────────
                // Validated: SHORT 32/19, F1=10? (=101+), F2=00, morning window
                // 09:30–11:30 ET, {'0':2} qty rule (per-day). Ships with real
                // orders OFF and qty increment OFF — turn each on deliberately.
                EnableTradingHours   = true;    // restrict to the researched window
                TradingStartHour     = 9;       // 09:30 ET = morning_open start (6:30 PT)
                TradingStartMinute   = 30;
                TradingEndHour       = 11;      // 11:30 ET = morning_open end (8:30 PT)
                TradingEndMinute     = 30;
                StrategyLifeMinutes  = 1440;   // 24h; lifetime no longer the main control
                CheckIntervalSeconds = 1;
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 32;      // researched SHORT stop
                ProfitTargetPoints   = 19;      // researched SHORT target
                EnableTrailingStop   = false;
                TrailDistancePoints  = 10;
                EnableRealOrder      = false;   // observation only until you flip it
                Filter1Pattern       = "10?";   // 101+  (V-recovery)
                Filter2Pattern       = "00";     // two-loss confirm in L1
                BaseQuantity         = 1;
                EnableQtyIncrement   = false;   // {'0':2} rule; enable after live-validating
                MaxTotalSliceCount   = 100000; // high; not the main control anymore
                MaxRealLossInARow    = 5;       // plan for MCL up to 5-6 (backtest MCL=2 is a floor)

                // logging — folder + base name; timestamp appended per session
                LogFolder            = @"C:\temp";
                LogBaseName          = "scalper_SHORTrepeat_Layer2";

                // gap thresholds
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

                    // ── decide FRESH vs RESUME from own log, then warm/restore ──
                    StartupDecideAndLoad();
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
        // STARTUP: decide FRESH vs RESUME, set the active log file, restore or
        // initialize the pipeline. Writes a clear [FRESH START] / [RESUME] line.
        // =====================================================================
        private void StartupDecideAndLoad()
        {
            // baseline (fresh) state
            isArmed             = false;
            waitingForF1Outcome = false;
            nextIsMoney         = false;
            realLossesInARow    = 0;
            currentQty          = BaseQuantity;
            rawString.Clear();
            filter1Outcome.Clear();
            realTradeOutcome.Clear();

            string latest = FindMostRecentLogFile();   // null if none

            bool   doFresh = true;
            string reason  = "no prior log file";
            DateTime lastBitLocal = DateTime.MinValue;

            if (!string.IsNullOrEmpty(latest))
            {
                // read last bit time + cumulative strings from that file
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
                        // RESUME: restore pipeline from snapshot
                        doFresh = false;
                        rawString.Append(snap.rawString);
                        filter1Outcome.Append(snap.filter1Outcome);
                        realTradeOutcome.Append(snap.realTradeOutcome);
                        realLossesInARow = CountTrailingLosses(snap.realTradeOutcome);
                        ReDerivePipelineFlags();
                        activeLogFilePath = latest;     // keep same file
                        reason = gd.reason;

                        // Per-day qty session: on RESUME we start the qty session
                        // FRESH (empty) and set the day key to the current NY day.
                        // RESUME only happens for small same-session gaps, so the
                        // first post-reconnect real trade simply uses base qty until
                        // a new real loss occurs — conservative and safe. (The
                        // cumulative loss-streak breaker above is still restored.)
                        sessionRealOutcome.Clear();
                        sessionDayKey = CurrentNyDayKey();
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
                    + " realTradeOutcome=" + realTradeOutcome.ToString()
                    + " realLossesInARow=" + realLossesInARow
                    + " isArmed=" + isArmed
                    + " waitingForF1Outcome=" + waitingForF1Outcome
                    + " nextIsMoney=" + nextIsMoney
                    + " | last bit was " + lastBitLocal.ToString("yyyy-MM-dd HH:mm:ss")
                    + ". Breaker intact across reconnect.");
            }

            DiagLog(Name + " ready (SHORT). EnableRealOrder=" + EnableRealOrder
                + ", Filter1=[" + Filter1Pattern + "], Filter2=[" + Filter2Pattern + "]"
                + ", MaxRealLossInARow=" + MaxRealLossInARow
                + ", Stop=" + StopLossPoints + "pt, Target=" + ProfitTargetPoints + "pt"
                + ", GapTolerance=" + GapToleranceMinutes + "min, GapCeiling=" + GapCeilingHours + "h");
        }

        // ── gap decision ──────────────────────────────────────────────────────
        private class GapDecision { public bool fresh; public string reason; }

        private GapDecision DecideGap(DateTime lastBitLocal, DateTime nowLocal)
        {
            var d = new GapDecision();

            // 1) weekend in the gap -> fresh
            if (WeekendInGap(lastBitLocal, nowLocal))
            {
                d.fresh = true;
                d.reason = "weekend fell within the gap (Fri pipeline not continuous with reopen)";
                return d;
            }

            // 2) wall-clock ceiling -> fresh
            double wallHours = (nowLocal - lastBitLocal).TotalHours;
            if (wallHours > GapCeilingHours)
            {
                d.fresh = true;
                d.reason = "wall-clock gap " + wallHours.ToString("F1")
                         + "h exceeds ceiling " + GapCeilingHours + "h";
                return d;
            }

            // 3) market-open minutes in gap -> fresh if over tolerance
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
            // step day by day; if any calendar day is Sat or Sun, weekend in gap
            DateTime cur = a.Date;
            while (cur <= b.Date)
            {
                if (cur.DayOfWeek == DayOfWeek.Saturday || cur.DayOfWeek == DayOfWeek.Sunday)
                    return true;
                cur = cur.AddDays(1);
            }
            return false;
        }

        // Count how many minutes between a and b the market was OPEN, per the
        // data series Trading Hours template. Bounded by GapCeilingHours so the
        // loop can never run long (we only reach here when wall gap <= ceiling).
        private int MarketOpenMinutesInGap(DateTime a, DateTime b)
        {
            try
            {
                if (sessionIter == null || b <= a) return 0;
                int openCount = 0;
                DateTime t = a;
                int safety = GapCeilingHours * 60 + 5;   // hard cap on iterations
                while (t < b && safety-- > 0)
                {
                    DateTime next = t.AddMinutes(1);
                    // Is the market open at minute 't'? Use TradingHours.
                    if (IsMarketOpenAt(t))
                        openCount++;
                    t = next;
                }
                return openCount;
            }
            catch (Exception ex)
            {
                DiagLog("MarketOpenMinutesInGap error: " + ex.Message
                    + " -> treating as OPEN (conservative -> fresh).");
                // On error, return a large number so we FRESH-start (safe).
                return GapToleranceMinutes + 9999;
            }
        }

        // Determine if the market is open at a given LOCAL time, via the
        // SessionIterator (which reads the data series' Trading Hours template).
        private bool IsMarketOpenAt(DateTime localTime)
        {
            try
            {
                if (sessionIter == null) return true;  // fail-open -> caller fresh-starts (safe)
                // IsInSession(timeLocal, includesEndTimeStamp, isIntraDay)
                // isIntraDay=true so the time-of-day is considered (not just the date).
                return sessionIter.IsInSession(localTime, true, true);
            }
            catch
            {
                // If the API differs in your NT build, default to OPEN so a gap
                // is treated as a real hole (fresh start) — the safe direction.
                return true;
            }
        }

        // =====================================================================
        // CalcQty — researched layered per-day loss-run rule (see QtyMultiplierTable)
        // Returns the real order quantity, or 0 meaning "SKIP the trade" (the
        // caller must NOT place a real order when this returns 0).
        // LONGEST-matching pattern wins (order-independent).
        // =====================================================================
        private int CalcQty()
        {
            if (!EnableQtyIncrement) return BaseQuantity;

            // Qty rule is driven by the PER-DAY session outcome string so it
            // resets every trading day (each day is a brand-new sizing sequence).
            string outcome = sessionRealOutcome.ToString();
            if (outcome.Length == 0) return BaseQuantity;

            // Longest-matching pattern wins, regardless of table order. We scan
            // all entries and keep the match whose pattern is the longest.
            int  bestLen  = -1;
            int  bestMult = 1;
            bool matched  = false;
            foreach (var entry in QtyMultiplierTable)
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

        // Returns the NY-session day key (yyyymmdd) for "now". Used to detect a new
        // trading day and roll the per-day qty session. We key on the NY calendar
        // date at the moment a real trade resolves; the morning trade window
        // (default 09:30–11:30 ET) is always within one NY calendar day, so a plain
        // date key is sufficient and simple.
        private int CurrentNyDayKey()
        {
            TimeZoneInfo et;
            try   { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { et = null; } }
            DateTime ny = (et != null)
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et)
                : DateTime.Now;
            return ny.Year * 10000 + ny.Month * 100 + ny.Day;
        }

        // Append a real outcome bit to the per-day session string, rolling to a
        // fresh session string when the NY day changes.
        private void RecordSessionOutcome(int bit)
        {
            int key = CurrentNyDayKey();
            if (key != sessionDayKey)
            {
                if (sessionDayKey != -1)
                    DiagLog(string.Format("[QTY SESSION ROLL] new NY day {0} (was {1}) -> qty session reset. "
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
            double refPrice = GetCurrentAsk();
            if (refPrice <= 0) { sliceCount--; return; }

            if (!UseMarketEntry)
                refPrice = Instrument.MasterInstrument.RoundToTickSize(refPrice + LimitOffsetPoints);

            sliceEntryPrice  = refPrice;
            sliceStopPrice   = Instrument.MasterInstrument.RoundToTickSize(sliceEntryPrice + StopLossPoints);   // ABOVE entry
            sliceTargetPrice = Instrument.MasterInstrument.RoundToTickSize(sliceEntryPrice - ProfitTargetPoints); // BELOW entry
            inSlice          = true;
            isMoneySlice     = startMoney && EnableRealOrder;
            suppressReason   = null;   // reset; set by the guards below if demoted

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
            // another strategy (e.g. the LONG book), or by you manually / via ATM —
            // then we do NOT place an order. We demote this money slice to an
            // OBSERVATION slice: it still runs, still resolves, and still feeds the
            // pipeline, so the bit string stays continuous and the filters stay
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

            // ── GUARD 3: QTY RULE ──────────────────────────────────────────────
            // Compute qty up front. A qty of 0 means the qty rule says SKIP this
            // trade (e.g. deep into a loss run). We demote it to an OBSERVATION
            // slice: no real order is placed, but the slice still resolves and
            // feeds the pipeline so the bit string stays continuous.
            if (isMoneySlice)
            {
                currentQty = CalcQty();
                if (currentQty <= 0)
                {
                    DiagLog(string.Format(
                        "[QTY SKIP] Slice #{0} qty rule returned 0 -> NO real order, observe only. sessionReal={1}",
                        sliceCount, sessionRealOutcome.ToString()));
                    isMoneySlice   = false;   // run as a fake/observation slice
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
                        workingEntryOrder = EnterShort(currentQty, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SLICE #{0} MARKET qty={1} entry~{2:F2} stop={3:F2} target={4:F2} | raw={5} | f1={6} | real={7}",
                            sliceCount, currentQty, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8), TailOf(realTradeOutcome, 8)));
                    }
                    else
                    {
                        double limitPx = Instrument.MasterInstrument.RoundToTickSize(
                            GetCurrentAsk() + LimitOffsetPoints);
                        workingEntryOrder = EnterShortLimit(0, true, currentQty, limitPx, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SLICE #{0} LIMIT qty={1} limit={2:F2} | raw={3} | f1={4} | real={5}",
                            sliceCount, currentQty, limitPx,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8), TailOf(realTradeOutcome, 8)));
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
                    "FAKE SLICE #{0} entry={1:F2} stop={2:F2} target={3:F2} | isArmed={4} | rawTail={5} | f1={6}",
                    sliceCount, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                    isArmed, TailOf(rawString, 8), TailOf(filter1Outcome, 8)));
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

                bool stopHit   = ask >= sliceStopPrice;
                bool targetHit = bid <= sliceTargetPrice;
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

                    if (Position.MarketPosition == MarketPosition.Short)
                    {
                        DiagLog(string.Format("[BRICK CLEANUP Case2] Slice #{0} position still open. Force close. bit={1}", sliceCount, bit));
                        try { ExitShort(Math.Abs(Position.Quantity), "SR_ForceClose", ENTRY_SIGNAL); }
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
        // UpdatePipeline (unchanged)
        // =====================================================================
        private void UpdatePipeline(int bit)
        {
            rawString.Append(bit.ToString());
            string raw = rawString.ToString();

            if (waitingForF1Outcome)
            {
                waitingForF1Outcome = false;
                filter1Outcome.Append(bit.ToString());
                string f1str = filter1Outcome.ToString();
                DiagLog(string.Format("[F1 COLLECT] digit after F1='{0}' is '{1}' -> f1={2}",
                    Filter1Pattern, bit, f1str));
                isArmed = TailMatches(f1str, Filter2Pattern);
                DiagLog(isArmed ? "[F2 MATCH] isArmed=true" : "[F2 NO MATCH] isArmed=false");
            }

            bool f1Match = TailMatches(raw, Filter1Pattern);
            if (f1Match)
            {
                waitingForF1Outcome = true;
                DiagLog("[F1 MATCH] rawString tail matches Filter1 -> next bit feeds filter1Outcome");
            }

            nextIsMoney = isArmed && TailMatches(raw, Filter1Pattern);

            DiagLog(string.Format("[PIPELINE] raw({0})={1} | f1({2})={3} | waitF1={4} | isArmed={5} | nextIsMoney={6} | realLossRow={7}",
                rawString.Length, TailOf(rawString, 8),
                filter1Outcome.Length, TailOf(filter1Outcome, 8),
                waitingForF1Outcome, isArmed, nextIsMoney, realLossesInARow));
        }

        // Re-derive isArmed / waitingForF1Outcome / nextIsMoney from the loaded
        // rawString + filter1Outcome (used on RESUME). We recompute the flags
        // that the next-bit logic depends on, from the cumulative strings.
        private void ReDerivePipelineFlags()
        {
            string raw   = rawString.ToString();
            string f1str = filter1Outcome.ToString();

            // isArmed: does filter1Outcome currently end with Filter2Pattern?
            isArmed = TailMatches(f1str, Filter2Pattern);

            // waitingForF1Outcome: did the LAST bit complete an F1 match whose
            // "digit after" has not yet arrived? We cannot perfectly know if the
            // next bit was already consumed, so we set it from the current tail:
            // if rawString currently ends with Filter1Pattern, the next real bit
            // should feed filter1Outcome.
            waitingForF1Outcome = TailMatches(raw, Filter1Pattern);

            // nextIsMoney consistent with UpdatePipeline's definition
            nextIsMoney = isArmed && TailMatches(raw, Filter1Pattern);
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

            // ── entry fill ────────────────────────────────────────────────────
            if (oName == ENTRY_SIGNAL && (isFull || isPart))
            {
                if (entryFillPrice == 0.0) entryFillPrice = price;
                entryFillQty += quantity;
                DiagLog(string.Format("ENTRY {0} fill: qty={1} @ {2:F2} total={3}",
                    isFull ? "FULL" : "PARTIAL", quantity, price, entryFillQty));
                if (isFull) { entryInFlight = false; workingEntryOrder = null; }
                return;
            }

            // ── recognized bracket exit (stop / target) ───────────────────────
            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("StopCancelClose", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            // ── EOD / forced flatten detection ────────────────────────────────
            // Any exit that flattens the position but is NOT our stop/target and
            // NOT our own SR_ForceClose is treated as an EOD/session-close flatten.
            // We do not trust its price -> record as LOSS, conservatively.
            bool isOurForceClose = oName.IndexOf("SR_ForceClose", StringComparison.OrdinalIgnoreCase) >= 0
                                 || oName.IndexOf("SR_Flatten",    StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitFill = !(oName == ENTRY_SIGNAL);

            if (isFull && isExitFill && !isStopFill && !isTargetFill && !isOurForceClose
                && Position.MarketPosition == MarketPosition.Flat
                && awaitingClose)
            {
                // EOD/forced flatten of a real money position.
                int bit = 0;   // assume LOSS (we don't know the true outcome)
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

                UpdatePipeline(bit);   // append 0 to rawString too — keep continuity
                WriteLogRow(logFillPrice, price, 0.0, bit, logFillQty, time);
                return;
            }

            // ── normal bracket exit ───────────────────────────────────────────
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
        // AccountBusyOnThisInstrument  — "does ANYONE hold MNQ right now?"
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

            if (Position.MarketPosition == MarketPosition.Short)
            {
                try { ExitShort(Math.Abs(Position.Quantity), "SR_Flatten", ENTRY_SIGNAL);
                    DiagLog("Shutdown: ExitShort submitted for " + Math.Abs(Position.Quantity) + "."); }
                catch (Exception ex) { DiagLog("Shutdown ExitShort error: " + ex.Message); }
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
                + " | nextIsMoney=" + nextIsMoney
                + " | rawString=" + rawString.ToString()
                + " | filter1Outcome=" + filter1Outcome.ToString()
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

        // =====================================================================
        // WILDCARD PATTERN MATCHING
        // =====================================================================
        // Pattern language (matches the Python research tooling exactly):
        //     '0'  -> literal 0
        //     '1'  -> literal 1
        //     '*'  -> one-or-more 0s   (regex 0+)
        //     '?'  -> one-or-more 1s   (regex 1+)
        // The pattern is matched against the TAIL (suffix) of the text.
        // Because '*' / '?' expand IN PLACE, a literal that precedes them stacks:
        //     "0*"  = 0 then 0+  = "00+" = TWO-or-more 0s
        //     "1*"  = 1 then 0+  = "10+"
        //     "10?" = 1,0 then 1+ = "101+"
        //     "1*?" = 1 then 0+ then 1+ = "10+1+"
        //
        // Implementation: we walk the pattern and the text tail together using a
        // small backtracking matcher (no System.Text.RegularExpressions dependency,
        // and it only ever runs on short tails). The pattern must consume EXACTLY
        // the end of the text: we try each possible start position from the end and
        // succeed if the whole pattern matches text[start..end].
        //
        // For performance in the hot path we do the common no-wildcard case with a
        // plain EndsWith, and only use the backtracking matcher when the pattern
        // actually contains '*' or '?'.
        private static bool PatternHasWildcard(string pattern)
        {
            return pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
        }

        // Returns true if 'pattern' matches a suffix of 'text'.
        private static bool TailMatches(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (text.Length == 0) return false;

            if (!PatternHasWildcard(pattern))
                return text.Length >= pattern.Length && text.EndsWith(pattern);

            // Try every start index; the pattern must consume text[start .. end).
            // Most matches anchor near the end, so scan from the longest feasible
            // start backwards is unnecessary — we just need ANY start that works,
            // and a suffix match means the match must end exactly at text.Length.
            for (int start = text.Length - 1; start >= 0; start--)
            {
                if (MatchHere(text, start, pattern, 0))
                    return true;
                // small optimization: a pattern of minimum length L cannot start
                // later than text.Length - L, but variable wildcards make L fuzzy;
                // the loop is cheap on short tails so we keep it simple.
            }
            return false;
        }

        // Recursive matcher: does pattern[pi..] match text[ti..] and consume to end?
        private static bool MatchHere(string text, int ti, string pattern, int pi)
        {
            while (pi < pattern.Length)
            {
                char pc = pattern[pi];

                if (pc == '*' || pc == '?')
                {
                    char want = (pc == '*') ? '0' : '1';   // '*' = 0+, '?' = 1+
                    // need at least one matching char
                    if (ti >= text.Length || text[ti] != want) return false;
                    // consume the mandatory one
                    ti++;
                    // greedily consume additional 'want' chars, with backtracking
                    int maxConsume = ti;
                    while (maxConsume < text.Length && text[maxConsume] == want) maxConsume++;
                    // try longest first, backtrack down to the mandatory one
                    for (int consume = maxConsume; consume >= ti; consume--)
                    {
                        if (MatchHere(text, consume, pattern, pi + 1))
                            return true;
                    }
                    return false;
                }
                else
                {
                    // literal 0 or 1
                    if (ti >= text.Length || text[ti] != pc) return false;
                    ti++; pi++;
                }
            }
            // pattern fully consumed — for a SUFFIX match, text must also be at end
            return ti == text.Length;
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

        // Find the most-recent existing log file matching the base name pattern.
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
                // the data file, and it has no data rows — picking it would make the
                // reader see "unreadable/empty" and wrongly FRESH-start.
                var dataFiles = files
                    .Where(p => !Path.GetFileNameWithoutExtension(p)
                                     .EndsWith("-diagLog", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (dataFiles.Length == 0) return null;
                // newest by last write time
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
            public string realTradeOutcome = "";
        }

        // Log row format (EnsureLogHeader):
        // timestamp,slice_num,side,quantity,entry_price,exit_price,realized_pnl,
        //   win_loss_bit,rawString,filter1Outcome,realTradeOutcome
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
                    if (line.StartsWith("timestamp")) continue;   // header
                    string[] p = line.Split(',');
                    if (p.Length < 11) continue;                  // not a full data row
                    // skip CANCELLED rows (bit column == '-') for the strings,
                    // but they still carry cumulative strings, so we can use them;
                    // however the cleanest is the last row that has the strings.
                    string ts   = p[0].Trim();
                    string raw  = p[8].Trim();
                    string f1   = p[9].Trim();
                    string real = p[10].Trim();

                    DateTime tparsed;
                    if (!DateTime.TryParse(ts,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out tparsed)) continue;   // BUG FIX: we WRITE with InvariantCulture

                    snap.lastBitLocal     = tparsed;
                    snap.rawString        = raw;
                    snap.filter1Outcome   = f1;
                    snap.realTradeOutcome = real;
                    snap.valid            = raw.Length > 0;   // need at least some rawString
                    return snap;
                }
                return snap; // valid stays false
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
                // Only write header if the file does NOT already exist
                // (so a RESUME never overwrites; FRESH always makes a new name).
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "timestamp(machine_local_time),slice_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,rawString,filter1Outcome,realTradeOutcome\n");
                }
            }
            catch (Exception ex) { Print("Log header error: " + ex.Message); }
        }

        // =====================================================================
        // SafeAppend — concurrency-tolerant file append
        // =====================================================================
        // BUG FIX: File.AppendAllText opens the file WITHOUT sharing, so if two
        // instances of this strategy are alive at once (which HAPPENS during
        // NinjaTrader's auto-restart churn on a lost price connection — the old
        // instance may still be terminating while the new one starts), the second
        // writer throws IOException ("file in use"). The old code swallowed that
        // exception with a bare Print(), so the ROW WAS SILENTLY LOST while the bit
        // had ALREADY been appended to the in-memory rawString. Log and memory then
        // diverge, and the next RESUME restores a WRONG pipeline state.
        //
        // This version: opens with FileShare.ReadWrite, retries briefly on lock,
        // and if it STILL fails it shouts loudly (DiagLog) and sets logWriteFailed
        // so the failure is visible rather than silent.
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
                    return;   // success
                }
                catch (IOException)
                {
                    if (attempt == MAX_TRIES) break;
                    System.Threading.Thread.Sleep(20 * attempt);   // brief backoff, then retry
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
                // BUG FIX (timestamp consistency): ALL log rows now use DateTime.Now,
                // the SAME clock that DecideGap() compares against on RESUME. The old
                // code wrote the execution 'time' here but DateTime.Now in the fake /
                // cancelled rows. If those two clocks differ (different time base),
                // the RESUME gap calculation is wrong by that offset and can exceed
                // GapCeilingHours -> a SPURIOUS FRESH START that WIPES rawString.
                // The execution time is still available in the diag log.
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10}\n",
                    DateTime.Now, sliceCount, "Short", qty, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch (Exception ex) { DiagLog("[LOG ERROR] WriteLogRow: " + ex.Message); logWriteFailed = true; }
        }

        private void WriteLogRowFake(double entryPrice, double exitPrice, double pnl, int bit)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10}\n",
                    DateTime.Now, sliceCount,
                    (suppressReason ?? "FAKE_Short"),   // distinguishes suppressed trades
                    0, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch (Exception ex) { DiagLog("[LOG ERROR] WriteLogRowFake: " + ex.Message); logWriteFailed = true; }
        }

        private void WriteLogRowCancelled(double entryPrice)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}\n",
                    DateTime.Now, sliceCount, "CANCELLED_no_fill", 0, entryPrice, 0, 0, "-",
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
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

                // Concurrency-safe append. NOTE: we do NOT call SafeAppend() here —
                // SafeAppend calls DiagLog on failure, which would recurse forever.
                // Diag lines are best-effort: retry a couple of times, then give up
                // silently (Print above still shows it in the NT Output window).
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
        [Display(Name = "Start hour (NY/Eastern, 24h)", Order = 2, GroupName = "1. Hours")]
        public int TradingStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start minute (NY/Eastern)", Order = 3, GroupName = "1. Hours")]
        public int TradingStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End hour (NY/Eastern, 24h)", Order = 4, GroupName = "1. Hours")]
        public int TradingEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End minute (NY/Eastern)", Order = 5, GroupName = "1. Hours")]
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
            Description = "Tail pattern on rawString. Match -> next digit feeds filter1Outcome. "
                        + "Wildcards: '*'=one-or-more 0s, '?'=one-or-more 1s, '0'/'1'=literal. "
                        + "They expand in place, so '0*'='00+' (two+ zeros), '10?'='101+', '1*?'='10+1+'. "
                        + "Researched SHORT default: 10?")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 Pattern", Order = 3, GroupName = "5. Filter & Real Order",
            Description = "Tail pattern on filter1Outcome. Match -> isArmed=true. "
                        + "Same wildcards as Filter 1 ('*'=0+, '?'=1+, expand in place). "
                        + "Researched SHORT default: 00")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 1, GroupName = "6. Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Qty Increment ({'0':2} per-day rule)",
            Order = 2, GroupName = "6. Quantity",
            Description = "FALSE = always BaseQuantity. TRUE = researched per-day loss-run rule "
                        + "(see QtyMultiplierTable in source): after a win, losses 1-3 => x2; "
                        + "losses 4-6 => SKIP (no trade). Resets every NY trading day. "
                        + "REVIEW the table whenever MaxRealLossInARow changes.")]
        public bool EnableQtyIncrement { get; set; }

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
