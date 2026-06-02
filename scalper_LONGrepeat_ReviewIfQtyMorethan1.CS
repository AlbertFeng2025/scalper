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
// PATCH 1 — ReadyForNewEntry() broker-side working order check
// PATCH 2 — Trading hours rework (EnableTradingHours toggle + NY integers)
//
// PATCH 3 — Cancel unfilled limit entry on shutdown
//
// PATCH 4 — Enable trailing stop
//
// PATCH 5 — Fake order mode + pattern-armed real orders
// ------------------------------------------------------
//   Two new parameters:
//
//   EnableRealOrder (bool, default false):
//     true  → strategy runs fake (paper) trades continuously, building
//             outcomeHistory. Real orders fire only when EntryPattern
//             matches the tail of outcomeHistory.
//     false → every trade is real (original behavior).
//
//   EntryPattern (string, default "0,1"):
//     Comma-separated list of binary patterns. After each trade closes
//     (fake or real), the tail of outcomeHistory is checked against
//     every pattern. If ANY pattern matches → next entry is REAL.
//     Default "0,1" means every outcome arms a real order (effectively
//     effectively no filter — same as always real.
//     Example: "01,011" → real order only after loss-then-win or
//     loss-then-win-then-win sequence.
//
//   Key design rules:
//   - Empty / too-short sequence never matches → starts in fake mode (safe).
//   - Fake trades use FIXED bracket only (no trailing stop).
//   - Real trades use full logic including trailing stop if enabled.
//   - Fake and real outcomes go into the SAME outcomeHistory string.
//   - All session gates (MaxOrders, StrategyLifeMinutes, TradingHours,
//     MaxConsecutiveLosses) apply equally to fake and real trades.
//   - MaxOrders counts fake + real combined.
//   - After real order closes, pattern is re-checked immediately —
//     may arm another real order right away if pattern still matches.
//   - isArmed resets to false after each real order is submitted.
//
//   Fake trade tracking (tick by tick):
//   - fakeEntryPrice: bid at moment entry condition fires
//   - fakeStopPrice:  fakeEntryPrice - StopLossPoints
//   - fakeTargetPrice: fakeEntryPrice + ProfitTargetPoints
//   - Each tick: if bid <= fakeStopPrice → fake loss (0)
//                if ask >= fakeTargetPrice → fake win (1)
//   - No actual orders submitted during fake trade.
// --------------------------------
//   Added EnableTrailingStop (bool, default true) and TrailDistancePoints
//   (double, default = StopLossPoints).
//
//   When EnableTrailingStop = false:
//     Fixed stop only. Behavior identical to patch 1-3.
//
//   When EnableTrailingStop = true:
//     On entry fill: initial hard stop set at StopLossPoints below fill.
//     Every CheckIntervalSeconds (same timer as entry check, label updated):
//       proposedStop = GetCurrentBid() - TrailDistancePoints
//       if proposedStop > currentStopPrice → move stop up via SetStopLoss
//       if proposedStop <= currentStopPrice → do nothing (stop never moves down)
//     Trail only moves UP for Long — ratchets with price, never retreats.
//
//   TrailDistancePoints defaults to StopLossPoints value for back-compat.
//   User may set a tighter trail (e.g. stop=20pt, trail=10pt).
//   Uses existing CheckIntervalSeconds timer — no new timer parameter needed.
// -------------------------------------------------
//   Problem: when StrategyLifeMinutes expires (or MaxOrders /
//   MaxConsecutiveLosses triggers BeginShutdown), ProcessShutdown
//   only called ExitLong — which exits an open position. It did NOT
//   cancel a working unfilled limit entry order. The unfilled order
//   would remain live at the broker indefinitely (until session close
//   or manual cancel), and entryInFlight=true would block
//   FinalizeTermination forever, leaving the strategy stuck.
//
//   Fix — three small changes:
//
//   1. Added private Order workingEntryOrder field.
//      Stores the Order reference returned by EnterLongLimit so we
//      can call CancelOrder on it during shutdown.
//      (Market entries also store the reference for consistency,
//      though market orders fill near-instantly and rarely need cancel.)
//
//   2. ProcessShutdown now cancels workingEntryOrder if entryInFlight.
//      Checks OrderState is Working/Accepted/Submitted before cancelling.
//      After cancel request, waits for OnOrderUpdate Cancelled callback
//      to clear entryInFlight — does not force-clear it synchronously.
//
//   3. OnOrderUpdate now handles Cancelled state for ENTRY_SIGNAL.
//      When the entry order is cancelled (by shutdown or any other reason)
//      with zero fills, clears entryInFlight, awaitingClose, resets fill
//      tracking fields, and nulls workingEntryOrder.
//      This allows FinalizeTermination to proceed cleanly once flat.

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

        // append-only outcome string (study only)
        private StringBuilder outcomeHistory = new StringBuilder();

        // sequencing flags
        private bool entryInFlight = false;
        private bool awaitingClose = false;

        // [PATCH 3] Store entry order reference for cancel-on-shutdown
        private Order workingEntryOrder = null;

        // captured at entry fill for PnL calculation
        private double entryFillPrice = 0.0;
        private int    entryFillQty   = 0;

        // ---- Fake order mode state [PATCH 5] ----
        private bool   inFakeTrade      = false;   // currently tracking a fake trade
        private double fakeEntryPrice   = 0.0;
        private double fakeStopPrice    = 0.0;
        private double fakeTargetPrice  = 0.0;
        private bool   isArmed          = false;   // next entry should be real
        private bool   enableRealOrder  = true;    // [PATCH 5] cached from property
        private List<string> compiledPatterns = null;  // parsed EntryPattern list

        // trailing stop state
        private double currentStopPrice   = 0.0;   // [PATCH 4] tracks current stop level

        // shutdown control
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;

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
                EnableTrailingStop   = true;
                TrailDistancePoints  = 5;    // [PATCH 4] default mirrors StopLossPoints
                EnableRealOrder      = false;  // default: observation only until user enables
                EntryPattern         = "0,1";
                BaseQuantity         = 1;
                MaxOrders            = 10;
                MaxConsecutiveLosses = 3;
                LogFilePath          = @"C:\temp\scalper_LONGrepeat_log.csv";
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
                    strategyStartUtc  = DateTime.UtcNow;
                    lifeStarted       = true;
                    compiledPatterns  = ParsePatternList(EntryPattern);
                    isArmed           = false;
                    enableRealOrder   = EnableRealOrder;
                    EnsureLogHeader();
                    DiagLog(Name + " enabled (MANAGED). Life=" + StrategyLifeMinutes
                        + "min, MaxOrders=" + MaxOrders
                        + ", MaxConsecLosses=" + MaxConsecutiveLosses
                        + ", Qty=" + BaseQuantity
                        + ", Stop=" + StopLossPoints + "pt"
                        + ", Target=" + ProfitTargetPoints + "pt"
                        + ", StopTargetHandling=PerEntryExecution"
                        + ", EnableTrailingStop=" + EnableTrailingStop
                        + (EnableTrailingStop ? ", TrailDist=" + TrailDistancePoints + "pt" : "")
                        + ", EnableRealOrder=" + EnableRealOrder
                        + ", EntryPattern=[" + EntryPattern + "]"
                        + (EnableRealOrder ? "" : " (observation only — no real orders)")
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

            // [PATCH 5] Fake trade tick-by-tick resolution
            // Runs on every tick regardless of CheckIntervalSeconds throttle
            if (inFakeTrade)
            {
                CheckFakeTrade();
                return;  // while in fake trade, don't process anything else
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

            // [PATCH 4] Trailing stop update — runs on same CheckIntervalSeconds timer
            // Only when in a position and trail is enabled.
            if (EnableTrailingStop && awaitingClose
                && Position.MarketPosition == MarketPosition.Long)
            {
                DoTrailingStop();
            }

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
            entryInFlight       = true;
            awaitingClose       = true;
            workingEntryOrder   = null;   // [PATCH 3] clear before submitting

            try
            {
                if (UseMarketEntry)
                {
                    workingEntryOrder = EnterLong(qty, ENTRY_SIGNAL);  // [PATCH 3]
                    DiagLog(string.Format("ENTRY #{0} MARKET qty={1}", ordersPlaced, qty));
                }
                else
                {
                    double limitPx = GetCurrentBid() - LimitOffsetPoints;
                    limitPx = Instrument.MasterInstrument.RoundToTickSize(limitPx);
                    workingEntryOrder = EnterLongLimit(0, true, qty, limitPx, ENTRY_SIGNAL);  // [PATCH 3]
                    DiagLog(string.Format("ENTRY #{0} LIMIT qty={1} limit={2:F2}",
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
        // CheckFakeTrade
        // [PATCH 5] Called every tick while inFakeTrade=true.
        // Checks if fake stop or fake target has been hit.
        // LONG: stop hit when bid <= fakeStopPrice
        //        target hit when ask >= fakeTargetPrice
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

                // Fake trade resolved
                int    bit      = stopHit ? 0 : 1;
                double exitPrice = stopHit ? fakeStopPrice : fakeTargetPrice;
                double pnl      = stopHit
                    ? -(StopLossPoints    * Instrument.MasterInstrument.PointValue)
                    : +(ProfitTargetPoints * Instrument.MasterInstrument.PointValue);

                outcomeHistory.Append(bit.ToString());
                inFakeTrade = false;

                DiagLog(string.Format("FAKE {0}: entry={1:F2} exit={2:F2} pnl={3:0.00} seq={4}",
                    stopHit ? "LOSS" : "WIN",
                    fakeEntryPrice, exitPrice, pnl,
                    outcomeHistory.ToString()));

                // Check pattern — arm real order if match
                bool prevArmed = isArmed;
                isArmed = AnyPatternMatchesTail();
                if (isArmed && !prevArmed)
                    DiagLog(string.Format("[ARMED] Pattern matched tail '{0}'" + (EnableRealOrder ? " → next entry is REAL." : " → (observation mode, no order)"),
                        TailForLog(8)));

                // Write to CSV (fake trades logged with FAKE prefix)
                WriteLogRowFake(exitPrice, pnl, bit);

                // Reset fake fields
                fakeEntryPrice  = 0.0;
                fakeStopPrice   = 0.0;
                fakeTargetPrice = 0.0;
                awaitingClose   = false;  // ready for next entry
            }
            catch (Exception ex)
            {
                DiagLog("[FAKE] CheckFakeTrade error: " + ex.Message);
                inFakeTrade   = false;
                awaitingClose = false;
            }
        }

        // =====================================================================
        // Pattern helpers [PATCH 5]
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

        private bool AnyPatternMatchesTail()
        {
            if (compiledPatterns == null || compiledPatterns.Count == 0) return false;
            string tail = outcomeHistory.ToString();
            foreach (var p in compiledPatterns)
            {
                if (tail.Length < p.Length) continue;
                string t = tail.Substring(tail.Length - p.Length);
                if (t == p) return true;
            }
            return false;
        }

        private string TailForLog(int n)
        {
            string s = outcomeHistory.ToString();
            return s.Length <= n ? s : "..." + s.Substring(s.Length - n);
        }

        // =====================================================================
        // DoTrailingStop
        // [PATCH 4] Called every CheckIntervalSeconds while in a long position.
        // Moves stop up as price rises — never moves stop down.
        // =====================================================================
        private void DoTrailingStop()
        {
            try
            {
                double currentPrice = GetCurrentBid();
                if (currentPrice <= 0) return;
                if (currentStopPrice <= 0) return;

                double proposedStop = currentPrice - TrailDistancePoints;
                proposedStop = Instrument.MasterInstrument.RoundToTickSize(proposedStop);

                if (proposedStop > currentStopPrice)
                {
                    DiagLog(string.Format("[TRAIL] Stop UP: {0:F2} -> {1:F2} (bid={2:F2}, dist={3}pt)",
                        currentStopPrice, proposedStop, currentPrice, TrailDistancePoints));
                    try
                    {
                        SetStopLoss(ENTRY_SIGNAL, CalculationMode.Price, proposedStop, false);
                        currentStopPrice = proposedStop;
                    }
                    catch (Exception ex)
                    {
                        DiagLog("[TRAIL] SetStopLoss error: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("[TRAIL] DoTrailingStop error: " + ex.Message);
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
                    // [PATCH 4] Record initial stop price for trail tracking
                    currentStopPrice  = entryFillPrice - StopLossPoints;
                    currentStopPrice  = Instrument.MasterInstrument.RoundToTickSize(currentStopPrice);
                    DiagLog(string.Format("Entry complete. Fill={0:F2} qty={1}. InitialStop={2:F2}. Trail={3}.",
                        entryFillPrice, entryFillQty, currentStopPrice,
                        EnableTrailingStop ? "ENABLED" : "disabled"));
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
                    currentStopPrice  = 0.0;   // [PATCH 4] reset trail tracking

                    // [PATCH 5] Re-check pattern after real trade closes
                    // [PATCH 5] Re-check pattern after real trade closes (always)
                    {
                        bool prevArmed = isArmed;
                        isArmed = AnyPatternMatchesTail();
                        if (isArmed && !prevArmed)
                            DiagLog(string.Format("[ARMED] Real trade closed. Pattern matched '{0}'" + (EnableRealOrder ? " → next entry is REAL." : " → (observation mode)"),
                                TailForLog(8)));
                        else if (!isArmed)
                            DiagLog("[SEQUENCE] Real trade closed. Pattern not matched → fake trades continue.");
                    }
                }
            }
        }

        // =====================================================================
        // OnOrderUpdate
        // [PATCH 3] Handles Cancelled state for ENTRY_SIGNAL.
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
                    // entryInFlight cleared so shutdown can proceed
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
            // This handles: limit order submitted but never filled when shutdown fires.
            // After CancelOrder(), we wait for OnOrderUpdate Cancelled callback
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
                        DiagLog(string.Format("Shutdown: cancelling unfilled entry order (state={0}).", os));
                        CancelOrder(workingEntryOrder);
                    }
                    catch (Exception ex)
                    {
                        DiagLog("Shutdown: CancelOrder error: " + ex.Message);
                        // Force-clear flags as fallback if cancel throws
                        entryInFlight     = false;
                        awaitingClose     = false;
                        workingEntryOrder = null;
                    }
                }
                // Wait for the Cancelled callback in OnOrderUpdate to clear entryInFlight
                return;
            }

            // If already flat and no entry in flight — terminate cleanly
            if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight)
            {
                FinalizeTermination();
                return;
            }

            // If holding a position — submit ExitLong at market
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

        private void WriteLogRowFake(double exitPrice, double pnl, int bit)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8}\n",
                    DateTime.Now, ordersPlaced, "FAKE_Long", 0,
                    fakeEntryPrice, exitPrice, pnl, bit, outcomeHistory.ToString());
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
        [Display(Name = "D: Check interval (seconds)", Order = 7, GroupName = "Timing",
            Description = "Seconds between entry checks. Also used as the trailing stop "
                        + "update interval when EnableTrailingStop=true.")]
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

        // [PATCH 5] Real order enable + pattern filter
        [NinjaScriptProperty]
        [Display(Name = "Enable Real Order", Order = 1, GroupName = "Pattern & Real Order",
            Description = "When FALSE (default): observation mode only. No real orders placed. "
                        + "Fake trades run silently recording every outcome into the sequence. "
                        + "Use this to study the pattern before risking real money. "
                        + "When TRUE: real orders placed, gated by Entry Pattern. "
                        + "The sequence records ALL trades (fake + real) regardless of this setting.")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry Pattern (comma-separated)", Order = 2, GroupName = "Pattern & Real Order",
            Description = "Comma-separated binary patterns checked after EVERY trade (fake or real). "
                        + "0=loss 1=win. When EnableRealOrder=true and tail matches → real order fires. "
                        + "When EnableRealOrder=false → pattern checked but no order placed. "
                        + "Default '0,1' = every outcome matches (no filter). "
                        + "Examples: '01' = only after loss+win. '01,011' = loss+win OR loss+win+win.")]
        public string EntryPattern { get; set; }

        // [PATCH 4] Trailing stop
        [NinjaScriptProperty]
        [Display(Name = "Enable Trailing Stop", Order = 12, GroupName = "Bracket",
            Description = "[PATCH 4] When true: stop ratchets up as price rises, checked every "
                        + "CheckIntervalSeconds. When false: fixed stop only (original behavior). "
                        + "Default true.")]
        public bool EnableTrailingStop { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Trail distance (points)", Order = 13, GroupName = "Bracket",
            Description = "[PATCH 4] Distance in points between current bid and trailing stop. "
                        + "Default mirrors StopLossPoints. Can be set tighter than StopLossPoints "
                        + "(e.g. stop=20pt initial, trail=10pt once in profit). "
                        + "Only used when EnableTrailingStop=true.")]
        public double TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity (fixed)", Order = 14, GroupName = "Quantity")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max orders (then disable)", Order = 15, GroupName = "Limits")]
        public int MaxOrders { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max consecutive losses (then disable)", Order = 16, GroupName = "Limits")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log file path", Order = 17, GroupName = "Logging")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
