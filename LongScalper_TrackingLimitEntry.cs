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
//  STRATEGY:    LongScalper_TrackingLimitEntry v1.3.1
//  AUTHOR:      Albert Feng / Drafted with help from Claude
//  REPLACES:    LongScalper_TrackingLimitEntry v1.3
// =============================================================================
//
//  v1.3.1 BUGFIX vs v1.3
//  ---------------------
//  Fixes a compile error in v1.3's session-end check. v1.3 used the wrong
//  NinjaTrader 8 API to obtain session boundaries; the correct idiom is the
//  SessionIterator class.
//
//  Three small edits, ALL inside the session-safety feature. No logic change
//  to entry, stop, target, trailing, orphan cleanup, or anything else.
//
//  EDIT 1 (Variables region):
//      Added a SessionIterator field.
//
//          private SessionIterator sessionIterator;
//
//  EDIT 2 (State.Configure):
//      Initialize the SessionIterator after AddDataSeries.
//
//          sessionIterator = new SessionIterator(Bars);
//
//  EDIT 3 (CheckSessionSafety, around the session-lookup block):
//      Replaced the broken Bars.Session.GetNextBeginEnd(...) call with the
//      correct SessionIterator.GetNextSession(...) pattern.
//
//          Old (broken):
//              DateTime sBegin, sEnd;
//              if (!Bars.Session.GetNextBeginEnd(Time[0], out sBegin, out sEnd))
//                  return false;
//
//          New (correct):
//              if (sessionIterator == null) return false;
//              sessionIterator.GetNextSession(Time[0], true);
//              DateTime sBegin = sessionIterator.ActualSessionBegin;
//              DateTime sEnd   = sessionIterator.ActualSessionEnd;
//
//  Everything else in the file is byte-for-byte identical to v1.3.
//
// =============================================================================
//  v1.3 CHANGES (preserved here for reference)
// =============================================================================
//
//  New "no-entry window" safety feature before session close:
//
//    EnableNoEntryWindow         bool  default true   master switch
//    NoEntryMinutesBeforeClose   int   default 10     window size in minutes
//
//  Layered defense:
//    1. At -10min (configurable): no NEW trades start.
//    2. At  -30s  (existing):     any open position is force-flattened.
//
//  See in-code comments in CheckSessionSafety() for the per-state behavior.
//
// =============================================================================
//  PHILOSOPHY NOTE
// =============================================================================
//
//  This strategy is an INTRADAY SCALPER. It is not designed to hold overnight.
//  Missing the last 10 minutes of the trading session is an acceptable cost
//  for the safety of guaranteed flat-at-close.
//
//  IMPORTANT: Verify the chart's session template matches your intent.
//    Right-click chart -> Data Series -> Session Template
//    "CME US Index Futures RTH"  ends at 4:00 PM ET (cash equity close).
//    "CME US Index Futures ETH"  runs nearly 24h with break around 5 PM ET.
//
// =============================================================================
//  WHAT'S UNCHANGED FROM v1.3
// =============================================================================
//
//  - Both entry modes (TrackingLimit, Fixed_Price).
//  - Both stop modes (OffsetFromFilledPrice, FixedPoints).
//  - All conditional UI hiding (ShouldSerialize* methods).
//  - All defensive orphan-bracket cleanup.
//  - All pre-entry safety checks.
//  - Trailing logic, ClosingDelay/Done flow, audit logging structure.
//  - The 30-second session-close auto-flatten.
//  - All parameters and their defaults.
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum LongScalper_StopMode
    {
        OffsetFromFilledPrice,
        FixedPoints
    }

    public enum LongScalper_EntryMode
    {
        TrackingLimit,
        Fixed_Price
    }

    public class LongScalper_TrackingLimitEntry : Strategy
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
        private DateTime lastTrackingCheckTime;
        private int      trackingModificationCount;

        private double entryLastTradedPrice = 0;
        private double calculatedLimitPrice = 0;
        private double actualFillPrice      = 0;
        private double avgBarSizeAtEntry    = 0;
        private double previousCheckPrice   = 0;
        private double currentStopPrice     = 0;
        private double finalExitPrice       = 0;
        private double finalPnLPoints       = 0;
        private string finalExitReason      = "UNKNOWN";
        private double initialStopDistance   = 0;

        // [v1.3] cached session boundaries for the no-entry-window check
        private DateTime cachedSessionBegin   = DateTime.MinValue;
        private DateTime cachedSessionEnd     = DateTime.MinValue;
        private bool     loggedSessionInfo    = false;

        // [v1.3.1 NEW] SessionIterator for getting session begin/end correctly
        private SessionIterator sessionIterator;

        private const int MinuteBarsIndex = 1;
        private const string EntrySignalName = "TrackingLimitEntry";

        private const double STATIC_PRICE_MIN_RATIO = 0.5;
        private const double STATIC_PRICE_MAX_RATIO = 2.0;

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "Long-only scalper with tracking/static limit entry, bar-size-based stops, and session-end no-entry safety (v1.3.1).";
                Name                                        = "LongScalper_TrackingLimitEntry";
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
                EntryModeSelection        = LongScalper_EntryMode.TrackingLimit;
                Fixed_PricePrice          = 0;
                EntryOffsetMultiplier     = 0.10;
                BarSizeAveragePeriod      = 10;
                OrderLifeSeconds          = 5;
                TrackingIntervalSeconds   = 1;
                TrackingMinTickChange     = 2;

                // Profit target
                ProfitTargetPoints        = 10;

                // Stop loss
                StopMode                  = LongScalper_StopMode.OffsetFromFilledPrice;
                StopOffsetMultiplier      = 2.0;
                TrailOffsetMultiplier     = 1.2;
                HardStopPoints            = 20;
                TrailDistancePoints       = 10;

                // Trailing
                MonitorIntervalSeconds    = 3;
                PullbackTolerancePoints   = 2;

                // Cleanup
                DisableDelaySeconds       = 1.5;

                // Notifications
                EnableSoundOnFill         = true;

                // Logging
                AuditLogPath              = @"C:\temp";

                // [v1.3] Session safety
                EnableNoEntryWindow          = true;
                NoEntryMinutesBeforeClose    = 10;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                // [v1.3.1] Initialize SessionIterator here, after Bars is available.
                sessionIterator = new SessionIterator(Bars);
                ResetTradeState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] LongScalper_TrackingLimitEntry v1.3.1 armed at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] EntryMode={0}", EntryModeSelection));
                if (EntryModeSelection == LongScalper_EntryMode.TrackingLimit)
                {
                    Print(string.Format("[INIT]   Tracking: recalc every {0}s, min change {1} ticks ({2} pts)",
                        TrackingIntervalSeconds, TrackingMinTickChange,
                        TrackingMinTickChange * TickSize));
                    Print(string.Format("[INIT]   Entry offset: {0} x avgBarSize, OrderLife {1}s",
                        EntryOffsetMultiplier, OrderLifeSeconds));
                }
                else
                {
                    Print(string.Format("[INIT]   Fixed_Price price: {0:F2} (no tracking)",
                        Fixed_PricePrice));
                    Print(string.Format("[INIT]   OrderLife {0}s, sanity range x{1}..x{2} of market",
                        OrderLifeSeconds, STATIC_PRICE_MIN_RATIO, STATIC_PRICE_MAX_RATIO));
                }
                Print(string.Format("[INIT] StopMode={0}", StopMode));
                if (StopMode == LongScalper_StopMode.OffsetFromFilledPrice)
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
                Print(string.Format("[INIT] Session-close auto-flatten: ENABLED at -{0}s before close",
                    ExitOnSessionCloseSeconds));
                if (EnableNoEntryWindow)
                {
                    Print(string.Format("[INIT] No-entry window: ENABLED, {0} minutes before session close",
                        NoEntryMinutesBeforeClose));
                }
                else
                {
                    Print("[INIT] No-entry window: DISABLED (new entries allowed up to session-close flatten)");
                }
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

            // [v1.1] Defensive cleanup check
            CheckForOrphanedState();

            // [v1.3] Session safety check - no new entries near session close,
            // cancel waiting entries near session close.
            if (CheckSessionSafety()) return;   // returns true if it took terminal action

            switch (currentState)
            {
                case TradeState.Idle:
                    PlaceEntryOrder();
                    break;

                case TradeState.WaitingForFill:
                    if (EntryModeSelection == LongScalper_EntryMode.TrackingLimit)
                    {
                        DoTrackingCheck();
                    }

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
        // [v1.3 NEW, v1.3.1 fixed session lookup]
        // CheckSessionSafety
        //
        // Returns TRUE if the method took a terminal action (audit + disable)
        // so the caller should stop processing this OnBarUpdate cycle.
        // =====================================================================
        private bool CheckSessionSafety()
        {
            if (!EnableNoEntryWindow) return false;
            if (currentState == TradeState.Done || currentState == TradeState.ClosingDelay) return false;
            if (currentState == TradeState.InPosition) return false;  // let trade run

            try
            {
                // [v1.3.1] Use SessionIterator (correct NT8 API) instead of
                // the non-existent Bars.Session.GetNextBeginEnd call.
                if (sessionIterator == null) return false;

                sessionIterator.GetNextSession(Time[0], true);
                DateTime sBegin = sessionIterator.ActualSessionBegin;
                DateTime sEnd   = sessionIterator.ActualSessionEnd;

                // Defensive: if either end is uninitialized, bail out quietly.
                if (sEnd == DateTime.MinValue) return false;

                cachedSessionBegin = sBegin;
                cachedSessionEnd   = sEnd;

                if (!loggedSessionInfo)
                {
                    Print(string.Format("[SESSION] Current session: begin={0}, end={1}, cutoff={2} ({3} min before end)",
                        sBegin.ToString("yyyy-MM-dd HH:mm:ss"),
                        sEnd.ToString("yyyy-MM-dd HH:mm:ss"),
                        sEnd.AddMinutes(-NoEntryMinutesBeforeClose).ToString("yyyy-MM-dd HH:mm:ss"),
                        NoEntryMinutesBeforeClose));
                    loggedSessionInfo = true;
                }

                DateTime cutoffMoment = sEnd.AddMinutes(-NoEntryMinutesBeforeClose);
                if (DateTime.Now < cutoffMoment) return false;   // not yet in window

                // --- We ARE in the no-entry window ---

                if (currentState == TradeState.Idle)
                {
                    Print("================================================================");
                    Print(string.Format("[SESSION] *** NO-ENTRY WINDOW *** at {0}",
                        DateTime.Now.ToString("HH:mm:ss.fff")));
                    Print(string.Format("[SESSION] Within {0} min of session close ({1}). New entries blocked.",
                        NoEntryMinutesBeforeClose, sEnd.ToString("HH:mm:ss")));
                    Print("[SESSION] Re-enable on next session.");

                    WriteAuditLog("BLOCKED_NEAR_SESSION_END", 0, 0, 0);
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return true;
                }

                if (currentState == TradeState.WaitingForFill)
                {
                    // Race-condition guard: re-check entry order state right now.
                    // If a fill came in while we were processing, let it transition
                    // to InPosition normally via OnExecutionUpdate.
                    if (entryOrder != null && entryOrder.OrderState == OrderState.Filled)
                    {
                        Print("[SESSION] Race: entry filled at cutoff moment. Letting normal flow continue.");
                        return false;
                    }

                    Print("================================================================");
                    Print(string.Format("[SESSION] *** NO-ENTRY WINDOW *** at {0}",
                        DateTime.Now.ToString("HH:mm:ss.fff")));
                    Print(string.Format("[SESSION] Within {0} min of session close ({1}). Cancelling working entry order and brackets.",
                        NoEntryMinutesBeforeClose, sEnd.ToString("HH:mm:ss")));

                    CleanupRemainingBrackets();
                    WriteAuditLog("CANCELLED_NEAR_SESSION_END", 0, 0, 0);
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[SESSION] ERROR in safety check: {0}", ex.Message));
            }

            return false;
        }

        // [v1.1] CheckForOrphanedState - unchanged
        private void CheckForOrphanedState()
        {
            if (currentState != TradeState.InPosition && currentState != TradeState.WaitingForFill)
                return;

            try
            {
                if (Account == null) return;

                Position acctPos = Account.Positions.FirstOrDefault(p => p.Instrument == Instrument);
                bool acctIsFlat = (acctPos == null || acctPos.MarketPosition == MarketPosition.Flat);

                if (!acctIsFlat) return;

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

                    double newTarget = Instrument.MasterInstrument.RoundToTickSize(newLimitPrice + ProfitTargetPoints);
                    double newStop;
                    if (StopMode == LongScalper_StopMode.OffsetFromFilledPrice)
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
            Print(string.Format("[STEP1] PlaceEntryOrder called at {0}, EntryMode={1}",
                DateTime.Now.ToString("HH:mm:ss.fff"), EntryModeSelection));
            Print(string.Format("[STEP1] Position: {0} qty={1}", Position.MarketPosition, Position.Quantity));

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[STEP1] *** BLOCKED *** Strategy position is {0} qty={1}.",
                    Position.MarketPosition, Position.Quantity));
                WriteAuditLog("BLOCKED_NOT_FLAT", 0, 0, 0);
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            try
            {
                if (Account != null)
                {
                    Position acctPos = Account.Positions.FirstOrDefault(p => p.Instrument == Instrument);
                    if (acctPos != null && acctPos.MarketPosition != MarketPosition.Flat)
                    {
                        Print(string.Format("[STEP1] *** BLOCKED *** ACCOUNT position is {0} qty={1}.",
                            acctPos.MarketPosition, acctPos.Quantity));
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

            avgBarSizeAtEntry = CalculateEmaBarSize();
            if (avgBarSizeAtEntry <= 0)
            {
                Print("[STEP1] ERROR: Could not calculate bar size. Disabling.");
                currentState = TradeState.Done;
                DisableStrategy();
                return;
            }

            if (EntryModeSelection == LongScalper_EntryMode.TrackingLimit)
            {
                double offset = EntryOffsetMultiplier * avgBarSizeAtEntry;
                calculatedLimitPrice = Instrument.MasterInstrument.RoundToTickSize(entryLastTradedPrice - offset);

                Print(string.Format("[STEP1] TrackingLimit: lastPrice={0}, avgBarSize={1:F2}, offset={2:F2}, limit={3}",
                    entryLastTradedPrice, avgBarSizeAtEntry, offset, calculatedLimitPrice));
            }
            else
            {
                Print(string.Format("[STEP1] Fixed_Price: user-specified price={0:F2}, market price={1:F2}",
                    Fixed_PricePrice, entryLastTradedPrice));

                if (Fixed_PricePrice <= 0)
                {
                    Print(string.Format("[STEP1] *** BLOCKED *** Fixed_PricePrice = {0:F2} is invalid (must be > 0).",
                        Fixed_PricePrice));
                    WriteAuditLog("BLOCKED_INVALID_STATIC_PRICE", 0, 0, 0);
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return;
                }

                if (Fixed_PricePrice >= entryLastTradedPrice)
                {
                    Print(string.Format("[STEP1] *** BLOCKED *** Fixed_PricePrice {0:F2} >= current market {1:F2}.",
                        Fixed_PricePrice, entryLastTradedPrice));
                    WriteAuditLog("BLOCKED_STATIC_AT_OR_ABOVE_MARKET", 0, 0, 0);
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return;
                }

                double minSane = entryLastTradedPrice * STATIC_PRICE_MIN_RATIO;
                double maxSane = entryLastTradedPrice * STATIC_PRICE_MAX_RATIO;
                if (Fixed_PricePrice < minSane || Fixed_PricePrice > maxSane)
                {
                    Print(string.Format("[STEP1] *** BLOCKED *** Fixed_PricePrice {0:F2} outside sanity range [{1:F2} .. {2:F2}].",
                        Fixed_PricePrice, minSane, maxSane));
                    WriteAuditLog("BLOCKED_STATIC_PRICE_SANITY", 0, 0, 0);
                    currentState = TradeState.Done;
                    DisableStrategy();
                    return;
                }

                calculatedLimitPrice = Instrument.MasterInstrument.RoundToTickSize(Fixed_PricePrice);
                Print(string.Format("[STEP1] Fixed_Price validated. Limit = {0} (no tracking will occur).",
                    calculatedLimitPrice));
            }

            double expectedFillPrice = calculatedLimitPrice;
            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice + ProfitTargetPoints);

            double initialStop;
            if (StopMode == LongScalper_StopMode.OffsetFromFilledPrice)
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

            entryOrderPlacedTime      = DateTime.Now;
            lastTrackingCheckTime     = DateTime.Now;
            trackingModificationCount = 0;

            currentState = TradeState.WaitingForFill;
            entryOrder = EnterLongLimit(0, true, Quantity, calculatedLimitPrice, EntrySignalName);

            if (EntryModeSelection == LongScalper_EntryMode.TrackingLimit)
            {
                Print(string.Format("[STEP1] Buy limit submitted at {0}. Tracking enabled (every {1}s, min {2} ticks). State: {3}",
                    calculatedLimitPrice, TrackingIntervalSeconds, TrackingMinTickChange, currentState));
            }
            else
            {
                Print(string.Format("[STEP1] Buy limit submitted at {0} (STATIC, no tracking). OrderLife={1}s. State: {2}",
                    calculatedLimitPrice, OrderLifeSeconds, currentState));
            }
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

                double trailDistance;
                if (StopMode == LongScalper_StopMode.OffsetFromFilledPrice)
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
            cachedSessionBegin     = DateTime.MinValue;
            cachedSessionEnd       = DateTime.MinValue;
            loggedSessionInfo      = false;
        }

        private void WriteAuditLog(string outcome, double fillPrice, double exitPrice, double pnlPoints)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "LongScalper_TrackingLimitEntry.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("Timestamp,Instrument,Outcome,EnableTime,FillTime,ExitTime,"
                            + "LastPriceAtEnable,AvgBarSize,EntryMode,EntryOffsetMultiplier,Fixed_PricePrice,"
                            + "LimitPriceFinal,FillPrice,ExitPrice,PnLPoints,OrderLifeSeconds,"
                            + "TrackingIntervalSecs,TrackingMods,MonitorIntervalSeconds,ProfitTargetPoints,"
                            + "HardStopPoints,PullbackTolerancePoints,TrailDistancePoints,StopMode,"
                            + "StopOffsetMultiplier,TrailOffsetMultiplier,InitialStopDistance,"
                            + "EnableNoEntryWindow,NoEntryMinutesBeforeClose,SessionEnd");
                    }

                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7:F2},{8},{9},{10:F2},{11},{12},{13},{14:F2},{15},{16},{17},{18},{19},{20},{21},{22},{23},{24:F2},{25:F2},{26:F2},{27},{28},{29}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        Instrument.FullName,
                        outcome,
                        entryOrderPlacedTime.ToString("HH:mm:ss"),
                        (fillTime != default(DateTime)) ? fillTime.ToString("HH:mm:ss") : "",
                        DateTime.Now.ToString("HH:mm:ss"),
                        entryLastTradedPrice,
                        avgBarSizeAtEntry,
                        EntryModeSelection,
                        EntryOffsetMultiplier,
                        Fixed_PricePrice,
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
                        initialStopDistance,
                        EnableNoEntryWindow,
                        NoEntryMinutesBeforeClose,
                        (cachedSessionEnd != DateTime.MinValue) ? cachedSessionEnd.ToString("HH:mm:ss") : ""
                    ));
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

        [NinjaScriptProperty]
        [Display(Name="EntryModeSelection",
            Description="TrackingLimit (default) = recalculate every 1s and chase market down. Fixed_Price = submit at user-specified price exactly once, no tracking.",
            Order=2, GroupName="2. Entry")]
        public LongScalper_EntryMode EntryModeSelection { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 10.0)]
        [Display(Name="EntryOffsetMultiplier", Description="[TrackingLimit only] Limit = currentPrice - (this x avgBarSize). Default 0.10.", Order=3, GroupName="2. Entry")]
        public double EntryOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name="Fixed_PricePrice",
            Description="[Fixed_Price only] Exact limit price. Must be > 0, below market, within 0.5x-2.0x of market. Default 0.",
            Order=4, GroupName="2. Entry")]
        public double Fixed_PricePrice { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="BarSizeAveragePeriod", Description="Number of recent 1-min bars used in EMA volatility calc. Default 10.", Order=5, GroupName="2. Entry")]
        public int BarSizeAveragePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 86400)]
        [Display(Name="OrderLifeSeconds", Description="Cancel limit if not filled within this many seconds. Default 5.", Order=6, GroupName="2. Entry")]
        public int OrderLifeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="TrackingIntervalSeconds", Description="[TrackingLimit only] How often to recalculate the limit price. Default 1.", Order=7, GroupName="2. Entry")]
        public int TrackingIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="TrackingMinTickChange", Description="[TrackingLimit only] Minimum ticks the new calculated limit must differ from current order before modifying. Default 2.", Order=8, GroupName="2. Entry")]
        public int TrackingMinTickChange { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name="ProfitTargetPoints", Description="Profit target = expectedFill + this many points. Default 10.", Order=9, GroupName="3. Profit Target")]
        public int ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name="StopMode", Description="OffsetFromFilledPrice (default) = avgBarSize x multiplier. FixedPoints = use HardStopPoints/TrailDistancePoints.", Order=10, GroupName="4. Stop Loss")]
        public LongScalper_StopMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 20.0)]
        [Display(Name="StopOffsetMultiplier", Description="[OffsetFromFilledPrice only] Initial stop = fillPrice - (avgBarSize x this). Default 2.0.", Order=11, GroupName="4. Stop Loss")]
        public double StopOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 20.0)]
        [Display(Name="TrailOffsetMultiplier", Description="[OffsetFromFilledPrice only] Trailing stop distance = avgBarSize x this. Default 0.8.", Order=12, GroupName="4. Stop Loss")]
        public double TrailOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="HardStopPoints", Description="[FixedPoints only] Initial stop distance in points. Default 20.", Order=13, GroupName="4. Stop Loss")]
        public int HardStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="TrailDistancePoints", Description="[FixedPoints only] Trailed stop distance in points. Default 8.", Order=14, GroupName="4. Stop Loss")]
        public int TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name="MonitorIntervalSeconds", Description="How often the trailing check runs. Default 3.", Order=15, GroupName="5. Trailing")]
        public int MonitorIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="PullbackTolerancePoints", Description="Tolerated pullback before considering a real reversal. Default 2.", Order=16, GroupName="5. Trailing")]
        public int PullbackTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name="DisableDelaySeconds", Description="Delay between position close and strategy disable. Default 1.5.", Order=17, GroupName="6. Cleanup")]
        public double DisableDelaySeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableSoundOnFill", Description="Play sound when entry fills.", Order=18, GroupName="7. Notifications")]
        public bool EnableSoundOnFill { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath", Description="Folder for audit log CSV. Auto-created if missing. Default C:\\temp.", Order=19, GroupName="8. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableNoEntryWindow",
            Description="[v1.3] Master switch. When TRUE, no new entries inside the N-minute window before session close. Default TRUE.",
            Order=20, GroupName="9. Session Safety")]
        public bool EnableNoEntryWindow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="NoEntryMinutesBeforeClose",
            Description="[v1.3] Minutes before session close to start blocking new entries. Default 10.",
            Order=21, GroupName="9. Session Safety")]
        public int NoEntryMinutesBeforeClose { get; set; }

        #endregion

        // =====================================================================
        // CONDITIONAL VISIBILITY - ShouldSerialize methods
        // =====================================================================

        public bool ShouldSerializeEntryOffsetMultiplier()
        {
            return EntryModeSelection == LongScalper_EntryMode.TrackingLimit;
        }

        public bool ShouldSerializeFixed_PricePrice()
        {
            return EntryModeSelection == LongScalper_EntryMode.Fixed_Price;
        }

        public bool ShouldSerializeTrackingIntervalSeconds()
        {
            return EntryModeSelection == LongScalper_EntryMode.TrackingLimit;
        }

        public bool ShouldSerializeTrackingMinTickChange()
        {
            return EntryModeSelection == LongScalper_EntryMode.TrackingLimit;
        }

        public bool ShouldSerializeStopOffsetMultiplier()
        {
            return StopMode == LongScalper_StopMode.OffsetFromFilledPrice;
        }

        public bool ShouldSerializeTrailOffsetMultiplier()
        {
            return StopMode == LongScalper_StopMode.OffsetFromFilledPrice;
        }

        public bool ShouldSerializeHardStopPoints()
        {
            return StopMode == LongScalper_StopMode.FixedPoints;
        }

        public bool ShouldSerializeTrailDistancePoints()
        {
            return StopMode == LongScalper_StopMode.FixedPoints;
        }

        public bool ShouldSerializeNoEntryMinutesBeforeClose()
        {
            return EnableNoEntryWindow;
        }
    }
}
