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
// STRATEGY: scalper_RenkoStringPatternAlert v1.0
// AUTHOR:   Albert Feng / Drafted with help from Claude
// =============================================================================
//
// PURPOSE
// -------
// A two-layer Renko pattern alerter that uses STRING parameters for both
// the pattern to detect and the follow-up to confirm success. Replaces
// the count-based parameters (N greens / M greens) from
// scalper_RenkoTwoLayerAlert v1.
//
// LAYER 1 - STRING PATTERN MATCH
// ------------------------------
// Each completed Renko brick appends one bit to a running bit string:
//   1 = green (close > open), 0 = red (close < open).
// On every new brick, the strategy checks if the LAST N bits (where N =
// length of PatternToMatch) exactly equal PatternToMatch. If yes -> an
// "occurrence" is detected.
//
// Each pending occurrence then watches the next M bricks (where M =
// length of FollowUpPattern). After exactly M follow-up bricks have
// arrived, they are compared bit-for-bit to FollowUpPattern:
//   - Exact match -> SUCCESS (S)
//   - Any mismatch -> FAILURE (F)
//
// Default values:
//   PatternToMatch  = "011"   (red brick followed by 2 greens)
//   FollowUpPattern = "11"    (success needs 2 more greens, exact)
//
// So "011" + "11" -> success requires the brick sequence "01111" exactly
// (any other 5-bit ending starting with "011" is a failure).
//
// LAYER 2 - FAILURE STREAK ALERTER
// --------------------------------
// Tracks consecutive FAILUREs.
//   - After FailuresToArm strict consecutive Fs, the alert fires ONCE.
//   - Counter resets to 0 only when a SUCCESS occurs.
//   - Alert fires once per F-streak; does not re-fire until reset by an S.
//
// Special case: FailuresToArm = 0 means "alert on every OCCURRENCE
// DETECTION" (i.e., the moment the pattern is found, before outcome is
// known). Useful for low-frequency configurations to see everything.
//
// DAILY RESET
// -----------
// All state resets at 9:30 AM NY time each trading day. Uses TimeZoneInfo
// for DST safety. Bricks before today's 9:30 ET are ignored.
//
// NO ORDERS
// ---------
// This strategy places NO orders. It is a notification tool only.
//
// USAGE
// -----
// 1. Add to a Renko chart, e.g. 20-tick / 5-pt bricks on MNQ.
// 2. Set PatternToMatch and FollowUpPattern (strict 0/1 chars, max 10 each).
// 3. Set FailuresToArm (default 2; use 0 to alert on every detection).
// 4. Enable. Beeps + chart markers + CSV logging begin after 9:30 ET reset.
//
// CSV FILES (in AuditLogPath, default C:\temp)
// --------------------------------------------
// scalper_RenkoStringPatternAlert_Occurrences.csv - one row per Layer-1
//   resolved occurrence (S or F)
// scalper_RenkoStringPatternAlert_Alerts.csv     - one row per fired alert
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_RenkoStringPatternAlert : Strategy
    {
        #region Variables

        // ---- Bit string of bricks since today's reset ----
        private List<int> bricks = new List<int>();

        // ---- Layer-2 state ----
        private int  failureCount = 0;
        private bool alertArmedAndFired = false;

        // ---- Daily numbering for CSV correlation ----
        private int dailyOccurrenceNumber = 0;
        private int dailyAlertNumber = 0;

        // ---- Beep cadence ----
        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        // ---- Daily reset state ----
        private DateTime currentTradingDateNy = DateTime.MinValue;
        private TimeZoneInfo nyTz;
        private const int RESET_HOUR_NY   = 9;
        private const int RESET_MINUTE_NY = 30;

        // ---- Compiled patterns (parsed once at State.Realtime) ----
        // These are int arrays of 0/1 derived from the user's strings.
        // null = invalid configuration; strategy will refuse to operate.
        private int[] compiledPattern  = null;
        private int[] compiledFollowUp = null;
        private bool configValid = false;

        // ---- Pending occurrences awaiting follow-up resolution ----
        private class PendingOccurrence
        {
            public int       PatternStartIndex;    // index in bricks[] of pattern's first bit
            public int       PatternEndIndex;      // index of pattern's last bit
            public int       BricksWatched;        // how many follow-up bricks examined
            public bool      FollowUpMismatch;     // true if any follow-up byte didn't match
            public DateTime  OccurrenceTime;
            public double    PatternEndPrice;
        }

        private List<PendingOccurrence> pendingOccurrences = new List<PendingOccurrence>();

        #endregion

        // =====================================================================
        // OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Two-layer Renko string-pattern alerter. Detects exact bit pattern, watches exact follow-up, alerts after K consecutive failures. Resets at 9:30 ET. Places NO orders.";
                Name        = "scalper_RenkoStringPatternAlert";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- Layer 1 string parameters ----
                PatternToMatch   = "011";
                FollowUpPattern  = "11";

                // ---- Layer 2 ----
                FailuresToArm    = 2;

                // ---- Beep ----
                AlertSoundCount   = 3;
                AlertReminderSecs = 1;

                // ---- Visuals ----
                EnableChartMarkers = true;
                ShowSuccessMarker  = true;
                ShowFailureMarker  = true;

                // ---- Logging ----
                AuditLogPath = @"C:\temp";

                // ---- Memory cap ----
                MaxBitsKept = 5000;
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
                ResetState();
            }
            else if (State == State.Realtime)
            {
                // ---- Validate & compile pattern strings ----
                configValid = TryCompilePatterns();

                Print("================================================================");
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlert v1.0 at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));

                if (!configValid)
                {
                    Print("[INIT] *** CONFIGURATION INVALID *** Strategy will not process bricks.");
                    Print("[INIT] Fix the parameter strings and re-enable.");
                    return;
                }

                Print(string.Format("[INIT] PatternToMatch  = \"{0}\" (length {1})",
                    PatternToMatch, compiledPattern.Length));
                Print(string.Format("[INIT] FollowUpPattern = \"{0}\" (length {1})",
                    FollowUpPattern, compiledFollowUp.Length));
                Print(string.Format("[INIT] FailuresToArm   = {0} {1}",
                    FailuresToArm,
                    FailuresToArm == 0 ? "(=> alert on every OCCURRENCE DETECTION)"
                                        : "(=> alert when this many consecutive FAILURES accumulate)"));
                Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));
                Print(string.Format("[INIT] Beep: {0} beeps, {1}s apart", AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));
                Print("[INIT] This strategy places NO orders.");
            }
        }

        // =====================================================================
        // TryCompilePatterns - validate and convert the string parameters
        //
        // Rules:
        //   - Strict: only '0' and '1' allowed
        //   - Length: 1..10 chars each
        //   - Empty / whitespace / nulls => invalid
        //
        // On success: fills compiledPattern[] and compiledFollowUp[], returns true.
        // On failure: prints reason, returns false.
        // =====================================================================
        private bool TryCompilePatterns()
        {
            compiledPattern  = TryCompileOne(PatternToMatch,  "PatternToMatch");
            compiledFollowUp = TryCompileOne(FollowUpPattern, "FollowUpPattern");
            return compiledPattern != null && compiledFollowUp != null;
        }

        private int[] TryCompileOne(string s, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                Print(string.Format("[VALIDATE] *** {0} is empty. Must be 1-10 chars of '0' or '1' only. ***",
                    fieldName));
                return null;
            }
            if (s.Length > 10)
            {
                Print(string.Format("[VALIDATE] *** {0} is {1} chars; max allowed is 10. Pattern: \"{2}\" ***",
                    fieldName, s.Length, s));
                return null;
            }
            int[] result = new int[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '0') result[i] = 0;
                else if (c == '1') result[i] = 1;
                else
                {
                    Print(string.Format("[VALIDATE] *** {0} contains invalid char '{1}' at position {2}. Only '0' and '1' allowed. ***",
                        fieldName, c, i));
                    return null;
                }
            }
            return result;
        }

        // =====================================================================
        // OnBarUpdate - one call per closed Renko brick
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            if (!configValid) return;   // invalid config => do nothing

            // -----------------------------------------------------------------
            // Daily reset check at 9:30 NY
            // -----------------------------------------------------------------
            DateTime barTimeNy = TimeZoneInfo.ConvertTime(Time[0], nyTz);
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
                    Print(string.Format("[RESET] New trading day at 9:30 NY ({0:yyyy-MM-dd}). Bricks today before reset: {1}, occurrences: {2}, alerts: {3}",
                        effectiveTradingDate, bricks.Count, dailyOccurrenceNumber, dailyAlertNumber));
                }
                else
                {
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}",
                        effectiveTradingDate));
                }
                currentTradingDateNy = effectiveTradingDate;
                ResetState();
            }

            // -----------------------------------------------------------------
            // Append this brick's bit
            // -----------------------------------------------------------------
            int thisBit = (Close[0] > Open[0]) ? 1 : 0;
            bricks.Add(thisBit);

            if (bricks.Count > MaxBitsKept)
            {
                int removeCount = bricks.Count - MaxBitsKept;
                bricks.RemoveRange(0, removeCount);
                foreach (var po in pendingOccurrences)
                {
                    po.PatternStartIndex -= removeCount;
                    po.PatternEndIndex   -= removeCount;
                }
            }

            int currentBrickIdx = bricks.Count - 1;
            double currentPrice = Close[0];

            // -----------------------------------------------------------------
            // Update pending occurrences (Layer 1 follow-up evaluation)
            // -----------------------------------------------------------------
            List<PendingOccurrence> resolved = new List<PendingOccurrence>();

            foreach (var po in pendingOccurrences)
            {
                if (currentBrickIdx <= po.PatternEndIndex) continue;

                // This brick is a follow-up byte. Compare to FollowUpPattern[BricksWatched].
                int expectedBit = compiledFollowUp[po.BricksWatched];
                if (thisBit != expectedBit)
                    po.FollowUpMismatch = true;

                po.BricksWatched++;

                if (po.BricksWatched >= compiledFollowUp.Length)
                {
                    // Done watching this occurrence
                    string outcome = po.FollowUpMismatch ? "F" : "S";
                    HandleOccurrenceOutcome(po, outcome, currentPrice);
                    resolved.Add(po);
                }
            }
            foreach (var po in resolved)
                pendingOccurrences.Remove(po);

            // -----------------------------------------------------------------
            // Detect new Layer-1 occurrence ending at this brick
            //
            // The "last N bits" must exactly equal compiledPattern[].
            // -----------------------------------------------------------------
            int patternLen = compiledPattern.Length;
            if (bricks.Count >= patternLen)
            {
                int patternStartIdx = bricks.Count - patternLen;
                bool isMatch = true;
                for (int i = 0; i < patternLen; i++)
                {
                    if (bricks[patternStartIdx + i] != compiledPattern[i])
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    var po = new PendingOccurrence
                    {
                        PatternStartIndex = patternStartIdx,
                        PatternEndIndex   = patternStartIdx + patternLen - 1,
                        BricksWatched     = 0,
                        FollowUpMismatch  = false,
                        OccurrenceTime    = Time[0],
                        PatternEndPrice   = currentPrice
                    };
                    pendingOccurrences.Add(po);

                    dailyOccurrenceNumber++;
                    Print(string.Format("[L1] Pattern \"{0}\" detected at brick {1} ({2}). Occurrence #{3} pending {4} follow-up bricks.",
                        PatternToMatch,
                        currentBrickIdx,
                        Time[0].ToString("HH:mm:ss"),
                        dailyOccurrenceNumber,
                        compiledFollowUp.Length));

                    // ----- Special case: FailuresToArm == 0 -----
                    // Alert immediately on detection, before outcome is known.
                    if (FailuresToArm == 0)
                    {
                        FireAlertOnDetection(po);
                    }
                }
            }
        }

        // =====================================================================
        // HandleOccurrenceOutcome - called when a Layer-1 occurrence resolves
        // =====================================================================
        private void HandleOccurrenceOutcome(PendingOccurrence po, string outcome, double currentPrice)
        {
            bool prevAlertArmed = alertArmedAndFired;

            if (outcome == "S")
            {
                failureCount = 0;
                alertArmedAndFired = false;
                Print(string.Format("[L1] *** SUCCESS *** Occurrence resolved at {0}. failureCount reset to 0.",
                    Time[0].ToString("HH:mm:ss")));
            }
            else // F
            {
                failureCount++;
                Print(string.Format("[L1] *** FAILURE *** Occurrence resolved at {0}. failureCount = {1}",
                    Time[0].ToString("HH:mm:ss"), failureCount));

                // Layer 2: arm and fire ONCE per F-streak that reaches the threshold.
                // (Only when FailuresToArm > 0; if 0 we already alerted on detection.)
                if (FailuresToArm > 0 && failureCount >= FailuresToArm && !alertArmedAndFired)
                {
                    FireAlertOnFailureStreak();
                    alertArmedAndFired = true;
                }
            }

            WriteOccurrenceRow(po, outcome, currentPrice, prevAlertArmed);

            // Chart marker on the brick where the outcome landed
            if (EnableChartMarkers)
            {
                if (outcome == "S" && ShowSuccessMarker)
                {
                    string tag = "RSP_S_" + CurrentBar;
                    Draw.ArrowUp(this, tag, true, 0, Low[0] - (2 * TickSize), Brushes.LimeGreen);
                }
                else if (outcome == "F" && ShowFailureMarker)
                {
                    string tag = "RSP_F_" + CurrentBar;
                    Draw.ArrowDown(this, tag, true, 0, High[0] + (2 * TickSize), Brushes.OrangeRed);
                }
            }
        }

        // =====================================================================
        // FireAlertOnDetection - used when FailuresToArm == 0
        // (Alert at the moment pattern is detected, before outcome.)
        // =====================================================================
        private void FireAlertOnDetection(PendingOccurrence po)
        {
            dailyAlertNumber++;
            Print("================================================================");
            Print(string.Format("[ALERT] *** ALERT-ON-DETECTION at {0}. Pattern \"{1}\" found. Daily alert #{2}. ***",
                Time[0].ToString("HH:mm:ss"), PatternToMatch, dailyAlertNumber));

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar;
                Draw.Text(this, txt, string.Format("ALERT #{0}\nON DETECT", dailyAlertNumber),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow("DETECT");
        }

        // =====================================================================
        // FireAlertOnFailureStreak - used when FailuresToArm > 0
        // =====================================================================
        private void FireAlertOnFailureStreak()
        {
            dailyAlertNumber++;
            Print("================================================================");
            Print(string.Format("[ALERT] *** ARMED at {0}! failureCount={1} reached threshold {2}. Daily alert #{3}. ***",
                Time[0].ToString("HH:mm:ss"), failureCount, FailuresToArm, dailyAlertNumber));
            Print("[ALERT] Watch the chart. Next pattern occurrence is your trial.");

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar;
                Draw.Text(this, txt, string.Format("ALERT #{0}\n{1} F in a row", dailyAlertNumber, failureCount),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow("STREAK");
        }

        // =====================================================================
        // Beep cadence (driven by brick close; see note in v1 about timer)
        // =====================================================================
        private void RunBeepCadence()
        {
            if (beepCount > 0 && beepCount < AlertSoundCount)
            {
                double secs = (DateTime.Now - lastBeepWallClock).TotalSeconds;
                if (secs >= AlertReminderSecs)
                {
                    PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
                    lastBeepWallClock = DateTime.Now;
                    beepCount++;
                }
            }
            if (beepCount >= AlertSoundCount) beepCount = 0;
        }

        // =====================================================================
        // CSV: per-occurrence rows
        // =====================================================================
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice, bool prevAlertArmed)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlert");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("OccurrenceTime,OutcomeTime,BrickIndexAtPatternEnd,Pattern,FollowUp,"
                            + "ActualFollowUp,Outcome,PatternEndPrice,OutcomePrice,"
                            + "FailureCountAfter,AlertArmedAfter,DailyOccurrenceNumber");
                    }

                    // Reconstruct the actual follow-up bricks that were watched
                    string actualFollow = "";
                    int startIdx = po.PatternEndIndex + 1;
                    for (int i = 0; i < compiledFollowUp.Length && (startIdx + i) < bricks.Count; i++)
                        actualFollow += bricks[startIdx + i].ToString();

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9},{10},{11}",
                        po.OccurrenceTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        po.PatternEndIndex,
                        PatternToMatch,
                        FollowUpPattern,
                        actualFollow,
                        outcome,
                        po.PatternEndPrice,
                        endPrice,
                        failureCount,
                        alertArmedAndFired ? "YES" : "NO",
                        dailyOccurrenceNumber));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-OCC] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // CSV: per-alert rows
        // =====================================================================
        private void WriteAlertRow(string alertType)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlert");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("AlertTime,DailyAlertNumber,AlertType,FailureCountAtAlert,"
                            + "FailuresToArm,Pattern,FollowUp,CurrentPrice");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2}",
                        Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        alertType,           // "DETECT" or "STREAK"
                        failureCount,
                        FailuresToArm,
                        PatternToMatch,
                        FollowUpPattern,
                        Close[0]));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ALERT] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // ResetState
        // =====================================================================
        private void ResetState()
        {
            bricks.Clear();
            pendingOccurrences.Clear();
            failureCount = 0;
            alertArmedAndFired = false;
            dailyOccurrenceNumber = 0;
            dailyAlertNumber = 0;
            beepCount = 0;
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name="PatternToMatch",
            Description="Bit pattern to detect, 1-10 chars of '0' (red) or '1' (green). Default \"011\". Examples: \"011\", \"100\", \"0011\".",
            Order=1, GroupName="1. Layer 1 - Pattern")]
        public string PatternToMatch { get; set; }

        [NinjaScriptProperty]
        [Display(Name="FollowUpPattern",
            Description="Required follow-up bricks for SUCCESS, 1-10 chars of '0' or '1'. After PatternToMatch is detected, the next bricks must match this EXACTLY for SUCCESS; any mismatch = FAILURE. Default \"11\".",
            Order=2, GroupName="1. Layer 1 - Pattern")]
        public string FollowUpPattern { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name="FailuresToArm",
            Description="K: consecutive FAILURES required to fire alert. Default 2. Set to 0 to fire on EVERY pattern detection (regardless of outcome).",
            Order=3, GroupName="2. Layer 2 - Alert")]
        public int FailuresToArm { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert. Default 3.",
            Order=4, GroupName="3. Beep")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=5, GroupName="3. Beep")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Master toggle for all chart drawings.",
            Order=6, GroupName="4. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowSuccessMarker",
            Description="Draw a green up-arrow on each SUCCESS outcome.",
            Order=7, GroupName="4. Visuals")]
        public bool ShowSuccessMarker { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowFailureMarker",
            Description="Draw an orange-red down-arrow on each FAILURE outcome.",
            Order=8, GroupName="4. Visuals")]
        public bool ShowFailureMarker { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for CSV files. Auto-created. Default C:\\temp.",
            Order=9, GroupName="5. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name="MaxBitsKept",
            Description="Max in-memory bit string length before trimming. Default 5000.",
            Order=10, GroupName="6. Advanced")]
        public int MaxBitsKept { get; set; }

        #endregion
    }
}
