#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
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
// STRATEGY: scalper_TrendPullbackAlert_beta v3.11 (FULLY SELF-CONTAINED)
// AUTHOR: Albert Feng / Drafted with help from Claude
// REPLACES: v3.10
// =============================================================================
//
// v3.11 CHANGES vs v3.10
// ----------------------
//
// CHANGE 1 — IB + Recent HL logic now EMBEDDED in this file.
//   v3.10 referenced an external indicator (Scalper_IBandRecentHL) and
//   called AddChartIndicator(). That caused compile errors when the
//   indicator file had issues. v3.11 has the IB and Recent HL calculation
//   built directly into the strategy — no external dependency. One file,
//   one place to maintain.
//
// CHANGE 2 — IB anchored to NEW YORK RTH (9:30–10:30 ET).
//   v3.10 inherited the session-template-anchored IB from the indicator,
//   which gave wrong results with ETH charts (IB reset at 6 PM ET, the
//   Asian open). v3.11 always computes IB based on the first 60 minutes
//   of NY cash market hours, regardless of the chart's session template.
//   Uses TimeZoneInfo to handle EST/EDT and DST transitions automatically.
//
// CHANGE 3 — IB lines persist across overnight session.
//   Once today's IB is locked at 10:30 ET, the lines stay drawn until
//   next morning's 10:30 ET. So at 2 AM you see today's morning IB,
//   not yesterday's, not a fresh-but-meaningless midnight IB.
//
// CHANGE 4 — AutoScale=false on all drawn lines.
//   If today's IB High is 500 points above current price (e.g., during a
//   big overnight drop), the chart will NOT stretch to include the line.
//   The line is simply off-screen. Price action stays well-sized.
//   NT's right-axis price tag still shows the value.
//
//   USER NOTE: If you don't see a line on the chart, the level is
//   outside your visible price range. Zoom out, or check the Output
//   window / audit log for the value.
//
// CHANGE 5 — Master toggle EnableLevelDrawing.
//   New parameter. When false, NO lines or labels are drawn at all,
//   regardless of the individual Show* toggles. Useful if you want
//   level logging in the CSV without any chart clutter.
//
// CHANGE 6 — Historical reconstruction.
//   If you load a chart at 2 PM ET (past the IB window), the strategy
//   scans back through recent bars to reconstruct today's IB. So you
//   don't have to wait until tomorrow to see IB lines.
//
// =============================================================================
//
// CARRY-OVER NOTES FROM v3.9/v3.10 (unchanged)
// --------------------------------------------
// This is a NOTIFICATION TOOL, not an auto-trader. When a beep fires, the
// human decides whether to enter (via LongScalper or otherwise).
//
// **IMPORTANT**: This alert system is primarily designed and optimized for
// 1-MINUTE bars. Performance on other timeframes may vary significantly.
//
// KNOWN WEAKNESS: FLASH-CRASH FALSE POSITIVES (still open)
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_TrendPullbackAlert_beta : Strategy
    {
        #region Variables

        private SMA trendMa;
        private SMA closeUpMa;

        // ---- Alert state (unchanged from v3.10) ----
        private DateTime lastBeepWallClock;
        private int beepCount = 0;
        private DateTime lastAlertBarTime = DateTime.MinValue;
        private int lastAlertBar = -1;
        private int lastAuditedBar = -1;
        private int lastAlertCurrentBar = -1;

        // ---- v3.11 IB state (embedded, NY-anchored, persistent overnight) ----
        private const int IB_START_HOUR = 9;
        private const int IB_START_MIN  = 30;
        private const int IB_END_HOUR   = 10;
        private const int IB_END_MIN    = 30;

        private double   ibHigh;
        private double   ibLow;
        private bool     ibCalculated;    // true once today's IB is locked
        private bool     ibAccumulating;  // true while inside 9:30-10:30 ET window
        private DateTime currentIbDate;   // NY date the current IB belongs to
        private int      lastDrawnBar = -1;  // throttle Draw calls to once per bar

        // ---- Cached NY timezone (built in DataLoaded) ----
        private TimeZoneInfo nyTz;

        // ---- Pending results queue (level snapshots added at beep time) ----
        private class PendingResult
        {
            public DateTime BeepBarTime;
            public int      BeepBarIndex;
            public double   BeepClose;
            public double   Recovery;
            public double   Range;
            public double   TrendMargin;
            public double   H, L;
            public string   HTime, LTime;
            public double   FutureHigh;
            public double   FutureLow;

            public double   IBHigh;
            public double   IBLow;
            public double   IBMid;
            public double   RecentHigh;
            public double   RecentLow;
            public double   RecentMid;
            public bool     IBValid;
            public string   PriceVsIB;
        }

        private List<PendingResult> pendingResults = new List<PendingResult>();

        #endregion

        // =====================================================================
        // OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Trend + Pullback + Recovery alerter (v3.11). Self-contained: embedded NY-anchored IB (9:30-10:30 ET, DST-safe, persistent overnight) and Recent HL rolling window. Lines drawn with AutoScale=false so chart never twists.";
                Name        = "scalper_TrendPullbackAlert_beta";
                Calculate   = Calculate.OnPriceChange;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 80;
                IsInstantiatedOnEachOptimizationIteration = true;

                TrendMaPeriod        = 30;
                LookbackBars         = 50;
                MinPullbackPoints    = 100;
                CloseUpMaPeriod      = 3;
                AlertLowPct          = 0.25;
                AlertHighPct         = 0.50;
                MinBarsBetweenAlerts = 10;
                CooldownMinutes      = 5;
                ResultEvalBars       = 5;
                ActiveStartTime      = 0;
                ActiveEndTime        = 235959;
                AlertSoundCount      = 3;
                AlertReminderSecs    = 1;
                EnableChartMarkers   = true;
                EnableAuditLog       = false;
                AuditLogPath         = @"C:\temp";

                // ---- v3.11 IB + Recent HL defaults ----
                EnableLevelDrawing = true;        // master toggle
                RecentMinutes      = 60;
                ShowIBHigh         = true;
                ShowIBLow          = true;
                ShowIBMid          = true;
                ShowRecentHigh     = true;
                ShowRecentLow      = true;
                ShowRecentMid      = true;
            }
            else if (State == State.DataLoaded)
            {
                trendMa   = SMA(TrendMaPeriod);
                closeUpMa = SMA(CloseUpMaPeriod);

                try
                {
                    nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                }
                catch (Exception ex)
                {
                    Print(string.Format("[INIT] WARN: Could not load NY timezone: {0}", ex.Message));
                    nyTz = TimeZoneInfo.Local;  // fallback
                }

                ResetState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] scalper_TrendPullbackAlert_beta v3.11 armed at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] Trend rule: Close at H bar > SMA at H bar. SMA period = {0}",
                    TrendMaPeriod));
                Print(string.Format("[INIT] Pullback: lookback up to {0} bars, min size {1} pts",
                    LookbackBars, MinPullbackPoints));
                Print(string.Format("[INIT] Zone: recovery {0:P0}-{1:P0} AND Close > SMA({2})",
                    AlertLowPct, AlertHighPct, CloseUpMaPeriod));
                Print(string.Format("[INIT] IB window: 9:30-10:30 ET (NY time, DST-safe, persistent overnight)"));
                Print(string.Format("[INIT] Recent HL window: {0} min rolling", RecentMinutes));
                Print(string.Format("[INIT] EnableLevelDrawing = {0}", EnableLevelDrawing));
                Print(string.Format("[INIT] Audit log: {0}",
                    EnableAuditLog ? "ON ("+AuditLogPath+"\\scalper_TrendPullbackAlert_beta.csv)" : "OFF"));
                Print(string.Format("[INIT] Results log: ALWAYS ON ({0}\\scalper_TrendPullbackAlert_beta_Results.csv)",
                    AuditLogPath));
                Print("[INIT] This strategy places NO orders. Safe for replay.");
            }
        }

        // =====================================================================
        // OnBarUpdate
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(Math.Max(TrendMaPeriod, LookbackBars), CloseUpMaPeriod) + 5) return;

            // ---- Session boundary - reset alert state daily ----
            if (Bars.IsFirstBarOfSession)
            {
                if (lastAlertCurrentBar >= 0)
                {
                    Print(string.Format("[SESSION] New session at {0}. Resetting alert state.",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss")));
                }
                lastAlertCurrentBar = -1;
            }

            // ---- v3.11: Update IB state (NY-anchored, persistent) ----
            UpdateIbState();

            // ---- v3.11: Refresh drawn lines (once per bar) ----
            if (IsFirstTickOfBar && CurrentBar != lastDrawnBar)
            {
                RefreshLevelLines();
                lastDrawnBar = CurrentBar;
            }

            // ---- Pending results: update high/low, then evaluate ----
            UpdatePendingHighLow();
            EvaluatePendingResults();

            // ---- Reminder beeps ----
            if (beepCount > 0 && beepCount < AlertSoundCount)
            {
                double secsSinceLastBeep = (DateTime.Now - lastBeepWallClock).TotalSeconds;
                if (secsSinceLastBeep >= AlertReminderSecs)
                {
                    PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
                    lastBeepWallClock = DateTime.Now;
                    beepCount++;
                }
            }
            if (beepCount >= AlertSoundCount) beepCount = 0;

            // ---- Time window ----
            int currentTime = ToTime(Time[0]);
            bool isWithinHours;
            if (ActiveStartTime <= ActiveEndTime)
                isWithinHours = (currentTime >= ActiveStartTime && currentTime <= ActiveEndTime);
            else
                isWithinHours = (currentTime >= ActiveStartTime || currentTime <= ActiveEndTime);
            if (!isWithinHours) return;

            // ---- Truncated lookback ----
            int effectiveLookback;
            bool tooSoon = false;

            if (lastAlertCurrentBar < 0)
            {
                effectiveLookback = LookbackBars;
            }
            else
            {
                int barsSinceLastAlert = CurrentBar - lastAlertCurrentBar;
                if (barsSinceLastAlert < MinBarsBetweenAlerts)
                {
                    tooSoon = true;
                    effectiveLookback = barsSinceLastAlert;
                }
                else
                {
                    effectiveLookback = Math.Min(LookbackBars, barsSinceLastAlert);
                }
            }

            // ---- Find H, then L after H ----
            double H = double.MinValue;
            int hBarsAgo = -1;
            for (int k = 1; k <= effectiveLookback; k++)
            {
                if (High[k] > H) { H = High[k]; hBarsAgo = k; }
            }

            double L = double.MaxValue;
            int lBarsAgo = -1;
            for (int k = 1; k < hBarsAgo; k++)
            {
                if (Low[k] < L) { L = Low[k]; lBarsAgo = k; }
            }

            double range = (lBarsAgo > 0) ? (H - L) : 0;
            double currentPrice = Close[0];
            double smaNow = trendMa[0];
            double recovery = (range > 0) ? (currentPrice - L) / range : 0;
            double pullbackPts = (lBarsAgo > 0) ? (H - currentPrice) : 0;

            DateTime hTime = (hBarsAgo > 0) ? Time[hBarsAgo] : DateTime.MinValue;
            DateTime lTime = (lBarsAgo > 0) ? Time[lBarsAgo] : DateTime.MinValue;

            // ---- 3 gates ----
            double hBarClose = (hBarsAgo > 0 && hBarsAgo <= CurrentBar) ? Close[hBarsAgo] : double.NaN;
            double smaAtH = (hBarsAgo > 0 && hBarsAgo < CurrentBar) ? trendMa[hBarsAgo] : double.NaN;
            double trendMarginAtH = (!double.IsNaN(smaAtH) && !double.IsNaN(hBarClose)) ? (hBarClose - smaAtH) : 0;
            bool condTrend = !double.IsNaN(smaAtH) && !double.IsNaN(hBarClose) && (hBarClose > smaAtH);

            bool condSize    = (range >= MinPullbackPoints) && (lBarsAgo > 0);
            bool condZone    = recovery >= AlertLowPct && recovery <= AlertHighPct;
            bool condCloseUp = (Close[0] > closeUpMa[0]);

            bool inCooldown = (lastAlertBarTime != DateTime.MinValue)
                && ((Time[0] - lastAlertBarTime).TotalMinutes < CooldownMinutes);

            // ---- Outcome ----
            string outcome;
            bool wantsToFire = false;

            if (tooSoon)
                outcome = "SKIP_TOO_SOON";
            else if (!condTrend)
                outcome = "FAIL_TREND";
            else if (lBarsAgo <= 0)
                outcome = "FAIL_NO_DIP_AFTER_PEAK";
            else if (!condSize)
                outcome = "FAIL_SIZE";
            else if (recovery < AlertLowPct)
                outcome = "FAIL_ZONE_LOW";
            else if (recovery > AlertHighPct)
                outcome = "FAIL_ZONE_HIGH";
            else if (!condCloseUp)
                outcome = "FAIL_CLOSE_NOT_UP";
            else if (inCooldown)
                outcome = "COOLDOWN";
            else
            {
                wantsToFire = true;
                if (beepCount > 0) { outcome = "SUPPRESSED_BUSY"; wantsToFire = false; }
                else if (CurrentBar == lastAlertBar) { outcome = "SUPPRESSED_BUSY"; wantsToFire = false; }
                else { outcome = "BEEP"; }
            }

            // ---- Fire alert ----
            if (wantsToFire)
            {
                Print("================================================================");
                Print(string.Format("[ALERT] *** RECOVERY in zone at {0} (price {1}) ***",
                    Time[0].ToString("HH:mm:ss"), currentPrice));
                Print(string.Format("[ALERT]   H_close={0:F2}, SMA(@H)={1:F2}, margin@H={2:+0.00;-0.00}",
                    hBarClose, smaAtH, trendMarginAtH));
                Print(string.Format("[ALERT]   H={0:F2} @{1}  L={2:F2} @{3}  range={4:F2}  effLookback={5}",
                    H, hTime.ToString("HH:mm"),
                    L, lTime.ToString("HH:mm"),
                    range, effectiveLookback));
                Print(string.Format("[ALERT] Pullback={0:F2} pts, Recovery={1:P1} (zone {2:P0}-{3:P0}), CloseUp: {4:F2} > SMA({5})={6:F2}",
                    pullbackPts, recovery, AlertLowPct, AlertHighPct,
                    Close[0], CloseUpMaPeriod, closeUpMa[0]));

                // Snapshot IB + Recent HL at beep time
                double snapIBHigh, snapIBLow, snapIBMid;
                double snapRecentHigh, snapRecentLow, snapRecentMid;
                bool snapIBValid;
                GetCurrentLevels(out snapIBHigh, out snapIBLow, out snapIBMid,
                                 out snapRecentHigh, out snapRecentLow, out snapRecentMid,
                                 out snapIBValid);

                string snapPriceVsIB;
                if (!snapIBValid)
                    snapPriceVsIB = "PRE_IB";
                else if (currentPrice > snapIBHigh)
                    snapPriceVsIB = "ABOVE_IB";
                else if (currentPrice < snapIBLow)
                    snapPriceVsIB = "BELOW_IB";
                else
                    snapPriceVsIB = "IN_IB";

                Print(string.Format("[ALERT]   IB H/L/M={0:F2}/{1:F2}/{2:F2}  Recent H/L/M={3:F2}/{4:F2}/{5:F2}  PriceVsIB={6}",
                    snapIBHigh, snapIBLow, snapIBMid,
                    snapRecentHigh, snapRecentLow, snapRecentMid,
                    snapPriceVsIB));

                PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
                lastBeepWallClock = DateTime.Now;
                beepCount = 1;
                lastAlertBarTime = Time[0];
                lastAlertBar = CurrentBar;
                lastAlertCurrentBar = CurrentBar;

                if (EnableChartMarkers)
                {
                    string arrowTag = "TPA_arrow_" + CurrentBar;
                    string textTag  = "TPA_text_"  + CurrentBar;
                    Draw.ArrowUp(this, arrowTag, true, 0, L - (2 * TickSize), Brushes.LimeGreen);
                    string label = string.Format("Pullback {0:F1}pts\nRecov {1:P0}", pullbackPts, recovery);
                    Draw.Text(this, textTag, label, 0, High[0] + (4 * TickSize), Brushes.LimeGreen);
                }

                WriteAuditRow(currentPrice, smaNow, smaAtH, trendMarginAtH, H, hTime, hBarsAgo,
                    L, lTime, lBarsAgo, range, recovery, pullbackPts,
                    effectiveLookback, condTrend, condSize, condZone, inCooldown, outcome, true);

                PendingResult pr = new PendingResult();
                pr.BeepBarTime  = Time[0];
                pr.BeepBarIndex = CurrentBar;
                pr.BeepClose    = currentPrice;
                pr.Recovery     = recovery;
                pr.Range        = range;
                pr.TrendMargin  = trendMarginAtH;
                pr.H            = H;
                pr.L            = L;
                pr.HTime        = hTime.ToString("HH:mm");
                pr.LTime        = lTime.ToString("HH:mm");
                pr.FutureHigh   = currentPrice;
                pr.FutureLow    = currentPrice;
                pr.IBHigh       = snapIBHigh;
                pr.IBLow        = snapIBLow;
                pr.IBMid        = snapIBMid;
                pr.RecentHigh   = snapRecentHigh;
                pr.RecentLow    = snapRecentLow;
                pr.RecentMid    = snapRecentMid;
                pr.IBValid      = snapIBValid;
                pr.PriceVsIB    = snapPriceVsIB;
                pendingResults.Add(pr);

                Print(string.Format("[ALERT]   Added to result queue (eval at bar+{0}). Queue size: {1}",
                    ResultEvalBars, pendingResults.Count));
            }

            // ---- Per-bar audit row ----
            if (EnableAuditLog && IsFirstTickOfBar && CurrentBar != lastAuditedBar)
            {
                WriteAuditRow(currentPrice, smaNow, smaAtH, trendMarginAtH, H, hTime, hBarsAgo,
                    L, lTime, lBarsAgo, range, recovery, pullbackPts,
                    effectiveLookback, condTrend, condSize, condZone, inCooldown, outcome, false);
                lastAuditedBar = CurrentBar;
            }
        }

        // =====================================================================
        // [v3.11] UpdateIbState — NY-anchored IB lifecycle
        // =====================================================================
        private void UpdateIbState()
        {
            DateTime barTimeNy = TimeZoneInfo.ConvertTime(Time[0], nyTz);
            DateTime tradingDateNy = barTimeNy.Date;

            int minuteOfDay = barTimeNy.Hour * 60 + barTimeNy.Minute;
            int ibStartMin  = IB_START_HOUR * 60 + IB_START_MIN;
            int ibEndMin    = IB_END_HOUR   * 60 + IB_END_MIN;

            bool isInIb    = minuteOfDay >= ibStartMin && minuteOfDay < ibEndMin;
            bool isAfterIb = minuteOfDay >= ibEndMin;

            if (isInIb)
            {
                if (tradingDateNy != currentIbDate)
                {
                    // First IB bar of a new NY trading day → reset
                    currentIbDate  = tradingDateNy;
                    ibHigh         = High[0];
                    ibLow          = Low[0];
                    ibAccumulating = true;
                    ibCalculated   = false;
                }
                else if (ibAccumulating)
                {
                    ibHigh = Math.Max(ibHigh, High[0]);
                    ibLow  = Math.Min(ibLow,  Low[0]);
                }
            }
            else if (isAfterIb)
            {
                if (ibAccumulating && tradingDateNy == currentIbDate)
                {
                    // Crossed 10:30 ET → lock today's IB
                    ibAccumulating = false;
                    ibCalculated   = true;
                }
                else if (currentIbDate != tradingDateNy || !ibCalculated)
                {
                    // Chart loaded past IB window — try historical reconstruction
                    TryReconstructTodayIb(tradingDateNy);
                }
            }
            // else: isBeforeIb (midnight–9:30 ET) — yesterday's IB persists
        }

        private void TryReconstructTodayIb(DateTime tradingDateNy)
        {
            double rebuildHigh = double.MinValue;
            double rebuildLow  = double.MaxValue;
            bool foundAny = false;

            int maxScan = Math.Min(CurrentBar, 1440);
            int ibStartMin = IB_START_HOUR * 60 + IB_START_MIN;
            int ibEndMin   = IB_END_HOUR   * 60 + IB_END_MIN;

            for (int k = 0; k <= maxScan; k++)
            {
                DateTime tNy = TimeZoneInfo.ConvertTime(Time[k], nyTz);
                if (tNy.Date != tradingDateNy) continue;

                int minOfDay = tNy.Hour * 60 + tNy.Minute;
                if (minOfDay >= ibStartMin && minOfDay < ibEndMin)
                {
                    rebuildHigh = Math.Max(rebuildHigh, High[k]);
                    rebuildLow  = Math.Min(rebuildLow,  Low[k]);
                    foundAny = true;
                }
            }

            if (foundAny)
            {
                ibHigh = rebuildHigh;
                ibLow  = rebuildLow;
                currentIbDate  = tradingDateNy;
                ibCalculated   = true;
                ibAccumulating = false;
                Print(string.Format("[IB] Reconstructed today's IB from history: H={0:F2} L={1:F2}",
                    ibHigh, ibLow));
            }
        }

        // =====================================================================
        // [v3.11] GetCurrentLevels — snapshot helper
        // =====================================================================
        private void GetCurrentLevels(out double ibH, out double ibL, out double ibM,
                                       out double rH, out double rL, out double rM,
                                       out bool ibValid)
        {
            ibValid = (ibCalculated || ibAccumulating) && ibHigh > 0 && ibLow > 0;
            ibH = ibValid ? ibHigh : 0;
            ibL = ibValid ? ibLow  : 0;
            ibM = ibValid ? (ibHigh + ibLow) / 2 : 0;

            // Recent HL
            DateTime windowStartTime = Time[0].AddMinutes(-RecentMinutes);
            int barsInWindow = Math.Max(1, CurrentBar - Bars.GetBar(windowStartTime));
            rH = MAX(High, barsInWindow)[0];
            rL = MIN(Low,  barsInWindow)[0];
            rM = (rH + rL) / 2;
        }

        // =====================================================================
        // [v3.11] RefreshLevelLines — redraw all level lines once per bar
        //
        // Uses Draw.HorizontalLine with isAutoScale=false so far-away lines
        // don't twist the chart. NT automatically shows a right-axis marker
        // for each horizontal line.
        // =====================================================================
        private void RefreshLevelLines()
        {
            if (!EnableLevelDrawing)
            {
                // Master toggle off — make sure nothing lingers
                RemoveAllLevelDrawings();
                return;
            }

            double ibH, ibL, ibM, rH, rL, rM;
            bool ibValid;
            GetCurrentLevels(out ibH, out ibL, out ibM, out rH, out rL, out rM, out ibValid);

            // ---- IB lines (only draw if today's IB is valid) ----
            if (ibValid)
            {
                if (ShowIBHigh)
                    DrawLevelLine("ib_high", ibH, Brushes.Green, "IB High");
                else
                    { RemoveDrawObject("ib_high"); RemoveDrawObject("ib_high_lbl"); }

                if (ShowIBLow)
                    DrawLevelLine("ib_low",  ibL, Brushes.Green, "IB Low");
                else
                    { RemoveDrawObject("ib_low"); RemoveDrawObject("ib_low_lbl"); }

                if (ShowIBMid)
                    DrawLevelLine("ib_mid",  ibM, Brushes.Green, "IB Mid");
                else
                    { RemoveDrawObject("ib_mid"); RemoveDrawObject("ib_mid_lbl"); }
            }
            else
            {
                // No valid IB yet — clear any stale IB drawings
                RemoveDrawObject("ib_high"); RemoveDrawObject("ib_high_lbl");
                RemoveDrawObject("ib_low");  RemoveDrawObject("ib_low_lbl");
                RemoveDrawObject("ib_mid");  RemoveDrawObject("ib_mid_lbl");
            }

            // ---- Recent HL lines (always valid once we have enough bars) ----
            if (ShowRecentHigh)
                DrawLevelLine("rec_high", rH, Brushes.DodgerBlue, "Recent High");
            else
                { RemoveDrawObject("rec_high"); RemoveDrawObject("rec_high_lbl"); }

            if (ShowRecentLow)
                DrawLevelLine("rec_low",  rL, Brushes.DodgerBlue, "Recent Low");
            else
                { RemoveDrawObject("rec_low");  RemoveDrawObject("rec_low_lbl"); }

            if (ShowRecentMid)
                DrawLevelLine("rec_mid",  rM, Brushes.DodgerBlue, "Recent Mid");
            else
                { RemoveDrawObject("rec_mid");  RemoveDrawObject("rec_mid_lbl"); }
        }

        /// <summary>
        /// Draw a horizontal line at `price` with autoScale=false.
        /// NT automatically shows the value on the right price axis.
        /// Also draws a small text label near the right edge of the chart.
        /// </summary>
        private void DrawLevelLine(string tag, double price, Brush color, string label)
        {
            try
            {
                // [v3.11-fix] Remove the prior drawing with this tag BEFORE redrawing.
                // Draw.HorizontalLine with an existing tag does NOT reliably update the
                // line's price; it can silently keep the old line. Removing first
                // guarantees the new price is used.
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "_lbl");

                Draw.HorizontalLine(this, tag, false, price, color, DashStyleHelper.Solid, 2);

                Draw.Text(this, tag + "_lbl", false, label, -3, price, 0,
                    color, new SimpleFont("Arial", 10),
                    System.Windows.TextAlignment.Left,
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
            catch (Exception ex)
            {
                Print(string.Format("[DRAW] ERROR drawing {0}: {1}", tag, ex.Message));
            }
        }

        private void RemoveAllLevelDrawings()
        {
            string[] tags = { "ib_high", "ib_low", "ib_mid", "rec_high", "rec_low", "rec_mid",
                              "ib_high_lbl", "ib_low_lbl", "ib_mid_lbl",
                              "rec_high_lbl", "rec_low_lbl", "rec_mid_lbl" };
            foreach (var t in tags) RemoveDrawObject(t);
        }

        // =====================================================================
        // Pending results (unchanged from v3.10)
        // =====================================================================
        private void UpdatePendingHighLow()
        {
            if (pendingResults.Count == 0) return;

            foreach (PendingResult pr in pendingResults)
            {
                int barsElapsed = CurrentBar - pr.BeepBarIndex;
                if (barsElapsed <= 0) continue;
                if (barsElapsed > ResultEvalBars) continue;
                if (!IsFirstTickOfBar) continue;

                if (CurrentBar >= 1)
                {
                    double prevHigh = High[1];
                    double prevLow  = Low[1];
                    if (prevHigh > pr.FutureHigh) pr.FutureHigh = prevHigh;
                    if (prevLow  < pr.FutureLow)  pr.FutureLow  = prevLow;
                }
            }
        }

        private void EvaluatePendingResults()
        {
            if (pendingResults.Count == 0) return;

            List<PendingResult> toRemove = new List<PendingResult>();

            foreach (PendingResult pr in pendingResults)
            {
                int barsElapsed = CurrentBar - pr.BeepBarIndex;
                if (barsElapsed < ResultEvalBars) continue;

                double evalClose = Close[0];
                double deltaClose = evalClose - pr.BeepClose;
                double deltaHigh  = pr.FutureHigh - pr.BeepClose;
                double deltaLow   = pr.FutureLow  - pr.BeepClose;
                string verdict;
                if (deltaClose >= 5)       verdict = "WIN";
                else if (deltaClose <= -5) verdict = "LOSS";
                else                       verdict = "FLAT";

                Print(string.Format("[RESULT] Beep at {0} -> {1} bars later: close {2:F2} ({3:+0.00;-0.00}) {4}, max +{5:F2}, max -{6:F2}",
                    pr.BeepBarTime.ToString("HH:mm:ss"),
                    barsElapsed,
                    evalClose, deltaClose, verdict,
                    deltaHigh, -deltaLow));

                WriteResultRow(pr, evalClose, deltaClose, deltaHigh, deltaLow, verdict);
                toRemove.Add(pr);
            }

            foreach (var pr in toRemove)
                pendingResults.Remove(pr);
        }

        // =====================================================================
        // WriteResultRow (column structure unchanged from v3.10)
        // =====================================================================
        private void WriteResultRow(PendingResult pr, double evalClose,
            double deltaClose, double deltaHigh, double deltaLow, string verdict)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_TrendPullbackAlert_beta_Results.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("BeepTime,BeepClose,EvalBars,EvalTime,EvalClose,FutureHigh,FutureLow,"
                            + "DeltaClose,DeltaHigh,DeltaLow,Verdict,"
                            + "Recovery,Range,TrendMargin,H,H_Time,L,L_Time,"
                            + "IBHigh,IBLow,IBMid,RecentHigh,RecentLow,RecentMid,"
                            + "DistIBHigh,DistIBLow,DistIBMid,DistRecentHigh,DistRecentLow,DistRecentMid,"
                            + "PriceVsIB");
                    }

                    double distIBH  = pr.IBValid ? (pr.BeepClose - pr.IBHigh) : 0;
                    double distIBL  = pr.IBValid ? (pr.BeepClose - pr.IBLow)  : 0;
                    double distIBM  = pr.IBValid ? (pr.BeepClose - pr.IBMid)  : 0;
                    double distRH   = (pr.RecentHigh > 0) ? (pr.BeepClose - pr.RecentHigh) : 0;
                    double distRL   = (pr.RecentLow  > 0) ? (pr.BeepClose - pr.RecentLow)  : 0;
                    double distRM   = (pr.RecentMid  > 0) ? (pr.BeepClose - pr.RecentMid)  : 0;

                    sw.WriteLine(string.Format(
                        "{0},{1:F2},{2},{3},{4:F2},{5:F2},{6:F2},"
                        + "{7:F2},{8:F2},{9:F2},{10},"
                        + "{11:F4},{12:F2},{13:F2},{14:F2},{15},{16:F2},{17},"
                        + "{18:F2},{19:F2},{20:F2},{21:F2},{22:F2},{23:F2},"
                        + "{24:F2},{25:F2},{26:F2},{27:F2},{28:F2},{29:F2},"
                        + "{30}",
                        pr.BeepBarTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        pr.BeepClose,
                        ResultEvalBars,
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        evalClose,
                        pr.FutureHigh, pr.FutureLow,
                        deltaClose, deltaHigh, deltaLow, verdict,
                        pr.Recovery, pr.Range, pr.TrendMargin,
                        pr.H, pr.HTime, pr.L, pr.LTime,
                        pr.IBHigh, pr.IBLow, pr.IBMid,
                        pr.RecentHigh, pr.RecentLow, pr.RecentMid,
                        distIBH, distIBL, distIBM,
                        distRH, distRL, distRM,
                        pr.PriceVsIB));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[RESULT] ERROR writing results log: {0}", ex.Message));
            }
        }

        // =====================================================================
        // WriteAuditRow (unchanged from v3.10)
        // =====================================================================
        private void WriteAuditRow(double price, double sma, double smaAtH, double trendMarginAtH,
            double H, DateTime hTime, int hBarsAgo,
            double L, DateTime lTime, int lBarsAgo,
            double range, double recovery, double pullbackPts,
            int effLookback,
            bool condTrend, bool condSize, bool condZone, bool inCooldown,
            string outcome, bool isAlertEvent)
        {
            if (!EnableAuditLog && !isAlertEvent) return;

            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_TrendPullbackAlert_beta.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("BarTime,WriteTime,Price,SMA,SmaAtH,TrendMarginAtH,"
                            + "H,H_Time,H_BarsAgo,L,L_Time,L_BarsAgo,"
                            + "Range,PullbackPts,RecoveryPct,EffLookback,"
                            + "CondTrend,CondSize,CondZone,InCooldown,"
                            + "Outcome,IsAlertEvent");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1},{2:F2},{3:F2},{4:F2},{5:F2},"
                        + "{6:F2},{7},{8},{9:F2},{10},{11},"
                        + "{12:F2},{13:F2},{14:F4},{15},"
                        + "{16},{17},{18},{19},"
                        + "{20},{21}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        DateTime.Now.ToString("HH:mm:ss.fff"),
                        price, sma, double.IsNaN(smaAtH) ? 0 : smaAtH, trendMarginAtH,
                        H,
                        hTime != DateTime.MinValue ? hTime.ToString("HH:mm") : "",
                        hBarsAgo,
                        (lBarsAgo > 0 ? L : 0),
                        lTime != DateTime.MinValue ? lTime.ToString("HH:mm") : "",
                        lBarsAgo,
                        range, pullbackPts, recovery, effLookback,
                        condTrend ? "PASS" : "FAIL",
                        condSize  ? "PASS" : "FAIL",
                        condZone  ? "PASS" : "FAIL",
                        inCooldown ? "YES" : "NO",
                        outcome,
                        isAlertEvent ? "YES" : "NO"));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[AUDIT] ERROR writing log: {0}", ex.Message));
            }
        }

        private void ResetState()
        {
            beepCount           = 0;
            lastAlertBarTime    = DateTime.MinValue;
            lastAlertBar        = -1;
            lastAuditedBar      = -1;
            lastAlertCurrentBar = -1;
            pendingResults.Clear();

            // v3.11 IB state
            ibHigh         = 0;
            ibLow          = 0;
            ibCalculated   = false;
            ibAccumulating = false;
            currentIbDate  = DateTime.MinValue;
            lastDrawnBar   = -1;
        }

        #region Properties

        // ---- Existing alert parameters (unchanged) ----

        [NinjaScriptProperty]
        [Range(10, 500)]
        [Display(Name="TrendMaPeriod",
            Description="SMA period for the trend filter.",
            Order=1, GroupName="1. Trend Filter")]
        public int TrendMaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name="LookbackBars",
            Description="Maximum bars to look back for H/L.",
            Order=2, GroupName="2. Pullback Size")]
        public int LookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="MinPullbackPoints",
            Description="Minimum points the H-L range must span.",
            Order=3, GroupName="2. Pullback Size")]
        public int MinPullbackPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.95)]
        [Display(Name="AlertLowPct",
            Description="Lower edge of alert zone. Default 0.25.",
            Order=4, GroupName="3. Trigger Zone")]
        public double AlertLowPct { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.99)]
        [Display(Name="AlertHighPct",
            Description="Upper edge of alert zone. Default 0.50.",
            Order=5, GroupName="3. Trigger Zone")]
        public double AlertHighPct { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name="CloseUpMaPeriod",
            Description="SMA period for Close-Up filter. Default 3.",
            Order=6, GroupName="3. Trigger Zone")]
        public int CloseUpMaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name="MinBarsBetweenAlerts",
            Description="Minimum bars between any 2 alerts.",
            Order=7, GroupName="4. Alert Spacing")]
        public int MinBarsBetweenAlerts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="CooldownMinutes",
            Description="Bar-time minutes after a beep before another beep is allowed.",
            Order=8, GroupName="5. Cooldown")]
        public int CooldownMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="ResultEvalBars",
            Description="Bars after each beep before measuring forward performance.",
            Order=9, GroupName="6. Result Tracking")]
        public int ResultEvalBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveStartTime",
            Description="Time to start watching (HHMMSS).",
            Order=10, GroupName="7. Time Window")]
        public int ActiveStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveEndTime",
            Description="Time to stop watching (HHMMSS).",
            Order=11, GroupName="7. Time Window")]
        public int ActiveEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert.",
            Order=12, GroupName="8. Beep Sequence")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps.",
            Order=13, GroupName="8. Beep Sequence")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Draw arrow + label when alert fires.",
            Order=14, GroupName="9. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableAuditLog",
            Description="Write CSV row every minute for debugging.",
            Order=15, GroupName="10. Audit Log")]
        public bool EnableAuditLog { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for log CSV files.",
            Order=16, GroupName="10. Audit Log")]
        public string AuditLogPath { get; set; }

        // ---- v3.11 IB + Recent HL parameters ----

        [NinjaScriptProperty]
        [Display(Name="EnableLevelDrawing",
            Description="[v3.11] Master toggle: draw IB + Recent HL lines on chart. When OFF, no lines/labels drawn but levels still logged to CSV.",
            Order=17, GroupName="11. IB + Recent HL")]
        public bool EnableLevelDrawing { get; set; }

        [NinjaScriptProperty]
        [Range(5, 1440)]
        [Display(Name="RecentMinutes",
            Description="[v3.11] Rolling Recent HL window in minutes. Default 60.",
            Order=18, GroupName="11. IB + Recent HL")]
        public int RecentMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowIBHigh",
            Description="[v3.11] Show IB High line (when EnableLevelDrawing is on).",
            Order=19, GroupName="11. IB + Recent HL")]
        public bool ShowIBHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowIBLow",
            Description="[v3.11] Show IB Low line.",
            Order=20, GroupName="11. IB + Recent HL")]
        public bool ShowIBLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowIBMid",
            Description="[v3.11] Show IB Mid line.",
            Order=21, GroupName="11. IB + Recent HL")]
        public bool ShowIBMid { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowRecentHigh",
            Description="[v3.11] Show Recent High line.",
            Order=22, GroupName="11. IB + Recent HL")]
        public bool ShowRecentHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowRecentLow",
            Description="[v3.11] Show Recent Low line.",
            Order=23, GroupName="11. IB + Recent HL")]
        public bool ShowRecentLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowRecentMid",
            Description="[v3.11] Show Recent Mid line.",
            Order=24, GroupName="11. IB + Recent HL")]
        public bool ShowRecentMid { get; set; }

        #endregion
    }
}
