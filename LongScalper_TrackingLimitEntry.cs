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
//  STRATEGY:    LongScalper_TrackingLimit v1.1
//  AUTHOR:      Albert Feng / Drafted with help from Claude
//  REPLACES:    LongScalper_TrackingLimit v1.0
// =============================================================================
//
//  v1.1 CHANGES vs v1.0
//  --------------------
//  Two changes brought in from LongScalper v2.6 — same purpose, same
//  semantics. The TRACKING-LIMIT entry logic is unchanged from v1.0.
//
//  CHANGE A: DEFENSIVE ORPHAN-BRACKET CLEANUP
//  ------------------------------------------
//  Real-world incident: strategy filled, attached brackets. User then
//  manually placed a sell order which closed the position. Account went
//  flat but the strategy's stop loss and profit target STAYED ALIVE.
//  Later, market touched one of those orphan orders and created a NEW
//  unintended position (short instead of flat).
//
//  v1.1 adds CheckForOrphanedState() at the top of every OnBarUpdate.
//  If the account is flat but the strategy thinks it's still managing a
//  trade, force CleanupRemainingBrackets() and disable the strategy.
//
//  CHANGE B: STOP LOSS REPLACED ATR -> OFFSET FROM FILLED PRICE
//  ------------------------------------------------------------
//  v1.0 used ATR(14) for stop distances. ATR is a separate concept the
//  user must learn to tune. v1.1 unifies on the SAME concept already
//  used for the ENTRY: avgBarSize (EMA of recent 1-min bar high-low
//  ranges).
//
//  Old (v1.0):
//    Initial stop = ATR x AtrInitialStopMultiplier (default 2.5)
//    Trail        = ATR x AtrTrailMultiplier        (default 1.0)
//
//  New (v1.1):
//    Initial stop = avgBarSize x StopOffsetMultiplier   (default 2.0)
//    Trail        = avgBarSize x TrailOffsetMultiplier  (default 0.8)
//
//  avgBarSize is calculated ONCE at entry (already done for the limit
//  price calculation) and held constant for the trade's lifetime — same
//  baseline for entry, initial stop, and trail.
//
//  Default multipliers are chosen so that on typical MNQ volatility
//  (avgBarSize ~ 10 pts), stops behave like the old fixed-point defaults
//  (HardStopPoints=20, TrailDistancePoints=8).
//
//  Removed parameters: AtrPeriod, AtrInitialStopMultiplier, AtrTrailMultiplier
//  Added parameters:   StopOffsetMultiplier, TrailOffsetMultiplier
//  Kept:               HardStopPoints, TrailDistancePoints (FixedPoints mode)
//
//  StopMode enum renamed:
//    AtrBased -> OffsetFromFilledPrice (semantic, not "ATR" anymore)
//    FixedPoints -> FixedPoints (unchanged)
//
//  Note: TrackingStopLossMode (this strategy's enum) is renamed to match
//  the new naming. It is still SEPARATE from LongScalper's StopLossMode
//  enum so the two strategies don't conflict at compile time.
//
// =============================================================================
//  WHAT'S UNCHANGED FROM v1.0 - THE TRACKING-LIMIT BEHAVIOR
// =============================================================================
//
//  This strategy's UNIQUE feature is the floating entry limit:
//
//    - At enable: submit limit at currentPrice - (offset)
//    - Every TrackingIntervalSeconds (1 sec default), recalculate
//    - If new limit differs from current by 2+ ticks, ChangeOrder() to modify
//    - OrderLifeSeconds timer (5 sec default) is NOT reset by tracking mods
//    - On fill: brackets attached based on actual fill price
//
//  All of that is unchanged in v1.1. Only the stop calculation and the
//  defensive cleanup are new.
//
// =============================================================================
//  HOW TO USE
// =============================================================================
//
//  Same as v1.0:
//    1. Compile (F5).
//    2. Right-click chart -> Strategies -> Add LongScalper_TrackingLimit.
//    3. Set parameters (defaults are tuned for MNQ).
//    4. Set Stop behavior = "Close position".
//    5. Verify Positions and Orders tabs are empty.
//    6. Enable.
//
//  PARAMETER DEFAULTS (v1.1)
//  -------------------------
//    Quantity                  1
//    EntryOffsetMultiplier     0.10
//    BarSizeAveragePeriod      10
//    OrderLifeSeconds          5
//    TrackingIntervalSeconds   1
//    TrackingMinTickChange     2
//    ProfitTargetPoints        10
//    StopMode                  OffsetFromFilledPrice    <-- NEW DEFAULT
//    StopOffsetMultiplier      2.0                       <-- NEW
//    TrailOffsetMultiplier     0.8                       <-- NEW
//    HardStopPoints            20  (FixedPoints mode only)
//    TrailDistancePoints       8   (FixedPoints mode only)
//    MonitorIntervalSeconds    3
//    PullbackTolerancePoints   2
//    DisableDelaySeconds       1.5
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    // [v1.1] Renamed from AtrBased -> OffsetFromFilledPrice.
    // Kept as TrackingStopLossMode (separate enum) so this strategy and
    // LongScalper can compile in the same project without enum collisions.
    public enum TrackingStopLossMode
    {
        OffsetFromFilledPrice,
        FixedPoints
    }

    public class LongScalper_TrackingLimit : Strategy
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

        // [v1.0] Tracking-limit state
        private DateTime lastTrackingCheckTime;        // wall-clock time of last recalc
        private int      trackingModificationCount;    // how many times we modified the order

        private double entryLastTradedPrice = 0;
        private double calculatedLimitPrice = 0;
        private double actualFillPrice      = 0;
        private double avgBarSizeAtEntry    = 0;
        private double previousCheckPrice   = 0;
        private double currentStopPrice     = 0;
        private double finalExitPrice       = 0;
        private double finalPnLPoints       = 0;
        private string finalExitReason      = "UNKNOWN";

        // [v1.1] Replaces atrAtEntry; serves the same audit purpose
        private double initialStopDistance   = 0;

        private const int MinuteBarsIndex = 1;
        private const string EntrySignalName = "TrackingLimitEntry";

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "Long-only scalper with TRACKING limit entry (recalc every 1 sec) and bar-size-based adaptive stops (v1.1).";
                Name                                        = "LongScalper_TrackingLimit";
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
                Quantity                  = 1;

                // Entry
                EntryOffsetMultiplier     = 0.10;
                BarSizeAveragePeriod      = 10;
                OrderLifeSeconds          = 5;

                // [v1.0] Tracking parameters
                TrackingIntervalSeconds   = 1;
                TrackingMinTickChange     = 2;

                // Profit target
                ProfitTargetPoints        = 10;

                // [v1.1] Stop loss defaults - replaces ATR-based with bar-size-based
                StopMode                  = TrackingStopLossMode.OffsetFromFilledPrice;
                StopOffsetMultiplier      = 2.0;
                TrailOffsetMultiplier     = 0.8;

                // Fixed-points stops (used only when StopMode = FixedPoints).
                HardStopPoints            = 20;
                TrailDistancePoints       = 8;

                // Trailing
                MonitorIntervalSeconds    = 3;
                PullbackTolerancePoints   = 2;

                // Cleanup
                DisableDelaySeconds       = 1.5;

                // Notifications
                EnableSoundOnFill         = true;

                // Logging
                AuditLogPath              = @"C:\temp";
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
                Print(string.Format("[INIT] LongScalper_TrackingLimit v1.1 armed at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] Tracking: recalc every {0}s, min change {1} ticks ({2} pts)",
                    TrackingIntervalSeconds, TrackingMinTickChange,
                    TrackingMinTickChange * TickSize));
                Print(string.Format("[INIT] Entry: offset multiplier {0} x avgBarSize, OrderLife {1}s",
                    EntryOffsetMultiplier, OrderLifeSeconds));
                Print(string.Format("[INIT] StopMode={0}", StopMode));
                if (StopMode == TrackingStopLossMode.OffsetFromFilledPrice)
                {
                    Print(string.Format("[INIT]   StopOffsetMult={0}, TrailOffsetMult={1} (x avgBarSize)",
                        StopOffsetMultiplier, TrailOffsetMultiplier));
                }
                else
                {
                    Print(string.Format("[INIT]   HardStop={0}pts, Trail={1}pts",
                        HardStopPoints, TrailDistancePoints));
                }
                Print(string.Format("[INIT] Other: Target={0}pts, Monitor={1}s, Tolerance={2}pts, DisableDelay={3}s",
                    ProfitTargetPoints, MonitorIntervalSeconds,
                    PullbackTolerancePoints, DisableDelaySeconds));
                Print("[INIT] Defensive cleanup: ENABLED (auto-disable if account flattens unexpectedly)");
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

            // -----------------------------------------------------------------
            // [v1.1 NEW] DEFENSIVE CLEANUP CHECK
            //
            // Detect when account has gone flat unexpectedly while the
            // strategy thinks it's still managing a trade.
            // -----------------------------------------------------------------
            CheckForOrphanedState();

            switch (currentState)
            {
                case TradeState.Idle:
                    PlaceEntryOrder();
                    break;

                case TradeState.WaitingForFill:
                    // [v1.0] Tracking: recalculate limit every TrackingIntervalSeconds
                    DoTrackingCheck();

                    // Existing timeout check
                    if ((DateTime.Now - entryOrderPlacedTime).TotalSeconds >= OrderLifeSeconds)
                    {
                        if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
                        {
                            Print(string.Format("[TIMEOUT] Order life expired ({0}s, {1} modifications). Cancelling.",
                                OrderLifeSeconds, trackingModificationCount));
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
                        Print("[CLOSE] Delay elapsed. Writing audit and disabling.");
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

        // =====================================================================
        // [v1.1 NEW] CheckForOrphanedState
        //
        // Detects desync between strategy view and actual account position.
        // If currentState is InPosition (or WaitingForFill) but the ACCOUNT
        // shows flat, something happened outside our control. Force cleanup
        // and disable.
        //
        // We check the ACCOUNT (not Position.MarketPosition) because the
        // strategy's internal Position can be out of sync with reality
        // depending on Sync settings.
        // =====================================================================
        private void CheckForOrphanedState()
        {
            if (currentState != TradeState.InPosition && currentState != TradeState.WaitingForFill)
                return;

            try
            {
                if (Account == null) return;

                Position acctPos = Account.Positions.FirstOrDefault(p => p.Instrument == Instrument);
                bool acctIsFlat = (acctPos == null || acctPos.MarketPosition == MarketPosition.Flat);

                if (!acctIsFlat) return;  // account has a position, all is well

                // ----- Account is FLAT but strategy thinks we have a trade -----

                if (currentState == TradeState.InPosition)
                {
                    Print("================================================================");
                    Print(string.Format("[ORPHAN] *** ACCOUNT FLATTENED UNEXPECTEDLY at {0} ***",
                        DateTime.Now.ToString("HH:mm:ss.fff")));
                    Print("[ORPHAN] Strategy state was InPosition but account shows flat.");
                    Print("[ORPHAN] Likely cause: manual close, broker action, or external trade.");
                    Print("[ORPHAN] Forcing cleanup of any orphan bracket orders and disabling strategy.");

                    finalExitReason = "ORPHAN_EXTERNAL_CLOSE";
                    finalExitPrice  = GetCurrentLastPrice();
                    finalPnLPoints  = (finalExitPrice > 0 && actualFillPrice > 0) ? (finalExitPrice - actualFillPrice) : 0;

                    CleanupRemainingBrackets();
                    WriteAuditLog(finalExitReason, actualFillPrice, finalExitPrice, finalPnLPoints);
                    currentState = TradeState.Done;
                    DisableStrategy();
                }
                else if (currentState == TradeState.WaitingForFill)
                {
                    bool entryStillWorking = entryOrder != null
                        && (entryOrder.OrderState == OrderState.Working
                            || entryOrder.OrderState == OrderState.Accepted
                            || entryOrder.OrderState == OrderState.Submitted);

                    if (!entryStillWorking)
                    {
                        bool foundOrphans = false;
                        lock (Account.Orders)
                        {
                            foreach (var ord in Account.Orders)
                            {
                                if (ord.Instrument == Instrument && IsOrderActive(ord))
                                {
                                    string name = ord.Name ?? "";
                                    if (name.Contains("Stop") || name.Contains("Profit")
                                        || name.Contains("Target") || name == EntrySignalName)
                                    {
                                        foundOrphans = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (foundOrphans)
                        {
                            Print("================================================================");
                            Print(string.Format("[ORPHAN] *** Found orphan orders while WaitingForFill at {0} ***",
                                DateTime.Now.ToString("HH:mm:ss.fff")));
                            Print("[ORPHAN] Cleaning up and disabling.");
                            CleanupRemainingBrackets();
                            WriteAuditLog("ORPHAN_PRE_FILL", 0, 0, 0);
                            currentState = TradeState.Done;
                            DisableStrategy();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[ORPHAN] ERROR in defensive check: {0}", ex.Message));
            }
        }

        // =====================================================================
        // [v1.0] DoTrackingCheck - UNCHANGED
        //
        // Every TrackingIntervalSeconds, recompute desired limit price from
        // currentPrice and avgBarSize. If different by 2+ ticks, ChangeOrder()
        // to modify atomically. Update brackets to match the new expected fill.
        // =====================================================================
        private void DoTrackingCheck()
        {
            if ((DateTime.Now - lastTrackingCheckTime).TotalSeconds < TrackingIntervalSeconds)
                return;

            if (entryOrder == null || entryOrder.OrderState != OrderState.Working)
            {
                lastTrackingCheckTime = DateTime.Now;
                return;
            }

            try
            {
                double currentPrice = GetCurrentLastPrice();
                if (currentPrice <= 0)
                {
                    lastTrackingCheckTime = DateTime.Now;
                    return;
                }

                double offset = EntryOffsetMultiplier * avgBarSizeAtEntry;
                double newLimitPrice = Instrument.MasterInstrument.RoundToTickSize(currentPrice - offset);

                double currentLimit = entryOrder.LimitPrice;
                double priceDelta = Math.Abs(newLimitPrice - currentLimit);
                double minDelta = TrackingMinTickChange * TickSize;

                if (priceDelta >= minDelta)
                {
                    Print(string.Format("[TRACK] Modifying limit: current={0:F2}, new={1:F2} (delta={2:F2}, currentPrice={3:F2})",
                        currentLimit, newLimitPrice, priceDelta, currentPrice));

                    ChangeOrder(entryOrder, entryOrder.Quantity, newLimitPrice, 0);
                    calculatedLimitPrice = newLimitPrice;
                    trackingModificationCount++;

                    // Update brackets to match new expected fill
                    double newTarget = Instrument.MasterInstrument.RoundToTickSize(newLimitPrice + ProfitTargetPoints);
                    double newStop;
                    // [v1.1] Use OffsetFromFilledPrice (avgBarSize x multiplier) instead of ATR
                    if (StopMode == TrackingStopLossMode.OffsetFromFilledPrice)
                    {
                        double stopDistance = avgBarSizeAtEntry * StopOffsetMultiplier;
                        newStop = Instrument.MasterInstrument.RoundToTickSize(newLimitPrice - stopDistance);
                    }
                    else
                    {
                        newStop = Instrument.MasterInstrument.RoundToTickSize(newLimitPrice - HardStopPoints);
                    }

                    try
                    {
                        SetProfitTarget(EntrySignalName, CalculationMode.Price, newTarget);
                        SetStopLoss(EntrySignalName, CalculationMode.Price, newStop, false);
                        currentStopPrice = newStop;
                    }
                    catch (Exception ex)
                    {
                        Print(string.Format("[TRACK]   WARN updating brackets: {0}", ex.Message));
                    }
                }
                else
                {
                    Print(string.Format("[TRACK] No change: current limit={0:F2}, new calc={1:F2}, delta {2:F2} < {3:F2}",
                        currentLimit, newLimitPrice, priceDelta, minDelta));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[TRACK] UNEXPECTED ERROR: {0}", ex.Message));
            }
            finally
            {
                lastTrackingCheckTime = DateTime.Now;
            }
        }

        private void PlaceEntryOrder()
        {
            Print("================================================================");
            Print(string.Format("[STEP1] PlaceEntryOrder called at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
            Print(string.Format("[STEP1] Position: {0} qty={1}", Position.MarketPosition, Position.Quantity));

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

            // Calculate avgBarSize ONCE (held constant for the order's lifetime)
            avgBarSizeAtEntry = CalculateEmaBarSize();
            if (avgBarSizeAtEntry <= 0)
            {
                Print("[STEP1] ERROR: Could not calculate bar size. Disabling.");
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            double offset = EntryOffsetMultiplier * avgBarSizeAtEntry;
            calculatedLimitPrice = Instrument.MasterInstrument.RoundToTickSize(entryLastTradedPrice - offset);

            Print(string.Format("[STEP1] Initial: lastPrice={0}, avgBarSize={1:F2}, offset={2:F2}, limit={3} (BELOW market)",
                entryLastTradedPrice, avgBarSizeAtEntry, offset, calculatedLimitPrice));

            // ----- Pre-declare brackets BEFORE entry -----
            double expectedFillPrice = calculatedLimitPrice;
            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice + ProfitTargetPoints);

            // [v1.1] Initial stop calculation - branch on StopMode.
            double initialStop;
            if (StopMode == TrackingStopLossMode.OffsetFromFilledPrice)
            {
                initialStopDistance = avgBarSizeAtEntry * StopOffsetMultiplier;
                initialStop = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice - initialStopDistance);
                Print(string.Format("[STEP1] OffsetFromFilledPrice stop: avgBarSize={0:F2}, mult={1}, distance={2:F2}, stop={3}",
                    avgBarSizeAtEntry, StopOffsetMultiplier, initialStopDistance, initialStop));
            }
            else
            {
                initialStopDistance = HardStopPoints;
                initialStop = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice - HardStopPoints);
                Print(string.Format("[STEP1] FixedPoints stop: distance={0}pts, stop={1}",
                    HardStopPoints, initialStop));
            }

            try
            {
                SetProfitTarget(EntrySignalName, CalculationMode.Price, targetPrice);
                SetStopLoss(EntrySignalName, CalculationMode.Price, initialStop, false);
                currentStopPrice = initialStop;
                Print(string.Format("[STEP1] Pre-declared brackets: target={0}, stop={1}", targetPrice, initialStop));
            }
            catch (Exception ex)
            {
                Print(string.Format("[STEP1] ERROR setting brackets: {0}. Disabling.", ex.Message));
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            // [v1.0] Initialize tracking state
            entryOrderPlacedTime    = DateTime.Now;
            lastTrackingCheckTime   = DateTime.Now;     // start the throttle clock
            trackingModificationCount = 0;

            currentState = TradeState.WaitingForFill;
            entryOrder = EnterLongLimit(0, true, Quantity, calculatedLimitPrice, EntrySignalName);
            Print(string.Format("[STEP1] Buy limit submitted at {0}. Tracking enabled (every {1}s, min {2} ticks). State: {3}",
                calculatedLimitPrice, TrackingIntervalSeconds, TrackingMinTickChange, currentState));
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
        // [v1.1] DoTrailingCheck: trail distance is now avgBarSize x TrailOffsetMultiplier
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

                double threshold = referencePrice - PullbackTolerancePoints;

                // [v1.1] Compute trail distance based on StopMode.
                double trailDistance;
                if (StopMode == TrackingStopLossMode.OffsetFromFilledPrice)
                {
                    trailDistance = avgBarSizeAtEntry * TrailOffsetMultiplier;
                }
                else
                {
                    trailDistance = TrailDistancePoints;
                }

                Print(string.Format("[MONITOR] Check at {0}, currentPrice={1}, ref={2}, threshold={3}, trailDist={4:F2}",
                    DateTime.Now.ToString("HH:mm:ss.fff"),
                    currentPrice, referencePrice, threshold, trailDistance));

                if (currentPrice < threshold)
                {
                    Print(string.Format("[MONITOR]   Pullback detected (price fell). Letting NT stop {0} handle exit.",
                        currentStopPrice));
                    previousCheckPrice = currentPrice;
                    lastMonitorCheckTime = DateTime.Now;
                }
                else
                {
                    double proposedStop = Instrument.MasterInstrument.RoundToTickSize(
                        currentPrice - trailDistance);

                    if (proposedStop > currentStopPrice)
                    {
                        Print(string.Format("[MONITOR]   Trailing stop UP from {0} to {1}",
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
                        Print(string.Format("[MONITOR]   Stop stays at {0} (proposed {1} not higher)",
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
                    Print("[CLEANUP]   Cancelling tracked stop loss order");
                    CancelOrder(stopLossOrder);
                    cancelled++;
                }
                if (profitTargetOrder != null && IsOrderActive(profitTargetOrder))
                {
                    Print("[CLEANUP]   Cancelling tracked profit target order");
                    CancelOrder(profitTargetOrder);
                    cancelled++;
                }
                if (entryOrder != null && IsOrderActive(entryOrder))
                {
                    Print("[CLEANUP]   Cancelling tracked entry order");
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
                    Print(string.Format("[ORDER]   Entry order ended without fill: {0} (after {1} modifications)",
                        orderState, trackingModificationCount));
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

                    Print(string.Format("[EXEC]   *** ENTRY FILLED at {0} (limit was {1}, after {2} tracking mods) - LONG ***",
                        actualFillPrice, calculatedLimitPrice, trackingModificationCount));

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
                    finalPnLPoints = price - actualFillPrice;

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
            initialStopDistance    = 0;
            trackingModificationCount = 0;
            lastTrackingCheckTime  = DateTime.MinValue;
        }

        // =====================================================================
        // WriteAuditLog - writes to LongScalper_TrackingLimit.csv
        // [v1.1] Replaced AtrAtEntry column with StopOffsetMultiplier and
        //        TrailOffsetMultiplier
        // =====================================================================
        private void WriteAuditLog(string outcome, double fillPrice, double exitPrice, double pnlPoints)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "LongScalper_TrackingLimit.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("Timestamp,Instrument,Outcome,EnableTime,FillTime,ExitTime,"
                            + "LastPriceAtEnable,AvgBarSize,EntryOffsetMultiplier,LimitPriceFinal,FillPrice,"
                            + "ExitPrice,PnLPoints,OrderLifeSeconds,TrackingIntervalSecs,TrackingMods,"
                            + "MonitorIntervalSeconds,ProfitTargetPoints,HardStopPoints,"
                            + "PullbackTolerancePoints,TrailDistancePoints,StopMode,"
                            + "StopOffsetMultiplier,TrailOffsetMultiplier,InitialStopDistance");
                    }

                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9},{10},{11},{12:F2},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22:F2},{23:F2},{24:F2}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Instrument.FullName,
                        outcome,
                        entryOrderPlacedTime.ToString("HH:mm:ss"),
                        (fillTime != default(DateTime)) ? fillTime.ToString("HH:mm:ss") : "",
                        DateTime.Now.ToString("HH:mm:ss"),
                        entryLastTradedPrice,
                        avgBarSizeAtEntry,
                        EntryOffsetMultiplier,
                        calculatedLimitPrice,
                        fillPrice,
                        exitPrice,
                        pnlPoints,
                        OrderLifeSeconds,
                        TrackingIntervalSeconds,
                        trackingModificationCount,
                        MonitorIntervalSeconds,
                        ProfitTargetPoints,
                        HardStopPoints,
                        PullbackTolerancePoints,
                        TrailDistancePoints,
                        StopMode,
                        StopOffsetMultiplier,
                        TrailOffsetMultiplier,
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
        [Range(0.01, 10.0)]
        [Display(Name="EntryOffsetMultiplier", Description="Limit = currentPrice - (this x avgBarSize). Default 0.10. Recalculated every 1s during order life.", Order=2, GroupName="2. Entry")]
        public double EntryOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="BarSizeAveragePeriod", Description="Number of recent 1-min bars used in EMA volatility calc. Default 10. Used for entry AND for stops in OffsetFromFilledPrice mode.", Order=3, GroupName="2. Entry")]
        public int BarSizeAveragePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 86400)]
        [Display(Name="OrderLifeSeconds",
            Description="Cancel limit if not filled within this many seconds (timer starts at first submission, NOT reset on tracking modifications). Default 5.",
            Order=4, GroupName="2. Entry")]
        public int OrderLifeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="TrackingIntervalSeconds",
            Description="How often (wall-clock seconds) to recalculate the limit price and modify the order. Default 1.",
            Order=5, GroupName="2. Entry")]
        public int TrackingIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="TrackingMinTickChange",
            Description="Minimum number of TICKS the new calculated limit must differ from the current order before we modify. Default 2 (= 0.50 pts on MNQ).",
            Order=6, GroupName="2. Entry")]
        public int TrackingMinTickChange { get; set; }

        // ---- Profit Target ----
        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name="ProfitTargetPoints", Description="Profit target = expectedFill + this many points. Default 10.", Order=7, GroupName="3. Profit Target")]
        public int ProfitTargetPoints { get; set; }

        // ---- Stop Loss ----
        [NinjaScriptProperty]
        [Display(Name="StopMode",
            Description="How to compute stop distances. OffsetFromFilledPrice (default, v1.1) = avgBarSize x multiplier (same concept as entry). FixedPoints = use HardStopPoints/TrailDistancePoints.",
            Order=8, GroupName="4. Stop Loss")]
        public TrackingStopLossMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 20.0)]
        [Display(Name="StopOffsetMultiplier",
            Description="[v1.1 NEW] Initial stop = fillPrice - (avgBarSize x this). Default 2.0. Used only in OffsetFromFilledPrice mode. On MNQ with avgBarSize ~10pts, this gives a 20pt stop.",
            Order=9, GroupName="4. Stop Loss")]
        public double StopOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 20.0)]
        [Display(Name="TrailOffsetMultiplier",
            Description="[v1.1 NEW] Trailing stop distance = avgBarSize x this. Default 0.8. Used only in OffsetFromFilledPrice mode. Tighter than initial stop.",
            Order=10, GroupName="4. Stop Loss")]
        public double TrailOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="HardStopPoints", Description="INITIAL stop distance in points. Used only in FixedPoints mode. Default 20.", Order=11, GroupName="4. Stop Loss")]
        public int HardStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="TrailDistancePoints", Description="Trailed stop distance in points. Used only in FixedPoints mode. Default 8.", Order=12, GroupName="4. Stop Loss")]
        public int TrailDistancePoints { get; set; }

        // ---- Trailing ----
        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name="MonitorIntervalSeconds", Description="How often the trailing check runs (wall-clock seconds). Default 3.", Order=13, GroupName="5. Trailing")]
        public int MonitorIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="PullbackTolerancePoints", Description="Tolerated pullback before considering a real reversal. Default 2.", Order=14, GroupName="5. Trailing")]
        public int PullbackTolerancePoints { get; set; }

        // ---- Cleanup ----
        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name="DisableDelaySeconds", Description="Delay between position close and strategy disable. Default 1.5.", Order=15, GroupName="6. Cleanup")]
        public double DisableDelaySeconds { get; set; }

        // ---- Notifications ----
        [NinjaScriptProperty]
        [Display(Name="EnableSoundOnFill", Description="Play sound when entry fills.", Order=16, GroupName="7. Notifications")]
        public bool EnableSoundOnFill { get; set; }

        // ---- Logging ----
        [NinjaScriptProperty]
        [Display(Name="AuditLogPath", Description="Folder for audit log CSV. Auto-created if missing. Default C:\\temp.", Order=17, GroupName="8. Logging")]
        public string AuditLogPath { get; set; }

        #endregion
    }
}
