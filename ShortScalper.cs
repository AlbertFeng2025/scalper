#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =============================================================================
//  STRATEGY:    ShortScalper v2.4
//  AUTHOR:      Drafted with help from Claude
//  VERSION:     2.4 - FIXED LIMIT PRICE OPTION
// =============================================================================
//
//  Mirror of LongScalper v2.4 for shorts. Same orphan-order cleanup, same
//  trailing logic, same state machine. Only the directional math is flipped.
//
//  v2.4 CHANGES vs v2.3
//  --------------------
//  Two small additions, all other logic unchanged.
//
//  CHANGE 1: New "Limit Mode" dropdown for entry price.
//
//    Two options:
//      (a) OffsetFromLastPrice - the existing behavior. Limit price is
//          computed as: lastPrice + (EntryOffsetMultiplier x avgBarSize).
//          Use this for fast scalping near current price. (DEFAULT)
//
//      (b) FixedPrice - the user types a specific price. Limit price is
//          set directly to whatever they typed. Use this when you want
//          to set a HIGH limit (e.g. 1000 points above current price)
//          and let the order sit waiting for a spike up to fill.
//
//    The dropdown is a parameter called `LimitMode`. The price for fixed
//    mode is a parameter called `FixedLimitPrice`.
//
//    NOTE: For shorts, the FixedLimitPrice should be ABOVE current price
//    (sell-the-rip). For longs, FixedLimitPrice is BELOW current price
//    (buy-the-dip). Don't confuse them at 2 AM.
//
//  CHANGE 2: OrderLifeSeconds maximum raised from 3600 (1 hour) to
//    86400 (24 hours). Lets you set very long-life bait orders.
//
//  AUDIT LOG: One new column `LimitMode` was added.
//
//  KEY DIRECTIONAL LOGIC (UNCHANGED FROM v2.3)
//  -------------------------------------------
//  - Entry limit:    lastPrice + offset (sell limit ABOVE market)
//  - Profit target:  expectedFill - ProfitTargetPoints (BELOW fill)
//  - Initial stop:   expectedFill + HardStopPoints (ABOVE fill)
//  - Healthy trade:  price moves DOWN, stop ratchets DOWN
//  - Pullback:       price rallies UP beyond tolerance
//  - PnL:            actualFillPrice - exitPrice (positive when price fell)
//  - Stop only ever moves DOWN, never up.
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    // -------------------------------------------------------------------------
    // [v2.4 NEW] Enum defining the two limit-price modes for shorts.
    // Named ShortEntryLimitMode so it does NOT collide with LongScalper's
    // EntryLimitMode enum if both strategies are compiled together.
    // -------------------------------------------------------------------------
    public enum ShortEntryLimitMode
    {
        OffsetFromLastPrice,
        FixedPrice
    }

    public class ShortScalper : Strategy
    {
        #region Variables

        private enum TradeState
        {
            Idle,
            WaitingForFill,
            InPosition,
            ClosingDelay,
            Done
        }
        private TradeState currentState = TradeState.Idle;

        private Order entryOrder        = null;
        private Order stopLossOrder     = null;
        private Order profitTargetOrder = null;

        private DateTime entryOrderPlacedTime;
        private DateTime fillTime;
        private DateTime lastMonitorCheckTime;
        private DateTime closingDelayStartTime;

        private double entryLastTradedPrice = 0;
        private double calculatedLimitPrice = 0;
        private double actualFillPrice      = 0;
        private double avgBarSizeAtEntry    = 0;
        private double previousCheckPrice   = 0;
        private double currentStopPrice     = 0;
        private double finalExitPrice       = 0;
        private double finalPnLPoints       = 0;
        private string finalExitReason      = "UNKNOWN";

        private const int MinuteBarsIndex = 1;
        private const string EntrySignalName = "ShortScalperEntry";

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "Short-only scalper with trailing stop, orphan-order cleanup, and fixed-limit-price option.";
                Name                                        = "ShortScalper";
                Calculate                                   = Calculate.OnEachTick;
                EntriesPerDirection                         = 1;
                EntryHandling                               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy                = true;
                ExitOnSessionCloseSeconds                   = 30;
                IsFillLimitOnTouch                          = false;
                MaximumBarsLookBack                         = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                         = OrderFillResolution.Standard;
                Slippage                                    = 0;
                StartBehavior                               = StartBehavior.WaitUntilFlat;
                TimeInForce                                 = TimeInForce.Day;
                TraceOrders                                 = false;
                RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                          = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                         = 20;
                IsInstantiatedOnEachOptimizationIteration   = true;

                // ---- Defaults ----
                Quantity                = 1;

                // [v2.4 NEW] Default to existing behavior so old configs work unchanged.
                LimitMode               = ShortEntryLimitMode.OffsetFromLastPrice;
                FixedLimitPrice         = 0;

                EntryOffsetMultiplier   = 0.10;
                BarSizeAveragePeriod    = 10;
                OrderLifeSeconds        = 5;
                ProfitTargetPoints      = 10;
                HardStopPoints          = 20;
                MonitorIntervalSeconds  = 3;
                PullbackTolerancePoints = 2;
                TrailDistancePoints     = 8;
                DisableDelaySeconds     = 1.5;
                EnableSoundOnFill       = true;
                AuditLogPath            = @"C:\temp";
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                ResetTradeState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] ShortScalper v2.4 armed at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] LimitMode={0}, FixedLimitPrice={1}",
                    LimitMode, FixedLimitPrice));
                Print(string.Format("[INIT] Parameters: OrderLife={0}s, Monitor={1}s, Target={2}pts, InitStop={3}pts, Trail={4}pts, Tolerance={5}pts, DisableDelay={6}s",
                    OrderLifeSeconds, MonitorIntervalSeconds, ProfitTargetPoints,
                    HardStopPoints, TrailDistancePoints, PullbackTolerancePoints, DisableDelaySeconds));
                Print(string.Format("[INIT] AuditLogPath: {0}", AuditLogPath));
                Print(string.Format("[INIT] Pre-check Position: {0} qty={1}", Position.MarketPosition, Position.Quantity));
            }
            else if (State == State.Terminated)
            {
                Print(string.Format("[TERM] Strategy terminated at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
                try { CleanupRemainingBrackets(); } catch { }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade) return;
            if (BarsArray.Length > MinuteBarsIndex && CurrentBars[MinuteBarsIndex] < BarSizeAveragePeriod) return;
            if (State != State.Realtime) return;

            switch (currentState)
            {
                case TradeState.Idle:
                    PlaceEntryOrder();
                    break;

                case TradeState.WaitingForFill:
                    if ((DateTime.Now - entryOrderPlacedTime).TotalSeconds >= OrderLifeSeconds)
                    {
                        if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
                        {
                            Print(string.Format("[TIMEOUT] Order life expired ({0}s). Cancelling.", OrderLifeSeconds));
                            CancelOrder(entryOrder);
                        }
                    }
                    break;

                case TradeState.InPosition:
                    if ((DateTime.Now - lastMonitorCheckTime).TotalSeconds >= MonitorIntervalSeconds)
                    {
                        DoTrailingCheck();
                    }
                    break;

                case TradeState.ClosingDelay:
                    if ((DateTime.Now - closingDelayStartTime).TotalSeconds >= DisableDelaySeconds)
                    {
                        Print(string.Format("[CLOSE] Delay elapsed. Writing audit and disabling."));
                        CleanupRemainingBrackets();
                        WriteAuditLog(finalExitReason, actualFillPrice, finalExitPrice, finalPnLPoints);
                        currentState = TradeState.Done;
                        DisableStrategy();
                    }
                    break;

                case TradeState.Done:
                    break;
            }
        }

        private void PlaceEntryOrder()
        {
            Print("================================================================");
            Print(string.Format("[STEP1] PlaceEntryOrder called at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
            Print(string.Format("[STEP1] Position: {0} qty={1}", Position.MarketPosition, Position.Quantity));
            Print(string.Format("[STEP1] LimitMode={0}", LimitMode));

            // ----- SAFETY CHECK 1: Strategy's own position must be flat -----
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[STEP1] *** BLOCKED *** Strategy position is {0} qty={1}.",
                    Position.MarketPosition, Position.Quantity));
                WriteAuditLog("BLOCKED_NOT_FLAT", 0, 0, 0);
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            // ----- SAFETY CHECK 2: ACCOUNT must also be flat -----
            try
            {
                if (Account != null)
                {
                    Position acctPos = Account.Positions.FirstOrDefault(p => p.Instrument == Instrument);
                    if (acctPos != null && acctPos.MarketPosition != MarketPosition.Flat)
                    {
                        Print(string.Format("[STEP1] *** BLOCKED *** ACCOUNT position is {0} qty={1}.",
                            acctPos.MarketPosition, acctPos.Quantity));
                        Print("[STEP1] Manually flatten the account first, then re-enable.");
                        WriteAuditLog("BLOCKED_ACCOUNT_NOT_FLAT", 0, 0, 0);
                        currentState = TradeState.Done;
                        DisableStrategy();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[STEP1] WARN: Account position check failed: {0}", ex.Message));
            }

            // ----- SAFETY CHECK 3: No working orders for this instrument -----
            try
            {
                if (Account != null)
                {
                    int workingCount = 0;
                    lock (Account.Orders)
                    {
                        foreach (var ord in Account.Orders)
                        {
                            if (ord.Instrument == Instrument
                                && (ord.OrderState == OrderState.Working
                                    || ord.OrderState == OrderState.Accepted
                                    || ord.OrderState == OrderState.Submitted))
                            {
                                workingCount++;
                            }
                        }
                    }
                    if (workingCount > 0)
                    {
                        Print(string.Format("[STEP1] *** BLOCKED *** Found {0} working order(s).", workingCount));
                        WriteAuditLog("BLOCKED_WORKING_ORDERS", 0, 0, 0);
                        currentState = TradeState.Done;
                        DisableStrategy();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[STEP1] WARN: Working orders check failed: {0}", ex.Message));
            }

            // ----- Read current price (always useful for logging/audit) -----
            entryLastTradedPrice = GetCurrentLastPrice();
            if (entryLastTradedPrice <= 0)
            {
                Print("[STEP1] ERROR: Could not get current price. Disabling.");
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            // -----------------------------------------------------------------
            // [v2.4 NEW] Branch on LimitMode to compute the limit price.
            // SHORT-SPECIFIC: limit is ABOVE current price.
            // -----------------------------------------------------------------
            if (LimitMode == ShortEntryLimitMode.FixedPrice)
            {
                // ---- Fixed price mode: use whatever the user typed. ----

                if (FixedLimitPrice <= 0)
                {
                    Print(string.Format("[STEP1] *** BLOCKED *** FixedLimitPrice is {0} (must be > 0).",
                        FixedLimitPrice));
                    WriteAuditLog("BLOCKED_INVALID_FIXED_PRICE", 0, 0, 0);
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return;
                }

                avgBarSizeAtEntry = CalculateEmaBarSize();
                calculatedLimitPrice = Instrument.MasterInstrument.RoundToTickSize(FixedLimitPrice);

                Print(string.Format("[STEP1] FixedPrice mode: lastPrice={0}, userTyped={1}, rounded limitPrice={2}",
                    entryLastTradedPrice, FixedLimitPrice, calculatedLimitPrice));
            }
            else
            {
                // ---- OffsetFromLastPrice mode: original v2.3 behavior. ----

                avgBarSizeAtEntry = CalculateEmaBarSize();
                if (avgBarSizeAtEntry <= 0)
                {
                    Print("[STEP1] ERROR: Could not calculate bar size. Disabling.");
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return;
                }

                double offset = EntryOffsetMultiplier * avgBarSizeAtEntry;
                // SHORT: limit price is ABOVE current price.
                calculatedLimitPrice = Instrument.MasterInstrument.RoundToTickSize(entryLastTradedPrice + offset);

                Print(string.Format("[STEP1] Offset mode: lastPrice={0}, avgBarSize={1:F2}, offset={2:F2}, limitPrice={3} (ABOVE market)",
                    entryLastTradedPrice, avgBarSizeAtEntry, offset, calculatedLimitPrice));
            }

            // ----- Pre-declare bracket BEFORE entry -----
            // SHORT-SPECIFIC: target BELOW fill, initial stop ABOVE fill.
            double expectedFillPrice = calculatedLimitPrice;
            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice - ProfitTargetPoints);
            double initialStop = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice + HardStopPoints);

            try
            {
                SetProfitTarget(EntrySignalName, CalculationMode.Price, targetPrice);
                SetStopLoss(EntrySignalName, CalculationMode.Price, initialStop, false);
                currentStopPrice = initialStop;
                Print(string.Format("[STEP1] Pre-declared brackets: target={0} (below), stop={1} (above)", targetPrice, initialStop));
            }
            catch (Exception ex)
            {
                Print(string.Format("[STEP1] ERROR setting brackets: {0}. Disabling.", ex.Message));
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            entryOrderPlacedTime = DateTime.Now;
            currentState = TradeState.WaitingForFill;

            // SHORT-SPECIFIC: EnterShortLimit instead of EnterLongLimit.
            entryOrder = EnterShortLimit(0, true, Quantity, calculatedLimitPrice, EntrySignalName);
            Print(string.Format("[STEP1] Sell limit submitted at {0}. State now: {1}", calculatedLimitPrice, currentState));
        }

        private double CalculateEmaBarSize()
        {
            if (BarsArray.Length <= MinuteBarsIndex) return 0;
            if (CurrentBars[MinuteBarsIndex] < BarSizeAveragePeriod) return 0;

            double alpha = 2.0 / (BarSizeAveragePeriod + 1.0);
            double ema = 0;

            for (int i = BarSizeAveragePeriod - 1; i >= 0; i--)
            {
                double barSize = Highs[MinuteBarsIndex][i] - Lows[MinuteBarsIndex][i];
                if (i == BarSizeAveragePeriod - 1)
                    ema = barSize;
                else
                    ema = alpha * barSize + (1 - alpha) * ema;
            }
            return ema;
        }

        private double GetCurrentLastPrice()
        {
            if (Closes[0].Count > 0)
                return Close[0];
            return 0;
        }

        // SHORT-SPECIFIC trailing logic: stop ratchets DOWN as price falls.
        private void DoTrailingCheck()
        {
            try
            {
                double currentPrice = GetCurrentLastPrice();
                if (currentPrice <= 0) return;

                double referencePrice = previousCheckPrice;
                if (referencePrice == 0)
                    referencePrice = actualFillPrice;

                // SHORT-SPECIFIC: pullback threshold is ABOVE reference (price rallied).
                double threshold = referencePrice + PullbackTolerancePoints;

                Print(string.Format("[MONITOR] Check at {0}, currentPrice={1}, ref={2}, threshold={3}",
                    DateTime.Now.ToString("HH:mm:ss.fff"),
                    currentPrice, referencePrice, threshold));

                if (currentPrice > threshold)
                {
                    Print(string.Format("[MONITOR]   Pullback detected (price rallied). Letting NT stop {0} handle exit.",
                        currentStopPrice));
                    previousCheckPrice = currentPrice;
                    lastMonitorCheckTime = DateTime.Now;
                }
                else
                {
                    // SHORT-SPECIFIC: trailing stop sits ABOVE current price.
                    double proposedStop = Instrument.MasterInstrument.RoundToTickSize(
                        currentPrice + TrailDistancePoints);

                    // SHORT-SPECIFIC: only move stop DOWN (lower stop = closer to fill, less risk).
                    if (proposedStop < currentStopPrice)
                    {
                        Print(string.Format("[MONITOR]   Trailing stop DOWN from {0} to {1}",
                            currentStopPrice, proposedStop));
                        try
                        {
                            SetStopLoss(EntrySignalName, CalculationMode.Price, proposedStop, false);
                            currentStopPrice = proposedStop;
                        }
                        catch (Exception ex)
                        {
                            Print(string.Format("[MONITOR]   ERROR updating stop: {0}", ex.Message));
                        }
                    }
                    else
                    {
                        Print(string.Format("[MONITOR]   Stop stays at {0} (proposed {1} not lower)",
                            currentStopPrice, proposedStop));
                    }

                    previousCheckPrice = currentPrice;
                    lastMonitorCheckTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[MONITOR] UNEXPECTED ERROR: {0}", ex.Message));
            }
        }

        private void CleanupRemainingBrackets()
        {
            try
            {
                Print(string.Format("[CLEANUP] Running cleanup at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));

                int cancelled = 0;

                if (stopLossOrder != null && IsOrderActive(stopLossOrder))
                {
                    Print(string.Format("[CLEANUP]   Cancelling tracked stop loss order"));
                    CancelOrder(stopLossOrder);
                    cancelled++;
                }
                if (profitTargetOrder != null && IsOrderActive(profitTargetOrder))
                {
                    Print(string.Format("[CLEANUP]   Cancelling tracked profit target order"));
                    CancelOrder(profitTargetOrder);
                    cancelled++;
                }
                if (entryOrder != null && IsOrderActive(entryOrder))
                {
                    Print(string.Format("[CLEANUP]   Cancelling tracked entry order"));
                    CancelOrder(entryOrder);
                    cancelled++;
                }

                if (Account != null)
                {
                    List<Order> toCancel = new List<Order>();
                    lock (Account.Orders)
                    {
                        foreach (var ord in Account.Orders)
                        {
                            if (ord.Instrument == Instrument && IsOrderActive(ord))
                            {
                                string name = ord.Name ?? "";
                                if (name.Contains("Stop") || name.Contains("Profit") || name.Contains("Target")
                                    || name == EntrySignalName)
                                {
                                    toCancel.Add(ord);
                                }
                            }
                        }
                    }
                    foreach (var ord in toCancel)
                    {
                        Print(string.Format("[CLEANUP]   Cancelling leftover order: name='{0}', state={1}",
                            ord.Name ?? "", ord.OrderState));
                        try { CancelOrder(ord); cancelled++; } catch { }
                    }
                }

                Print(string.Format("[CLEANUP] Done. Cancelled {0} order(s).", cancelled));
            }
            catch (Exception ex)
            {
                Print(string.Format("[CLEANUP] ERROR: {0}", ex.Message));
            }
        }

        private bool IsOrderActive(Order o)
        {
            if (o == null) return false;
            return o.OrderState == OrderState.Working
                || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.Submitted;
        }

        private void DisableStrategy()
        {
            Print(string.Format("[DISABLE] DisableStrategy called at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
            Print(string.Format("[DISABLE]   Position: {0} qty={1}", Position.MarketPosition, Position.Quantity));

            try
            {
                TriggerCustomEvent(o =>
                {
                    SetState(State.Finalized);
                }, null);
                Print("[DISABLE]   Strategy disable triggered. Re-enable for next trade.");
            }
            catch (Exception ex)
            {
                Print(string.Format("[DISABLE]   ERROR disabling: {0}", ex.Message));
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

            string oname = order.Name ?? "";

            Print(string.Format("[ORDER] OnOrderUpdate at {0}: name='{1}', state={2}, filled={3}/{4}",
                DateTime.Now.ToString("HH:mm:ss.fff"),
                oname, orderState, filled, quantity));

            if (oname.Contains("Stop loss") || oname == "Stop loss")
            {
                if (stopLossOrder == null) Print("[ORDER]   Captured stop loss order reference.");
                stopLossOrder = order;
            }
            else if (oname.Contains("Profit target") || oname == "Profit target")
            {
                if (profitTargetOrder == null) Print("[ORDER]   Captured profit target order reference.");
                profitTargetOrder = order;
            }

            if (entryOrder != null && order.OrderId == entryOrder.OrderId)
            {
                if ((orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                    && currentState == TradeState.WaitingForFill)
                {
                    Print(string.Format("[ORDER]   Entry order ended without fill: {0}", orderState));
                    WriteAuditLog("ENTRY_NOT_FILLED_" + orderState.ToString(), 0, 0, 0);
                    CleanupRemainingBrackets();
                    currentState = TradeState.Done;
                    DisableStrategy();
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                if (execution == null)
                {
                    Print("[EXEC] WARN: execution is null, ignoring.");
                    return;
                }

                Order execOrder = execution.Order;
                string execOrderName = execOrder != null ? (execOrder.Name ?? "") : "";
                string execOrderId   = execOrder != null ? execOrder.OrderId : "";
                OrderState execState = execOrder != null ? execOrder.OrderState : OrderState.Unknown;

                Print(string.Format("[EXEC] OnExecutionUpdate at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[EXEC]   order='{0}', state={1}, price={2}, qty={3}, marketPos={4}",
                    execOrderName, execState, price, quantity, marketPosition));
                Print(string.Format("[EXEC]   currentState={0}, Position={1} qty={2}",
                    currentState, Position.MarketPosition, Position.Quantity));

                bool isEntryFill = false;
                if (execOrder != null && entryOrder != null && execOrderId == entryOrder.OrderId
                    && execState == OrderState.Filled)
                {
                    isEntryFill = true;
                }
                else if (execOrderName == EntrySignalName && execState == OrderState.Filled)
                {
                    isEntryFill = true;
                }

                if (isEntryFill && currentState == TradeState.WaitingForFill)
                {
                    actualFillPrice    = price;
                    fillTime           = DateTime.Now;
                    previousCheckPrice = 0;
                    lastMonitorCheckTime = DateTime.Now;

                    Print(string.Format("[EXEC]   *** ENTRY FILLED at {0} (limit was {1}) - SHORT ***",
                        actualFillPrice, calculatedLimitPrice));

                    if (EnableSoundOnFill)
                    {
                        try { PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav"); }
                        catch { }
                    }

                    Print("[EXEC]   Brackets already attached by NT (declared pre-entry).");
                    currentState = TradeState.InPosition;
                    Print(string.Format("[EXEC]   State now: {0}.", currentState));
                }

                if (Position.MarketPosition == MarketPosition.Flat
                    && currentState == TradeState.InPosition)
                {
                    finalExitPrice = price;
                    // SHORT-SPECIFIC: PnL is fillPrice - exitPrice (positive when price dropped).
                    finalPnLPoints = actualFillPrice - price;

                    finalExitReason = "UNKNOWN";
                    if (!string.IsNullOrEmpty(execOrderName))
                    {
                        if (execOrderName.Contains("Profit") || execOrderName.Contains("Target"))
                            finalExitReason = "PROFIT_TARGET";
                        else if (execOrderName.Contains("Stop"))
                            finalExitReason = "STOP_HIT";
                        else if (execOrderName.Contains("Close") || execOrderName.Contains("Exit"))
                            finalExitReason = "MANUAL_OR_OTHER";
                        else
                            finalExitReason = execOrderName;
                    }

                    Print(string.Format("[EXEC]   *** POSITION CLOSED at {0} ({1}). PnL = {2:F2} pts ***",
                        finalExitPrice, finalExitReason, finalPnLPoints));

                    CleanupRemainingBrackets();

                    closingDelayStartTime = DateTime.Now;
                    currentState = TradeState.ClosingDelay;
                    Print(string.Format("[EXEC]   State now: {0}. Waiting {1}s for OCO finalization.",
                        currentState, DisableDelaySeconds));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[EXEC] UNEXPECTED ERROR: {0}", ex.Message));
                Print(string.Format("[EXEC] Stack: {0}", ex.StackTrace));
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice,
            int quantity, MarketPosition marketPosition)
        {
            try
            {
                Print(string.Format("[POSITION] OnPositionUpdate at {0}: marketPosition={1}, quantity={2}, avgPrice={3}",
                    DateTime.Now.ToString("HH:mm:ss.fff"), marketPosition, quantity, averagePrice));
            }
            catch (Exception ex)
            {
                Print(string.Format("[POSITION] ERROR: {0}", ex.Message));
            }
        }

        private void ResetTradeState()
        {
            currentState           = TradeState.Idle;
            entryOrder             = null;
            stopLossOrder          = null;
            profitTargetOrder      = null;
            entryLastTradedPrice   = 0;
            calculatedLimitPrice   = 0;
            actualFillPrice        = 0;
            avgBarSizeAtEntry      = 0;
            previousCheckPrice     = 0;
            currentStopPrice       = 0;
            finalExitPrice         = 0;
            finalPnLPoints         = 0;
            finalExitReason        = "UNKNOWN";
        }

        // =====================================================================
        // WriteAuditLog: writes to ShortScalper.csv. Now includes LimitMode column.
        // =====================================================================
        private void WriteAuditLog(string outcome, double fillPrice, double exitPrice, double pnlPoints)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "ShortScalper.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("Timestamp,Instrument,Outcome,EnableTime,FillTime,ExitTime,"
                            + "LastPriceAtEnable,AvgBarSize,EntryOffsetMultiplier,LimitMode,LimitPrice,FillPrice,"
                            + "ExitPrice,PnLPoints,OrderLifeSeconds,MonitorIntervalSeconds,"
                            + "ProfitTargetPoints,HardStopPoints,PullbackTolerancePoints,TrailDistancePoints");
                    }

                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9},{10},{11},{12},{13:F2},{14},{15},{16},{17},{18},{19}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Instrument.FullName,
                        outcome,
                        entryOrderPlacedTime.ToString("HH:mm:ss"),
                        (fillTime != default(DateTime)) ? fillTime.ToString("HH:mm:ss") : "",
                        DateTime.Now.ToString("HH:mm:ss"),
                        entryLastTradedPrice,
                        avgBarSizeAtEntry,
                        EntryOffsetMultiplier,
                        LimitMode,
                        calculatedLimitPrice,
                        fillPrice,
                        exitPrice,
                        pnlPoints,
                        OrderLifeSeconds,
                        MonitorIntervalSeconds,
                        ProfitTargetPoints,
                        HardStopPoints,
                        PullbackTolerancePoints,
                        TrailDistancePoints));
                }
                Print(string.Format("[AUDIT] Wrote outcome '{0}' to log", outcome));
            }
            catch (Exception ex)
            {
                Print(string.Format("[AUDIT] ERROR: {0}", ex.Message));
            }
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Quantity", Description="Number of contracts per trade.", Order=1, GroupName="Trade Size")]
        public int Quantity { get; set; }

        // ---------------------------------------------------------------------
        // [v2.4 NEW] LimitMode dropdown.
        // ---------------------------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name="LimitMode",
            Description="How to compute the sell limit price. OffsetFromLastPrice (default) = lastPrice + (EntryOffsetMultiplier * avgBarSize). FixedPrice = use the FixedLimitPrice value below.",
            Order=2, GroupName="Entry")]
        public ShortEntryLimitMode LimitMode { get; set; }

        // ---------------------------------------------------------------------
        // [v2.4 NEW] FixedLimitPrice (only used when LimitMode = FixedPrice).
        // ---------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name="FixedLimitPrice",
            Description="The exact sell limit price to use when LimitMode = FixedPrice. Ignored if LimitMode = OffsetFromLastPrice. Set this much HIGHER than current price to bait spikes (sell-the-rip). Remember to also raise OrderLifeSeconds.",
            Order=3, GroupName="Entry")]
        public double FixedLimitPrice { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 10.0)]
        [Display(Name="EntryOffsetMultiplier", Description="Limit = lastPrice + (this x avgBarSize). Used only in OffsetFromLastPrice mode. Default 0.10 (10% of avg bar). Max 10.0 (10x avg bar size).", Order=4, GroupName="Entry")]
        public double EntryOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="BarSizeAveragePeriod", Description="Number of recent 1-min bars used in EMA volatility calc. Default 10.", Order=5, GroupName="Entry")]
        public int BarSizeAveragePeriod { get; set; }

        // ---------------------------------------------------------------------
        // [v2.4 CHANGED] Range max raised from 3600 to 86400 (24 hours).
        // ---------------------------------------------------------------------
        [NinjaScriptProperty]
        [Range(1, 86400)]
        [Display(Name="OrderLifeSeconds",
            Description="Cancel sell limit if not filled within this many seconds. Default 5 (fast scalp). For FixedPrice mode set this large: 3600=1hr, 36000=10hr, 86400=24hr.",
            Order=6, GroupName="Entry")]
        public int OrderLifeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name="ProfitTargetPoints", Description="Profit target = expectedFill - this many points. Default 10.", Order=7, GroupName="Exit")]
        public int ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="HardStopPoints", Description="INITIAL stop = expectedFill + this many points. Default 20.", Order=8, GroupName="Exit")]
        public int HardStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name="MonitorIntervalSeconds", Description="How often the trailing check runs. Default 3.", Order=9, GroupName="Trailing")]
        public int MonitorIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="PullbackTolerancePoints", Description="Tolerated rally before considering a real reversal. Default 2.", Order=10, GroupName="Trailing")]
        public int PullbackTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="TrailDistancePoints", Description="Trailed stop sits this many points ABOVE current price. Default 8.", Order=11, GroupName="Trailing")]
        public int TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name="DisableDelaySeconds", Description="Delay between position close and strategy disable. Default 1.5.", Order=12, GroupName="Cleanup")]
        public double DisableDelaySeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableSoundOnFill", Description="Play sound when entry fills.", Order=13, GroupName="Notifications")]
        public bool EnableSoundOnFill { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath", Description="Folder for audit log CSV. Auto-created if missing. Default C:\\temp.", Order=14, GroupName="Logging")]
        public string AuditLogPath { get; set; }

        #endregion
    }
}
