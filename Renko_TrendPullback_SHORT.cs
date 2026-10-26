#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// Renko_TrendPullback_SHORT  v1.11  (2026-10-23)  -- mirror of Renko_TrendPullback_LONG v1.11
//   v1.10: (1) a stop resize rejected while the target is partly filled -> close the rest (cancel brackets first),
//          because the oversized stop can over-fill; (2) an over-fill is caught at the fill itself, not at the next
//          brick; (3) a close is sized only when NinjaTrader's position, the latest position update and our own fill
//          count agree (after a reconnect they can differ); if they disagree on direction, nothing is sent; (4) a late entry fill (arriving after the
//          cancel) is counted as entry, not as an exit loss.
//   v1.11: (1) after a reconnect/lag, if NinjaTrader's Position and our own fill count agree, use them (v1.10
//          used the smallest of three and missed 1 contract with no stop); (2) the naked guard runs every second,
//          not only on brick closes; (3) when only SOME contracts have no stop, a rescue closes just those.
//          (4) a mixed trade (part target, part stop) scores 1/0 by its real P&L sign.
//   v1.9: a rescue/shutdown close is never sized inside an order or fill callback (only from the 1-second
//         heartbeat or a brick), and live orders' remaining quantity is counted from the fills the strategy has
//         seen itself. Fixes the 'closing LONG 1 of N' each pass -> short 1 seen in Sim.
//   v1.8: a rescue/shutdown never sends a second close while its own close order is still working (max 5 s),
//         so a partly-filled close can no longer be topped up by mistake.
//         Review fixes: margin-cutoff flatten now runs through a close-all rescue (cancel brackets, one close at a
//         time) instead of a full-size exit on every bar; a rescue never cancels our own close orders; after a
//         rescue hands a trade back, a later stop reject starts a new rescue; trade P&L uses the average of the
//         ACTUAL exit fills, and a trade closed mostly by our own close orders is scored by its P&L.
//   v1.7: a RESCUE no longer cancels the stop/target brackets: it closes only contracts with no live stop and
//         hands protected contracts back to their own stop/target (Sim over-sold when it closed contracts whose
//         stops NinjaTrader never cancelled). A full shutdown still closes everything. One trade counts once toward
//         the daily rescue limit. The 'cancel the rest of the entry at the stop' check applies to LIMIT entries only.
//   v1.6: (1) no real order for an OLD brick (start-up backlog: Sim traded a 6-minute-old brick 80 pt away);
//         (2) skip a setup when price is already past the target or more than 1 brick from the setup close;
//         (3) a close is never sized within 0.75 s of a fill (Position lag caused short 1 / short 3 overshoots).
//   v1.5: FAST-MARKET handling. (A) skip an entry that is already at/through the stop; (B) cancel the rest
//         of a filling entry once price trades at the stop; (C) a stop rejected because the market is already
//         past it = close the position and KEEP RUNNING (other rejects still shut down); a daily limit
//         'Max fast-market rescues per day' (default 3) turns the strategy off; a rescue that cannot get flat
//         becomes a full shutdown; (D) wait up to 4 s for own cancels before the market close.
//   v1.4: a virtual WIN now needs a trade 1+ tick PAST the target (a brick closing exactly AT the target
//         no longer counts; the trade stays open, log event TARGET_TOUCH_WAIT). Matches the real target order.
//
// ============================================================================
// WHAT THIS IS
// ============================================================================
// SHORT book of the trend-pullback module. One file does Layer 1, 2 and 3:
//     Filter 2 blank                -> Layer 1
//     Filter 2 set,  Filter 3 blank -> Layer 2
//     Filter 2 set,  Filter 3 set   -> Layer 3
//     Filter 2 blank, Filter 3 set  -> INVALID: the strategy refuses to start.
//
// RAW BITS (SHORT encoding, same as the other SHORT files): each closed brick -> 1 bit
// from Close[0] vs Close[1]:  RED (price fell) = 1,  GREEN (price rose) = 0.
// '1' always means "good for this book".
//
// THE SETUP (research, 12 weeks Jul 6 - Oct 15 2026, 80-tick bricks, in-sample):
//   Trend:   brick EMA 9 < EMA 20 < EMA 30   (a downtrend, EMAs of the Renko closes)
//   F1:      "110" = red, red, then green (a 1-brick bounce inside a downtrend)
//   Entry:   SELL at MARKET when the green brick closes (limit entry is available, default OFF)
//   Bracket: stop 1 brick ABOVE the green brick's close, target 3 bricks BELOW it
//            (3 bricks = a distance of 3 x brick size, e.g. 60 pt on an 80-tick chart; the
//             trade ends when price touches either level, however the bricks form on the way)
//   F2:      "0" = skip the setup right after a WIN (meta-label)
//   Result:  ~33 trades/week, ~24% wins, ~+3.3 pt/trade after costs. Expect losing streaks of
//            ~18-25 trades: that is the normal shape of this edge.
//
// VIRTUAL TRADE TRACKER: every F1 match that passes the EMA gate is a SETUP with fixed levels
// anchored to the setup brick close C:  stop = C + StopBricks x brick,  target = C - TargetBricks x brick.
//   a later brick closes at/below target -> '1' (win)
//   a later brick closes at/above stop   -> '0' (loss)
//   LIMIT entry ON and price never traded through the limit before the next brick closed -> 'N'
// One virtual position at a time; the strings are identical with real orders ON or OFF.
// "Ignore N for matching" (default ON) removes N before F2/F3 are matched.
//
// LIMIT ENTRY (if enabled): sell limit at (bid + offset); bracket anchored to the setup brick close;
// the rest is cancelled when the next brick closes; partial fills keep their own bracket;
// PAST-STOP GUARD: a fill that arrives with the ask already at/above the stop is closed at once.
//
// SAFETY ENGINE (kept from the validated Transit files): immediate shutdown flatten + heartbeat,
// two-look naked-position guard, modify-reject + orphan guards, brick-gap detection, account-busy
// check (one account shared with the LONG book: one book blocks the other while it has a
// position/order), margin cutoff, session-gated seed back-fill, majority-quantity scoring.
//
// DAILY RESET: strings reset at the "Daily reset" time (default 15:00 Pacific). EMAs do not reset.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Renko_TrendPullback_SHORT : Strategy
    {
        // ── names ────────────────────────────────────────────────────────────
        private const string ENTRY_SIGNAL  = "TPS_Entry";
        private const string FLATTEN_NAME  = "TPS_Flatten";
        private const string MARGIN_NAME   = "TPS_MarginFlat";
        private const string PASTSTOP_NAME = "TPS_PastStop";

        // ── lifecycle ────────────────────────────────────────────────────────
        private DateTime strategyStartUtc;
        private bool     lifeStarted  = false;
        private bool     disabledSelf = false;
        private int      barCount     = 0;
        private bool     configOK     = true;

        // ── brick geometry ───────────────────────────────────────────────────
        private double brickPts = 0.0;

        // ── EMA trend gate ───────────────────────────────────────────────────
        private readonly List<int>  emaPeriods = new List<int>();
        private readonly List<char> emaOps     = new List<char>();   // between EMA i and i+1: '>' or '<'
        private readonly List<NinjaTrader.NinjaScript.Indicators.EMA> emaInd =
            new List<NinjaTrader.NinjaScript.Indicators.EMA>();

        // ── pipeline strings ─────────────────────────────────────────────────
        private readonly StringBuilder rawString        = new StringBuilder();
        private readonly StringBuilder filter1Outcome   = new StringBuilder();   // 1/0/N per recorded setup
        private readonly StringBuilder filter2Outcome   = new StringBuilder();   // results of setups taken while F2 armed
        private readonly StringBuilder realTradeOutcome = new StringBuilder();   // real money results (1/0)
        private readonly StringBuilder sessionRealOutcome = new StringBuilder(); // today's real results (qty rule / exit)
        private readonly StringBuilder brickEvents      = new StringBuilder();   // events of the current brick (log)

        private readonly List<string> filter1Patterns = new List<string>();
        private readonly List<string> filter2Patterns = new List<string>();
        private readonly List<string> filter3Patterns = new List<string>();
        private int  layer  = 1;
        private bool armed2 = false;
        private bool armed3 = false;

        // ── virtual trade ────────────────────────────────────────────────────
        private bool   vOpen = false;
        private double vC, vStop, vTarget, vLimit;
        private bool   vUsesLimit = false;
        private bool   vFilled    = false;
        private bool   vThrough   = false;   // v1.4: a trade happened 1+ tick PAST the target (real target would fill)
        private bool   vTouched   = false;   // v1.4: a brick closed AT the target, waiting for a trade past it
        private bool   vArmed2AtSetup = false;
        private bool   vIsMoney   = false;
        private int    vSetupBar  = -1;
        private string lastSetupInfo = "";
        private char   lastResolved  = ' ';

        // ── real order state ─────────────────────────────────────────────────
        private bool   entryInFlight      = false;
        private bool   awaitingClose      = false;
        private Order  workingEntryOrder  = null;
        private double entryFillPrice     = 0.0;
        private int    entryFillQty       = 0;
        private int    winQtyThisTrade    = 0;
        private int    lossQtyThisTrade   = 0;
        private int    realEntryBar       = -1;
        private bool   entryCancelSent    = false;
        private bool   pastStopExit       = false;
        private bool   rescueActive       = false;   // flatten-without-disable in progress
        private readonly Dictionary<Order, int> execFilledByOrder = new Dictionary<Order, int>();   // v1.9: fills seen in OnExecutionUpdate
        private bool   inOrderCallback    = false;   // v1.9: inside OnOrderUpdate/OnExecutionUpdate
        private int    lastPosSigned      = 0;       // v1.10: position from the latest OnPositionUpdate (+long/-short)
        private int    sizingQtyCap       = int.MaxValue;   // v1.10: max qty a rescue close may send this pass
        private bool   rescueCloseAll     = false;   // v1.8: this rescue closes EVERYTHING (margin cutoff), brackets included
        private double exitNotional       = 0.0;     // v1.8: sum of exit fill price x qty (real P&L)
        private int    exitQtySum         = 0;
        private int    flatQtySum         = 0;       // v1.8: exit contracts closed by our own close orders
        private int    cancelWaitPasses   = 0;
        private int    closeWaitPasses    = 0;   // v1.8: passes spent waiting for our own close order
        private DateTime lastTickTime     = DateTime.MinValue;   // v1.6: time stamp of the latest live trade (chart time zone)
        private DateTime lastExecUtc      = DateTime.MinValue;
        private int      ownNet           = 0;                   // v1.6: position counted from our OWN fills (+long / -short)
        private DateTime posMismatchUtc   = DateTime.MinValue;   // v1.6: last fill of ANY of this strategy's orders
        private int    fastRescuesToday   = 0;
        private bool   fastRescueThisTrade = false;   // v1.7: a trade's rescues count once toward the daily limit   // v1.5: fast-market rescues since the daily reset
        private string rescueReason       = "";
        private double lastExitPrice      = 0.0;
        private double tradeStopPrice     = 0.0;
        private double tradeTargetPrice   = 0.0;
        private double tradeLimitPrice    = 0.0;
        private int    currentQty         = 1;
        private int    realLossesInARow   = 0;

        // ── qty rule ─────────────────────────────────────────────────────────
        private (string pattern, int multiplier)[] qtyTable =
            new (string pattern, int multiplier)[] { ("00", 2), ("000", 3) };
        private const int QTY_MULT_CAP = 20;

        // ── day / session ────────────────────────────────────────────────────
        private int currentTradingDayKey = -1;
        private SessionIterator sessionIter = null;

        // ── shutdown / guards ────────────────────────────────────────────────
        private bool   pendingFlatten = false;
        private string pendingReason  = string.Empty;
        private int      flattenAttempts = 0;
        private DateTime lastFlattenUtc  = DateTime.MinValue;
        private bool     flattenGaveUp   = false;
        private bool     inFlatten       = false;
        private const int MaxFlattenAttempts = 8;
        private System.Timers.Timer flattenTimer = null;
        private DateTime entryFillUtc      = DateTime.MinValue;
        private DateTime nakedFirstSeenUtc = DateTime.MinValue;
        private const int NakedGraceSeconds   = 5;
        private const int NakedConfirmSeconds = 2;
        private bool     openModifyRejectLogged = false;
        private int      lastProcessedBar = -1;
        private bool     quietLog    = false;
        private bool     seedChecked = false;
        private bool     marginActive = false;
        private bool     marginLogged = false;
        private bool     pendingRewarm = false;
        private int      prevBarBit = -1;

        // ── logging ──────────────────────────────────────────────────────────
        private string activeLogFilePath = null;
        private bool   logWriteFailed = false;
        private const string LOG_HEADER =
            "timestamp,bar,event,close,bit,ema_values,trend_ok,setup_close,limit_price,stop_level,target_level,"
          + "virtual_result,qty,entry_price,exit_price,pnl,real_bit,filter1Outcome,filter2Outcome,realTradeOutcome";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Renko trend-pullback SHORT (Layer 1/2/3 in one file). F1 on bricks, gated by a brick-EMA "
                            + "trend filter; virtual trade tracker gives 1/0/N outcomes; optional limit entry; stop/target in bricks.";
                Name        = "Renko_TrendPullback_SHORT";

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                IsFillLimitOnTouch           = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.Infinite;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade          = 0;
                IsUnmanaged                  = false;

                // 1. Core
                EnableRealOrder      = false;   // observation first; flip to true when ready
                BaseQuantity         = 1;
                StopBricks           = 1;
                TargetBricks         = 3;
                // 2. Entry
                UseLimitEntry        = false;   // research: SHORT used MARKET entry
                LimitOffsetPoints    = 5;
                // 3. Filters
                Filter1Pattern       = "110";   // SHORT encoding: red, red, green
                EmaTrendGate         = "9 < 20 < 30";
                EmaOnPreviousBrick   = false;
                Filter2Pattern       = "0";     // skip the setup right after a win
                Filter3Pattern       = "";
                IgnoreNForMatching   = true;
                // 4. Seed
                SeedOnStart          = true;
                SeedBrickCount       = 1000;
                // 5. Hours (OFF = trade all hours)
                EnableTradingHours   = false;
                TradingStartHour     = 9;
                TradingStartMinute   = 30;
                TradingEndHour       = 12;
                TradingEndMinute     = 0;
                // 6. Quantity
                EnableQtyIncrement   = false;
                QtyRuleText          = "(\"00\":2),(\"000\":3)";
                // 7. Limits
                StrategyLifeMinutes  = 100000;
                MaxTotalBarCount     = 1000000;
                MaxRealLossInARow    = 30;
                EnableTradeOutcomeExit  = true;
                TradeOutcomeExitPattern = "111";
                RestoreTodayFromLog  = true;
                MaxFastRescuesPerDay = 3;
                MaxBrickAgeSeconds = 60;
                // 8. Day reset
                DailyResetHourPT     = 15;
                DailyResetMinutePT   = 0;
                // 9. Margin
                EnableMarginCutoff   = true;
                MarginCutoffHour     = 16;
                MarginCutoffMinute   = 35;
                MarginCutoffLeadMin  = 5;
                // 10. Logging / advanced
                LogFolder            = @"C:\temp";
                LogBaseName          = "Renko_TrendPullback_SHORT";
                EnableReconnectRewarm = false;
            }
            else if (State == State.Configure)
            {
                // 1-minute clock series: heartbeat for the margin cutoff when bricks are sparse.
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                if (BarsArray != null && BarsArray.Length > 0)
                    sessionIter = new SessionIterator(BarsArray[0]);
                string err = ParseEmaGate();
                if (err == null)
                {
                    emaInd.Clear();
                    foreach (int p in emaPeriods) emaInd.Add(EMA(Closes[0], p));
                }
                else configErrorText = err;
            }
            else if (State == State.Terminated)
            {
                StopFlattenTimer();
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    strategyStartUtc = DateTime.UtcNow;
                    lifeStarted = true;
                    if (sessionIter == null && BarsArray != null && BarsArray.Length > 0)
                        sessionIter = new SessionIterator(BarsArray[0]);
                    StartupLoad();
                }
            }
        }

        private string configErrorText = null;
        private ConnectionStatus lastOrderStatus = ConnectionStatus.Connected;
        private ConnectionStatus lastPriceStatus = ConnectionStatus.Connected;

        // =====================================================================
        // START-UP: log file, settings check, restore today's real results
        // =====================================================================
        private void StartupLoad()
        {
            activeLogFilePath = BuildNewLogFilePath();
            EnsureLogHeader(activeLogFilePath);
            DiagLog("[START] new log=" + activeLogFilePath);

            ParsePatterns(Filter1Pattern, filter1Patterns, false, "F1");
            ParsePatterns(Filter2Pattern, filter2Patterns, true,  "F2");
            ParsePatterns(Filter3Pattern, filter3Patterns, true,  "F3");
            ParseQtyRule();

            // ── settings check: refuse to start on a bad configuration ──
            var problems = new List<string>();
            if (configErrorText != null) problems.Add(configErrorText);
            // Read the PRIMARY series (BarsArray[0]) - NOT 'BarsPeriod', which can point at the hidden
            // 1-minute clock series at this moment (v1 bug: refused a real 80-tick Renko chart).
            string brickErr = DetectBrickSize();
            if (brickErr != null) problems.Add(brickErr);
            if (filter1Patterns.Count == 0) problems.Add("Filter 1 is blank or invalid");
            if (filter2Patterns.Count == 0 && filter3Patterns.Count > 0)
                problems.Add("Filter 3 is set but Filter 2 is blank (Layer 3 needs both)");
            if (!string.IsNullOrWhiteSpace(Filter2Pattern) && filter2Patterns.Count == 0)
                problems.Add("Filter 2 has text but no valid pattern");
            if (!string.IsNullOrWhiteSpace(Filter3Pattern) && filter3Patterns.Count == 0)
                problems.Add("Filter 3 has text but no valid pattern");
            if (UseLimitEntry && LimitOffsetPoints >= StopBricks * brickPts && brickPts > 0)
                problems.Add("Limit offset must be smaller than the stop distance (" + (StopBricks * brickPts) + " pt)");

            layer = filter2Patterns.Count == 0 ? 1 : (filter3Patterns.Count == 0 ? 2 : 3);

            if (problems.Count > 0)
            {
                configOK = false;
                DiagLog("[CONFIG ERROR] strategy will NOT trade: " + string.Join(" | ", problems));
                pendingReason = "configuration error: " + string.Join(" | ", problems);
                return;   // the strategy stops itself on its first brick (see OnBarUpdate)
            }

            currentTradingDayKey = CurrentTradingDayKey();
            if (RestoreTodayFromLog) RestoreTodaysRealResults();

            DiagLog(string.Format(
                "{0} ready. Layer={1} | EnableRealOrder={2} | brick={3}pt | stop={4} brick(s)={5}pt | target={6} brick(s)={7}pt | "
              + "entry={8} | F1=[{9}] | EMA gate=[{10}]{11} | F2=[{12}] | F3=[{13}] | ignoreN={14} | qtyRule={15} | "
              + "exitPattern={16} | maxLossRow={17} | dailyReset {18:00}:{19:00} PT | margin flat {20}",
                Name, layer, EnableRealOrder, brickPts, StopBricks, StopBricks * brickPts, TargetBricks, TargetBricks * brickPts,
                UseLimitEntry ? ("LIMIT bid+" + LimitOffsetPoints + "pt") : "MARKET",
                string.Join(",", filter1Patterns), EmaGateText(), EmaOnPreviousBrick ? " (checked on previous brick)" : "",
                string.Join(",", filter2Patterns), string.Join(",", filter3Patterns), IgnoreNForMatching,
                EnableQtyIncrement ? "ON" : "OFF",
                EnableTradeOutcomeExit ? ("'" + TradeOutcomeExitPattern + "'") : "OFF", MaxRealLossInARow,
                DailyResetHourPT, DailyResetMinutePT,
                EnableMarginCutoff ? string.Format("{0:00}:{1:00} NY",
                    (MarginCutoffHour * 60 + MarginCutoffMinute - MarginCutoffLeadMin) / 60,
                    (MarginCutoffHour * 60 + MarginCutoffMinute - MarginCutoffLeadMin) % 60) : "OFF"));
            if (EnableRealOrder)
                DiagLog("[!! CHECK PLATFORM SETTING !!] Real orders are ON. Review Tools -> Options -> Strategies -> "
                    + "'On connection loss'. A long disconnect can make NinjaTrader cancel this strategy's stop/target "
                    + "and leave a NAKED position that no code here can protect while the feed is down.");
            DiagLog(string.Format("[CLOCK] New York now={0:HH:mm:ss}, platform clock={1:HH:mm:ss}. Hour settings are New York "
                + "time except the daily reset (Pacific).", EasternNow(), DateTime.Now));
        }


        // Brick size from the primary series. Built-in Renko: Value (ticks) x TickSize.
        // Anything else (e.g. a custom Renko type): measure it from the chart's own closes and accept
        // only if they clearly sit on one brick grid.
        private string DetectBrickSize()
        {
            try
            {
                Bars b = BarsArray[0];
                BarsPeriod bp = b.BarsPeriod;
                if (bp != null && bp.BarsPeriodType == BarsPeriodType.Renko && bp.Value > 0)
                {
                    brickPts = bp.Value * TickSize;
                    DiagLog(string.Format("[BRICK] Renko {0} ticks = {1} pt (from the chart settings).", bp.Value, brickPts));
                    return null;
                }
                string typeTxt = bp == null ? "unknown" : (bp.BarsPeriodType + " value " + bp.Value);
                int n = b.Count;
                var diffs = new List<double>();
                for (int i = Math.Max(1, n - 400); i < n; i++)
                {
                    double d = Math.Abs(b.GetClose(i) - b.GetClose(i - 1));
                    if (d > TickSize * 0.5) diffs.Add(d);
                }
                if (diffs.Count < 30) return "the chart data series is not Renko (" + typeTxt + ") and there are too few bars to measure a brick size";
                double unit = diffs.Min();
                int onGrid = diffs.Count(d => Math.Abs(d / unit - Math.Round(d / unit)) < 0.01 && Math.Round(d / unit) <= 3);
                if (onGrid < diffs.Count * 0.98)
                    return "the chart data series does not look like Renko (" + typeTxt + "): closes are not on one brick grid";
                brickPts = Instrument.MasterInstrument.RoundToTickSize(unit);
                DiagLog(string.Format("[BRICK] WARNING: data series type reads as '{0}', not built-in Renko. Measured brick = {1} pt "
                    + "from {2} closes ({3} on the grid). Check this matches your chart!", typeTxt, brickPts, diffs.Count, onGrid));
                return null;
            }
            catch (Exception ex) { return "brick size could not be read: " + ex.Message; }
        }

        // =====================================================================
        // OnMarketData — watch trades for the virtual limit fill (trade-through)
        // =====================================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (State != State.Realtime || e == null) return;
            if (e.MarketDataType != MarketDataType.Last) return;
            lastTickTime = e.Time;   // same time zone as the bars' Time[]
            if (lifeStarted && configOK && (DateTime.UtcNow - lastGuardUtc).TotalSeconds >= 1.0)
            {
                if (RunNakedGuard()) return;   // v1.11: guard checks every second, not only on brick closes
            }
            if (vOpen && vUsesLimit && !vFilled && e.Price > vLimit + TickSize * 0.5)
                vFilled = true;   // SHORT: price traded THROUGH the sell limit (upward) by >= 1 tick
            if (vOpen && (!vUsesLimit || vFilled) && e.Price < vTarget - TickSize * 0.5)
                vThrough = true;  // v1.4 SHORT: price traded THROUGH the target (downward) by >= 1 tick

            // v1.5 (B): the market traded at the stop while part of the entry is still working -> cancel the
            // rest of the entry now, so no more contracts are bought into a trade that has already lost.
            if (UseLimitEntry && entryInFlight && !entryCancelSent && workingEntryOrder != null && tradeStopPrice > 0 && e.Price >= tradeStopPrice - TickSize * 0.25)
            {
                OrderState s = workingEntryOrder.OrderState;
                if (s == OrderState.Working || s == OrderState.Accepted || s == OrderState.Submitted || s == OrderState.PartFilled)
                {
                    entryCancelSent = true;
                    DiagLog(string.Format("[FAST MARKET] price {0:F2} reached the stop {1:F2} while the entry is still filling ({2}/{3}) -> cancelling the rest of the entry.",
                        e.Price, tradeStopPrice, entryFillQty, currentQty));
                    try { CancelOrder(workingEntryOrder); } catch (Exception ex) { DiagLog("[FAST MARKET] cancel error: " + ex.Message); }
                }
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs c)
        {
            try
            {
                // v1.3: ALWAYS log connection changes, so a platform-side disable after a disconnect is visible here.
                if (c.Status != lastOrderStatus || c.PriceStatus != lastPriceStatus)
                {
                    DiagLog(string.Format("[CONNECTION] orders={0} price={1} | position={2} {3} | trade open={4}",
                        c.Status, c.PriceStatus, Position.MarketPosition, Position.Quantity, awaitingClose));
                    if (c.PriceStatus == ConnectionStatus.ConnectionLost || c.PriceStatus == ConnectionStatus.Disconnected
                        || c.Status == ConnectionStatus.ConnectionLost || c.Status == ConnectionStatus.Disconnected)
                        DiagLog("[CONNECTION] LOST. If it stays down longer than NinjaTrader's 'On connection loss' delay, "
                            + "NinjaTrader may DISABLE this strategy (that is a platform action - this log would then just stop).");
                    lastOrderStatus = c.Status; lastPriceStatus = c.PriceStatus;
                }
                if (!EnableReconnectRewarm) return;
                if (c.PriceStatus == ConnectionStatus.ConnectionLost || c.PriceStatus == ConnectionStatus.Disconnected)
                {
                    pendingRewarm = true;
                    DiagLog("[RECONNECT REWARM] price connection " + c.PriceStatus + " -> will re-warm on next brick.");
                }
            }
            catch { }
        }

        // =====================================================================
        // OnBarUpdate
        // =====================================================================
        protected override void OnBarUpdate()
        {
            // keep the EMAs current on every primary bar (historical too)
            if (BarsInProgress == 0 && emaInd.Count > 0 && CurrentBar >= 0)
                foreach (var e in emaInd) { double touch = e[0]; }

            if (State != State.Realtime) return;
            if (BarsInProgress == 1) { CheckMarginCutoff(); return; }
            if (BarsInProgress != 0) return;
            if (!lifeStarted) return;
            if (!configOK) { if (!disabledSelf) FinalizeTermination(); return; }
            if (CurrentBar < 2) return;

            if (pendingRewarm) { ResetPipeline("reconnect re-warm"); pendingRewarm = false; }

            bool brickHole     = lastProcessedBar >= 0 && CurrentBar > lastProcessedBar + 1;
            int  prevProcessed = lastProcessedBar;
            lastProcessedBar   = CurrentBar;

            if (disabledSelf || pendingFlatten) { ProcessShutdown(); return; }
            if (rescueActive) ProcessShutdown();   // keep driving a rescue flatten; the pipeline still runs
            if (marginActive && Position.MarketPosition != MarketPosition.Flat) { MarginFlatten(); return; }

            if (RunNakedGuard()) return;   // v1.11: also runs every second from OnMarketData

            if ((DateTime.UtcNow - strategyStartUtc).TotalMinutes >= StrategyLifeMinutes)
            { BeginShutdown("strategy life of " + StrategyLifeMinutes + " min reached"); return; }
            if (barCount >= MaxTotalBarCount)
            { BeginShutdown("MaxTotalBarCount (" + MaxTotalBarCount + ") reached"); return; }
            if (realLossesInARow >= MaxRealLossInARow)
            { BeginShutdown("MaxRealLossInARow (" + MaxRealLossInARow + ") reached"); return; }

            CheckTradingDayRollover();

            if (brickHole)
                ResetPipeline("brick gap: CurrentBar " + prevProcessed + " -> " + CurrentBar
                    + " (missed " + (CurrentBar - prevProcessed - 1) + " brick(s))");

            if (!seedChecked)
            {
                seedChecked = true;
                if (SeedOnStart && rawString.Length == 0) ReplaySeedBricks();
            }

            // A real LIMIT entry lives only until the next brick closes.
            CancelStaleLimitEntry();

            // ── this brick's bit ──
            int bit;
            if (Close[0] < Close[1]) bit = 1;        // SHORT encoding: red (down) = 1
            else if (Close[0] > Close[1]) bit = 0;   // green (up) = 0
            else { bit = prevBarBit >= 0 ? prevBarBit : 0; DiagLog("[RENKO ANOMALY] Close[0]==Close[1] -> carrying prev bit " + bit); }
            prevBarBit = bit;
            barCount++;

            brickEvents.Clear();
            bool money = StepBrick(bit, Close[0], EmaOnPreviousBrick ? 1 : 0, false, GetCurrentBid());

            string emaTxt; bool trendOk = EmaGateOK(EmaOnPreviousBrick ? 1 : 0, out emaTxt);
            WriteBrickRow(bit, Close[0], emaTxt, trendOk);
            DiagLog(string.Format("[BRICK #{0}] close={1:F2} bit={2} trend={3} ({4}) | events={5} | f1={6} | f2={7} | armed2={8} armed3={9} vOpen={10}",
                barCount, Close[0], bit, trendOk ? "OK" : "no", emaTxt, brickEvents.Length > 0 ? brickEvents.ToString() : "-",
                TailOf(filter1Outcome, 24), TailOf(filter2Outcome, 24), armed2, armed3, vOpen));

            // WATCHDOG (v1.3): never stay stuck, never keep a position the strategy does not expect.
            if (!rescueActive && !pendingFlatten)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    BeginRescue("UNEXPECTED " + Position.MarketPosition + " " + Position.Quantity + " (wrong direction, e.g. an overfill)");
                else if (Position.MarketPosition == MarketPosition.Short && !awaitingClose && !entryInFlight)
                    BeginRescue("position " + Position.Quantity + " open but no trade is being tracked");
                else if (awaitingClose && Position.MarketPosition == MarketPosition.Flat && !entryInFlight && !HasLiveOrders())
                {
                    DiagLog("[WATCHDOG] flat and order-free but the last trade was never scored -> scoring it now.");
                    CloseRealTrade(lastExitPrice, DateTime.Now);
                }
            }

            if (money)
            {
                if (rescueActive)
                {
                    DiagLog("[MONEY] skipped: rescue flatten in progress.");
                    WriteObsRow("OBS_RESCUE");
                }
                else if (!EnableRealOrder)
                    DiagLog("[MONEY] setup would trade, but EnableRealOrder=false (observation only).");
                else if (Position.MarketPosition == MarketPosition.Flat && !entryInFlight && !awaitingClose && HasLiveOrders())
                {
                    DiagLog("[MONEY] skipped: flat but a leftover order of this strategy is still live -> cancelling it.");
                    foreach (Order o in Orders)
                    {
                        if (o == null) continue;
                        OrderState st = o.OrderState;
                        if (st == OrderState.Working || st == OrderState.Accepted || st == OrderState.PartFilled
                            || st == OrderState.Submitted || st == OrderState.ChangePending)
                        { try { CancelOrder(o); } catch { } }
                    }
                    WriteObsRow("OBS_LEFTOVER_ORDER");
                }
                else if (RealBusy())
                {
                    DiagLog("[MONEY] skipped: this strategy still has a position/order.");
                    WriteObsRow("OBS_BUSY_SELF");
                }
                else TryOpenRealTrade();
            }
        }

        // =====================================================================
        // THE PIPELINE STEP (live and seed use exactly the same code)
        // returns true when this brick created a MONEY setup
        // =====================================================================
        private bool StepBrick(int bit, double close, int emaBarsAgo, bool isSeed, double bidNow)
        {
            rawString.Append(bit == 1 ? '1' : '0');
            if (rawString.Length > 4096) rawString.Remove(0, rawString.Length - 4096);

            // 1) resolve an open virtual trade
            if (vOpen)
            {
                char res = ' ';
                if (vUsesLimit && !vFilled)
                {
                    if (isSeed) vFilled = true;   // seed has no tick data: count the limit as filled
                    else res = 'N';               // the limit was not filled by the next brick close
                }
                if (res == ' ' && (!vUsesLimit || vFilled))
                {
                    double eps = TickSize * 0.5;
                    // v1.4: a WIN needs price to trade at least 1 tick PAST the target (like the real target
                    // limit order). A brick that only closes AT the target keeps the trade open; it then wins
                    // on a later trade past the target, or loses if a later brick closes at the stop.
                    // The seed has no tick data, so there a touch still counts as a win.
                    if (!isSeed && !vThrough && close <= vTarget + eps)
                    {
                        double b = GetCurrentAsk();   // a resting BUY limit at the target fills if the ask is below it
                        if (b > 0 && b <= vTarget - eps) vThrough = true;
                    }
                    if (vThrough) res = '1';
                    else if (close <= vTarget + eps)
                    {
                        if (isSeed) res = '1';
                        else if (!vTouched) { vTouched = true; AddEvent("TARGET_TOUCH_WAIT"); }
                    }
                    else if (close >= vStop - eps) res = '0';
                }
                if (res != ' ') ResolveVirtual(res);
            }

            // 2) a new setup?
            if (!TailMatchesList(rawString.ToString(), filter1Patterns)) return false;

            string emaTxt;
            if (!EmaGateOK(emaBarsAgo, out emaTxt))
            {
                AddEvent("F1_NO_TREND");
                return false;
            }
            if (vOpen)
            {
                AddEvent("F1_SKIP_VIRTUAL_OPEN");
                return false;
            }

            vOpen = true;
            vC = close;
            vStop   = Instrument.MasterInstrument.RoundToTickSize(close + StopBricks * brickPts);     // SHORT: stop ABOVE
            vTarget = Instrument.MasterInstrument.RoundToTickSize(close - TargetBricks * brickPts);   // SHORT: target BELOW
            vUsesLimit = UseLimitEntry;
            vFilled = false; vThrough = false; vTouched = false;
            double refBid = (bidNow > 0) ? bidNow : close;
            vLimit = Instrument.MasterInstrument.RoundToTickSize(refBid + LimitOffsetPoints);   // SHORT: sell limit ABOVE the bid
            vArmed2AtSetup = armed2;
            vIsMoney = (layer == 1) || (layer == 2 && armed2) || (layer == 3 && armed2 && armed3);
            vSetupBar = CurrentBar;
            AddEvent(vIsMoney ? "SETUP_MONEY" : "SETUP");
            lastSetupInfo = string.Format("C={0:F2} stop={1:F2} target={2:F2}{3}", vC, vStop, vTarget,
                vUsesLimit ? string.Format(" limit={0:F2}", vLimit) : "");
            return vIsMoney && !isSeed;
        }

        private void ResolveVirtual(char res)
        {
            filter1Outcome.Append(res);
            if (filter1Outcome.Length > 2048) filter1Outcome.Remove(0, filter1Outcome.Length - 2048);
            if (layer >= 2 && vArmed2AtSetup)
            {
                filter2Outcome.Append(res);
                if (filter2Outcome.Length > 2048) filter2Outcome.Remove(0, filter2Outcome.Length - 2048);
                armed3 = (layer == 3) && TailMatchesList(MatchText(filter2Outcome), filter3Patterns);
            }
            armed2 = (layer >= 2) && TailMatchesList(MatchText(filter1Outcome), filter2Patterns);
            vOpen = false;
            lastResolved = res;
            AddEvent("RESOLVE_" + res);
        }

        private string MatchText(StringBuilder sb)
        {
            string s = sb.ToString();
            return IgnoreNForMatching ? s.Replace("N", "") : s;
        }

        private void AddEvent(string ev)
        {
            if (brickEvents.Length > 0) brickEvents.Append(';');
            brickEvents.Append(ev);
        }

        // =====================================================================
        // EMA TREND GATE   e.g. "9 > 20 > 30"  (blank = off)
        // =====================================================================
        private string ParseEmaGate()
        {
            emaPeriods.Clear(); emaOps.Clear();
            string s = (EmaTrendGate ?? "").Trim();
            if (s.Length == 0) return null;   // gate OFF
            var num = new StringBuilder();
            foreach (char ch in s + " ")
            {
                if (char.IsDigit(ch)) { num.Append(ch); continue; }
                if (num.Length > 0)
                {
                    int p; if (!int.TryParse(num.ToString(), out p) || p < 1 || p > 500) return "EMA gate: bad period '" + num + "'";
                    emaPeriods.Add(p); num.Clear();
                }
                if (ch == '>' || ch == '<')
                {
                    if (emaPeriods.Count != emaOps.Count + 1) return "EMA gate: '" + ch + "' must sit between two numbers";
                    emaOps.Add(ch);
                }
                else if (ch != ' ') return "EMA gate: unexpected character '" + ch + "' (use e.g. 9 > 20 > 30)";
            }
            if (emaPeriods.Count < 2) return "EMA gate: needs at least two EMAs (e.g. 9 > 30)";
            if (emaOps.Count != emaPeriods.Count - 1) return "EMA gate: needs a > or < between every two EMAs";
            if (emaPeriods.Count > 5) return "EMA gate: at most 5 EMAs";
            return null;
        }

        private string EmaGateText()
        {
            if (emaPeriods.Count == 0) return "OFF";
            var sb = new StringBuilder();
            for (int i = 0; i < emaPeriods.Count; i++)
            {
                if (i > 0) sb.Append(' ').Append(emaOps[i - 1]).Append(' ');
                sb.Append(emaPeriods[i]);
            }
            return sb.ToString();
        }

        private bool EmaGateOK(int barsAgo, out string txt)
        {
            txt = "";
            if (emaInd.Count == 0) { txt = "gate off"; return true; }
            if (CurrentBar < barsAgo) { txt = "no history"; return false; }
            var vals = new double[emaInd.Count];
            var sb = new StringBuilder();
            for (int i = 0; i < emaInd.Count; i++)
            {
                vals[i] = emaInd[i][barsAgo];
                if (i > 0) sb.Append('|');
                sb.Append(emaPeriods[i]).Append('=').Append(vals[i].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            txt = sb.ToString();
            for (int i = 0; i < emaOps.Count; i++)
            {
                if (emaOps[i] == '>' && !(vals[i] > vals[i + 1])) return false;
                if (emaOps[i] == '<' && !(vals[i] < vals[i + 1])) return false;
            }
            return true;
        }

        // =====================================================================
        // SEED BACK-FILL: replay today's closed bricks (never places orders)
        // =====================================================================
        private void ReplaySeedBricks()
        {
            if (Bars.IsFirstBarOfSession)
            {
                DiagLog("[SEED] first live brick opens a new session -> nothing to back-fill.");
                return;
            }
            int todayKey = TradingDayKeyOfLocal(Time[0]);
            int maxN = Math.Max(1, SeedBrickCount);
            int k = 0;
            string stopReason = "SeedBrickCount (" + maxN + ") reached";
            for (int i = 1; i <= maxN; i++)
            {
                if (i + 2 > CurrentBar) { stopReason = "no older bricks loaded (increase Days to load)"; break; }
                if (todayKey > 0 && TradingDayKeyOfLocal(Time[i]) != todayKey) { stopReason = "daily reset reached"; break; }
                k = i;
                if (Bars.IsFirstBarOfSessionByIndex(CurrentBar - i)) { stopReason = "session start reached"; break; }
            }
            if (k == 0) { DiagLog("[SEED] nothing to back-fill (" + stopReason + ")."); return; }

            quietLog = true;
            try
            {
                for (int i = k; i >= 1; i--)
                {
                    int bit;
                    if (Close[i] < Close[i + 1]) bit = 1;        // red = 1
                    else if (Close[i] > Close[i + 1]) bit = 0;   // green = 0
                    else bit = prevBarBit >= 0 ? prevBarBit : 0;
                    prevBarBit = bit;
                    brickEvents.Clear();
                    StepBrick(bit, Close[i], i + (EmaOnPreviousBrick ? 1 : 0), true, 0);
                }
            }
            finally { quietLog = false; }
            brickEvents.Clear();
            if (vOpen && vUsesLimit) vFilled = true;   // no tick history for the seed: count its limit as filled

            DiagLog(string.Format("[SEED] back-filled {0} brick(s) {1:yyyy-MM-dd HH:mm:ss} -> {2:yyyy-MM-dd HH:mm:ss} (stopped: {3}). "
                + "No orders. Seed limit setups are counted as filled (no tick data). | f1={4} | f2={5} | armed2={6} armed3={7} vOpen={8}",
                k, Time[k], Time[1], stopReason, TailOf(filter1Outcome, 30), TailOf(filter2Outcome, 30), armed2, armed3, vOpen));
            WriteObsRow("SEED_BACKFILL_" + k);
        }

        private void ResetPipeline(string reason)
        {
            rawString.Clear(); filter1Outcome.Clear(); filter2Outcome.Clear();
            armed2 = false; armed3 = false; vOpen = false; prevBarBit = -1;
            DiagLog("[PIPELINE RESET] " + reason + " -> strings cleared; re-warming from live bricks. "
                + "(An open REAL position keeps its broker-side stop/target.)");
        }

        // =====================================================================
        // REAL TRADE
        // =====================================================================
        private bool RealBusy()
        {
            return Position.MarketPosition != MarketPosition.Flat || entryInFlight || awaitingClose || HasLiveOrders();
        }

        private void TryOpenRealTrade()
        {
            double bidRef = GetCurrentBid();
            if (bidRef <= 0) { DiagLog("[TRADE ABORT] no valid bid"); return; }

            if (EnableTradingHours && !WithinTradingHours())
            { DiagLog("[OUTSIDE HOURS] money setup not traded."); WriteObsRow("OBS_OUTSIDE_HOURS"); return; }
            if (EnableMarginCutoff && marginActive)
            { DiagLog("[MARGIN CUTOFF] entry blocked."); WriteObsRow("OBS_MARGIN_WINDOW"); return; }
            if (AccountBusyOnThisInstrument())
            { DiagLog("[ACCOUNT BUSY] another position/order on this instrument (e.g. the LONG book) -> skipped."); WriteObsRow("OBS_ACCOUNT_BUSY"); return; }

            // v1.6: never trade an OLD brick. When the strategy starts, NinjaTrader can deliver a backlog of
            // bricks (seen in Sim: 26 bricks from the last 6 minutes in under a second). Those are processed
            // normally for the filters, but a real order is only sent for a brick that just closed.
            {
                // age = latest live trade time - this brick's time (both in the chart's time zone, so a
                // PC / platform time-zone difference cannot block trading). No live trade seen yet -> no check.
                double ageSec = lastTickTime == DateTime.MinValue ? 0 : (lastTickTime - Time[0]).TotalSeconds;
                if (ageSec > MaxBrickAgeSeconds)
                {
                    DiagLog(string.Format("[CATCH-UP] this brick closed {0:F0} s ago (limit {1} s) -> no real order.", ageSec, MaxBrickAgeSeconds));
                    WriteObsRow("OBS_OLD_BRICK");
                    return;
                }
            }
            // v1.6: price has already moved more than 1 brick away from the setup brick (or past the target)
            // -> the setup is stale, skip it.
            {
                bool pastTarget = bidRef > 0 && bidRef <= vTarget + TickSize * 0.5;
                bool tooFar     = bidRef > 0 && bidRef <  vC - brickPts - TickSize * 0.5;
                if (pastTarget || tooFar)
                {
                    DiagLog(string.Format("[SKIP] price {0:F2} moved too far from the setup close {1:F2} ({2}) -> no trade.",
                        bidRef, vC, pastTarget ? "already past the target" : "more than 1 brick away"));
                    WriteObsRow("OBS_PRICE_MOVED");
                    return;
                }
            }

            currentQty = CalcQty();
            if (currentQty <= 0) { DiagLog("[QTY SKIP] qty rule returned 0."); WriteObsRow("OBS_QTY_SKIP"); return; }

            // v1.5 (A): never send an entry that is already at/through the stop (fast market: price moved
            // between the brick close and now). A SHORT sell must be at least 1 tick BELOW the stop.
            {
                double half = TickSize * 0.5;
                bool limitBad  = UseLimitEntry && vLimit >= vStop - half;
                bool marketBad = bidRef >= vStop - half;
                if (limitBad || marketBad)
                {
                    DiagLog(string.Format("[SKIP] fast market: {0} {1:F2} is not below the stop {2:F2} -> no trade (it would be lost on arrival).",
                        limitBad ? "limit" : "bid", limitBad ? vLimit : bidRef, vStop));
                    WriteObsRow("OBS_ENTRY_PAST_STOP");
                    return;
                }
            }

            awaitingClose = true;
            entryInFlight = true;
            winQtyThisTrade = 0; lossQtyThisTrade = 0;
            exitNotional = 0.0; exitQtySum = 0; flatQtySum = 0;
            entryFillPrice = 0.0; entryFillQty = 0;
            entryFillUtc = DateTime.MinValue;
            openModifyRejectLogged = false;
            pastStopExit = false;
            entryCancelSent = false;
            fastRescueThisTrade = false;
            workingEntryOrder = null;
            realEntryBar = CurrentBar;

            tradeStopPrice   = vStop;     // anchored to the setup brick close (same levels as the virtual trade)
            tradeTargetPrice = vTarget;
            tradeLimitPrice  = UseLimitEntry ? vLimit : 0.0;

            SetStopLoss(ENTRY_SIGNAL, CalculationMode.Price, tradeStopPrice, false);
            SetProfitTarget(ENTRY_SIGNAL, CalculationMode.Price, tradeTargetPrice);
            try
            {
                if (UseLimitEntry)
                    workingEntryOrder = EnterShortLimit(0, true, currentQty, tradeLimitPrice, ENTRY_SIGNAL);
                else
                    workingEntryOrder = EnterShort(currentQty, ENTRY_SIGNAL);
                if (!entryInFlight)
                {
                    // NinjaTrader rejected/cancelled the entry WHILE it was being submitted (OnOrderUpdate already ran
                    // and reset the trade). Do not log it as a live trade.
                    DiagLog("[TRADE ABORT] entry was rejected immediately by NinjaTrader/the account -> no trade (see [ENTRY] line for the reason).");
                    workingEntryOrder = null;
                    return;
                }
                if (workingEntryOrder == null)
                {
                    DiagLog("[TRADE ABORT] NinjaTrader did not accept the entry order -> no trade.");
                    awaitingClose = false; entryInFlight = false;
                    WriteObsRow("OBS_ENTRY_NOT_ACCEPTED");
                    return;
                }
                DiagLog(string.Format("MONEY TRADE #{0} {1} qty={2} {3} stop={4:F2} target={5:F2} | {6} | f1={7} | real={8}",
                    barCount, UseLimitEntry ? "LIMIT" : "MARKET", currentQty,
                    UseLimitEntry ? string.Format("limit={0:F2}", tradeLimitPrice) : string.Format("bid~{0:F2}", bidRef),
                    tradeStopPrice, tradeTargetPrice, lastSetupInfo, TailOf(filter1Outcome, 12), TailOf(realTradeOutcome, 12)));
                WriteRealRow("REAL_ENTRY", currentQty, UseLimitEntry ? tradeLimitPrice : bidRef, 0, 0, "-");
            }
            catch (Exception ex)
            {
                DiagLog("TryOpenRealTrade error: " + ex.Message);
                awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
            }
        }

        // A real limit entry is cancelled (rest of it) once the next brick has closed.
        private void CancelStaleLimitEntry()
        {
            if (!UseLimitEntry || !entryInFlight || entryCancelSent || workingEntryOrder == null) return;
            if (CurrentBar <= realEntryBar) return;
            OrderState s = workingEntryOrder.OrderState;
            if (s == OrderState.Working || s == OrderState.Accepted || s == OrderState.Submitted || s == OrderState.PartFilled)
            {
                entryCancelSent = true;
                DiagLog(string.Format("[LIMIT] next brick closed; limit {0:F2} filled {1}/{2} -> cancelling the rest.",
                    tradeLimitPrice, entryFillQty, currentQty));
                try { CancelOrder(workingEntryOrder); } catch (Exception ex) { DiagLog("[LIMIT] cancel error: " + ex.Message); }
            }
        }

        // v1.9: callbacks are wrapped so a close is never SIZED from inside an order/fill callback
        // (NinjaTrader updates an order's Filled count before the fill and the position arrive).
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            bool outer = !inOrderCallback; inOrderCallback = true;
            try { OnExecutionUpdateCore(execution, executionId, price, quantity, marketPosition, orderId, time); }
            finally { if (outer) inOrderCallback = false; }
        }

        private void OnExecutionUpdateCore(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            { int fq; execFilledByOrder.TryGetValue(execution.Order, out fq); execFilledByOrder[execution.Order] = fq + quantity; }
            lastExecUtc = DateTime.UtcNow;   // v1.6: Position can lag a fill for a moment
            {
                OrderAction act = execution.Order.OrderAction;
                ownNet += (act == OrderAction.Buy || act == OrderAction.BuyToCover) ? quantity : -quantity;
            }
            // v1.10: an over-fill (wrong-direction position) is caught at the FILL, not at the next brick close.
            if (ownNet > 0 && !rescueActive && !pendingFlatten && !disabledSelf && State == State.Realtime)
                BeginRescue("UNEXPECTED: our own fills show LONG " + ownNet + " (over-fill) - close it now");
            string oName  = execution.Order.Name ?? "";
            bool   isFull = execution.Order.OrderState == OrderState.Filled;
            bool   isPart = execution.Order.OrderState == OrderState.PartFilled;

            // ── entry fill ──
            // v1.10: match the entry by NAME only. The last fill of a partly-filled entry can arrive AFTER
            // NinjaTrader has already marked the order Cancelled; v1.9 then counted those contracts as an
            // EXIT loss (seen in Sim: qty=19 lossQty=26, and a 1-lot trade logged with qty=0).
            if (oName == ENTRY_SIGNAL)
            {
                if (entryFillPrice == 0.0) entryFillPrice = price;
                else entryFillPrice = (entryFillPrice * entryFillQty + price * quantity) / (entryFillQty + quantity);
                entryFillQty += quantity;
                entryFillUtc = DateTime.UtcNow;
                DiagLog(string.Format("ENTRY {0} fill: qty={1} @ {2:F2} total={3}{4}", isFull ? "FULL" : isPart ? "PARTIAL" : "LATE (order already " + execution.Order.OrderState + ")",
                    quantity, price, entryFillQty, entryCancelSent ? " (arrived after the cancel was sent - still protected)" : ""));
                if (isFull) { entryInFlight = false; workingEntryOrder = null; }

                // PAST-STOP GUARD (v1.3): the market is already past the stop when contracts fill.
                // Do NOT fire an extra exit for just these contracts (v1.2 could double-exit together with
                // NinjaTrader's own bracket stop). Instead run a RESCUE FLATTEN: cancel all of this strategy's
                // orders, then close the WHOLE live position, re-reading the real position every pass.
                double askNow = GetCurrentAsk();
                if (askNow > 0 && askNow >= tradeStopPrice)
                {
                    pastStopExit = true;
                    DiagLog(string.Format("[PAST-STOP GUARD] fill @ {0:F2} but askNow {1:F2} >= stop {2:F2} -> rescue flatten of the whole position.",
                        price, askNow, tradeStopPrice));
                    FastMarketRescue("past-stop fill");
                }
                if (pendingFlatten || rescueActive) ProcessShutdown();
                return;
            }

            // ── exits: quantity tally (mixed trades score by P&L sign in CloseRealTrade, v1.11) ──
            bool isStopFill     = oName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill   = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                               || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isPastStop     = oName == PASTSTOP_NAME;
            bool isOurFlatten   = oName == FLATTEN_NAME || oName == MARGIN_NAME;
            lastExitPrice = price;
            exitNotional += price * quantity; exitQtySum += quantity;   // v1.8: real P&L
            if (isOurFlatten) flatQtySum += quantity;
            if      (isPastStop)   lossQtyThisTrade += quantity;
            else if (isStopFill)   lossQtyThisTrade += quantity;
            else if (isTargetFill) winQtyThisTrade  += quantity;
            else if (isOurFlatten) { /* forced cleanup: does not vote */ }
            else                   lossQtyThisTrade += quantity;   // session close / unknown -> loss

            if (Position.MarketPosition == MarketPosition.Flat && awaitingClose && !entryInFlight)
                CloseRealTrade(price, time);
            else if (Position.MarketPosition == MarketPosition.Flat && awaitingClose && entryInFlight && entryCancelSent)
                DiagLog("[EXIT] flat, waiting for the entry cancel to confirm before scoring.");

            if (pendingFlatten || rescueActive) ProcessShutdown();
        }

        // v1.3: the position going flat is the most reliable "trade is over" signal (fill callbacks can
        // arrive before NinjaTrader has updated Position).
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            lastPosSigned = marketPosition == MarketPosition.Long ? quantity : marketPosition == MarketPosition.Short ? -quantity : 0;
            if (State != State.Realtime) return;
            if (marketPosition != MarketPosition.Flat || !awaitingClose || rescueActive || pendingFlatten) return;
            bool entryLive = entryInFlight && workingEntryOrder != null
                && (workingEntryOrder.OrderState == OrderState.Working || workingEntryOrder.OrderState == OrderState.Accepted
                    || workingEntryOrder.OrderState == OrderState.Submitted || workingEntryOrder.OrderState == OrderState.PartFilled);
            if (entryLive)
            {
                if (!entryCancelSent) { entryCancelSent = true; try { CancelOrder(workingEntryOrder); } catch { } }
                DiagLog("[POSITION] flat while part of the entry is still working -> cancelling it, scoring after the cancel.");
                return;
            }
            CloseRealTrade(lastExitPrice, DateTime.Now);
        }

        private void CloseRealTrade(double exitPrice, DateTime time)
        {
            if (!awaitingClose) return;   // already scored
            int bit = (winQtyThisTrade > lossQtyThisTrade) ? 1 : 0;
            double pnl = (entryFillPrice - exitPrice) * entryFillQty * Instrument.MasterInstrument.PointValue;   // SHORT
            // v1.8: P&L from the ACTUAL exit fills (average exit price), not just the last fill.
            if (exitQtySum > 0)
            {
                exitPrice = exitNotional / exitQtySum;
                pnl = (entryFillPrice - exitPrice) * entryFillQty * Instrument.MasterInstrument.PointValue;
                // closed mostly by our own close orders (rescue/shutdown/margin): those do not vote -> score by P&L
                if (flatQtySum * 2 >= exitQtySum) bit = pnl > 0 ? 1 : 0;
                // v1.11: a MIXED trade (part closed at the target, part at the stop) scores by its real P&L sign,
                // not by which side had more contracts (seen in Sim: +$120 trade scored as a loss).
                else if (winQtyThisTrade > 0 && lossQtyThisTrade > 0) bit = pnl > 0 ? 1 : 0;
            }
            DiagLog(string.Format("MONEY TRADE CLOSED {0}: avgEntry={1:F2} avgExit={2:F2} qty={3} winQty={4} lossQty={5} pnl~{6:0.00} bit={7}",
                bit == 1 ? "WIN" : "LOSS", entryFillPrice, exitPrice, entryFillQty, winQtyThisTrade, lossQtyThisTrade, pnl, bit));

            realTradeOutcome.Append(bit == 1 ? '1' : '0');
            if (realTradeOutcome.Length > 2048) realTradeOutcome.Remove(0, realTradeOutcome.Length - 2048);
            sessionRealOutcome.Append(bit == 1 ? '1' : '0');
            if (bit == 0) { realLossesInARow++; DiagLog("[REAL LOSS] realLossesInARow=" + realLossesInARow); }
            else { realLossesInARow = 0; }

            WriteRealRow("REAL_CLOSE", entryFillQty, entryFillPrice, exitPrice, pnl, bit.ToString());

            awaitingClose = false; entryInFlight = false; workingEntryOrder = null;
            entryFillPrice = 0.0; entryFillQty = 0; pastStopExit = false;

            if (EnableTradeOutcomeExit && !string.IsNullOrEmpty(TradeOutcomeExitPattern)
                && sessionRealOutcome.ToString().EndsWith(TradeOutcomeExitPattern.Trim()))
            {
                DiagLog("[OUTCOME EXIT] today's real results end with '" + TradeOutcomeExitPattern + "' -> halting. today="
                    + sessionRealOutcome.ToString());
                BeginShutdown("trade-outcome exit '" + TradeOutcomeExitPattern + "' matched");
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            bool outer = !inOrderCallback; inOrderCallback = true;
            try { OnOrderUpdateCore(order, limitPrice, stopPrice, quantity, filled, averageFillPrice, orderState, time, error, nativeError); }
            finally { if (outer) inOrderCallback = false; }
        }

        private void OnOrderUpdateCore(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;
            string oName = order.Name ?? "";

            if (oName == ENTRY_SIGNAL && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                entryInFlight = false; workingEntryOrder = null;
                if (filled == 0)
                {
                    DiagLog("[ENTRY] " + orderState + " with no fill -> no trade this setup."
                        + (orderState == OrderState.Rejected
                            ? " Reason: " + error + " | " + (string.IsNullOrEmpty(nativeError) ? "-" : nativeError)
                              + " (check Base quantity and the account's risk / max order size settings)"
                            : ""));
                    WriteRealRow(orderState == OrderState.Cancelled ? "REAL_NOFILL" : "REAL_ENTRY_REJECTED", 0, tradeLimitPrice, 0, 0, "-");
                    awaitingClose = false; entryFillPrice = 0.0; entryFillQty = 0; pastStopExit = false;
                }
                else
                {
                    DiagLog("[ENTRY] " + orderState + " after a partial fill of " + filled + " -> the filled contracts keep their bracket.");
                    if (Position.MarketPosition == MarketPosition.Flat && awaitingClose)
                        CloseRealTrade(averageFillPrice, time);   // bracket already closed everything
                }
                if (pendingFlatten || rescueActive) ProcessShutdown();
                return;
            }

            // MODIFY-REJECT GUARD
            bool isProtective = (oName == "Stop loss" || oName == "Profit target");
            if (error == ErrorCode.UnableToChangeOrder && isProtective && Position.MarketPosition == MarketPosition.Flat
                && (orderState == OrderState.Working || orderState == OrderState.Accepted || orderState == OrderState.PartFilled))
            {
                DiagLog("[ORPHAN GUARD] flat & '" + oName + "' modify rejected -> force-cancelling leftover order.");
                try { CancelOrder(order); } catch { }
            }
            else if (error == ErrorCode.UnableToChangeOrder && isProtective && Position.MarketPosition != MarketPosition.Flat)
            {
                if (!openModifyRejectLogged)
                {
                    openModifyRejectLogged = true;
                    DiagLog("[ORPHAN GUARD] '" + oName + "' modify rejected while OPEN -> left in place; naked guard is the backstop.");
                }
                // v1.10: when the TARGET has partly filled, NinjaTrader shrinks the stop to the contracts left. If that
                // resize is rejected the stop is now too big and can over-fill (seen in Sim: 800 short -> long 9,
                // 35 long -> short 2). Take over: cancel the brackets and close the rest at market, one close at a time.
                if (oName == "Stop loss" && TargetPartlyFilled() && !rescueActive && !pendingFlatten && !disabledSelf)
                    BeginRescue("target partly filled but NinjaTrader could not resize the stop (over-fill risk) - closing the rest", true);
            }

            // ORPHAN GUARD: a protective order was rejected -> unprotected position.
            if (isProtective && orderState == OrderState.Rejected)
            {
                if (pastStopExit || rescueActive)
                {
                    DiagLog("[ORPHAN GUARD] '" + oName + "' rejected during a rescue flatten (expected: price was already past it).");
                }
                else
                {
                    // v1.5 (C): a STOP rejected because the market is already past it is a fast-market LOSS,
                    // not a breakdown: close the position (same bounded flatten) and keep running.
                    // Any other protective reject still shuts the strategy down.
                    string nat = nativeError ?? "";
                    double pxNow = GetCurrentAsk();
                    bool stopPastMarket = oName == "Stop loss" && tradeStopPrice > 0 && ((pxNow > 0 && pxNow >= tradeStopPrice - TickSize * 0.5) || nat.IndexOf("below the market", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (stopPastMarket && Position.MarketPosition != MarketPosition.Flat)
                    {
                        pastStopExit = true;
                        DiagLog("[ORPHAN GUARD] 'Stop loss' rejected because the market is already past it (fast market) -> close the position. native="
                            + (string.IsNullOrEmpty(nat) ? "-" : nat));
                        FastMarketRescue("stop rejected, market already past the stop");
                        return;
                    }
                    DiagLog("[ORPHAN GUARD] protective '" + oName + "' REJECTED (" + error + ") -> flattening now. native="
                        + (string.IsNullOrEmpty(nativeError) ? "-" : nativeError));
                    BeginShutdown("protective order rejected (orphan guard)");
                    return;
                }
            }

            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
                DiagLog(string.Format("ORDER WARN: {0} state={1} err={2} native={3}", oName, orderState, error,
                    string.IsNullOrEmpty(nativeError) ? "-" : nativeError));

            if (pendingFlatten || rescueActive) ProcessShutdown();
        }

        // =====================================================================
        // QTY RULE (pattern on today's REAL results; OFF by default)
        // =====================================================================
        private void ParseQtyRule()
        {
            try
            {
                string src = QtyRuleText ?? "";
                var units = new List<string>();
                var groups = System.Text.RegularExpressions.Regex.Matches(src, @"\(([^)]*)\)");
                if (groups.Count > 0) foreach (System.Text.RegularExpressions.Match g in groups) units.Add(g.Groups[1].Value);
                else units.AddRange(src.Split(','));
                var list = new List<(string pattern, int multiplier)>();
                foreach (string u in units)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(u, @"([01]+)\s*""?\s*:\s*(\d+)");
                    if (!m.Success) { if (u.Trim().Length > 0) DiagLog("[QTY RULE] ignored '" + u.Trim() + "'"); continue; }
                    int q = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (q > QTY_MULT_CAP) { DiagLog("[QTY RULE] DANGER '" + m.Groups[1].Value + "':" + q + " exceeds cap " + QTY_MULT_CAP + " -> REFUSED."); continue; }
                    list.Add((m.Groups[1].Value, q));
                }
                if (list.Count > 0)
                {
                    qtyTable = list.ToArray();
                    DiagLog("[QTY RULE] " + (EnableQtyIncrement ? "ACTIVE: " : "(off) table: ")
                        + string.Join("  ", list.Select(e => e.pattern + "->x" + e.multiplier)));
                }
                else DiagLog("[QTY RULE] no valid pairs in '" + src + "' -> keeping default.");
            }
            catch (Exception ex) { DiagLog("[QTY RULE] parse error: " + ex.Message + " -> keeping default."); }
        }

        private int CalcQty()
        {
            if (!EnableQtyIncrement) return BaseQuantity;
            string outcome = sessionRealOutcome.ToString();
            if (outcome.Length == 0) return BaseQuantity;
            int bestLen = -1, bestMult = 1; bool matched = false;
            foreach (var e in qtyTable)
                if (e.pattern.Length > bestLen && outcome.EndsWith(e.pattern)) { bestLen = e.pattern.Length; bestMult = e.multiplier; matched = true; }
            if (!matched) return BaseQuantity;
            int qty = BaseQuantity * bestMult;
            DiagLog(string.Format("[QTY] today={0} -> x{1} -> qty={2}", outcome, bestMult, qty));
            return qty;
        }

        // =====================================================================
        // DAY KEY (daily reset in Pacific time, setting) + rollover
        // =====================================================================
        private TimeZoneInfo PacificZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch { try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { return null; } }
        }

        private int TradingDayKeyOfLocal(DateTime localTime)
        {
            try
            {
                TimeZoneInfo pt = PacificZone();
                DateTime ptTime = (pt == null) ? localTime
                    : TimeZoneInfo.ConvertTimeFromUtc(TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), TimeZoneInfo.Local), pt);
                TimeSpan reset = new TimeSpan(DailyResetHourPT, DailyResetMinutePT, 0);
                DateTime d = (ptTime.TimeOfDay >= reset) ? ptTime.Date : ptTime.Date.AddDays(-1);
                return d.Year * 10000 + d.Month * 100 + d.Day;
            }
            catch { return -1; }
        }

        private int CurrentTradingDayKey() { return TradingDayKeyOfLocal(DateTime.Now); }

        private void CheckTradingDayRollover()
        {
            int key = CurrentTradingDayKey();
            if (key < 0) return;
            if (currentTradingDayKey == -1) { currentTradingDayKey = key; return; }
            if (key == currentTradingDayKey) return;
            DiagLog(string.Format("[DAY ROLLOVER] {0} -> {1}: strings, arming, open virtual trade, today's real results "
                + "and the loss breaker are reset. realTradeOutcome (all-time) kept.", currentTradingDayKey, key));
            rawString.Clear(); filter1Outcome.Clear(); filter2Outcome.Clear();
            armed2 = false; armed3 = false; vOpen = false;
            sessionRealOutcome.Clear();
            realLossesInARow = 0;
            fastRescuesToday = 0;
            currentTradingDayKey = key;
        }

        // Rebuild today's real results (qty rule / outcome exit / breaker) from this strategy's
        // most recent earlier log, so a restart in the middle of the day does not forget them.
        private void RestoreTodaysRealResults()
        {
            try
            {
                if (!Directory.Exists(LogFolder)) return;
                string latest = Directory.GetFiles(LogFolder, LogBaseName + "_*.csv")
                    .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("-diagLog", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(p, activeLogFilePath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => File.GetLastWriteTime(p)).FirstOrDefault();
                if (latest == null) { DiagLog("[RESTORE] no earlier log -> today's real results start empty."); return; }
                int today = CurrentTradingDayKey();
                var bits = new StringBuilder();
                foreach (string line in File.ReadAllLines(latest))
                {
                    string[] p = line.Split(',');
                    if (p.Length < 17 || p[2].Trim() != "REAL_CLOSE") continue;
                    DateTime ts;
                    if (!DateTime.TryParse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out ts)) continue;
                    if (TradingDayKeyOfLocal(ts) != today) continue;
                    string b = p[16].Trim();
                    if (b == "0" || b == "1") bits.Append(b);
                }
                sessionRealOutcome.Clear(); sessionRealOutcome.Append(bits.ToString());
                realLossesInARow = 0;
                for (int i = bits.Length - 1; i >= 0 && bits[i] == '0'; i--) realLossesInARow++;
                DiagLog("[RESTORE] today's real results from " + Path.GetFileName(latest) + ": '" + bits + "' (losses in a row="
                    + realLossesInARow + ").");
            }
            catch (Exception ex) { DiagLog("[RESTORE] failed: " + ex.Message); }
        }

        // =====================================================================
        // PATTERNS  ('*' = 1+ zeros, '?' = 1+ ones; F2/F3 may also contain N)
        // =====================================================================
        private void ParsePatterns(string text, List<string> list, bool allowN, string label)
        {
            list.Clear();
            if (!string.IsNullOrEmpty(text))
                foreach (string tok in text.Split(','))
                {
                    string t = (tok ?? "").Trim().ToUpperInvariant();
                    if (t.Length == 0) continue;
                    bool ok = true;
                    foreach (char c in t)
                        if (!(c == '0' || c == '1' || c == '*' || c == '?' || (allowN && c == 'N'))) { ok = false; break; }
                    if (ok) list.Add(t); else DiagLog("[" + label + "] ignored invalid pattern '" + t + "'");
                }
            DiagLog("[" + label + "] active=[" + string.Join(",", list) + "]");
        }

        private static bool TailMatchesList(string text, List<string> pats)
        {
            for (int i = 0; i < pats.Count; i++) if (TailMatches(text, pats[i])) return true;
            return false;
        }

        private static bool TailMatches(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || text.Length == 0) return false;
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0) return text.EndsWith(pattern);
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
                    int maxConsume = ti;
                    while (maxConsume < text.Length && text[maxConsume] == want) maxConsume++;
                    for (int consume = maxConsume; consume >= ti; consume--)
                        if (MatchHere(text, consume, pattern, pi + 1)) return true;
                    return false;
                }
                if (ti >= text.Length || text[ti] != pc) return false;
                ti++; pi++;
            }
            return ti == text.Length;
        }

        private string TailOf(StringBuilder sb, int n)
        {
            string s = sb.ToString();
            return s.Length <= n ? s : "..." + s.Substring(s.Length - n);
        }

        // =====================================================================
        // LOGGING
        // =====================================================================
        private string BuildNewLogFilePath()
        {
            return Path.Combine(LogFolder, LogBaseName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
        }

        private void EnsureLogHeader(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path)) File.WriteAllText(path, LOG_HEADER + "\n");
            }
            catch { }
        }

        private void SafeAppend(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) { logWriteFailed = true; return; }
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs)) { sw.Write(text); }
                    return;
                }
                catch (IOException) { if (attempt == 5) break; System.Threading.Thread.Sleep(20 * attempt); }
                catch (Exception ex) { DiagLog("[LOG ERROR] " + ex.Message); logWriteFailed = true; return; }
            }
            DiagLog("[LOG ERROR] log file locked after 5 tries - row dropped.");
            logWriteFailed = true;
        }

        private static string F(double v) { return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }

        private string Row(string ev, string close, string bit, string ema, string trend, string setupC, string lim,
            string stop, string tgt, string vres, string qty, string entry, string exit, string pnl, string realBit)
        {
            return string.Join(",", new[] {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                barCount.ToString(), ev, close, bit, ema, trend, setupC, lim, stop, tgt, vres, qty, entry, exit, pnl, realBit,
                filter1Outcome.ToString(), filter2Outcome.ToString(), realTradeOutcome.ToString() }) + "\n";
        }

        // one row per closed brick; 'event' lists everything that happened on it (e.g. RESOLVE_0;SETUP_MONEY)
        private void WriteBrickRow(int bit, double close, string emaTxt, bool trendOk)
        {
            bool setupNow = brickEvents.ToString().Contains("SETUP");
            bool resolvedNow = brickEvents.ToString().Contains("RESOLVE_");
            SafeAppend(activeLogFilePath, Row(brickEvents.Length > 0 ? "BRICK;" + brickEvents : "BRICK",
                F(close), bit.ToString(), emaTxt.Replace(",", ";"), trendOk ? "1" : "0",
                setupNow ? F(vC) : "", setupNow && vUsesLimit ? F(vLimit) : "", setupNow ? F(vStop) : "", setupNow ? F(vTarget) : "",
                resolvedNow ? lastResolved.ToString() : "", "", "", "", "", ""));
        }

        private void WriteRealRow(string ev, int qty, double entry, double exit, double pnl, string realBit)
        {
            SafeAppend(activeLogFilePath, Row(ev, "", "", "", "", "", tradeLimitPrice > 0 ? F(tradeLimitPrice) : "",
                F(tradeStopPrice), F(tradeTargetPrice), "", qty.ToString(), F(entry), F(exit), F(pnl), realBit));
        }

        private void WriteObsRow(string ev)
        {
            SafeAppend(activeLogFilePath, Row(ev, "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
        }

        private void DiagLog(string msg)
        {
            if (quietLog) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg;
            Print(line);
            try
            {
                string dir = Path.GetDirectoryName(activeLogFilePath ?? "");
                if (string.IsNullOrEmpty(dir)) dir = LogFolder;
                if (string.IsNullOrEmpty(dir)) dir = @"C:\temp";
                string baseName = Path.GetFileNameWithoutExtension(activeLogFilePath ?? (LogBaseName + ".csv"));
                string diagPath = Path.Combine(dir, baseName + "-diagLog.csv");
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(diagPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (var sw = new StreamWriter(fs)) { sw.Write(line + "\n"); }
                        break;
                    }
                    catch (IOException) { if (attempt == 3) break; System.Threading.Thread.Sleep(10 * attempt); }
                }
            }
            catch { }
        }

        // =====================================================================
        // NEW YORK CLOCK + MARGIN CUTOFF
        // =====================================================================
        private TimeZoneInfo _etZone = null;
        private bool _etWarned = false;
        private TimeZoneInfo EasternZone()
        {
            if (_etZone != null) return _etZone;
            try { _etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { _etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { _etZone = null; } }
            return _etZone;
        }
        private DateTime EasternNow()
        {
            TimeZoneInfo z = EasternZone();
            if (z == null)
            {
                if (!_etWarned) { DiagLog("[CLOCK] WARNING: New York time zone not found; using platform time."); _etWarned = true; }
                return DateTime.Now;
            }
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, z);
        }

        private void CheckMarginCutoff()
        {
            if (!EnableMarginCutoff) { marginActive = false; marginLogged = false; return; }
            if (State != State.Realtime) return;
            if (CurrentBars.Length < 2 || CurrentBars[1] < 0) return;
            DateTime t = EasternNow();
            int nowMin = t.Hour * 60 + t.Minute;
            int flattenMin = MarginCutoffHour * 60 + MarginCutoffMinute - MarginCutoffLeadMin;
            const int blockMinutes = 80;
            bool active = (nowMin >= flattenMin && nowMin < flattenMin + blockMinutes);
            if (active && !marginLogged)
            {
                DiagLog(string.Format("[MARGIN CUTOFF] window active (NY {0:HH:mm}) - flatten, no new entries.", t));
                marginLogged = true;
            }
            if (!active) marginLogged = false;
            marginActive = active;
            if (marginActive && Position.MarketPosition != MarketPosition.Flat) MarginFlatten();
            if (marginActive && entryInFlight && workingEntryOrder != null && !entryCancelSent)
            {
                entryCancelSent = true;
                try { CancelOrder(workingEntryOrder); } catch { }
            }
        }

        private void MarginFlatten()
        {
            try
            {
                // v1.8: through the bounded close-all rescue (one close at a time, brackets cancelled first)
                // instead of a full-size market exit on every bar while the stop/target are still live.
                if (Position.MarketPosition != MarketPosition.Flat && !rescueActive && !pendingFlatten && !disabledSelf)
                    BeginRescue("margin cutoff: flatten before the overnight margin snapshot", true);
            }
            catch (Exception ex) { DiagLog("[MARGIN CUTOFF] flatten error: " + ex.Message); }
        }

        // =====================================================================
        // SHUTDOWN (immediate flatten + 1-second heartbeat, bounded)
        // =====================================================================
        // RESCUE FLATTEN (v1.3): the same bounded, re-checking flatten as the shutdown, but the strategy
        // keeps running afterwards. Used for a past-stop fill and for a position the strategy did not expect.
        // v1.5: a fast-market rescue (past-stop fill or stop rejected because the market is already past it).
        // Counted per day; reaching MaxFastRescuesPerDay closes the position AND turns the strategy off.
        private void FastMarketRescue(string reason)
        {
            if (rescueActive || pendingFlatten || disabledSelf) return;   // already flattening: do not count twice
            if (fastRescueThisTrade) { BeginRescue(reason + " (same trade - not counted again)"); return; }
            fastRescueThisTrade = true;
            fastRescuesToday++;
            if (fastRescuesToday >= MaxFastRescuesPerDay)
            {
                DiagLog("[FAST MARKET] rescue " + fastRescuesToday + "/" + MaxFastRescuesPerDay
                    + " today -> limit reached: close the position and turn the strategy OFF (market too fast for this brick size?).");
                BeginShutdown("fast-market rescue limit (" + MaxFastRescuesPerDay + "/day) reached: " + reason);
                return;
            }
            BeginRescue(reason + " (fast-market rescue " + fastRescuesToday + "/" + MaxFastRescuesPerDay + " today)");
        }

        private void BeginRescue(string reason, bool closeAll = false)
        {
            if (rescueActive || pendingFlatten || disabledSelf) return;
            rescueActive = true;
            rescueCloseAll = closeAll;
            rescueReason = reason;
            flattenAttempts = 0; lastFlattenUtc = DateTime.MinValue; flattenGaveUp = false; cancelWaitPasses = 0; closeWaitPasses = 0;
            DiagLog("[RESCUE] " + reason + (closeAll
                ? " -> cancel this strategy's orders and close the WHOLE position, one close at a time (strategy keeps running)."
                : " -> close the contracts that have no live stop; protected contracts keep their own stop/target (strategy keeps running)."));
            StartFlattenTimer();
            ProcessShutdown();
        }

        private void EndRescue()
        {
            rescueActive = false;
            StopFlattenTimer();
            flattenAttempts = 0; flattenGaveUp = false;
            DiagLog("[RESCUE] done (" + rescueReason + "): flat and order-free.");
            if (awaitingClose) CloseRealTrade(lastExitPrice, DateTime.Now);
            entryInFlight = false; workingEntryOrder = null; pastStopExit = false;
        }

        private void BeginShutdown(string reason)
        {
            if (disabledSelf || pendingFlatten) return;
            if (rescueActive) { rescueActive = false; flattenAttempts = 0; flattenGaveUp = false; }   // shutdown takes over
            cancelWaitPasses = 0; closeWaitPasses = 0;
            pendingReason = reason;
            pendingFlatten = true;
            DiagLog("Shutdown: " + reason + " | bars=" + barCount + " | lossRow=" + realLossesInARow);
            StartFlattenTimer();
            ProcessShutdown();
        }

        private void StartFlattenTimer()
        {
            if (flattenTimer != null) return;
            try
            {
                flattenTimer = new System.Timers.Timer(1000);
                flattenTimer.AutoReset = true;
                flattenTimer.Elapsed += OnFlattenHeartbeat;
                flattenTimer.Start();
            }
            catch (Exception ex) { DiagLog("[SHUTDOWN] heartbeat start error: " + ex.Message); }
        }

        private void StopFlattenTimer()
        {
            System.Timers.Timer t = flattenTimer;
            flattenTimer = null;
            if (t == null) return;
            try { t.Stop(); t.Elapsed -= OnFlattenHeartbeat; t.Dispose(); } catch { }
        }

        private void OnFlattenHeartbeat(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                TriggerCustomEvent(o =>
                {
                    if (State != State.Realtime || !(pendingFlatten || rescueActive) || flattenGaveUp || disabledSelf) { StopFlattenTimer(); return; }
                    ProcessShutdown();
                }, null);
            }
            catch { }
        }

        // v1.11: the naked-position guard as a method, run on every brick AND at most once a second from
        // OnMarketData. In v1.10 it only ran on brick closes, so on slow bricks a contract with no stop
        // waited 1 min 43 s (Sim, 11:03:56 -> 11:05:39).
        private DateTime lastGuardUtc       = DateTime.MinValue;
        private DateTime uncoveredFirstUtc  = DateTime.MinValue;
        private bool RunNakedGuard()
        {
            lastGuardUtc = DateTime.UtcNow;
            if (disabledSelf || pendingFlatten || marginActive) return false;
            // NAKED-POSITION GUARD (two-look)
            bool openAndUnprotected =
                Position.MarketPosition != MarketPosition.Flat && (!entryInFlight || entryFillQty > 0)
                && entryFillUtc != DateTime.MinValue
                && (DateTime.UtcNow - entryFillUtc).TotalSeconds > NakedGraceSeconds
                && !rescueActive
                && !HasLiveStop();
            if (!openAndUnprotected) nakedFirstSeenUtc = DateTime.MinValue;
            else if (nakedFirstSeenUtc == DateTime.MinValue)
            {
                nakedFirstSeenUtc = DateTime.UtcNow;
                DiagLog("[NAKED GUARD] saw open + no live stop -> waiting " + NakedConfirmSeconds + "s to confirm.");
            }
            else if ((DateTime.UtcNow - nakedFirstSeenUtc).TotalSeconds >= NakedConfirmSeconds)
            {
                DiagLog("[NAKED GUARD] STILL open + no live stop -> flatten + disable.");
                nakedFirstSeenUtc = DateTime.MinValue;
                BeginShutdown("naked position (no live stop)");
                return true;
            }


            // v1.11: PART of the position has no stop (some contracts' stop was rejected, the rest are protected).
            // Hand those contracts to a rescue, which closes only the unprotected ones.
            int posQ = Math.Abs(Position.Quantity);
            int posSigned = Position.MarketPosition == MarketPosition.Long ? posQ : Position.MarketPosition == MarketPosition.Short ? -posQ : 0;
            int covered = posQ > 0 ? LiveStopQty(Position.MarketPosition == MarketPosition.Long) : 0;
            bool uncovered = posQ > 0 && posSigned == ownNet && covered > 0 && covered < posQ
                && !rescueActive && !entryInFlight
                && entryFillUtc != DateTime.MinValue && (DateTime.UtcNow - entryFillUtc).TotalSeconds > NakedGraceSeconds
                && (DateTime.UtcNow - lastExecUtc).TotalSeconds > 2.0;
            if (!uncovered) uncoveredFirstUtc = DateTime.MinValue;
            else if (uncoveredFirstUtc == DateTime.MinValue)
            {
                uncoveredFirstUtc = DateTime.UtcNow;
                DiagLog("[NAKED GUARD] " + (posQ - covered) + " of " + posQ + " contracts have no live stop -> waiting " + NakedConfirmSeconds + "s to confirm.");
            }
            else if ((DateTime.UtcNow - uncoveredFirstUtc).TotalSeconds >= NakedConfirmSeconds)
            {
                uncoveredFirstUtc = DateTime.MinValue;
                BeginRescue((posQ - covered) + " of " + posQ + " contracts have no live stop -> close only those");
            }
            return false;
        }

        private bool HasLiveStop()
        {
            foreach (Order o in Orders)
            {
                if (o == null || (o.Name ?? "") != "Stop loss") continue;
                OrderState s = o.OrderState;
                if (!(s == OrderState.Cancelled || s == OrderState.Filled || s == OrderState.Rejected || s == OrderState.Unknown))
                    return true;
            }
            return false;
        }

        // v1.5: how many contracts this strategy's own LIVE exit orders (stop, target, flatten) could still close.
        // Stop/target pairs share an OCO id and only one of them can fill, so each OCO group counts once.
        // v1.7: contracts protected by a LIVE stop of ours, plus what our own close orders already working will close.
        private int LiveStopQty(bool positionIsLong)
        {
            int sum = 0;
            foreach (Order o in Orders)
            {
                if (o == null) continue;
                string n = o.Name ?? "";
                if (n != "Stop loss" && n != FLATTEN_NAME && n != MARGIN_NAME && n != PASTSTOP_NAME) continue;
                OrderState st = o.OrderState;
                bool live = st == OrderState.Working || st == OrderState.Accepted || st == OrderState.Submitted
                         || st == OrderState.PartFilled || st == OrderState.ChangePending || st == OrderState.ChangeSubmitted
                         || st == OrderState.CancelPending || st == OrderState.CancelSubmitted || st == OrderState.Initialized
                         || st == OrderState.TriggerPending;
                if (!live) continue;
                bool reduces = positionIsLong
                    ? (o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort)
                    : (o.OrderAction == OrderAction.Buy  || o.OrderAction == OrderAction.BuyToCover);
                if (reduces) sum += RemainingQty(o);
            }
            return sum;
        }

        // v1.9: an order's remaining quantity counted from the fills WE have seen (OnExecutionUpdate), so it
        // always agrees with our own position count; NinjaTrader's Order.Filled can run ahead of both.
        private int RemainingQty(Order o)
        {
            int f; execFilledByOrder.TryGetValue(o, out f);
            return Math.Max(0, o.Quantity - f);
        }

        // v1.10: has any of this trade's profit-target quantity filled?
        private bool TargetPartlyFilled()
        {
            if (winQtyThisTrade > 0) return true;
            foreach (Order o in Orders)
            {
                if (o == null || (o.Name ?? "") != "Profit target") continue;
                int f; execFilledByOrder.TryGetValue(o, out f);
                if (f > 0 || o.Filled > 0) return true;
            }
            return false;
        }

        private int LiveFlattenQty()
        {
            int sum = 0;
            foreach (Order o in Orders)
            {
                if (o == null) continue;
                string n = o.Name ?? "";
                if (n != FLATTEN_NAME && n != MARGIN_NAME && n != PASTSTOP_NAME) continue;
                OrderState st = o.OrderState;
                if (st == OrderState.Filled || st == OrderState.Cancelled || st == OrderState.Rejected) continue;
                sum += RemainingQty(o);
            }
            return sum;
        }

        private int LiveClosingQty(bool positionIsLong)
        {
            var byOco = new Dictionary<string, int>();
            int loose = 0;
            foreach (Order o in Orders)
            {
                if (o == null || (o.Name ?? "") == ENTRY_SIGNAL) continue;
                OrderState st = o.OrderState;
                bool live = st == OrderState.Working || st == OrderState.Accepted || st == OrderState.Submitted
                         || st == OrderState.PartFilled || st == OrderState.ChangePending || st == OrderState.ChangeSubmitted
                         || st == OrderState.CancelPending || st == OrderState.CancelSubmitted || st == OrderState.Initialized
                         || st == OrderState.TriggerPending;
                if (!live) continue;
                bool reduces = positionIsLong
                    ? (o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort)
                    : (o.OrderAction == OrderAction.Buy  || o.OrderAction == OrderAction.BuyToCover);
                if (!reduces) continue;
                int rem = RemainingQty(o);
                string oco = o.Oco ?? "";
                if (oco.Length == 0) loose += rem;
                else { int cur; byOco[oco] = byOco.TryGetValue(oco, out cur) ? Math.Max(cur, rem) : rem; }
            }
            int sum = loose;
            foreach (int v in byOco.Values) sum += v;
            return sum;
        }

        private bool HasLiveOrders()
        {
            foreach (Order o in Orders)
            {
                if (o == null) continue;
                OrderState s = o.OrderState;
                if (s == OrderState.Working || s == OrderState.Accepted || s == OrderState.PartFilled || s == OrderState.Submitted
                    || s == OrderState.ChangePending || s == OrderState.CancelPending)
                    return true;
            }
            return false;
        }

        private void ProcessShutdown()
        {
            if (inFlatten) return;
            inFlatten = true;
            try
            {
                if (entryInFlight && workingEntryOrder != null)
                {
                    var os = workingEntryOrder.OrderState;
                    if (os == OrderState.Working || os == OrderState.Accepted || os == OrderState.Submitted || os == OrderState.PartFilled)
                    { try { CancelOrder(workingEntryOrder); } catch { entryInFlight = false; awaitingClose = false; workingEntryOrder = null; } }
                }
                if (!flattenGaveUp && (DateTime.UtcNow - lastFlattenUtc).TotalSeconds >= 1.0
                    && (Position.MarketPosition != MarketPosition.Flat || HasLiveOrders()))
                {
                    if (flattenAttempts < MaxFlattenAttempts)
                    {
                        foreach (Order o in Orders)
                        {
                            if (o == null || (o.Name ?? "") == FLATTEN_NAME || (o.Name ?? "") == MARGIN_NAME || (o.Name ?? "") == PASTSTOP_NAME) continue;
                            // v1.7: a RESCUE leaves the stop/target brackets alone (NinjaTrader did not cancel them in Sim,
                            // and closing their contracts while they are live over-sold). Only a full shutdown cancels them.
                            if (rescueActive && !rescueCloseAll && ((o.Name ?? "") == "Stop loss" || (o.Name ?? "") == "Profit target")) continue;
                            OrderState s = o.OrderState;
                            if (s == OrderState.Working || s == OrderState.Accepted || s == OrderState.PartFilled
                                || s == OrderState.Submitted || s == OrderState.ChangePending)
                            { try { CancelOrder(o); } catch { } }
                        }
                        // v1.5 (D): close only the part of the position that NO live order of ours can still close.
                        // Our stops/targets are being cancelled above; while they are still live they may fill,
                        // so closing their quantity too could over-sell (seen in Sim: went short 1).
                        // An unprotected part (e.g. its stop was rejected) is closed AT ONCE, never delayed.
                        // If the cancels have not confirmed after 4 passes (~4 s), close everything anyway.
                        string tag = rescueActive ? "RESCUE" : "SHUTDOWN";
                        // v1.9: never SIZE a close from inside an order/fill callback: NinjaTrader has already counted
                        // the fill on the order but not yet on the position (seen in Sim: rescue closed 1 contract per
                        // pass that a stop was also filling -> short 1). The 1-second heartbeat sizes it instead.
                        if (inOrderCallback && Position.MarketPosition != MarketPosition.Flat) return;
                        // v1.6: NinjaTrader updates Position a moment AFTER a fill. Sizing a close on a
                        // not-yet-updated Position over-sells (seen in Sim: went short 1 and short 3).
                        // So never size a close within 0.75 s of one of our own fills - try again next pass.
                        if (Position.MarketPosition != MarketPosition.Flat && (DateTime.UtcNow - lastExecUtc).TotalSeconds < 0.75)
                            return;
                        // v1.6: the execution grid showed NinjaTrader's Position lagging our fills by 1-2 s in Sim.
                        // We also count the position from our own fills; while the two disagree, wait (max 3 s,
                        // then trust NinjaTrader's Position).
                        {
                            int posSigned = Position.MarketPosition == MarketPosition.Long ? Position.Quantity
                                          : Position.MarketPosition == MarketPosition.Short ? -Position.Quantity : 0;
                            sizingQtyCap = int.MaxValue;
                            // v1.10: THREE sources must agree: NinjaTrader's Position, the latest position update, and our
                            // own fill count (after a reconnect NinjaTrader re-syncs the position without fills -> seen in
                            // Sim: 'closing SHORT 395 of 395' right after the trade had gone flat).
                            if ((posSigned != ownNet || posSigned != lastPosSigned) && Position.MarketPosition != MarketPosition.Flat)
                            {
                                if (posMismatchUtc == DateTime.MinValue)
                                {
                                    posMismatchUtc = DateTime.UtcNow;
                                    DiagLog("[" + (rescueActive ? "RESCUE" : "SHUTDOWN") + "] NinjaTrader position " + posSigned
                                        + " / last position update " + lastPosSigned + " / our own fill count " + ownNet + " disagree -> waiting before closing.");
                                }
                                if ((DateTime.UtcNow - posMismatchUtc).TotalSeconds < 3.0) return;
                                int sg = Math.Sign(posSigned);
                                if (posSigned == ownNet)
                                {
                                    // v1.11: NinjaTrader's Position AND our own fill count agree -> trust them; only the
                                    // position-update event is behind (seen in Sim: 272/271/272 -> v1.10 used 271 and
                                    // left 1 contract with no stop).
                                    DiagLog("[" + (rescueActive ? "RESCUE" : "SHUTDOWN") + "] Position and our own fill count agree (" + posSigned
                                        + "); the position-update event is just late -> using " + Math.Abs(posSigned) + ".");
                                }
                                else if (Math.Sign(ownNet) != sg || Math.Sign(lastPosSigned) != sg)
                                {
                                    // the sources still disagree on the DIRECTION (or one says flat): sending a close could
                                    // open a position the wrong way. Do nothing this pass; the bounded passes then give up
                                    // and report FLATTEN FAILED rather than guess.
                                    DiagLog("[" + (rescueActive ? "RESCUE" : "SHUTDOWN") + "] position sources still disagree on direction ("
                                        + posSigned + " / " + lastPosSigned + " / " + ownNet + ") -> no close this pass.");
                                    flattenAttempts++; lastFlattenUtc = DateTime.UtcNow;
                                    return;
                                }
                                else
                                {
                                sizingQtyCap = Math.Min(Math.Abs(posSigned), Math.Min(Math.Abs(ownNet), Math.Abs(lastPosSigned)));
                                DiagLog("[" + (rescueActive ? "RESCUE" : "SHUTDOWN") + "] still disagreeing after 3 s -> closing at most the SMALLEST of them ("
                                    + sizingQtyCap + ").");
                                }
                            }
                            else posMismatchUtc = DateTime.MinValue;
                        }
                        // v1.8: ONE close order at a time. While a close order of ours is still working, NinjaTrader can
                        // already show part of it filled before the position catches up; sizing a second close then
                        // can over-close (seen in Sim: 'closing SHORT 1 of 50' right after 'closing SHORT 50 of 50').
                        if (Position.MarketPosition != MarketPosition.Flat)
                        {
                            int inFlight = LiveFlattenQty();
                            if (inFlight > 0 && closeWaitPasses < 5)
                            {
                                closeWaitPasses++;
                                lastFlattenUtc = DateTime.UtcNow;
                                DiagLog("[" + (rescueActive ? "RESCUE" : "SHUTDOWN") + "] our close order for " + inFlight
                                    + " is still working -> waiting for it before sending anything else (" + closeWaitPasses + "/5).");
                                return;
                            }
                            if (inFlight == 0) closeWaitPasses = 0;
                        }
                        if (Position.MarketPosition != MarketPosition.Flat)
                        {
                            bool isLongPos = Position.MarketPosition == MarketPosition.Long;
                            int posQ = Math.Min(Math.Abs(Position.Quantity), sizingQtyCap);
                            int covered = (rescueActive && !rescueCloseAll) ? LiveStopQty(isLongPos) : LiveClosingQty(isLongPos);
                            int q = posQ - covered;
                            if (rescueActive && !rescueCloseAll && q <= 0)
                            {
                                // v1.7: every remaining contract has its own live stop -> leave it to its stop/target.
                                DiagLog("[RESCUE] " + Position.MarketPosition + " " + posQ + " left: all protected by their own live stop ("
                                    + covered + ") -> handing them back to their stop/target; the rescue ends, the strategy keeps running.");
                                rescueActive = false;
                                StopFlattenTimer();
                                flattenAttempts = 0; flattenGaveUp = false; cancelWaitPasses = 0;
                                pastStopExit = false;   // v1.8: a later stop reject on this trade must start a new rescue again
                                return;
                            }
                            if (q <= 0)
                            {
                                if (cancelWaitPasses < 4)
                                {
                                    cancelWaitPasses++;
                                    lastFlattenUtc = DateTime.UtcNow;
                                    DiagLog("[" + tag + "] " + Position.MarketPosition + " " + posQ + " is fully covered by this strategy's own live exit orders ("
                                        + covered + ") -> waiting for their cancel/fill before closing (" + cancelWaitPasses + "/4).");
                                    return;
                                }
                                // cancels never confirmed: close everything (last resort), minus what our own
                                // close orders already working will close (never send a second close for them).
                                q = posQ - LiveFlattenQty();
                            }
                            if (q > 0)
                            {
                                if (isLongPos) ExitLong(0, q, FLATTEN_NAME, "");
                                else           ExitShort(0, q, FLATTEN_NAME, "");
                            }
                            else q = 0;
                            DiagLog("[" + tag + "] pass " + (flattenAttempts + 1) + "/" + MaxFlattenAttempts + ": closing "
                                + (isLongPos ? "LONG " : "SHORT ") + q + " of " + posQ
                                + (q < posQ ? " (the other " + (posQ - q) + " are still covered by our own live exit orders)." : "."));
                        }
                        else DiagLog("[" + tag + "] pass " + (flattenAttempts + 1) + "/" + MaxFlattenAttempts + ": flat, cancelling leftover order(s).");
                        flattenAttempts++;
                        lastFlattenUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        flattenGaveUp = true;
                        StopFlattenTimer();
                        if (rescueActive)
                        {
                            // v1.5: a rescue that cannot get flat turns into a full shutdown: no more trading.
                            rescueActive = false; pendingFlatten = true;
                            pendingReason = "rescue flatten failed (" + rescueReason + ")";
                        }
                        DiagLog("[" + (rescueActive ? "RESCUE" : "SHUTDOWN") + "][FLATTEN FAILED] not flat & order-free after " + MaxFlattenAttempts
                            + " passes -> STOPPING all order activity. CHECK THE ACCOUNT AND FLATTEN BY HAND.");
                    }
                }
                if (Position.MarketPosition == MarketPosition.Flat && !HasLiveOrders())
                {
                    if (pendingFlatten) { if (!entryInFlight) FinalizeTermination(); }
                    else if (rescueActive) EndRescue();
                }
            }
            finally { inFlatten = false; }
        }

        private void FinalizeTermination()
        {
            if (disabledSelf) return;
            disabledSelf = true;
            pendingFlatten = false;
            StopFlattenTimer();
            DiagLog("TERMINATED. Reason: " + pendingReason + " | bars=" + barCount + " | lossRow=" + realLossesInARow
                + " | f1=" + filter1Outcome + " | f2=" + filter2Outcome + " | real=" + realTradeOutcome);
            try { SetState(State.Terminated); } catch { }
        }

        // =====================================================================
        // ACCOUNT / HOURS
        // =====================================================================
        private bool AccountBusyOnThisInstrument()
        {
            try
            {
                if (Account == null) return true;
                lock (Account.Positions)
                    foreach (Position p in Account.Positions)
                        if (p.Instrument == Instrument && p.MarketPosition != MarketPosition.Flat) return true;
                lock (Account.Orders)
                    foreach (Order ord in Account.Orders)
                        if (ord.Instrument == Instrument
                            && (ord.OrderState == OrderState.Working || ord.OrderState == OrderState.Accepted
                                || ord.OrderState == OrderState.Submitted || ord.OrderState == OrderState.PartFilled
                                || ord.OrderState == OrderState.ChangePending || ord.OrderState == OrderState.CancelPending))
                            return true;
                return false;
            }
            catch { return true; }
        }

        private bool WithinTradingHours()
        {
            if (!EnableTradingHours) return true;
            DateTime ny = EasternNow();
            int cur = ny.Hour * 60 + ny.Minute;
            int s = TradingStartHour * 60 + TradingStartMinute;
            int e = TradingEndHour * 60 + TradingEndMinute;
            return (s <= e) ? (cur >= s && cur <= e) : (cur >= s || cur <= e);
        }

        // =====================================================================
        // PROPERTIES
        // =====================================================================
        #region Properties

        [Display(Name = "Data series", Order = 1, GroupName = "0. REQUIRED SETUP",
            Description = "Renko series, Trading Hours = CME US Index Futures ETH, load at least 3 days (the EMAs need history).")]
        [ReadOnly(true)]
        public string SeriesReminder { get { return "Renko, CME US Index Futures ETH, >= 3 days loaded"; } set { } }

        [Display(Name = "Layers", Order = 2, GroupName = "0. REQUIRED SETUP",
            Description = "F2 blank = Layer 1. F2 set + F3 blank = Layer 2. F2 + F3 set = Layer 3. F3 without F2 = refused.")]
        [ReadOnly(true)]
        public string LayerReminder { get { return "F2 blank = L1 | F2 only = L2 | F2 + F3 = L3"; } set { } }

        [NinjaScriptProperty]
        [Display(Name = "Enable Real Order", Order = 1, GroupName = "1. Core",
            Description = "FALSE = observation only (strings and log still run). TRUE = money setups place real orders.")]
        public bool EnableRealOrder { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Base quantity", Order = 2, GroupName = "1. Core")]
        public int BaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Stop (bricks)", Order = 3, GroupName = "1. Core",
            Description = "Stop distance in bricks ABOVE the setup brick close. Converted to points from the chart's brick size.")]
        public int StopBricks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Target (bricks)", Order = 4, GroupName = "1. Core",
            Description = "Target distance in bricks BELOW the setup brick close. Research: SHORT = 3.")]
        public int TargetBricks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use limit entry", Order = 1, GroupName = "2. Entry",
            Description = "ON = sell limit at (bid + offset), cancelled if not filled by the next brick close (recorded as N). "
                        + "OFF = market entry.")]
        public bool UseLimitEntry { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, double.MaxValue)]
        [Display(Name = "Limit offset (points)", Order = 2, GroupName = "2. Entry")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 1 (bricks, comma OR)", Order = 1, GroupName = "3. Filters",
            Description = "Tail pattern(s) on the brick string, SHORT encoding (red=1, green=0), e.g. 110 = red, red, green. '*'=1+ zeros, '?'=1+ ones.")]
        public string Filter1Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA trend gate  (e.g. 9 < 20 < 30, blank = off)", Order = 2, GroupName = "3. Filters",
            Description = "EMAs of the Renko closes. A setup counts only when the chain holds, e.g. 9 < 20 < 30 means "
                        + "EMA9 below EMA20 below EMA30 (downtrend). 2 to 5 EMAs.")]
        public string EmaTrendGate { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Check EMAs on the previous brick", Order = 3, GroupName = "3. Filters",
            Description = "ON = test the EMA chain on the brick BEFORE the setup brick (the bounce brick is not included yet). Default OFF.")]
        public bool EmaOnPreviousBrick { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 2 (on F1 outcomes, blank = Layer 1)", Order = 4, GroupName = "3. Filters",
            Description = "Tail pattern(s) on the F1 outcome string (1/0/N). Default 0 = skip the setup right after a win.")]
        public string Filter2Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter 3 (on F2 outcomes, blank = no Layer 3)", Order = 5, GroupName = "3. Filters")]
        public string Filter3Pattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ignore N for matching", Order = 6, GroupName = "3. Filters",
            Description = "ON = N (unfilled limit) is removed before F2/F3 are matched. OFF = write N into your patterns (e.g. 0,0N).")]
        public bool IgnoreNForMatching { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Seed: back-fill today's bricks on start", Order = 1, GroupName = "4. Seed")]
        public bool SeedOnStart { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Seed brick count (max)", Order = 2, GroupName = "4. Seed")]
        public int SeedBrickCount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable trading hours filter", Order = 1, GroupName = "5. Hours",
            Description = "OFF = trade all hours (research default). Times are New York.")]
        public bool EnableTradingHours { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start hour (NY)", Order = 2, GroupName = "5. Hours")]
        public int TradingStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start minute (NY)", Order = 3, GroupName = "5. Hours")]
        public int TradingStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End hour (NY)", Order = 4, GroupName = "5. Hours")]
        public int TradingEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End minute (NY)", Order = 5, GroupName = "5. Hours")]
        public int TradingEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable qty rule", Order = 1, GroupName = "6. Quantity",
            Description = "OFF by default. ON = multiply Base quantity when TODAY's real results end with a pattern.")]
        public bool EnableQtyIncrement { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Qty rule  ***TRIPLE-CHECK FORMAT***", Order = 2, GroupName = "6. Quantity",
            Description = "e.g. (\"00\":2),(\"000\":3). Longest matching tail of today's real results wins. "
                        + "Any multiplier over 20 is refused. Check the [QTY RULE] line in the diag log.")]
        public string QtyRuleText { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Strategy life (minutes)", Order = 1, GroupName = "7. Limits")]
        public int StrategyLifeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max total brick count", Order = 2, GroupName = "7. Limits")]
        public int MaxTotalBarCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max real losses in a row (per day)", Order = 3, GroupName = "7. Limits",
            Description = "Halts the strategy. Research saw 18-25 losses in a row, so keep this high (default 30).")]
        public int MaxRealLossInARow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable trade-outcome exit", Order = 4, GroupName = "7. Limits")]
        public bool EnableTradeOutcomeExit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade-outcome exit pattern (halt)", Order = 5, GroupName = "7. Limits",
            Description = "Halts when TODAY's real results end with this plain pattern. Default 111 (rare = effectively runs all day).")]
        public string TradeOutcomeExitPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Restore today's real results on restart", Order = 6, GroupName = "7. Limits",
            Description = "ON = re-read today's real results from the previous log so the qty rule, outcome exit and loss breaker "
                        + "remember them after a restart. Strings still re-warm from the seed back-fill.")]
        public bool RestoreTodayFromLog { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max fast-market rescues per day", Order = 7, GroupName = "7. Limits",
            Description = "A fast market can run through the stop before it is placed. The strategy then closes the trade "
                        + "and keeps running. When this many such rescues happen in one day, it closes and turns itself OFF. Default 3.")]
        public int MaxFastRescuesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(5, int.MaxValue)]
        [Display(Name = "Max brick age for a real order (s)", Order = 8, GroupName = "7. Limits",
            Description = "A real order is only sent for a brick that closed within this many seconds. Older bricks "
                        + "(a backlog delivered at start-up or after a freeze) still update the filters but never trade. Default 60.")]
        public int MaxBrickAgeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Daily reset hour (Pacific)", Order = 1, GroupName = "8. Day reset",
            Description = "Strings and today's results reset here. Default 15:00 Pacific = the ETH session start.")]
        public int DailyResetHourPT { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Daily reset minute (Pacific)", Order = 2, GroupName = "8. Day reset")]
        public int DailyResetMinutePT { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable margin cutoff (early EOD)", Order = 1, GroupName = "9. Margin")]
        public bool EnableMarginCutoff { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Broker margin cutoff hour (NY)", Order = 2, GroupName = "9. Margin")]
        public int MarginCutoffHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Broker margin cutoff minute (NY)", Order = 3, GroupName = "9. Margin")]
        public int MarginCutoffMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Flatten lead minutes", Order = 4, GroupName = "9. Margin")]
        public int MarginCutoffLeadMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log folder", Order = 1, GroupName = "10. Logging")]
        public string LogFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log base name", Order = 2, GroupName = "10. Logging")]
        public string LogBaseName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Re-warm on disconnect (advanced)", Order = 3, GroupName = "10. Logging",
            Description = "OFF by default. ON = a lost price connection clears the strings so they re-warm from live bricks.")]
        public bool EnableReconnectRewarm { get; set; }

        #endregion
    }
}
