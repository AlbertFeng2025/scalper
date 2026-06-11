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

// scalper_LONGrepeat_Layer2  v2
//
// PIPELINE (matches Python trade_filter.py exactly):
//
//   Every slice (fake or real) closes:
//     1. append bit to rawString
//     2. rawString tail matches Filter1Pattern?
//            YES → append bit to filter1Outcome
//     3. filter1Outcome tail matches Filter2Pattern?
//            YES → isArmed = true
//            NO  → isArmed = false
//     4. isArmed AND rawString tail matches Filter1Pattern?
//            YES → NEXT slice = money trade
//            NO  → NEXT slice = fake trade
//
// TARGET = 1 = price UP (LONG)
//   fake/real slice hits profit target (price UP)  → record 1
//   fake/real slice hits stop loss    (price DOWN) → record 0
//
// VARIABLE NAMES:
//   rawString      = all slice outcomes (fake + real combined)
//   filter1Outcome = digits collected after each Filter1Pattern match in rawString
//   filter2Outcome = digits collected after each Filter2Pattern match in filter1Outcome
//                    (not stored — used only to decide real trade in Python offline)
//
// SAFETY at every slice end:
//   cancel any pending order + close any open position before next slice

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
        private StringBuilder rawString      = new StringBuilder(); // Layer 0
        private StringBuilder filter1Outcome = new StringBuilder(); // Layer 1

        // ── pipeline state ───────────────────────────────────────────────────
        private bool isArmed             = false;  // filter2Outcome tail matched Filter2Pattern
        private bool waitingForF1Outcome = false;  // F1 matched → next bit feeds filter1Outcome
        private bool nextIsMoney         = false;  // set end of UpdatePipeline: next slice = money trade

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

        // ── shutdown ─────────────────────────────────────────────────────────
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "SR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "SHORT scalper v2. Pipeline matches Python trade_filter.py. "
                            + "rawString=all slices, filter1Outcome=after F1 match, "
                            + "isArmed=F2 tail match, next F1 match=money trade. "
                            + "Target=1=price DOWN.";
                Name        = "Scalper_Shortrepeat_Layer2";

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

                // ── defaults ──────────────────────────────────────────────────
                EnableTradingHours   = false;
                TradingStartHour     = 9;
                TradingStartMinute   = 30;
                TradingEndHour       = 16;
                TradingEndMinute     = 0;
                StrategyLifeMinutes  = 3;
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
                BaseQuantity         = 1;
                MaxTotalSliceCount   = 100;
                MaxRealLossInARow    = 3;
                LogFilePath          = @"C:\temp\scalper_LONGrepeat_Layer2_log.csv";
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
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    strategyStartUtc = DateTime.UtcNow;
                    lifeStarted      = true;
                    isArmed             = false;
                    waitingForF1Outcome = false;
                    nextIsMoney         = false;
                    realLossesInARow    = 0;
                    EnsureLogHeader();
                    DiagLog(Name + " enabled (SHORT). Life=" + StrategyLifeMinutes
                        + "min, MaxTotalSliceCount=" + MaxTotalSliceCount
                        + ", MaxRealLossInARow=" + MaxRealLossInARow
                        + ", Qty=" + BaseQuantity
                        + ", Stop=" + StopLossPoints + "pt"
                        + ", Target=" + ProfitTargetPoints + "pt"
                        + ", EnableTrailingStop=" + EnableTrailingStop
                        + (EnableTrailingStop ? ", TrailDist=" + TrailDistancePoints + "pt" : "")
                        + ", EnableRealOrder=" + EnableRealOrder
                        + ", Filter1=[" + Filter1Pattern + "]"
                        + ", Filter2=[" + Filter2Pattern + "]");
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

            // ── monitor current slice tick by tick ────────────────────────────
            if (inSlice && !isMoneySlice)
            {
                CheckFakeSlice();
                return;
            }

            // ── if money slice in flight, wait for OnExecutionUpdate ──────────
            if (inSlice && isMoneySlice)
                return;

            DateTime nowUtc = DateTime.UtcNow;

            // ── strategy life limit ───────────────────────────────────────────
            if ((nowUtc - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached");
                return;
            }

            // ── max slice count ───────────────────────────────────────────────
            if (sliceCount >= MaxTotalSliceCount)
            {
                BeginShutdown("MaxTotalSliceCount (" + MaxTotalSliceCount + ") reached");
                return;
            }

            // ── max real loss in a row ────────────────────────────────────────
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
        // StartNextSlice — decides if next slice is fake or money
        // Based on: isArmed AND rawString tail matches Filter1Pattern
        // =====================================================================
        private void StartNextSlice()
        {
            // Is this slice a money trade?
            // YES if nextIsMoney flag was set at end of previous slice
            // (= isArmed AND rawString tail matched F1 at end of last slice)
            bool startMoney = nextIsMoney;
            nextIsMoney = false;  // reset after reading

            sliceCount++;
            double refPrice = GetCurrentAsk();
            if (refPrice <= 0) { sliceCount--; return; }

            if (!UseMarketEntry)
                refPrice = Instrument.MasterInstrument.RoundToTickSize(refPrice - LimitOffsetPoints);

            sliceEntryPrice  = refPrice;
            sliceStopPrice   = Instrument.MasterInstrument.RoundToTickSize(sliceEntryPrice + StopLossPoints);   // ABOVE entry
            sliceTargetPrice = Instrument.MasterInstrument.RoundToTickSize(sliceEntryPrice - ProfitTargetPoints); // BELOW entry
            inSlice          = true;
            isMoneySlice     = startMoney && EnableRealOrder;

            if (isMoneySlice)
            {
                // Place real money order
                awaitingClose     = true;
                entryInFlight     = true;
                workingEntryOrder = null;
                try
                {
                    if (UseMarketEntry)
                    {
                        workingEntryOrder = EnterShort(BaseQuantity, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SHORT SLICE #{0} MARKET qty={1} entry~{2:F2} stop={3:F2}(above) target={4:F2}(below) | rawString={5} | filter1Outcome={6}",
                            sliceCount, BaseQuantity, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8)));
                    }
                    else
                    {
                        double limitPx = Instrument.MasterInstrument.RoundToTickSize(
                            GetCurrentAsk() + LimitOffsetPoints);
                        workingEntryOrder = EnterShortLimit(0, true, BaseQuantity, limitPx, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SHORT SLICE #{0} LIMIT qty={1} limit={2:F2} | rawString={3} | filter1Outcome={4}",
                            sliceCount, BaseQuantity, limitPx,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8)));
                    }
                }
                catch (Exception ex)
                {
                    DiagLog("StartNextSlice money error: " + ex.Message);
                    sliceCount--;
                    inSlice           = false;
                    isMoneySlice      = false;
                    awaitingClose     = false;
                    entryInFlight     = false;
                    workingEntryOrder = null;
                }
            }
            else
            {
                DiagLog(string.Format(
                    "FAKE SHORT SLICE #{0} entry={1:F2} stop={2:F2}(above) target={3:F2}(below) | isArmed={4} | rawTail={5} | filter1Outcome={6}",
                    sliceCount, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                    isArmed, TailOf(rawString, Filter1Pattern.Length),
                    TailOf(filter1Outcome, 8)));
            }
        }

        // =====================================================================
        // CheckFakeSlice — tick by tick resolution for fake slices
        // TARGET = 1 = price DOWN (SHORT)
        //   stop hit   when ask >= sliceStopPrice   → bit = 0  (price went UP against short)
        //   target hit when bid <= sliceTargetPrice → bit = 1  (price went DOWN for short)
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

                inSlice      = false;
                isMoneySlice = false;

                DiagLog(string.Format(
                    "FAKE SHORT SLICE #{0} {1}: entry={2:F2} exit={3:F2} pnl={4:0.00} bit={5}",
                    sliceCount, stopHit ? "LOSS" : "WIN",
                    sliceEntryPrice, exitPrice, pnl, bit));

                // save entry price before reset
                double logEntryPrice = sliceEntryPrice;

                // reset slice prices
                sliceEntryPrice  = 0.0;
                sliceStopPrice   = 0.0;
                sliceTargetPrice = 0.0;

                // ── update pipeline FIRST ─────────────────────────────────────
                UpdatePipeline(bit);

                // ── log AFTER pipeline updated — shows final state ────────────
                WriteLogRowFake(logEntryPrice, exitPrice, pnl, bit);
            }
            catch (Exception ex)
            {
                DiagLog("CheckFakeSlice error: " + ex.Message);
                inSlice      = false;
                isMoneySlice = false;
            }
        }

        // =====================================================================
        // UpdatePipeline — called after EVERY slice closes (fake or real)
        //
        // Matches Python apply_filter logic exactly:
        //   Python: find F1 pattern at position i → collect digit at position i+len(F1)
        //   = the digit AFTER the pattern, not the last digit OF the pattern
        //
        //   Therefore we use waitingForF1Outcome flag:
        //     - current bit completes F1 pattern → set waitingForF1Outcome=true
        //     - NEXT bit arrives → append THAT bit to filter1Outcome
        //
        //   1. append bit to rawString
        //   2. was waitingForF1Outcome=true from previous call?
        //        YES → this bit is digit AFTER F1 → append to filter1Outcome
        //              check filter1Outcome tail matches Filter2?
        //              YES → isArmed = true
        //              NO  → isArmed = false
        //              clear waitingForF1Outcome
        //   3. does current rawString tail match Filter1?
        //        YES → waitingForF1Outcome = true (next bit feeds filter1Outcome)
        //        NO  → waitingForF1Outcome = false
        // =====================================================================
        private void UpdatePipeline(int bit)
        {
            // Step 1: append to rawString
            rawString.Append(bit.ToString());
            string raw = rawString.ToString();

            // Step 2: was previous rawString tail matching F1?
            // i.e. waitingForF1Outcome set from last call?
            if (waitingForF1Outcome)
            {
                waitingForF1Outcome = false;

                // this bit is the digit RIGHT AFTER F1 pattern → feeds filter1Outcome
                filter1Outcome.Append(bit.ToString());
                string f1str = filter1Outcome.ToString();

                DiagLog(string.Format(
                    "[F1 COLLECT] digit after F1='{0}' is '{1}' → filter1Outcome={2}",
                    Filter1Pattern, bit, f1str));

                // Step 3: filter1Outcome tail matches Filter2Pattern?
                isArmed = f1str.Length >= Filter2Pattern.Length
                       && f1str.EndsWith(Filter2Pattern);

                if (isArmed)
                    DiagLog(string.Format(
                        "[F2 MATCH] filter1Outcome tail='{0}' matches Filter2='{1}' → isArmed=true",
                        f1str.Length >= Filter2Pattern.Length
                            ? f1str.Substring(f1str.Length - Filter2Pattern.Length) : f1str,
                        Filter2Pattern));
                else
                    DiagLog(string.Format(
                        "[F2 NO MATCH] filter1Outcome tail='{0}' → isArmed=false",
                        f1str.Length >= Filter2Pattern.Length
                            ? f1str.Substring(f1str.Length - Filter2Pattern.Length) : f1str));
            }

            // Check if CURRENT rawString tail matches F1
            // → next bit will feed filter1Outcome
            bool f1Match = raw.Length >= Filter1Pattern.Length
                        && raw.EndsWith(Filter1Pattern);

            if (f1Match)
            {
                waitingForF1Outcome = true;
                DiagLog(string.Format(
                    "[F1 MATCH] rawString tail='{0}' matches Filter1='{1}' → next bit feeds filter1Outcome",
                    raw.Length >= Filter1Pattern.Length
                        ? raw.Substring(raw.Length - Filter1Pattern.Length) : raw,
                    Filter1Pattern));
            }

            // Set nextIsMoney flag for StartNextSlice:
            // isArmed AND current rawString tail matches F1
            // → next slice that starts will be a money trade
            nextIsMoney = isArmed
                       && rawString.Length >= Filter1Pattern.Length
                       && rawString.ToString().EndsWith(Filter1Pattern);

            DiagLog(string.Format(
                "[PIPELINE] rawString({0})={1} | filter1Outcome({2})={3} | waitingForF1Outcome={4} | isArmed={5} | nextIsMoney={6} | realLossRow={7}",
                rawString.Length,      TailOf(rawString,      8),
                filter1Outcome.Length, TailOf(filter1Outcome, 8),
                waitingForF1Outcome, isArmed, nextIsMoney, realLossesInARow));
        }

        // =====================================================================
        // OnExecutionUpdate — handles real money slice fills
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
                    DiagLog(string.Format("Entry complete. Fill={0:F2} qty={1}.",
                        entryFillPrice, entryFillQty));
                }
                return;
            }

            // ── bracket exit fill ─────────────────────────────────────────────
            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("StopCancelClose", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill) && isFull)
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    double pnl = isStopFill
                        ? -(StopLossPoints     * entryFillQty * Instrument.MasterInstrument.PointValue)
                        : +(ProfitTargetPoints * entryFillQty * Instrument.MasterInstrument.PointValue);

                    int bit = isStopFill ? 0 : 1;

                    DiagLog(string.Format(
                        "MONEY SHORT SLICE #{0} CLOSED {1}: entry={2:F2} exit={3:F2} qty={4} pnl={5:0.00} bit={6}",
                        sliceCount,
                        isStopFill ? "STOP" : "TARGET",
                        entryFillPrice, price, entryFillQty, pnl, bit));

                    // update real loss streak
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

                    awaitingClose     = false;
                    entryInFlight     = false;
                    workingEntryOrder = null;
                    inSlice           = false;
                    isMoneySlice      = false;

                    // save fill price before reset
                    double logFillPrice = entryFillPrice;
                    int    logFillQty   = entryFillQty;
                    entryFillPrice = 0.0;
                    entryFillQty   = 0;

                    // ── update pipeline FIRST ──────────────────────────────────
                    UpdatePipeline(bit);

                    // ── log AFTER pipeline updated — shows final state ─────────
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
                DiagLog(string.Format("Entry order {0} (filled={1}). Resetting.",
                    orderState, filled));
                if (filled == 0)
                {
                    sliceCount--;
                    entryInFlight     = false;
                    awaitingClose     = false;
                    workingEntryOrder = null;
                    entryFillPrice    = 0.0;
                    entryFillQty      = 0;
                    inSlice           = false;
                    isMoneySlice      = false;
                    DiagLog("Entry cancelled zero fills. sliceCount decremented.");
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
                DiagLog(string.Format("ORDER WARN: {0} state={1} err={2} native={3}",
                    oName, orderState, error,
                    string.IsNullOrEmpty(nativeError) ? "-" : nativeError));
            else
                DiagLog(string.Format("ORDER {0} state={1} qty={2} filled={3} avg={4:F2}",
                    oName, orderState, quantity, filled, averageFillPrice));
        }

        // =====================================================================
        // ReadyForNewSlice
        // =====================================================================
        private bool ReadyForNewSlice()
        {
            if (inSlice)      return false;
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
                                DiagLog(string.Format(
                                    "ReadyForNewSlice: BLOCKED by order name='{0}' state={1}.",
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
                + " | sliceCount=" + sliceCount
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
                            "Shutdown: cancelling entry order (state={0}).", os));
                        CancelOrder(workingEntryOrder);
                    }
                    catch (Exception ex)
                    {
                        DiagLog("Shutdown CancelOrder error: " + ex.Message);
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

            if (Position.MarketPosition == MarketPosition.Short)
            {
                try
                {
                    ExitShort(Math.Abs(Position.Quantity), "SR_Flatten", ENTRY_SIGNAL);
                    DiagLog("Shutdown: ExitShort submitted for "
                        + Math.Abs(Position.Quantity) + " contracts.");
                }
                catch (Exception ex)
                {
                    DiagLog("Shutdown ExitShort error: " + ex.Message);
                }
            }
        }

        private void FinalizeTermination()
        {
            if (disabledSelf) return;
            disabledSelf   = true;
            pendingFlatten = false;
            DiagLog(Name + " terminated. Reason: " + pendingReason
                + " | sliceCount=" + sliceCount
                + " | realLossesInARow=" + realLossesInARow
                + " | isArmed=" + isArmed
                + " | waitingForF1Outcome=" + waitingForF1Outcome
                + " | nextIsMoney=" + nextIsMoney
                + " | rawString=" + rawString.ToString()
                + " | filter1Outcome=" + filter1Outcome.ToString());
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
        // Logging
        // =====================================================================
        private void EnsureLogHeader()
        {
            try
            {
                string dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(LogFilePath,
                    "timestamp,slice_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,rawString,filter1Outcome\n");
            }
            catch (Exception ex) { Print("Log header error: " + ex.Message); }
        }

        private void WriteLogRow(double entryPrice, double exitPrice, double pnl, int bit, int qty, DateTime exitTime)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9}\n",
                    exitTime, sliceCount, "Short", qty,
                    entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString());
                File.AppendAllText(LogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error: " + ex.Message); }
        }

        private void WriteLogRowFake(double entryPrice, double exitPrice, double pnl, int bit)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9}\n",
                    DateTime.Now, sliceCount, "FAKE_Short", 0,
                    entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString());
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
                string diagPath = Path.Combine(dir, baseName + "-diagLog.csv");
                File.AppendAllText(diagPath, line + "\n");
            }
            catch { }
        }

        // =====================================================================
        // Properties
        // =====================================================================
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading Hours filter", Order = 1, GroupName = "Hours")]
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
        [Display(Name = "Enable Trailing Stop", Order = 12, GroupName = "Bracket")]
        public bool EnableTrailingStop { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Trail distance (points)", Order = 13, GroupName = "Bracket")]
        public double TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Real Order", Order = 1, GroupName = "Filter & Real Order",
            Description = "FALSE = observation only. TRUE = real order fires when armed and F1 matches.")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 1 Pattern", Order = 2, GroupName = "Filter & Real Order",
            Description = "Pattern checked against rawString tail. Match → digit appended to filter1Outcome. Default '01'.")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 Pattern", Order = 3, GroupName = "Filter & Real Order",
            Description = "Pattern checked against filter1Outcome tail. Match → isArmed=true → next F1 match = money trade. Default '11'.")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 14, GroupName = "Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Total Slice Count", Order = 1, GroupName = "Limits",
            Description = "Stop after this many total slices (fake + real). Default 100.")]
        public int MaxTotalSliceCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Real Loss In A Row", Order = 2, GroupName = "Limits",
            Description = "Stop after this many consecutive real trade losses. Default 3.")]
        public int MaxRealLossInARow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log file path", Order = 3, GroupName = "Logging")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
