#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion


// =============================================================================
// STRATEGY: scalper_QEMA_Order v1.0.2
// AUTHOR:   Albert Feng / Drafted with help from Claude
// =============================================================================
//
// Single-file unified Qualified EMA Cross strategy with three subsystems:
//   1. Event Generator (QEMA-specific): two EMAs + slope-qualified crosses
//   2. Alert Subsystem (UNIVERSAL — copied from scalper_MACD_Order v1.0.1)
//   3. Order Subsystem (UNIVERSAL — copied from scalper_MACD_Order v1.0.1)
//
// EnableOrders=false (default) → alert-only mode. No real trades.
// EnableOrders=true            → alert + order subsystem active.
//
// CORE DESIGN: Pending trade has FIVE possible close triggers:
//   1. Stop loss hit
//   2. Profit target hit
//   3. Trading hours end
//   4. EventWindowBars expired (hold window)
//   5. NEW QUALIFIED EMA CROSS — closes the current trade, does NOT open new
//
// =============================================================================
// v1.0.2 CHANGES vs v1.0.1 — brake counter scope + observability
// =============================================================================
//
// CHANGE 1: consecutiveLosses now counts ONLY post-alert captured outcomes
//   (capturedPostAlert == true). These are the trades the order subsystem
//   would actually take with real money — i.e. simulated real-order outcomes.
//
//   Old (v1.0.1): the brake counted ANY qualified outcome with bit=0,
//   regardless of whether an alert had armed the next entry. This meant
//   "would-trade" qualified crosses that occurred BEFORE the first alert
//   fired could trip the brake even though the order subsystem would never
//   have placed an order on them.
//
//   New (v1.0.2): the brake counts only outcomes that satisfy
//   capturedPostAlert == true (the same condition that appends a bit to
//   PostAlertOutcomeString). For these rows:
//       bit == 0  → consecutiveLosses++  (simulated real-order loss)
//       bit == 1  → consecutiveLosses=0  (simulated real-order win, reset)
//   Rows with capturedPostAlert == false do not touch the counter.
//
//   The brake is now also evaluated regardless of EnableOrders, because the
//   captured sequence is a SIMULATION of what real orders would have done.
//   This makes historical replay diagnostic-useful: a multi-day preload that
//   trips the brake is telling you exactly what your real-order P&L history
//   looked like under the current parameters.
//
//   NOTE ON 'bit' SEMANTICS — what the brake actually counts:
//     STOP    → bit=0 (broker stop fired = real loss)
//     TARGET  → bit=1 (broker target fired = real win)
//     WINDOW_EXPIRED / HOURS_BOUNDARY / NEW_QEMA killer:
//             → bit = sign of (Close[0] vs Entry) at force-flat moment.
//               This is the would-be P&L sign of the simulated market exit.
//     EDGE CASE: Close[0] == Entry exactly is classified as bit=0 (loss).
//     This is the conservative choice — a real order would pay commission +
//     slippage on a flat exit, producing a small real loss. Documented here
//     so users understand why a perfectly flat resolution counts against
//     the brake.
//
// CHANGE 2: New CSV columns in scalper_QEMA_occ.csv
//   - SimConsecutiveLossesAfter: running count of consecutive sim-order losses
//     after processing THIS row. Resets to 0 on a sim win. Unchanged on
//     non-captured rows (carries forward the previous value).
//   - RunningMaxSimConsLossesEver: highest value SimConsecutiveLossesAfter
//     has ever reached up to and including this row. Monotonically increasing.
//     READ THE LAST ROW to know the worst sim-loss streak in your history.
//   These columns are written for EVERY row (qualified + skipped) so the
//   reader can scan top-to-bottom without filtering.
//
// CHANGE 3: Safety brake trips during historical replay too
//   Because Change 1 made the counter reflect simulated real orders, the
//   brake now meaningfully reads historical data. If a preload contains a
//   sim-loss streak >= MaxConsecutiveLosses, the strategy auto-disables
//   IMMEDIATELY (during the historical pass, before reaching realtime).
//
//   This is the intended UX: the user enables the strategy, sees it disable,
//   opens scalper_QEMA_occ.csv, sorts/searches RunningMaxSimConsLossesEver,
//   and learns the historical worst streak. They can then either:
//     (a) raise MaxConsecutiveLosses to (historical_max + a few) and re-enable
//     (b) reconsider the entry rules
//
//   IMPORTANT WARNING — historical replay vs. real-time can differ:
//     NinjaTrader's historical replay is bar-close based and runs on
//     completed bars. Real-time runs on the same bar-close events but
//     real orders experience slippage, partial fills, and broker delays
//     that the simulator does not model. The captured-bit sequence assumes
//     the simulated order resolves exactly at the qualified setup's
//     stop/target/close prices. In live trading the actual P&L bit may
//     differ on edge cases (e.g. a stop-and-target both within one bar's
//     range, a TARGET fill at a price worse than the bracket level due to
//     slippage, or a WINDOW exit that fills materially worse than Close).
//     The brake counter is therefore CALIBRATED on simulated outcomes;
//     real-time outcomes may produce slightly different streak lengths.
//     This is acceptable because the brake's job is to detect REGIME
//     issues (broken parameters, bad market hours), not slippage-level
//     differences.
//
// EVERYTHING ELSE IDENTICAL TO v1.0.1. No changes to event generator, alert
// subsystem state, order subsystem state machine, file names, or defaults.
// Schema version bumped to 1.0.2 in all three CSV headers.
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_QEMA_Order : Strategy
    {
        // =====================================================================
        // ENUMS
        // =====================================================================
        public enum TradeDirectionMode
        {
            LongOnly,
            ShortOnly,
            Both
        }

        public enum OrderEntryType
        {
            Market,
            Limit
        }

        private enum OrderSubState
        {
            Idle,           // no working order, no position
            Working,        // entry order submitted, waiting for fill
            Position        // entry filled, bracket attached, waiting for stop or target
        }

        private enum PendingState
        {
            Free,           // not watching any cross
            Pending         // watching a confirmed cross for stop/target/window
        }

        #region Fields

        // =====================================================================
        // ALERT SUBSYSTEM STATE (universal)
        // =====================================================================
        private StringBuilder qualifiedEmaOutcomeString = new StringBuilder();
        private StringBuilder postAlertOutcomeString = new StringBuilder();
        private int pendingPostAlertCaptures = 0;
        private bool isAlerted = false;

        // [v1.0.0] AlertPattern is a LIST of patterns (comma-separated)
        private List<string> compiledAlertPatterns = null;

        // Daily numbering
        private int dailyCrossNumber = 0;          // ALL crosses (accepted + skipped)
        private int dailyQualifiedNumber = 0;       // QUALIFIED only
        private int dailyAlertNumber = 0;
        private int dailyOrderNumber = 0;

        // Daily reset state
        private DateTime currentTradingDateNy = DateTime.MinValue;
        private TimeZoneInfo nyTz;
        private const int RESET_HOUR_NY   = 9;
        private const int RESET_MINUTE_NY = 30;

        private bool configValid = false;

        // Beep cadence (reserved for future use; currently single sound per alert)
        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        // =====================================================================
        // EVENT GENERATOR STATE (QEMA-specific)
        // =====================================================================
        private EMA emaFastIndicator;
        private EMA emaSlowIndicator;

        private PendingState pendingState = PendingState.Free;
        private bool   pendingIsLong;
        private double pendingEntry;
        private double pendingStop;
        private double pendingTarget;
        private int    pendingBarsWatched;
        private DateTime pendingCrossTime;
        private double pendingMaxFavorable;     // best excursion (in direction of trade)
        private double pendingMaxAdverse;       // worst excursion (against trade)
        private int    pendingDailyCrossNumber; // CSV linkage
        private int    pendingDailyQualifiedNumber;
        private double pendingEmaFastAtCross;
        private double pendingEmaSlowAtCross;
        private double pendingSlopeFastAtCross;
        private double pendingSlopeSlowAtCross;

        // Live order linkage (only meaningful when EnableOrders=true)
        private int pendingLiveOrderNumber = 0;

        // =====================================================================
        // ORDER SUBSYSTEM STATE (universal)
        // =====================================================================
        private OrderSubState orderState = OrderSubState.Idle;

        private Order workingEntryOrder = null;
        private int   bricksSinceEntrySubmit = 0;        // "bars" in this strategy (kept name for symmetry)
        private int   entrySubmitBarIdx = -1;
        private double entryReferencePrice = 0.0;
        private double entryLimitPrice = 0.0;
        private int   entryOrderNumber = 0;
        private bool  entryIsLong = true;

        private bool cancelRequested = false;
        private int  bricksInCurrentState = 0;

        private OrderEntryType currentWorkingOrderType = OrderEntryType.Market;
        private DateTime marketSubmitWallClock = DateTime.MinValue;
        private bool marketTimeoutLogged = false;

        // [v1.0.2] consecutiveLosses is now driven by SIMULATED real-order
        // outcomes (capturedPostAlert==true rows only), not all qualified
        // outcomes. See header comments Change 1.
        private int consecutiveLosses = 0;

        // [v1.0.2] runningMaxSimConsLossesEver tracks the all-time peak of
        // consecutiveLosses. Written to every CSV row so the user can read
        // the worst historical sim-loss streak directly from the last row.
        private int runningMaxSimConsLossesEver = 0;

        private bool safetyBrakeTripped = false;
        private int lastEntryOrderNumber = 0;
        private DateTime lastDesyncLogTime = DateTime.MinValue;

        private double actualFillPrice = 0.0;
        private double computedStopPrice = 0.0;
        private double computedTargetPrice = 0.0;

        private bool forceFlatInProgress = false;

        // Live order tied to a pending event (so we can force-flat on window expiry)
        private int liveOrderEntryBarIdx = -1;
        private int liveOrderEventWindowBars = 0;

        #endregion

        // =====================================================================
        // OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Qualified-EMA-Cross strategy. Two EMAs with slope-qualification. Trade lifecycle has 5 close triggers: stop, target, window, hours, OR new qualified cross (which closes current trade without opening new). EnableOrders=false (default) runs alert-only. Supports long/short/both. Comma-separated AlertPattern list. Mirrors scalper_MACD_Order v1.0.1 architecture; only event generator and 5th close trigger differ.";
                Name        = "scalper_QEMA_Order";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = true;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- EMA Source defaults ----
                PeriodEMA_A         = 8;
                PeriodEMA_B         = 30;
                SlopePeriod         = 5;
                SlopeThresholdFast  = 0.25;
                SlopeThresholdSlow  = 0.05;

                // ---- Direction & Outcome defaults ----
                TradeDirection    = TradeDirectionMode.LongOnly;
                EventWindowBars   = 30;
                StopPoints        = 20.0;
                TargetPoints      = 50.0;

                // ---- Trading Hours defaults (NY) ----
                TradingStartHourNY   = 9;
                TradingStartMinuteNY = 30;
                TradingEndHourNY     = 16;
                TradingEndMinuteNY   = 0;

                // ---- Alert Subsystem defaults ----
                AlertPattern      = "01";
                AlertSoundCount   = 3;
                AlertReminderSecs = 1;

                // ---- Order Execution defaults ----
                EnableOrders             = false;           // SAFETY: alert-only by default
                OrderQuantity            = 1;
                OrderType                = OrderEntryType.Market;
                LimitOffsetPoints        = 5.0;
                LimitUnfilledCancelBars  = 1;
                MaxConsecutiveLosses     = 3;               // SAFETY: conservative for new code

                // ---- Visuals defaults ----
                EnableChartMarkers = true;
                ShowOutcomeLabels  = true;
                AutoPlotEMAs       = true;

                // ---- Logging defaults ----
                AuditLogPath = @"C:\temp";

                // ---- Advanced defaults ----
                MaxBitsKept = 5000;
            }
            else if (State == State.Configure)
            {
                // Ensure we have enough bars for the slow EMA + slope lookback to be valid
                BarsRequiredToTrade = Math.Max(BarsRequiredToTrade,
                    Math.Max(PeriodEMA_A, PeriodEMA_B) + SlopePeriod + 1);
            }
            else if (State == State.DataLoaded)
            {
                try
                {
                    nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                }
                catch (Exception ex)
                {
                    Print(string.Format("[INIT] WARN: NY timezone load failed: {0}. Using local.", ex.Message));
                    nyTz = TimeZoneInfo.Local;
                }

                emaFastIndicator = EMA(PeriodEMA_A);
                emaSlowIndicator = EMA(PeriodEMA_B);

                if (AutoPlotEMAs)
                {
                    AddChartIndicator(emaFastIndicator);
                    AddChartIndicator(emaSlowIndicator);
                }

                configValid = TryCompileConfig();
                ResetDailyState();
            }
            else if (State == State.Realtime)
            {
                PrintInitLog();
            }
        }

        // =====================================================================
        // TryCompileConfig — validate parameters and parse AlertPattern list
        // =====================================================================
        private bool TryCompileConfig()
        {
            compiledAlertPatterns = ParseAlertPatternList(AlertPattern);

            if (compiledAlertPatterns == null || compiledAlertPatterns.Count == 0)
                return false;

            if (StopPoints <= 0 || TargetPoints <= 0)
            {
                Print("[VALIDATE] *** StopPoints and TargetPoints must be > 0. ***");
                return false;
            }

            if (TradingStartHourNY * 60 + TradingStartMinuteNY ==
                TradingEndHourNY   * 60 + TradingEndMinuteNY)
            {
                Print("[VALIDATE] *** TradingStart and TradingEnd are equal. Set to different times. ***");
                return false;
            }

            if (PeriodEMA_A >= PeriodEMA_B)
            {
                Print(string.Format("[VALIDATE] WARN: PeriodEMA_A ({0}) should typically be < PeriodEMA_B ({1}). Fast should be smaller than Slow. Strategy will still run but signals may be inverted.",
                    PeriodEMA_A, PeriodEMA_B));
            }

            if (SlopePeriod < 1)
            {
                Print("[VALIDATE] *** SlopePeriod must be >= 1. ***");
                return false;
            }

            return true;
        }

        // [v1.0.0] Parse comma-separated AlertPattern (identical to Renko v1.4.4)
        private List<string> ParseAlertPatternList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Print("[VALIDATE] *** AlertPattern is empty. Must contain at least one pattern (1-10 chars of '0' or '1'). ***");
                return null;
            }

            var result = new List<string>();
            var rawParts = raw.Split(',');
            foreach (var rawPart in rawParts)
            {
                string p = rawPart.Trim();
                if (p.Length == 0) continue;
                if (p.Length > 10)
                {
                    Print(string.Format("[VALIDATE] *** AlertPattern entry \"{0}\" is {1} chars; max allowed is 10. Skipping. ***",
                        p, p.Length));
                    continue;
                }

                bool ok = true;
                for (int i = 0; i < p.Length; i++)
                {
                    if (p[i] != '0' && p[i] != '1')
                    {
                        Print(string.Format("[VALIDATE] *** AlertPattern entry \"{0}\" contains invalid char '{1}' at position {2}. Only '0' and '1' allowed. Skipping. ***",
                            p, p[i], i));
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;

                if (result.Contains(p))
                {
                    Print(string.Format("[VALIDATE] WARN: duplicate AlertPattern entry \"{0}\" — keeping only first occurrence.", p));
                    continue;
                }

                result.Add(p);
            }

            if (result.Count == 0)
            {
                Print("[VALIDATE] *** AlertPattern list produced 0 valid entries after parsing. Strategy cannot start. ***");
                return null;
            }

            return result;
        }

        // [v1.0.0] Suffix-overlap warning (identical to Renko v1.4.4)
        private void CheckForSuffixOverlaps()
        {
            if (compiledAlertPatterns == null || compiledAlertPatterns.Count < 2) return;

            int warnCount = 0;
            for (int i = 0; i < compiledAlertPatterns.Count; i++)
            {
                for (int j = 0; j < compiledAlertPatterns.Count; j++)
                {
                    if (i == j) continue;
                    string a = compiledAlertPatterns[i];
                    string b = compiledAlertPatterns[j];
                    if (a.Length < b.Length && b.EndsWith(a))
                    {
                        string msg = string.Format(
                            "AlertPattern overlap: \"{0}\" is a suffix of \"{1}\". Both will match the same brick when the tail ends in \"{1}\", but only ONE alert fires per brick (no extra information). Consider removing \"{0}\" if you don't need it.",
                            a, b);
                        Print(string.Format("[VALIDATE] WARN: {0}", msg));
                        try { Log(msg, LogLevel.Warning); } catch { }
                        warnCount++;
                    }
                }
            }
            if (warnCount == 0)
                Print("[VALIDATE] AlertPattern list: no suffix overlaps detected.");
            else
                Print(string.Format("[VALIDATE] AlertPattern list: {0} suffix-overlap warning(s) above. Strategy will still run.", warnCount));
        }

        // =====================================================================
        // PrintInitLog
        // =====================================================================
        private void PrintInitLog()
        {
            Print("================================================================");
            Print(string.Format("[INIT] scalper_QEMA_Order v1.0.2 at {0}",
                DateTime.Now.ToString("HH:mm:ss.fff")));
            Print(string.Format("[INIT] Instrument: {0}  TickSize: {1}",
                Instrument.FullName, TickSize));
            Print(string.Format("[INIT] BarsPeriod: {0} {1}  (any bar type works)",
                BarsPeriod.Value, BarsPeriod.BarsPeriodType));
            Print(string.Format("[INIT] Run start (local): {0}   (UTC: {1})   (NY: {2})",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));

            if (!configValid)
            {
                Print("[INIT] *** CONFIGURATION INVALID *** Strategy will not process bars.");
                Print("[INIT] Fix parameters and re-enable.");
                return;
            }

            Print(string.Format("[INIT] EMAs: Fast={0}  Slow={1}", PeriodEMA_A, PeriodEMA_B));
            Print(string.Format("[INIT] Slope: Period={0}  ThresholdFast={1:F3}  ThresholdSlow={2:F3}",
                SlopePeriod, SlopeThresholdFast, SlopeThresholdSlow));
            if (SlopeThresholdFast <= 0 && SlopeThresholdSlow <= 0)
                Print("[INIT]   (both slope thresholds <= 0 — qualification falls back to raw cross only)");
            Print(string.Format("[INIT] TradeDirection: {0}", TradeDirection));
            Print(string.Format("[INIT] EventWindowBars: {0}", EventWindowBars));

            double pointValue = 0.0;
            try { pointValue = Instrument.MasterInstrument.PointValue; } catch { }
            Print(string.Format("[INIT] Stop: {0:F2} pt{1}",
                StopPoints,
                pointValue > 0 ? string.Format(" = ${0:F2} RISK/contract", StopPoints * pointValue) : ""));
            Print(string.Format("[INIT] Target: {0:F2} pt{1}",
                TargetPoints,
                pointValue > 0 ? string.Format(" = ${0:F2} REWARD/contract", TargetPoints * pointValue) : ""));
            Print(string.Format("[INIT] Risk:Reward = 1:{0:F2}", TargetPoints / StopPoints));
            Print(string.Format("[INIT] Trading hours: {0:D2}:{1:D2} - {2:D2}:{3:D2} NY",
                TradingStartHourNY, TradingStartMinuteNY, TradingEndHourNY, TradingEndMinuteNY));

            Print(string.Format("[INIT] AlertPattern (raw): \"{0}\"", AlertPattern));
            Print(string.Format("[INIT] AlertPattern (parsed, {0} pattern(s)): [{1}]",
                compiledAlertPatterns.Count, string.Join(", ", compiledAlertPatterns)));
            CheckForSuffixOverlaps();
            Print("[INIT] AlertPattern tip: enter \"0, 1\" to alert on EVERY qualified cross (no filter).");

            Print(string.Format("[INIT] Auto-plot EMAs: {0}", AutoPlotEMAs));
            Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));
            Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));

            Print("[INIT]");
            Print("[INIT] ---- 5 EXIT TRIGGERS FOR EACH PENDING TRADE ----");
            Print("[INIT]   1. Stop loss hit       (bit=0, RESOLUTION=STOP)");
            Print("[INIT]   2. Profit target hit   (bit=1, RESOLUTION=TARGET)");
            Print("[INIT]   3. Window expired      (bit by Close vs Entry, RESOLUTION=WINDOW_*)");
            Print("[INIT]   4. Hours end           (bit by Close vs Entry, RESOLUTION=HOURS_*)");
            Print("[INIT]   5. New qualified cross (bit by Close vs Entry, RESOLUTION=NEW_QEMA_*)");
            Print("[INIT] The new-cross killer closes the current trade and is CONSUMED;");
            Print("[INIT] it does NOT open a new trade on the same bar. Next qualified cross");
            Print("[INIT] (later) is the next entry candidate.");

            Print("[INIT]");
            Print("[INIT] ----- ORDER SUBSYSTEM -----");
            Print(string.Format("[INIT] EnableOrders = {0}", EnableOrders));
            if (!EnableOrders)
            {
                Print("[INIT]   ← alert-only mode, NO real orders will be placed.");
                Print("[INIT]   To enable trading: set EnableOrders=true in parameters.");
                Print("[INIT]   STRONGLY RECOMMENDED: run with EnableOrders=false for at least one full");
                Print("[INIT]   session on YOUR setup to validate alert behavior in CSV before enabling.");
            }
            else
            {
                Print(string.Format("[INIT] OrderQuantity       = {0} contract(s)", OrderQuantity));
                Print(string.Format("[INIT] OrderType           = {0}", OrderType));
                if (OrderType == OrderEntryType.Limit)
                {
                    Print(string.Format("[INIT] LimitOffsetPoints   = {0:F2} pt", LimitOffsetPoints));
                    Print(string.Format("[INIT] LimitUnfilledCancelBars = {0}", LimitUnfilledCancelBars));
                }
                Print(string.Format("[INIT] MaxConsecutiveLosses = {0}{1}",
                    MaxConsecutiveLosses,
                    MaxConsecutiveLosses >= 99 ? " (effectively OFF)" : " (safety brake ACTIVE)"));
                Print("[INIT]   [v1.0.2] Counter increments ONLY on post-alert captured outcomes");
                Print("[INIT]            (rows where CapturedPostAlert=YES). bit=0 increments, bit=1 resets.");
                Print("[INIT]            Qualified crosses BEFORE the first alert do NOT touch counter.");
                Print("[INIT]   [v1.0.2] Counter active in BOTH historical replay and realtime. A multi-day");
                Print("[INIT]            preload may trip the brake on historical sim-losses; this is the");
                Print("[INIT]            intended diagnostic UX — open the CSV, read RunningMaxSimConsLossesEver");
                Print("[INIT]            in the last row to see the worst historical sim-loss streak.");
                Print("[INIT]   [v1.0.2] When brake trips, strategy AUTO-DISABLES itself (visible in");
                Print("[INIT]            NT Strategies tab). Re-enable manually to reset.");
                Print("[INIT]   NOTE: brake reads SIMULATED outcomes (bit values). Real-time order P&L can");
                Print("[INIT]         differ slightly due to slippage. The brake's job is regime detection,");
                Print("[INIT]         not slippage-level accuracy. See header comments for details.");
                Print("[INIT] Safety layers active:");
                Print("[INIT]   Layer 1: trading-hours end force-flat");
                Print("[INIT]   Layer 2: NT session-close auto-flat (IsExitOnSessionCloseStrategy=true)");
                Print("[INIT]   Layer 3: broker server-side bracket (SetStopLoss/SetProfitTarget)");
                Print("[INIT]   Layer 4: EventWindowBars force-flat (consistency with would-trade math)");
                Print("[INIT]   Defensive: position-sync check every bar");
                Print("[INIT]   Defensive: market orders NEVER cancelled (heartbeat instead)");
                Print("[INIT] Order sounds: Entry=Alert4 Target=BoxingBell Stop=GlassBreak Cancel=Alert3 Flat=Alert2");
            }
            Print("[INIT] ---------------------------------");
            Print("================================================================");
        }

        // =====================================================================
        // OnBarUpdate — main entry point per bar close
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (!configValid) return;

            // DEFENSIVE POSITION-SYNC CHECK (only when orders enabled)
            if (EnableOrders)
                CheckPositionSync();

            // Daily reset check
            DateTime barTimeNy = TimeZoneInfo.ConvertTime(Time[0], nyTz);
            DailyResetCheckIfNeeded(barTimeNy);

            // Order subsystem bar-tick housekeeping (working state, heartbeat)
            if (EnableOrders)
                OrderSubsystemPerBarTick(barTimeNy);

            // ----- EVENT GENERATOR -----
            // 1. Update any pending trade (resolve stop/target/window/hours)
            UpdatePendingTradeIfAny(barTimeNy);

            // 2. Check for a new EMA cross on this bar (qualified or not)
            //    NOTE: a qualified cross during pending state will close the
            //    trade as the 5th exit trigger (killer cross), AFTER the
            //    stop/target/window/hours checks have had their priority pass.
            CheckForNewEMACross(barTimeNy);
        }

        // =====================================================================
        // DAILY RESET
        // =====================================================================
        private void DailyResetCheckIfNeeded(DateTime barTimeNy)
        {
            int minuteOfDayNy = barTimeNy.Hour * 60 + barTimeNy.Minute;
            int resetMin = RESET_HOUR_NY * 60 + RESET_MINUTE_NY;

            DateTime effectiveTradingDate;
            if (minuteOfDayNy >= resetMin)
                effectiveTradingDate = barTimeNy.Date;
            else
                effectiveTradingDate = barTimeNy.Date.AddDays(-1);

            if (effectiveTradingDate != currentTradingDateNy)
            {
                if (currentTradingDateNy != DateTime.MinValue)
                {
                    Print(string.Format("[RESET] New trading day at 9:30 NY ({0:yyyy-MM-dd}). Crosses: {1}, Qualified: {2}, Alerts: {3}, Orders: {4}",
                        effectiveTradingDate, dailyCrossNumber, dailyQualifiedNumber, dailyAlertNumber, dailyOrderNumber));
                }
                else
                {
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}", effectiveTradingDate));
                }
                currentTradingDateNy = effectiveTradingDate;
                ResetDailyState();
            }
        }

        private void ResetDailyState()
        {
            qualifiedEmaOutcomeString.Clear();
            postAlertOutcomeString.Clear();
            pendingPostAlertCaptures = 0;
            isAlerted = false;
            dailyCrossNumber = 0;
            dailyQualifiedNumber = 0;
            dailyAlertNumber = 0;
            dailyOrderNumber = 0;
            beepCount = 0;
            // Note: pendingState is NOT cleared on daily reset. If a cross
            // was started yesterday and is still resolving, let it finish.
            // Note: order subsystem state is NOT cleared on daily reset.
            // Note: consecutiveLosses, runningMaxSimConsLossesEver,
            //       safetyBrakeTripped persist across days.
        }

        // =====================================================================
        // ============================================================
        //   EVENT GENERATOR — QEMA-SPECIFIC CODE BEGINS HERE
        // ============================================================
        // =====================================================================

        // Excursion tracking helper — called every bar of pending state, also called
        // when a killer cross closes the trade so MaxFav/MaxAdv reflect the
        // entire pending window including the close bar.
        private void UpdatePendingExcursions()
        {
            if (pendingIsLong)
            {
                double favThisBar = High[0] - pendingEntry;
                double advThisBar = Low[0]  - pendingEntry;
                if (favThisBar > pendingMaxFavorable) pendingMaxFavorable = favThisBar;
                if (advThisBar < pendingMaxAdverse)   pendingMaxAdverse   = advThisBar;
            }
            else
            {
                // SHORT: favorable = move DOWN, adverse = move UP
                double favThisBar = pendingEntry - Low[0];
                double advThisBar = pendingEntry - High[0];
                if (favThisBar > pendingMaxFavorable) pendingMaxFavorable = favThisBar;
                if (advThisBar < pendingMaxAdverse)   pendingMaxAdverse   = advThisBar;
            }
        }

        // ---- 1. Resolve pending trade by stop/target/hours/window on this bar ----
        // Note: the 5th close trigger (new qualified cross = killer) is handled
        // in CheckForNewEMACross below, AFTER this method has had its priority pass.
        private void UpdatePendingTradeIfAny(DateTime barTimeNy)
        {
            if (pendingState != PendingState.Pending) return;

            pendingBarsWatched++;
            UpdatePendingExcursions();

            // Check STOP FIRST (conservative tied-bar rule)
            bool stopHit;
            if (pendingIsLong)
                stopHit = (Low[0] <= pendingStop);
            else
                stopHit = (High[0] >= pendingStop);

            if (stopHit)
            {
                ResolvePending(0, "STOP", pendingStop);
                return;
            }

            // Check TARGET
            bool targetHit;
            if (pendingIsLong)
                targetHit = (High[0] >= pendingTarget);
            else
                targetHit = (Low[0] <= pendingTarget);

            if (targetHit)
            {
                ResolvePending(1, "TARGET", pendingTarget);
                return;
            }

            // Check trading-hours boundary (force-resolve at hours end)
            if (IsBeyondTradingHours(barTimeNy))
            {
                int bit;
                string resType;
                if (pendingIsLong)
                {
                    bit = (Close[0] > pendingEntry) ? 1 : 0;
                    resType = bit == 1 ? "HOURS_WIN" : "HOURS_LOSS";
                }
                else
                {
                    bit = (Close[0] < pendingEntry) ? 1 : 0;
                    resType = bit == 1 ? "HOURS_WIN" : "HOURS_LOSS";
                }
                ResolvePending(bit, resType, Close[0]);
                return;
            }

            // Check window expiry
            if (pendingBarsWatched >= EventWindowBars)
            {
                int bit;
                string resType;
                if (pendingIsLong)
                {
                    bit = (Close[0] > pendingEntry) ? 1 : 0;
                    resType = bit == 1 ? "WINDOW_WIN" : "WINDOW_LOSS";
                }
                else
                {
                    bit = (Close[0] < pendingEntry) ? 1 : 0;
                    resType = bit == 1 ? "WINDOW_WIN" : "WINDOW_LOSS";
                }
                ResolvePending(bit, resType, Close[0]);
                return;
            }

            // Still pending; no stop/target/hours/window resolution this bar.
            // The new-cross killer check is in CheckForNewEMACross below.
        }

        // Resolve the current pending trade: append outcome bit, write CSV,
        // force-flat any live order, return to Free state.
        //
        // [v1.0.2] Note: consecutiveLosses bookkeeping is NO LONGER performed
        // here. It has moved to OnNewOutcomeBit (after the capturedPostAlert
        // decision has been made) so it only counts simulated real-order
        // outcomes — see Change 1 in the header.
        private void ResolvePending(int bit, string resolutionType, double resolutionPrice)
        {
            Print(string.Format("[QEMA] PENDING #{0} RESOLVED at {1} ({2}). Direction={3}, Entry={4:F2}, Resolution={5}@{6:F2}, Bars={7}, MaxFav={8:F2}, MaxAdv={9:F2}, Bit={10}",
                pendingDailyQualifiedNumber,
                Time[0].ToString("HH:mm:ss"),
                resolutionType,
                pendingIsLong ? "LONG" : "SHORT",
                pendingEntry,
                resolutionType,
                resolutionPrice,
                pendingBarsWatched,
                pendingMaxFavorable,
                pendingMaxAdverse,
                bit));

            // Snapshot resolution info for the CSV row
            DateTime resolutionTime = Time[0];

            // Hand off to the universal alert subsystem.
            // OnNewOutcomeBit will (a) decide capturedPostAlert,
            // (b) update consecutiveLosses iff captured, (c) trip brake iff
            // threshold reached, (d) write CSV row.
            OnNewOutcomeBit(
                bit: bit,
                isLong: pendingIsLong,
                entryPrice: pendingEntry,
                stopPrice: pendingStop,
                targetPrice: pendingTarget,
                crossTime: pendingCrossTime,
                resolutionTime: resolutionTime,
                barsToResolve: pendingBarsWatched,
                resolutionType: resolutionType,
                resolutionPrice: resolutionPrice,
                maxFavorable: pendingMaxFavorable,
                maxAdverse: pendingMaxAdverse,
                emaFastAtCross: pendingEmaFastAtCross,
                emaSlowAtCross: pendingEmaSlowAtCross,
                slopeFastAtCross: pendingSlopeFastAtCross,
                slopeSlowAtCross: pendingSlopeSlowAtCross,
                rawCrossNumber: pendingDailyCrossNumber,
                qualifiedNumber: pendingDailyQualifiedNumber,
                liveOrderNumber: pendingLiveOrderNumber);

            // Chart marker for outcome
            if (EnableChartMarkers && ShowOutcomeLabels)
            {
                string label;
                Brush color;
                if (bit == 1)
                {
                    if (resolutionType == "TARGET")
                    {
                        label = "W"; color = Brushes.LimeGreen;
                    }
                    else if (resolutionType == "NEW_QEMA_WIN")
                    {
                        label = "Nw"; color = Brushes.Aqua;
                    }
                    else
                    {
                        label = "Ww"; color = Brushes.LightGreen;
                    }
                }
                else
                {
                    if (resolutionType == "STOP")
                    {
                        label = "L"; color = Brushes.Red;
                    }
                    else if (resolutionType == "NEW_QEMA_LOSS")
                    {
                        label = "Nl"; color = Brushes.OrangeRed;
                    }
                    else
                    {
                        label = "Ll"; color = Brushes.DarkRed;
                    }
                }

                string tag = "QEMA_OUT_" + CurrentBar + "_" + pendingDailyQualifiedNumber;
                double yOffset = (bit == 1) ? (3 * TickSize) : -(3 * TickSize);
                double yPos = (bit == 1) ? (High[0] + yOffset) : (Low[0] + yOffset);
                Draw.Text(this, tag, label, 0, yPos, color);
            }

            // If a live order was placed for this trade and is still open, force-flat at market.
            // The bracket auto-cancels.
            if (EnableOrders && orderState == OrderSubState.Position && liveOrderEntryBarIdx >= 0)
            {
                bool isForceFlatResolution =
                       resolutionType == "WINDOW_WIN"   || resolutionType == "WINDOW_LOSS"
                    || resolutionType == "HOURS_WIN"    || resolutionType == "HOURS_LOSS"
                    || resolutionType == "NEW_QEMA_WIN" || resolutionType == "NEW_QEMA_LOSS";

                if (isForceFlatResolution)
                {
                    string forceFlatTag = (resolutionType.StartsWith("WINDOW_")) ? "EXIT_WINDOW_EXPIRED"
                                       : (resolutionType.StartsWith("HOURS_"))   ? "EXIT_HOURS_BOUNDARY"
                                                                                 : "EXIT_NEW_QEMA";
                    string exitName = (resolutionType.StartsWith("WINDOW_")) ? "ExitWindow"
                                    : (resolutionType.StartsWith("HOURS_"))  ? "ExitHours"
                                                                             : "ExitNewCross";
                    string exitNameShort = exitName + "Short";

                    Print(string.Format("[ORDER] Trade resolved as {0} with live order #{1} still open. Submitting market exit.",
                        resolutionType, entryOrderNumber));
                    WriteOrderRow(forceFlatTag, entryOrderNumber, "MARKET_EXIT",
                        actualFillPrice, computedStopPrice, computedTargetPrice, resolutionType);
                    PlayOrderSound("Alert2.wav", "Force-flat at " + resolutionType);
                    forceFlatInProgress = true;
                    try
                    {
                        if (entryIsLong)
                            ExitLong(OrderQuantity, exitName, "EntryQEMA");
                        else
                            ExitShort(OrderQuantity, exitNameShort, "EntryQEMAShort");
                    }
                    catch (Exception ex)
                    {
                        Print(string.Format("[ORDER] Force-flat exit error: {0}", ex.Message));
                    }
                }
            }

            // Clear pending state
            ClearPendingCross();
        }

        private void ClearPendingCross()
        {
            pendingState = PendingState.Free;
            pendingBarsWatched = 0;
            pendingMaxFavorable = 0;
            pendingMaxAdverse = 0;
            pendingLiveOrderNumber = 0;
            liveOrderEntryBarIdx = -1;
            liveOrderEventWindowBars = 0;
        }

        // ---- 2. Detect a new EMA cross on this bar ----
        private void CheckForNewEMACross(DateTime barTimeNy)
        {
            if (emaFastIndicator == null || emaSlowIndicator == null) return;
            if (CurrentBar < SlopePeriod + 1) return;

            // Read current EMA values
            double emaFastNow  = emaFastIndicator[0];
            double emaSlowNow  = emaSlowIndicator[0];
            double emaFastPrev = emaFastIndicator[1];
            double emaSlowPrev = emaSlowIndicator[1];

            bool rawGolden = (emaFastPrev <= emaSlowPrev) && (emaFastNow > emaSlowNow);
            bool rawDeath  = (emaFastPrev >= emaSlowPrev) && (emaFastNow < emaSlowNow);

            if (!rawGolden && !rawDeath) return;

            bool isLong = rawGolden;
            string direction = isLong ? "LONG" : "SHORT";

            dailyCrossNumber++;

            // Compute slopes
            double slopeFast = emaFastNow - emaFastIndicator[SlopePeriod];
            double slopeSlow = emaSlowNow - emaSlowIndicator[SlopePeriod];

            bool slopeQualifiedLong  = (slopeFast >  SlopeThresholdFast) && (slopeSlow >  SlopeThresholdSlow);
            bool slopeQualifiedShort = (slopeFast < -SlopeThresholdFast) && (slopeSlow < -SlopeThresholdSlow);
            bool slopeQualified = isLong ? slopeQualifiedLong : slopeQualifiedShort;

            // ----- UNQUALIFIED CROSS -----
            if (!slopeQualified)
            {
                Print(string.Format("[QEMA] {0} cross @ {1} SKIPPED_UNQUALIFIED  slopeFast={2:F3} (req {3:F3})  slopeSlow={4:F3} (req {5:F3})  Daily#{6}",
                    direction, Time[0].ToString("HH:mm:ss"),
                    slopeFast, isLong ? SlopeThresholdFast : -SlopeThresholdFast,
                    slopeSlow, isLong ? SlopeThresholdSlow : -SlopeThresholdSlow,
                    dailyCrossNumber));
                WriteSkippedCrossRow(barTimeNy, isLong, "SKIPPED_UNQUALIFIED",
                    emaFastNow, emaSlowNow, slopeFast, slopeSlow);
                return;
            }

            // ----- QUALIFIED CROSS — what role? -----
            // CASE A: Pending trade. This qualified cross is a KILLER.
            if (pendingState == PendingState.Pending)
            {
                Print(string.Format("[QEMA] {0} qualified cross @ {1} is KILLER for pending trade #{2}.  Closing trade.",
                    direction, Time[0].ToString("HH:mm:ss"), pendingDailyQualifiedNumber));

                int bit;
                string resType;
                if (pendingIsLong)
                {
                    bit = (Close[0] > pendingEntry) ? 1 : 0;
                    resType = bit == 1 ? "NEW_QEMA_WIN" : "NEW_QEMA_LOSS";
                }
                else
                {
                    bit = (Close[0] < pendingEntry) ? 1 : 0;
                    resType = bit == 1 ? "NEW_QEMA_WIN" : "NEW_QEMA_LOSS";
                }
                ResolvePending(bit, resType, Close[0]);
                return;
            }

            // CASE B: Free state. OPENER candidate.

            // Gate 1: Trading hours
            if (!IsWithinTradingHours(barTimeNy))
            {
                Print(string.Format("[QEMA] {0} qualified cross @ {1} SKIPPED_HOURS  Daily#{2}",
                    direction, Time[0].ToString("HH:mm:ss"), dailyCrossNumber));
                WriteSkippedCrossRow(barTimeNy, isLong, "SKIPPED_HOURS",
                    emaFastNow, emaSlowNow, slopeFast, slopeSlow);
                return;
            }

            // Gate 2: Direction filter
            if (TradeDirection == TradeDirectionMode.LongOnly && !isLong)
            {
                Print(string.Format("[QEMA] {0} qualified cross @ {1} SKIPPED_DIR (LongOnly)  Daily#{2}",
                    direction, Time[0].ToString("HH:mm:ss"), dailyCrossNumber));
                WriteSkippedCrossRow(barTimeNy, isLong, "SKIPPED_DIR",
                    emaFastNow, emaSlowNow, slopeFast, slopeSlow);
                return;
            }
            if (TradeDirection == TradeDirectionMode.ShortOnly && isLong)
            {
                Print(string.Format("[QEMA] {0} qualified cross @ {1} SKIPPED_DIR (ShortOnly)  Daily#{2}",
                    direction, Time[0].ToString("HH:mm:ss"), dailyCrossNumber));
                WriteSkippedCrossRow(barTimeNy, isLong, "SKIPPED_DIR",
                    emaFastNow, emaSlowNow, slopeFast, slopeSlow);
                return;
            }

            // Gate 3: External position
            if (HasAnyPositionOnInstrument())
            {
                Print(string.Format("[QEMA] {0} qualified cross @ {1} SKIPPED_POSITION (external position exists)  Daily#{2}",
                    direction, Time[0].ToString("HH:mm:ss"), dailyCrossNumber));
                WriteSkippedCrossRow(barTimeNy, isLong, "SKIPPED_POSITION",
                    emaFastNow, emaSlowNow, slopeFast, slopeSlow);
                return;
            }

            // ----- OPEN trade -----
            dailyQualifiedNumber++;
            pendingState        = PendingState.Pending;
            pendingIsLong       = isLong;
            pendingEntry        = Close[0];
            pendingStop         = isLong ? (pendingEntry - StopPoints) : (pendingEntry + StopPoints);
            pendingTarget       = isLong ? (pendingEntry + TargetPoints) : (pendingEntry - TargetPoints);
            pendingBarsWatched  = 0;
            pendingCrossTime    = Time[0];
            pendingMaxFavorable = 0;
            pendingMaxAdverse   = 0;
            pendingDailyCrossNumber = dailyCrossNumber;
            pendingDailyQualifiedNumber = dailyQualifiedNumber;
            pendingEmaFastAtCross = emaFastNow;
            pendingEmaSlowAtCross = emaSlowNow;
            pendingSlopeFastAtCross = slopeFast;
            pendingSlopeSlowAtCross = slopeSlow;
            pendingLiveOrderNumber = 0;

            Print(string.Format("[QEMA] *** QUALIFIED {0} CROSS OPENS TRADE *** at {1}  Entry={2:F2}  Stop={3:F2}  Target={4:F2}  slopeFast={5:F3}  slopeSlow={6:F3}  Daily Qualified#{7}",
                direction, Time[0].ToString("HH:mm:ss"),
                pendingEntry, pendingStop, pendingTarget,
                slopeFast, slopeSlow,
                dailyQualifiedNumber));

            // Chart marker for cross start
            if (EnableChartMarkers)
            {
                string tag = "QEMA_CROSS_" + CurrentBar + "_" + dailyQualifiedNumber;
                if (isLong)
                {
                    Draw.TriangleUp(this, tag, true, 0, Low[0] - (3 * TickSize), Brushes.Cyan);
                }
                else
                {
                    Draw.TriangleDown(this, tag, true, 0, High[0] + (3 * TickSize), Brushes.Yellow);
                }
            }

            // If orders are enabled AND isAlerted, place a live order on this qualified cross
            if (EnableOrders && isAlerted && !safetyBrakeTripped)
            {
                TryEnterLiveOrder(isLong, pendingEntry, barTimeNy);
            }
            else if (EnableOrders && safetyBrakeTripped)
            {
                Print(string.Format("[ORDER] Qualified QEMA cross detected but SAFETY BRAKE is tripped ({0} consecutive sim losses >= {1}). No order. Disable+re-enable to reset.",
                    consecutiveLosses, MaxConsecutiveLosses));
            }
            else if (EnableOrders && !isAlerted)
            {
                Print("[ORDER] Qualified QEMA cross detected but isAlerted=false. No live order. (Would-trade outcome will still be tracked.)");
            }
        }

        // =====================================================================
        // ============================================================
        //   EVENT GENERATOR — QEMA-SPECIFIC CODE ENDS HERE
        // ============================================================
        // =====================================================================


        // =====================================================================
        // ============================================================
        //   ALERT SUBSYSTEM — UNIVERSAL
        // ============================================================
        // =====================================================================

        // Called by event generator when a new outcome bit is produced.
        private void OnNewOutcomeBit(
            int bit, bool isLong,
            double entryPrice, double stopPrice, double targetPrice,
            DateTime crossTime, DateTime resolutionTime,
            int barsToResolve, string resolutionType, double resolutionPrice,
            double maxFavorable, double maxAdverse,
            double emaFastAtCross, double emaSlowAtCross,
            double slopeFastAtCross, double slopeSlowAtCross,
            int rawCrossNumber, int qualifiedNumber,
            int liveOrderNumber)
        {
            // 1. Append bit to QualifiedEMAOutcomeString
            qualifiedEmaOutcomeString.Append(bit == 1 ? '1' : '0');

            // Trim if exceeds MaxBitsKept
            if (qualifiedEmaOutcomeString.Length > MaxBitsKept)
            {
                int removeCount = qualifiedEmaOutcomeString.Length - MaxBitsKept;
                qualifiedEmaOutcomeString.Remove(0, removeCount);
            }

            Print(string.Format("[ALERT-SUB] Bit appended: {0}. QualifiedEMAOutcomeString = \"{1}\"",
                bit, qualifiedEmaOutcomeString.ToString()));

            // 2. Post-alert capture FIRST (so a new alert on this same bit doesn't capture itself)
            bool capturedPostAlert = false;
            char capturedBit = '\0';
            if (pendingPostAlertCaptures > 0)
            {
                capturedBit = bit == 1 ? '1' : '0';
                postAlertOutcomeString.Append(capturedBit);
                pendingPostAlertCaptures--;
                capturedPostAlert = true;
                Print(string.Format("[POSTALERT] Captured outcome bit '{0}'. PostAlertOutcomeString = \"{1}\". Pending = {2}.",
                    capturedBit, postAlertOutcomeString.ToString(), pendingPostAlertCaptures));
            }

            // 3. [v1.0.2] BRAKE COUNTER — only on captured (simulated real-order) outcomes.
            //    Active regardless of EnableOrders so that historical replay is diagnostic.
            //    bit=0 + captured -> increment.  bit=1 + captured -> reset.
            //    Non-captured rows leave consecutiveLosses unchanged.
            if (capturedPostAlert)
            {
                if (bit == 0)
                {
                    consecutiveLosses++;
                    if (consecutiveLosses > runningMaxSimConsLossesEver)
                        runningMaxSimConsLossesEver = consecutiveLosses;

                    Print(string.Format("[BRAKE] Sim loss recorded ({0}). Consecutive sim losses = {1} of {2}. RunningMaxEver = {3}.",
                        resolutionType, consecutiveLosses, MaxConsecutiveLosses, runningMaxSimConsLossesEver));

                    if (consecutiveLosses >= MaxConsecutiveLosses && !safetyBrakeTripped)
                    {
                        safetyBrakeTripped = true;
                        Print(string.Format("[CRITICAL] *** SAFETY BRAKE TRIPPED *** {0} consecutive sim losses >= {1}. STRATEGY AUTO-DISABLING. Re-enable manually to reset.",
                            consecutiveLosses, MaxConsecutiveLosses));
                        Print(string.Format("[CRITICAL] State at trip: {0}. Open scalper_QEMA_occ.csv and look for RunningMaxSimConsLossesEver = {1} near this timestamp.",
                            State == State.Historical ? "HISTORICAL REPLAY" : "REALTIME",
                            runningMaxSimConsLossesEver));
                        WriteOrderRow("SAFETY_BRAKE_TRIPPED", liveOrderNumber, "n/a", 0, 0, 0,
                            string.Format("consecutive_sim_losses={0} reason={1} state={2}",
                                consecutiveLosses, resolutionType,
                                State == State.Historical ? "historical" : "realtime"));
                        try { PlayOrderSound("Alert2.wav", "Safety brake tripped — strategy auto-disabling"); } catch { }
                        DisableStrategyDueToSafetyBrake();
                    }
                }
                else
                {
                    if (consecutiveLosses > 0)
                        Print(string.Format("[BRAKE] Sim win ({0}) resets consecutive sim losses (was {1}). RunningMaxEver remains {2}.",
                            resolutionType, consecutiveLosses, runningMaxSimConsLossesEver));
                    consecutiveLosses = 0;
                }
            }

            // 4. Check for alert: does ANY pattern in the list match the tail?
            bool firedAlert = false;
            if (AnyAlertPatternMatchesTail())
            {
                FireAlert();
                firedAlert = true;
            }

            // 5. Recompute isAlerted
            bool prev = isAlerted;
            isAlerted = AnyAlertPatternMatchesTail();
            if (prev != isAlerted)
                Print(string.Format("[ALERTSTATE] isAlerted: {0} -> {1}", prev, isAlerted ? "YES" : "no"));

            // 6. Write CSV row for this QUALIFIED cross
            WriteQualifiedCrossRow(
                crossTime: crossTime,
                isLong: isLong,
                eventType: "QUALIFIED",
                emaFastValue: emaFastAtCross,
                emaSlowValue: emaSlowAtCross,
                slopeFast: slopeFastAtCross,
                slopeSlow: slopeSlowAtCross,
                entryPrice: entryPrice,
                stopPrice: stopPrice,
                targetPrice: targetPrice,
                resolutionTime: resolutionTime,
                barsToResolve: barsToResolve,
                resolutionType: resolutionType,
                resolutionPrice: resolutionPrice,
                maxFavorable: maxFavorable,
                maxAdverse: maxAdverse,
                outcomeBit: bit,
                rawCrossNumber: rawCrossNumber,
                qualifiedNumber: qualifiedNumber,
                firedAlert: firedAlert,
                capturedPostAlert: capturedPostAlert,
                capturedBit: capturedBit,
                liveOrderNumber: liveOrderNumber);
        }

        private bool AnyAlertPatternMatchesTail()
        {
            string tail = qualifiedEmaOutcomeString.ToString();
            foreach (var p in compiledAlertPatterns)
            {
                if (tail.Length < p.Length) continue;
                bool match = true;
                int start = tail.Length - p.Length;
                for (int i = 0; i < p.Length; i++)
                {
                    if (tail[start + i] != p[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        private void FireAlert()
        {
            dailyAlertNumber++;
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern list matched the tail at {0}. Daily alert #{1}. Pending captures = {2}. ***",
                Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] Tail (last 20): \"...{0}\"",
                qualifiedEmaOutcomeString.Length > 20
                    ? qualifiedEmaOutcomeString.ToString().Substring(qualifiedEmaOutcomeString.Length - 20)
                    : qualifiedEmaOutcomeString.ToString()));
            Print("[ALERT] Watch the chart. Next qualified MACD cross is your trial.");

            try { PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav"); } catch { }
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "MACD_ALERT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "MACD_ALERT_TXT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Text(this, txt, string.Format("ALERT #{0}\nAP=\"{1}\"", dailyAlertNumber, AlertPattern),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow();
        }

        // =====================================================================
        // ============================================================
        //   ALERT SUBSYSTEM — END
        // ============================================================
        // =====================================================================


        // =====================================================================
        // ============================================================
        //   ORDER SUBSYSTEM — UNIVERSAL
        // ============================================================
        // =====================================================================

        private void TryEnterLiveOrder(bool isLong, double currentPrice, DateTime barTimeNy)
        {
            if (orderState != OrderSubState.Idle)
            {
                Print(string.Format("[ORDER] Qualified cross detected but orderState={0} (not Idle). No new order.", orderState));
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[ORDER] Qualified cross detected but strategy Position={0} (not Flat). No order.",
                    Position.MarketPosition));
                return;
            }

            dailyOrderNumber++;
            entryOrderNumber = dailyOrderNumber;
            lastEntryOrderNumber = entryOrderNumber;
            pendingLiveOrderNumber = entryOrderNumber;
            entryReferencePrice = currentPrice;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx = CurrentBar;
            currentWorkingOrderType = OrderType;
            cancelRequested = false;
            marketTimeoutLogged = false;
            entryIsLong = isLong;
            liveOrderEntryBarIdx = CurrentBar;
            liveOrderEventWindowBars = EventWindowBars;

            int stopTicks   = (int)Math.Round(StopPoints   / TickSize);
            int targetTicks = (int)Math.Round(TargetPoints / TickSize);
            string entrySignal = isLong ? "EntryQEMA" : "EntryQEMAShort";

            try
            {
                SetStopLoss(entrySignal, CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget(entrySignal, CalculationMode.Ticks, targetTicks);
                Print(string.Format("[ORDER] Pre-set OCO bracket: stop={0} ticks, target={1} ticks (server-side).",
                    stopTicks, targetTicks));
            }
            catch (Exception ex)
            {
                Print(string.Format("[CRITICAL] SetStopLoss/SetProfitTarget failed: {0}. ABORTING ENTRY.", ex.Message));
                return;
            }

            if (OrderType == OrderEntryType.Market)
            {
                Print(string.Format("[ORDER] *** SUBMIT MARKET {0} *** qty={1}, ref={2:F2}. Order #{3}.",
                    isLong ? "BUY" : "SELL", OrderQuantity, currentPrice, entryOrderNumber));
                WriteOrderRow("SUBMIT_MARKET", entryOrderNumber, "MARKET", 0, 0, 0, isLong ? "LONG" : "SHORT");
                orderState = OrderSubState.Working;
                bricksInCurrentState = 0;
                marketSubmitWallClock = DateTime.Now;
                try
                {
                    if (isLong)
                        workingEntryOrder = EnterLong(OrderQuantity, entrySignal);
                    else
                        workingEntryOrder = EnterShort(OrderQuantity, entrySignal);
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] Enter{0} (market) error: {1}", isLong ? "Long" : "Short", ex.Message));
                    orderState = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
            else
            {
                double limitPrice = isLong
                    ? (currentPrice - LimitOffsetPoints)
                    : (currentPrice + LimitOffsetPoints);
                limitPrice = Instrument.MasterInstrument.RoundToTickSize(limitPrice);
                entryLimitPrice = limitPrice;

                Print(string.Format("[ORDER] *** SUBMIT LIMIT {0} *** qty={1}, ref={2:F2}, limit={3:F2}. Order #{4}.",
                    isLong ? "BUY" : "SELL", OrderQuantity, currentPrice, limitPrice, entryOrderNumber));
                WriteOrderRow("SUBMIT_LIMIT", entryOrderNumber, "LIMIT", limitPrice, 0, 0, isLong ? "LONG" : "SHORT");
                orderState = OrderSubState.Working;
                bricksInCurrentState = 0;
                try
                {
                    if (isLong)
                        workingEntryOrder = EnterLongLimit(0, true, OrderQuantity, limitPrice, entrySignal);
                    else
                        workingEntryOrder = EnterShortLimit(0, true, OrderQuantity, limitPrice, entrySignal);
                }
                catch (Exception ex)
                {
                    Print(string.Format("[ORDER] Enter{0}Limit error: {1}", isLong ? "Long" : "Short", ex.Message));
                    orderState = OrderSubState.Idle;
                    workingEntryOrder = null;
                }
            }
        }

        private void OrderSubsystemPerBarTick(DateTime barTimeNy)
        {
            if (orderState == OrderSubState.Working && workingEntryOrder != null)
            {
                bricksSinceEntrySubmit++;

                if (currentWorkingOrderType == OrderEntryType.Limit)
                {
                    if (bricksSinceEntrySubmit >= LimitUnfilledCancelBars && !cancelRequested)
                    {
                        Print(string.Format("[ORDER] LIMIT entry #{0} unfilled after {1} bar(s). Cancelling.",
                            entryOrderNumber, LimitUnfilledCancelBars));
                        WriteOrderRow("CANCEL_UNFILLED", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                        cancelRequested = true;
                        try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                        catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                    }
                    else if (cancelRequested && bricksSinceEntrySubmit >= LimitUnfilledCancelBars + 2)
                    {
                        Print(string.Format("[ORDER] *** LIMIT GRACE PERIOD EXPIRED *** Entry #{0}. Verifying broker position.",
                            entryOrderNumber));
                        WriteOrderRow("FORCE_RESET_STUCK", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "callback_never_arrived");
                        if (Position.MarketPosition == MarketPosition.Flat)
                        {
                            ResetOrderSubsystemToIdle("force_reset_stuck_limit_broker_flat");
                        }
                        else
                        {
                            Print(string.Format("[CRITICAL] LIMIT grace-period expired but Position={0}. Force-flattening.",
                                Position.MarketPosition));
                            WriteOrderRow("DESYNC_FORCE_FLAT", entryOrderNumber, "n/a", 0, 0, 0, "limit_grace_broker_not_flat");
                            try
                            {
                                if (Position.MarketPosition == MarketPosition.Long)
                                    ExitLong(Math.Abs(Position.Quantity), "ExitDesync", "EntryQEMA");
                                else if (Position.MarketPosition == MarketPosition.Short)
                                    ExitShort(Math.Abs(Position.Quantity), "ExitDesyncShort", "EntryQEMAShort");
                            }
                            catch (Exception ex)
                            {
                                Print(string.Format("[CRITICAL] ExitLong/Short (desync) failed: {0}", ex.Message));
                            }
                        }
                    }
                }
                else
                {
                    TimeSpan elapsed = DateTime.Now - marketSubmitWallClock;
                    if (elapsed.TotalSeconds > 30 && !marketTimeoutLogged)
                    {
                        Print(string.Format("[CRITICAL] MARKET order #{0} no fill confirmation after {1:F1}s. NOT cancelling.",
                            entryOrderNumber, elapsed.TotalSeconds));
                        WriteOrderRow("MARKET_HEARTBEAT_WARN", entryOrderNumber, "MARKET", 0, 0, 0,
                            string.Format("elapsed_seconds={0:F1}", elapsed.TotalSeconds));
                        marketTimeoutLogged = true;
                    }
                }
            }

            if (orderState != OrderSubState.Idle)
            {
                bricksInCurrentState++;
                if (bricksInCurrentState % 10 == 0)
                {
                    Print(string.Format("[HEARTBEAT] orderState={0} for {1} bars. workingEntryOrder={2}, brokerPosition={3}.",
                        orderState, bricksInCurrentState,
                        workingEntryOrder == null ? "null" : "set",
                        Position.MarketPosition));
                }
            }
            else
            {
                bricksInCurrentState = 0;
            }

            if (!forceFlatInProgress && IsBeyondTradingHours(barTimeNy))
            {
                if (orderState == OrderSubState.Working)
                {
                    Print(string.Format("[ORDER] Trading-hours end reached while order WORKING. Cancelling entry #{0}.", entryOrderNumber));
                    WriteOrderRow("CANCEL_HOURS", entryOrderNumber, "n/a", entryLimitPrice, 0, 0, "");
                    PlayOrderSound("Alert2.wav", "Force-flat hours-end (cancel working entry)");
                    forceFlatInProgress = true;
                    try { if (workingEntryOrder != null) CancelOrder(workingEntryOrder); }
                    catch (Exception ex) { Print(string.Format("[ORDER] CancelOrder error: {0}", ex.Message)); }
                }
                else if (orderState == OrderSubState.Position)
                {
                    Print(string.Format("[ORDER] Trading-hours end reached while in POSITION. Submitting market exit."));
                    WriteOrderRow("FORCEFLAT_HOURS", entryOrderNumber, "MARKET_EXIT",
                        actualFillPrice, computedStopPrice, computedTargetPrice, "");
                    PlayOrderSound("Alert2.wav", "Force-flat hours-end (exit position)");
                    forceFlatInProgress = true;
                    try
                    {
                        if (entryIsLong)
                            ExitLong(OrderQuantity, "ExitHours", "EntryQEMA");
                        else
                            ExitShort(OrderQuantity, "ExitHoursShort", "EntryQEMAShort");
                    }
                    catch (Exception ex) { Print(string.Format("[ORDER] ExitLong/Short (hours) error: {0}", ex.Message)); }
                }
                else if (orderState == OrderSubState.Idle && Position.MarketPosition != MarketPosition.Flat)
                {
                    Print(string.Format("[CRITICAL] Hours-end reached: orderState=Idle but Position={0}. Force-flatten.",
                        Position.MarketPosition));
                    WriteOrderRow("FORCEFLAT_HOURS_GHOST", lastEntryOrderNumber, "MARKET_EXIT", 0, 0, 0, "ghost_at_hours_end");
                    PlayOrderSound("Alert2.wav", "Force-flat ghost position at hours-end");
                    forceFlatInProgress = true;
                    try
                    {
                        if (Position.MarketPosition == MarketPosition.Long)
                            ExitLong(Math.Abs(Position.Quantity), "ExitHoursGhost", "EntryQEMA");
                        else if (Position.MarketPosition == MarketPosition.Short)
                            ExitShort(Math.Abs(Position.Quantity), "ExitHoursGhostShort", "EntryQEMAShort");
                    }
                    catch (Exception ex) { Print(string.Format("[CRITICAL] Ghost hours-end exit failed: {0}", ex.Message)); }
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderUpdateState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            if (!EnableOrders) return;

            bool isOurEntry = (workingEntryOrder != null && order == workingEntryOrder)
                              || ((order.Name == "EntryQEMA" || order.Name == "EntryQEMAShort")
                                  && orderState == OrderSubState.Working);

            if (isOurEntry)
            {
                Print(string.Format("[ORDER] OnOrderUpdate: entry #{0}, name={1}, state={2}.",
                    entryOrderNumber, order.Name, orderUpdateState));

                if (orderUpdateState == NinjaTrader.Cbi.OrderState.Cancelled)
                {
                    if (!forceFlatInProgress)
                        PlayOrderSound("Alert3.wav", "Entry cancelled (unfilled)");
                    workingEntryOrder = null;
                    ResetOrderSubsystemToIdle("entry_cancelled_callback");
                }
                else if (orderUpdateState == NinjaTrader.Cbi.OrderState.Rejected)
                {
                    Print(string.Format("[ORDER] Entry #{0} REJECTED: {1}", entryOrderNumber, error));
                    WriteOrderRow("REJECTED", entryOrderNumber, order.OrderType.ToString(),
                        limitPrice, 0, 0, error.ToString());
                    workingEntryOrder = null;
                    ResetOrderSubsystemToIdle("entry_rejected");
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (!EnableOrders) return;
            if (execution == null || execution.Order == null) return;

            string oName = execution.Order.Name ?? "";

            if ((oName == "EntryQEMA" || oName == "EntryQEMAShort")
                && (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                 || execution.Order.OrderState == NinjaTrader.Cbi.OrderState.PartFilled))
            {
                actualFillPrice = price;
                Print(string.Format("[ORDER] *** ENTRY FILLED *** order #{0} qty={1} @ {2:F2} (state={3})",
                    entryOrderNumber, quantity, price, execution.Order.OrderState));

                if (execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled)
                {
                    PlayOrderSound("Alert4.wav", "Entry filled");
                    if (entryIsLong)
                    {
                        computedStopPrice   = actualFillPrice - StopPoints;
                        computedTargetPrice = actualFillPrice + TargetPoints;
                    }
                    else
                    {
                        computedStopPrice   = actualFillPrice + StopPoints;
                        computedTargetPrice = actualFillPrice - TargetPoints;
                    }
                    computedStopPrice   = Instrument.MasterInstrument.RoundToTickSize(computedStopPrice);
                    computedTargetPrice = Instrument.MasterInstrument.RoundToTickSize(computedTargetPrice);
                    Print(string.Format("[ORDER] Bracket auto-attached: STOP ~{0:F2}, TARGET ~{1:F2}.",
                        computedStopPrice, computedTargetPrice));
                    WriteOrderRow("FILLED_BRACKET_ATTACH", entryOrderNumber, "n/a",
                        actualFillPrice, computedStopPrice, computedTargetPrice, entryIsLong ? "LONG" : "SHORT");
                    orderState = OrderSubState.Position;
                    bricksInCurrentState = 0;
                    workingEntryOrder = null;
                    marketTimeoutLogged = false;
                }
                return;
            }

            bool isStopFill   = oName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isTargetFill = oName.IndexOf("Profit", StringComparison.OrdinalIgnoreCase) >= 0
                             || oName.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isStopFill || isTargetFill)
                && execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                && orderState == OrderSubState.Position)
            {
                string which = isStopFill ? "STOP" : "TARGET";
                double pnl = entryIsLong ? (price - actualFillPrice) : (actualFillPrice - price);
                Print(string.Format("[ORDER] *** {0} HIT *** order #{1} ({2}) exit @ {3:F2}. Entry={4:F2}. PnL/contract={5:+0.00;-0.00} pt.",
                    which, entryOrderNumber, oName, price, actualFillPrice, pnl));
                WriteOrderRow(isStopFill ? "EXIT_STOP" : "EXIT_TARGET", entryOrderNumber, "n/a",
                    0, computedStopPrice, computedTargetPrice, entryIsLong ? "LONG" : "SHORT");

                if (isStopFill)
                {
                    PlayOrderSound("Glass Break.wav", "Stop loss hit");
                    // [v1.0.2] consecutiveLosses is now updated in OnNewOutcomeBit on
                    // capturedPostAlert==true rows, not here. Sound still fires at the
                    // actual moment the broker confirms the stop fill.
                }
                else
                {
                    PlayOrderSound("Boxing Bell.wav", "Profit target hit");
                }

                ResetOrderSubsystemToIdle(isStopFill ? "stop_hit" : "target_hit");
                return;
            }

            bool isOurForceFlat =
                   oName == "ExitWindow"            || oName == "ExitWindowShort"
                || oName == "ExitHours"             || oName == "ExitHoursShort"
                || oName == "ExitHoursGhost"        || oName == "ExitHoursGhostShort"
                || oName == "ExitDesync"            || oName == "ExitDesyncShort"
                || oName == "ExitNewCross"          || oName == "ExitNewCrossShort";

            if (isOurForceFlat
                && execution.Order.OrderState == NinjaTrader.Cbi.OrderState.Filled
                && orderState != OrderSubState.Idle)
            {
                double pnl = entryIsLong ? (price - actualFillPrice) : (actualFillPrice - price);
                Print(string.Format("[ORDER] *** FORCE-FLAT FILLED *** order #{0} ({1}) exit @ {2:F2}. Entry={3:F2}. PnL/contract={4:+0.00;-0.00} pt.",
                    entryOrderNumber, oName, price, actualFillPrice, pnl));
                WriteOrderRow("EXIT_FORCE_FLAT_FILLED", entryOrderNumber, "n/a",
                    price, computedStopPrice, computedTargetPrice, oName);

                ResetOrderSubsystemToIdle("force_flat_filled_" + oName);
            }
        }

        private void CheckPositionSync()
        {
            try
            {
                MarketPosition brokerPos = Position.MarketPosition;

                if (orderState == OrderSubState.Idle && brokerPos != MarketPosition.Flat)
                {
                    bool shouldLog = (DateTime.Now - lastDesyncLogTime).TotalSeconds > 30;
                    if (shouldLog)
                    {
                        Print(string.Format("[CRITICAL] *** DESYNC *** orderState=Idle but broker Position={0} (qty={1}). Force-flatten.",
                            brokerPos, Position.Quantity));
                        WriteOrderRow("DESYNC_FORCE_FLAT", lastEntryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("orderState=Idle, brokerPosition={0}", brokerPos));
                        PlayOrderSound("Alert2.wav", "Desync ghost position detected");
                        lastDesyncLogTime = DateTime.Now;
                    }

                    try
                    {
                        if (brokerPos == MarketPosition.Long)
                            ExitLong(Math.Abs(Position.Quantity), "ExitDesync", "EntryQEMA");
                        else if (brokerPos == MarketPosition.Short)
                            ExitShort(Math.Abs(Position.Quantity), "ExitDesyncShort", "EntryQEMAShort");
                    }
                    catch (Exception ex)
                    {
                        if (shouldLog)
                            Print(string.Format("[CRITICAL] Desync force-flat failed: {0}", ex.Message));
                    }
                }
                else if ((orderState == OrderSubState.Working || orderState == OrderSubState.Position)
                         && brokerPos == MarketPosition.Flat)
                {
                    if ((DateTime.Now - lastDesyncLogTime).TotalSeconds > 5)
                    {
                        Print(string.Format("[CRITICAL] *** DESYNC *** orderState={0} but broker Position=Flat. Resetting to Idle.",
                            orderState));
                        WriteOrderRow("DESYNC_RESET_IDLE", lastEntryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("orderState={0}, brokerPosition=Flat", orderState));
                        lastDesyncLogTime = DateTime.Now;
                        ResetOrderSubsystemToIdle("desync_broker_flat");
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[ORDER] CheckPositionSync error: {0}", ex.Message));
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
        {
            if (connectionStatusUpdate == null) return;

            ConnectionStatus status = connectionStatusUpdate.Status;
            Print(string.Format("[CONN] Connection status update: {0}", status));

            if (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Disconnecting)
            {
                Print("[CRITICAL] *** BROKER CONNECTION LOST *** Server-side bracket should still protect open positions, but no new orders can be placed.");
                try { PlayOrderSound("Alert2.wav", "Broker connection lost"); } catch { }
            }
            else if (status == ConnectionStatus.Connected)
            {
                Print("[CONN] Broker connection (re)established.");
            }
        }

        private void ResetOrderSubsystemToIdle(string reason)
        {
            Print(string.Format("[ORDER] *** STATE -> IDLE *** (reason: {0}).", reason));
            orderState = OrderSubState.Idle;
            workingEntryOrder = null;
            bricksSinceEntrySubmit = 0;
            entrySubmitBarIdx = -1;
            entryReferencePrice = 0.0;
            entryLimitPrice = 0.0;
            actualFillPrice = 0.0;
            computedStopPrice = 0.0;
            computedTargetPrice = 0.0;
            forceFlatInProgress = false;
            cancelRequested = false;
            bricksInCurrentState = 0;
            currentWorkingOrderType = OrderEntryType.Market;
            marketSubmitWallClock = DateTime.MinValue;
            marketTimeoutLogged = false;
            liveOrderEntryBarIdx = -1;
            liveOrderEventWindowBars = 0;
            // NOTE: consecutiveLosses, runningMaxSimConsLossesEver, and
            // safetyBrakeTripped persist.
        }

        // [v1.0.1] Auto-disable the strategy after the safety brake trips.
        // [v1.0.2] Now reachable during historical replay too.
        private void DisableStrategyDueToSafetyBrake()
        {
            Print(string.Format("[DISABLE] Triggering strategy disable at {0}",
                DateTime.Now.ToString("HH:mm:ss.fff")));
            try
            {
                TriggerCustomEvent(o =>
                {
                    try { SetState(State.Finalized); }
                    catch (Exception ex)
                    {
                        Print(string.Format("[DISABLE] SetState(Finalized) error: {0}", ex.Message));
                    }
                }, null);
                Print("[DISABLE] Strategy disable scheduled. Check NT Strategies tab — strategy should appear disabled shortly.");
            }
            catch (Exception ex)
            {
                Print(string.Format("[DISABLE] TriggerCustomEvent error: {0}", ex.Message));
            }
        }

        // =====================================================================
        // ============================================================
        //   ORDER SUBSYSTEM — END
        // ============================================================
        // =====================================================================


        // =====================================================================
        // HELPERS
        // =====================================================================

        private bool IsWithinTradingHours(DateTime nyTime)
        {
            int curMin   = nyTime.Hour * 60 + nyTime.Minute;
            int startMin = TradingStartHourNY * 60 + TradingStartMinuteNY;
            int endMin   = TradingEndHourNY   * 60 + TradingEndMinuteNY;
            if (startMin <= endMin)
                return curMin >= startMin && curMin <= endMin;
            return curMin >= startMin || curMin <= endMin;
        }

        private bool IsBeyondTradingHours(DateTime nyTime)
        {
            int curMin   = nyTime.Hour * 60 + nyTime.Minute;
            int startMin = TradingStartHourNY * 60 + TradingStartMinuteNY;
            int endMin   = TradingEndHourNY   * 60 + TradingEndMinuteNY;
            if (startMin <= endMin)
                return curMin > endMin || curMin < startMin;
            return curMin > endMin && curMin < startMin;
        }

        private bool HasAnyPositionOnInstrument()
        {
            try
            {
                if (Account == null) return false;
                lock (Account.Positions)
                {
                    foreach (var pos in Account.Positions)
                    {
                        if (pos.Instrument == Instrument && pos.MarketPosition != MarketPosition.Flat)
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[HELPER] HasAnyPositionOnInstrument error: {0}. Treating as no position.", ex.Message));
            }
            return false;
        }

        private void PlayOrderSound(string wavFileName, string eventLabel)
        {
            try
            {
                string fullPath = NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + wavFileName;
                Print(string.Format("[SOUND] {0} -> playing {1}", eventLabel, wavFileName));
                PlaySound(fullPath);
            }
            catch (Exception ex)
            {
                Print(string.Format("[SOUND] ERROR playing {0}: {1}", wavFileName, ex.Message));
            }
        }

        // =====================================================================
        // CSV WRITERS
        // =====================================================================

        private void WriteCommonOccHeader(StreamWriter sw)
        {
            sw.WriteLine("# schema_version=1.0.2");
            sw.WriteLine(string.Format("# file_created_NY={0}",
                TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
            sw.WriteLine(string.Format("# file_created_UTC={0}",
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
            sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
            sw.WriteLine(string.Format("# bar_type={0} {1}", BarsPeriod.Value, BarsPeriod.BarsPeriodType));
            sw.WriteLine(string.Format("# tick_size={0}", TickSize));
            sw.WriteLine(string.Format("# PeriodEMA_A={0}", PeriodEMA_A));
            sw.WriteLine(string.Format("# PeriodEMA_B={0}", PeriodEMA_B));
            sw.WriteLine(string.Format("# SlopePeriod={0}", SlopePeriod));
            sw.WriteLine(string.Format("# SlopeThresholdFast={0}", SlopeThresholdFast));
            sw.WriteLine(string.Format("# SlopeThresholdSlow={0}", SlopeThresholdSlow));
            sw.WriteLine(string.Format("# TradeDirection={0}", TradeDirection));
            sw.WriteLine(string.Format("# StopPoints={0}", StopPoints));
            sw.WriteLine(string.Format("# TargetPoints={0}", TargetPoints));
            sw.WriteLine(string.Format("# EventWindowBars={0}", EventWindowBars));
            sw.WriteLine(string.Format("# TradingHours_NY={0:D2}:{1:D2}-{2:D2}:{3:D2}",
                TradingStartHourNY, TradingStartMinuteNY, TradingEndHourNY, TradingEndMinuteNY));
            sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
            sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
            sw.WriteLine(string.Format("# EnableOrders={0}", EnableOrders));
            sw.WriteLine(string.Format("# MaxConsecutiveLosses={0}", MaxConsecutiveLosses));
            sw.WriteLine("# encoding: SUCCESS=1, FAILURE=0");
            sw.WriteLine("# all timestamps NY time");
            sw.WriteLine("#");
            sw.WriteLine("# [v1.0.2] BRAKE COLUMNS (new):");
            sw.WriteLine("#   SimConsecutiveLossesAfter   - consecutive sim-order losses after this row");
            sw.WriteLine("#                                 (only changes on CapturedPostAlert=YES rows;");
            sw.WriteLine("#                                  bit=0 increments, bit=1 resets to 0)");
            sw.WriteLine("#   RunningMaxSimConsLossesEver - all-time peak of SimConsecutiveLossesAfter");
            sw.WriteLine("#                                 up to and including this row. READ THE LAST");
            sw.WriteLine("#                                 ROW to see the worst sim-loss streak ever.");
            sw.WriteLine("# WARNING: brake counts SIMULATED outcomes (bit values). Live order P&L may");
            sw.WriteLine("#          differ slightly due to slippage. See strategy header comments.");
            sw.WriteLine("#");
        }

        private void WriteOccCsvHeaderRow(StreamWriter sw)
        {
            sw.WriteLine("CrossTime_NY,Direction,EventType,"
                + "EmaFastValue,EmaSlowValue,SlopeFast,SlopeSlow,"
                + "EntryPrice,StopPrice,TargetPrice,"
                + "ResolutionTime_NY,BarsToResolve,ResolutionType,ResolutionPrice,"
                + "MaxFavorable,MaxAdverse,OutcomeBit,"
                + "DailyCrossNumber,DailyQualifiedNumber,"
                + "AlertPattern,QualifiedEMAOutcomeString,"
                + "FiredAlertOnThisRow,CapturedPostAlert,CapturedBit,"
                + "PostAlertOutcomeString,PendingPostAlertCapturesAfter,"
                + "IsAlertedAfter,OrderStateAfter,LiveOrderNumber,"
                + "SimConsecutiveLossesAfter,RunningMaxSimConsLossesEver");
        }

        private void WriteQualifiedCrossRow(
            DateTime crossTime, bool isLong, string eventType,
            double emaFastValue, double emaSlowValue, double slopeFast, double slopeSlow,
            double entryPrice, double stopPrice, double targetPrice,
            DateTime resolutionTime, int barsToResolve, string resolutionType, double resolutionPrice,
            double maxFavorable, double maxAdverse, int outcomeBit,
            int rawCrossNumber, int qualifiedNumber,
            bool firedAlert, bool capturedPostAlert, char capturedBit,
            int liveOrderNumber)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_QEMA_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        WriteCommonOccHeader(sw);
                        WriteOccCsvHeaderRow(sw);
                    }

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3:F4},{4:F4},{5:F4},{6:F4},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13:F2},{14:F2},{15:F2},{16},{17},{18},{19},{20},{21},{22},{23},{24},{25},{26},{27},{28},{29},{30}",
                        TimeZoneInfo.ConvertTime(crossTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        isLong ? "LONG" : "SHORT",
                        eventType,
                        emaFastValue,
                        emaSlowValue,
                        slopeFast,
                        slopeSlow,
                        entryPrice,
                        stopPrice,
                        targetPrice,
                        TimeZoneInfo.ConvertTime(resolutionTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        barsToResolve,
                        resolutionType,
                        resolutionPrice,
                        maxFavorable,
                        maxAdverse,
                        outcomeBit,
                        rawCrossNumber,
                        qualifiedNumber,
                        apForCsv,
                        qualifiedEmaOutcomeString.ToString(),
                        firedAlert ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",
                        capturedPostAlert ? capturedBit.ToString() : "",
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState,
                        liveOrderNumber > 0 ? liveOrderNumber.ToString() : "",
                        consecutiveLosses,
                        runningMaxSimConsLossesEver));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-OCC] ERROR: {0}", ex.Message));
            }
        }

        // Write a row for a SKIPPED cross (gate failed OR unqualified)
        private void WriteSkippedCrossRow(DateTime crossTime, bool isLong, string eventType,
            double emaFastValue, double emaSlowValue, double slopeFast, double slopeSlow)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_QEMA_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        WriteCommonOccHeader(sw);
                        WriteOccCsvHeaderRow(sw);
                    }

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    // SKIPPED rows: cross-detection info only; resolution/outcome columns blank.
                    // Two trailing brake columns ALWAYS present (carry forward unchanged values).
                    // Column layout (31 fields):
                    //  1-7   CrossTime, Direction, EventType, Ema*, Slope*
                    //  8-10  EntryPrice, StopPrice, TargetPrice  (blank)
                    //  11-14 ResolutionTime, Bars, ResType, ResPrice  (blank)
                    //  15-17 MaxFav, MaxAdv, OutcomeBit  (blank)
                    //  18-19 DailyCross#, DailyQualified#
                    //  20-21 AlertPattern, QualifiedEMAOutcomeString
                    //  22-24 FiredAlert(NO), CapturedPostAlert(NO), CapturedBit(blank)
                    //  25-26 PostAlertOutcomeString, PendingPostAlertCapturesAfter
                    //  27-29 IsAlertedAfter, OrderStateAfter, LiveOrderNumber(blank)
                    //  30-31 SimConsecutiveLossesAfter, RunningMaxSimConsLossesEver
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3:F4},{4:F4},{5:F4},{6:F4},,,,,,,,,,,{7},{8},{9},{10},NO,NO,,{11},{12},{13},{14},,{15},{16}",
                        TimeZoneInfo.ConvertTime(crossTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        isLong ? "LONG" : "SHORT",
                        eventType,
                        emaFastValue,
                        emaSlowValue,
                        slopeFast,
                        slopeSlow,
                        dailyCrossNumber,
                        dailyQualifiedNumber,
                        apForCsv,
                        qualifiedEmaOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState,
                        consecutiveLosses,
                        runningMaxSimConsLossesEver));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-OCC] ERROR: {0}", ex.Message));
            }
        }

        private void WriteAlertRow()
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_QEMA_alert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.0.2");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("AlertTime_NY,DailyAlertNumber,AlertPattern,CurrentPrice,"
                            + "QualifiedEMAOutcomeString,PostAlertOutcomeString,"
                            + "PendingPostAlertCapturesAfter,IsAlertedAfter,OrderStateAtAlert,"
                            + "SimConsecutiveLossesAtAlert,RunningMaxSimConsLossesEver");
                    }

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3:F2},{4},{5},{6},{7},{8},{9},{10}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        apForCsv,
                        Close[0],
                        qualifiedEmaOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState,
                        consecutiveLosses,
                        runningMaxSimConsLossesEver));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ALERT] ERROR: {0}", ex.Message));
            }
        }

        private void WriteOrderRow(string eventType, int orderNumber, string orderTypeStr,
            double price, double stopPx, double targetPx, string extra)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_QEMA_orders.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.0.2");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# StopPoints={0}", StopPoints));
                        sw.WriteLine(string.Format("# TargetPoints={0}", TargetPoints));
                        sw.WriteLine(string.Format("# LimitOffsetPoints={0}", LimitOffsetPoints));
                        sw.WriteLine(string.Format("# LimitUnfilledCancelBars={0}", LimitUnfilledCancelBars));
                        sw.WriteLine(string.Format("# EventWindowBars={0}", EventWindowBars));
                        sw.WriteLine(string.Format("# MaxConsecutiveLosses={0}", MaxConsecutiveLosses));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("# Event types:");
                        sw.WriteLine("#   SUBMIT_MARKET / SUBMIT_LIMIT   - entry submitted");
                        sw.WriteLine("#   FILLED_BRACKET_ATTACH          - entry filled, server-side bracket active");
                        sw.WriteLine("#   EXIT_STOP / EXIT_TARGET        - bracket leg filled");
                        sw.WriteLine("#   EXIT_WINDOW_EXPIRED            - EventWindowBars timeout, market exit submitted");
                        sw.WriteLine("#   EXIT_HOURS_BOUNDARY            - Trading-hours end mid-trade, market exit submitted");
                        sw.WriteLine("#   EXIT_NEW_QEMA                  - NEW qualified cross fired during pending, market exit submitted (5th close trigger)");
                        sw.WriteLine("#   EXIT_FORCE_FLAT_FILLED         - force-flat exit market-fill confirmed (window/hours/new-cross/desync). State -> Idle.");
                        sw.WriteLine("#   CANCEL_UNFILLED                - limit entry cancelled (bars elapsed)");
                        sw.WriteLine("#   CANCEL_HOURS                   - entry cancelled at trading-hours end");
                        sw.WriteLine("#   FORCEFLAT_HOURS                - position closed at trading-hours end (order-subsystem path)");
                        sw.WriteLine("#   FORCEFLAT_HOURS_GHOST          - ghost position closed at hours end");
                        sw.WriteLine("#   FORCE_RESET_STUCK              - limit grace expired, callback never arrived");
                        sw.WriteLine("#   DESYNC_FORCE_FLAT              - state=Idle but broker has position");
                        sw.WriteLine("#   DESYNC_RESET_IDLE              - state=Working/Position but broker Flat");
                        sw.WriteLine("#   MARKET_HEARTBEAT_WARN          - market order no fill confirm after 30s");
                        sw.WriteLine("#   SAFETY_BRAKE_TRIPPED           - [v1.0.2] consecutive SIM-loss limit reached; strategy auto-disables");
                        sw.WriteLine("#   REJECTED                       - entry order rejected");
                        sw.WriteLine("#");
                        sw.WriteLine("EventTime_NY,EventType,EntryOrderNumber,OrderTypeStr,Price,StopPrice,TargetPrice,OrderState,SimConsecutiveLosses,RunningMaxSimConsLossesEver,Extra");
                    }
                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8},{9},{10}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        eventType,
                        orderNumber,
                        orderTypeStr,
                        price,
                        stopPx,
                        targetPx,
                        orderState,
                        consecutiveLosses,
                        runningMaxSimConsLossesEver,
                        extra));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ORDER] ERROR: {0}", ex.Message));
            }
        }

        #region Properties

        // ===== Group 1: EMA Source =====

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "EMA A (Fast) Period",
            Description = "Fast EMA period. Default 8. Must typically be smaller than Slow.",
            Order = 1, GroupName = "1. EMA Source")]
        public int PeriodEMA_A { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "EMA B (Slow) Period",
            Description = "Slow EMA period. Default 30.",
            Order = 2, GroupName = "1. EMA Source")]
        public int PeriodEMA_B { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Slope Period (bars)",
            Description = "Bars to look back to compute slope: slope = EMA[0] - EMA[SlopePeriod]. Default 5.",
            Order = 3, GroupName = "1. EMA Source")]
        public int SlopePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Slope Threshold (Fast) — POINTS",
            Description = "Min absolute slope of Fast EMA (in price points) required to qualify a cross. Default 0.25.",
            Order = 4, GroupName = "1. EMA Source")]
        public double SlopeThresholdFast { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Slope Threshold (Slow) — POINTS",
            Description = "Min absolute slope of Slow EMA (in price points) required to qualify a cross. Default 0.05.",
            Order = 5, GroupName = "1. EMA Source")]
        public double SlopeThresholdSlow { get; set; }

        // ===== Group 2: Direction & Outcome =====

        [NinjaScriptProperty]
        [Display(Name = "Trade Direction",
            Description = "Which crosses produce events. Both = golden→long, death→short. LongOnly = golden only. ShortOnly = death only.",
            Order = 1, GroupName = "2. Direction & Outcome")]
        public TradeDirectionMode TradeDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Event Window (BARS)",
            Description = "Bars after cross detection to watch for stop/target. Window ends at bar N+EventWindowBars. Used by both would-trade math AND live-order force-flat.",
            Order = 2, GroupName = "2. Direction & Outcome")]
        public int EventWindowBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 500.0)]
        [Display(Name = "Stop Loss (POINTS)",
            Description = "Stop distance in POINTS from entry. Default 20.",
            Order = 3, GroupName = "2. Direction & Outcome")]
        public double StopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 500.0)]
        [Display(Name = "Profit Target (POINTS)",
            Description = "Profit target distance in POINTS from entry. Default 20.",
            Order = 4, GroupName = "2. Direction & Outcome")]
        public double TargetPoints { get; set; }

        // ===== Group 3: Trading Hours (NY) =====

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Trading Start HOUR (NY)",
            Description = "Trading window start hour (NY time). Default 9.",
            Order = 1, GroupName = "3. Trading Hours (NY)")]
        public int TradingStartHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Trading Start MINUTE (NY)",
            Description = "Trading window start minute (NY time). Default 30.",
            Order = 2, GroupName = "3. Trading Hours (NY)")]
        public int TradingStartMinuteNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Trading End HOUR (NY)",
            Description = "Trading window end hour (NY time). Default 16.",
            Order = 3, GroupName = "3. Trading Hours (NY)")]
        public int TradingEndHourNY { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Trading End MINUTE (NY)",
            Description = "Trading window end minute (NY time). Default 0. At/after this time: position force-flat at market, working orders cancelled.",
            Order = 4, GroupName = "3. Trading Hours (NY)")]
        public int TradingEndMinuteNY { get; set; }

        // ===== Group 4: Alert Subsystem =====

        [NinjaScriptProperty]
        [Display(Name = "Alert Pattern (comma-separated list OK)",
            Description = "[v1.0.0] One or more suffix patterns, comma-separated. Each entry 1-10 chars of '0'/'1'. At each qualified MACD outcome append, if ANY pattern matches the tail, ONE alert fires (sound + diamond + CSV row), and isAlerted becomes true (next qualified MACD will trigger a real order when EnableOrders=true). Examples: \"01\" (single), \"01, 011, 0111\" (any of three), \"0, 1\" (always match — trade EVERY qualified MACD signal). WARNING: avoid lists where one entry is a suffix of another (e.g. \"1, 11\" or \"101, 10101\") — the shorter is redundant. Strategy still runs but prints a warning.",
            Order = 1, GroupName = "4. Alert Subsystem")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Alert Sound Count",
            Description = "Total beeps per alert. Default 3.",
            Order = 2, GroupName = "4. Alert Subsystem")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Alert Reminder Secs",
            Description = "Seconds between beeps. Default 1.",
            Order = 3, GroupName = "4. Alert Subsystem")]
        public int AlertReminderSecs { get; set; }

        // ===== Group 5: Order Execution =====

        [NinjaScriptProperty]
        [Display(Name = "Enable Orders",
            Description = "Master switch. When OFF (default), strategy runs alert subsystem only — NO real orders. When ON, the order subsystem activates and live orders are placed on qualified MACD crosses after an alert is armed. STRONGLY recommend running OFF for at least one full session to validate alert behavior in CSV first.",
            Order = 1, GroupName = "5. Order Execution")]
        public bool EnableOrders { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Order Quantity (CONTRACTS)",
            Description = "Contracts per entry. Default 1.",
            Order = 2, GroupName = "5. Order Execution")]
        public int OrderQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Order Type (Market or Limit)",
            Description = "Market = immediate fill (may slip). Limit = LimitOffsetPoints below close (long) or above (short).",
            Order = 3, GroupName = "5. Order Execution")]
        public OrderEntryType OrderType { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Limit Offset (POINTS)",
            Description = "(LIMIT only) offset from close. Default 5.0. Ignored if Market.",
            Order = 4, GroupName = "5. Order Execution")]
        public double LimitOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Limit Unfilled Cancel (BARS)",
            Description = "(LIMIT only) cancel after this many bars unfilled. Default 1. Market orders never cancelled.",
            Order = 5, GroupName = "5. Order Execution")]
        public int LimitUnfilledCancelBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Consecutive Losses (Halt)",
            Description = "[v1.0.2] Halt new orders after this many SIM-ORDER losses in a row. The brake counts only post-alert captured outcomes (the trades the order subsystem would actually take), NOT all qualified crosses. Counter active in both historical and realtime. Default 3 (conservative). Set to 99 to effectively disable. Disable+re-enable strategy to reset. To find your historical worst streak: open scalper_QEMA_occ.csv and read RunningMaxSimConsLossesEver in the last row.",
            Order = 6, GroupName = "5. Order Execution")]
        public int MaxConsecutiveLosses { get; set; }

        // ===== Group 6: Visuals =====

        [NinjaScriptProperty]
        [Display(Name = "Enable Chart Markers",
            Description = "Master toggle for all chart drawings.",
            Order = 1, GroupName = "6. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Outcome Labels",
            Description = "Show W/L text at resolution bar.",
            Order = 2, GroupName = "6. Visuals")]
        public bool ShowOutcomeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto-plot EMAs",
            Description = "Automatically add Fast and Slow EMA indicators to the price panel using strategy's parameters. If you also load the QualifiedEMA_CrossOver indicator on the chart, both will agree because they use the same math.",
            Order = 3, GroupName = "6. Visuals")]
        public bool AutoPlotEMAs { get; set; }

        // ===== Group 7: Logging =====

        [NinjaScriptProperty]
        [Display(Name = "Audit Log Path",
            Description = "Folder for CSV files (auto-created). Default C:\\temp.",
            Order = 1, GroupName = "7. Logging")]
        public string AuditLogPath { get; set; }

        // ===== Group 8: Advanced =====

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name = "Max Bits Kept",
            Description = "Max in-memory QualifiedEMAOutcomeString length before trimming oldest. Default 5000.",
            Order = 1, GroupName = "8. Advanced")]
        public int MaxBitsKept { get; set; }

        #endregion
    }
}
