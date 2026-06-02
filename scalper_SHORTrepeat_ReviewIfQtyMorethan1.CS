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

// scalper_SHORTrepeat_ReviewIfQtyMorethan1  (managed rewrite)
//
// PATCH 1 — ReadyForNewEntry() broker-side working order check
// PATCH 2 — Trading hours rework (EnableTradingHours toggle + NY integers)
//
// PATCH 3 — Cancel unfilled limit entry on shutdown
// -------------------------------------------------
//   Identical fix to scalper_LONGrepeat PATCH 3, SHORT direction.
//
//   Problem: ProcessShutdown only called ExitShort — which exits an open
//   position. It did NOT cancel a working unfilled limit entry order.
//   The unfilled order would remain live at the broker indefinitely,
//   and entryInFlight=true blocked FinalizeTermination forever.
//
//   Fix — three small changes:
//
//   1. Added private Order workingEntryOrder field.
//      Stores the Order reference returned by EnterShortLimit so we
//      can call CancelOrder on it during shutdown.
//
//   2. ProcessShutdown now cancels workingEntryOrder if entryInFlight.
//      Checks OrderState is Working/Accepted/Submitted before cancelling.
//      Waits for OnOrderUpdate Cancelled callback to clear entryInFlight.
//
//   3. OnOrderUpdate now handles Cancelled/Rejected state for ENTRY_SIGNAL.
//      When cancelled with zero fills: clears all flags, decrements
//      ordersPlaced, nulls workingEntryOrder.
//      When cancelled with partial fills: clears entryInFlight only —
//      position is still open and managed by bracket.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_SHORTrepeat_ReviewIfQtyMorethan1 : Strategy
    {
        // ---- strategy lifecycle ----
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private DateTime lastCheckTime = DateTime.MinValue;
        private int      ordersPlaced  = 0;

        // append-only outcome string (study + consecutive-loss safety)
        private StringBuilder outcomeHistory = new StringBuilder();

        // sequencing flags
        private bool entryInFlight = false;
        private bool awaitingClose = false;

        // [PATCH 3] Store entry order reference for cancel-on-shutdown
        private Order workingEntryOrder = null;

        // captured at entry fill for PnL calculation
        private double entryFillPrice = 0.0;
        private int    entryFillQty   = 0;

        // shutdown control
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "SR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Simple fast one-at-a-time SHORT scalper — MANAGED mode. "
                             + "NT attaches OCO bracket (SetStopLoss + SetProfitTarget) on fill. "
                             + "SHORT: stop ABOVE entry, target BELOW entry. "
                             + "If bracket resize is rejected by exchange on a partial fill, "
                             + "NT RealtimeErrorHandling=StopCancelClose flattens the position. "
                             + "Forced flat beats an unprotected short position.";
                Name         = "scalper_SHORTrepeat_ReviewIfQtyMorethan1";

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
                EnableTradingHours   = false;
                TradingStartHour     = 9;
                TradingStartMinute   = 30;
                TradingEndHour       = 16;
                TradingEndMinute     = 0;
                StrategyLifeMinutes  = 3;
                CheckIntervalSeconds = 1;
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 5;
                ProfitTargetPoints   = 20;
                BaseQuantity         = 1;
                MaxOrders            = 10;
                MaxConsecutiveLosses = 3;
                LogFilePath          = @"C:\temp\scalper_SHORTrepeat_log.csv";
            }
            else if (State == State.Configure)
            {
                SetStopLoss   (ENTRY_SIGNAL, CalculationMode.Ticks,
                    (int)Math.Round(StopLossPoints    / TickSize), false);
                SetProfitTarget(ENTRY_SIGNAL, CalculationMode.Ticks,
                    (int)Math.Round(ProfitTargetPoints / TickSize));
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    strategyStartUtc = DateTime.UtcNow;
                    lifeStarted      = true;
                    EnsureLogHeader();
                    DiagLog(Name + " enabled (MANAGED SHORT). Life=" + StrategyLifeMinutes
                        + "min, MaxOrders=" + MaxOrders
                        + ", MaxConsecLosses=" + MaxConsecutiveLosses
                        + ", Qty=" + BaseQuantity
                        + ", Stop=" + StopLossPoints + "pt (above entry)"
                        + ", Target=" + ProfitTargetPoints + "pt (below entry)"
                        + ", StopTargetHandling=PerEntryExecution"
                        + ", EnableTradingHours=" + EnableTradingHours
                        + (EnableTradingHours
                            ? string.Format(", Window={0:D2}:{1:D2}-{2:D2}:{3:D2} NY",
                                TradingStartHour, TradingStartMinute,
                                TradingEndHour,   TradingEndMinute)
                            : " (chart session governs)"));
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

            DateTime nowUtc = DateTime.UtcNow;

            if ((nowUtc - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached");
                return;
            }

            if (ordersPlaced >= MaxOrders)
            {
                if (awaitingClose || Position.MarketPosition != MarketPosition.Flat)
                    return;
                BeginShutdown("max orders (" + MaxOrders + ") reached");
                return;
            }

            if (HasReachedConsecutiveLossLimit())
            {
                if (awaitingClose || Position.MarketPosition != MarketPosition.Flat)
                    return;
                BeginShutdown("max consecutive losses (" + MaxConsecutiveLosses
                    + ") reached, tail=" + TailOfHistory(MaxConsecutiveLosses));
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
        // PlaceEntry
        // [PATCH 3] Stores workingEntryOrder reference for cancel-on-shutdown.
        // =====================================================================
        private void PlaceEntry()
        {
            int qty = BaseQuantity;
            ordersPlaced++;
            entryInFlight     = true;
            awaitingClose     = true;
            workingEntryOrder = null;   // [PATCH 3] clear before submitting

            try
            {
                if (UseMarketEntry)
                {
                    workingEntryOrder = EnterShort(qty, ENTRY_SIGNAL);  // [PATCH 3]
                    DiagLog(string.Format("ENTRY #{0} MARKET SHORT qty={1}",
                        ordersPlaced, qty));
                }
                else
                {
                    // SHORT limit: sell ABOVE current ask
                    double limitPx = GetCurrentAsk() + LimitOffsetPoints;
                    limitPx = Instrument.MasterInstrument.RoundToTickSize(limitPx);
                    workingEntryOrder = EnterShortLimit(0, true, qty, limitPx, ENTRY_SIGNAL);  // [PATCH 3]
                    DiagLog(string.Format("ENTRY #{0} LIMIT SHORT qty={1} limit={2:F2}",
                        ordersPlaced, qty, limitPx));
                }
            }
            catch (Exception ex)
            {
                DiagLog("PlaceEntry error: " + ex.Message);
                ordersPlaced--;
                entryInFlight     = false;
                awaitingClose     = false;
                workingEntryOrder = null;
            }
        }

        // =====================================================================
        // ReadyForNewEntry — PATCH 1 broker-side scan
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

            // ---- entry fill (full or partial) ----
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
                    workingEntryOrder = null;  // [PATCH 3] fully filled, no longer needs cancel
                    DiagLog(string.Format("Entry complete. Fill={0:F2} qty={1}. Bracket active.",
                        entryFillPrice, entryFillQty));
                }
                else
                {
                    DiagLog(string.Format("Partial fill {0} of {1}. NT attaches bracket for this lot.",
                        entryFillQty, BaseQuantity));
                }
                return;
            }

            // ---- bracket exit fill ----
            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill) && isFull)
            {
                DiagLog(string.Format("{0} fill: qty={1} @ {2:F2} name={3}",
                    isStopFill ? "STOP" : "TARGET", quantity, price, oName));

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    // SHORT PnL: profit when exit BELOW entry
                    double pnl = isStopFill
                        ? -(StopLossPoints    * entryFillQty * Instrument.MasterInstrument.PointValue)
                        : +(ProfitTargetPoints * entryFillQty * Instrument.MasterInstrument.PointValue);

                    int bit = isStopFill ? 0 : 1;
                    outcomeHistory.Append(bit.ToString());

                    WriteLogRow(price, pnl, bit, entryFillQty, time);
                    DiagLog(string.Format("CLOSED {0}: entry={1:F2} exit={2:F2} qty={3} pnl={4:0.00} bit={5} hist(last10)={6}",
                        isStopFill ? "STOP" : "TARGET",
                        entryFillPrice, price, entryFillQty, pnl, bit, TailOfHistory(10)));

                    awaitingClose     = false;
                    entryInFlight     = false;
                    workingEntryOrder = null;
                    entryFillPrice    = 0.0;
                    entryFillQty      = 0;
                }
            }
        }

        // =====================================================================
        // OnOrderUpdate
        // [PATCH 3] Handles Cancelled/Rejected state for ENTRY_SIGNAL.
        // Clears entryInFlight so ProcessShutdown can reach FinalizeTermination.
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

            string oName = order.Name ?? "";

            // [PATCH 3] Entry order cancelled — reset state so shutdown can complete
            if (oName == ENTRY_SIGNAL
                && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                DiagLog(string.Format("Entry order {0} (filled={1}). Resetting entry flags.",
                    orderState, filled));

                if (filled == 0)
                {
                    // No fill at all — full reset, don't count as a placed order
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
                    // Partial fills already occurred — position may be open
                    // Clear entryInFlight only so shutdown can proceed
                    entryInFlight     = false;
                    workingEntryOrder = null;
                    DiagLog(string.Format("Entry cancelled with {0} partial fill(s). Position still managed.", filled));
                }
                return;
            }

            // Diagnostic logging for all other order events
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
        // WithinTradingHours — PATCH 2
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
        // Consecutive loss helpers
        // =====================================================================
        private string TailOfHistory(int n)
        {
            string s = outcomeHistory.ToString();
            return s.Length <= n ? s : s.Substring(s.Length - n);
        }

        private bool HasReachedConsecutiveLossLimit()
        {
            if (MaxConsecutiveLosses <= 0) return false;
            string s = outcomeHistory.ToString();
            if (s.Length < MaxConsecutiveLosses) return false;
            for (int i = s.Length - MaxConsecutiveLosses; i < s.Length; i++)
                if (s[i] != '0') return false;
            return true;
        }

        // =====================================================================
        // Shutdown
        // [PATCH 3] ProcessShutdown now cancels unfilled entry order if present.
        // =====================================================================
        private void BeginShutdown(string reason)
        {
            if (disabledSelf || pendingFlatten) return;
            pendingReason  = reason;
            pendingFlatten = true;
            DiagLog(Name + " shutdown requested: " + reason + " | orders=" + ordersPlaced);
        }

        private void ProcessShutdown()
        {
            // [PATCH 3] Cancel unfilled entry order if working
            // After CancelOrder(), wait for OnOrderUpdate Cancelled callback
            // to clear entryInFlight — do NOT force-clear here synchronously.
            if (entryInFlight && workingEntryOrder != null)
            {
                OrderState os = workingEntryOrder.OrderState;
                if (os == OrderState.Working
                    || os == OrderState.Accepted
                    || os == OrderState.Submitted)
                {
                    try
                    {
                        DiagLog(string.Format("Shutdown: cancelling unfilled SHORT entry order (state={0}).", os));
                        CancelOrder(workingEntryOrder);
                    }
                    catch (Exception ex)
                    {
                        DiagLog("Shutdown: CancelOrder error: " + ex.Message);
                        // Force-clear as fallback if cancel throws
                        entryInFlight     = false;
                        awaitingClose     = false;
                        workingEntryOrder = null;
                    }
                }
                // Wait for Cancelled callback in OnOrderUpdate to clear entryInFlight
                return;
            }

            // If already flat and no entry in flight — terminate cleanly
            if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight)
            {
                FinalizeTermination();
                return;
            }

            // If holding a short position — submit ExitShort at market
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
            DiagLog(Name + " terminated cleanly. Reason: " + pendingReason
                + " | orders=" + ordersPlaced
                + " | history=" + outcomeHistory.ToString());
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
                if (!File.Exists(LogFilePath))
                    File.AppendAllText(LogFilePath,
                        "timestamp,order_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,running_history\n");
            }
            catch (Exception ex) { Print("Log header error: " + ex.Message); }
        }

        private void WriteLogRow(double exitPrice, double pnl, int bit, int qty, DateTime exitTime)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8}\n",
                    exitTime, ordersPlaced, "Short", qty,
                    entryFillPrice, exitPrice, pnl, bit, outcomeHistory.ToString());
                File.AppendAllText(LogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error: " + ex.Message); }
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
            Description = "When false: no time filter, trades within NT session template. "
                        + "When true: only new entries within NY window below (same-day only).")]
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
        [Display(Name = "End minute (NY)", Order = 5, GroupName = "Hours",
            Description = "Outside this window: no new entries. Open trades finish naturally.")]
        public int TradingEndMinute { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "C: Strategy life (minutes)", Order = 6, GroupName = "Timing")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "D: Check interval (seconds)", Order = 7, GroupName = "Timing")]
        public int CheckIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "E: Use market entry (else limit)", Order = 8, GroupName = "Entry")]
        public bool UseMarketEntry { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Limit offset (points)", Order = 9, GroupName = "Entry")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "F: Stop loss (points)", Order = 10, GroupName = "Bracket")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "G/H: Profit target (points)", Order = 11, GroupName = "Bracket")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 12, GroupName = "Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max orders (then disable)", Order = 13, GroupName = "Limits")]
        public int MaxOrders { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max consecutive losses (then disable)", Order = 14, GroupName = "Limits")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log file path", Order = 15, GroupName = "Logging")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
