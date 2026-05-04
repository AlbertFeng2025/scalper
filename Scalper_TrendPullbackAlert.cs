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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =============================================================================
//  STRATEGY:    Scalper_TrendPullbackAlert v3.7
//  AUTHOR:      Albert Feng / Drafted with help from Claude
//  REPLACES:    Scalper_TrendPullbackAlert v3.6
// =============================================================================
//
//  PURPOSE
//  -------
//  Order-aware trend + pullback + recovery alerter. Places NO orders.
//  Signals "buy-the-dip" opportunities for human reaction.
//
// =============================================================================
//  v3.7 CHANGES vs v3.6 - SIMPLIFICATION
// =============================================================================
//
//  v3.6 had a complex re-dip state machine that didn't work as expected.
//  Real-world test on 5/4/2026 MNQ data showed 4 alerts in 35 minutes (9:35
//  to 10:05) on essentially the SAME setup (same L=27614.25 @ 09:09). The
//  re-dip state was being cleared incorrectly because H_Time drifted as
//  the lookback window slid forward.
//
//  v3.7 replaces all of v3.6's re-dip logic with a single simple rule:
//
//      After a beep, the strategy is LOCKED.
//      The lock CLEARS only when current H > lastSignalH.
//
//  In plain language: once we beep on a setup, ignore further beeps until
//  the market pushes to a new HIGHER peak. A new peak = fresh leg up =
//  fresh opportunity for the next pullback.
//
// =============================================================================
//  WHAT WAS REMOVED FROM v3.6
// =============================================================================
//
//  - SecondBeepLowPct parameter (no more threshold raising for second beep)
//  - ResetRecoveryPct parameter (no more re-dip percentage check)
//  - armedForRedip state variable
//  - lastBeepHTime, lastBeepLTime tracking
//  - Re-dip state machine in OnBarUpdate
//  - ARMED_NO_REDIP outcome
//
// =============================================================================
//  WHAT WAS ADDED IN v3.7
// =============================================================================
//
//  - lastSignalH variable (the H price at the time of the last beep)
//  - lockedAfterBeep boolean (true after a beep, false after H makes new high)
//  - LOCKED_NO_NEW_HIGH outcome (replaces ARMED_NO_REDIP)
//  - LastSignalH and Locked columns in audit log (replace Threshold and ArmedForRedip)
//
// =============================================================================
//  WHAT'S UNCHANGED FROM v3.6
// =============================================================================
//
//  - 3 gates (TREND, SIZE, ZONE) and their semantics
//  - Order-aware H/L computation (L must come AFTER H in time)
//  - OR-trend gate: Close > SMA(now) OR Close > SMA(at H-bar)
//  - 5-minute cooldown using bar time (backup safety net)
//  - SUPPRESSED_BUSY outcome (still relevant for replay)
//  - Chart markers (green up arrow at L)
//  - Beep sequence (3 beeps, 1 sec apart, wall-clock cadence)
//  - Forward performance result tracking via _Results.csv
//  - LookbackBars = 80, MinPullbackPoints = 80, AlertLowPct = 0.35
//
// =============================================================================
//  TRADE-OFFS OF THE SIMPLE H-RULE
// =============================================================================
//
//  Catches well:
//    - Cluster spam from same setup -> killed cleanly (bug from v3.6 fixed)
//    - Multi-leg uptrends -> each new high triggers fresh opportunity
//
//  Misses:
//    - W-shape recoveries within same setup (first attempt fails, deeper
//      dip, recovery) -> typically H doesn't make new high during W
//    - Deep crashes followed by V-bottom recoveries -> trend gate usually
//      fails anyway in those cases, so this is mostly a non-issue
//
//  We accepted these misses in exchange for simplicity. Real-world data
//  from the forward-performance log will tell us if we're missing
//  important opportunities. If so, we add complexity back later.
//
// =============================================================================
//  PARAMETER DEFAULTS (v3.7)
// =============================================================================
//    TrendMaPeriod          80
//    LookbackBars           80
//    MinPullbackPoints      80
//    AlertLowPct            0.35
//    AlertHighPct           0.70
//    CooldownMinutes        5
//    ResultEvalBars         5
//    ActiveStartTime        0
//    ActiveEndTime          235959
//    AlertSoundCount        3
//    AlertReminderSecs      1
//    EnableChartMarkers     true
//    EnableAuditLog         false
//    AuditLogPath           C:\temp
//
// =============================================================================
//  HOW THE LOCK WORKS - WALK THROUGH
// =============================================================================
//
//  Initial state:
//    lockedAfterBeep = false
//    lastSignalH     = 0  (no beeps yet)
//
//  First beep at 09:35 (H = 27867.75):
//    All 3 gates pass. lockedAfterBeep is false. -> FIRE BEEP.
//    Then set: lockedAfterBeep = true, lastSignalH = 27867.75
//
//  Subsequent bars (09:36 onwards):
//    Compute H from lookback.
//    If H > lastSignalH (27867.75): unlock the strategy
//      Then if other gates pass, can fire next beep.
//    Else: stay locked. Outcome = LOCKED_NO_NEW_HIGH.
//
//  In your 9:35-10:05 window, H drifts DOWN (27867 -> 27790 -> 27771)
//  as the lookback window slides forward. So H never exceeds 27867.75.
//  Strategy stays locked the entire time. ZERO additional beeps.
//
//  When does the lock release?
//  Only when price climbs to a new high above 27867.75 and that new high
//  enters the lookback window. Then the lookback finds H > 27867.75,
//  the lock releases, and the strategy is ready for the next pullback.
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Scalper_TrendPullbackAlert : Strategy
    {
        #region Variables

        private SMA trendMa;

        // Beep sequence (wall-clock cadence between beeps)
        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        // Cooldown - uses BAR time (backup safety net)
        private DateTime lastAlertBarTime = DateTime.MinValue;

        // Prevent duplicate fire on same bar
        private int lastAlertBar = -1;

        // Once-per-minute audit logging
        private int lastAuditedBar = -1;

        // ---------------------------------------------------------------------
        // [v3.7 NEW] State for the simple H-based lock rule
        //
        // lockedAfterBeep:  true = strategy is locked, no beeps allowed until
        //                          H > lastSignalH (a new higher peak)
        // lastSignalH:      the H price at the time of the last beep
        //                   (used for the H > lastSignalH comparison)
        // ---------------------------------------------------------------------
        private bool   lockedAfterBeep = false;
        private double lastSignalH     = 0;

        // ---------------------------------------------------------------------
        // [v3.5] Pending-evaluation queue for forward-performance logging
        // ---------------------------------------------------------------------
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
                Description = "Trend + Pullback + Recovery alerter (v3.7). Simple H-based lock: after beep, locked until H makes new high.";
                Name        = "Scalper_TrendPullbackAlert";
                Calculate   = Calculate.OnPriceChange;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 80;
                IsInstantiatedOnEachOptimizationIteration = true;

                TrendMaPeriod      = 80;
                LookbackBars       = 80;
                MinPullbackPoints  = 80;
                AlertLowPct        = 0.35;
                AlertHighPct       = 0.70;
                CooldownMinutes    = 5;
                ResultEvalBars     = 5;
                ActiveStartTime    = 0;
                ActiveEndTime      = 235959;
                AlertSoundCount    = 3;
                AlertReminderSecs  = 1;
                EnableChartMarkers = true;
                EnableAuditLog     = false;
                AuditLogPath       = @"C:\temp";
            }
            else if (State == State.DataLoaded)
            {
                trendMa = SMA(TrendMaPeriod);
                ResetState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] Scalper_TrendPullbackAlert v3.7 armed at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] Trend (OR): Close > SMA(now) OR Close > SMA(at H-bar). SMA period = {0}",
                    TrendMaPeriod));
                Print(string.Format("[INIT] Pullback: lookback={0} bars, min size={1} pts (ORDER-AWARE: L must be after H)",
                    LookbackBars, MinPullbackPoints));
                Print(string.Format("[INIT] Zone: recovery between {0:P0} and {1:P0}",
                    AlertLowPct, AlertHighPct));
                Print("[INIT] LOCK RULE: after each beep, locked until H > lastSignalH (new higher peak).");
                Print(string.Format("[INIT] Cooldown: {0} bar-minutes (backup safety net)",
                    CooldownMinutes));
                Print(string.Format("[INIT] Result eval: {0} bars after each beep",
                    ResultEvalBars));
                Print(string.Format("[INIT] Time window: {0:D6} to {1:D6}",
                    ActiveStartTime, ActiveEndTime));
                Print(string.Format("[INIT] Beeps: {0} times, {1}s apart",
                    AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] Audit log: {0}",
                    EnableAuditLog ? "ON ("+AuditLogPath+"\\Scalper_TrendPullbackAlert.csv)" : "OFF"));
                Print(string.Format("[INIT] Results log: ALWAYS ON ({0}\\Scalper_TrendPullbackAlert_Results.csv)",
                    AuditLogPath));
                Print("[INIT] This strategy places NO orders. Safe for replay.");
            }
        }

        // =====================================================================
        // OnBarUpdate
        //
        // Order of operations on each tick:
        //   1. Process pending result evaluations (forward perf)
        //   2. Reminder beeps (if mid-sequence)
        //   3. Time window check
        //   4. Compute H, L, recovery, trend margin
        //   5. [v3.7] Update lock state: clear lock if H > lastSignalH
        //   6. Decide outcome (FAIL_xxx, COOLDOWN, LOCKED, BEEP, SUPPRESSED_BUSY)
        //   7. If BEEP: play sound, draw arrow, write audit, ADD TO PENDING,
        //               and SET LOCK with new lastSignalH
        //   8. Per-minute audit log (if EnableAuditLog)
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(TrendMaPeriod, LookbackBars) + 1) return;

            // -----------------------------------------------------------------
            // Section 0: Process pending result evaluations
            // -----------------------------------------------------------------
            EvaluatePendingResults();

            // -----------------------------------------------------------------
            // Section A: Reminder beeps (wall-clock cadence)
            // -----------------------------------------------------------------
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
            if (beepCount >= AlertSoundCount)
            {
                beepCount = 0;
            }

            // -----------------------------------------------------------------
            // Section B: Time window
            // -----------------------------------------------------------------
            int currentTime = ToTime(Time[0]);
            bool isWithinHours;
            if (ActiveStartTime <= ActiveEndTime)
                isWithinHours = (currentTime >= ActiveStartTime && currentTime <= ActiveEndTime);
            else
                isWithinHours = (currentTime >= ActiveStartTime || currentTime <= ActiveEndTime);
            if (!isWithinHours) return;

            // -----------------------------------------------------------------
            // Section C: Order-aware H and L
            // -----------------------------------------------------------------
            double H = double.MinValue;
            int hBarsAgo = -1;
            for (int k = 1; k <= LookbackBars; k++)
            {
                if (High[k] > H)
                {
                    H = High[k];
                    hBarsAgo = k;
                }
            }

            double L = double.MaxValue;
            int lBarsAgo = -1;
            for (int k = 1; k < hBarsAgo; k++)
            {
                if (Low[k] < L)
                {
                    L = Low[k];
                    lBarsAgo = k;
                }
            }

            double range = (lBarsAgo > 0) ? (H - L) : 0;
            double currentPrice = Close[0];
            double smaNow = trendMa[0];
            double recovery = (range > 0) ? (currentPrice - L) / range : 0;
            double pullbackPts = (lBarsAgo > 0) ? (H - currentPrice) : 0;
            double trendMarginNow = currentPrice - smaNow;

            DateTime hTime = (hBarsAgo > 0) ? Time[hBarsAgo] : DateTime.MinValue;
            DateTime lTime = (lBarsAgo > 0) ? Time[lBarsAgo] : DateTime.MinValue;

            // -----------------------------------------------------------------
            // [v3.7 NEW] Section C2: Update lock state
            //
            // The lock clears when current H exceeds lastSignalH. This means
            // the market has pushed to a new peak higher than where we last
            // alerted, so a fresh leg up is now possible.
            //
            // Note: this comparison uses STRICT inequality (>). A tie (H ==
            // lastSignalH) keeps us locked. This prevents flickering when
            // the same peak bar is still in the lookback.
            // -----------------------------------------------------------------
            if (lockedAfterBeep && H > lastSignalH)
            {
                Print(string.Format("[UNLOCK] H={0:F2} now exceeds lastSignalH={1:F2}. Lock cleared.",
                    H, lastSignalH));
                lockedAfterBeep = false;
            }

            // -----------------------------------------------------------------
            // Section D: 3 conditions
            // -----------------------------------------------------------------

            // OR-trend: pass if EITHER current trend OR pre-dip trend was up
            double smaAtH = (hBarsAgo > 0 && hBarsAgo < CurrentBar) ? trendMa[hBarsAgo] : double.NaN;
            bool condTrendNow = currentPrice > smaNow;
            bool condTrendAtH = !double.IsNaN(smaAtH) && currentPrice > smaAtH;
            bool condTrend    = condTrendNow || condTrendAtH;

            bool condSize = (range >= MinPullbackPoints) && (lBarsAgo > 0);
            bool condZone = recovery >= AlertLowPct && recovery <= AlertHighPct;

            bool inCooldown = (lastAlertBarTime != DateTime.MinValue)
                && ((Time[0] - lastAlertBarTime).TotalMinutes < CooldownMinutes);

            // -----------------------------------------------------------------
            // Section E: Determine outcome
            // -----------------------------------------------------------------
            string outcome;
            bool wantsToFire = false;

            if (!condTrend)
                outcome = "FAIL_TREND";
            else if (lBarsAgo <= 0)
                outcome = "FAIL_NO_DIP_AFTER_PEAK";
            else if (!condSize)
                outcome = "FAIL_SIZE";
            else if (recovery < AlertLowPct)
                outcome = "FAIL_ZONE_LOW";
            else if (recovery > AlertHighPct)
                outcome = "FAIL_ZONE_HIGH";
            else if (lockedAfterBeep)             // [v3.7] new outcome
                outcome = "LOCKED_NO_NEW_HIGH";
            else if (inCooldown)
                outcome = "COOLDOWN";
            else
            {
                wantsToFire = true;

                if (beepCount > 0)
                {
                    outcome = "SUPPRESSED_BUSY";
                    wantsToFire = false;
                }
                else if (CurrentBar == lastAlertBar)
                {
                    outcome = "SUPPRESSED_BUSY";
                    wantsToFire = false;
                }
                else
                {
                    outcome = "BEEP";
                }
            }

            // -----------------------------------------------------------------
            // Section F: Fire alert
            // -----------------------------------------------------------------
            if (wantsToFire)
            {
                Print("================================================================");
                Print(string.Format("[ALERT] *** RECOVERY in zone at {0} (price {1}) ***",
                    Time[0].ToString("HH:mm:ss"), currentPrice));
                Print(string.Format("[ALERT]   SMA(now)={0:F2} (margin={1:+0.00;-0.00})  SMA(@H)={2:F2}",
                    smaNow, trendMarginNow,
                    double.IsNaN(smaAtH) ? 0 : smaAtH));
                Print(string.Format("[ALERT]   Trend pass: now={0}, atH={1}",
                    condTrendNow ? "YES" : "no", condTrendAtH ? "YES" : "no"));
                Print(string.Format("[ALERT]   H={0:F2} @{1}  L={2:F2} @{3}  range={4:F2}",
                    H, hTime.ToString("HH:mm"),
                    L, lTime.ToString("HH:mm"),
                    range));
                Print(string.Format("[ALERT]   Pullback={0:F2} pts, Recovery={1:P1} (zone {2:P0} to {3:P0})",
                    pullbackPts, recovery, AlertLowPct, AlertHighPct));

                PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
                lastBeepWallClock = DateTime.Now;
                beepCount = 1;
                lastAlertBarTime = Time[0];
                lastAlertBar = CurrentBar;

                if (EnableChartMarkers)
                {
                    string arrowTag = "TPA_arrow_" + CurrentBar;
                    string textTag  = "TPA_text_"  + CurrentBar;
                    Draw.ArrowUp(this, arrowTag, true, 0, L - (2 * TickSize),
                        Brushes.LimeGreen);
                    string label = string.Format("Pullback {0:F1}pts\nRecov {1:P0}",
                        pullbackPts, recovery);
                    Draw.Text(this, textTag, label, 0,
                        High[0] + (4 * TickSize), Brushes.LimeGreen);
                }

                WriteAuditRow(currentPrice, smaNow, trendMarginNow, H, hTime, hBarsAgo,
                    L, lTime, lBarsAgo, range, recovery, pullbackPts,
                    condTrend, condSize, condZone, inCooldown, outcome, true);

                // [v3.5] Add to result-tracking queue
                PendingResult pr = new PendingResult();
                pr.BeepBarTime  = Time[0];
                pr.BeepBarIndex = CurrentBar;
                pr.BeepClose    = currentPrice;
                pr.Recovery     = recovery;
                pr.Range        = range;
                pr.TrendMargin  = trendMarginNow;
                pr.H            = H;
                pr.L            = L;
                pr.HTime        = hTime.ToString("HH:mm");
                pr.LTime        = lTime.ToString("HH:mm");
                pendingResults.Add(pr);

                // -----------------------------------------------------------------
                // [v3.7 NEW] Lock the strategy and remember H for unlock check
                // -----------------------------------------------------------------
                lockedAfterBeep = true;
                lastSignalH     = H;

                Print(string.Format("[ALERT]   LOCKED. Next beep needs H > {0:F2} (new higher peak).",
                    lastSignalH));
                Print(string.Format("[ALERT]   Added to result queue (eval at bar+{0}). Queue size: {1}",
                    ResultEvalBars, pendingResults.Count));
            }

            // -----------------------------------------------------------------
            // Section G: Per-minute audit row
            // -----------------------------------------------------------------
            if (EnableAuditLog && IsFirstTickOfBar && CurrentBar != lastAuditedBar)
            {
                WriteAuditRow(currentPrice, smaNow, trendMarginNow, H, hTime, hBarsAgo,
                    L, lTime, lBarsAgo, range, recovery, pullbackPts,
                    condTrend, condSize, condZone, inCooldown, outcome, false);
                lastAuditedBar = CurrentBar;
            }
        }

        // =====================================================================
        // [v3.5] EvaluatePendingResults - forward-performance logging
        // =====================================================================
        private void EvaluatePendingResults()
        {
            if (pendingResults.Count == 0) return;

            List<PendingResult> toRemove = new List<PendingResult>();

            foreach (PendingResult pr in pendingResults)
            {
                int barsElapsed = CurrentBar - pr.BeepBarIndex;
                if (barsElapsed < ResultEvalBars) continue;

                double evalClose = Close[0];
                double delta = evalClose - pr.BeepClose;
                string verdict;
                if (delta >= 5)       verdict = "WIN";
                else if (delta <= -5) verdict = "LOSS";
                else                  verdict = "FLAT";

                Print(string.Format("[RESULT] Beep at {0} -> {1} bars later: {2:F2} -> {3:F2} = {4:+0.00;-0.00} pts ({5})",
                    pr.BeepBarTime.ToString("HH:mm:ss"),
                    barsElapsed, pr.BeepClose, evalClose, delta, verdict));

                WriteResultRow(pr, evalClose, delta, verdict);
                toRemove.Add(pr);
            }

            foreach (var pr in toRemove)
                pendingResults.Remove(pr);
        }

        // =====================================================================
        // [v3.5] WriteResultRow - appends to results CSV
        // =====================================================================
        private void WriteResultRow(PendingResult pr, double evalClose, double delta, string verdict)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "Scalper_TrendPullbackAlert_Results.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("BeepTime,BeepClose,EvalBars,EvalTime,EvalClose,DeltaPoints,Verdict,"
                            + "Recovery,Range,TrendMargin,H,H_Time,L,L_Time");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1:F2},{2},{3},{4:F2},{5:F2},{6},{7:F4},{8:F2},{9:F2},{10:F2},{11},{12:F2},{13}",
                        pr.BeepBarTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        pr.BeepClose,
                        ResultEvalBars,
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        evalClose,
                        delta,
                        verdict,
                        pr.Recovery,
                        pr.Range,
                        pr.TrendMargin,
                        pr.H,
                        pr.HTime,
                        pr.L,
                        pr.LTime));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[RESULT] ERROR writing results log: {0}", ex.Message));
            }
        }

        // =====================================================================
        // WriteAuditRow - per-bar or per-alert audit log
        // [v3.7] Replaced Threshold/ArmedForRedip columns with LastSignalH/Locked
        // =====================================================================
        private void WriteAuditRow(double price, double sma, double trendMargin,
            double H, DateTime hTime, int hBarsAgo,
            double L, DateTime lTime, int lBarsAgo,
            double range, double recovery, double pullbackPts,
            bool condTrend, bool condSize, bool condZone, bool inCooldown,
            string outcome, bool isAlertEvent)
        {
            if (!EnableAuditLog && !isAlertEvent) return;

            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "Scalper_TrendPullbackAlert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("BarTime,WriteTime,Price,SMA,TrendMargin,"
                            + "H,H_Time,H_BarsAgo,L,L_Time,L_BarsAgo,"
                            + "Range,PullbackPts,RecoveryPct,LastSignalH,Locked,"
                            + "CondTrend,CondSize,CondZone,InCooldown,"
                            + "Outcome,IsAlertEvent");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1},{2:F2},{3:F2},{4:F2},"
                        + "{5:F2},{6},{7},{8:F2},{9},{10},"
                        + "{11:F2},{12:F2},{13:F4},{14:F2},{15},"
                        + "{16},{17},{18},{19},"
                        + "{20},{21}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        DateTime.Now.ToString("HH:mm:ss.fff"),
                        price, sma, trendMargin,
                        H,
                        hTime != DateTime.MinValue ? hTime.ToString("HH:mm") : "",
                        hBarsAgo,
                        (lBarsAgo > 0 ? L : 0),
                        lTime != DateTime.MinValue ? lTime.ToString("HH:mm") : "",
                        lBarsAgo,
                        range, pullbackPts, recovery,
                        lastSignalH,
                        lockedAfterBeep ? "YES" : "NO",
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
            beepCount         = 0;
            lastAlertBarTime  = DateTime.MinValue;
            lastAlertBar      = -1;
            lastAuditedBar    = -1;
            lockedAfterBeep   = false;
            lastSignalH       = 0;
            pendingResults.Clear();
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(10, 500)]
        [Display(Name="TrendMaPeriod",
            Description="SMA period for the trend filter. v3.5+ OR-trend: alerts fire when Close > SMA(now) OR Close > SMA(at H-bar).",
            Order=1, GroupName="1. Trend Filter")]
        public int TrendMaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name="LookbackBars",
            Description="Bars used to find H (peak) and L (dip after peak). Default 80.",
            Order=2, GroupName="2. Pullback Size")]
        public int LookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="MinPullbackPoints",
            Description="Minimum points the H-L range must span. Default 80 for MNQ.",
            Order=3, GroupName="2. Pullback Size")]
        public int MinPullbackPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.95)]
        [Display(Name="AlertLowPct",
            Description="Lower edge of alert zone. Default 0.35.",
            Order=4, GroupName="3. Trigger Zone")]
        public double AlertLowPct { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.99)]
        [Display(Name="AlertHighPct",
            Description="Upper edge of alert zone. Default 0.70.",
            Order=5, GroupName="3. Trigger Zone")]
        public double AlertHighPct { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="CooldownMinutes",
            Description="Bar-time minutes after a beep before another beep is allowed. Backup safety net (main suppression is the H-lock). Default 5.",
            Order=6, GroupName="4. Cooldown")]
        public int CooldownMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="ResultEvalBars",
            Description="Bars after each beep to wait before measuring forward performance. Default 5. Logged to Scalper_TrendPullbackAlert_Results.csv.",
            Order=7, GroupName="5. Result Tracking")]
        public int ResultEvalBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveStartTime",
            Description="Time to start watching (HHMMSS). 0 = midnight.",
            Order=8, GroupName="6. Time Window")]
        public int ActiveStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveEndTime",
            Description="Time to stop watching (HHMMSS). 235959 = 1 sec before midnight.",
            Order=9, GroupName="6. Time Window")]
        public int ActiveEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total number of beeps per alert. Default 3.",
            Order=10, GroupName="7. Beep Sequence")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=11, GroupName="7. Beep Sequence")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Draw green up-arrow at L and text label when alert fires.",
            Order=12, GroupName="8. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableAuditLog",
            Description="Write CSV row every minute (and on every alert). Useful for debugging. Off for live use.",
            Order=13, GroupName="9. Audit Log")]
        public bool EnableAuditLog { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for audit log AND results log. Auto-created. Default C:\\temp.",
            Order=14, GroupName="9. Audit Log")]
        public string AuditLogPath { get; set; }

        #endregion
    }
}