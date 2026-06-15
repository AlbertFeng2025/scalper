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

// scalper_SHORTrepeat_Layer2  v3
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
        private StringBuilder rawString         = new StringBuilder(); // Layer 0: all bricks
        private StringBuilder filter1Outcome    = new StringBuilder(); // Layer 1: after F1 match
        private StringBuilder realTradeOutcome  = new StringBuilder(); // real money trade results only

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

        // ── qty multiplier table ──────────────────────────────────────────────
        // Edit this table to define your position sizing strategy.
        // Pattern is checked against the TAIL of realTradeOutcome string.
        // Longest matching pattern wins (most specific takes priority).
        // Default multiplier = 1 if no pattern matches.
        // Only used when EnableQtyIncrement = true.
        //
        // Pattern meaning:
        //   "10"    = 1 win followed by 1 loss  → multiply qty by 2
        //   "100"   = 1 win followed by 2 losses → multiply qty by 2
        //   "1000"  = 1 win followed by 3 losses → multiply qty by 3
        //   "10000" = 1 win followed by 4 losses → multiply qty by 4
        //             (remove this line to surrender at 4 losses instead)
        //
        // You can use any pattern and any multiplier, for example:
        //   ("10",    2)  → double after 1 loss
        //   ("100",   4)  → quadruple after 2 losses
        //   ("1000",  8)  → 8x after 3 losses
        //   ("10000", 10) → 10x after 4 losses
        //   OR: ("10", 2), ("100", 2), ("1000", 3), ("10000", 3) → gradual
        //
        // Future: this table will be loaded from external JSON/config file.
        // ─────────────────────────────────────────────────────────────────────
        private static readonly (string pattern, int multiplier)[] QtyMultiplierTable =
        {
            ("10000", 4),   // 4 losses after win → ×4 (remove to surrender at ×1)
            ("1000",  3),   // 3 losses after win → ×3
            ("100",   2),   // 2 losses after win → ×2
            ("10",    2),   // 1 loss  after win  → ×2
        };

        // ── computed qty for current money trade ──────────────────────────────
        private int currentQty = 1;  // recalculated in StartNextSlice when armed

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
                EnableQtyIncrement   = false;
                MaxTotalSliceCount   = 100;
                MaxRealLossInARow    = 3;
                LogFilePath          = @"C:\temp\scalper_SHORTrepeat_Layer2_log.csv";
                StartMode            = 0;
                RawStringFilePath    = @"C:\temp\rawString_SHORT_20stop_20profit.csv";
                MaxFileAgeMinutes    = 10;
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
                    currentQty          = BaseQuantity;
                    realTradeOutcome.Clear();
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
                        + ", Filter2=[" + Filter2Pattern + "]"
                        + ", StartMode=" + (StartMode == 0 ? "Fresh" : "LoadFromFile")
                        + (StartMode == 1 ? ", RawStringFile=" + RawStringFilePath : ""));

                    // ── load and replay pre-built raw string if requested ──────
                    if (StartMode == 1)
                        LoadAndReplayRawString();
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
        // CalcQty — returns qty for next money trade based on QtyMultiplierTable
        // Only called when EnableQtyIncrement = true.
        // Checks realTradeOutcome tail against each table entry (longest first).
        // First match wins. No match → BaseQuantity × 1.
        // =====================================================================
        private int CalcQty()
        {
            if (!EnableQtyIncrement) return BaseQuantity;

            string outcome = realTradeOutcome.ToString();
            if (outcome.Length == 0) return BaseQuantity;

            // Table is ordered longest→shortest so first match = most specific
            foreach (var entry in QtyMultiplierTable)
            {
                if (outcome.Length >= entry.pattern.Length
                    && outcome.EndsWith(entry.pattern))
                {
                    int qty = BaseQuantity * entry.multiplier;
                    DiagLog(string.Format(
                        "[QTY] realTradeOutcome tail matches '{0}' → multiplier={1} → qty={2}",
                        entry.pattern, entry.multiplier, qty));
                    return qty;
                }
            }

            DiagLog(string.Format(
                "[QTY] No pattern match for realTradeOutcome tail '{0}' → qty={1} (default)",
                outcome.Length > 8 ? "..." + outcome.Substring(outcome.Length - 8) : outcome,
                BaseQuantity));
            return BaseQuantity;
        }

        // =====================================================================
        // StartNextSlice — decides if next slice is fake or money
        // Based on: isArmed AND rawString tail matches Filter1Pattern
        // =====================================================================
        private void StartNextSlice()
        {
            bool startMoney = nextIsMoney;
            nextIsMoney = false;

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
                // ── calculate qty for this money trade ────────────────────────
                currentQty = CalcQty();

                awaitingClose     = true;
                entryInFlight     = true;
                workingEntryOrder = null;
                try
                {
                    if (UseMarketEntry)
                    {
                        workingEntryOrder = EnterShort(currentQty, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SHORT SLICE #{0} MARKET qty={1} entry~{2:F2} stop={3:F2}(above) target={4:F2}(below) | rawString={5} | filter1Outcome={6} | realTradeOutcome={7}",
                            sliceCount, currentQty, sliceEntryPrice, sliceStopPrice, sliceTargetPrice,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8),
                            TailOf(realTradeOutcome, 8)));
                    }
                    else
                    {
                        double limitPx = Instrument.MasterInstrument.RoundToTickSize(
                            GetCurrentAsk() + LimitOffsetPoints);
                        workingEntryOrder = EnterShortLimit(0, true, currentQty, limitPx, ENTRY_SIGNAL);
                        DiagLog(string.Format(
                            "MONEY SHORT SLICE #{0} LIMIT qty={1} limit={2:F2} | rawString={3} | filter1Outcome={4} | realTradeOutcome={5}",
                            sliceCount, currentQty, limitPx,
                            TailOf(rawString, 8), TailOf(filter1Outcome, 8),
                            TailOf(realTradeOutcome, 8)));
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
        // CheckFakeSlice — tick by tick brick resolution (LONG)
        // TARGET = 1 = price DOWN (SHORT)
        //   stop hit   when ask >= sliceStopPrice   → bit = 0  (price UP against short)
        //   target hit when bid <= sliceTargetPrice → bit = 1  (price DOWN for short)
        //
        // BRICK END CLEANUP — if this was a money slice:
        //   Case 1: not filled (position flat, order pending)
        //     → cancel order, sliceCount--, no bit recorded
        //   Case 2: filled but position still open (slippage/timing)
        //     → force close, record bit from brick outcome
        //   Case 3: already closed by bracket (normal)
        //     → nothing to do, already handled by OnExecutionUpdate
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

                bool wasMoneySlice = isMoneySlice;
                inSlice      = false;
                isMoneySlice = false;

                double logEntryPrice = sliceEntryPrice;
                sliceEntryPrice  = 0.0;
                sliceStopPrice   = 0.0;
                sliceTargetPrice = 0.0;

                // ── Brick end cleanup for money slice ─────────────────────────
                if (wasMoneySlice)
                {
                    // Case 1: order never filled — cancel and discard
                    if (Position.MarketPosition == MarketPosition.Flat
                        && workingEntryOrder != null
                        && (workingEntryOrder.OrderState == OrderState.Working
                            || workingEntryOrder.OrderState == OrderState.Accepted
                            || workingEntryOrder.OrderState == OrderState.Submitted))
                    {
                        DiagLog(string.Format(
                            "[BRICK CLEANUP Case1] Slice #{0} — order never filled. Cancelling. No bit recorded.",
                            sliceCount));
                        try { CancelOrder(workingEntryOrder); } catch (Exception ex) {
                            DiagLog("CancelOrder error: " + ex.Message);
                        }
                        workingEntryOrder = null;
                        entryInFlight     = false;
                        awaitingClose     = false;
                        sliceCount--;  // refund — no trade happened
                        WriteLogRowCancelled(logEntryPrice);
                        return;  // NO pipeline update — brick discarded
                    }

                    // Case 2: filled but position still open — force close
                    if (Position.MarketPosition == MarketPosition.Short)
                    {
                        DiagLog(string.Format(
                            "[BRICK CLEANUP Case2] Slice #{0} — position still open at brick end. Force closing. bit={1}",
                            sliceCount, bit));
                        try { ExitShort(Math.Abs(Position.Quantity), "SR_ForceClose", ENTRY_SIGNAL); }
                        catch (Exception ex) { DiagLog("ForceClose error: " + ex.Message); }
                        awaitingClose     = false;
                        entryInFlight     = false;
                        workingEntryOrder = null;
                        // record real trade outcome
                        realTradeOutcome.Append(bit.ToString());
                        if (bit == 0) {
                            realLossesInARow++;
                            DiagLog(string.Format("[REAL LOSS forced] realLossesInARow={0}", realLossesInARow));
                        } else {
                            realLossesInARow = 0;
                            DiagLog("[REAL WIN forced] realLossesInARow reset to 0");
                        }
                        UpdatePipeline(bit);
                        WriteLogRow(entryFillPrice > 0 ? entryFillPrice : logEntryPrice,
                            exitPrice, pnl, bit, entryFillQty > 0 ? entryFillQty : currentQty,
                            DateTime.Now);
                        entryFillPrice = 0.0;
                        entryFillQty   = 0;
                        return;
                    }

                    // Case 3: already closed normally by OnExecutionUpdate
                    if (Position.MarketPosition == MarketPosition.Flat && !awaitingClose)
                    {
                        DiagLog(string.Format(
                            "[BRICK CLEANUP Case3] Slice #{0} — already closed by bracket. Normal.",
                            sliceCount));
                        return;  // already handled, do not double-record
                    }
                }

                // ── Normal fake slice ─────────────────────────────────────────
                DiagLog(string.Format(
                    "FAKE SHORT SLICE #{0} {1}: entry={2:F2} exit={3:F2} pnl={4:0.00} bit={5}",
                    sliceCount, stopHit ? "LOSS" : "WIN",
                    logEntryPrice, exitPrice, pnl, bit));

                // update pipeline FIRST
                UpdatePipeline(bit);

                // log AFTER pipeline updated
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
        // LoadAndReplayRawString — called once on enable when StartMode=1
        //
        // VERIFICATION (hard stop on failure — no auto fallback):
        //   Step 1: File exists
        //   Step 2: Header settings match (direction=SHORT, stop, profit)
        //   Step 3: Last timestamp within MaxFileAgeMinutes
        //   Step 4: Bit count >= 20 (soft warn only, continues)
        //   Step 5: Replay all bits through UpdatePipeline()
        //
        // On hard stop: strategy terminates. User must either:
        //   A) Fix RawStringFilePath to correct file
        //   B) Change StartMode=0 for fresh start
        //   Then re-enable manually.
        // =====================================================================
        private void LoadAndReplayRawString()
        {
            try
            {
                // ── Step 1: File exists ───────────────────────────────────────
                if (!File.Exists(RawStringFilePath))
                {
                    string msg = "[LOAD FAILED] File not found: " + RawStringFilePath + "\n"
                               + "  Action: Verify path is correct, or set StartMode=0 for fresh start.\n"
                               + "  Strategy will now terminate.";
                    DiagLog(msg); Print(msg);
                    BeginShutdown("LoadAndReplayRawString: file not found — " + RawStringFilePath);
                    return;
                }

                string[] lines = File.ReadAllLines(RawStringFilePath);

                // ── Step 2: Parse header and verify settings ──────────────────
                string fileDirection = "";
                string fileStop      = "";
                string fileProfit    = "";

                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (!t.StartsWith("#")) break;
                    if      (t.StartsWith("# direction:"))  fileDirection = t.Replace("# direction:",  "").Trim().ToUpper();
                    else if (t.StartsWith("# stop_pts:"))   fileStop      = t.Replace("# stop_pts:",   "").Trim();
                    else if (t.StartsWith("# profit_pts:")) fileProfit    = t.Replace("# profit_pts:", "").Trim();
                }

                // direction check — SHORT strategy must load SHORT file
                const string stratDirection = "SHORT";
                if (fileDirection != stratDirection)
                {
                    string msg = "[LOAD FAILED] Direction mismatch.\n"
                               + "  File says:  direction=" + fileDirection + "\n"
                               + "  Strategy:   direction=" + stratDirection + "\n"
                               + "  Action: Point RawStringFilePath to a SHORT file,\n"
                               + "          or set StartMode=0 for fresh start.\n"
                               + "  Strategy will now terminate.";
                    DiagLog(msg); Print(msg);
                    BeginShutdown("LoadAndReplayRawString: direction mismatch file="
                        + fileDirection + " strategy=" + stratDirection);
                    return;
                }

                // stop loss check
                double fileStopVal = 0;
                if (!double.TryParse(fileStop,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out fileStopVal) || Math.Abs(fileStopVal - StopLossPoints) > 0.001)
                {
                    string msg = "[LOAD FAILED] StopLossPoints mismatch.\n"
                               + "  File says:  stop_pts=" + fileStop + "\n"
                               + "  Strategy:   StopLossPoints=" + StopLossPoints + "\n"
                               + "  Action: Match parameters or set StartMode=0.\n"
                               + "  Strategy will now terminate.";
                    DiagLog(msg); Print(msg);
                    BeginShutdown("LoadAndReplayRawString: stop mismatch file="
                        + fileStop + " strategy=" + StopLossPoints);
                    return;
                }

                // profit target check
                double fileProfitVal = 0;
                if (!double.TryParse(fileProfit,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out fileProfitVal) || Math.Abs(fileProfitVal - ProfitTargetPoints) > 0.001)
                {
                    string msg = "[LOAD FAILED] ProfitTargetPoints mismatch.\n"
                               + "  File says:  profit_pts=" + fileProfit + "\n"
                               + "  Strategy:   ProfitTargetPoints=" + ProfitTargetPoints + "\n"
                               + "  Action: Match parameters or set StartMode=0.\n"
                               + "  Strategy will now terminate.";
                    DiagLog(msg); Print(msg);
                    BeginShutdown("LoadAndReplayRawString: profit mismatch file="
                        + fileProfit + " strategy=" + ProfitTargetPoints);
                    return;
                }

                // ── Step 3: Find last timestamp and check staleness ───────────
                DateTime lastTimestamp = DateTime.MinValue;
                int      bitCount      = 0;

                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                    // Support both old 2-column (timestamp,bit) and
                    // new 3-column (timestamp,bit,bitStringForHuman) format.
                    // Bit is always the SECOND field (between 1st and 2nd comma).
                    int firstComma = t.IndexOf(',');
                    if (firstComma < 0) continue;
                    int secondComma = t.IndexOf(',', firstComma + 1);
                    string bitStr = secondComma >= 0
                        ? t.Substring(firstComma + 1, secondComma - firstComma - 1).Trim()
                        : t.Substring(firstComma + 1).Trim();
                    if (bitStr != "0" && bitStr != "1") continue;
                    bitCount++;
                    DateTime ts;
                    if (DateTime.TryParse(t.Substring(0, firstComma).Trim(), out ts))
                        lastTimestamp = ts;
                }

                if (lastTimestamp == DateTime.MinValue)
                {
                    string msg = "[LOAD FAILED] No valid data lines found in file.\n"
                               + "  File: " + RawStringFilePath + "\n"
                               + "  Action: Verify Part A is running and writing bits,\n"
                               + "          or set StartMode=0 for fresh start.\n"
                               + "  Strategy will now terminate.";
                    DiagLog(msg); Print(msg);
                    BeginShutdown("LoadAndReplayRawString: no valid data in file");
                    return;
                }

                double ageMinutes = (DateTime.Now - lastTimestamp).TotalMinutes;
                if (ageMinutes > MaxFileAgeMinutes)
                {
                    string msg = "[LOAD FAILED] File is stale.\n"
                               + "  Last bit recorded: " + lastTimestamp.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                               + "  Current time:      " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                               + "  Gap:               " + ageMinutes.ToString("F1") + " min"
                               + " (max allowed: " + MaxFileAgeMinutes + " min)\n"
                               + "  Action: Verify Part A (rawString recorder) is still enabled\n"
                               + "          and actively writing to: " + RawStringFilePath + "\n"
                               + "          Or set StartMode=0 for fresh start.\n"
                               + "  Strategy will now terminate.";
                    DiagLog(msg); Print(msg);
                    BeginShutdown("LoadAndReplayRawString: file stale "
                        + ageMinutes.ToString("F1") + " min > max " + MaxFileAgeMinutes + " min");
                    return;
                }

                // ── Step 4: Soft warn if too few bits ─────────────────────────
                const int MIN_BITS = 20;
                if (bitCount < MIN_BITS)
                {
                    string warn = "[LOAD WARN] Only " + bitCount + " bits in file "
                                + "(recommended minimum: " + MIN_BITS + ").\n"
                                + "  Pipeline will be weakly warmed. "
                                + "Consider waiting for more bits before enabling.";
                    DiagLog(warn); Print(warn);
                }

                // ── Step 5: Replay all bits through UpdatePipeline() ──────────
                int replayed = 0;
                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                    // Support both old 2-column (timestamp,bit) and
                    // new 3-column (timestamp,bit,bitStringForHuman) format.
                    // Bit is always the SECOND field (between 1st and 2nd comma).
                    int firstComma = t.IndexOf(',');
                    if (firstComma < 0) continue;
                    int secondComma = t.IndexOf(',', firstComma + 1);
                    string bitStr = secondComma >= 0
                        ? t.Substring(firstComma + 1, secondComma - firstComma - 1).Trim()
                        : t.Substring(firstComma + 1).Trim();
                    if (bitStr != "0" && bitStr != "1") continue;
                    UpdatePipeline(int.Parse(bitStr));
                    replayed++;
                }

                DiagLog(string.Format(
                    "[LOAD OK] Replayed {0} bits. Last timestamp={1} (age={2:F1} min). "
                    + "Pipeline: rawString.len={3} f1.len={4} "
                    + "isArmed={5} waitF1={6} nextIsMoney={7}",
                    replayed,
                    lastTimestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    ageMinutes,
                    rawString.Length, filter1Outcome.Length,
                    isArmed, waitingForF1Outcome, nextIsMoney));
            }
            catch (Exception ex)
            {
                string msg = "[LOAD FAILED] Unexpected error: " + ex.Message + "\n"
                           + "  Strategy will now terminate.\n"
                           + "  Action: Check file format or set StartMode=0.";
                DiagLog(msg); Print(msg);
                BeginShutdown("LoadAndReplayRawString exception: " + ex.Message);
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

                    // update real loss streak and realTradeOutcome string
                    realTradeOutcome.Append(bit.ToString());
                    if (bit == 0)
                    {
                        realLossesInARow++;
                        DiagLog(string.Format("[REAL LOSS] realLossesInARow={0} / max={1} | realTradeOutcome={2}",
                            realLossesInARow, MaxRealLossInARow, realTradeOutcome.ToString()));
                    }
                    else
                    {
                        if (realLossesInARow > 0)
                            DiagLog(string.Format("[REAL WIN] Resetting realLossesInARow {0}→0 | realTradeOutcome={1}",
                                realLossesInARow, realTradeOutcome.ToString()));
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
                + " | filter1Outcome=" + filter1Outcome.ToString()
                + " | realTradeOutcome=" + realTradeOutcome.ToString());
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
                    "timestamp(machine_local_time),slice_num,side,quantity,entry_price,exit_price,realized_pnl,win_loss_bit,rawString,filter1Outcome,realTradeOutcome\n");
            }
            catch (Exception ex) { Print("Log header error: " + ex.Message); }
        }

        private void WriteLogRow(double entryPrice, double exitPrice, double pnl, int bit, int qty, DateTime exitTime)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10}\n",
                    exitTime, sliceCount, "Short", qty,
                    entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
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
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6:0.00},{7},{8},{9},{10}\n",
                    DateTime.Now, sliceCount, "FAKE_Short", 0,
                    entryPrice, exitPrice, pnl, bit,
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                File.AppendAllText(LogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error (fake): " + ex.Message); }
        }

        private void WriteLogRowCancelled(double entryPrice)
        {
            try
            {
                string row = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}\n",
                    DateTime.Now, sliceCount, "CANCELLED_no_fill", 0,
                    entryPrice, 0, 0, "-",
                    rawString.ToString(), filter1Outcome.ToString(), realTradeOutcome.ToString());
                File.AppendAllText(LogFilePath, row);
            }
            catch (Exception ex) { Print("Log write error (cancelled): " + ex.Message); }
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
        [Display(Name = "Enable Qty Increment (if enabled, see code — QtyMultiplierTable)",
            Order = 15, GroupName = "Quantity",
            Description = "FALSE = always use BaseQuantity. "
                        + "TRUE = qty scales dynamically per QtyMultiplierTable hardcoded in strategy. "
                        + "Edit QtyMultiplierTable in source code to define your sizing pattern.")]
        public bool EnableQtyIncrement { get; set; }

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

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name = "Start Mode (0=Fresh 1=LoadFromFile)", Order = 1, GroupName = "Raw String Load",
            Description = "0=Fresh start (default). 1=Load pre-built raw string from file and replay through pipeline before live trading.")]
        public int StartMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Raw String File Path", Order = 2, GroupName = "Raw String Load",
            Description = "Path to file produced by LONG_SHORT_rawString_recorder. "
                        + "MUST match Direction=SHORT, StopLossPoints, ProfitTargetPoints. "
                        + "Only used when StartMode=1.")]
        public string RawStringFilePath { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max File Age (minutes)", Order = 3, GroupName = "Raw String Load",
            Description = "Maximum age of last bit in file before rejecting as stale. "
                        + "Default 10 min — Part A must be actively running when Part B loads. "
                        + "Strategy terminates if last bit is older than this threshold.")]
        public int MaxFileAgeMinutes { get; set; }

        #endregion
    }
}
