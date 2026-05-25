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
// STRATEGY: scalper_MACD_Order v1.0.0
// AUTHOR:   Albert Feng / Drafted with help from Claude
// =============================================================================
//
// Single-file unified MACD strategy with three subsystems:
//   1. Event Generator (MACD-specific)
//   2. Alert Subsystem (UNIVERSAL — copy/paste-able from Renko v1.4.4)
//   3. Order Subsystem (UNIVERSAL — copy/paste-able from Renko v1.4.3)
//
// EnableOrders=false (default) → alert-only mode, identical to a "phase 1"
//                                 alert subsystem. No real trades.
// EnableOrders=true            → alert + order subsystem active.
//
// To adapt to another indicator (RSI, VWAP, etc.):
//   - Copy this file
//   - Replace the EVENT GENERATOR section
//   - Leave ALERT SUBSYSTEM and ORDER SUBSYSTEM unchanged
//   - Rename class, Name property, CSV file prefixes
//
// See spec doc for full design: scalper_MACD_Order_v100_spec.md
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_MACD_Order : Strategy
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
        private StringBuilder qualifiedMacdOutcomeString = new StringBuilder();
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
        // EVENT GENERATOR STATE (MACD-specific)
        // =====================================================================
        private MACD macdIndicator;
        private ATR  atrIndicator;

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
        private double pendingMacdValueAtCross;
        private double pendingSignalValueAtCross;
        private double pendingAtrAtCross;
        private double pendingDiffOverAtr;

        // Live order linkage (only meaningful when EnableOrders=true)
        private int pendingLiveOrderNumber = 0;

        // Previous-bar MACD/Signal values for cross detection
        // (Pulled from indicator history each bar; no need to cache manually
        //  if indicator provides .Default[1] and .Avg[1].)

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
        private int consecutiveLosses = 0;
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
                Description = "MACD signal-line cross strategy with universal alert + order subsystems. EnableOrders=false (default) runs alert-only. Supports long/short/both, ATR confirmation filter, event-window outcome math, comma-separated AlertPattern list. Mirrors Renko v1.4.x architecture; only event generator differs.";
                Name        = "scalper_MACD_Order";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = true;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- MACD Source defaults ----
                MACDFast          = 12;
                MACDSlow          = 26;
                MACDSignal        = 9;
                ATRPeriod         = 14;
                CrossToleranceATR = 0.0;            // pure MACD out of box

                // ---- Direction & Outcome defaults ----
                TradeDirection    = TradeDirectionMode.Both;
                EventWindowBars   = 3;
                StopPoints        = 20.0;
                TargetPoints      = 20.0;

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
                AutoPlotMACD       = true;
                AutoPlotATR        = true;

                // ---- Logging defaults ----
                AuditLogPath = @"C:\temp";

                // ---- Advanced defaults ----
                MaxBitsKept = 5000;
            }
            else if (State == State.Configure)
            {
                // Ensure we have enough bars for MACD slow EMA + signal to be valid
                BarsRequiredToTrade = Math.Max(BarsRequiredToTrade, MACDSlow + MACDSignal + 1);
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

                macdIndicator = MACD(MACDFast, MACDSlow, MACDSignal);
                atrIndicator  = ATR(ATRPeriod);

                if (AutoPlotMACD)
                    AddChartIndicator(macdIndicator);
                if (AutoPlotATR)
                    AddChartIndicator(atrIndicator);

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
            Print(string.Format("[INIT] scalper_MACD_Order v1.0.0 at {0}",
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

            Print(string.Format("[INIT] MACD: Fast={0} Slow={1} Signal={2}", MACDFast, MACDSlow, MACDSignal));
            Print(string.Format("[INIT] ATR: Period={0}  CrossToleranceATR={1:F3}  ({2})",
                ATRPeriod, CrossToleranceATR,
                CrossToleranceATR <= 0 ? "0 = pure MACD, no ATR filter" : "ATR filter active"));
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
            Print("[INIT] AlertPattern tip: enter \"0, 1\" to alert on EVERY qualified MACD signal (no filter).");

            Print(string.Format("[INIT] Auto-plot: MACD={0}  ATR={1}", AutoPlotMACD, AutoPlotATR));
            Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));
            Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));

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
            // 1. Update any pending cross (resolve stop/target/window/hours)
            UpdatePendingCrossIfAny(barTimeNy);

            // 2. Check for a new MACD cross on this bar
            CheckForNewMACDCross(barTimeNy);
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
            qualifiedMacdOutcomeString.Clear();
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
            // Note: consecutiveLosses, safetyBrakeTripped persist across days.
        }

        // =====================================================================
        // ============================================================
        //   EVENT GENERATOR — MACD-SPECIFIC CODE BEGINS HERE
        //   To adapt this strategy to another indicator (RSI, VWAP, etc.):
        //     1. Replace methods CheckForNewMACDCross, UpdatePendingCrossIfAny,
        //        and the indicator initialization in State.DataLoaded.
        //     2. Keep the same contract: call OnNewOutcomeBit(bit, ...) when an
        //        event resolves; call TryEnterLiveOrderIfArmed(...) when a new
        //        event starts and orders should fire.
        //     3. Leave ALERT SUBSYSTEM and ORDER SUBSYSTEM sections unchanged.
        //     4. Update class name, Name property, CSV file prefixes.
        // ============================================================
        // =====================================================================

        // ---- 1. Resolve pending cross on this bar (if any) ----
        private void UpdatePendingCrossIfAny(DateTime barTimeNy)
        {
            if (pendingState != PendingState.Pending) return;

            pendingBarsWatched++;

            // Update max favorable/adverse excursion
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

            // Still pending; nothing to do this bar
        }

        // Resolve the current pending cross: append outcome bit, write CSV, return to Free.
        private void ResolvePending(int bit, string resolutionType, double resolutionPrice)
        {
            Print(string.Format("[MACD] PENDING #{0} RESOLVED at {1} ({2}). Direction={3}, Entry={4:F2}, Resolution={5}@{6:F2}, Bars={7}, MaxFav={8:F2}, MaxAdv={9:F2}, Bit={10}",
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

            // Hand off to the universal alert subsystem
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
                macdValueAtCross: pendingMacdValueAtCross,
                signalValueAtCross: pendingSignalValueAtCross,
                atrAtCross: pendingAtrAtCross,
                diffOverAtr: pendingDiffOverAtr,
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
                    label = (resolutionType == "TARGET") ? "W" : "Ww";
                    color = (resolutionType == "TARGET") ? Brushes.LimeGreen : Brushes.LightGreen;
                }
                else
                {
                    label = (resolutionType == "STOP") ? "L" : "Ll";
                    color = (resolutionType == "STOP") ? Brushes.Red : Brushes.DarkRed;
                }

                string tag = "MACD_OUT_" + CurrentBar + "_" + pendingDailyQualifiedNumber;
                double yOffset = (bit == 1) ? (3 * TickSize) : -(3 * TickSize);
                double yPos = (bit == 1) ? (High[0] + yOffset) : (Low[0] + yOffset);
                Draw.Text(this, tag, label, 0, yPos, color);
            }

            // If a live order was placed for this event and is still open at window expiry,
            // submit a market exit (force-flat). The bracket will auto-cancel.
            if (EnableOrders && orderState == OrderSubState.Position && liveOrderEntryBarIdx >= 0)
            {
                if (resolutionType == "WINDOW_WIN" || resolutionType == "WINDOW_LOSS"
                 || resolutionType == "HOURS_WIN"  || resolutionType == "HOURS_LOSS")
                {
                    Print(string.Format("[ORDER] Event window expired with live order #{0} still open. Submitting market exit.",
                        entryOrderNumber));
                    WriteOrderRow("EXIT_WINDOW_EXPIRED", entryOrderNumber, "MARKET_EXIT",
                        actualFillPrice, computedStopPrice, computedTargetPrice, resolutionType);
                    PlayOrderSound("Alert2.wav", "Force-flat at event window expiry");
                    forceFlatInProgress = true;
                    try
                    {
                        if (entryIsLong)
                            ExitLong(OrderQuantity, "ExitWindow", "EntryMACD");
                        else
                            ExitShort(OrderQuantity, "ExitWindowShort", "EntryMACDShort");
                    }
                    catch (Exception ex)
                    {
                        Print(string.Format("[ORDER] Window exit error: {0}", ex.Message));
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

        // ---- 2. Detect a new MACD cross on this bar ----
        private void CheckForNewMACDCross(DateTime barTimeNy)
        {
            if (macdIndicator == null || atrIndicator == null) return;
            if (CurrentBar < 1) return;

            double macdNow  = macdIndicator.Default[0];
            double macdPrev = macdIndicator.Default[1];
            double sigNow   = macdIndicator.Avg[0];
            double sigPrev  = macdIndicator.Avg[1];

            bool rawGolden = (macdPrev <= sigPrev) && (macdNow > sigNow);
            bool rawDeath  = (macdPrev >= sigPrev) && (macdNow < sigNow);

            if (!rawGolden && !rawDeath) return;

            bool isLong = rawGolden;
            string direction = isLong ? "LONG" : "SHORT";

            dailyCrossNumber++;

            // Compute ATR confirmation
            double diff = isLong ? (macdNow - sigNow) : (sigNow - macdNow);
            double atrNow = atrIndicator[0];
            double diffOverAtr = atrNow > 0 ? diff / atrNow : 0;

            // ----- GATE CHECKS -----
            string skipReason = null;

            // Gate 1: ATR confirmation
            if (CrossToleranceATR > 0 && diffOverAtr <= CrossToleranceATR)
                skipReason = "SKIPPED_ATR";

            // Gate 2: Trading hours
            if (skipReason == null && !IsWithinTradingHours(barTimeNy))
                skipReason = "SKIPPED_HOURS";

            // Gate 3: Direction filter
            if (skipReason == null)
            {
                if (TradeDirection == TradeDirectionMode.LongOnly && !isLong)
                    skipReason = "SKIPPED_DIR";
                else if (TradeDirection == TradeDirectionMode.ShortOnly && isLong)
                    skipReason = "SKIPPED_DIR";
            }

            // Gate 4: External position
            if (skipReason == null && HasAnyPositionOnInstrument())
                skipReason = "SKIPPED_POSITION";

            // Gate 5: Busy with another pending cross
            if (skipReason == null && pendingState == PendingState.Pending)
                skipReason = "SKIPPED_BUSY";

            // ----- ROUTING -----
            if (skipReason != null)
            {
                Print(string.Format("[MACD] {0} cross @ {1} {2} (Diff/ATR={3:F3})  Daily#{4}",
                    direction, Time[0].ToString("HH:mm:ss"), skipReason, diffOverAtr, dailyCrossNumber));
                WriteSkippedCrossRow(barTimeNy, isLong, skipReason, macdNow, sigNow, atrNow, diffOverAtr);
                return;
            }

            // ----- QUALIFIED — start pending state -----
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
            pendingMacdValueAtCross = macdNow;
            pendingSignalValueAtCross = sigNow;
            pendingAtrAtCross = atrNow;
            pendingDiffOverAtr = diffOverAtr;
            pendingLiveOrderNumber = 0;

            Print(string.Format("[MACD] *** QUALIFIED {0} CROSS *** at {1}  Entry={2:F2}  Stop={3:F2}  Target={4:F2}  Diff/ATR={5:F3}  Daily Qualified#{6}",
                direction, Time[0].ToString("HH:mm:ss"),
                pendingEntry, pendingStop, pendingTarget, diffOverAtr,
                dailyQualifiedNumber));

            // Chart marker for cross start
            if (EnableChartMarkers)
            {
                string tag = "MACD_CROSS_" + CurrentBar + "_" + dailyQualifiedNumber;
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
                Print(string.Format("[ORDER] Qualified MACD cross detected but SAFETY BRAKE is tripped ({0} consecutive losses >= {1}). No order. Disable+re-enable to reset.",
                    consecutiveLosses, MaxConsecutiveLosses));
            }
            else if (EnableOrders && !isAlerted)
            {
                Print("[ORDER] Qualified MACD cross detected but isAlerted=false. No live order. (Would-trade outcome will still be tracked.)");
            }
        }

        // =====================================================================
        // ============================================================
        //   EVENT GENERATOR — MACD-SPECIFIC CODE ENDS HERE
        // ============================================================
        // =====================================================================


        // =====================================================================
        // ============================================================
        //   ALERT SUBSYSTEM — UNIVERSAL, COPY/PASTE-ABLE
        //   (identical to Renko v1.4.4 alert subsystem)
        // ============================================================
        // =====================================================================

        // Called by event generator when a new outcome bit is produced.
        private void OnNewOutcomeBit(
            int bit, bool isLong,
            double entryPrice, double stopPrice, double targetPrice,
            DateTime crossTime, DateTime resolutionTime,
            int barsToResolve, string resolutionType, double resolutionPrice,
            double maxFavorable, double maxAdverse,
            double macdValueAtCross, double signalValueAtCross,
            double atrAtCross, double diffOverAtr,
            int rawCrossNumber, int qualifiedNumber,
            int liveOrderNumber)
        {
            // 1. Append bit to QualifiedMACDOutcomeString
            qualifiedMacdOutcomeString.Append(bit == 1 ? '1' : '0');

            // Trim if exceeds MaxBitsKept
            if (qualifiedMacdOutcomeString.Length > MaxBitsKept)
            {
                int removeCount = qualifiedMacdOutcomeString.Length - MaxBitsKept;
                qualifiedMacdOutcomeString.Remove(0, removeCount);
            }

            Print(string.Format("[ALERT-SUB] Bit appended: {0}. QualifiedMACDOutcomeString = \"{1}\"",
                bit, qualifiedMacdOutcomeString.ToString()));

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

            // 3. Check for alert: does ANY pattern in the list match the tail?
            bool firedAlert = false;
            if (AnyAlertPatternMatchesTail())
            {
                FireAlert();
                firedAlert = true;
            }

            // 4. Recompute isAlerted (used by event generator to gate live orders)
            bool prev = isAlerted;
            isAlerted = AnyAlertPatternMatchesTail();
            if (prev != isAlerted)
                Print(string.Format("[ALERTSTATE] isAlerted: {0} -> {1}", prev, isAlerted ? "YES" : "no"));

            // 5. Write CSV row for this QUALIFIED cross
            WriteQualifiedCrossRow(
                crossTime: crossTime,
                isLong: isLong,
                eventType: "QUALIFIED",
                macdValue: macdValueAtCross,
                signalValue: signalValueAtCross,
                atrValue: atrAtCross,
                diffOverAtr: diffOverAtr,
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

        // Returns true iff ANY pattern in compiledAlertPatterns is a suffix of QualifiedMACDOutcomeString.
        private bool AnyAlertPatternMatchesTail()
        {
            string tail = qualifiedMacdOutcomeString.ToString();
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

        // FireAlert — sound, diamond, CSV row, counter increment
        private void FireAlert()
        {
            dailyAlertNumber++;
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern list matched the tail at {0}. Daily alert #{1}. Pending captures = {2}. ***",
                Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] Tail (last 20): \"...{0}\"",
                qualifiedMacdOutcomeString.Length > 20
                    ? qualifiedMacdOutcomeString.ToString().Substring(qualifiedMacdOutcomeString.Length - 20)
                    : qualifiedMacdOutcomeString.ToString()));
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
        //   ORDER SUBSYSTEM — UNIVERSAL, COPY/PASTE-ABLE
        //   (adapted from Renko v1.4.3 — Bricks → Points substitution)
        // ============================================================
        // =====================================================================

        // Called by event generator when a qualified cross occurs AND isAlerted is true.
        private void TryEnterLiveOrder(bool isLong, double currentPrice, DateTime barTimeNy)
        {
            // Gate: order subsystem must be Idle
            if (orderState != OrderSubState.Idle)
            {
                Print(string.Format("[ORDER] Qualified cross detected but orderState={0} (not Idle). No new order.", orderState));
                return;
            }

            // Gate: strategy position must be flat
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                Print(string.Format("[ORDER] Qualified cross detected but strategy Position={0} (not Flat). No order.",
                    Position.MarketPosition));
                return;
            }

            // All gates passed. Submit entry order.
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

            // Pre-set OCO bracket via SetStopLoss / SetProfitTarget (server-side)
            // Use Ticks mode (NT computes from fill price).
            int stopTicks   = (int)Math.Round(StopPoints   / TickSize);
            int targetTicks = (int)Math.Round(TargetPoints / TickSize);
            string entrySignal = isLong ? "EntryMACD" : "EntryMACDShort";

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

        // Per-bar tick housekeeping for working/position states
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
                                    ExitLong(Math.Abs(Position.Quantity), "ExitDesync", "EntryMACD");
                                else if (Position.MarketPosition == MarketPosition.Short)
                                    ExitShort(Math.Abs(Position.Quantity), "ExitDesyncShort", "EntryMACDShort");
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
                    // Market: wall-clock heartbeat, NEVER cancel
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

            // Heartbeat for non-idle states
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

            // Trading-hours end force-flat
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
                            ExitLong(OrderQuantity, "ExitHours", "EntryMACD");
                        else
                            ExitShort(OrderQuantity, "ExitHoursShort", "EntryMACDShort");
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
                            ExitLong(Math.Abs(Position.Quantity), "ExitHoursGhost", "EntryMACD");
                        else if (Position.MarketPosition == MarketPosition.Short)
                            ExitShort(Math.Abs(Position.Quantity), "ExitHoursGhostShort", "EntryMACDShort");
                    }
                    catch (Exception ex) { Print(string.Format("[CRITICAL] Ghost hours-end exit failed: {0}", ex.Message)); }
                }
            }
        }

        // OnOrderUpdate — entry order lifecycle
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderUpdateState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            if (!EnableOrders) return;

            bool isOurEntry = (workingEntryOrder != null && order == workingEntryOrder)
                              || ((order.Name == "EntryMACD" || order.Name == "EntryMACDShort")
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

        // OnExecutionUpdate — entry fill and exit fills
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (!EnableOrders) return;
            if (execution == null || execution.Order == null) return;

            string oName = execution.Order.Name ?? "";

            // Entry fills
            if ((oName == "EntryMACD" || oName == "EntryMACDShort")
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

            // Exit fills — identified by NT default bracket names
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
                    consecutiveLosses++;
                    PlayOrderSound("Glass Break.wav", "Stop loss hit");
                    Print(string.Format("[ORDER] Consecutive losses now = {0} of {1}.", consecutiveLosses, MaxConsecutiveLosses));
                    if (consecutiveLosses >= MaxConsecutiveLosses && !safetyBrakeTripped)
                    {
                        safetyBrakeTripped = true;
                        Print(string.Format("[CRITICAL] *** SAFETY BRAKE TRIPPED *** {0} consecutive losses >= {1}. ALL FURTHER ORDERS HALTED.",
                            consecutiveLosses, MaxConsecutiveLosses));
                        WriteOrderRow("SAFETY_BRAKE_TRIPPED", entryOrderNumber, "n/a", 0, 0, 0,
                            string.Format("consecutive_losses={0}", consecutiveLosses));
                        PlayOrderSound("Alert2.wav", "Safety brake tripped");
                    }
                }
                else
                {
                    PlayOrderSound("Boxing Bell.wav", "Profit target hit");
                    if (consecutiveLosses > 0)
                        Print(string.Format("[ORDER] Win resets consecutive losses (was {0}).", consecutiveLosses));
                    consecutiveLosses = 0;
                }

                ResetOrderSubsystemToIdle(isStopFill ? "stop_hit" : "target_hit");
            }
        }

        // Defensive position-sync check at top of every OnBarUpdate
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
                            ExitLong(Math.Abs(Position.Quantity), "ExitDesync", "EntryMACD");
                        else if (brokerPos == MarketPosition.Short)
                            ExitShort(Math.Abs(Position.Quantity), "ExitDesyncShort", "EntryMACDShort");
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
            // NOTE: consecutiveLosses and safetyBrakeTripped persist.
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
            // Wraps midnight (e.g. 22:00 - 02:00)
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

        // Check if account has ANY position on this instrument (manual, other strategy, etc.)
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
            sw.WriteLine("# schema_version=1.0.0");
            sw.WriteLine(string.Format("# file_created_NY={0}",
                TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
            sw.WriteLine(string.Format("# file_created_UTC={0}",
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
            sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
            sw.WriteLine(string.Format("# bar_type={0} {1}", BarsPeriod.Value, BarsPeriod.BarsPeriodType));
            sw.WriteLine(string.Format("# tick_size={0}", TickSize));
            sw.WriteLine(string.Format("# MACDFast={0}", MACDFast));
            sw.WriteLine(string.Format("# MACDSlow={0}", MACDSlow));
            sw.WriteLine(string.Format("# MACDSignal={0}", MACDSignal));
            sw.WriteLine(string.Format("# ATRPeriod={0}", ATRPeriod));
            sw.WriteLine(string.Format("# CrossToleranceATR={0}", CrossToleranceATR));
            sw.WriteLine(string.Format("# TradeDirection={0}", TradeDirection));
            sw.WriteLine(string.Format("# StopPoints={0}", StopPoints));
            sw.WriteLine(string.Format("# TargetPoints={0}", TargetPoints));
            sw.WriteLine(string.Format("# EventWindowBars={0}", EventWindowBars));
            sw.WriteLine(string.Format("# TradingHours_NY={0:D2}:{1:D2}-{2:D2}:{3:D2}",
                TradingStartHourNY, TradingStartMinuteNY, TradingEndHourNY, TradingEndMinuteNY));
            sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
            sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
            sw.WriteLine(string.Format("# EnableOrders={0}", EnableOrders));
            sw.WriteLine("# encoding: SUCCESS=1, FAILURE=0");
            sw.WriteLine("# all timestamps NY time");
            sw.WriteLine("#");
        }

        private void WriteOccCsvHeaderRow(StreamWriter sw)
        {
            sw.WriteLine("CrossTime_NY,Direction,EventType,"
                + "MACDValueAtCross,SignalValueAtCross,Diff,ATRAtCross,DiffOverATR,"
                + "EntryPrice,StopPrice,TargetPrice,"
                + "ResolutionTime_NY,BarsToResolve,ResolutionType,ResolutionPrice,"
                + "MaxFavorable,MaxAdverse,OutcomeBit,"
                + "DailyCrossNumber,DailyQualifiedNumber,"
                + "AlertPattern,QualifiedMACDOutcomeString,"
                + "FiredAlertOnThisRow,CapturedPostAlert,CapturedBit,"
                + "PostAlertOutcomeString,PendingPostAlertCapturesAfter,"
                + "IsAlertedAfter,OrderStateAfter,LiveOrderNumber");
        }

        // Write a row for a QUALIFIED + RESOLVED cross
        private void WriteQualifiedCrossRow(
            DateTime crossTime, bool isLong, string eventType,
            double macdValue, double signalValue, double atrValue, double diffOverAtr,
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

                string logFile = Path.Combine(AuditLogPath, "scalper_MACD_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        WriteCommonOccHeader(sw);
                        WriteOccCsvHeaderRow(sw);
                    }

                    double diff = macdValue - signalValue;
                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},{8:F2},{9:F2},{10:F2},{11},{12},{13},{14:F2},{15:F2},{16:F2},{17},{18},{19},{20},{21},{22},{23},{24},{25},{26},{27},{28},{29}",
                        TimeZoneInfo.ConvertTime(crossTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        isLong ? "LONG" : "SHORT",
                        eventType,
                        macdValue,
                        signalValue,
                        diff,
                        atrValue,
                        diffOverAtr,
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
                        qualifiedMacdOutcomeString.ToString(),
                        firedAlert ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",
                        capturedPostAlert ? capturedBit.ToString() : "",
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState,
                        liveOrderNumber > 0 ? liveOrderNumber.ToString() : ""));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-OCC] ERROR: {0}", ex.Message));
            }
        }

        // Write a row for a SKIPPED cross (gate failed)
        private void WriteSkippedCrossRow(DateTime crossTime, bool isLong, string eventType,
            double macdValue, double signalValue, double atrValue, double diffOverAtr)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_MACD_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        WriteCommonOccHeader(sw);
                        WriteOccCsvHeaderRow(sw);
                    }

                    double diff = macdValue - signalValue;
                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    // SKIPPED rows: only fill in cross-detection info; resolution/outcome columns blank
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},,,,,,,,,,,{8},{9},{10},{11},NO,NO,,{12},{13},{14},{15},",
                        TimeZoneInfo.ConvertTime(crossTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        isLong ? "LONG" : "SHORT",
                        eventType,
                        macdValue,
                        signalValue,
                        diff,
                        atrValue,
                        diffOverAtr,
                        dailyCrossNumber,
                        dailyQualifiedNumber,
                        apForCsv,
                        qualifiedMacdOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState));
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

                string logFile = Path.Combine(AuditLogPath, "scalper_MACD_alert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.0.0");
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine("# all timestamps NY time");
                        sw.WriteLine("#");
                        sw.WriteLine("AlertTime_NY,DailyAlertNumber,AlertPattern,CurrentPrice,"
                            + "QualifiedMACDOutcomeString,PostAlertOutcomeString,"
                            + "PendingPostAlertCapturesAfter,IsAlertedAfter,OrderStateAtAlert");
                    }

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3:F2},{4},{5},{6},{7},{8}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        apForCsv,
                        Close[0],
                        qualifiedMacdOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures,
                        isAlerted ? "YES" : "NO",
                        orderState));
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

                string logFile = Path.Combine(AuditLogPath, "scalper_MACD_orders.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.0.0");
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
                        sw.WriteLine("#   EXIT_WINDOW_EXPIRED            - EventWindow timeout, market exit");
                        sw.WriteLine("#   CANCEL_UNFILLED                - limit entry cancelled (bars elapsed)");
                        sw.WriteLine("#   CANCEL_HOURS                   - entry cancelled at trading-hours end");
                        sw.WriteLine("#   FORCEFLAT_HOURS                - position closed at trading-hours end");
                        sw.WriteLine("#   FORCEFLAT_HOURS_GHOST          - ghost position closed at hours end");
                        sw.WriteLine("#   FORCE_RESET_STUCK              - limit grace expired, callback never arrived");
                        sw.WriteLine("#   DESYNC_FORCE_FLAT              - state=Idle but broker has position");
                        sw.WriteLine("#   DESYNC_RESET_IDLE              - state=Working/Position but broker Flat");
                        sw.WriteLine("#   MARKET_HEARTBEAT_WARN          - market order no fill confirm after 30s");
                        sw.WriteLine("#   SAFETY_BRAKE_TRIPPED           - consecutive losses limit reached");
                        sw.WriteLine("#   REJECTED                       - entry order rejected");
                        sw.WriteLine("#");
                        sw.WriteLine("EventTime_NY,EventType,EntryOrderNumber,OrderTypeStr,Price,StopPrice,TargetPrice,OrderState,Extra");
                    }
                    sw.WriteLine(string.Format("{0},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7},{8}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        eventType,
                        orderNumber,
                        orderTypeStr,
                        price,
                        stopPx,
                        targetPx,
                        orderState,
                        extra));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ORDER] ERROR: {0}", ex.Message));
            }
        }

        #region Properties

        // ===== Group 1: MACD Source =====

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MACD Fast", Description = "MACD fast EMA period. Default 12.",
            Order = 1, GroupName = "1. MACD Source")]
        public int MACDFast { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "MACD Slow", Description = "MACD slow EMA period. Default 26.",
            Order = 2, GroupName = "1. MACD Source")]
        public int MACDSlow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MACD Signal", Description = "MACD signal-line EMA period. Default 9.",
            Order = 3, GroupName = "1. MACD Source")]
        public int MACDSignal { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "ATR Period", Description = "ATR period for cross-confirmation filter. Default 14.",
            Order = 4, GroupName = "1. MACD Source")]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Cross Tolerance × ATR",
            Description = "Cross confirmed when |MACD - Signal| > ATR × this value. 0.0 = no filter (pure MACD cross). 0.05 = 5% of ATR.",
            Order = 5, GroupName = "1. MACD Source")]
        public double CrossToleranceATR { get; set; }

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
            Description = "Halt new orders after this many losing trades in a row. Default 3 (conservative for new strategy). Set to 99 to effectively disable. Disable+re-enable strategy to reset.",
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
        [Display(Name = "Auto-plot MACD",
            Description = "Automatically add MACD indicator to chart using strategy's parameters.",
            Order = 3, GroupName = "6. Visuals")]
        public bool AutoPlotMACD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto-plot ATR",
            Description = "Automatically add ATR indicator to chart using strategy's ATRPeriod.",
            Order = 4, GroupName = "6. Visuals")]
        public bool AutoPlotATR { get; set; }

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
            Description = "Max in-memory QualifiedMACDOutcomeString length before trimming oldest. Default 5000.",
            Order = 1, GroupName = "8. Advanced")]
        public int MaxBitsKept { get; set; }

        #endregion
    }
}
