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
//=============================================================================
//  TODO / FUTURE WORK NOTES (added 2026-05-05)
// =============================================================================
//
//  CURRENT STATUS
//  --------------
//  v3.9 logic is final for now. The alerter is a NOTIFICATION TOOL, not an
//  auto-trader. When a beep fires, the human decides whether to enter (via
//  LongScalper or otherwise). LongScalper handles its own bracket and
//  trailing stop, which we can tune later independently.
//
//  KNOWN WEAKNESS: FLASH-CRASH FALSE POSITIVES
//  -------------------------------------------
//  Backtest on MNQ 4/30 - 5/1 showed that the strategy can fire alerts
//  during violent market crashes that are NOT normal pullback recoveries.
//  Example: 4/30 13:45 alert with range = 277 pts (price crashed in
//  minutes, then briefly bounced). The recovery looked valid by all 3
//  gates, but it was a dead-cat bounce that lost -20 instantly.
//
//  Two FILTER IDEAS to consider for future versions:
//
//    FILTER A: MaxRangePoints
//      Skip alert if range (H - L) > some threshold (e.g., 200 pts on MNQ).
//      A pullback bigger than this is more likely a crash than a normal dip.
//      Easy to add: one extra condition in the SIZE gate.
//
//    FILTER B: Volume confirmation at the L bar
//      Skip alert if Volume[lBarsAgo] > AvgVolume * Multiplier (default 1.5
//      or 2.0). Massive volume at the L means panic selling, often followed
//      by more selling. Real pullback recoveries usually happen on normal
//      volume.
//      Requires: rolling volume average, comparison at L bar.
//
//    Could combine: trigger filter only if EITHER is too extreme.
//
//  WHEN TO REVISIT
//  ---------------
//  After running v3.9 for a week of live (or replay) data, review the
//  results CSV (scalper_TrendPullbackAlert_beta_Results.csv). Look at
//  losing alerts and check whether they share these patterns:
//    - Large Range column (suggests Filter A would help)
//    - Volume spike at L_Time (suggests Filter B would help)
//    - Other patterns we haven't thought of yet
//
//  RELATED: LONGSCALPER TUNING
//  ---------------------------
//  LongScalper currently uses ATR-based stops and a fixed profit target.
//  Tuning of ProfitTargetPoints, AtrInitialStopMultiplier, and
//  AtrTrailMultiplier is a SEPARATE concern from this alerter. The
//  alerter just signals the moment; LongScalper handles the trade.
//  Don't mix the two when analyzing performance.
//
// =============================================================================
//  STRATEGY:    scalper_TrendPullbackAlert_beta v3.9
//  AUTHOR:      Albert Feng / Drafted with help from Claude
//  REPLACES:    scalper_TrendPullbackAlert_beta v3.8
// =============================================================================
//
//  PURPOSE
//  -------
//  Order-aware trend + pullback + recovery alerter. Places NO orders.
//  Signals "buy-the-dip" opportunities for human reaction.
//
//  In real-world use, the human hears the beep, walks to the computer,
//  and enables LongScalper which immediately attaches PT=10/SL=20 bracket.
//  The bracket does the heavy lifting on exit. The alerter just needs to
//  signal a good entry MOMENT, not predict the perfect outcome.
//
// =============================================================================
//  v3.9 CHANGES vs v3.8
// =============================================================================
//
//  Three changes based on backtest analysis with bracket trading
//  (PT=10/SL=20 instead of buy-and-hold-5-bars):
//
//  CHANGE A: AlertLowPct lowered from 0.35 to 0.25.
//  -----------------------------------------------
//  Buy-and-hold-5-bars analysis suggested 0.35 was the sweet spot. But
//  with bracket trading (PT=10/SL=20), 0.25 dramatically outperforms:
//
//  Test on MNQ 5/4/2026 data:
//     0.35 + PT/SL: 7 alerts, 3W/4L, total -36.0 pts
//     0.25 + PT/SL: 7 alerts, 7W/0L, total +70.0 pts
//
//  Why 0.25 wins: firing earlier in the recovery means more upside left
//  before reaching H. The PT=10 target is closer to entry. The SL=20 gives
//  enough room for normal retests of L. With brackets, "earlier = better
//  entry price" matters more than "wait for confirmation."
//
//  CHANGE B: Recovery gate changed from cross-up to close>prev close.
//  -----------------------------------------------------------------
//  v3.8 fired when recovery percent CROSSED UP through 0.35 (transition
//  event between two bars). v3.9 fires when:
//      recovery is in zone (0.25 - 0.70)
//      AND close > previous close (this bar is green)
//
//  Why: simpler rule, matches human chart-reading intuition. When you see
//  a green bar in the recovery zone, that's the moment to alert.
//
//  The truncated lookback already prevents repeat alerts on the same
//  setup, so we don't need the cross-up rule for that purpose.
//
//  Backtest on MNQ:
//      Option A (close > prev close):              7 alerts, 7W/0L
//      Option B (close > prev close > 2-bars ago): 7 alerts, 6W/1L
//  Option A won on this data. We chose A for simplicity and earlier entry.
//
//  CHANGE C: 5-bar HIGH and LOW now logged in results CSV.
//  -------------------------------------------------------
//  v3.8 logged only the 5-bar close-delta. This was misleading because
//  many alerts hit a meaningful HIGH within 5 bars but gave it back by
//  bar 5. With brackets, the high is what matters (PT might be hit before
//  the 5-bar mark).
//
//  v3.9 logs:
//    - Future5BarsClose: close at bar+5 (same as before)
//    - Future5BarsHigh:  highest high in bars 1..5 (best possible exit)
//    - Future5BarsLow:   lowest low in bars 1..5 (worst drawdown)
//    - DeltaClose:       Future5BarsClose - BeepClose (was DeltaPoints)
//    - DeltaHigh:        Future5BarsHigh - BeepClose (max gain)
//    - DeltaLow:         Future5BarsLow - BeepClose (max drawdown)
//
//  This lets you analyze: "of my 50 alerts, how many had a +10 high
//  within 5 bars?" That's relevant for PT=10 trading.
//
// =============================================================================
//  WHAT'S UNCHANGED FROM v3.8
// =============================================================================
//
//  - Truncated lookback (H/L only from bars after last alert)
//  - Trend rule: Close at H bar > SMA at H bar (note: was High in v3.8 prose
//    but actually used Close in code; v3.9 is explicit about using Close)
//  - SIZE gate: range >= MinPullbackPoints (80 default)
//  - MinBarsBetweenAlerts (default 20)
//  - Daily session reset
//  - 5-min cooldown safety net
//  - Beep sequence (3 beeps, 1 sec apart)
//  - Chart markers
//
// =============================================================================
//  PARAMETER DEFAULTS (v3.9)
// =============================================================================
//    TrendMaPeriod          80
//    LookbackBars           80
//    MinPullbackPoints      80
//    AlertLowPct            0.25   <-- LOWERED from 0.35
//    AlertHighPct           0.70
//    MinBarsBetweenAlerts   20
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
//  THE 3 GATES IN v3.9
// =============================================================================
//
//  Gate 1 — TREND
//    Close at H bar > SMA at H bar
//    Translation: "When the peak was made, was the closing price above
//    the SMA trend line?" If yes, this is a real uptrend pullback.
//
//  Gate 2 — SIZE
//    range = H - L >= MinPullbackPoints (80 pts default)
//    Translation: "Was the dip meaningful, or just noise?"
//
//  Gate 3 — RECOVERY
//    AlertLowPct (0.25) <= recovery <= AlertHighPct (0.70)
//    AND Close[0] > Close[1] (this bar is green)
//    Translation: "Are we in the alert zone AND is this bar going up?"
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_TrendPullbackAlert_beta : Strategy
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
        // [v3.8] State for truncated lookback and daily reset
        //
        // lastAlertCurrentBar: CurrentBar value at the time of the last alert.
        //                      Used to limit the H/L scan window. -1 = no alert
        //                      yet today (use full lookback).
        // ---------------------------------------------------------------------
        private int lastAlertCurrentBar = -1;

        // ---------------------------------------------------------------------
        // [v3.5] Pending-evaluation queue for forward-performance logging
        // [v3.9] Now tracks 5-bar HIGH and LOW in addition to close
        // ---------------------------------------------------------------------
        private class PendingResult
        {
            public DateTime BeepBarTime;
            public int      BeepBarIndex;
            public double   BeepClose;
            public double   Recovery;
            public double   Range;
            public double   TrendMargin;    // h_close - sma_at_h
            public double   H, L;
            public string   HTime, LTime;

            // [v3.9 NEW] running tracking of high/low between beep and eval
            public double   FutureHigh;     // max High[k] for k=1..ResultEvalBars
            public double   FutureLow;      // min Low[k] for k=1..ResultEvalBars
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
                Description = "Trend + Pullback + Recovery alerter (v3.9). Truncated lookback, AlertLow=25%, close>prev close gate.";
                Name        = "scalper_TrendPullbackAlert_beta";
                Calculate   = Calculate.OnPriceChange;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 80;
                IsInstantiatedOnEachOptimizationIteration = true;

                TrendMaPeriod        = 80;
                LookbackBars         = 80;
                MinPullbackPoints    = 80;
                AlertLowPct          = 0.25;     // [v3.9] lowered from 0.35
                AlertHighPct         = 0.70;
                MinBarsBetweenAlerts = 20;
                CooldownMinutes      = 5;
                ResultEvalBars       = 5;
                ActiveStartTime      = 0;
                ActiveEndTime        = 235959;
                AlertSoundCount      = 3;
                AlertReminderSecs    = 1;
                EnableChartMarkers   = true;
                EnableAuditLog       = false;
                AuditLogPath         = @"C:\temp";
            }
            else if (State == State.DataLoaded)
            {
                trendMa = SMA(TrendMaPeriod);
                ResetState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] scalper_TrendPullbackAlert_beta v3.9 armed at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] Trend rule: Close at H bar > SMA at H bar. SMA period = {0}",
                    TrendMaPeriod));
                Print(string.Format("[INIT] Pullback: lookback up to {0} bars, min size {1} pts (TRUNCATED at last alert)",
                    LookbackBars, MinPullbackPoints));
                Print(string.Format("[INIT] Zone: recovery {0:P0}-{1:P0} AND Close[0] > Close[1]",
                    AlertLowPct, AlertHighPct));
                Print(string.Format("[INIT] Min bars between alerts: {0}",
                    MinBarsBetweenAlerts));
                Print(string.Format("[INIT] Cooldown: {0} bar-minutes (backup safety net)",
                    CooldownMinutes));
                Print(string.Format("[INIT] Result eval: {0} bars after each beep, with 5-bar HIGH/LOW",
                    ResultEvalBars));
                Print(string.Format("[INIT] Time window: {0:D6} to {1:D6}",
                    ActiveStartTime, ActiveEndTime));
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
            if (CurrentBar < Math.Max(TrendMaPeriod, LookbackBars) + 1) return;

            // -----------------------------------------------------------------
            // Session boundary - reset alert state daily
            // -----------------------------------------------------------------
            if (Bars.IsFirstBarOfSession)
            {
                if (lastAlertCurrentBar >= 0)
                {
                    Print(string.Format("[SESSION] New session at {0}. Resetting alert state.",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss")));
                }
                lastAlertCurrentBar = -1;
            }

            // -----------------------------------------------------------------
            // Section 0: Update pending results' running high/low, then evaluate
            // -----------------------------------------------------------------
            UpdatePendingHighLow();
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
            // Section B2: Compute the truncated lookback limit
            // -----------------------------------------------------------------
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

            // -----------------------------------------------------------------
            // Section C: Order-aware H and L (truncated)
            // -----------------------------------------------------------------
            double H = double.MinValue;
            int hBarsAgo = -1;
            for (int k = 1; k <= effectiveLookback; k++)
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

            DateTime hTime = (hBarsAgo > 0) ? Time[hBarsAgo] : DateTime.MinValue;
            DateTime lTime = (lBarsAgo > 0) ? Time[lBarsAgo] : DateTime.MinValue;

            // -----------------------------------------------------------------
            // Section D: Evaluate the 3 gates
            // -----------------------------------------------------------------

            // Gate 1: TREND - Close at H bar > SMA at H bar
            // Use Close[hBarsAgo] (the close at the H bar) and trendMa[hBarsAgo]
            double hBarClose = (hBarsAgo > 0 && hBarsAgo <= CurrentBar) ? Close[hBarsAgo] : double.NaN;
            double smaAtH = (hBarsAgo > 0 && hBarsAgo < CurrentBar) ? trendMa[hBarsAgo] : double.NaN;
            double trendMarginAtH = (!double.IsNaN(smaAtH) && !double.IsNaN(hBarClose)) ? (hBarClose - smaAtH) : 0;
            bool condTrend = !double.IsNaN(smaAtH) && !double.IsNaN(hBarClose) && (hBarClose > smaAtH);

            // Gate 2: SIZE
            bool condSize  = (range >= MinPullbackPoints) && (lBarsAgo > 0);

            // Gate 3: RECOVERY (zone) AND CLOSE-UP
            // [v3.9] Replaced cross-up rule with simpler "close > prev close"
            bool condZone   = recovery >= AlertLowPct && recovery <= AlertHighPct;
            bool condCloseUp = (CurrentBar >= 1) && (Close[0] > Close[1]);

            bool inCooldown = (lastAlertBarTime != DateTime.MinValue)
                && ((Time[0] - lastAlertBarTime).TotalMinutes < CooldownMinutes);

            // -----------------------------------------------------------------
            // Section E: Determine outcome
            // -----------------------------------------------------------------
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
                outcome = "FAIL_CLOSE_NOT_UP";   // [v3.9] new outcome
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
                Print(string.Format("[ALERT]   H_close={0:F2}, SMA(@H)={1:F2}, margin@H={2:+0.00;-0.00}",
                    hBarClose, smaAtH, trendMarginAtH));
                Print(string.Format("[ALERT]   H={0:F2} @{1}  L={2:F2} @{3}  range={4:F2}  effLookback={5}",
                    H, hTime.ToString("HH:mm"),
                    L, lTime.ToString("HH:mm"),
                    range, effectiveLookback));
                Print(string.Format("[ALERT]   Pullback={0:F2} pts, Recovery={1:P1} (zone {2:P0}-{3:P0}), CloseUp: {4:F2} > {5:F2}",
                    pullbackPts, recovery, AlertLowPct, AlertHighPct, Close[0], Close[1]));

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
                    Draw.ArrowUp(this, arrowTag, true, 0, L - (2 * TickSize),
                        Brushes.LimeGreen);
                    string label = string.Format("Pullback {0:F1}pts\nRecov {1:P0}",
                        pullbackPts, recovery);
                    Draw.Text(this, textTag, label, 0,
                        High[0] + (4 * TickSize), Brushes.LimeGreen);
                }

                WriteAuditRow(currentPrice, smaNow, smaAtH, trendMarginAtH, H, hTime, hBarsAgo,
                    L, lTime, lBarsAgo, range, recovery, pullbackPts,
                    effectiveLookback, condTrend, condSize, condZone, inCooldown, outcome, true);

                // Add to result-tracking queue with running high/low init
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
                // [v3.9] Initialize running high/low to the beep's own price
                // (will be updated as future bars arrive)
                pr.FutureHigh   = currentPrice;
                pr.FutureLow    = currentPrice;
                pendingResults.Add(pr);

                Print(string.Format("[ALERT]   Added to result queue (eval at bar+{0}). Queue size: {1}",
                    ResultEvalBars, pendingResults.Count));
            }

            // -----------------------------------------------------------------
            // Section G: Per-minute audit row
            // -----------------------------------------------------------------
            if (EnableAuditLog && IsFirstTickOfBar && CurrentBar != lastAuditedBar)
            {
                WriteAuditRow(currentPrice, smaNow, smaAtH, trendMarginAtH, H, hTime, hBarsAgo,
                    L, lTime, lBarsAgo, range, recovery, pullbackPts,
                    effectiveLookback, condTrend, condSize, condZone, inCooldown, outcome, false);
                lastAuditedBar = CurrentBar;
            }
        }

        // =====================================================================
        // [v3.9 NEW] UpdatePendingHighLow - track running max/min for each pending
        //
        // For every pending result, look at the most recent bar (Bar[0]) and
        // update the FutureHigh and FutureLow if this bar's high/low is more
        // extreme than what we've tracked so far.
        //
        // Skip the very first call (the bar of the alert itself); only count
        // bars AFTER the beep.
        // =====================================================================
        private void UpdatePendingHighLow()
        {
            if (pendingResults.Count == 0) return;

            foreach (PendingResult pr in pendingResults)
            {
                int barsElapsed = CurrentBar - pr.BeepBarIndex;
                if (barsElapsed <= 0) continue;  // skip the alert bar itself
                if (barsElapsed > ResultEvalBars) continue;  // already past eval

                // Use IsFirstTickOfBar to update only once per bar
                if (!IsFirstTickOfBar) continue;

                // Use the just-completed bar (Bar[1]) since we're at the start
                // of the new bar. Actually, on first tick of bar N, we want to
                // capture bar N-1's complete High/Low as part of the window.
                if (CurrentBar >= 1)
                {
                    double prevHigh = High[1];
                    double prevLow  = Low[1];
                    if (prevHigh > pr.FutureHigh) pr.FutureHigh = prevHigh;
                    if (prevLow  < pr.FutureLow)  pr.FutureLow  = prevLow;
                }
            }
        }

        // =====================================================================
        // EvaluatePendingResults - forward-performance logging at +ResultEvalBars
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
        // WriteResultRow - appends to results CSV
        // [v3.9] Now includes Future5BarsHigh/Low and DeltaHigh/Low
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
                            + "Recovery,Range,TrendMargin,H,H_Time,L,L_Time");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1:F2},{2},{3},{4:F2},{5:F2},{6:F2},"
                        + "{7:F2},{8:F2},{9:F2},{10},"
                        + "{11:F4},{12:F2},{13:F2},{14:F2},{15},{16:F2},{17}",
                        pr.BeepBarTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        pr.BeepClose,
                        ResultEvalBars,
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        evalClose,
                        pr.FutureHigh,
                        pr.FutureLow,
                        deltaClose,
                        deltaHigh,
                        deltaLow,
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
            beepCount             = 0;
            lastAlertBarTime      = DateTime.MinValue;
            lastAlertBar          = -1;
            lastAuditedBar        = -1;
            lastAlertCurrentBar   = -1;
            pendingResults.Clear();
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(10, 500)]
        [Display(Name="TrendMaPeriod",
            Description="SMA period for the trend filter. v3.9 trend rule: Close at H bar > SMA at H bar.",
            Order=1, GroupName="1. Trend Filter")]
        public int TrendMaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name="LookbackBars",
            Description="Maximum bars to look back for H/L. Truncated at last alert time. Default 80.",
            Order=2, GroupName="2. Pullback Size")]
        public int LookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="MinPullbackPoints",
            Description="Minimum points the H-L range must span. Default 80.",
            Order=3, GroupName="2. Pullback Size")]
        public int MinPullbackPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.95)]
        [Display(Name="AlertLowPct",
            Description="Lower edge of alert zone. v3.9 default 0.25 (was 0.35 in v3.8). Lowered after bracket-trade backtest showed earlier entries are better with PT/SL.",
            Order=4, GroupName="3. Trigger Zone")]
        public double AlertLowPct { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.99)]
        [Display(Name="AlertHighPct",
            Description="Upper edge of alert zone. Default 0.70.",
            Order=5, GroupName="3. Trigger Zone")]
        public double AlertHighPct { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name="MinBarsBetweenAlerts",
            Description="Minimum bars between any 2 alerts. Default 20. Prevents chop-trap alerts.",
            Order=6, GroupName="4. Alert Spacing")]
        public int MinBarsBetweenAlerts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="CooldownMinutes",
            Description="Bar-time minutes after a beep before another beep is allowed. Backup safety net. Default 5.",
            Order=7, GroupName="5. Cooldown")]
        public int CooldownMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="ResultEvalBars",
            Description="Bars after each beep to wait before measuring forward performance. Default 5. v3.9 also tracks 5-bar HIGH/LOW.",
            Order=8, GroupName="6. Result Tracking")]
        public int ResultEvalBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveStartTime",
            Description="Time to start watching (HHMMSS). 0 = midnight.",
            Order=9, GroupName="7. Time Window")]
        public int ActiveStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveEndTime",
            Description="Time to stop watching (HHMMSS). 235959 = 1 sec before midnight.",
            Order=10, GroupName="7. Time Window")]
        public int ActiveEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total number of beeps per alert. Default 3.",
            Order=11, GroupName="8. Beep Sequence")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=12, GroupName="8. Beep Sequence")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Draw green up-arrow at L and text label when alert fires.",
            Order=13, GroupName="9. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableAuditLog",
            Description="Write CSV row every minute (and on every alert). Useful for debugging. Off for live use.",
            Order=14, GroupName="10. Audit Log")]
        public bool EnableAuditLog { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for audit log AND results log. Auto-created. Default C:\\temp.",
            Order=15, GroupName="10. Audit Log")]
        public string AuditLogPath { get; set; }

        #endregion
    }
}
