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

// =============================================================================
// scalper_LONGrepeat_BacktestOnly
// =============================================================================
// Backtest-compatible version of scalper_LONGrepeat_ReviewIfQtyMorethan1.
// DO NOT USE FOR LIVE TRADING — use scalper_LONGrepeat_ReviewIfQtyMorethan1
// for live. This file exists purely for Strategy Analyzer backtesting.
// After testing, delete or archive this file.
//
// KEY DIFFERENCES FROM LIVE VERSION:
//
//   1. Runs in State.Historical (backtest) AND State.Realtime.
//      Live version blocks all processing except State.Realtime.
//
//   2. Strategy life timer uses bar time (Time[0]) not wall clock.
//      Live: DateTime.UtcNow - strategyStartUtc >= StrategyLifeMinutes
//      Here: (Time[0] - strategyStartBarTime).TotalMinutes >= StrategyLifeMinutes
//      Setting StrategyLifeMinutes=10000 means ~7 days of bar time —
//      effectively never expires on a single-day backtest. MaxOrders
//      or session end will always trigger first.
//
//   3. CheckIntervalSeconds uses bar time not wall clock.
//      Live: DateTime.Now - lastCheckTime >= CheckIntervalSeconds
//      Here: (Time[0] - lastCheckBarTime).TotalSeconds >= CheckIntervalSeconds
//      On 1-min bars with CheckIntervalSeconds=1, this fires every bar.
//
//   4. Account.Orders broker scan SKIPPED in Historical mode.
//      The scan is only valid in live trading. In backtest it would
//      either throw or return wrong results.
//
//   5. GetCurrentBid() / GetCurrentAsk() replaced with Close[0] in Historical.
//      Limit offset uses Close[0] as the reference price in backtest.
//
//   6. DiagLog skips disk writes in Historical — print only.
//      Avoids huge log files during backtesting thousands of bars.
//
//   7. EnsureLogHeader and WriteLogRow skip in Historical.
//      No CSV written during backtest. Results come from NT Strategy Analyzer.
//
//   8. lifeStarted initialized in State.DataLoaded (not State.Realtime).
//      Allows OnBarUpdate to process historical bars immediately.
//
// EVERYTHING ELSE IS IDENTICAL TO THE LIVE VERSION:
//   Same entry logic, same bracket (SetStopLoss + SetProfitTarget),
//   same PerEntryExecution, same StopCancelClose, same MaxOrders,
//   same MaxConsecutiveLosses, same trading hours filter (uses Time[0]),
//   same outcome history string, same parameters and defaults.
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_LONGrepeat_BacktestOnly : Strategy
    {
        // ---- strategy lifecycle ----
        private DateTime strategyStartBarTime;   // [BACKTEST] bar time, not wall clock
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;

        private DateTime lastCheckBarTime = DateTime.MinValue;  // [BACKTEST] bar time
        private int      ordersPlaced  = 0;

        // append-only outcome string (study only)
        private StringBuilder outcomeHistory = new StringBuilder();

        // sequencing flags
        private bool entryInFlight = false;
        private bool awaitingClose = false;

        // captured at entry fill for PnL calculation
        private double entryFillPrice = 0.0;
        private int    entryFillQty   = 0;

        // shutdown control
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

        private const string ENTRY_SIGNAL = "LR_Entry";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "BACKTEST ONLY — do not use for live trading. "
                             + "Backtest-compatible version of scalper_LONGrepeat_ReviewIfQtyMorethan1. "
                             + "Uses bar time for all timers. Skips broker-side checks and disk logging. "
                             + "Delete or archive after backtesting.";
                Name         = "scalper_LONGrepeat_BacktestOnly";

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
                StrategyLifeMinutes  = 10000;  // effectively never for single-day backtest
                CheckIntervalSeconds = 1;
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 5;
                ProfitTargetPoints   = 10;
                BaseQuantity         = 1;
                MaxOrders            = 10;
                MaxConsecutiveLosses = 30;
                LogFilePath          = @"C:\temp\scalper_LONGrepeat_log.csv";
            }
            else if (State == State.Configure)
            {
                SetStopLoss   (ENTRY_SIGNAL, CalculationMode.Ticks,
                    (int)Math.Round(StopLossPoints    / TickSize), false);
                SetProfitTarget(ENTRY_SIGNAL, CalculationMode.Ticks,
                    (int)Math.Round(ProfitTargetPoints / TickSize));
            }
            else if (State == State.DataLoaded)
            {
                // [BACKTEST FIX] Initialize here so Historical bars are processed.
                // Live version does this in State.Realtime which blocks backtest.
                // strategyStartBarTime set on first bar in OnBarUpdate.
                lifeStarted = true;
            }
            else if (State == State.Realtime)
            {
                // In realtime, reset start time to now so life timer
                // continues correctly if strategy runs into live session.
                if (strategyStartBarTime == DateTime.MinValue)
                    strategyStartBarTime = Time[0];

                DiagLog(Name + " now REALTIME. Life=" + StrategyLifeMinutes
                    + "min, MaxOrders=" + MaxOrders
                    + ", MaxConsecLosses=" + MaxConsecutiveLosses
                    + ", Qty=" + BaseQuantity
                    + ", Stop=" + StopLossPoints + "pt"
                    + ", Target=" + ProfitTargetPoints + "pt"
                    + ", EnableTradingHours=" + EnableTradingHours);
            }
        }

        protected override void OnBarUpdate()
        {
            if (!lifeStarted) return;

            // Accept both Historical and Realtime
            if (State != State.Historical && State != State.Realtime) return;

            // [BACKTEST FIX] Set strategy start time on very first bar
            if (strategyStartBarTime == DateTime.MinValue)
                strategyStartBarTime = Time[0];

            if (disabledSelf || pendingFlatten)
            {
                ProcessShutdown();
                return;
            }

            // [BACKTEST FIX] Use bar time for life timer
            if ((Time[0] - strategyStartBarTime).TotalMinutes >= StrategyLifeMinutes)
            {
                BeginShutdown("strategy life of " + StrategyLifeMinutes + " min (bar time) reached");
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

            // [BACKTEST FIX] Use bar time for check interval throttle
            if ((Time[0] - lastCheckBarTime).TotalSeconds < CheckIntervalSeconds)
                return;
            lastCheckBarTime = Time[0];

            if (!WithinTradingHours())
                return;

            if (!ReadyForNewEntry())
                return;

            PlaceEntry();
        }

        // =====================================================================
        // PlaceEntry
        // [BACKTEST FIX] Uses Close[0] as reference price in Historical mode.
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
                    DiagLog(string.Format("ENTRY #{0} MARKET qty={1} @ ~{2:F2}",
                        ordersPlaced, qty, Close[0]));
                }
                else
                {
                    // [BACKTEST FIX] Use Close[0] instead of GetCurrentBid()
                    // GetCurrentBid() is only valid in realtime.
                    double refPrice = (State == State.Realtime)
                        ? GetCurrentBid()
                        : Close[0];
                    double limitPx = Instrument.MasterInstrument.RoundToTickSize(
                        refPrice - LimitOffsetPoints);
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
        // [BACKTEST FIX] Account.Orders broker scan only runs in Realtime.
        // In Historical it is invalid and would return wrong results.
        // =====================================================================
        private bool ReadyForNewEntry()
        {
            if (entryInFlight) return false;
            if (awaitingClose) return false;
            if (Position.MarketPosition != MarketPosition.Flat) return false;

            // Broker-side scan — realtime only
            if (State == State.Realtime)
            {
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
                                        "ReadyForNewEntry: BLOCKED by broker order name='{0}' state={1}.",
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
            }

            return true;
        }

        // =====================================================================
        // OnExecutionUpdate — identical to live version
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
                    entryInFlight = false;
                    DiagLog(string.Format("Entry complete. Fill={0:F2} qty={1}.",
                        entryFillPrice, entryFillQty));
                }
                else
                {
                    DiagLog(string.Format("Partial fill {0} of {1}. Bracket attached for this lot.",
                        entryFillQty, BaseQuantity));
                }
                return;
            }

            // ---- entry cancelled with no fills ----
            if (oName == ENTRY_SIGNAL
                && execution.Order.OrderState == OrderState.Cancelled
                && entryFillQty == 0)
            {
                DiagLog(string.Format("Entry #{0} cancelled with no fills. Resetting.", ordersPlaced));
                ordersPlaced--;
                entryInFlight  = false;
                awaitingClose  = false;
                entryFillPrice = 0.0;
                entryFillQty   = 0;
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
                    double pnl = isStopFill
                        ? -(StopLossPoints    * entryFillQty * Instrument.MasterInstrument.PointValue)
                        : +(ProfitTargetPoints * entryFillQty * Instrument.MasterInstrument.PointValue);

                    int bit = isStopFill ? 0 : 1;
                    outcomeHistory.Append(bit.ToString());

                    // [BACKTEST FIX] Only write CSV in Realtime
                    if (State == State.Realtime)
                        WriteLogRow(price, pnl, bit, entryFillQty, time);

                    DiagLog(string.Format("CLOSED {0}: entry={1:F2} exit={2:F2} qty={3} pnl={4:0.00} bit={5} hist(last10)={6}",
                        isStopFill ? "STOP" : "TARGET",
                        entryFillPrice, price, entryFillQty, pnl, bit, TailOfHistory(10)));

                    awaitingClose  = false;
                    entryInFlight  = false;
                    entryFillPrice = 0.0;
                    entryFillQty   = 0;
                }
            }
        }

        // =====================================================================
        // OnOrderUpdate — diagnostic only
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
            {
                DiagLog(string.Format("ORDER WARN: {0} state={1} err={2} native={3}",
                    order.Name, orderState, error,
                    string.IsNullOrEmpty(nativeError) ? "-" : nativeError));
            }
        }

        // =====================================================================
        // WithinTradingHours
        // [BACKTEST FIX] Uses Time[0] (bar time) for NY conversion — correct
        // in both Historical and Realtime.
        // =====================================================================
        private bool WithinTradingHours()
        {
            if (!EnableTradingHours) return true;

            TimeZoneInfo et;
            try   { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { return true; } }

            // [BACKTEST FIX] Convert bar time, not wall clock
            DateTime nyBar  = TimeZoneInfo.ConvertTime(Time[0], et);
            int      curMin  = nyBar.Hour * 60 + nyBar.Minute;
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
        // Shutdown — identical to live version
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
                + " | orders=" + ordersPlaced
                + " | history=" + outcomeHistory.ToString());
            try { SetState(State.Terminated); } catch { }
        }

        // =====================================================================
        // Logging
        // [BACKTEST FIX] DiagLog: print always, disk write only in Realtime.
        // EnsureLogHeader / WriteLogRow: only in Realtime.
        // =====================================================================
        private void EnsureLogHeader()
        {
            if (State != State.Realtime) return;
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
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            Print(line);

            // [BACKTEST FIX] Skip disk write in Historical — print only
            if (State != State.Realtime) return;

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
        // Properties — same as live version
        // =====================================================================
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading Hours filter", Order = 1, GroupName = "Hours",
            Description = "When false (default): no time filter, strategy trades within NT session template. "
                        + "When true: only place new entries within the NY window below (same-day, no overnight).")]
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
            Description = "Outside this window: no new entries. Open trades finish naturally via bracket.")]
        public int TradingEndMinute { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "C: Strategy life (bar-minutes)", Order = 6, GroupName = "Timing",
            Description = "Minutes of BAR TIME before life timer fires. Set 10000 for single-day backtest "
                        + "(effectively never expires — MaxOrders or session end triggers first).")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "D: Check interval (seconds)", Order = 7, GroupName = "Timing",
            Description = "Minimum bar-time seconds between entry checks. On 1-min bars, setting 1 fires every bar.")]
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
        [Display(Name = "Log file path", Order = 15, GroupName = "Logging",
            Description = "CSV log path. Only written during Realtime, not during backtest.")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
