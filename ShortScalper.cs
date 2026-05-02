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
//  STRATEGY:    ShortScalper v2.5
//  AUTHOR:      Drafted with help from Claude
//  VERSION:     2.5 - ATR-BASED ADAPTIVE STOPS
// =============================================================================
//
//  Mirror of LongScalper v2.5 for shorts. Same orphan-order cleanup, same
//  trailing logic, same state machine, same ATR-based stops. Only the
//  directional math is flipped.
//
// =============================================================================
//  HOW TO USE THIS STRATEGY
// =============================================================================
//
//  CHART SETUP
//  -----------
//  Chart bar size does NOT matter for the strategy logic. The strategy
//  internally adds its own 1-min data series (via AddDataSeries) and uses
//  that for ALL calculations: avgBarSize for entry offset, ATR for stops,
//  trailing checks, etc. The chart bar size only affects what YOU see.
//
//  Recommended: 1-min chart. Why?
//    - Visually matches what the strategy is doing internally.
//    - Output Window timestamps line up with bars you can see.
//    - Easier to debug or audit what happened.
//
//  Other sizes (5-min, 15-min, 30-min) work fine but you lose visual
//  alignment with the strategy's actual decisions. Tick charts also work.
//
//  PRELOADED DATA
//  --------------
//  When you enable the strategy, NT has already loaded historical bars
//  on your chart. BarsRequiredToTrade = 20 is satisfied immediately as
//  long as your chart shows at least 20 bars of history (almost always
//  true). The strategy is ready to trade at the moment of enable.
//
//  ENABLING - STEP BY STEP
//  -----------------------
//  1. Compile in NinjaScript Editor (F5). Confirm "Compile succeeded."
//  2. Right-click chart -> Strategies -> Add ShortScalper.
//  3. In the strategy parameters dialog:
//       - Group "1. Trade Size":     set Quantity (default 1)
//       - Group "2. Entry":          choose LimitMode
//                                      OffsetFromLastPrice (default) for fast scalp
//                                      FixedPrice for high-limit bait
//                                    if FixedPrice, set FixedLimitPrice
//                                      ABOVE current price (sell-the-rip)
//                                    set OrderLifeSeconds large for FixedPrice
//                                      (e.g. 3600 = 1 hr, 86400 = 24 hr)
//       - Group "3. Profit Target":  ProfitTargetPoints (default 10)
//       - Group "4. Stop Loss":      StopMode = AtrBased (default, recommended)
//                                    AtrPeriod (default 14)
//                                    AtrInitialStopMultiplier (default 2.5)
//                                    AtrTrailMultiplier (default 1.0)
//                                    HardStopPoints/TrailDistancePoints
//                                      only used if StopMode = FixedPoints
//       - Group "5. Trailing":       MonitorIntervalSeconds (default 3)
//                                    PullbackTolerancePoints (default 2)
//       - Group "6. Cleanup":        DisableDelaySeconds (default 1.5)
//       - Group "7. Notifications":  EnableSoundOnFill (default true)
//       - Group "8. Logging":        AuditLogPath (default C:\temp)
//  4. Set "Stop behavior" = "Close position".
//  5. Verify Positions and Orders tabs are empty.
//  6. Enable. The strategy will execute ONE trade then disable itself.
//
//  TYPICAL USE CASES
//  -----------------
//  CASE A - Fast scalp during active trading:
//    LimitMode = OffsetFromLastPrice
//    EntryOffsetMultiplier = 0.10 (small offset, fills quickly)
//    OrderLifeSeconds = 5
//    StopMode = AtrBased
//    Use this when you see a SHORT setup live and want to react fast.
//
//  CASE B - High-limit bait order (sell-the-rip during work or sleep):
//    LimitMode = FixedPrice
//    FixedLimitPrice = (number well ABOVE current price)
//    OrderLifeSeconds = 86400 (24 hours)
//    StopMode = AtrBased
//    For multiple bait orders at different prices, add the strategy
//    multiple times via NT Control Center. Each runs independently.
//
//    REMEMBER: For shorts, FixedLimitPrice is ABOVE current price
//    (you want to sell on a spike up). For longs, it's BELOW.
//
//  RUNNING MULTIPLE COPIES
//  -----------------------
//  Each strategy instance is independent. You can run several at once
//  with different parameters. The audit log appends rows from all
//  instances to the same ShortScalper.csv file.
//
//  LIMITATIONS
//  -----------
//  1. ONE TRADE PER ENABLE. After fill -> exit -> the strategy DISABLES
//     itself. To trade again you must re-enable it manually. This is
//     intentional: removes "what just happened" confusion and prevents
//     runaway behavior. If you want continuous trading, this strategy
//     is the wrong tool.
//
//  2. SHORT ONLY. This file places only sell limits. For longs, use
//     LongScalper. Don't try to long-bait by entering a fixed price
//     below current market - it will reject the order or behave
//     unexpectedly.
//
//  3. POSITION SIZE FIXED AT ENABLE. Quantity is set when you enable.
//     The strategy doesn't scale in or pyramid. One fill, one position.
//
//  4. NT MANAGED ORDER METHODS. Uses NT's built-in SetProfitTarget /
//     SetStopLoss for the bracket. Simplest, most reliable pattern for
//     retail use. The cleanup function handles the rare cases where
//     OCO doesn't auto-cancel cleanly.
//
//  5. WALL-CLOCK TIMERS. OrderLifeSeconds and MonitorIntervalSeconds
//     use real wall-clock time (DateTime.Now). In market replay, replay
//     speed compresses time, so a "5 second" order life might cancel
//     after just 1 replay-second. Use moderate replay speeds (5-10x).
//
//  6. NOT VALIDATED FOR ALL INSTRUMENTS. Defaults are tuned for MNQ on
//     1-min bars. Other instruments need parameter tuning.
//
//  7. NO RE-ENTRY AFTER LOSS. If a trade hits stop, strategy disables.
//     There's no "automatic try again" - by design, you re-evaluate
//     before re-enabling.
//
//  WHAT TO WATCH IN OUTPUT WINDOW
//  ------------------------------
//  [INIT]    - strategy started, parameter summary
//  [STEP1]   - entry order placement, with calculated prices
//  [ORDER]   - order state changes (Working, Filled, Cancelled)
//  [EXEC]    - fill events (entry fill, position close)
//  [MONITOR] - trailing stop checks (every MonitorIntervalSeconds)
//  [CLEANUP] - bracket order cancellations after exit
//  [DISABLE] - strategy shutting down
//  [AUDIT]   - row written to ShortScalper.csv
//  [TIMEOUT] - entry order lifetime expired before fill
//  [POSITION]- position update (informational)
//
// =============================================================================
//  v2.5 CHANGES vs v2.4
// =============================================================================
//  Adaptive stop loss using ATR (Average True Range), same as LongScalper v2.5.
//
//  THE NEW PARAMETERS
//  ------------------
//  StopMode (dropdown, default = AtrBased):
//    - AtrBased   : compute stops as ATR x multiplier (adapts to volatility)
//    - FixedPoints: use HardStopPoints / TrailDistancePoints (v2.4 way)
//
//  AtrPeriod (default 14):                Lookback period for ATR.
//  AtrInitialStopMultiplier (default 2.5): Initial stop = ATR x this. Wider.
//  AtrTrailMultiplier (default 1.0):       Trail stop = ATR x this. Tighter.
//
//  KEY DIRECTIONAL LOGIC (UNCHANGED)
//  ---------------------------------
//  - Entry limit:    lastPrice + offset (sell limit ABOVE market)
//  - Profit target:  expectedFill - ProfitTargetPoints (BELOW fill)
//  - Initial stop:   expectedFill + stopDistance (ABOVE fill)
//  - Healthy trade:  price moves DOWN, stop ratchets DOWN
//  - Pullback:       price rallies UP beyond tolerance
//  - PnL:            actualFillPrice - exitPrice (positive when price fell)
//  - Stop only ever moves DOWN, never up.
//
//  AUDIT LOG: Three new columns: StopMode, AtrAtEntry, InitialStopDistance.
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    // [v2.4] Limit-price mode for short entry.
    public enum ShortEntryLimitMode
    {
        OffsetFromLastPrice,
        FixedPrice
    }

    // [v2.5 NEW] Stop-loss mode for shorts.
    // Named ShortStopLossMode so it does NOT collide with LongScalper's
    // StopLossMode enum if both strategies are compiled together.
    public enum ShortStopLossMode
    {
        AtrBased,
        FixedPoints
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

        // [v2.5 NEW] ATR tracking for audit log.
        private double atrAtEntry            = 0;
        private double initialStopDistance   = 0;

        // [v2.5 NEW] ATR indicator on the 1-min series.
        private ATR atrIndicator;

        private const int MinuteBarsIndex = 1;
        private const string EntrySignalName = "ShortScalperEntry";

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "Short-only scalper with trailing stop, fixed-limit-price option, and ATR-based adaptive stops.";
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

                LimitMode               = ShortEntryLimitMode.OffsetFromLastPrice;
                FixedLimitPrice         = 0;

                EntryOffsetMultiplier   = 0.10;
                BarSizeAveragePeriod    = 10;
                OrderLifeSeconds        = 5;
                ProfitTargetPoints      = 10;

                // [v2.5 NEW] ATR-based stop defaults.
                StopMode                 = ShortStopLossMode.AtrBased;
                AtrPeriod                = 14;
                AtrInitialStopMultiplier = 2.5;
                AtrTrailMultiplier       = 1.0;

                // Fixed-points stops (used only when StopMode = FixedPoints).
                HardStopPoints          = 20;
                TrailDistancePoints     = 8;

                MonitorIntervalSeconds  = 3;
                PullbackTolerancePoints = 2;
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
                // [v2.5 NEW] Initialize ATR on the 1-min series.
                atrIndicator = ATR(BarsArray[MinuteBarsIndex], AtrPeriod);
                ResetTradeState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] ShortScalper v2.5 armed at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] LimitMode={0}, FixedLimitPrice={1}",
                    LimitMode, FixedLimitPrice));
                Print(string.Format("[INIT] StopMode={0}", StopMode));
                if (StopMode == ShortStopLossMode.AtrBased)
                {
                    Print(string.Format("[INIT]   AtrPeriod={0}, InitialStopMult={1}, TrailMult={2}",
                        AtrPeriod, AtrInitialStopMultiplier, AtrTrailMultiplier));
                }
                else
                {
                    Print(string.Format("[INIT]   HardStop={0}pts, Trail={1}pts",
                        HardStopPoints, TrailDistancePoints));
                }
                Print(string.Format("[INIT] Other: OrderLife={0}s, Monitor={1}s, Target={2}pts, Tolerance={3}pts, DisableDelay={4}s",
                    OrderLifeSeconds, MonitorIntervalSeconds, ProfitTargetPoints,
                    PullbackTolerancePoints, DisableDelaySeconds));
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
            if (BarsArray.Length > MinuteBarsIndex && CurrentBars[MinuteBarsIndex] < Math.Max(BarSizeAveragePeriod, AtrPeriod)) return;
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
            Print(string.Format("[STEP1] LimitMode={0}, StopMode={1}", LimitMode, StopMode));

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

            entryLastTradedPrice = GetCurrentLastPrice();
            if (entryLastTradedPrice <= 0)
            {
                Print("[STEP1] ERROR: Could not get current price. Disabling.");
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            // -----------------------------------------------------------------
            // [v2.4] Branch on LimitMode to compute the limit price.
            // SHORT-SPECIFIC: limit is ABOVE current price.
            // -----------------------------------------------------------------
            if (LimitMode == ShortEntryLimitMode.FixedPrice)
            {
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

            // -----------------------------------------------------------------
            // Compute target and initial stop based on StopMode.
            // SHORT-SPECIFIC: target BELOW fill, initial stop ABOVE fill.
            // -----------------------------------------------------------------
            double expectedFillPrice = calculatedLimitPrice;
            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice - ProfitTargetPoints);

            // [v2.5 NEW] Initial stop calculation - branch on StopMode.
            double initialStop;
            if (StopMode == ShortStopLossMode.AtrBased)
            {
                atrAtEntry = atrIndicator[0];
                if (atrAtEntry <= 0)
                {
                    Print("[STEP1] WARN: ATR is 0, falling back to fixed-points stop.");
                    initialStopDistance = HardStopPoints;
                }
                else
                {
                    initialStopDistance = atrAtEntry * AtrInitialStopMultiplier;
                }
                // SHORT: stop is ABOVE fill.
                initialStop = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice + initialStopDistance);
                Print(string.Format("[STEP1] ATR-based stop: ATR={0:F2}, multiplier={1}, distance={2:F2}, stopPrice={3} (above)",
                    atrAtEntry, AtrInitialStopMultiplier, initialStopDistance, initialStop));
            }
            else
            {
                atrAtEntry = atrIndicator[0];   // record for audit even if not used
                initialStopDistance = HardStopPoints;
                initialStop = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice + HardStopPoints);
                Print(string.Format("[STEP1] FixedPoints stop: distance={0}pts, stopPrice={1} (above)",
                    HardStopPoints, initialStop));
            }

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

        // =====================================================================
        // [v2.5] DoTrailingCheck: trail distance is now ATR-based when
        // StopMode = AtrBased. PullbackTolerance still uses fixed points.
        // SHORT-SPECIFIC: stop ratchets DOWN as price falls.
        // =====================================================================
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

                // [v2.5 NEW] Compute trail distance based on StopMode.
                double trailDistance;
                if (StopMode == ShortStopLossMode.AtrBased)
                {
                    double currentAtr = atrIndicator[0];
                    if (currentAtr <= 0)
                    {
                        trailDistance = TrailDistancePoints;  // safety fallback
                    }
                    else
                    {
                        trailDistance = currentAtr * AtrTrailMultiplier;
                    }
                }
                else
                {
                    trailDistance = TrailDistancePoints;
                }

                Print(string.Format("[MONITOR] Check at {0}, currentPrice={1}, ref={2}, threshold={3}, trailDist={4:F2}",
                    DateTime.Now.ToString("HH:mm:ss.fff"),
                    currentPrice, referencePrice, threshold, trailDistance));

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
                        currentPrice + trailDistance);

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
            atrAtEntry             = 0;
            initialStopDistance    = 0;
        }

        // =====================================================================
        // WriteAuditLog: writes to ShortScalper.csv with v2.5 columns.
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
                            + "ProfitTargetPoints,HardStopPoints,PullbackTolerancePoints,TrailDistancePoints,"
                            + "StopMode,AtrAtEntry,InitialStopDistance");
                    }

                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9},{10},{11},{12},{13:F2},{14},{15},{16},{17},{18},{19},{20},{21:F2},{22:F2}",
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
                        TrailDistancePoints,
                        StopMode,
                        atrAtEntry,
                        initialStopDistance));
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
        [Display(Name="Quantity", Description="Number of contracts per trade.", Order=1, GroupName="1. Trade Size")]
        public int Quantity { get; set; }

        // ---- Entry ----
        [NinjaScriptProperty]
        [Display(Name="LimitMode",
            Description="How to compute the sell limit price. OffsetFromLastPrice (default) = lastPrice + (EntryOffsetMultiplier * avgBarSize). FixedPrice = use the FixedLimitPrice value below.",
            Order=2, GroupName="2. Entry")]
        public ShortEntryLimitMode LimitMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name="FixedLimitPrice",
            Description="The exact sell limit price to use when LimitMode = FixedPrice. Ignored if LimitMode = OffsetFromLastPrice. Set this much HIGHER than current price to bait spikes (sell-the-rip). Remember to also raise OrderLifeSeconds.",
            Order=3, GroupName="2. Entry")]
        public double FixedLimitPrice { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 10.0)]
        [Display(Name="EntryOffsetMultiplier", Description="Limit = lastPrice + (this x avgBarSize). Used only in OffsetFromLastPrice mode. Default 0.10 (10% of avg bar). Max 10.0 (10x avg bar size).", Order=4, GroupName="2. Entry")]
        public double EntryOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="BarSizeAveragePeriod", Description="Number of recent 1-min bars used in EMA volatility calc for entry. Default 10.", Order=5, GroupName="2. Entry")]
        public int BarSizeAveragePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 86400)]
        [Display(Name="OrderLifeSeconds",
            Description="Cancel sell limit if not filled within this many seconds. Default 5 (fast scalp). For FixedPrice mode set this large: 3600=1hr, 36000=10hr, 86400=24hr.",
            Order=6, GroupName="2. Entry")]
        public int OrderLifeSeconds { get; set; }

        // ---- Profit Target ----
        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name="ProfitTargetPoints", Description="Profit target = expectedFill - this many points. Default 10.", Order=7, GroupName="3. Profit Target")]
        public int ProfitTargetPoints { get; set; }

        // ---- Stop Loss ----
        [NinjaScriptProperty]
        [Display(Name="StopMode",
            Description="How to compute stop distances. AtrBased (default) = adaptive, uses ATR x multiplier. FixedPoints = use HardStopPoints / TrailDistancePoints.",
            Order=8, GroupName="4. Stop Loss")]
        public ShortStopLossMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="AtrPeriod",
            Description="ATR lookback period (in 1-min bars). Default 14 (standard). Used only in AtrBased mode.",
            Order=9, GroupName="4. Stop Loss")]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name="AtrInitialStopMultiplier",
            Description="Initial stop distance = ATR x this. Default 2.5. Used only in AtrBased mode. Wider gives the trade room to breathe at entry.",
            Order=10, GroupName="4. Stop Loss")]
        public double AtrInitialStopMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name="AtrTrailMultiplier",
            Description="Trailing stop distance = ATR x this. Default 1.0. Used only in AtrBased mode. Tighter than initial since trade is already running.",
            Order=11, GroupName="4. Stop Loss")]
        public double AtrTrailMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="HardStopPoints", Description="INITIAL stop = expectedFill + this many points. Used only in FixedPoints mode. Default 20.", Order=12, GroupName="4. Stop Loss")]
        public int HardStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="TrailDistancePoints", Description="Trailed stop sits this many points ABOVE current price. Used only in FixedPoints mode. Default 8.", Order=13, GroupName="4. Stop Loss")]
        public int TrailDistancePoints { get; set; }

        // ---- Trailing ----
        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name="MonitorIntervalSeconds", Description="How often the trailing check runs. Default 3.", Order=14, GroupName="5. Trailing")]
        public int MonitorIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="PullbackTolerancePoints", Description="Tolerated rally before considering a real reversal. Default 2.", Order=15, GroupName="5. Trailing")]
        public int PullbackTolerancePoints { get; set; }

        // ---- Cleanup ----
        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name="DisableDelaySeconds", Description="Delay between position close and strategy disable. Default 1.5.", Order=16, GroupName="6. Cleanup")]
        public double DisableDelaySeconds { get; set; }

        // ---- Notifications ----
        [NinjaScriptProperty]
        [Display(Name="EnableSoundOnFill", Description="Play sound when entry fills.", Order=17, GroupName="7. Notifications")]
        public bool EnableSoundOnFill { get; set; }

        // ---- Logging ----
        [NinjaScriptProperty]
        [Display(Name="AuditLogPath", Description="Folder for audit log CSV. Auto-created if missing. Default C:\\temp.", Order=18, GroupName="8. Logging")]
        public string AuditLogPath { get; set; }

        #endregion
    }
}
