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

// scalper_LONGrepeat_ReviewIfQtyMorethan1  (managed rewrite)
//
// CHANGES vs unmanaged version:
//   * IsUnmanaged = false  -> full managed mode.
//   * Entry via EnterLong / EnterLongLimit (signal name "LR_Entry").
//   * Bracket via SetStopLoss + SetProfitTarget in State.Configure.
//     NT attaches the bracket immediately on fill, resizes automatically
//     if partial fills arrive, and OCO-cancels the surviving leg on exit.
//   * If NT cannot resize the bracket (e.g. exchange rejects a stop
//     modification on a late partial fill), NT's RealtimeErrorHandling
//     = StopCancelClose will flatten the position.  That is an acceptable
//     outcome — a forced flat beats an unprotected position.
//   * Shutdown flatten via ExitLong (managed); NT cancels working bracket
//     legs automatically when the position closes.
//   * Manual OCO, stopOrder, targetOrder, flattenOrder, ocoId, and
//     CancelAllWorkingOrders all removed — NT owns that now.
//   * entryInFlight / awaitingClose flags retained but simplified:
//       entryInFlight: true from EnterLong call until first execution fill.
//       awaitingClose: true from first fill until position returns Flat.
//   * Win/loss detection: OnExecutionUpdate watches for exit fills and
//     classifies by exit order name ("Stop loss" vs "Profit target").
//   * All parameters, CSV logging, DiagLog, and shutdown logic preserved.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_LONGrepeat_ReviewIfQtyMorethan1 : Strategy
    {
        // ---- strategy lifecycle ----
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private DateTime lastCheckTime = DateTime.MinValue;
        private int      ordersPlaced  = 0;

        // append-only outcome string (study only — does not drive orders)
        private StringBuilder outcomeHistory = new StringBuilder();

        // sequencing flags
        private bool entryInFlight = false;  // EnterLong submitted, no fill yet
        private bool awaitingClose = false;  // position open, waiting for bracket exit

        // captured at entry fill for PnL calculation
        private double entryFillPrice = 0.0;
        private int    entryFillQty   = 0;

        // shutdown control
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        // signal name — used consistently for entry + bracket + exit calls
        private const string ENTRY_SIGNAL = "LR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Simple fast one-at-a-time LONG scalper — MANAGED mode. "
                             + "NT attaches OCO bracket (SetStopLoss + SetProfitTarget) on fill. "
                             + "If bracket resize is rejected by exchange on a partial fill, "
                             + "NT RealtimeErrorHandling=StopCancelClose flattens the position. "
                             + "Forced flat is acceptable vs an unprotected position.";
                Name         = "scalper_LONGrepeat_ReviewIfQtyMorethan1";

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

                // [MANAGED] StopTargetHandling.PerEntryExecution means NT
                // submits a separate bracket per execution fill.  For qty=1
                // this is identical to PerStrategy.  For larger qty with
                // partial fills, each filled lot gets its own bracket legs —
                // avoiding the single-bracket resize problem entirely.
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;

                BarsRequiredToTrade          = 0;
                IsUnmanaged                  = false;   // [MANAGED]

                // ---- defaults (unchanged from unmanaged version) ----
                StartTimeProp        = DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
                EndTimeProp          = DateTime.Parse("16:00", System.Globalization.CultureInfo.InvariantCulture);
                StrategyLifeMinutes  = 3;
                CheckIntervalSeconds = 1;
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 5;
                ProfitTargetPoints   = 20;
                BaseQuantity         = 1;
                MaxOrders            = 10;
                MaxConsecutiveLosses = 3;
                LogFilePath          = @"C:\temp\scalper_LONGrepeat_log.csv";
            }
            else if (State == State.Configure)
            {
                // [MANAGED] Set bracket parameters once here.
                // NT applies these to every EnterLong fill automatically.
                // With StopTargetHandling.PerEntryExecution, each partial fill
                // execution gets its own independent stop + target pair,
                // so NT never needs to resize a single bracket across fills.
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
                    DiagLog(Name + " enabled (MANAGED). Life=" + StrategyLifeMinutes
                        + "min, MaxOrders=" + MaxOrders
                        + ", MaxConsecLosses=" + MaxConsecutiveLosses
                        + ", Qty=" + BaseQuantity
                        + ", Stop=" + StopLossPoints + "pt"
                        + ", Target=" + ProfitTargetPoints + "pt"
                        + ", StopTargetHandling=PerEntryExecution");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;
            if (!lifeStarted) return;

            // Drive shutdown to completion first; do nothing else while shutting down.
            if (disabledSelf || pendingFlatten)
            {
                ProcessShutdown();
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;

            // ---- shutdown: strategy life expired ----
            if ((nowUtc - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached");
                return;
            }

            // ---- max orders reached ----
            // Stop placing NEW entries. Let any open trade finish via its bracket.
            if (ordersPlaced >= MaxOrders)
            {
                if (awaitingClose || Position.MarketPosition != MarketPosition.Flat)
                    return;
                BeginShutdown("max orders (" + MaxOrders + ") reached");
                return;
            }

            // ---- max consecutive losses reached ----
            if (HasReachedConsecutiveLossLimit())
            {
                if (awaitingClose || Position.MarketPosition != MarketPosition.Flat)
                    return;
                BeginShutdown("max consecutive losses (" + MaxConsecutiveLosses
                    + ") reached, tail=" + TailOfHistory(MaxConsecutiveLosses));
                return;
            }

            // ---- throttle ----
            if ((DateTime.Now - lastCheckTime).TotalSeconds < CheckIntervalSeconds)
                return;
            lastCheckTime = DateTime.Now;

            // ---- trading hours gate ----
            if (!WithinTradingHours())
                return;

            // ---- entry gate: flat, no working orders, not already in a trade ----
            if (!ReadyForNewEntry())
                return;

            PlaceEntry();
        }

        // =====================================================================
        // PlaceEntry — managed EnterLong / EnterLongLimit
        // =====================================================================
        private void PlaceEntry()
        {
            int qty = BaseQuantity;
            ordersPlaced++;
            entryInFlight = true;
            awaitingClose = true;

            try
            {
                if (UseMarketEntry)
                {
                    EnterLong(qty, ENTRY_SIGNAL);
                    DiagLog(string.Format("ENTRY #{0} MARKET qty={1}", ordersPlaced, qty));
                }
                else
                {
                    double limitPx = GetCurrentBid() - LimitOffsetPoints;
                    limitPx = Instrument.MasterInstrument.RoundToTickSize(limitPx);
                    EnterLongLimit(0, true, qty, limitPx, ENTRY_SIGNAL);
                    DiagLog(string.Format("ENTRY #{0} LIMIT qty={1} limit={2:F2}",
                        ordersPlaced, qty, limitPx));
                }
            }
            catch (Exception ex)
            {
                DiagLog("PlaceEntry error: " + ex.Message);
                ordersPlaced--;
                entryInFlight = false;
                awaitingClose = false;
            }
        }

        // =====================================================================
        // ReadyForNewEntry
        // In managed mode NT tracks working orders internally.  We guard on:
        //   - not already waiting for an entry fill (entryInFlight)
        //   - not already in a trade (awaitingClose)
        //   - broker position is Flat
        // =====================================================================
        private bool ReadyForNewEntry()
        {
            if (entryInFlight) return false;
            if (awaitingClose) return false;
            if (Position.MarketPosition != MarketPosition.Flat) return false;
            return true;
        }

        // =====================================================================
        // OnExecutionUpdate
        // [MANAGED] NT submits and manages the bracket automatically.
        // We only need to:
        //   1. Detect entry fill -> record fill price, clear entryInFlight.
        //   2. Detect bracket exit fill -> record outcome, clear awaitingClose.
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
                // Record the first fill price as our reference for PnL.
                // With PerEntryExecution each lot gets its own bracket from
                // its own fill price — so each lot's PnL is self-contained.
                // We capture the first fill here for the overall trade log row.
                if (entryFillPrice == 0.0)
                    entryFillPrice = price;

                entryFillQty += quantity;

                DiagLog(string.Format("ENTRY {0} fill: qty={1} @ {2:F2} totalFilled={3}/{4}",
                    isFull ? "FULL" : "PARTIAL",
                    quantity, price, entryFillQty, BaseQuantity));

                if (isFull)
                {
                    // Full fill — entry complete, bracket attached by NT.
                    entryInFlight = false;
                    DiagLog(string.Format("Entry complete. Fill={0:F2} qty={1}. Bracket active (PerEntryExecution).",
                        entryFillPrice, entryFillQty));
                }
                else
                {
                    // Partial fill — NT will attach a bracket for this lot
                    // via PerEntryExecution.  Entry order remains working for
                    // the remainder.  entryInFlight stays true until full fill.
                    DiagLog(string.Format("Partial fill {0} of {1}. NT attaches bracket for this lot. Remainder still working.",
                        entryFillQty, BaseQuantity));
                }
                return;
            }

            // ---- entry order cancelled with no fills (e.g. limit expired) ----
            if (oName == ENTRY_SIGNAL
                && execution.Order.OrderState == OrderState.Cancelled
                && entryFillQty == 0)
            {
                DiagLog(string.Format("Entry #{0} cancelled with no fills. Resetting.", ordersPlaced));
                ordersPlaced--;   // don't count a cancelled-with-no-fill as a placed order
                entryInFlight = false;
                awaitingClose = false;
                entryFillPrice = 0.0;
                entryFillQty   = 0;
                return;
            }

            // ---- bracket exit fill: stop loss or profit target ----
            // NT names these "Stop loss" and "Profit target" (managed bracket).
            bool isStopFill   = oName.IndexOf("Stop",   StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill) && isFull)
            {
                // With PerEntryExecution each lot has its own bracket.
                // RecordOutcome is called once per exit fill.
                // For qty=1 this fires once; for qty=N it fires N times.
                // We only log the overall trade summary once the full
                // position returns to Flat.
                DiagLog(string.Format("{0} fill: qty={1} @ {2:F2} name={3}",
                    isStopFill ? "STOP" : "TARGET", quantity, price, oName));

                // Position went Flat: record overall outcome and reset.
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    double pnl = isStopFill
                        ? -(StopLossPoints    * entryFillQty * Instrument.MasterInstrument.PointValue)
                        : +(ProfitTargetPoints * entryFillQty * Instrument.MasterInstrument.PointValue);

                    int bit = isStopFill ? 0 : 1;
                    outcomeHistory.Append(bit.ToString());

                    WriteLogRow(price, pnl, bit, entryFillQty, time);
                    DiagLog(string.Format("CLOSED {0}: entry={1:F2} exit={2:F2} qty={3} pnl={4:0.00} bit={5} hist(last10)={6}",
                        isStopFill ? "STOP" : "TARGET",
                        entryFillPrice, price, entryFillQty, pnl, bit, TailOfHistory(10)));

                    // Reset trade state for next entry.
                    awaitingClose  = false;
                    entryInFlight  = false;
                    entryFillPrice = 0.0;
                    entryFillQty   = 0;
                }
            }
        }

        // =====================================================================
        // OnOrderUpdate — diagnostic logging only in managed mode.
        // NT handles all state transitions internally.
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

            // Log errors and rejections prominently.
            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
            {
                DiagLog(string.Format("ORDER WARN: {0} state={1} err={2} native={3}",
                    order.Name, orderState, error,
                    string.IsNullOrEmpty(nativeError) ? "-" : nativeError));
            }
            else
            {
                DiagLog(string.Format("ORDER {0} state={1} qty={2} filled={3} avg={4:F2}",
                    order.Name, orderState, quantity, filled, averageFillPrice));
            }
        }

        // =====================================================================
        // WithinTradingHours
        // =====================================================================
        private bool WithinTradingHours()
        {
            TimeZoneInfo et;
            try   { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { return true; } }

            DateTime nyNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et);
            TimeSpan t     = nyNow.TimeOfDay;
            return t >= StartTimeProp.TimeOfDay && t <= EndTimeProp.TimeOfDay;
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
        // [MANAGED] ExitLong closes the position at market.
        // NT cancels the working bracket legs automatically when position closes.
        // =====================================================================
        private void BeginShutdown(string reason)
        {
            if (disabledSelf || pendingFlatten) return;
            pendingReason  = reason;
            pendingFlatten = true;
            DiagLog(Name + " shutdown requested: " + reason
                + " | orders=" + ordersPlaced);
        }

        private void ProcessShutdown()
        {
            // If already flat and no entry in flight — terminate cleanly.
            if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight)
            {
                FinalizeTermination();
                return;
            }

            // If holding a position, submit ExitLong at market (once per tick
            // until NT confirms flat — NT is idempotent on duplicate exit calls
            // for the same signal in managed mode).
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

            // If an entry limit is still working (no fill yet) — NT will cancel
            // it on the next bar via the managed approach's auto-cancel behavior
            // or we can explicitly call ExitLong with qty=0 to cancel all pending.
            // Since managed mode auto-cancels unfilled entry orders at bar end
            // when we stop calling EnterLong, we just wait for flat here.
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
                    exitTime, ordersPlaced, "Long", qty,
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
        // Properties — identical to unmanaged version
        // =====================================================================
        #region Properties

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "A: Start time (NY)", Order = 1, GroupName = "Hours")]
        public DateTime StartTimeProp { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "B: End time (NY)", Order = 2, GroupName = "Hours")]
        public DateTime EndTimeProp { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "C: Strategy life (minutes)", Order = 3, GroupName = "Timing")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "D: Check interval (seconds)", Order = 4, GroupName = "Timing")]
        public int CheckIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "E: Use market entry (else limit)", Order = 5, GroupName = "Entry")]
        public bool UseMarketEntry { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Limit offset (points)", Order = 6, GroupName = "Entry")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "F: Stop loss (points)", Order = 7, GroupName = "Bracket")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "G/H: Profit target (points)", Order = 8, GroupName = "Bracket")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 9, GroupName = "Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max orders (then disable)", Order = 10, GroupName = "Limits")]
        public int MaxOrders { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max consecutive losses (then disable)", Order = 11, GroupName = "Limits")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log file path", Order = 12, GroupName = "Logging")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
