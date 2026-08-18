#region Using declarations
using System;
using System.IO;
using System.Text;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// =============================================================================
// Renko_mergedTransit_layer3  —  DUAL-DIRECTION merged Renko, THREE layers
// =============================================================================
// One instance trades BOTH long and short off a MERGED brick stream.
//
// THREE-LAYER PIPELINE (each string is the win/loss of the layer below it):
//   brick colors (rawString)                     green=1 / red=0
//     -> F1 shape picks direction + entry        (green-red=LONG, red-green=SHORT)
//   filter1Outcome = win/loss of each F1 trade   (1 = that book's trade won)
//     -> F2 shape on filter1Outcome  => isArmed
//   filter2Outcome = win/loss of each ARMED (L2) would-be trade
//     -> F3 shape on filter2Outcome  => isArmed3
//   REAL order fires only when: F1 signal AND isArmed(F2) AND isArmed3(F3).
//
//   MAPPING to the fixed single-book strategy: merged filter1Outcome is the SAME
//   thing as the fixed strategy's rawString (both are trade win/loss). So fixed-F1
//   -> merged-F2, and fixed-F2 -> merged-F3. Merged spends F1 turning colors into
//   trades; the fixed book gets that free by fixing direction.
//
//   WARM-UP / SESSION TEMPLATE:  filter2Outcome (=> F3) needs several L2 would-be
//   trades to accumulate before it can arm. To carry a pre-market warm-up into the
//   09:30 RTH window, apply an ETH (24h Globex) session template and enable ~5 AM.
//   An RTH template opens a NEW session at 09:30 and (with RestartOnNewSession=true)
//   WIPES the pipeline at 09:30 -> you start cold. Use ETH, or RestartOnNewSession=false.
//
//   Bit convention (MERGED / LONG-string):  Close rose = GREEN = 1
//                                            Close fell = RED   = 0
//   shortString = bitwise flip of longString.
//
//   Each brick, F1 is tested against BOTH strings:
//     - longString  tail matches F1  ->  a LONG transit  -> place LONG
//     - shortString tail matches F1  ->  a SHORT transit -> place SHORT
//   For F1="10" the two never match on the same brick (a tail can't be both
//   "10" and "01"); if a symmetric F1 ever matches both, LONG is taken first
//   and (serialized) SHORT is skipped while the long is open.
//
//   DIRECTION = the book that matched. "Chase the transit":
//     long-string "10" (green->red) -> bet next green -> LONG
//     short-string "10" (= real red->green) -> bet next red -> SHORT
//
//   Bracket geometry flips per trade, anchored to the BRICK CLOSE (Close[0]):
//     LONG : stop = brickClose - StopLossPoints (below), target = brickClose + ProfitTargetPoints (above)
//     SHORT: stop = brickClose + StopLossPoints (above), target = brickClose - ProfitTargetPoints (below)
//
//   Layer 1 = fires on EVERY F1 match (no F2 gate). filter1Outcome (merged,
//   win-encoded) is still collected for observation/logging.
//
//   ONE account, SERIALIZED: a new signal is skipped while a position/order is
//   open. With exit-bit=1 (win-and-quit) the book clears fast, so the next
//   transit is usually free to take.
//
//   Qty rule, breaker, exit-bit, trading hours, gap fresh-start, and the
//   StopCancelCloseIgnoreRejects safety are the SAME as the single-book files.
//   Qty rule / exit-bit / breaker all read the MERGED realTradeOutcome (a loss
//   is a loss regardless of side).
// =============================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
    public class Renko_mergedTransit_layer3 : Strategy
    {
        // ── merged pipeline strings (LONG encoding: green=1/red=0) ────────────
        private readonly StringBuilder longStr        = new StringBuilder();
        private readonly StringBuilder shortStr       = new StringBuilder();
        private readonly StringBuilder filter1Outcome = new StringBuilder(); // merged, win-encoded (observation)
        private readonly StringBuilder realTradeOutcome = new StringBuilder(); // merged real W/L
        private readonly StringBuilder filter2Outcome = new StringBuilder(); // L3: win/loss of ARMED (L2) would-be trades

        // ── pipeline / order state ────────────────────────────────────────────
        private int    prevBarBit    = -1;     // 1=green, 0=red
        private int    barCount      = 0;
        private bool   entryInFlight = false;
        private bool   awaitingClose = false;
        private double entryFillPrice = 0.0;
        private int    entryFillQty   = 0;
        private int    tradeSide      = 0;      // +1 long, -1 short, 0 flat (side of the working money trade)
        private const string ENTRY_LONG  = "ML2_Long";
        private const string ENTRY_SHORT = "ML2_Short";

        // waiting-for-outcome (observation string, per book)
        private bool waitLongOutcome  = false;
        private bool waitShortOutcome = false;
        private bool isArmed          = false;   // Layer-2: F2 matched on merged outcome
        private bool waitLongOutcome2  = false;  // Layer-3: awaiting outcome of an armed LONG would-be trade
        private bool waitShortOutcome2 = false;  // Layer-3: awaiting outcome of an armed SHORT would-be trade
        private bool isArmed3          = false;  // Layer-3: F3 matched on filter2Outcome
        private bool loggedSessionTemplate = false;

        // ── loss streak / qty ─────────────────────────────────────────────────
        private int realLossesInARow = 0;
        private (string pattern, int multiplier)[] qtyTable =
            new (string pattern, int multiplier)[] { ("00", 2), ("000", 3), ("0000", 5) };
        private int currentQty = 1;
        private readonly StringBuilder sessionRealOutcome = new StringBuilder();

        // ── life / shutdown ───────────────────────────────────────────────────
        private DateTime strategyStartUtc = DateTime.MinValue;
        private bool     lifeStarted      = false;
        private bool     pendingFlatten   = false;
        private string   pendingReason    = string.Empty;

        // ── logging / gap ─────────────────────────────────────────────────────
        private string  activeLogFilePath = null;
        private bool    seedPending      = false;   // prepend the previously-closed bar once after a clean enable (NOT the forming bar)
        private bool    firstBrickDone   = false;
        private int     lastProcessedBar = -1;      // brick-contiguity (hole) detection
        private bool    marginActive     = false;   // inside the margin-cutoff flat window
        private bool    marginLogged     = false;   // one-shot 'window active' log

        // ── parsed F1 list ────────────────────────────────────────────────────
        private System.Collections.Generic.List<string> filter1Patterns =
            new System.Collections.Generic.List<string>();
        // parsed F2 list (multi-pattern OR on the merged outcome string)
        private System.Collections.Generic.List<string> filter2Patterns =
            new System.Collections.Generic.List<string>();
        // parsed F3 list (multi-pattern OR on the ARMED-trade outcome string)
        private System.Collections.Generic.List<string> filter3Patterns =
            new System.Collections.Generic.List<string>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Renko_mergedTransit_layer3";
                Description = "Dual-direction MERGED Renko Layer-3. green=1/red=0. F1 picks direction; F2 arms on the "
                            + "F1-trade win/loss string (filter1Outcome); F3 arms on the ARMED-trade win/loss string "
                            + "(filter2Outcome). Real order needs F1 signal AND F2-armed AND F3-armed. Use an ETH "
                            + "template for a pre-market warm-up into 09:30.";

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade          = 0;
                IsUnmanaged                  = false;

                EnableTradingHours   = true;
                TradingStartHour     = 9;
                TradingStartMinute   = 30;
                TradingEndHour       = 11;
                TradingEndMinute     = 30;
                StrategyLifeMinutes  = 1440;
                UseMarketEntry       = true;
                LimitOffsetPoints    = 5;
                StopLossPoints       = 10;      // 40-tick brick default
                ProfitTargetPoints   = 20;
                EnableRealOrder      = false;   // observation only until flipped
                Filter1Pattern       = "10";
                Filter2Pattern       = "100,01";
                Filter3Pattern       = "110,00110";
                BaseQuantity         = 1;
                EnableQtyIncrement   = false;
                QtyRuleText          = "(\"00\":2),(\"000\":3),(\"0000\":5)";
                EnableTradeOutcomeExit  = true; // exit-bit on by default per design
                TradeOutcomeExitPattern = "1";  // win-and-quit
                MaxTotalBarCount     = 100000;
                MaxRealLossInARow    = 5;
                LogFolder            = @"C:\temp";
                LogBaseName          = "Renko_mergedTransit_layer3";
                SeedPendingBarOnStart = true;
                RestartOnNewSession   = true;
                EnableMarginCutoff    = true;   // early EOD before broker overnight-margin snapshot
                MarginCutoffHour      = 16;     // 16:35 NY cutoff -> flatten 16:30 NY (15 min before the 16:45 NY snapshot)
                MarginCutoffMinute    = 35;
                MarginCutoffLeadMin   = 5;      // flatten 5 min before -> 16:30 NY
            }
            else if (State == State.Configure)
            {
                // 1-minute clock series: reliable heartbeat so the margin cutoff fires on time
                // even when Renko bricks are sparse (thin post-RTH tape).
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    strategyStartUtc = DateTime.UtcNow;
                    lifeStarted      = true;
                    ParseFilter1Patterns();
                    ParseFilter2Patterns();
                    ParseFilter3Patterns();
                    ParseQtyRule();
                    FreshStart("strategy enabled (fresh start)", SeedPendingBarOnStart);
                }
            }
        }

        // =====================================================================
        // OnBarUpdate — brick close -> bit -> merged pipeline -> maybe trade(s)
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;
            if (BarsInProgress == 1) { CheckMarginCutoff(); return; }   // 1-min clock series
            if (BarsInProgress != 0) return;                            // ignore any other series
            if (CurrentBar < 1) return;

            if (pendingFlatten) { DoFlatten(); return; }
            // margin-cutoff backup: also flatten on a brick close inside the window
            // (guaranteed-correct order context; the 1-min series covers quiet tape).
            if (marginActive && Position.MarketPosition != MarketPosition.Flat) { MarginFlatten(); return; }

            // ── NEW SESSION: clean roll (reset qty/breaker; optionally fresh log + empty pipeline) ──
            if (firstBrickDone && Bars.IsFirstBarOfSession)
            {
                sessionRealOutcome.Clear();
                realLossesInARow = 0;
                if (RestartOnNewSession)
                    FreshStart("new trading session", false);   // no seed: do NOT carry prior day across
            }
            // ── HOLE: bricks were skipped (a disconnect that MISSED bricks) -> clean restart, NO seed ──
            else if (lastProcessedBar >= 0 && CurrentBar > lastProcessedBar + 1)
            {
                FreshStart("brick gap: CurrentBar " + lastProcessedBar + " -> " + CurrentBar
                    + " (missed " + (CurrentBar - lastProcessedBar - 1) + " brick(s))", false);
            }
            // (a short blip that missed NO bricks advances CurrentBar by exactly 1 -> we just continue)
            lastProcessedBar = CurrentBar;
            firstBrickDone   = true;

            if (!loggedSessionTemplate)
            {
                loggedSessionTemplate = true;
                string thName = "(unknown)";
                try { if (Bars != null && Bars.TradingHours != null) thName = Bars.TradingHours.Name; } catch {}
                DiagLog("[SESSION TEMPLATE] " + thName + " | RestartOnNewSession=" + RestartOnNewSession
                    + " -- WARM-UP: for 09:30 RTH trading with a pre-market warm-up, use an ETH (24h) template. "
                    + "An RTH template resets the pipeline at the 09:30 session open (when RestartOnNewSession=true) -> no warm-up.");
            }

            // ── SEED (one time, after a clean enable): prepend the PREVIOUSLY-CLOSED bar (the bar just BEFORE the one forming at enable),
            //    recovered from Close[1] vs Close[2] on the first realtime brick. (Verified 2026-08-14: seededCloseTime predated enable -> this is the prior closed bar; the forming bar is captured normally as the first live brick / Close[0].) Observation only - it NEVER places an order. ──
            if (seedPending)
            {
                seedPending = false;
                if (CurrentBar >= 2)
                {
                    int seedBit;
                    if (Close[1] > Close[2])      seedBit = 1;   // green
                    else if (Close[1] < Close[2]) seedBit = 0;   // red
                    else                          seedBit = 0;
                    longStr.Append(seedBit == 1 ? "1" : "0");
                    shortStr.Append(seedBit == 1 ? "0" : "1");
                    prevBarBit = seedBit;
                    barCount++;
                    DiagLog("[SEED BAR #" + barCount + "] seededClose=" + Close[1].ToString("F2")
                        + " seededCloseTime=" + Time[1].ToString("yyyy-MM-dd HH:mm:ss")
                        + " (compare to enable time: before=previous-closed bar, after=forming bar)"
                        + " prevClose=" + Close[2].ToString("F2") + " bit="
                        + seedBit + " (" + (seedBit == 1 ? "GREEN" : "RED") + ") - observation only, no order.");
                }
            }

            // ── life / bar-count / breaker shutdown checks ───────────────────
            if (StrategyLifeMinutes > 0 && (DateTime.UtcNow - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            { BeginShutdown("strategy life reached"); return; }
            if (barCount >= MaxTotalBarCount) { BeginShutdown("MaxTotalBarCount reached"); return; }
            if (realLossesInARow >= MaxRealLossInARow) { BeginShutdown("MaxRealLossInARow reached"); return; }

            // ── derive the brick bit (green=1 / red=0) from close-vs-close ────
            int bit;
            if (Close[0] > Close[1])      bit = 1;   // up brick  (green)
            else if (Close[0] < Close[1]) bit = 0;   // down brick (red)
            else                          bit = (prevBarBit >= 0) ? prevBarBit : 0;
            prevBarBit = bit;
            barCount++;

            // ── collect prior-match observation outcomes (merged, win-encoded)
            // long win = green next (bit==1); short win = red next (bit==0)
            if (waitLongOutcome)
            {
                waitLongOutcome = false;
                filter1Outcome.Append(bit == 1 ? "1" : "0");
                isArmed = TailMatchesAnyF2(filter1Outcome.ToString());
                DiagLog(isArmed ? "[F2 MATCH] armed=true" : "[F2] armed=false");
            }
            if (waitShortOutcome)
            {
                waitShortOutcome = false;
                filter1Outcome.Append(bit == 0 ? "1" : "0");
                isArmed = TailMatchesAnyF2(filter1Outcome.ToString());
                DiagLog(isArmed ? "[F2 MATCH] armed=true" : "[F2] armed=false");
            }
            if (filter1Outcome.Length > 2048) filter1Outcome.Remove(0, filter1Outcome.Length - 2048);

            // ── L3: collect ARMED (L2) would-be-trade outcomes -> feeds F3 (win-encoded) ─
            if (waitLongOutcome2)
            {
                waitLongOutcome2 = false;
                filter2Outcome.Append(bit == 1 ? "1" : "0");   // long L2 trade wins on next GREEN
                isArmed3 = TailMatchesAnyF3(filter2Outcome.ToString());
                DiagLog(isArmed3 ? "[F3 MATCH] armed3=true" : "[F3] armed3=false");
            }
            if (waitShortOutcome2)
            {
                waitShortOutcome2 = false;
                filter2Outcome.Append(bit == 0 ? "1" : "0");   // short L2 trade wins on next RED
                isArmed3 = TailMatchesAnyF3(filter2Outcome.ToString());
                DiagLog(isArmed3 ? "[F3 MATCH] armed3=true" : "[F3] armed3=false");
            }
            if (filter2Outcome.Length > 2048) filter2Outcome.Remove(0, filter2Outcome.Length - 2048);

            // ── append bit to both strings ───────────────────────────────────
            longStr.Append(bit == 1 ? "1" : "0");
            shortStr.Append(bit == 1 ? "0" : "1");
            if (longStr.Length  > 2048) longStr.Remove(0, longStr.Length - 2048);
            if (shortStr.Length > 2048) shortStr.Remove(0, shortStr.Length - 2048);

            DiagLog(string.Format("[BRICK #{0}] Close={1:F2} Prev={2:F2} bit={3}({4}) | longTail={5} shortTail={6}",
                barCount, Close[0], Close[1], bit, bit == 1 ? "GREEN/up" : "RED/down",
                TailOf(longStr, 12), TailOf(shortStr, 12)));

            // outcome bit-strings (win=1/loss=0, next-brick "would-trade" proxy) for eyeballing.
            // f1out = every F1 transit; f2out = F2-armed transits only (sparser subsequence).
            DiagLog(string.Format("[OUT   #{0}] f1out={1} f2out={2}",
                barCount, TailOf(filter1Outcome, 24), TailOf(filter2Outcome, 24)));

            // ── test F1 against BOTH books ───────────────────────────────────
            bool longMatch  = TailMatchesAny(longStr.ToString());
            bool shortMatch = TailMatchesAny(shortStr.ToString());

            // observation arm (collect next-brick outcome regardless of trading)
            if (longMatch)  waitLongOutcome  = true;
            if (shortMatch) waitShortOutcome = true;

            // ── fire (serialized): LONG first, then SHORT if still free ───────
            bool busy = hasOpenPosition() || entryInFlight || awaitingClose;

            if (longMatch)
            {
                bool l2long = isArmed;                  // Layer-2 would-be LONG trade (F1 while F2-armed)
                if (l2long) waitLongOutcome2 = true;    // record its outcome into filter2Outcome next brick (always, for F3 learning)
                if (l2long && isArmed3 && !busy && EnableRealOrder) { TryOpenRealTrade(+1); busy = true; }
                else DiagLog("[LONG SIGNAL] " + (!isArmed ? "not armed (F2)" : !isArmed3 ? "L2 obs only (F3 not armed)" : busy ? "skipped (busy)" : "obs only (real orders off)"));
            }
            if (shortMatch)
            {
                bool l2short = isArmed;                 // Layer-2 would-be SHORT trade
                if (l2short) waitShortOutcome2 = true;
                if (l2short && isArmed3 && !busy && EnableRealOrder) { TryOpenRealTrade(-1); }
                else DiagLog("[SHORT SIGNAL] " + (!isArmed ? "not armed (F2)" : !isArmed3 ? "L2 obs only (F3 not armed)" : busy ? "skipped (busy)" : "obs only (real orders off)"));
            }
        }

        // =====================================================================
        // TryOpenRealTrade(side) — direction-aware entry + flipped bracket
        // =====================================================================
        private void TryOpenRealTrade(int side)
        {
            if (EnableTradingHours && !WithinTradingHours())
            { DiagLog("[OUTSIDE HOURS] suppressed"); return; }
            if (EnableMarginCutoff && marginActive)
            { DiagLog("[MARGIN CUTOFF] entry blocked (early EOD before overnight-margin snapshot)"); return; }

            currentQty = CalcQty();
            if (currentQty <= 0) { DiagLog("[QTY SKIP] qty rule returned 0"); return; }

            double refPrice = (side > 0) ? GetCurrentAsk() : GetCurrentBid();
            if (refPrice <= 0) { DiagLog("[TRADE ABORT] no valid price"); return; }

            awaitingClose = true;
            entryInFlight = true;
            tradeSide     = side;

            double brickClose = Close[0];
            double stopPrice, targetPrice;
            if (side > 0)   // LONG: stop below, target above
            {
                stopPrice   = Instrument.MasterInstrument.RoundToTickSize(brickClose - StopLossPoints);
                targetPrice = Instrument.MasterInstrument.RoundToTickSize(brickClose + ProfitTargetPoints);
            }
            else            // SHORT: stop above, target below
            {
                stopPrice   = Instrument.MasterInstrument.RoundToTickSize(brickClose + StopLossPoints);
                targetPrice = Instrument.MasterInstrument.RoundToTickSize(brickClose - ProfitTargetPoints);
            }

            string sig = (side > 0) ? ENTRY_LONG : ENTRY_SHORT;
            SetStopLoss(sig, CalculationMode.Price, stopPrice, false);
            SetProfitTarget(sig, CalculationMode.Price, targetPrice);

            try
            {
                if (side > 0)
                {
                    if (UseMarketEntry) EnterLong(currentQty, sig);
                    else EnterLongLimit(0, true, currentQty,
                        Instrument.MasterInstrument.RoundToTickSize(refPrice - LimitOffsetPoints), sig);
                }
                else
                {
                    if (UseMarketEntry) EnterShort(currentQty, sig);
                    else EnterShortLimit(0, true, currentQty,
                        Instrument.MasterInstrument.RoundToTickSize(refPrice + LimitOffsetPoints), sig);
                }
                DiagLog(string.Format("MONEY TRADE #{0} {1} qty={2} brickClose={3:F2} stop={4:F2} target={5:F2} | real={6}",
                    barCount, side > 0 ? "LONG" : "SHORT", currentQty, brickClose, stopPrice, targetPrice,
                    TailOf(realTradeOutcome, 12)));
            }
            catch (Exception ex)
            {
                DiagLog("TryOpenRealTrade error: " + ex.Message);
                awaitingClose = false; entryInFlight = false; tradeSide = 0;
            }
        }

        // =====================================================================
        // OnExecutionUpdate — entry fill tracking + bracket close -> W/L
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            string oName = execution.Order.Name ?? "";
            bool isEntry = (oName == ENTRY_LONG || oName == ENTRY_SHORT);

            if (isEntry)
            {
                entryFillPrice = price;
                entryFillQty  += quantity;
                if (execution.Order.OrderState == OrderState.Filled)
                    entryInFlight = false;
                return;
            }

            // an exit fill (stop/target/flatten) -> once flat, record the outcome
            if (Position.MarketPosition == MarketPosition.Flat && awaitingClose)
            {
                // win/loss by side: long wins if exit price > entry; short wins if exit < entry
                bool win = (tradeSide > 0) ? (price > entryFillPrice) : (price < entryFillPrice);
                RecordTradeOutcome(win, price);
            }
        }

        private void RecordTradeOutcome(bool win, double exitPrice)
        {
            awaitingClose = false;
            entryInFlight = false;
            int side = tradeSide;
            tradeSide = 0;

            string bitStr = win ? "1" : "0";
            realTradeOutcome.Append(bitStr);
            sessionRealOutcome.Append(bitStr);
            if (realTradeOutcome.Length > 2048) realTradeOutcome.Remove(0, realTradeOutcome.Length - 2048);

            realLossesInARow = win ? 0 : (realLossesInARow + 1);
            entryFillQty = 0;

            DiagLog(string.Format("[TRADE CLOSED] side={0} {1} exit={2:F2} entry={3:F2} | real={4} | lossRow={5}",
                side > 0 ? "LONG" : "SHORT", win ? "WIN" : "LOSS", exitPrice, entryFillPrice,
                TailOf(realTradeOutcome, 12), realLossesInARow));

            // exit-bit / trade-outcome halt (merged)
            if (EnableTradeOutcomeExit && TailMatches(realTradeOutcome.ToString(), TradeOutcomeExitPattern))
                BeginShutdown("trade-outcome exit '" + TradeOutcomeExitPattern + "' matched");
        }

        // =====================================================================
        // OnOrderUpdate — ORPHAN GUARD
        // Managed mode auto-submits the stop/target when the entry fills, but a
        // fast spike (fill far from brickClose) or an OCO/partial-fill cascade can
        // get a protective leg REJECTED. RealtimeErrorHandling=IgnoreRejects means
        // NT will NOT flatten on that reject -> the position is left NAKED. This
        // strategy never MODIFIES a bracket, so a Stop-loss/Profit-target reject can
        // only mean "the bracket failed to attach" -> the position has no protection.
        // Rule: any protective reject -> flatten immediately at market and halt.
        // (An entry reject with nothing filled just releases the serialization lock
        //  so the strategy can't freeze; a partial-filled entry that rejects is
        //  treated as a naked position and flattened too.)
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time,
            ErrorCode error, string comment)
        {
            if (order == null || orderState != OrderState.Rejected) return;

            string nm = order.Name ?? "";
            bool isProtective = (nm == "Stop loss" || nm == "Profit target");
            bool isEntry      = (nm == ENTRY_LONG || nm == ENTRY_SHORT);

            if (isProtective)
            {
                DiagLog(string.Format("[ORPHAN GUARD] protective order '{0}' REJECTED ({1}) -> "
                    + "position is unprotected, flattening at market now.", nm, error));
                BeginShutdown("protective order rejected (orphan guard)");
                return;
            }

            if (isEntry)
            {
                if (hasOpenPosition())
                {
                    DiagLog(string.Format("[ORPHAN GUARD] entry '{0}' REJECTED ({1}) but a partial "
                        + "position exists -> flattening at market now.", nm, error));
                    BeginShutdown("entry rejected with open position (orphan guard)");
                }
                else
                {
                    DiagLog(string.Format("[ORPHAN GUARD] entry '{0}' REJECTED ({1}), nothing filled "
                        + "-> clearing in-flight state so the strategy does not freeze.", nm, error));
                    awaitingClose = false; entryInFlight = false; tradeSide = 0;
                }
            }
        }

        // =====================================================================
        // Qty rule (merged sessionRealOutcome) — longest-tail match wins
        // =====================================================================
        private int CalcQty()
        {
            int mult = 1;
            if (EnableQtyIncrement && qtyTable != null)
            {
                string s = sessionRealOutcome.ToString();
                int bestLen = -1;
                foreach (var row in qtyTable)
                {
                    if (row.pattern.Length > s.Length) continue;
                    if (s.EndsWith(row.pattern) && row.pattern.Length > bestLen)
                    { bestLen = row.pattern.Length; mult = row.multiplier; }
                }
            }
            int q = Math.Max(0, BaseQuantity * mult);
            if (q != BaseQuantity)
                DiagLog(string.Format("[QTY] sessionReal={0} -> mult applied -> qty={1}", sessionRealOutcome.ToString(), q));
            return q;
        }

        private void ParseQtyRule()
        {
            var list = new System.Collections.Generic.List<(string, int)>();
            string src = QtyRuleText ?? "";
            int i = 0;
            while (i < src.Length)
            {
                int q1 = src.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = src.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string pat = src.Substring(q1 + 1, q2 - q1 - 1).Trim();
                int colon = src.IndexOf(':', q2);
                if (colon < 0) break;
                int j = colon + 1;
                while (j < src.Length && !char.IsDigit(src[j]) && src[j] != '-') j++;
                int k = j;
                while (k < src.Length && (char.IsDigit(src[k]) || src[k] == '-')) k++;
                int mult;
                if (j < k && int.TryParse(src.Substring(j, k - j), out mult)
                    && pat.Length > 0 && pat.Trim('0', '1').Length == 0)
                    list.Add((pat, mult));
                i = (k > i) ? k : i + 1;
            }
            if (list.Count > 0) qtyTable = list.ToArray();
            DiagLog("[QTY RULE] parsed " + list.Count + " rows from '" + QtyRuleText + "'");
        }

        // =====================================================================
        // F1 parse + wildcard matcher (?=1+ ones, *=1+ zeros)
        // =====================================================================
        private void ParseFilter1Patterns()
        {
            filter1Patterns.Clear();
            if (!string.IsNullOrEmpty(Filter1Pattern))
                foreach (string tok in Filter1Pattern.Split(','))
                {
                    string t = (tok ?? "").Trim();
                    if (t.Length == 0) continue;
                    bool ok = true;
                    foreach (char c in t) if (c != '0' && c != '1' && c != '*' && c != '?') { ok = false; break; }
                    if (ok) filter1Patterns.Add(t);
                }
            DiagLog("[F1] active=[" + string.Join(",", filter1Patterns) + "]");
        }

        private bool TailMatchesAny(string text)
        {
            for (int i = 0; i < filter1Patterns.Count; i++)
                if (TailMatches(text, filter1Patterns[i])) return true;
            return false;
        }

        private void ParseFilter2Patterns()
        {
            filter2Patterns.Clear();
            if (!string.IsNullOrEmpty(Filter2Pattern))
                foreach (string tok in Filter2Pattern.Split(','))
                {
                    string t = (tok ?? "").Trim();
                    if (t.Length == 0) continue;
                    bool ok = true;
                    foreach (char c in t) if (c != '0' && c != '1' && c != '*' && c != '?') { ok = false; break; }
                    if (ok) filter2Patterns.Add(t);
                }
            DiagLog("[F2] active=[" + string.Join(",", filter2Patterns) + "]");
        }

        private bool TailMatchesAnyF2(string text)
        {
            for (int i = 0; i < filter2Patterns.Count; i++)
                if (TailMatches(text, filter2Patterns[i])) return true;
            return false;
        }

        private void ParseFilter3Patterns()
        {
            filter3Patterns.Clear();
            if (!string.IsNullOrEmpty(Filter3Pattern))
                foreach (string tok in Filter3Pattern.Split(','))
                {
                    string t = (tok ?? "").Trim();
                    if (t.Length == 0) continue;
                    bool ok = true;
                    foreach (char c in t) if (c != '0' && c != '1' && c != '*' && c != '?') { ok = false; break; }
                    if (ok) filter3Patterns.Add(t);
                }
            DiagLog("[F3] active=[" + string.Join(",", filter3Patterns) + "]");
        }

        private bool TailMatchesAnyF3(string text)
        {
            for (int i = 0; i < filter3Patterns.Count; i++)
                if (TailMatches(text, filter3Patterns[i])) return true;
            return false;
        }

        private static bool HasWild(string p)
        { for (int i = 0; i < p.Length; i++) if (p[i] == '*' || p[i] == '?') return true; return false; }

        private static bool TailMatches(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(text)) return false;
            if (!HasWild(pattern)) return text.Length >= pattern.Length && text.EndsWith(pattern);
            for (int start = text.Length - 1; start >= 0; start--)
                if (MatchHere(text, start, pattern, 0)) return true;
            return false;
        }

        private static bool MatchHere(string text, int ti, string pattern, int pi)
        {
            while (pi < pattern.Length)
            {
                char pc = pattern[pi];
                if (pc == '*' || pc == '?')
                {
                    char want = (pc == '*') ? '0' : '1';
                    if (ti >= text.Length || text[ti] != want) return false;
                    ti++;
                    int maxC = ti;
                    while (maxC < text.Length && text[maxC] == want) maxC++;
                    for (int c = maxC; c >= ti; c--) if (MatchHere(text, c, pattern, pi + 1)) return true;
                    return false;
                }
                else { if (ti >= text.Length || text[ti] != pc) return false; ti++; pi++; }
            }
            return ti == text.Length;
        }

        // =====================================================================
        // helpers: price, position, hours, gap, shutdown, logging
        // =====================================================================
        private double GetCurrentAsk()
        { try { return GetCurrentAsk(0) > 0 ? GetCurrentAsk(0) : Close[0]; } catch { return Close[0]; } }
        private double GetCurrentBid()
        { try { return GetCurrentBid(0) > 0 ? GetCurrentBid(0) : Close[0]; } catch { return Close[0]; } }

        private bool hasOpenPosition()
        { return Position != null && Position.MarketPosition != MarketPosition.Flat; }

        private bool WithinTradingHours()
        {
            DateTime t = EasternNow();   // New York time
            int cur = t.Hour * 60 + t.Minute;
            int s = TradingStartHour * 60 + TradingStartMinute;
            int e = TradingEndHour * 60 + TradingEndMinute;
            return (s <= e) ? (cur >= s && cur <= e) : (cur >= s || cur <= e);
        }

        private void FreshStart(string reason, bool seed)
        {
            seedPending = seed;
            longStr.Clear(); shortStr.Clear(); filter1Outcome.Clear(); filter2Outcome.Clear();
            waitLongOutcome = false; waitShortOutcome = false; isArmed = false;
            waitLongOutcome2 = false; waitShortOutcome2 = false; isArmed3 = false;
            prevBarBit = -1;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            try
            {
                if (!Directory.Exists(LogFolder)) Directory.CreateDirectory(LogFolder);
                activeLogFilePath = Path.Combine(LogFolder, LogBaseName + "_" + stamp + ".csv");
            }
            catch { activeLogFilePath = null; }
            DiagLog("[FRESH START] " + reason + " | seed=" + seed + " | new log=" + activeLogFilePath
                + " | pipeline EMPTY, will arm naturally (no real trades until a transit fires).");
            DiagLog(string.Format("{0} ready (MERGED L3). EnableRealOrder={1}, F1=[{2}], F2=[{3}], F3=[{11}], Stop={4}pt, Target={5}pt, "
                + "ExitBit={6}({7}), MaxLossRow={8}, Seed={9}, RestartOnNewSession={10} | "
                + "Hours={12}({13:00}:{14:00}-{15:00}:{16:00} NY) | MarginCutoff={17} flat@{18:00}:{19:00} NY "
                + "(cutoff {20:00}:{21:00} lead {22}m)",
                Name, EnableRealOrder, Filter1Pattern, Filter2Pattern, StopLossPoints, ProfitTargetPoints,
                EnableTradeOutcomeExit, TradeOutcomeExitPattern, MaxRealLossInARow, SeedPendingBarOnStart, RestartOnNewSession, Filter3Pattern,
                EnableTradingHours ? "ON" : "OFF", TradingStartHour, TradingStartMinute, TradingEndHour, TradingEndMinute,
                EnableMarginCutoff ? "ON" : "OFF",
                (MarginCutoffHour * 60 + MarginCutoffMinute - MarginCutoffLeadMin) / 60,
                (MarginCutoffHour * 60 + MarginCutoffMinute - MarginCutoffLeadMin) % 60,
                MarginCutoffHour, MarginCutoffMinute, MarginCutoffLeadMin));
            DiagLog(string.Format("[CLOCK] Strategy follows NEW YORK time (Wall St. bell). New York now={0:HH:mm:ss}, "
                + "this platform/log clock={1:HH:mm:ss}. All hour params are New York time; log timestamps are platform time.",
                EasternNow(), DateTime.Now));
        }

        private void BeginShutdown(string reason)
        {
            if (pendingFlatten) return;
            pendingFlatten = true;
            pendingReason  = reason;
            DiagLog("[SHUTDOWN] " + reason + " -> flatten + disable");
            DoFlatten();
        }

        // =====================================================================
        // NEW YORK (ET) CLOCK. All hour parameters (trading hours + margin cutoff) are
        // NEW YORK time. We derive it from UTC so DST is automatic and the platform/chart
        // timezone and the user's location are irrelevant - type New York numbers, always.
        // (Live/realtime uses the true current time. NOTE: Playback/Replay uses the real
        //  wall clock, not the replayed session time.) Log timestamps stay in platform time;
        // the [CLOCK] startup line prints both so the offset is obvious.
        // =====================================================================
        private TimeZoneInfo _etZone = null;
        private bool _etWarned = false;
        private TimeZoneInfo EasternZone()
        {
            if (_etZone != null) return _etZone;
            try { _etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch
            {
                try { _etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                catch { _etZone = null; }
            }
            return _etZone;
        }
        private DateTime EasternNow()
        {
            TimeZoneInfo z = EasternZone();
            if (z == null)
            {
                if (!_etWarned) { DiagLog("[CLOCK] WARNING: New York time zone not found; using platform local time - hour params may be wrong."); _etWarned = true; }
                return DateTime.Now;
            }
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, z);
        }

        // =====================================================================
        // Margin cutoff - an EARLY end-of-day, before the broker's overnight-margin
        // snapshot (Tradovate/NinjaTrader: 16:45 ET = 3:45 CT). Driven by the 1-min
        // clock series so it fires on time even if Renko bricks are sparse. At the
        // cutoff it flattens any open position and refuses new entries through the
        // real session close (~17:00 ET), releasing at the 18:00 ET reopen. Flatten
        // ONLY - it does not disable the strategy; it resumes next session. Turn off
        // via EnableMarginCutoff if the account is well-funded for overnight margin.
        // =====================================================================
        private void CheckMarginCutoff()
        {
            if (!EnableMarginCutoff) { marginActive = false; marginLogged = false; return; }
            if (State != State.Realtime) return;
            if (CurrentBars.Length < 2 || CurrentBars[1] < 0) return;

            DateTime t = EasternNow();                      // true New York time (UTC-derived, DST-safe)
            int nowMin     = t.Hour * 60 + t.Minute;
            int flattenMin = MarginCutoffHour * 60 + MarginCutoffMinute - MarginCutoffLeadMin;
            // Block window is RELATIVE to the flatten time (not a fixed 18:00), so any cutoff
            // works - including evening test times. ~80 min covers flatten -> ~market reopen
            // for the default 16:40 cutoff (16:40 -> 18:00), keeping you flat through the 16:45
            // snapshot and the 17:00-18:00 break.
            const int blockMinutes = 80;
            bool active    = (nowMin >= flattenMin && nowMin < flattenMin + blockMinutes);

            if (active && !marginLogged)
            {
                DiagLog(string.Format("[MARGIN CUTOFF] window active (New York now {0:HH:mm}) - flatten @ {1:00}:{2:00} "
                    + "New York: early EOD, no new entries (before {3:00}:{4:00} New York overnight-margin snapshot).",
                    t, flattenMin / 60, flattenMin % 60, MarginCutoffHour, MarginCutoffMinute));
                marginLogged = true;
            }
            if (!active) marginLogged = false;
            marginActive = active;

            if (marginActive && Position.MarketPosition != MarketPosition.Flat)
                MarginFlatten();
        }

        private void MarginFlatten()
        {
            try
            {
                // barsInProgressIndex=0 forces the exit onto the PRIMARY series even when this
                // is called from the 1-min clock (BarsInProgress==1), per NinjaTrader's rule that
                // same-instrument orders must target the first bars context.
                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort(0, Math.Abs(Position.Quantity), "ML2_MarginFlat", ENTRY_SHORT);
                else if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong(0, Math.Abs(Position.Quantity), "ML2_MarginFlat", ENTRY_LONG);
            }
            catch (Exception ex) { DiagLog("[MARGIN CUTOFF] flatten error: " + ex.Message); }
        }
        private void DoFlatten()
        {
            try
            {
                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort(Math.Abs(Position.Quantity), "ML2_Flatten", ENTRY_SHORT);
                else if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong(Math.Abs(Position.Quantity), "ML2_Flatten", ENTRY_LONG);
            }
            catch (Exception ex) { DiagLog("[SHUTDOWN] flatten error: " + ex.Message); }

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                try { DiagLog("[SHUTDOWN] flat -> disabling."); } catch { }
                pendingFlatten = false;
                try { SetState(State.Terminated); } catch { }
            }
        }

        private string TailOf(StringBuilder sb, int n)
        {
            int len = sb.Length;
            if (len <= n) return sb.ToString();
            return sb.ToString(len - n, n);
        }

        private void DiagLog(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg;
            try
            {
                if (activeLogFilePath != null)
                    File.AppendAllText(activeLogFilePath, line + Environment.NewLine);
            }
            catch { }
            Print(line);
        }

        #region Properties
        [NinjaScriptProperty] [Display(Name="Enable Real Order", Order=1, GroupName="1. Core")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty] [Display(Name="F1 Pattern (comma OR; ?=1+ ones *=1+ zeros)", Order=2, GroupName="1. Core")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty] [Display(Name="F2 Pattern (comma OR; on MERGED outcome; arms next transit)", Order=8, GroupName="1. Core")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty] [Display(Name="F3 Pattern (comma OR; on ARMED-trade outcome; gates real order)", Order=9, GroupName="1. Core")]
        public string Filter3Pattern { get; set; }

        [NinjaScriptProperty] [Range(1,int.MaxValue)] [Display(Name="Base Quantity", Order=3, GroupName="1. Core")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty] [Display(Name="Use Market Entry", Order=4, GroupName="1. Core")]
        public bool UseMarketEntry { get; set; }

        [NinjaScriptProperty] [Range(0,double.MaxValue)] [Display(Name="Limit Offset Points", Order=5, GroupName="1. Core")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty] [Range(0.25,double.MaxValue)] [Display(Name="Stop Loss Points", Order=6, GroupName="1. Core")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty] [Range(0.25,double.MaxValue)] [Display(Name="Profit Target Points", Order=7, GroupName="1. Core")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty] [Display(Name="Enable Qty Increment", Order=1, GroupName="2. Qty Rule")]
        public bool EnableQtyIncrement { get; set; }

        [NinjaScriptProperty] [Display(Name="Qty Rule Text", Order=2, GroupName="2. Qty Rule")]
        public string QtyRuleText { get; set; }

        [NinjaScriptProperty] [Range(1,int.MaxValue)] [Display(Name="Max Real Loss In A Row (breaker)", Order=3, GroupName="2. Qty Rule")]
        public int MaxRealLossInARow { get; set; }

        [NinjaScriptProperty] [Display(Name="Enable Trade-Outcome Exit", Order=1, GroupName="3. Exit")]
        public bool EnableTradeOutcomeExit { get; set; }

        [NinjaScriptProperty] [Display(Name="Trade-Outcome Exit Pattern (1=win-and-quit)", Order=2, GroupName="3. Exit")]
        public string TradeOutcomeExitPattern { get; set; }

        [NinjaScriptProperty] [Display(Name="Enable Trading Hours", Order=1, GroupName="4. Hours")]
        public bool EnableTradingHours { get; set; }

        [NinjaScriptProperty] [Range(0,23)] [Display(Name="Start Hour (New York time)", Order=2, GroupName="4. Hours")]
        public int TradingStartHour { get; set; }

        [NinjaScriptProperty] [Range(0,59)] [Display(Name="Start Minute (New York)", Order=3, GroupName="4. Hours")]
        public int TradingStartMinute { get; set; }

        [NinjaScriptProperty] [Range(0,23)] [Display(Name="End Hour (New York time)", Order=4, GroupName="4. Hours")]
        public int TradingEndHour { get; set; }

        [NinjaScriptProperty] [Range(0,59)] [Display(Name="End Minute (New York)", Order=5, GroupName="4. Hours")]
        public int TradingEndMinute { get; set; }

        [NinjaScriptProperty] [Range(1,int.MaxValue)] [Display(Name="Strategy Life Minutes", Order=1, GroupName="5. Session")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty] [Range(1,int.MaxValue)] [Display(Name="Max Total Bar Count", Order=2, GroupName="5. Session")]
        public int MaxTotalBarCount { get; set; }

        [NinjaScriptProperty] [Display(Name="Seed forming brick on start", Order=3, GroupName="5. Session")]
        public bool SeedPendingBarOnStart { get; set; }

        [NinjaScriptProperty] [Display(Name="Restart clean on new session (ETH template keeps 5AM warm-up; RTH wipes at 09:30)", Order=6, GroupName="5. Session")]
        public bool RestartOnNewSession { get; set; }

        [NinjaScriptProperty] [Display(Name="Log Folder", Order=4, GroupName="5. Session")]
        public string LogFolder { get; set; }

        [NinjaScriptProperty] [Display(Name="Log Base Name", Order=5, GroupName="5. Session")]
        public string LogBaseName { get; set; }

        [NinjaScriptProperty] [Display(Name="Enable margin cutoff (early EOD before overnight-margin)", Order=1, GroupName="6. Margin")]
        public bool EnableMarginCutoff { get; set; }

        [NinjaScriptProperty] [Range(0,23)] [Display(Name="Broker margin cutoff Hour (New York time)", Order=2, GroupName="6. Margin")]
        public int MarginCutoffHour { get; set; }

        [NinjaScriptProperty] [Range(0,59)] [Display(Name="Broker margin cutoff Minute (New York)", Order=3, GroupName="6. Margin")]
        public int MarginCutoffMinute { get; set; }

        [NinjaScriptProperty] [Range(0,120)] [Display(Name="Flatten lead minutes (before cutoff)", Order=4, GroupName="6. Margin")]
        public int MarginCutoffLeadMin { get; set; }
        #endregion
    }
}
