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
    public class scalper_SHORTrepeat_Layer3 : Strategy
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
        private static readonly (string pattern, int multiplier)[] QtyMultiplierTable =
        {
            ("10000", 4),
            ("1000",  3),
            ("100",   2),
            ("10",    2),
        };

        private int currentQty = 1;

        // ── shutdown ─────────────────────────────────────────────────────────
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "SR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "SHORT scalper v3 Layer3. Reconnect-survival: reloads its own log "
                            + "and RESUMES the 3-layer pipeline across reconnect / maintenance break, "
                            + "or FRESH-starts a new file when the gap is too big. Target=1=price DOWN.";
                Name        = "scalper_SHORTrepeat_Layer3";

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
                StopLossPoints       = 10;
                ProfitTargetPoints   = 10;
                EnableTrailingStop   = false;
                TrailDistancePoints  = 10;
                EnableRealOrder      = false;
                Filter1Pattern       = "01";
                Filter2Pattern       = "11";
                Filter3Pattern       = "1";
                BaseQuantity         = 1;
                EnableQtyIncrement   = false;
                MaxTotalSliceCount   = 100000;
                MaxRealLossInARow    = 3;

                LogFolder            = @"C:\temp";
                LogBaseName          = "scalper_SHORTrepeat_Layer3";

                GapToleranceMinutes  = 5;
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

            if (!WithinTradingHours())
                return;

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
                        realLossesInARow = CountTrailingLosses(snap.realTradeOutcome);
                        ReDerivePipelineFlags();
                        activeLogFilePath = latest;
                        reason = gd.reason;
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

            DiagLog(Name + " ready (SHORT). EnableRealOrder=" + EnableRealOrder
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
        private int CalcQty()
        {
            if (!EnableQtyIncrement) return BaseQuantity;
            string outcome = realTradeOutcome.ToString();
            if (outcome.Length == 0) return BaseQuantity;
            foreach (var entry in QtyMultiplierTable)
            {
                if (outcome.Length >= entry.pattern.Length && outcome.EndsWith(entry.pattern))
                {
                    int qty = BaseQuantity * entry.multiplier;
                    DiagLog(string.Format("[QTY] tail matches '{0}' -> x{1} -> qty={2}",
                        entry.pattern, entry.multiplier, qty));
                    return qty;
                }
            }
            return BaseQuantity;
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

            if (isMoneySlice)
            {
                currentQty = CalcQty();
                awaitingClose     = true;
                entryInFlight     = true;
                workingEntryOrder = null;
                try
                {
                    if (UseMarketEntry)
                    {
                        workingEntryOrder = EnterShort(currentQty, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SLICE #{0} MARKET qty={1} entry~{2:F2} stop={3:F2} target={4:F2} | raw={5} | f1={6} | f2={7} | real={8}",
                            sliceCount, currentQty, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8), TailOf(filter2Outcome, 8), TailOf(realTradeOutcome, 8)));
                    }
                    else
                    {
                        double limitPx = Instrument.MasterInstrument.RoundToTickSize(
                            GetCurrentAsk() + LimitOffsetPoints);
                        workingEntryOrder = EnterShortLimit(0, true, currentQty, limitPx, ENTRY_SIGNAL);
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
                    isArmed, TailOf(rawString, Filter1Pattern.Length),
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

                    isArmed = f2str.Length >= Filter3Pattern.Length && f2str.EndsWith(Filter3Pattern);
                    DiagLog(isArmed ? "[F3 MATCH] isArmed=true" : "[F3 NO MATCH] isArmed=false");
                }

                if (f1str.Length >= Filter2Pattern.Length && f1str.EndsWith(Filter2Pattern))
                {
                    waitingForF2Outcome = true;
                    DiagLog("[F2 MATCH] filter1Outcome tail matches Filter2 -> next f1-digit feeds filter2Outcome");
                }
            }

            if (raw.Length >= Filter1Pattern.Length && raw.EndsWith(Filter1Pattern))
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

            waitingForF1Outcome = raw.Length   >= Filter1Pattern.Length && raw.EndsWith(Filter1Pattern);
            waitingForF2Outcome = f1str.Length >= Filter2Pattern.Length && f1str.EndsWith(Filter2Pattern);
            isArmed             = f2str.Length >= Filter3Pattern.Length && f2str.EndsWith(Filter3Pattern);
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
            bool isOurForceClose = oName.IndexOf("SR_ForceClose", StringComparison.OrdinalIgnoreCase) >= 0
                                 || oName.IndexOf("SR_Flatten",    StringComparison.OrdinalIgnoreCase) >= 0;
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
        // ReadyForNewSlice (unchanged)
        // =====================================================================
        private bool ReadyForNewSlice()
        {
            if (inSlice)       return false;
            if (awaitingClose) return false;
            if (entryInFlight) return false;
            if (Position.MarketPosition != MarketPosition.Flat) return false;

            try
            {
                if (Account != null)
                {
                    lock (Account.Orders)
                    {
                        foreach (var ord in Account.Orders)
                        {
                            if (ord.Instrument == Instrument
                                && (ord.OrderState == OrderState.Working
                                    || ord.OrderState == OrderState.Accepted
                                    || ord.OrderState == OrderState.Submitted
                                    || ord.OrderState == OrderState.PartFilled))
                            {
                                DiagLog(string.Format("ReadyForNewSlice: BLOCKED by order '{0}' state={1}.",
                                    ord.Name ?? "", ord.OrderState));
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("ReadyForNewSlice scan error: " + ex.Message + ". Blocking.");
                return false;
            }
            return true;
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
                    if (!DateTime.TryParse(ts, out tparsed)) continue;

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

        private void WriteLogRow(double entryPrice, double exitPrice, double pnl, int bit, int qty, DateTime exitTime)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10},{11}\n",
                    exitTime, sliceCount, "Short", qty, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString());
                File.AppendAllText(activeLogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error: " + ex.Message); }
        }

        private void WriteLogRowFake(double entryPrice, double exitPrice, double pnl, int bit)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10},{11}\n",
                    DateTime.Now, sliceCount, "FAKE_Short", 0, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString());
                File.AppendAllText(activeLogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error (fake): " + ex.Message); }
        }

        private void WriteLogRowCancelled(double entryPrice)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}\n",
                    DateTime.Now, sliceCount, "CANCELLED_no_fill", 0, entryPrice, 0, 0, "-",
                    rawString.ToString(), filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString());
                File.AppendAllText(activeLogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error (cancelled): " + ex.Message); }
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
                File.AppendAllText(diagPath, line + "\n");
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
            Description = "Pattern checked against rawString tail. Match -> digit appended to filter1Outcome.")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 Pattern", Order = 3, GroupName = "5. Filter & Real Order",
            Description = "Pattern checked against filter1Outcome tail. Match -> digit appended to filter2Outcome.")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 3 Pattern", Order = 4, GroupName = "5. Filter & Real Order",
            Description = "Pattern checked against filter2Outcome tail. Match -> isArmed=true.")]
        public string Filter3Pattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 1, GroupName = "6. Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Qty Increment (see QtyMultiplierTable in code)",
            Order = 2, GroupName = "6. Quantity",
            Description = "FALSE = always BaseQuantity. TRUE = qty scales per QtyMultiplierTable in source.")]
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
