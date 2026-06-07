#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// scalper_LONGrepeat_Layer2
//
// Based on scalper_LONGrepeat_ReviewIfQtyMorethan1 (Patch 7)
//
// LAYER 2 UPGRADE
// ---------------
// Pipeline:
//   Layer 0 (raw outcomeHistory)
//       ↓  Filter1Pattern match on Layer 0 tail
//   Layer 1 (filter1History)
//       ↓  Filter2Pattern match on Layer 1 tail
//   REAL trade fires
//
// LIMITS UPGRADE
// --------------
// Two separate limits with clear labels:
//
//   "Max Fake + Real Trade Count" (default 100)
//       Counts ALL trades (fake + real combined).
//       Controls session activity / length.
//       Uses: ordersPlaced counter.
//
//   "Max Real Loss In A Row" (default 3)
//       Counts REAL trade losses only, back to back.
//       Controls real money drawdown risk.
//       Uses: realLossesInARow counter, resets to 0 on any real WIN.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_LONGrepeat_Layer2 : Strategy
    {
        // ---- strategy lifecycle ----
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private DateTime lastCheckTime = DateTime.MinValue;
        private int      ordersPlaced  = 0;      // fake + real combined

        // Layer 0 — raw market outcome string (every fake + real trade)
        private StringBuilder outcomeHistory = new StringBuilder();

        // Layer 1 — filtered outcome string (appended when Filter1Pattern matches Layer 0 tail)
        private StringBuilder filter1History = new StringBuilder();

        // sequencing flags
        private bool entryInFlight = false;
        private bool awaitingClose = false;

        // [PATCH 3] Store entry order reference for cancel-on-shutdown
        private Order workingEntryOrder = null;

        // captured at entry fill for PnL calculation
        private double entryFillPrice = 0.0;
        private int    entryFillQty   = 0;

        // ---- Fake order mode state ----
        private bool   inFakeTrade      = false;
        private double fakeEntryPrice   = 0.0;
        private double fakeStopPrice    = 0.0;
        private double fakeTargetPrice  = 0.0;
        private bool   waitingForL1     = false;  // F1 matched L0 → next raw digit → L1
        private bool   waitingForTrade   = false;  // F2 matched L1 → next L1 digit → REAL TRADE
        private bool   waitingForExpected = false;  // FIRE just happened → next fake trade = expected outcome
        private bool   enableRealOrder  = true;
        private List<string> compiledFilter1 = null;
        private List<string> compiledFilter2 = null;

        // ---- Real loss streak (real trades only) ----
        private int realLossesInARow = 0;

        // shutdown control
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "LR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "LONG scalper with two-layer pattern filter. "
                             + "Pipeline: Layer0 (raw) → Filter1Pattern → Layer1 → Filter2Pattern → REAL trade. "
                             + "Uses NT native SetTrailStop to avoid OCO ID reuse errors. "
                             + "Max Fake + Real Trade Count controls session length (all trades). "
                             + "Max Real Loss In A Row controls real money drawdown (real trades only).";
                Name         = "scalper_LONGrepeat_Layer2";

                Calculate                    = Calculate.OnEachTick;
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

                // ---- defaults ----
                EnableTradingHours      = false;
                TradingStartHour        = 9;
                TradingStartMinute      = 30;
                TradingEndHour          = 16;
                TradingEndMinute        = 0;
                StrategyLifeMinutes     = 3;
                CheckIntervalSeconds    = 1;
                UseMarketEntry          = true;
                LimitOffsetPoints       = 5;
                StopLossPoints          = 10;
                ProfitTargetPoints      = 10;
                EnableTrailingStop      = true;
                TrailDistancePoints     = 10;
                EnableRealOrder         = false;
                Filter1Pattern          = "01";
                Filter2Pattern          = "01";
                BaseQuantity            = 1;
                MaxTotalTradeCount      = 100;   // fake + real combined
                MaxRealLossInARow       = 3;     // real trades only
                LogFilePath             = @"C:\temp\scalper_LONGrepeat_Layer2_log.csv";
            }
            else if (State == State.Configure)
            {
                if (EnableTrailingStop)
                {
                    SetTrailStop  (ENTRY_SIGNAL, CalculationMode.Ticks,
                        (int)Math.Round(TrailDistancePoints / TickSize), false);
                }
                else
                {
                    SetStopLoss   (ENTRY_SIGNAL, CalculationMode.Ticks,
                        (int)Math.Round(StopLossPoints / TickSize), false);
                }
                SetProfitTarget(ENTRY_SIGNAL, CalculationMode.Ticks,
                    (int)Math.Round(ProfitTargetPoints / TickSize));
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    strategyStartUtc  = DateTime.UtcNow;
                    lifeStarted       = true;
                    compiledFilter1   = ParsePatternList(Filter1Pattern);
                    compiledFilter2   = ParsePatternList(Filter2Pattern);
                    waitingForL1      = false;
                    waitingForTrade   = false;
                    waitingForExpected = false;
                    realLossesInARow  = 0;
                    enableRealOrder   = EnableRealOrder;
                    EnsureLogHeader();
                    DiagLog(Name + " enabled. Life=" + StrategyLifeMinutes
                        + "min, MaxTotalTradeCount=" + MaxTotalTradeCount
                        + ", MaxRealLossInARow=" + MaxRealLossInARow
                        + ", Qty=" + BaseQuantity
                        + ", Stop=" + StopLossPoints + "pt"
                        + ", Target=" + ProfitTargetPoints + "pt"
                        + ", EnableTrailingStop=" + EnableTrailingStop
                        + (EnableTrailingStop ? ", TrailDist=" + TrailDistancePoints + "pt" : "")
                        + ", EnableRealOrder=" + EnableRealOrder
                        + ", Filter1=[" + Filter1Pattern + "]"
                        + ", Filter2=[" + Filter2Pattern + "]"
                        + (EnableRealOrder ? "" : " (observation only)"));
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

            // Fake trade tick-by-tick resolution
            if (inFakeTrade)
            {
                CheckFakeTrade();
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;

            // ── Session life limit ───────────────────────────────────────────
            if ((nowUtc - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached");
                return;
            }

            // ── Max Fake + Real Trade Count (all trades) ─────────────────────
            if (ordersPlaced >= MaxTotalTradeCount)
            {
                if (awaitingClose || Position.MarketPosition != MarketPosition.Flat)
                    return;
                BeginShutdown("Max Fake + Real Trade Count (" + MaxTotalTradeCount + ") reached");
                return;
            }

            // ── Max Real Loss In A Row (real trades only) ────────────────────
            if (realLossesInARow >= MaxRealLossInARow)
            {
                if (awaitingClose || Position.MarketPosition != MarketPosition.Flat)
                    return;
                BeginShutdown("Max Real Loss In A Row (" + MaxRealLossInARow
                    + ") reached. realLossesInARow=" + realLossesInARow);
                return;
            }

            if ((DateTime.Now - lastCheckTime).TotalSeconds < CheckIntervalSeconds)
                return;
            lastCheckTime = DateTime.Now;

            if (!WithinTradingHours())
                return;

            if (!ReadyForNewEntry())
                return;

            PlaceEntry();
        }

        // =====================================================================
        // AppendOutcome — called after EVERY fake trade outcome
        // Implements the two-flag state machine matching apply_filter logic:
        //
        //   waitingForL1   : F1 just matched L0 tail → next raw digit → L1
        //   waitingForTrade: F2 just matched L1 tail → next L1 digit → REAL TRADE
        //
        // Flow per digit:
        //   1. Append bit to L0 always.
        //   2. If waitingForL1:
        //        append bit to L1 always.
        //        if waitingForTrade → fire REAL TRADE with this bit.
        //        check F2 on L1 tail → set waitingForTrade.
        //        clear waitingForL1.
        //   3. Else check F1 on L0 tail → set waitingForL1.
        // =====================================================================
        private void AppendOutcome(int bit)
        {
            // ── Layer 0 ──────────────────────────────────────────────────────
            outcomeHistory.Append(bit.ToString());
            string l0 = outcomeHistory.ToString();

            if (waitingForL1)
            {
                // This digit always goes to L1
                waitingForL1 = false;
                filter1History.Append(bit.ToString());
                string l1 = filter1History.ToString();

                if (waitingForTrade && EnableRealOrder)
                {
                    // ── REAL TRADE ────────────────────────────────────────────
                    // This L1 digit is the real trade outcome signal
                    // Fire the real entry now
                    waitingForTrade   = false;
                    waitingForExpected = true;  // next fake trade = expected outcome
                    DiagLog(string.Format(
                        "[FIRE] F2 armed. L1 digit='{0}' → placing REAL trade. L1={1}",
                        bit, l1));
                    PlaceRealEntry();
                }
                else if (waitingForTrade && !EnableRealOrder)
                {
                    waitingForTrade = false;
                    DiagLog(string.Format(
                        "[FIRE obs] F2 armed but EnableRealOrder=false. L1 digit='{0}' (observation only). L1={1}",
                        bit, l1));
                }
                else
                {
                    DiagLog(string.Format(
                        "[L1] L1 gets '{0}' → L1={1}", bit, l1));
                }

                // Check F2 on L1 tail
                if (MatchesTail(l1, compiledFilter2))
                {
                    waitingForTrade = true;
                    DiagLog(string.Format(
                        "[ARMED] F2 '{0}' matched L1 tail '{1}' → next L1 digit = REAL TRADE",
                        Filter2Pattern, l1.Length >= compiledFilter2[0].Length
                            ? l1.Substring(l1.Length - compiledFilter2[0].Length) : l1));
                }
            }
            else
            {
                // Check F1 on L0 tail
                if (MatchesTail(l0, compiledFilter1))
                {
                    waitingForL1 = true;
                    DiagLog(string.Format(
                        "[F1] F1 '{0}' matched L0 tail → next raw digit → L1",
                        Filter1Pattern));
                }
                else
                {
                    DiagLog(string.Format(
                        "[F1] no match. L0 tail='{0}'",
                        l0.Length >= 4 ? l0.Substring(l0.Length - 4) : l0));
                }
            }

            DiagLog(string.Format(
                "[STATE] L0({0})={1} | L1({2})={3} | wL1={4} | wTrade={5} | realLossRow={6}",
                outcomeHistory.Length, TailOf(outcomeHistory, 8),
                filter1History.Length, TailOf(filter1History, 8),
                waitingForL1, waitingForTrade, realLossesInARow));
        }

        // =====================================================================
        // PlaceRealEntry — fires a real LONG order immediately
        // Called from AppendOutcome when F2 armed and next L1 digit arrives
        // =====================================================================
        private void PlaceRealEntry()
        {
            if (awaitingClose || entryInFlight) 
            {
                DiagLog("[FIRE] Cannot place real entry — already in trade. Signal skipped.");
                return;
            }
            if (!ReadyForNewEntry())
            {
                DiagLog("[FIRE] Cannot place real entry — ReadyForNewEntry=false. Signal skipped.");
                return;
            }

            int qty = BaseQuantity;
            ordersPlaced++;
            entryInFlight     = true;
            awaitingClose     = true;
            workingEntryOrder = null;

            try
            {
                if (UseMarketEntry)
                {
                    workingEntryOrder = EnterLong(qty, ENTRY_SIGNAL);
                    DiagLog(string.Format("REAL ENTRY #{0} MARKET qty={1}", ordersPlaced, qty));
                }
                else
                {
                    double limitPx = GetCurrentBid() - LimitOffsetPoints;
                    limitPx = Instrument.MasterInstrument.RoundToTickSize(limitPx);
                    workingEntryOrder = EnterLongLimit(0, true, qty, limitPx, ENTRY_SIGNAL);
                    DiagLog(string.Format("REAL ENTRY #{0} LIMIT qty={1} limit={2:F2}",
                        ordersPlaced, qty, limitPx));
                }
            }
            catch (Exception ex)
            {
                DiagLog("PlaceRealEntry error: " + ex.Message);
                ordersPlaced--;
                entryInFlight     = false;
                awaitingClose     = false;
                workingEntryOrder = null;
            }
        }

        // =====================================================================
        // PlaceEntry — always FAKE
        // Real trades fire from PlaceRealEntry() via AppendOutcome()
        // =====================================================================
        private void PlaceEntry()
        {
            // Always fake — real trade fires from state machine in AppendOutcome
            ordersPlaced++;
            double refPrice = GetCurrentBid();
            if (refPrice <= 0) { ordersPlaced--; return; }

            if (!UseMarketEntry)
                refPrice = Instrument.MasterInstrument.RoundToTickSize(refPrice - LimitOffsetPoints);

            fakeEntryPrice  = refPrice;
            fakeStopPrice   = Instrument.MasterInstrument.RoundToTickSize(fakeEntryPrice - StopLossPoints);
            fakeTargetPrice = Instrument.MasterInstrument.RoundToTickSize(fakeEntryPrice + ProfitTargetPoints);
            inFakeTrade     = true;
            awaitingClose   = true;

            DiagLog(string.Format("FAKE ENTRY #{0} @ {1:F2} stop={2:F2} target={3:F2}",
                ordersPlaced, fakeEntryPrice, fakeStopPrice, fakeTargetPrice));
        }

        // =====================================================================
        // CheckFakeTrade — called every tick while inFakeTrade=true
        // =====================================================================
        private void CheckFakeTrade()
        {
            try
            {
                double bid = GetCurrentBid();
                double ask = GetCurrentAsk();
                if (bid <= 0 || ask <= 0) return;

                bool stopHit   = bid <= fakeStopPrice;
                bool targetHit = ask >= fakeTargetPrice;
                if (!stopHit && !targetHit) return;

                int    bit       = stopHit ? 0 : 1;
                double exitPrice = stopHit ? fakeStopPrice : fakeTargetPrice;
                double pnl       = stopHit
                    ? -(StopLossPoints    * Instrument.MasterInstrument.PointValue)
                    : +(ProfitTargetPoints * Instrument.MasterInstrument.PointValue);

                inFakeTrade   = false;
                awaitingClose = false;

                DiagLog(string.Format("FAKE {0}: entry={1:F2} exit={2:F2} pnl={3:0.00}",
                    stopHit ? "LOSS" : "WIN", fakeEntryPrice, exitPrice, pnl));

                // Log expected real trade outcome (digit right after FIRE)
                if (waitingForExpected)
                {
                    waitingForExpected = false;
                    DiagLog(string.Format(
                        "[EXPECTED OUTCOME] Real trade expected: {0} (bit={1}) — compare with CLOSED STOP/TARGET above",
                        bit == 1 ? "WIN" : "LOSE", bit));
                }

                WriteLogRowFake(exitPrice, pnl, bit);

                fakeEntryPrice  = 0.0;
                fakeStopPrice   = 0.0;
                fakeTargetPrice = 0.0;

                AppendOutcome(bit);
            }
            catch (Exception ex)
            {
                DiagLog("[FAKE] CheckFakeTrade error: " + ex.Message);
                inFakeTrade   = false;
                awaitingClose = false;
            }
        }

        // =====================================================================
        // Pattern helpers
        // =====================================================================
        private List<string> ParsePatternList(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            foreach (var part in raw.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                bool ok = true;
                foreach (char c in p)
                    if (c != '0' && c != '1') { ok = false; break; }
                if (ok && !result.Contains(p))
                    result.Add(p);
            }
            return result;
        }

        private bool MatchesTail(string history, List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) return false;
            foreach (var p in patterns)
            {
                if (history.Length < p.Length) continue;
                if (history.Substring(history.Length - p.Length) == p) return true;
            }
            return false;
        }

        private string TailOf(StringBuilder sb, int n)
        {
            string s = sb.ToString();
            return s.Length <= n ? s : "..." + s.Substring(s.Length - n);
        }

        // =====================================================================
        // ReadyForNewEntry
        // =====================================================================
        private bool ReadyForNewEntry()
        {
            if (entryInFlight) return false;
            if (awaitingClose) return false;
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
                                DiagLog(string.Format(
                                    "ReadyForNewEntry: BLOCKED by broker-side order name='{0}' state={1}.",
                                    ord.Name ?? "", ord.OrderState));
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("ReadyForNewEntry: scan error: " + ex.Message + ". Blocking.");
                return false;
            }
            return true;
        }

        // =====================================================================
        // OnExecutionUpdate
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;

            string oName  = execution.Order.Name ?? "";
            bool   isFull = execution.Order.OrderState == OrderState.Filled;
            bool   isPart = execution.Order.OrderState == OrderState.PartFilled;

            // ---- entry fill ----
            if (oName == ENTRY_SIGNAL && (isFull || isPart))
            {
                if (entryFillPrice == 0.0)
                    entryFillPrice = price;
                entryFillQty += quantity;

                DiagLog(string.Format("ENTRY {0} fill: qty={1} @ {2:F2} totalFilled={3}/{4}",
                    isFull ? "FULL" : "PARTIAL",
                    quantity, price, entryFillQty, BaseQuantity));

                if (isFull)
                {
                    entryInFlight     = false;
                    workingEntryOrder = null;
                    DiagLog(string.Format("Entry complete. Fill={0:F2} qty={1}. Trail={2}.",
                        entryFillPrice, entryFillQty,
                        EnableTrailingStop ? "ENABLED (NT SetTrailStop)" : "disabled (fixed stop)"));
                }
                return;
            }

            // ---- bracket exit fill ----
            bool isStopFill   = oName.IndexOf("Stop",            StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("StopCancelClose", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill) && isFull)
            {
                DiagLog(string.Format("{0} fill: qty={1} @ {2:F2} name={3}",
                    isStopFill ? "STOP" : "TARGET", quantity, price, oName));

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    double pnl = isStopFill
                        ? -(StopLossPoints    * entryFillQty * Instrument.MasterInstrument.PointValue)
                        : +(ProfitTargetPoints * entryFillQty * Instrument.MasterInstrument.PointValue);

                    int bit = isStopFill ? 0 : 1;

                    DiagLog(string.Format(
                        "CLOSED {0}: entry={1:F2} exit={2:F2} qty={3} pnl={4:0.00} bit={5}",
                        isStopFill ? "STOP" : "TARGET",
                        entryFillPrice, price, entryFillQty, pnl, bit));

                    awaitingClose     = false;
                    entryInFlight     = false;
                    workingEntryOrder = null;

                    // Update real loss streak
                    if (bit == 0)
                    {
                        realLossesInARow++;
                        DiagLog(string.Format("[REAL LOSS] realLossesInARow={0} / max={1}",
                            realLossesInARow, MaxRealLossInARow));
                    }
                    else
                    {
                        if (realLossesInARow > 0)
                            DiagLog(string.Format("[REAL WIN] Resetting realLossesInARow {0}→0",
                                realLossesInARow));
                        realLossesInARow = 0;
                    }
                    // AppendOutcome FIRST — L0 includes real trade bit before logging
                    AppendOutcome(bit);
                    // WriteLogRow AFTER — L0 and L1 now reflect this real trade
                    WriteLogRow(price, pnl, bit, entryFillQty, time);
                    entryFillPrice    = 0.0;
                    entryFillQty      = 0;
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
                DiagLog(string.Format("Entry order {0} (filled={1}). Resetting entry flags.",
                    orderState, filled));
                if (filled == 0)
                {
                    ordersPlaced--;
                    entryInFlight     = false;
                    awaitingClose     = false;
                    workingEntryOrder = null;
                    entryFillPrice    = 0.0;
                    entryFillQty      = 0;
                    DiagLog("Entry cancelled with zero fills. ordersPlaced decremented.");
                }
                else
                {
                    entryInFlight     = false;
                    workingEntryOrder = null;
                    DiagLog(string.Format(
                        "Entry cancelled with {0} partial fill(s). Position still managed.", filled));
                }
                return;
            }

            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
            {
                DiagLog(string.Format("ORDER WARN: {0} state={1} err={2} native={3}",
                    oName, orderState, error,
                    string.IsNullOrEmpty(nativeError) ? "-" : nativeError));
            }
            else
            {
                DiagLog(string.Format("ORDER {0} state={1} qty={2} filled={3} avg={4:F2}",
                    oName, orderState, quantity, filled, averageFillPrice));
            }
        }

        // =====================================================================
        // WithinTradingHours
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
        // Shutdown
        // =====================================================================
        private void BeginShutdown(string reason)
        {
            if (disabledSelf || pendingFlatten) return;
            pendingReason  = reason;
            pendingFlatten = true;
            DiagLog(Name + " shutdown requested: " + reason
                + " | ordersPlaced=" + ordersPlaced
                + " | realLossesInARow=" + realLossesInARow);
        }

        private void ProcessShutdown()
        {
            if (entryInFlight && workingEntryOrder != null)
            {
                OrderState os = workingEntryOrder.OrderState;
                if (os == OrderState.Working
                    || os == OrderState.Accepted
                    || os == OrderState.Submitted)
                {
                    try
                    {
                        DiagLog(string.Format(
                            "Shutdown: cancelling unfilled entry order (state={0}).", os));
                        CancelOrder(workingEntryOrder);
                    }
                    catch (Exception ex)
                    {
                        DiagLog("Shutdown: CancelOrder error: " + ex.Message);
                        entryInFlight     = false;
                        awaitingClose     = false;
                        workingEntryOrder = null;
                    }
                }
                return;
            }

            if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight)
            {
                FinalizeTermination();
                return;
            }

            if (Position.MarketPosition == MarketPosition.Long)
            {
                try
                {
                    ExitLong(Math.Abs(Position.Quantity), "LR_Flatten", ENTRY_SIGNAL);
                    DiagLog("Shutdown: ExitLong submitted for "
                        + Math.Abs(Position.Quantity) + " contracts.");
                }
                catch (Exception ex)
                {
                    DiagLog("Shutdown ExitLong error: " + ex.Message);
                }
            }
        }

        private void FinalizeTermination()
        {
            if (disabledSelf) return;
            disabledSelf   = true;
            pendingFlatten = false;
            DiagLog(Name + " terminated. Reason: " + pendingReason
                + " | ordersPlaced=" + ordersPlaced
                + " | realLossesInARow=" + realLossesInARow
                + " | wL1=" + waitingForL1 + " | wTrade=" + waitingForTrade
                + " | L0=" + outcomeHistory.ToString()
                + " | L1=" + filter1History.ToString());
            try { SetState(State.Terminated); } catch { }
        }

        // =====================================================================
        // Logging
        // =====================================================================
        private void EnsureLogHeader()
        {
            try
            {
                string dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                // Always recreate on startup — guarantees header is always present
                File.WriteAllText(LogFilePath,
                    "timestamp,order_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,layer0,layer1\n");
            }
            catch (Exception ex) { Print("Log header error: " + ex.Message); }
        }

        private void WriteLogRow(double exitPrice, double pnl, int bit, int qty, DateTime exitTime)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9}\n",
                    exitTime, ordersPlaced, "Long", qty,
                    entryFillPrice, exitPrice, pnl, bit,
                    outcomeHistory.ToString(), filter1History.ToString());
                File.AppendAllText(LogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error: " + ex.Message); }
        }

        private void WriteLogRowFake(double exitPrice, double pnl, int bit)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9}\n",
                    DateTime.Now, ordersPlaced, "FAKE_Long", 0,
                    fakeEntryPrice, exitPrice, pnl, bit,
                    outcomeHistory.ToString(), filter1History.ToString());
                File.AppendAllText(LogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error (fake): " + ex.Message); }
        }

        private void DiagLog(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg;
            Print(line);
            try
            {
                string dir = Path.GetDirectoryName(LogFilePath);
                if (string.IsNullOrEmpty(dir)) dir = @"C:\temp";
                string baseName = Path.GetFileNameWithoutExtension(LogFilePath);
                if (baseName.EndsWith("_log", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName.Substring(0, baseName.Length - 4);
                string diagPath = Path.Combine(dir, baseName + "-diag.log");
                File.AppendAllText(diagPath, line + "\n");
            }
            catch { }
        }

        // =====================================================================
        // Properties
        // =====================================================================
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading Hours filter", Order = 1, GroupName = "Hours",
            Description = "When false: no time filter. When true: only new entries within NY window.")]
        public bool EnableTradingHours { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start hour (NY, 24h)", Order = 2, GroupName = "Hours")]
        public int TradingStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start minute (NY)", Order = 3, GroupName = "Hours")]
        public int TradingStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End hour (NY, 24h)", Order = 4, GroupName = "Hours")]
        public int TradingEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End minute (NY)", Order = 5, GroupName = "Hours")]
        public int TradingEndMinute { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Strategy life (minutes)", Order = 6, GroupName = "Timing")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Check interval (seconds)", Order = 7, GroupName = "Timing")]
        public int CheckIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use market entry (else limit)", Order = 8, GroupName = "Entry")]
        public bool UseMarketEntry { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Limit offset (points)", Order = 9, GroupName = "Entry")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Stop loss (points)", Order = 10, GroupName = "Bracket")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Profit target (points)", Order = 11, GroupName = "Bracket")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trailing Stop", Order = 12, GroupName = "Bracket",
            Description = "When true: NT native SetTrailStop (stop moves up with price). "
                        + "When false: fixed stop only. "
                        + "Recommended minimums: ETH 8-10pt | RTH 15-20pt | News 25-30pt.")]
        public bool EnableTrailingStop { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Trail distance (points)", Order = 13, GroupName = "Bracket",
            Description = "Only used when Enable Trailing Stop = true.")]
        public double TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Real Order", Order = 1, GroupName = "Filter & Real Order",
            Description = "FALSE = observation only, no real orders placed. "
                        + "TRUE = real order fires when Filter 2 Pattern matches Layer 1 tail.")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 1 Pattern (comma-separated)", Order = 2, GroupName = "Filter & Real Order",
            Description = "Pattern checked against Layer 0 (raw outcome) tail after every trade. "
                        + "Match → outcome appended to Layer 1. "
                        + "Default '01' = after loss then win. Examples: '011', '01,011'.")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 Pattern (comma-separated)", Order = 3, GroupName = "Filter & Real Order",
            Description = "Pattern checked against Layer 1 tail after every Layer 1 append. "
                        + "Match + Enable Real Order = true → real trade fires. "
                        + "Default '01' = after loss then win in Layer 1.")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 14, GroupName = "Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Fake + Real Trade Count", Order = 1, GroupName = "Limits",
            Description = "Stop strategy after this many total trades (fake + real combined). "
                        + "Controls session length and activity. Default 100.")]
        public int MaxTotalTradeCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Real Loss In A Row", Order = 2, GroupName = "Limits",
            Description = "Stop strategy after this many consecutive REAL trade losses. "
                        + "Fake trade losses do NOT count toward this limit. "
                        + "Resets to 0 on any real trade win. Default 3.")]
        public int MaxRealLossInARow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log file path", Order = 3, GroupName = "Logging")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
