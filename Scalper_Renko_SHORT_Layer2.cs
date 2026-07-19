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

// Scalper_Renko_SHORT_Layer2  v1
//
// ============================================================================
// WHAT THIS IS
// ============================================================================
// A Renko-based Layer-2 meta-labeling strategy for SHORT on MNQ.
//
// RAW STRING SOURCE:
//   Each closed Renko bar produces ONE bit:
//     Green bar (Close > Open) = price went UP    = bit '0' (loss for SHORT)
//     Red bar   (Close < Open) = price went DOWN  = bit '1' (win  for SHORT)
//
//   Target = 1 = we chase '1's = we want RED bars = bearish momentum.
//
// PIPELINE (same as Python trade_filter.py):
//   Every closed Renko bar:
//     1. Determine bit from bar color (Green=0, Red=1)
//     2. Append bit to rawString
//     3. If waitingForF1Outcome: append bit to filter1Outcome
//        check filter1Outcome tail matches F2 -> isArmed
//     4. If rawString tail matches F1 -> waitingForF1Outcome = true
//     5. If isArmed AND rawString tail matches F1 -> nextIsMoney = true
//     6. If nextIsMoney -> enter REAL SHORT on next bar with bracket
//
// REAL TRADE BRACKET (when fired):
//   EnterShort at market (or limit)
//   StopLoss   = entry + StopLossPoints   (price went up = bad)
//   ProfitTarget = entry - ProfitTargetPoints (price went down = good)
//
// DEFAULT COMBO (user-specified):
//   F1 = "1000"  (rawString ends with 1,0,0,0)
//   F2 = "000"   (filter1Outcome ends with 0,0,0)
//   Stop = 20pt, Target = 40pt
//
// ============================================================================
// CONNECTION INTERRUPT / RESUME (same as v4)
// ============================================================================
// On startup, reads its own most-recent log file and decides FRESH vs RESUME.
// See StartupDecideAndLoad() for full gap logic.
//
// ============================================================================
// TRADING DAY BOUNDARY
// ============================================================================
// rawString resets at 3:00 PM PT each trading day (matches research).
// The qty rule (sessionRealOutcome) also resets daily.
// realTradeOutcome and realLossesInARow are cumulative.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Scalper_Renko_SHORT_Layer2 : Strategy
    {
        // ── strategy lifecycle ────────────────────────────────────────────────
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private int      barCount      = 0;   // processed Renko bars

        // ── pipeline strings ─────────────────────────────────────────────────
        private StringBuilder rawString         = new StringBuilder(); // Layer 0: all bars
        private StringBuilder filter1Outcome    = new StringBuilder(); // Layer 1: after F1 match
        private StringBuilder realTradeOutcome  = new StringBuilder(); // real money trade results

        // ── pipeline state ───────────────────────────────────────────────────
        private bool isArmed             = false;
        private bool waitingForF1Outcome = false;
        private bool nextIsMoney         = false;

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

        // ── active log file path ──────────────────────────────────────────────
        private string activeLogFilePath = null;

        // ── qty multiplier table ──────────────────────────────────────────────
        // Longest-tail match wins (see CalcQty). Patterns are read against
        // sessionRealOutcome (real trade W/L only; 1=win, 0=loss), NOT bricks.
        // Current scheme = capped loss-ratchet on CONSECUTIVE losses:
        //   "00"  (2 losses) -> x2
        //   "000" (3 losses) -> x3, and stays x3 for 4+ losses (tail still ends 000)
        // Note: no leading "1", so this escalates on a loss run from the day's open
        // too (not only after a win). No x0 skip lines, so every armed trade is real
        // and the breaker counts every loss (MaxRealLossInARow must be >= 4 for the
        // x3 line to ever fire).
        private static readonly (string pattern, int multiplier)[] QtyMultiplierTable =
        {
            ("00",  2),
            ("000", 3),
        };

        private int currentQty = 1;
        private string suppressReason = null;

        // ── per-day qty session ──────────────────────────────────────────────
        private StringBuilder sessionRealOutcome = new StringBuilder();
        private int sessionDayKey = -1;
        private int currentTradingDayKey = -1;

        // ── shutdown ─────────────────────────────────────────────────────────
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "SR_Entry";

        // ── previous bar tracking (for Renko bit) ────────────────────────────
        private int prevBarBit = -1;  // -1 = uninitialized, 0 = green, 1 = red

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Renko-based SHORT Layer-2 strategy. Each Renko bar = one bit. "
                            + "Green=0 (up), Red=1 (down). Chases '1's (red bars). "
                            + "Reconnect-survival via own log file.";
                Name        = "Scalper_Renko_SHORT_Layer2";

                Calculate                    = Calculate.OnBarClose;   // KEY: OnBarClose for Renko
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
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

                // ── defaults ─────────────────────────────────────────────────
                EnableTradingHours   = true;
                TradingStartHour     = 9;       // 09:30 ET
                TradingStartMinute   = 30;
                TradingEndHour       = 11;      // 11:30 ET
                TradingEndMinute     = 30;
                StrategyLifeMinutes  = 1440;    // 24h
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 20;      // user-specified
                ProfitTargetPoints   = 40;      // user-specified
                EnableTrailingStop   = false;
                TrailDistancePoints  = 10;
                EnableRealOrder      = false;   // observation only until flipped
                Filter1Pattern       = "1000";  // user-specified
                Filter2Pattern       = "000";   // user-specified
                BaseQuantity         = 1;
                EnableQtyIncrement   = false;
                MaxTotalBarCount     = 100000;  // max Renko bars to process
                MaxRealLossInARow    = 4;       // breaker (>=4 so qty x3 line can fire)

                // RESUME across a reconnect is DISABLED by default for Renko: the
                // gap tolerance is measured in minutes but the pipeline advances in
                // BRICKS, and a fast move can print many bricks in a few minutes.
                // There is no reliable way to know how many bricks were missed during
                // a disconnect, so RESUMING risks appending live bricks onto a holed
                // string. Fresh-start re-warms from the day's live bricks (the pipeline
                // resets daily anyway, so the warm-up cost is bounded to one day).
                // Flip to true ONLY if you have validated brick continuity across your
                // own reconnect pattern.
                AllowLogResume       = false;

                LogFolder            = @"C:\temp";
                LogBaseName          = "scalper_Renko_SHORT_Layer2";

                GapToleranceMinutes  = 7;
                GapCeilingHours      = 4;
            }
            else if (State == State.Configure)
            {
                if (EnableTrailingStop)
                    SetTrailStop(ENTRY_SIGNAL, CalculationMode.Ticks,
                        (int)Math.Round(TrailDistancePoints / TickSize), false);
                else
                    SetStopLoss(ENTRY_SIGNAL, CalculationMode.Ticks,
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

        // =====================================================================
        // OnBarUpdate — CORE: Renko bar close -> bit -> pipeline -> maybe trade
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;
            if (!lifeStarted) return;

            // Must have at least 1 previous bar to compare
            if (CurrentBar < 1) return;

            if (disabledSelf || pendingFlatten)
            {
                ProcessShutdown();
                return;
            }

            // If real position is open, let bracket handle it via OnExecutionUpdate
            // We still process the bar for pipeline (rawString grows)
            // but we don't start a new trade while one is open
            bool hasOpenPosition = (Position.MarketPosition == MarketPosition.Short);

            // ── lifecycle checks ─────────────────────────────────────────────
            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached");
                return;
            }

            if (barCount >= MaxTotalBarCount)
            {
                BeginShutdown("MaxTotalBarCount (" + MaxTotalBarCount + ") reached");
                return;
            }

            if (realLossesInARow >= MaxRealLossInARow)
            {
                BeginShutdown("MaxRealLossInARow (" + MaxRealLossInARow
                    + ") reached. realLossesInARow=" + realLossesInARow);
                return;
            }

            // NOTE: no time-throttle here. With Calculate.OnBarClose this method
            // fires exactly once per CLOSED Renko brick, and EVERY brick must be
            // recorded or rawString develops a hole that silently corrupts the
            // filter pipeline. (The old CheckIntervalSeconds gate dropped bricks
            // whenever two closed within the interval — removed.)

            // ── trading day rollover ─────────────────────────────────────────
            CheckTradingDayRollover();

            // =====================================================================
            // STEP 1: DETERMINE BIT FROM RENKO BAR
            // =====================================================================
            // Direction is derived from THIS brick's close vs the PREVIOUS brick's
            // close — NOT Close[0] vs Open[0]. NinjaTrader's native Renko fabricates
            // the OPEN of reversal bricks for cosmetic reasons ("the open is not
            // real"), so Close-vs-Open can mislabel a reversal brick and flip the
            // foundational bit. Consecutive Renko closes differ by exactly one brick
            // size, so close-vs-close gives the true build direction.
            //   Close rose  = brick built UP   = green = bit '0' (loss for SHORT)
            //   Close fell  = brick built DOWN = red   = bit '1' (win  for SHORT)
            int bit;
            if (Close[0] > Close[1])
                bit = 0;   // up brick, bad for short
            else if (Close[0] < Close[1])
                bit = 1;   // down brick, good for short
            else
            {
                // Equal closes should not occur in valid Renko (data anomaly).
                // Carry the previous brick's direction rather than inject a bogus
                // bit or a hole; log it so the anomaly is visible.
                bit = (prevBarBit >= 0) ? prevBarBit : 1;
                DiagLog("[RENKO ANOMALY] Close[0]==Close[1] (unexpected) -> carrying prev bit=" + bit);
            }

            barCount++;
            prevBarBit = bit;

            DiagLog(string.Format("[RENKO BAR #{0}] Close={1:F2} PrevClose={2:F2} -> bit={3} ({4})",
                barCount, Close[0], Close[1], bit, bit == 1 ? "RED/down" : "GREEN/up"));

            // =====================================================================
            // STEP 2: UPDATE PIPELINE
            // =====================================================================
            // Same logic as original, but bit comes from Renko bar instead of slice
            UpdatePipeline(bit);

            // =====================================================================
            // STEP 3: CANONICAL PER-BRICK LOG ROW  (resume + Python verification)
            // =====================================================================
            // A data row is written for EVERY brick — not just trades. This is what
            // makes the log a faithful bit-for-bit mirror of rawString, which the
            // RESUME path and any Python re-check both depend on. (Earlier this row
            // was only written on trades, so the log skipped most bricks and a resume
            // would restore a stale string.)
            //   side = WOULDBE_TRADE  -> pipeline armed AND F1 matched this brick
            //   side = FAKE_Short     -> ordinary observation brick
            //   win_loss_bit column   -> the RAW brick bit (0=up/green, 1=down/red)
            // Real orders write their own supplementary rows (Short_ENTRY at entry,
            // Short at close, OBS_* if a guard suppresses) — those carry the fill
            // prices and the real outcome bit. Only side=="Short" close rows are read
            // back as real-trade outcomes.
            string barSide = nextIsMoney ? "WOULDBE_TRADE" : "FAKE_Short";
            WriteLogRowBar(bit, barSide);

            // =====================================================================
            // STEP 4: ACT ON THE TRADE TRIGGER
            // =====================================================================
            if (nextIsMoney && EnableRealOrder && !hasOpenPosition)
            {
                nextIsMoney = false;  // consume the trigger
                TryOpenRealTrade();
            }
            else if (nextIsMoney)
            {
                // Pipeline fired, but no real order: either observation mode is on,
                // or a position is already open. Bit is already recorded above.
                DiagLog(hasOpenPosition
                    ? "[WOULDBE TRADE] fired but a position is already open; no order."
                    : "[WOULDBE TRADE] fired but EnableRealOrder=false; no order.");
                nextIsMoney = false;
            }
        }

        // =====================================================================
        // TryOpenRealTrade — guards + entry
        // =====================================================================
        private void TryOpenRealTrade()
        {
            suppressReason = null;
            double refPrice = GetCurrentAsk();
            if (refPrice <= 0)
            {
                DiagLog("[TRADE ABORT] cannot get valid ask price");
                return;
            }

            // ── GUARD 1: TRADING HOURS ───────────────────────────────────────
            if (EnableTradingHours && !WithinTradingHours())
            {
                DiagLog(string.Format(
                    "[OUTSIDE HOURS] Trade suppressed (outside {0:00}:{1:00}-{2:00}:{3:00} NY).",
                    TradingStartHour, TradingStartMinute, TradingEndHour, TradingEndMinute));
                suppressReason = "OBS_OUTSIDE_HOURS";
                WriteLogRowObs(refPrice, "OBS_OUTSIDE_HOURS");
                return;
            }

            // ── GUARD 2: ACCOUNT BUSY ────────────────────────────────────────
            if (AccountBusyOnThisInstrument())
            {
                DiagLog("[ACCOUNT BUSY] Trade suppressed (another position/order active).");
                suppressReason = "OBS_ACCOUNT_BUSY";
                WriteLogRowObs(refPrice, "OBS_ACCOUNT_BUSY");
                return;
            }

            // ── GUARD 3: QTY RULE ────────────────────────────────────────────
            currentQty = CalcQty();
            if (currentQty <= 0)
            {
                DiagLog(string.Format(
                    "[QTY SKIP] qty rule returned 0 -> no trade. sessionReal={0}",
                    sessionRealOutcome.ToString()));
                suppressReason = "OBS_QTY_SKIP";
                WriteLogRowObs(refPrice, "OBS_QTY_SKIP");
                return;
            }

            // ── ENTER REAL SHORT ─────────────────────────────────────────────
            awaitingClose     = true;
            entryInFlight     = true;
            workingEntryOrder = null;

            double entryPrice = UseMarketEntry ? refPrice
                : Instrument.MasterInstrument.RoundToTickSize(refPrice + LimitOffsetPoints);
            double stopPrice   = Instrument.MasterInstrument.RoundToTickSize(entryPrice + StopLossPoints);
            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(entryPrice - ProfitTargetPoints);

            try
            {
                if (UseMarketEntry)
                {
                    workingEntryOrder = EnterShort(currentQty, ENTRY_SIGNAL);
                    DiagLog(string.Format(
                        "MONEY TRADE #{0} MARKET qty={1} entry~{2:F2} stop={3:F2} target={4:F2} | raw={5} | f1={6} | real={7}",
                        barCount, currentQty, entryPrice, stopPrice, targetPrice,
                        TailOf(rawString, 12), TailOf(filter1Outcome, 12), TailOf(realTradeOutcome, 12)));
                }
                else
                {
                    workingEntryOrder = EnterShortLimit(0, true, currentQty, entryPrice, ENTRY_SIGNAL);
                    DiagLog(string.Format(
                        "MONEY TRADE #{0} LIMIT qty={1} limit={2:F2} | raw={3} | f1={4} | real={5}",
                        barCount, currentQty, entryPrice,
                        TailOf(rawString, 12), TailOf(filter1Outcome, 12), TailOf(realTradeOutcome, 12)));
                }

                WriteLogRowObs(entryPrice, "Short_ENTRY", currentQty);
            }
            catch (Exception ex)
            {
                DiagLog("TryOpenRealTrade error: " + ex.Message);
                awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
            }
        }

        // =====================================================================
        // UpdatePipeline — identical to original v4
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
                rawString.Length, TailOf(rawString, 12),
                filter1Outcome.Length, TailOf(filter1Outcome, 12),
                waitingForF1Outcome, isArmed, nextIsMoney, realLossesInARow));
        }

        // =====================================================================
        // OnExecutionUpdate — bracket close handling
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

            // ── recognized bracket exit ───────────────────────────────────────
            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("StopCancelClose", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            // ── EOD / forced flatten ──────────────────────────────────────────
            bool isOurForceClose = oName.IndexOf("SR_ForceClose", StringComparison.OrdinalIgnoreCase) >= 0
                                 || oName.IndexOf("SR_Flatten",    StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitFill = !(oName == ENTRY_SIGNAL);

            if (isFull && isExitFill && !isStopFill && !isTargetFill && !isOurForceClose
                && Position.MarketPosition == MarketPosition.Flat
                && awaitingClose)
            {
                int bit = 0;
                DiagLog(string.Format(
                    "[EOD FLATTEN] Trade closed by session-close/forced exit (name='{0}'). "
                    + "Recording as LOSS (bit=0) conservatively.", oName));

                realTradeOutcome.Append("0");
                RecordSessionOutcome(0);
                realLossesInARow++;
                DiagLog("[REAL LOSS eod] realLossesInARow=" + realLossesInARow);

                awaitingClose = false; entryInFlight = false; workingEntryOrder = null;

                double logFillPrice = entryFillPrice;
                int    logFillQty   = entryFillQty;
                entryFillPrice = 0.0; entryFillQty = 0;

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

                    DiagLog(string.Format("MONEY TRADE CLOSED {0}: entry={1:F2} exit={2:F2} qty={3} pnl={4:0.00} bit={5}",
                        isStopFill ? "STOP" : "TARGET", entryFillPrice, price, entryFillQty, pnl, bit));

                    realTradeOutcome.Append(bit.ToString());
                    RecordSessionOutcome(bit);
                    if (bit == 0) { realLossesInARow++; DiagLog("[REAL LOSS] realLossesInARow=" + realLossesInARow); }
                    else { if (realLossesInARow > 0) DiagLog("[REAL WIN] reset " + realLossesInARow + "->0"); realLossesInARow = 0; }

                    awaitingClose = false; entryInFlight = false; workingEntryOrder = null;

                    double logFillPrice = entryFillPrice;
                    int    logFillQty   = entryFillQty;
                    entryFillPrice = 0.0; entryFillQty = 0;

                    WriteLogRow(logFillPrice, price, pnl, bit, logFillQty, time);
                }
            }
        }

        // =====================================================================
        // OnOrderUpdate
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
                    entryInFlight = false; awaitingClose = false; workingEntryOrder = null;
                    entryFillPrice = 0.0; entryFillQty = 0;
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
        // StartupDecideAndLoad — FRESH vs RESUME (identical to v4)
        // =====================================================================
        private void StartupDecideAndLoad()
        {
            isArmed             = false;
            waitingForF1Outcome = false;
            nextIsMoney         = false;
            realLossesInARow    = 0;
            currentQty          = BaseQuantity;
            rawString.Clear();
            filter1Outcome.Clear();
            realTradeOutcome.Clear();

            string latest = FindMostRecentLogFile();
            bool   doFresh = true;
            string reason  = "no prior log file";
            DateTime lastBitLocal = DateTime.MinValue;

            // RESUME is off by default for Renko (see AllowLogResume note in
            // SetDefaults). When off, always start a fresh file + empty pipeline and
            // re-warm from live bricks — no attempt to stitch across a brick gap we
            // cannot measure.
            if (!AllowLogResume)
            {
                doFresh = true;
                reason  = "AllowLogResume=false — Renko fresh start (brick gaps are not measurable in minutes)";
            }
            else if (!string.IsNullOrEmpty(latest))
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
                        realTradeOutcome.Append(snap.realTradeOutcome);
                        realLossesInARow = CountTodaysTrailingLosses(latest, CurrentTradingDayKey());
                        ReDerivePipelineFlags();
                        activeLogFilePath = latest;
                        reason = gd.reason;

                        int snapKey = TradingDayKeyOfLocal(snap.lastBitLocal);
                        int nowKey  = CurrentTradingDayKey();
                        if (snapKey > 0 && nowKey > 0 && snapKey != nowKey)
                        {
                            DiagLog(string.Format(
                                "[RESUME ACROSS DAY BOUNDARY] snapshot day {0}, now {1}. "
                                + "Discarding pipeline, starting EMPTY.", snapKey, nowKey));
                            rawString.Clear();
                            filter1Outcome.Clear();
                            isArmed = false;
                            waitingForF1Outcome = false;
                            nextIsMoney = false;
                        }
                        currentTradingDayKey = nowKey;

                        sessionRealOutcome.Clear();
                        sessionRealOutcome.Append(ReadTodaysRealOutcomes(latest, nowKey));
                        sessionDayKey = nowKey;
                        DiagLog("[QTY RESUME] rebuilt sessionReal='" + sessionRealOutcome.ToString() + "'");
                    }
                }
            }

            if (doFresh)
            {
                activeLogFilePath = BuildNewLogFilePath();
                EnsureLogHeader(activeLogFilePath);
                DiagLog("[FRESH START] " + reason + " | new log=" + activeLogFilePath);
            }
            else
            {
                DiagLog("[RESUME] " + reason + " | log=" + activeLogFilePath
                    + " | rawLen=" + rawString.Length + " f1Len=" + filter1Outcome.Length
                    + " real=" + realTradeOutcome.ToString() + " lossRow=" + realLossesInARow
                    + " | last=" + lastBitLocal.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            DiagLog(Name + " ready (Renko SHORT). EnableRealOrder=" + EnableRealOrder
                + ", F1=[" + Filter1Pattern + "], F2=[" + Filter2Pattern + "]"
                + ", Stop=" + StopLossPoints + "pt, Target=" + ProfitTargetPoints + "pt");
        }

        // =====================================================================
        // Gap decision (identical to v4)
        // =====================================================================
        private class GapDecision { public bool fresh; public string reason; }

        private GapDecision DecideGap(DateTime lastBitLocal, DateTime nowLocal)
        {
            var d = new GapDecision();
            if (WeekendInGap(lastBitLocal, nowLocal))
            {
                d.fresh = true;
                d.reason = "weekend in gap";
                return d;
            }
            double wallHours = (nowLocal - lastBitLocal).TotalHours;
            if (wallHours > GapCeilingHours)
            {
                d.fresh = true;
                d.reason = "wall gap " + wallHours.ToString("F1") + "h > ceiling " + GapCeilingHours + "h";
                return d;
            }
            int openMin = MarketOpenMinutesInGap(lastBitLocal, nowLocal);
            if (openMin > GapToleranceMinutes)
            {
                d.fresh = true;
                d.reason = "open min " + openMin + " > tolerance " + GapToleranceMinutes;
                return d;
            }
            d.fresh = false;
            d.reason = "gap small: " + openMin + " open min, " + wallHours.ToString("F2") + "h wall";
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
                    DateTime next = t.AddMinutes(1);
                    if (IsMarketOpenAt(t)) openCount++;
                    t = next;
                }
                return openCount;
            }
            catch
            {
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
            catch { return true; }
        }

        // =====================================================================
        // CalcQty (identical to v4)
        // =====================================================================
        private int CalcQty()
        {
            if (!EnableQtyIncrement) return BaseQuantity;
            string outcome = sessionRealOutcome.ToString();
            if (outcome.Length == 0) return BaseQuantity;
            int bestLen = -1, bestMult = 1;
            bool matched = false;
            foreach (var entry in QtyMultiplierTable)
            {
                if (entry.pattern.Length > bestLen && TailMatches(outcome, entry.pattern))
                {
                    bestLen = entry.pattern.Length;
                    bestMult = entry.multiplier;
                    matched = true;
                }
            }
            if (!matched) return BaseQuantity;
            int qty = BaseQuantity * bestMult;
            DiagLog(string.Format("[QTY] match len={0} -> x{1} -> qty={2} (session={3})",
                bestLen, bestMult, qty, outcome));
            return qty;
        }

        // =====================================================================
        // Trading day key (3PM PT boundary) (identical to v4)
        // =====================================================================
        private TimeZoneInfo PacificZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch { try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { return null; } }
        }

        private int TradingDayKeyOfLocal(DateTime localTime)
        {
            try
            {
                TimeZoneInfo pt = PacificZone();
                DateTime ptTime = (pt == null) ? localTime
                    : TimeZoneInfo.ConvertTimeFromUtc(TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), TimeZoneInfo.Local), pt);
                DateTime d = (ptTime.TimeOfDay >= new TimeSpan(15, 0, 0)) ? ptTime.Date : ptTime.Date.AddDays(-1);
                return d.Year * 10000 + d.Month * 100 + d.Day;
            }
            catch { return -1; }
        }

        private int CurrentTradingDayKey() { return TradingDayKeyOfLocal(DateTime.Now); }

        // =====================================================================
        // CheckTradingDayRollover (identical to v4)
        // =====================================================================
        private void CheckTradingDayRollover()
        {
            int key = CurrentTradingDayKey();
            if (key < 0) return;
            if (currentTradingDayKey == -1) { currentTradingDayKey = key; return; }
            if (key != currentTradingDayKey)
            {
                DiagLog(string.Format(
                    "[DAY ROLLOVER] {0}->{1}. Clearing pipeline. realTradeOutcome KEPT.",
                    currentTradingDayKey, key));
                rawString.Clear();
                filter1Outcome.Clear();
                isArmed = false;
                waitingForF1Outcome = false;
                nextIsMoney = false;
                sessionRealOutcome.Clear();
                sessionDayKey = key;
                if (realLossesInARow > 0)
                    DiagLog("[BREAKER RESET] new day -> " + realLossesInARow + "->0");
                realLossesInARow = 0;
                currentTradingDayKey = key;
            }
        }

        private void RecordSessionOutcome(int bit)
        {
            int key = CurrentTradingDayKey();
            if (key != sessionDayKey)
            {
                if (sessionDayKey != -1)
                    DiagLog(string.Format("[QTY ROLL] day {0}->{1}, reset.", sessionDayKey, key));
                sessionDayKey = key;
                sessionRealOutcome.Clear();
            }
            sessionRealOutcome.Append(bit.ToString());
        }

        // =====================================================================
        // ReDerivePipelineFlags (identical to v4)
        // =====================================================================
        private void ReDerivePipelineFlags()
        {
            string raw = rawString.ToString();
            string f1str = filter1Outcome.ToString();
            isArmed = TailMatches(f1str, Filter2Pattern);
            waitingForF1Outcome = TailMatches(raw, Filter1Pattern);
            nextIsMoney = isArmed && TailMatches(raw, Filter1Pattern);
        }

        // =====================================================================
        // Helpers (identical to v4)
        // =====================================================================
        private string TailOf(StringBuilder sb, int n)
        {
            string s = sb.ToString();
            return s.Length <= n ? s : "..." + s.Substring(s.Length - n);
        }

        private static bool PatternHasWildcard(string pattern)
        {
            return pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
        }

        private static bool TailMatches(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || text.Length == 0) return false;
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
        // Log readers (identical to v4)
        // =====================================================================
        private string ReadTodaysRealOutcomes(string path, int todayKey)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                string[] lines = File.ReadAllLines(path);
                var rev = new List<char>();
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("timestamp")) continue;
                    string[] p = line.Split(',');
                    if (p.Length < 8) continue;
                    string sideCol = p[2].Trim();
                    string bitCol = p[7].Trim();
                    if (sideCol != "Short") continue;
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
            catch { return ""; }
        }

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

        // =====================================================================
        // File helpers (identical to v4)
        // =====================================================================
        private string BuildNewLogFilePath()
        {
            return Path.Combine(LogFolder, LogBaseName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
        }

        private string FindMostRecentLogFile()
        {
            try
            {
                if (!Directory.Exists(LogFolder)) return null;
                var files = Directory.GetFiles(LogFolder, LogBaseName + "_*.csv")
                    .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("-diagLog", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (files.Length == 0) return null;
                return files.OrderByDescending(p => File.GetLastWriteTime(p)).First();
            }
            catch { return null; }
        }

        private class PipelineSnapshot
        {
            public bool valid;
            public DateTime lastBitLocal;
            public string rawString = "";
            public string filter1Outcome = "";
            public string realTradeOutcome = "";
        }

        private PipelineSnapshot ReadLastSnapshot(string path)
        {
            try
            {
                var snap = new PipelineSnapshot { valid = false };
                string[] lines = File.ReadAllLines(path);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("timestamp")) continue;
                    string[] p = line.Split(',');
                    if (p.Length < 11) continue;
                    string ts = p[0].Trim();
                    string raw = p[8].Trim();
                    string f1 = p[9].Trim();
                    string real = p[10].Trim();
                    DateTime tparsed;
                    if (!DateTime.TryParse(ts,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out tparsed))
                        continue;
                    snap.lastBitLocal = tparsed;
                    snap.rawString = raw;
                    snap.filter1Outcome = f1;
                    snap.realTradeOutcome = real;
                    snap.valid = raw.Length > 0;
                    return snap;
                }
                return snap;
            }
            catch { return null; }
        }

        // =====================================================================
        // Logging (identical to v4)
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
                        "timestamp(machine_local_time),bar_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,rawString,filter1Outcome,realTradeOutcome\n");
                }
            }
            catch { }
        }

        private bool logWriteFailed = false;

        private void SafeAppend(string path, string text)
        {
            if (string.IsNullOrEmpty(path))
            {
                DiagLog("[LOG ERROR] no path — row DROPPED: " + text.TrimEnd());
                logWriteFailed = true;
                return;
            }
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs)) { sw.Write(text); }
                    return;
                }
                catch (IOException)
                {
                    if (attempt == 5) break;
                    System.Threading.Thread.Sleep(20 * attempt);
                }
                catch (Exception ex)
                {
                    DiagLog("[LOG ERROR] " + ex.Message + " — DROPPED: " + text.TrimEnd());
                    logWriteFailed = true;
                    return;
                }
            }
            DiagLog("[LOG ERROR] locked after 5 tries — DROPPED: " + text.TrimEnd());
            logWriteFailed = true;
        }

        private void WriteLogRow(double entryPrice, double exitPrice, double pnl, int bit, int qty, DateTime time)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10}\n",
                    DateTime.Now, barCount, "Short", qty, entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch { logWriteFailed = true; }
        }

        // Canonical per-brick row: written for EVERY closed brick so the log mirrors
        // rawString bit-for-bit. win_loss_bit column holds the RAW brick bit.
        private void WriteLogRowBar(int rawBit, string side)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}\n",
                    DateTime.Now, barCount, side, 0, 0, 0, 0, rawBit,
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch { logWriteFailed = true; }
        }

        private void WriteLogRowObs(double price, string side, int qty)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}\n",
                    DateTime.Now, barCount, side, qty, price, 0, 0, "-",
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch { logWriteFailed = true; }
        }

        private void WriteLogRowObs(double price, string reason)
        {
            try
            {
                string row = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}\n",
                    DateTime.Now, barCount, reason, 0, price, 0, 0, "-",
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                SafeAppend(activeLogFilePath, row);
            }
            catch { logWriteFailed = true; }
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
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(diagPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (var sw = new StreamWriter(fs)) { sw.Write(line + "\n"); }
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
        // Shutdown (identical to v4)
        // =====================================================================
        private void BeginShutdown(string reason)
        {
            if (disabledSelf || pendingFlatten) return;
            pendingReason = reason;
            pendingFlatten = true;
            DiagLog("Shutdown: " + reason + " | bars=" + barCount + " | lossRow=" + realLossesInARow);
        }

        private void ProcessShutdown()
        {
            if (entryInFlight && workingEntryOrder != null)
            {
                var os = workingEntryOrder.OrderState;
                if (os == OrderState.Working || os == OrderState.Accepted || os == OrderState.Submitted)
                {
                    try { CancelOrder(workingEntryOrder); }
                    catch { entryInFlight = false; awaitingClose = false; workingEntryOrder = null; }
                }
                return;
            }
            if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight)
            {
                FinalizeTermination();
                return;
            }
            if (Position.MarketPosition == MarketPosition.Short)
            {
                try { ExitShort(Math.Abs(Position.Quantity), "SR_Flatten", ENTRY_SIGNAL); }
                catch { }
            }
        }

        private void FinalizeTermination()
        {
            if (disabledSelf) return;
            disabledSelf = true;
            pendingFlatten = false;
            DiagLog("TERMINATED. Reason: " + pendingReason
                + " | bars=" + barCount + " | lossRow=" + realLossesInARow
                + " | raw=" + rawString.ToString()
                + " | f1=" + filter1Outcome.ToString()
                + " | real=" + realTradeOutcome.ToString());
            try { SetState(State.Terminated); } catch { }
        }

        // =====================================================================
        // Account / Hours helpers (identical to v4)
        // =====================================================================
        private bool AccountBusyOnThisInstrument()
        {
            try
            {
                if (Account == null) return true;
                lock (Account.Positions)
                {
                    foreach (Position p in Account.Positions)
                    {
                        if (p.Instrument == Instrument && p.MarketPosition != MarketPosition.Flat)
                            return true;
                    }
                }
                lock (Account.Orders)
                {
                    foreach (Order ord in Account.Orders)
                    {
                        if (ord.Instrument == Instrument
                            && (ord.OrderState == OrderState.Working
                                || ord.OrderState == OrderState.Accepted
                                || ord.OrderState == OrderState.Submitted
                                || ord.OrderState == OrderState.PartFilled))
                            return true;
                    }
                }
                return false;
            }
            catch { return true; }
        }

        private bool WithinTradingHours()
        {
            if (!EnableTradingHours) return true;
            TimeZoneInfo et;
            try { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { return true; } }
            DateTime nyNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et);
            int curMin = nyNow.Hour * 60 + nyNow.Minute;
            int startMin = TradingStartHour * 60 + TradingStartMinute;
            int endMin = TradingEndHour * 60 + TradingEndMinute;
            return curMin >= startMin && curMin <= endMin;
        }

        // =====================================================================
        // Properties
        // =====================================================================
        #region Properties

        [Display(Name = "Template: CME US Index Futures ETH",
            Description = "REQUIRED. Set data series Trading Hours template.",
            Order = 1, GroupName = "0. REQUIRED SETUP")]
        [ReadOnly(true)]
        public string TemplateReminder { get { return "Set data series Trading Hours = CME US Index Futures ETH"; } set { } }

        [Display(Name = "Enable EOD break on data series",
            Description = "Keep IsExitOnSessionCloseStrategy ON.",
            Order = 2, GroupName = "0. REQUIRED SETUP")]
        [ReadOnly(true)]
        public string EodReminder { get { return "EOD flatten ON; recorded as loss"; } set { } }

        [Display(Name = "IMPORTANT — interrupt = fresh start",
            Description = "Any disable/enable, disconnect, or interrupt starts a BRAND-NEW pipeline "
                        + "(empty rawString, isArmed = false) and re-warms from live bricks. Pre-interrupt "
                        + "arming and context are discarded — this is intentional and safe for Renko. "
                        + "Prefer changing parameters at the 3:00 PM PT daily reset or before the open, "
                        + "so you are not throwing away mid-session arming.",
            Order = 3, GroupName = "0. REQUIRED SETUP")]
        [ReadOnly(true)]
        public string InterruptReminder { get { return "Any interrupt / disconnect => BRAND-NEW start (pipeline re-warms from live bricks)"; } set { } }

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

        // Hidden: resume is disabled by default (interrupt = fresh start), so these
        // three are dormant. Kept in code (Browsable(false)) so the resume path still
        // compiles and can be re-enabled in source if ever validated.
        [NinjaScriptProperty]
        [Browsable(false)]
        [Range(1, int.MaxValue)]
        [Display(Name = "Gap tolerance (market-open min)", Order = 3, GroupName = "2. Timing")]
        public int GapToleranceMinutes { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Range(1, 48)]
        [Display(Name = "Gap ceiling (wall-clock hours)", Order = 4, GroupName = "2. Timing")]
        public int GapCeilingHours { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Allow log resume (advanced)", Order = 5, GroupName = "2. Timing")]
        public bool AllowLogResume { get; set; }

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

        // Hidden: a trailing stop breaks this strategy's core invariant. The whole
        // design relies on stop = 1 brick (20pt) and target = 2 bricks (40pt), so a
        // trade always resolves in exactly one brick and brick color == trade outcome.
        // A trailing stop would move the stop off the brick grid and desync
        // filter1Outcome from the real fill. Kept fixed-stop only.
        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Enable Trailing Stop", Order = 3, GroupName = "4. Bracket")]
        public bool EnableTrailingStop { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Trail distance (points)", Order = 4, GroupName = "4. Bracket")]
        public double TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Real Order", Order = 1, GroupName = "5. Filter & Order",
            Description = "FALSE = observation only. TRUE = real order fires when armed+F1 match.")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 1 Pattern", Order = 2, GroupName = "5. Filter & Order",
            Description = "Tail pattern on rawString. Wildcards: '*'=0+, '?'=1+. Default: 1000")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 Pattern", Order = 3, GroupName = "5. Filter & Order",
            Description = "Tail pattern on filter1Outcome. Default: 000")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity", Order = 1, GroupName = "6. Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Qty Increment", Order = 2, GroupName = "6. Quantity")]
        public bool EnableQtyIncrement { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Total Bar Count", Order = 1, GroupName = "7. Limits")]
        public int MaxTotalBarCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Real Loss In A Row", Order = 2, GroupName = "7. Limits")]
        public int MaxRealLossInARow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Folder", Order = 1, GroupName = "8. Logging")]
        public string LogFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Base Name", Order = 2, GroupName = "8. Logging")]
        public string LogBaseName { get; set; }

        #endregion
    }
}
