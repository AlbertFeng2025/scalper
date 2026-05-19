#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;#region Using declarations
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
// STRATEGY: scalper_RenkoStringPatternAlertEMA v1.3.2
// AUTHOR:   Albert Feng / Drafted with help from Claude
// REPLACES: v1.3.1
// =============================================================================
//
// v1.3.2 CHANGES vs v1.3.1
// ------------------------
//
// CHANGE 1 - EMA qualification uses OPEN, not CLOSE
//   Visual intent: for a GREEN brick, the body bottom is Open and the top
//   is Close. v1.3.1 checked Close > EMA, which lets the brick straddle the
//   EMA (Open below, Close above) and still qualify. v1.3.2 checks Open[0],
//   which means the entire green brick body must sit above both EMAs.
//
//   Logic: emaQualified = (Open[0] > ema1) && (Open[0] > ema2)
//   Logged column "Open" is the brick open used; PatternEndPrice continues
//   to log Close (unchanged).
//
//   NOTE: for a RED pattern-end brick (pattern ending in '0'), Open is the
//   TOP of the body and Close is the BOTTOM. Open > EMA is a weaker filter
//   than Close > EMA for red bricks. For green-ending patterns like "011"
//   this matches your visual reading.
//
// CHANGE 2 - PostAlertOutcomeString
//   New column in occurrences CSV that captures the bit (S=1, F=0) of the
//   FIRST EMA-qualified outcome that resolves AFTER each alert fires.
//
//   Mechanics:
//     - Counter `pendingPostAlertCaptures` starts at 0.
//     - Every time an alert fires (in FireAlert), the counter increments.
//     - When the next EMA-qualified outcome resolves AND the counter > 0,
//       its bit is appended to postAlertOutcomeString and the counter
//       decrements.
//     - Non-qualified outcomes are skipped (don't decrement the counter,
//       don't get appended).
//     - The alert-triggering outcome itself is NOT captured for its own
//       alert - capture starts on the NEXT qualified outcome.
//     - If alerts overlap (alert 2 fires before alert 1's capture lands),
//       the counter accumulates and each subsequent qualified outcome
//       gets captured in order.
//
//   Use case: with AlertPattern = "011" (chase next after F-S-S), the
//   PostAlertOutcomeString is the success/failure of every chase trade.
//   Counting '1's in PostAlertOutcomeString / length = chase-trade hit rate.
//
// =============================================================================
//
// v1.3 CHANGES vs v1.2.1
// ----------------------
//
// CHANGE 1 - Bit encoding flipped to match brick convention
//   Old (v1.0 - v1.2.1):  S = 0, F = 1
//   New (v1.3):           S = 1, F = 0
//   This now matches the brick-bit convention (green=1, red=0) and the
//   natural reading: "success counts as a 1, failure counts as a 0."
//   Applied identically to both OutcomeString and TradedOutcomeString.
//
//   Example: occurrences resolving as S F F S F give:
//     v1.2.1:  "01101"   (F=1, S=0)
//     v1.3:    "10010"   (S=1, F=0)
//
// CHANGE 2 - AlertMode / Threshold REMOVED
//   The OnDetection / AfterFailures / AfterSuccesses dropdown and Threshold
//   integer are gone. Replaced by a single AlertPattern string.
//
// CHANGE 3 - AlertPattern (suffix match) ADDED
//   New parameter: AlertPattern (1-10 chars of '0' or '1', default "011").
//   Alert fires when the TAIL of TradedOutcomeString equals AlertPattern.
//
//   Match is against TradedOutcomeString (EMA-qualified only), not
//   OutcomeString. So "011" fires after F, S, S in the last three
//   EMA-qualified occurrences (you'd be chasing the next success).
//
//   Matching is sliding-window: every time a new char is appended to
//   TradedOutcomeString, we check if the last AlertPattern.Length chars
//   equal AlertPattern. If yes, fire alert. Can fire repeatedly.
//
//   Example, AlertPattern = "100":
//     Stream of EMA-qualified outcomes: S F F F S F F
//     TradedOutcomeString grows:        1 10 100 1000 10001 100010 1000100
//     Alerts fire after positions:      ----- ^^^ -------- ------ ^^^^^^^
//                                            (100)            (100 in tail)
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_RenkoStringPatternAlertEMA : Strategy
    {
        #region Variables

        // ---- Bit string of bricks since today's reset ----
        private List<int> bricks = new List<int>();

        // ---- Outcome strings ----
        // OutcomeString: ALL resolved occurrences (S=1, F=0)
        private StringBuilder outcomeString = new StringBuilder();
        // TradedOutcomeString: only EMA-qualified occurrences (S=1, F=0)
        private StringBuilder tradedOutcomeString = new StringBuilder();
        // [v1.3.2] PostAlertOutcomeString: bit of the NEXT EMA-qualified outcome
        // after each alert. Grows by one bit per captured outcome. Non-qualified
        // outcomes are skipped (don't decrement the counter).
        private StringBuilder postAlertOutcomeString = new StringBuilder();
        // [v1.3.2] How many EMA-qualified outcomes are still owed to past alerts.
        // Incremented when an alert fires, decremented when a qualified outcome
        // is captured. Allows alert overlap.
        private int pendingPostAlertCaptures = 0;

        // ---- Daily numbering for CSV correlation ----
        private int dailyOccurrenceNumber = 0;
        private int dailyTradedOccurrenceNumber = 0;
        private int dailyAlertNumber = 0;

        // ---- Beep cadence ----
        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        // ---- Daily reset state ----
        private DateTime currentTradingDateNy = DateTime.MinValue;
        private TimeZoneInfo nyTz;
        private const int RESET_HOUR_NY   = 9;
        private const int RESET_MINUTE_NY = 30;

        // ---- Compiled patterns ----
        private int[] compiledPattern  = null;
        private int[] compiledFollowUp = null;
        private string compiledAlertPattern = null;   // [v1.3] kept as string for direct tail compare
        private bool configValid = false;

        // ---- EMA indicator references ----
        private EMA ema1;
        private EMA ema2;

        // ---- Pending occurrences awaiting follow-up resolution ----
        private class PendingOccurrence
        {
            public int       PatternStartIndex;
            public int       PatternEndIndex;
            public int       BricksWatched;
            public bool      FollowUpMismatch;
            public DateTime  OccurrenceTime;
            public double    PatternEndPrice;

            public bool      EmaQualified;
            public double    Ema1AtDetect;
            public double    Ema2AtDetect;
            public double    OpenAtDetect;   // [v1.3.2] open of pattern's closing brick

            public int       OccurrenceNumberAtDetect;
            public int       TradedOccurrenceNumberAtDetect;
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
                Description = "Renko string-pattern alerter (v1.3.2). EMA qualification uses Open (whole green brick body above EMAs). New PostAlertOutcomeString column captures chase-trade outcomes. Places NO orders.";
                Name        = "scalper_RenkoStringPatternAlertEMA";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- Layer 1 string parameters ----
                PatternToMatch   = "011";
                FollowUpPattern  = "1";

                // ---- [v1.3] AlertPattern (replaces AlertMode + Threshold) ----
                AlertPattern     = "01";

                // ---- EMA filter ----
                EMA1Period       = 20;
                EMA2Period       = 9;

                // ---- Beep ----
                AlertSoundCount   = 3;
                AlertReminderSecs = 1;

                // ---- Visuals ----
                EnableChartMarkers = true;
                ShowOutcomeLabels  = true;

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

                ema1 = EMA(EMA1Period);
                ema2 = EMA(EMA2Period);
                configValid = TryCompilePatterns();
                ResetState();
            }
            else if (State == State.Realtime)
            {
                 

                Print("================================================================");
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlertEMA v1.3.2 at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
				// ---- Bar type / brick size info ----
                Print(string.Format("[INIT] BarsPeriod.BarsPeriodType = {0}",
                    BarsPeriod.BarsPeriodType));
                Print(string.Format("[INIT] BarsPeriod.Value  = {0}  (for standard Renko: brick size in ticks)",
                    BarsPeriod.Value));
                Print(string.Format("[INIT] BarsPeriod.Value2 = {0}  (used by UniRenko/BetterRenko for reversal offset; ignore for standard Renko)",
                    BarsPeriod.Value2));
                Print(string.Format("[INIT] Instrument TickSize = {0}  -> brick size in price = {1}",
                    TickSize, BarsPeriod.Value * TickSize));
                Print(string.Format("[INIT] Run start (local): {0}   (UTC: {1})   (NY: {2})",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));

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
                Print(string.Format("[INIT] AlertPattern    = \"{0}\" (length {1})",
                    compiledAlertPattern, compiledAlertPattern.Length));
                Print("[INIT] Bit encoding: SUCCESS=1, FAILURE=0 (matches brick green=1/red=0).");
                Print("[INIT] AlertPattern is suffix-matched against OutcomeString_EmaQualified (EMA-qualified outcomes only).");
                Print("[INIT] Alert fires every time the tail of OutcomeString_EmaQualified equals AlertPattern.");
                Print(string.Format("[INIT] EMA1 period = {0}, EMA2 period = {1}",
                    EMA1Period, EMA2Period));
                Print("[INIT] EMA filter: trade only when Open[0] > EMA1 AND Open[0] > EMA2 at pattern's last brick (i.e. green-brick body must sit entirely above both EMAs).");
                Print("[INIT] PostAlertOutcomeString: after each alert, captures the bit of the NEXT EMA-qualified outcome (overlap-safe).");
                Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));
                Print(string.Format("[INIT] Beep: {0} beeps, {1}s apart", AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));
                Print("[INIT] This strategy places NO orders.");
            }
        }

        // =====================================================================
        // TryCompilePatterns
        // =====================================================================
        private bool TryCompilePatterns()
        {
            compiledPattern  = TryCompileOne(PatternToMatch,  "PatternToMatch");
            compiledFollowUp = TryCompileOne(FollowUpPattern, "FollowUpPattern");

            // [v1.3] Validate AlertPattern; keep as string for direct tail compare.
            int[] alertBits = TryCompileOne(AlertPattern, "AlertPattern");
            compiledAlertPattern = (alertBits != null) ? AlertPattern : null;

            return compiledPattern != null
                && compiledFollowUp != null
                && compiledAlertPattern != null;
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
            if (!configValid) return;

            // ---- Daily reset check ----
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
                    Print(string.Format("[RESET] New trading day at 9:30 NY ({0:yyyy-MM-dd}). Bricks today before reset: {1}, occurrences: {2}, traded: {3}, alerts: {4}, post-alert captures: {5}, pending captures dropped: {6}",
                        effectiveTradingDate, bricks.Count, dailyOccurrenceNumber, dailyTradedOccurrenceNumber, dailyAlertNumber,
                        postAlertOutcomeString.Length, pendingPostAlertCaptures));
                }
                else
                {
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}",
                        effectiveTradingDate));
                }
                currentTradingDateNy = effectiveTradingDate;
                ResetState();
            }

            // ---- Append this brick's bit ----
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

            // ---- Resolve pending occurrences ----
            List<PendingOccurrence> resolved = new List<PendingOccurrence>();

            foreach (var po in pendingOccurrences)
            {
                if (currentBrickIdx <= po.PatternEndIndex) continue;

                int expectedBit = compiledFollowUp[po.BricksWatched];
                if (thisBit != expectedBit)
                    po.FollowUpMismatch = true;

                po.BricksWatched++;

                if (po.BricksWatched >= compiledFollowUp.Length)
                {
                    string outcome = po.FollowUpMismatch ? "F" : "S";
                    HandleOccurrenceOutcome(po, outcome, currentPrice);
                    resolved.Add(po);
                }
            }
            foreach (var po in resolved)
                pendingOccurrences.Remove(po);

            // ---- Detect new occurrence ending at this brick ----
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
                    // [v1.3.2] EMA filter uses OPEN of the pattern's closing brick.
                    // For a green brick, Open = body bottom, so the entire body must
                    // be above both EMAs to qualify.
                    double ema1Val = ema1[0];
                    double ema2Val = ema2[0];
                    double openPrice = Open[0];
                    bool emaQualified = (openPrice > ema1Val) && (openPrice > ema2Val);

                    var po = new PendingOccurrence
                    {
                        PatternStartIndex = patternStartIdx,
                        PatternEndIndex   = patternStartIdx + patternLen - 1,
                        BricksWatched     = 0,
                        FollowUpMismatch  = false,
                        OccurrenceTime    = Time[0],
                        PatternEndPrice   = currentPrice,
                        EmaQualified      = emaQualified,
                        Ema1AtDetect      = ema1Val,
                        Ema2AtDetect      = ema2Val,
                        OpenAtDetect      = openPrice   // [v1.3.2]
                    };
                    pendingOccurrences.Add(po);

                    dailyOccurrenceNumber++;
                    if (emaQualified) dailyTradedOccurrenceNumber++;

                    po.OccurrenceNumberAtDetect = dailyOccurrenceNumber;
                    po.TradedOccurrenceNumberAtDetect = emaQualified ? dailyTradedOccurrenceNumber : 0;

                    Print(string.Format("[L1] Pattern \"{0}\" detected at brick {1} ({2}). Occurrence #{3} pending {4} follow-up bricks. Open={5:F2}, Close={6:F2}, EMA1={7:F2}, EMA2={8:F2}, EmaQualified={9}",
                        PatternToMatch,
                        currentBrickIdx,
                        Time[0].ToString("HH:mm:ss"),
                        dailyOccurrenceNumber,
                        compiledFollowUp.Length,
                        openPrice,
                        currentPrice,
                        ema1Val,
                        ema2Val,
                        emaQualified ? "YES" : "NO"));
                }
            }
        }

        // =====================================================================
        // HandleOccurrenceOutcome
        // [v1.3] S=1, F=0 encoding. AlertPattern suffix match on TradedOutcomeString.
        // =====================================================================
        private void HandleOccurrenceOutcome(PendingOccurrence po, string outcome, double currentPrice)
        {
            // [v1.3] Update OutcomeString: S=1, F=0
            outcomeString.Append(outcome == "S" ? '1' : '0');

            // [v1.3] Update TradedOutcomeString (only EMA-qualified): S=1, F=0
            bool firedAlert = false;
            bool capturedPostAlert = false;
            char capturedBit = '\0';
            if (po.EmaQualified)
            {
                tradedOutcomeString.Append(outcome == "S" ? '1' : '0');

                Print(string.Format("[L1] *** {0} *** at {1} (EMA-qualified). OutcomeString_EmaQualified = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
                    tradedOutcomeString.ToString()));

                // [v1.3.2] Post-alert capture: if any prior alert is still owed a
                // capture, this qualified outcome consumes one slot. Do this BEFORE
                // checking for a NEW alert on this same outcome - otherwise an alert
                // firing on this outcome would capture itself.
                if (pendingPostAlertCaptures > 0)
                {
                    capturedBit = outcome == "S" ? '1' : '0';
                    postAlertOutcomeString.Append(capturedBit);
                    pendingPostAlertCaptures--;
                    capturedPostAlert = true;
                    Print(string.Format("[POSTALERT] Captured outcome bit '{0}' for prior alert. PostAlertOutcomeString = \"{1}\". Pending captures remaining = {2}.",
                        capturedBit, postAlertOutcomeString.ToString(), pendingPostAlertCaptures));
                }

                // [v1.3] Suffix match against AlertPattern (may add a new pending capture)
                if (TradedTailMatchesAlertPattern())
                {
                    FireAlert();
                    firedAlert = true;
                }
            }
            else
            {
                Print(string.Format("[L1] *** {0} *** at {1} (NOT EMA-qualified, not added to OutcomeString_EmaQualified). OutcomeString_NoEmaFilter = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
                    outcomeString.ToString()));
            }

            WriteOccurrenceRow(po, outcome, currentPrice, firedAlert, capturedPostAlert, capturedBit);

            // PT/S or PT/F text label at the outcome brick
            if (EnableChartMarkers && ShowOutcomeLabels)
            {
                string tag = "RSP_OUT_" + CurrentBar + "_" + po.OccurrenceNumberAtDetect;
                string prefix = po.EmaQualified ? "###" : "";
				string label = prefix + (outcome == "S" ? "PT/S" : "PT/F");
                Brush color = outcome == "S" ? Brushes.LimeGreen : Brushes.Red;

                double yOffset = outcome == "S" ? (3 * TickSize) : -(3 * TickSize);
                double yPos = outcome == "S" ? (High[0] + yOffset) : (Low[0] + yOffset);

                Draw.Text(this, tag, label, 0, yPos, color);
            }
        }

        // =====================================================================
        // [v1.3] TradedTailMatchesAlertPattern
        // Returns true iff the LAST AlertPattern.Length chars of tradedOutcomeString
        // are exactly equal to AlertPattern.
        // =====================================================================
        private bool TradedTailMatchesAlertPattern()
        {
            int patLen = compiledAlertPattern.Length;
            if (tradedOutcomeString.Length < patLen) return false;

            int start = tradedOutcomeString.Length - patLen;
            for (int i = 0; i < patLen; i++)
            {
                if (tradedOutcomeString[start + i] != compiledAlertPattern[i])
                    return false;
            }
            return true;
        }

        // =====================================================================
        // [v1.3] FireAlert - unified single alert function
        // =====================================================================
        private void FireAlert()
        {
            dailyAlertNumber++;

            // [v1.3.2] Owe one post-alert capture to this alert
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern \"{0}\" matched the tail of OutcomeString_EmaQualified at {1}. Daily alert #{2}. PendingPostAlertCaptures = {3}. ***",
                compiledAlertPattern, Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] OutcomeString_EmaQualified tail: \"...{0}\"",
                tradedOutcomeString.Length >= compiledAlertPattern.Length
                    ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - compiledAlertPattern.Length)
                    : tradedOutcomeString.ToString()));
            Print("[ALERT] Watch the chart. Next EMA-qualified pattern occurrence is your trial.");

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Text(this, txt, string.Format("ALERT #{0}\nAP=\"{1}\"", dailyAlertNumber, compiledAlertPattern),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow();
        }

        // =====================================================================
        // CSV: per-occurrence rows
        // =====================================================================
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice, bool firedAlert, bool capturedPostAlert, char capturedBit)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                // [v1.3.2] New filename to prevent collision with v1.3.1 files
                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_v132_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                     
                    if (!fileExists)
                    {
                        // ---- Schema + session header ----
                        sw.WriteLine("# schema_version=1.3.2");
                        sw.WriteLine(string.Format("# file_created_NY={0}",
                            TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# file_created_UTC={0}",
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# bar_type={0}", BarsPeriod.BarsPeriodType));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# brick_size_value2={0}  (UniRenko/BetterRenko only)", BarsPeriod.Value2));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# PatternToMatch={0}", PatternToMatch));
                        sw.WriteLine(string.Format("# FollowUpPattern={0}", FollowUpPattern));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
                        sw.WriteLine("# encoding: SUCCESS=1, FAILURE=0 (matches brick green=1, red=0)");
                        sw.WriteLine("# all timestamps in this file are NEW YORK time (America/New_York, DST-aware)");
                        sw.WriteLine("#");
                        sw.WriteLine("# ---- COLUMN LEGEND ----");
                        sw.WriteLine("# OccurrenceTime_NY                    : NY time when the pattern's last brick closed (pattern detected)");
                        sw.WriteLine("# OutcomeTime_NY                       : NY time when the follow-up bricks fully resolved as S or F");
                        sw.WriteLine("# BrickIndexAtPatternEnd               : zero-based index into today's brick list at the moment of detection");
                        sw.WriteLine("# Pattern                              : the PatternToMatch string in effect for this row");
                        sw.WriteLine("# FollowUp                             : the FollowUpPattern string in effect for this row");
                        sw.WriteLine("# ActualFollowUp                       : the bits that actually followed (for forensic comparison)");
                        sw.WriteLine("# Outcome                              : S = follow-up matched exactly, F = at least one bit mismatched");
                        sw.WriteLine("# PatternEndOpen                       : Open of the pattern's last brick (used for EMA qualification in v1.3.2)");
                        sw.WriteLine("# PatternEndClose                      : Close of the pattern's last brick");
                        sw.WriteLine("# OutcomePrice                         : Close of the brick that completed the follow-up sequence");
                        sw.WriteLine("# AlertPattern                         : the AlertPattern string in effect for this row");
                        sw.WriteLine("# DailyOccurrenceNumber                : 1-based count of ALL pattern occurrences since the 9:30 NY reset");
                        sw.WriteLine("# OutcomeString_NoEmaFilter            : running string of every occurrence's outcome bit (S=1, F=0), today");
                        sw.WriteLine("# Ema1AtDetect / Ema2AtDetect          : EMA values at the moment of detection");
                        sw.WriteLine("# EmaQualified                         : YES if PatternEndOpen was above BOTH EMAs (the whole green body sits above)");
                        sw.WriteLine("# DailyEmaQualifiedOccurrenceNumber    : 1-based count of EMA-qualified occurrences today (0 if not qualified)");
                        sw.WriteLine("# OutcomeString_EmaQualified           : running string of EMA-qualified outcome bits today");
                        sw.WriteLine("# FiredAlertOnThisRow                  : YES if this outcome's bit appended to OutcomeString_EmaQualified made it end with AlertPattern");
                        sw.WriteLine("# CapturedPostAlert                    : YES if this row's bit was captured as a 'next outcome after a prior alert'");
                        sw.WriteLine("# CapturedBit                          : the bit captured (1=S, 0=F), or blank if no capture");
                        sw.WriteLine("# PostAlertOutcomeString               : growing string of post-alert capture bits; count '1's / length = chase hit rate");
                        sw.WriteLine("# PendingPostAlertCapturesAfter        : how many alerts are still waiting for their next qualified outcome");
                        sw.WriteLine("#");

                        // ---- Actual CSV header row (renamed timestamps with _NY suffix) ----
                        sw.WriteLine("OccurrenceTime_NY,OutcomeTime_NY,BrickIndexAtPatternEnd,Pattern,FollowUp,"
                            + "ActualFollowUp,Outcome,PatternEndOpen,PatternEndClose,OutcomePrice,"
                            + "AlertPattern,DailyOccurrenceNumber,OutcomeString_NoEmaFilter,"
                            + "Ema1AtDetect,Ema2AtDetect,EmaQualified,"
                            + "DailyEmaQualifiedOccurrenceNumber,OutcomeString_EmaQualified,"
                            + "FiredAlertOnThisRow,"
                            + "CapturedPostAlert,CapturedBit,PostAlertOutcomeString,PendingPostAlertCapturesAfter");
                    }

                    string actualFollow = "";
                    int startIdx = po.PatternEndIndex + 1;
                    for (int i = 0; i < compiledFollowUp.Length && (startIdx + i) < bricks.Count; i++)
                        actualFollow += bricks[startIdx + i].ToString();

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13:F2},{14:F2},{15},{16},{17},{18},{19},{20},{21},{22}",
                        TimeZoneInfo.ConvertTime(po.OccurrenceTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        po.PatternEndIndex,
                        PatternToMatch,
                        FollowUpPattern,
                        actualFollow,
                        outcome,
                        po.OpenAtDetect,                          // [v1.3.2] PatternEndOpen
                        po.PatternEndPrice,                       // PatternEndClose
                        endPrice,
                        compiledAlertPattern,
                        po.OccurrenceNumberAtDetect,
                        outcomeString.ToString(),                 // OutcomeString_NoEmaFilter
                        po.Ema1AtDetect,
                        po.Ema2AtDetect,
                        po.EmaQualified ? "YES" : "NO",
                        po.TradedOccurrenceNumberAtDetect,        // DailyEmaQualifiedOccurrenceNumber
                        tradedOutcomeString.ToString(),           // OutcomeString_EmaQualified
                        firedAlert ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",         // [v1.3.2]
                        capturedPostAlert ? capturedBit.ToString() : "",  // [v1.3.2]
                        postAlertOutcomeString.ToString(),        // [v1.3.2]
                        pendingPostAlertCaptures));               // [v1.3.2]
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
        private void WriteAlertRow()
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                // [v1.3.2] New filename
                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_v132_alert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                     
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.3.2");
                        sw.WriteLine(string.Format("# file_created_NY={0}",
                            TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# file_created_UTC={0}",
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# bar_type={0}", BarsPeriod.BarsPeriodType));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# brick_size_value2={0}", BarsPeriod.Value2));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# PatternToMatch={0}", PatternToMatch));
                        sw.WriteLine(string.Format("# FollowUpPattern={0}", FollowUpPattern));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
                        sw.WriteLine("# all timestamps in this file are NEW YORK time (America/New_York, DST-aware)");
                        sw.WriteLine("#");
                        sw.WriteLine("# ---- COLUMN LEGEND ----");
                        sw.WriteLine("# AlertTime_NY                  : NY time when this alert fired (at the close of the brick that produced the AlertPattern tail)");
                        sw.WriteLine("# DailyAlertNumber              : 1-based alert count since the 9:30 NY reset");
                        sw.WriteLine("# AlertPattern                  : the suffix that just matched");
                        sw.WriteLine("# Pattern / FollowUp            : Layer-1 pattern config in effect");
                        sw.WriteLine("# CurrentPrice                  : Close of the brick at the moment the alert fired");
                        sw.WriteLine("# OutcomeString_NoEmaFilter     : all outcomes today (S=1, F=0)");
                        sw.WriteLine("# OutcomeString_EmaQualified    : EMA-qualified outcomes today (S=1, F=0)");
                        sw.WriteLine("# PostAlertOutcomeString        : captured chase-trade outcome bits");
                        sw.WriteLine("# PendingPostAlertCapturesAfter : alerts still owed a future capture (this alert just incremented it)");
                        sw.WriteLine("#");

                        sw.WriteLine("AlertTime_NY,DailyAlertNumber,AlertPattern,"
                            + "Pattern,FollowUp,CurrentPrice,"
                            + "OutcomeString_NoEmaFilter,OutcomeString_EmaQualified,"
                            + "PostAlertOutcomeString,PendingPostAlertCapturesAfter");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5:F2},{6},{7},{8},{9}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        compiledAlertPattern,
                        PatternToMatch,
                        FollowUpPattern,
                        Close[0],
                        outcomeString.ToString(),
                        tradedOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ALERT] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // ResetState (daily reset at 9:30 NY)
        // =====================================================================
        private void ResetState()
        {
            bricks.Clear();
            pendingOccurrences.Clear();
            dailyOccurrenceNumber = 0;
            dailyTradedOccurrenceNumber = 0;
            dailyAlertNumber = 0;
            beepCount = 0;
            outcomeString.Clear();
            tradedOutcomeString.Clear();
            postAlertOutcomeString.Clear();          // [v1.3.2]
            pendingPostAlertCaptures = 0;            // [v1.3.2]
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name="PatternToMatch",
            Description="Bit pattern to detect, 1-10 chars of '0' (red) or '1' (green). Default \"011\".",
            Order=1, GroupName="1. Layer 1 - Pattern")]
        public string PatternToMatch { get; set; }

        [NinjaScriptProperty]
        [Display(Name="FollowUpPattern",
            Description="Required follow-up bricks for SUCCESS, 1-10 chars of '0' or '1'. Must match EXACTLY. Default \"11\".",
            Order=2, GroupName="1. Layer 1 - Pattern")]
        public string FollowUpPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AlertPattern",
            Description="[v1.3] Suffix pattern matched against TradedOutcomeString (S=1, F=0). Alert fires when tail equals this. 1-10 chars. e.g. \"011\" = chase next after F-S-S, \"100\" = caution after S-F-F.",
            Order=3, GroupName="2. Alert")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA1Period",
            Description="Period for EMA1. A trade is qualified only when pattern's last-brick close > EMA1 AND > EMA2. Default 20.",
            Order=4, GroupName="3. EMA Filter")]
        public int EMA1Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA2Period",
            Description="Period for EMA2. Default 9.",
            Order=5, GroupName="3. EMA Filter")]
        public int EMA2Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert. Default 3.",
            Order=6, GroupName="4. Beep")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=7, GroupName="4. Beep")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Master toggle for all chart drawings (PT/S, PT/F, alert diamond).",
            Order=8, GroupName="5. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowOutcomeLabels",
            Description="Show 'PT/S' (green) or 'PT/F' (red) text at each outcome brick. '*' suffix means EMA-qualified.",
            Order=9, GroupName="5. Visuals")]
        public bool ShowOutcomeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for CSV files. Auto-created. Default C:\\temp.",
            Order=10, GroupName="6. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name="MaxBitsKept",
            Description="Max in-memory bit string length before trimming. Default 5000.",
            Order=11, GroupName="7. Advanced")]
        public int MaxBitsKept { get; set; }

        #endregion
    }
}
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
// STRATEGY: scalper_RenkoStringPatternAlertEMA v1.3.2
// AUTHOR:   Albert Feng / Drafted with help from Claude
// REPLACES: v1.3.1
// =============================================================================
//
// v1.3.2 CHANGES vs v1.3.1
// ------------------------
//
// CHANGE 1 - EMA qualification uses OPEN, not CLOSE
//   Visual intent: for a GREEN brick, the body bottom is Open and the top
//   is Close. v1.3.1 checked Close > EMA, which lets the brick straddle the
//   EMA (Open below, Close above) and still qualify. v1.3.2 checks Open[0],
//   which means the entire green brick body must sit above both EMAs.
//
//   Logic: emaQualified = (Open[0] > ema1) && (Open[0] > ema2)
//   Logged column "Open" is the brick open used; PatternEndPrice continues
//   to log Close (unchanged).
//
//   NOTE: for a RED pattern-end brick (pattern ending in '0'), Open is the
//   TOP of the body and Close is the BOTTOM. Open > EMA is a weaker filter
//   than Close > EMA for red bricks. For green-ending patterns like "011"
//   this matches your visual reading.
//
// CHANGE 2 - PostAlertOutcomeString
//   New column in occurrences CSV that captures the bit (S=1, F=0) of the
//   FIRST EMA-qualified outcome that resolves AFTER each alert fires.
//
//   Mechanics:
//     - Counter `pendingPostAlertCaptures` starts at 0.
//     - Every time an alert fires (in FireAlert), the counter increments.
//     - When the next EMA-qualified outcome resolves AND the counter > 0,
//       its bit is appended to postAlertOutcomeString and the counter
//       decrements.
//     - Non-qualified outcomes are skipped (don't decrement the counter,
//       don't get appended).
//     - The alert-triggering outcome itself is NOT captured for its own
//       alert - capture starts on the NEXT qualified outcome.
//     - If alerts overlap (alert 2 fires before alert 1's capture lands),
//       the counter accumulates and each subsequent qualified outcome
//       gets captured in order.
//
//   Use case: with AlertPattern = "011" (chase next after F-S-S), the
//   PostAlertOutcomeString is the success/failure of every chase trade.
//   Counting '1's in PostAlertOutcomeString / length = chase-trade hit rate.
//
// =============================================================================
//
// v1.3 CHANGES vs v1.2.1
// ----------------------
//
// CHANGE 1 - Bit encoding flipped to match brick convention
//   Old (v1.0 - v1.2.1):  S = 0, F = 1
//   New (v1.3):           S = 1, F = 0
//   This now matches the brick-bit convention (green=1, red=0) and the
//   natural reading: "success counts as a 1, failure counts as a 0."
//   Applied identically to both OutcomeString and TradedOutcomeString.
//
//   Example: occurrences resolving as S F F S F give:
//     v1.2.1:  "01101"   (F=1, S=0)
//     v1.3:    "10010"   (S=1, F=0)
//
// CHANGE 2 - AlertMode / Threshold REMOVED
//   The OnDetection / AfterFailures / AfterSuccesses dropdown and Threshold
//   integer are gone. Replaced by a single AlertPattern string.
//
// CHANGE 3 - AlertPattern (suffix match) ADDED
//   New parameter: AlertPattern (1-10 chars of '0' or '1', default "011").
//   Alert fires when the TAIL of TradedOutcomeString equals AlertPattern.
//
//   Match is against TradedOutcomeString (EMA-qualified only), not
//   OutcomeString. So "011" fires after F, S, S in the last three
//   EMA-qualified occurrences (you'd be chasing the next success).
//
//   Matching is sliding-window: every time a new char is appended to
//   TradedOutcomeString, we check if the last AlertPattern.Length chars
//   equal AlertPattern. If yes, fire alert. Can fire repeatedly.
//
//   Example, AlertPattern = "100":
//     Stream of EMA-qualified outcomes: S F F F S F F
//     TradedOutcomeString grows:        1 10 100 1000 10001 100010 1000100
//     Alerts fire after positions:      ----- ^^^ -------- ------ ^^^^^^^
//                                            (100)            (100 in tail)
//
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_RenkoStringPatternAlertEMA : Strategy
    {
        #region Variables

        // ---- Bit string of bricks since today's reset ----
        private List<int> bricks = new List<int>();

        // ---- Outcome strings ----
        // OutcomeString: ALL resolved occurrences (S=1, F=0)
        private StringBuilder outcomeString = new StringBuilder();
        // TradedOutcomeString: only EMA-qualified occurrences (S=1, F=0)
        private StringBuilder tradedOutcomeString = new StringBuilder();
        // [v1.3.2] PostAlertOutcomeString: bit of the NEXT EMA-qualified outcome
        // after each alert. Grows by one bit per captured outcome. Non-qualified
        // outcomes are skipped (don't decrement the counter).
        private StringBuilder postAlertOutcomeString = new StringBuilder();
        // [v1.3.2] How many EMA-qualified outcomes are still owed to past alerts.
        // Incremented when an alert fires, decremented when a qualified outcome
        // is captured. Allows alert overlap.
        private int pendingPostAlertCaptures = 0;

        // ---- Daily numbering for CSV correlation ----
        private int dailyOccurrenceNumber = 0;
        private int dailyTradedOccurrenceNumber = 0;
        private int dailyAlertNumber = 0;

        // ---- Beep cadence ----
        private DateTime lastBeepWallClock;
        private int beepCount = 0;

        // ---- Daily reset state ----
        private DateTime currentTradingDateNy = DateTime.MinValue;
        private TimeZoneInfo nyTz;
        private const int RESET_HOUR_NY   = 9;
        private const int RESET_MINUTE_NY = 30;

        // ---- Compiled patterns ----
        private int[] compiledPattern  = null;
        private int[] compiledFollowUp = null;
        private string compiledAlertPattern = null;   // [v1.3] kept as string for direct tail compare
        private bool configValid = false;

        // ---- EMA indicator references ----
        private EMA ema1;
        private EMA ema2;

        // ---- Pending occurrences awaiting follow-up resolution ----
        private class PendingOccurrence
        {
            public int       PatternStartIndex;
            public int       PatternEndIndex;
            public int       BricksWatched;
            public bool      FollowUpMismatch;
            public DateTime  OccurrenceTime;
            public double    PatternEndPrice;

            public bool      EmaQualified;
            public double    Ema1AtDetect;
            public double    Ema2AtDetect;
            public double    OpenAtDetect;   // [v1.3.2] open of pattern's closing brick

            public int       OccurrenceNumberAtDetect;
            public int       TradedOccurrenceNumberAtDetect;
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
                Description = "Renko string-pattern alerter (v1.3.2). EMA qualification uses Open (whole green brick body above EMAs). New PostAlertOutcomeString column captures chase-trade outcomes. Places NO orders.";
                Name        = "scalper_RenkoStringPatternAlertEMA";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection                       = 1;
                EntryHandling                             = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy              = false;
                BarsRequiredToTrade                       = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- Layer 1 string parameters ----
                PatternToMatch   = "011";
                FollowUpPattern  = "1";

                // ---- [v1.3] AlertPattern (replaces AlertMode + Threshold) ----
                AlertPattern     = "01";

                // ---- EMA filter ----
                EMA1Period       = 20;
                EMA2Period       = 9;

                // ---- Beep ----
                AlertSoundCount   = 3;
                AlertReminderSecs = 1;

                // ---- Visuals ----
                EnableChartMarkers = true;
                ShowOutcomeLabels  = true;

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

                ema1 = EMA(EMA1Period);
                ema2 = EMA(EMA2Period);

                ResetState();
            }
            else if (State == State.Realtime)
            {
                configValid = TryCompilePatterns();

                Print("================================================================");
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlertEMA v1.3.2 at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));
				// ---- Bar type / brick size info ----
                Print(string.Format("[INIT] BarsPeriod.BarsPeriodType = {0}",
                    BarsPeriod.BarsPeriodType));
                Print(string.Format("[INIT] BarsPeriod.Value  = {0}  (for standard Renko: brick size in ticks)",
                    BarsPeriod.Value));
                Print(string.Format("[INIT] BarsPeriod.Value2 = {0}  (used by UniRenko/BetterRenko for reversal offset; ignore for standard Renko)",
                    BarsPeriod.Value2));
                Print(string.Format("[INIT] Instrument TickSize = {0}  -> brick size in price = {1}",
                    TickSize, BarsPeriod.Value * TickSize));
                Print(string.Format("[INIT] Run start (local): {0}   (UTC: {1})   (NY: {2})",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));

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
                Print(string.Format("[INIT] AlertPattern    = \"{0}\" (length {1})",
                    compiledAlertPattern, compiledAlertPattern.Length));
                Print("[INIT] Bit encoding: SUCCESS=1, FAILURE=0 (matches brick green=1/red=0).");
                Print("[INIT] AlertPattern is suffix-matched against OutcomeString_EmaQualified (EMA-qualified outcomes only).");
                Print("[INIT] Alert fires every time the tail of OutcomeString_EmaQualified equals AlertPattern.");
                Print(string.Format("[INIT] EMA1 period = {0}, EMA2 period = {1}",
                    EMA1Period, EMA2Period));
                Print("[INIT] EMA filter: trade only when Open[0] > EMA1 AND Open[0] > EMA2 at pattern's last brick (i.e. green-brick body must sit entirely above both EMAs).");
                Print("[INIT] PostAlertOutcomeString: after each alert, captures the bit of the NEXT EMA-qualified outcome (overlap-safe).");
                Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));
                Print(string.Format("[INIT] Beep: {0} beeps, {1}s apart", AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));
                Print("[INIT] This strategy places NO orders.");
            }
        }

        // =====================================================================
        // TryCompilePatterns
        // =====================================================================
        private bool TryCompilePatterns()
        {
            compiledPattern  = TryCompileOne(PatternToMatch,  "PatternToMatch");
            compiledFollowUp = TryCompileOne(FollowUpPattern, "FollowUpPattern");

            // [v1.3] Validate AlertPattern; keep as string for direct tail compare.
            int[] alertBits = TryCompileOne(AlertPattern, "AlertPattern");
            compiledAlertPattern = (alertBits != null) ? AlertPattern : null;

            return compiledPattern != null
                && compiledFollowUp != null
                && compiledAlertPattern != null;
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
            if (!configValid) return;

            // ---- Daily reset check ----
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
                    Print(string.Format("[RESET] New trading day at 9:30 NY ({0:yyyy-MM-dd}). Bricks today before reset: {1}, occurrences: {2}, traded: {3}, alerts: {4}, post-alert captures: {5}, pending captures dropped: {6}",
                        effectiveTradingDate, bricks.Count, dailyOccurrenceNumber, dailyTradedOccurrenceNumber, dailyAlertNumber,
                        postAlertOutcomeString.Length, pendingPostAlertCaptures));
                }
                else
                {
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}",
                        effectiveTradingDate));
                }
                currentTradingDateNy = effectiveTradingDate;
                ResetState();
            }

            // ---- Append this brick's bit ----
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

            // ---- Resolve pending occurrences ----
            List<PendingOccurrence> resolved = new List<PendingOccurrence>();

            foreach (var po in pendingOccurrences)
            {
                if (currentBrickIdx <= po.PatternEndIndex) continue;

                int expectedBit = compiledFollowUp[po.BricksWatched];
                if (thisBit != expectedBit)
                    po.FollowUpMismatch = true;

                po.BricksWatched++;

                if (po.BricksWatched >= compiledFollowUp.Length)
                {
                    string outcome = po.FollowUpMismatch ? "F" : "S";
                    HandleOccurrenceOutcome(po, outcome, currentPrice);
                    resolved.Add(po);
                }
            }
            foreach (var po in resolved)
                pendingOccurrences.Remove(po);

            // ---- Detect new occurrence ending at this brick ----
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
                    // [v1.3.2] EMA filter uses OPEN of the pattern's closing brick.
                    // For a green brick, Open = body bottom, so the entire body must
                    // be above both EMAs to qualify.
                    double ema1Val = ema1[0];
                    double ema2Val = ema2[0];
                    double openPrice = Open[0];
                    bool emaQualified = (openPrice > ema1Val) && (openPrice > ema2Val);

                    var po = new PendingOccurrence
                    {
                        PatternStartIndex = patternStartIdx,
                        PatternEndIndex   = patternStartIdx + patternLen - 1,
                        BricksWatched     = 0,
                        FollowUpMismatch  = false,
                        OccurrenceTime    = Time[0],
                        PatternEndPrice   = currentPrice,
                        EmaQualified      = emaQualified,
                        Ema1AtDetect      = ema1Val,
                        Ema2AtDetect      = ema2Val,
                        OpenAtDetect      = openPrice   // [v1.3.2]
                    };
                    pendingOccurrences.Add(po);

                    dailyOccurrenceNumber++;
                    if (emaQualified) dailyTradedOccurrenceNumber++;

                    po.OccurrenceNumberAtDetect = dailyOccurrenceNumber;
                    po.TradedOccurrenceNumberAtDetect = emaQualified ? dailyTradedOccurrenceNumber : 0;

                    Print(string.Format("[L1] Pattern \"{0}\" detected at brick {1} ({2}). Occurrence #{3} pending {4} follow-up bricks. Open={5:F2}, Close={6:F2}, EMA1={7:F2}, EMA2={8:F2}, EmaQualified={9}",
                        PatternToMatch,
                        currentBrickIdx,
                        Time[0].ToString("HH:mm:ss"),
                        dailyOccurrenceNumber,
                        compiledFollowUp.Length,
                        openPrice,
                        currentPrice,
                        ema1Val,
                        ema2Val,
                        emaQualified ? "YES" : "NO"));
                }
            }
        }

        // =====================================================================
        // HandleOccurrenceOutcome
        // [v1.3] S=1, F=0 encoding. AlertPattern suffix match on TradedOutcomeString.
        // =====================================================================
        private void HandleOccurrenceOutcome(PendingOccurrence po, string outcome, double currentPrice)
        {
            // [v1.3] Update OutcomeString: S=1, F=0
            outcomeString.Append(outcome == "S" ? '1' : '0');

            // [v1.3] Update TradedOutcomeString (only EMA-qualified): S=1, F=0
            bool firedAlert = false;
            bool capturedPostAlert = false;
            char capturedBit = '\0';
            if (po.EmaQualified)
            {
                tradedOutcomeString.Append(outcome == "S" ? '1' : '0');

                Print(string.Format("[L1] *** {0} *** at {1} (EMA-qualified). OutcomeString_EmaQualified = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
                    tradedOutcomeString.ToString()));

                // [v1.3.2] Post-alert capture: if any prior alert is still owed a
                // capture, this qualified outcome consumes one slot. Do this BEFORE
                // checking for a NEW alert on this same outcome - otherwise an alert
                // firing on this outcome would capture itself.
                if (pendingPostAlertCaptures > 0)
                {
                    capturedBit = outcome == "S" ? '1' : '0';
                    postAlertOutcomeString.Append(capturedBit);
                    pendingPostAlertCaptures--;
                    capturedPostAlert = true;
                    Print(string.Format("[POSTALERT] Captured outcome bit '{0}' for prior alert. PostAlertOutcomeString = \"{1}\". Pending captures remaining = {2}.",
                        capturedBit, postAlertOutcomeString.ToString(), pendingPostAlertCaptures));
                }

                // [v1.3] Suffix match against AlertPattern (may add a new pending capture)
                if (TradedTailMatchesAlertPattern())
                {
                    FireAlert();
                    firedAlert = true;
                }
            }
            else
            {
                Print(string.Format("[L1] *** {0} *** at {1} (NOT EMA-qualified, not added to OutcomeString_EmaQualified). OutcomeString_NoEmaFilter = \"{2}\"",
                    outcome == "S" ? "SUCCESS" : "FAILURE",
                    Time[0].ToString("HH:mm:ss"),
                    outcomeString.ToString()));
            }

            WriteOccurrenceRow(po, outcome, currentPrice, firedAlert, capturedPostAlert, capturedBit);

            // PT/S or PT/F text label at the outcome brick
            if (EnableChartMarkers && ShowOutcomeLabels)
            {
                string tag = "RSP_OUT_" + CurrentBar + "_" + po.OccurrenceNumberAtDetect;
                string prefix = po.EmaQualified ? "###" : "";
				string label = prefix + (outcome == "S" ? "PT/S" : "PT/F");
                Brush color = outcome == "S" ? Brushes.LimeGreen : Brushes.Red;

                double yOffset = outcome == "S" ? (3 * TickSize) : -(3 * TickSize);
                double yPos = outcome == "S" ? (High[0] + yOffset) : (Low[0] + yOffset);

                Draw.Text(this, tag, label, 0, yPos, color);
            }
        }

        // =====================================================================
        // [v1.3] TradedTailMatchesAlertPattern
        // Returns true iff the LAST AlertPattern.Length chars of tradedOutcomeString
        // are exactly equal to AlertPattern.
        // =====================================================================
        private bool TradedTailMatchesAlertPattern()
        {
            int patLen = compiledAlertPattern.Length;
            if (tradedOutcomeString.Length < patLen) return false;

            int start = tradedOutcomeString.Length - patLen;
            for (int i = 0; i < patLen; i++)
            {
                if (tradedOutcomeString[start + i] != compiledAlertPattern[i])
                    return false;
            }
            return true;
        }

        // =====================================================================
        // [v1.3] FireAlert - unified single alert function
        // =====================================================================
        private void FireAlert()
        {
            dailyAlertNumber++;

            // [v1.3.2] Owe one post-alert capture to this alert
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern \"{0}\" matched the tail of OutcomeString_EmaQualified at {1}. Daily alert #{2}. PendingPostAlertCaptures = {3}. ***",
                compiledAlertPattern, Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] OutcomeString_EmaQualified tail: \"...{0}\"",
                tradedOutcomeString.Length >= compiledAlertPattern.Length
                    ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - compiledAlertPattern.Length)
                    : tradedOutcomeString.ToString()));
            Print("[ALERT] Watch the chart. Next EMA-qualified pattern occurrence is your trial.");

            PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
            lastBeepWallClock = DateTime.Now;
            beepCount = 1;

            if (EnableChartMarkers)
            {
                string tag = "RSP_ALERT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Diamond(this, tag, true, 0, High[0] + (6 * TickSize), Brushes.Magenta);
                string txt = "RSP_ALERT_TXT_" + CurrentBar + "_" + dailyAlertNumber;
                Draw.Text(this, txt, string.Format("ALERT #{0}\nAP=\"{1}\"", dailyAlertNumber, compiledAlertPattern),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow();
        }

        // =====================================================================
        // CSV: per-occurrence rows
        // =====================================================================
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice, bool firedAlert, bool capturedPostAlert, char capturedBit)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                // [v1.3.2] New filename to prevent collision with v1.3.1 files
                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_v132_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                     
                    if (!fileExists)
                    {
                        // ---- Schema + session header ----
                        sw.WriteLine("# schema_version=1.3.2");
                        sw.WriteLine(string.Format("# file_created_NY={0}",
                            TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# file_created_UTC={0}",
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# bar_type={0}", BarsPeriod.BarsPeriodType));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# brick_size_value2={0}  (UniRenko/BetterRenko only)", BarsPeriod.Value2));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# PatternToMatch={0}", PatternToMatch));
                        sw.WriteLine(string.Format("# FollowUpPattern={0}", FollowUpPattern));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
                        sw.WriteLine("# encoding: SUCCESS=1, FAILURE=0 (matches brick green=1, red=0)");
                        sw.WriteLine("# all timestamps in this file are NEW YORK time (America/New_York, DST-aware)");
                        sw.WriteLine("#");
                        sw.WriteLine("# ---- COLUMN LEGEND ----");
                        sw.WriteLine("# OccurrenceTime_NY                    : NY time when the pattern's last brick closed (pattern detected)");
                        sw.WriteLine("# OutcomeTime_NY                       : NY time when the follow-up bricks fully resolved as S or F");
                        sw.WriteLine("# BrickIndexAtPatternEnd               : zero-based index into today's brick list at the moment of detection");
                        sw.WriteLine("# Pattern                              : the PatternToMatch string in effect for this row");
                        sw.WriteLine("# FollowUp                             : the FollowUpPattern string in effect for this row");
                        sw.WriteLine("# ActualFollowUp                       : the bits that actually followed (for forensic comparison)");
                        sw.WriteLine("# Outcome                              : S = follow-up matched exactly, F = at least one bit mismatched");
                        sw.WriteLine("# PatternEndOpen                       : Open of the pattern's last brick (used for EMA qualification in v1.3.2)");
                        sw.WriteLine("# PatternEndClose                      : Close of the pattern's last brick");
                        sw.WriteLine("# OutcomePrice                         : Close of the brick that completed the follow-up sequence");
                        sw.WriteLine("# AlertPattern                         : the AlertPattern string in effect for this row");
                        sw.WriteLine("# DailyOccurrenceNumber                : 1-based count of ALL pattern occurrences since the 9:30 NY reset");
                        sw.WriteLine("# OutcomeString_NoEmaFilter            : running string of every occurrence's outcome bit (S=1, F=0), today");
                        sw.WriteLine("# Ema1AtDetect / Ema2AtDetect          : EMA values at the moment of detection");
                        sw.WriteLine("# EmaQualified                         : YES if PatternEndOpen was above BOTH EMAs (the whole green body sits above)");
                        sw.WriteLine("# DailyEmaQualifiedOccurrenceNumber    : 1-based count of EMA-qualified occurrences today (0 if not qualified)");
                        sw.WriteLine("# OutcomeString_EmaQualified           : running string of EMA-qualified outcome bits today");
                        sw.WriteLine("# FiredAlertOnThisRow                  : YES if this outcome's bit appended to OutcomeString_EmaQualified made it end with AlertPattern");
                        sw.WriteLine("# CapturedPostAlert                    : YES if this row's bit was captured as a 'next outcome after a prior alert'");
                        sw.WriteLine("# CapturedBit                          : the bit captured (1=S, 0=F), or blank if no capture");
                        sw.WriteLine("# PostAlertOutcomeString               : growing string of post-alert capture bits; count '1's / length = chase hit rate");
                        sw.WriteLine("# PendingPostAlertCapturesAfter        : how many alerts are still waiting for their next qualified outcome");
                        sw.WriteLine("#");

                        // ---- Actual CSV header row (renamed timestamps with _NY suffix) ----
                        sw.WriteLine("OccurrenceTime_NY,OutcomeTime_NY,BrickIndexAtPatternEnd,Pattern,FollowUp,"
                            + "ActualFollowUp,Outcome,PatternEndOpen,PatternEndClose,OutcomePrice,"
                            + "AlertPattern,DailyOccurrenceNumber,OutcomeString_NoEmaFilter,"
                            + "Ema1AtDetect,Ema2AtDetect,EmaQualified,"
                            + "DailyEmaQualifiedOccurrenceNumber,OutcomeString_EmaQualified,"
                            + "FiredAlertOnThisRow,"
                            + "CapturedPostAlert,CapturedBit,PostAlertOutcomeString,PendingPostAlertCapturesAfter");
                    }

                    string actualFollow = "";
                    int startIdx = po.PatternEndIndex + 1;
                    for (int i = 0; i < compiledFollowUp.Length && (startIdx + i) < bricks.Count; i++)
                        actualFollow += bricks[startIdx + i].ToString();

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13:F2},{14:F2},{15},{16},{17},{18},{19},{20},{21},{22}",
                        TimeZoneInfo.ConvertTime(po.OccurrenceTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        po.PatternEndIndex,
                        PatternToMatch,
                        FollowUpPattern,
                        actualFollow,
                        outcome,
                        po.OpenAtDetect,                          // [v1.3.2] PatternEndOpen
                        po.PatternEndPrice,                       // PatternEndClose
                        endPrice,
                        compiledAlertPattern,
                        po.OccurrenceNumberAtDetect,
                        outcomeString.ToString(),                 // OutcomeString_NoEmaFilter
                        po.Ema1AtDetect,
                        po.Ema2AtDetect,
                        po.EmaQualified ? "YES" : "NO",
                        po.TradedOccurrenceNumberAtDetect,        // DailyEmaQualifiedOccurrenceNumber
                        tradedOutcomeString.ToString(),           // OutcomeString_EmaQualified
                        firedAlert ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",         // [v1.3.2]
                        capturedPostAlert ? capturedBit.ToString() : "",  // [v1.3.2]
                        postAlertOutcomeString.ToString(),        // [v1.3.2]
                        pendingPostAlertCaptures));               // [v1.3.2]
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
        private void WriteAlertRow()
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                // [v1.3.2] New filename
                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_v132_alert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                     
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.3.2");
                        sw.WriteLine(string.Format("# file_created_NY={0}",
                            TimeZoneInfo.ConvertTime(DateTime.Now, nyTz).ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# file_created_UTC={0}",
                            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                        sw.WriteLine(string.Format("# instrument={0}", Instrument.FullName));
                        sw.WriteLine(string.Format("# bar_type={0}", BarsPeriod.BarsPeriodType));
                        sw.WriteLine(string.Format("# brick_size_ticks={0}", BarsPeriod.Value));
                        sw.WriteLine(string.Format("# brick_size_value2={0}", BarsPeriod.Value2));
                        sw.WriteLine(string.Format("# tick_size={0}", TickSize));
                        sw.WriteLine(string.Format("# brick_size_price={0}", BarsPeriod.Value * TickSize));
                        sw.WriteLine(string.Format("# PatternToMatch={0}", PatternToMatch));
                        sw.WriteLine(string.Format("# FollowUpPattern={0}", FollowUpPattern));
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
                        sw.WriteLine("# all timestamps in this file are NEW YORK time (America/New_York, DST-aware)");
                        sw.WriteLine("#");
                        sw.WriteLine("# ---- COLUMN LEGEND ----");
                        sw.WriteLine("# AlertTime_NY                  : NY time when this alert fired (at the close of the brick that produced the AlertPattern tail)");
                        sw.WriteLine("# DailyAlertNumber              : 1-based alert count since the 9:30 NY reset");
                        sw.WriteLine("# AlertPattern                  : the suffix that just matched");
                        sw.WriteLine("# Pattern / FollowUp            : Layer-1 pattern config in effect");
                        sw.WriteLine("# CurrentPrice                  : Close of the brick at the moment the alert fired");
                        sw.WriteLine("# OutcomeString_NoEmaFilter     : all outcomes today (S=1, F=0)");
                        sw.WriteLine("# OutcomeString_EmaQualified    : EMA-qualified outcomes today (S=1, F=0)");
                        sw.WriteLine("# PostAlertOutcomeString        : captured chase-trade outcome bits");
                        sw.WriteLine("# PendingPostAlertCapturesAfter : alerts still owed a future capture (this alert just incremented it)");
                        sw.WriteLine("#");

                        sw.WriteLine("AlertTime_NY,DailyAlertNumber,AlertPattern,"
                            + "Pattern,FollowUp,CurrentPrice,"
                            + "OutcomeString_NoEmaFilter,OutcomeString_EmaQualified,"
                            + "PostAlertOutcomeString,PendingPostAlertCapturesAfter");
                    }
                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5:F2},{6},{7},{8},{9}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        compiledAlertPattern,
                        PatternToMatch,
                        FollowUpPattern,
                        Close[0],
                        outcomeString.ToString(),
                        tradedOutcomeString.ToString(),
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-ALERT] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // ResetState (daily reset at 9:30 NY)
        // =====================================================================
        private void ResetState()
        {
            bricks.Clear();
            pendingOccurrences.Clear();
            dailyOccurrenceNumber = 0;
            dailyTradedOccurrenceNumber = 0;
            dailyAlertNumber = 0;
            beepCount = 0;
            outcomeString.Clear();
            tradedOutcomeString.Clear();
            postAlertOutcomeString.Clear();          // [v1.3.2]
            pendingPostAlertCaptures = 0;            // [v1.3.2]
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name="PatternToMatch",
            Description="Bit pattern to detect, 1-10 chars of '0' (red) or '1' (green). Default \"011\".",
            Order=1, GroupName="1. Layer 1 - Pattern")]
        public string PatternToMatch { get; set; }

        [NinjaScriptProperty]
        [Display(Name="FollowUpPattern",
            Description="Required follow-up bricks for SUCCESS, 1-10 chars of '0' or '1'. Must match EXACTLY. Default \"11\".",
            Order=2, GroupName="1. Layer 1 - Pattern")]
        public string FollowUpPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AlertPattern",
            Description="[v1.3] Suffix pattern matched against TradedOutcomeString (S=1, F=0). Alert fires when tail equals this. 1-10 chars. e.g. \"011\" = chase next after F-S-S, \"100\" = caution after S-F-F.",
            Order=3, GroupName="2. Alert")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA1Period",
            Description="Period for EMA1. A trade is qualified only when pattern's last-brick close > EMA1 AND > EMA2. Default 20.",
            Order=4, GroupName="3. EMA Filter")]
        public int EMA1Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA2Period",
            Description="Period for EMA2. Default 9.",
            Order=5, GroupName="3. EMA Filter")]
        public int EMA2Period { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="AlertSoundCount",
            Description="Total beeps per alert. Default 3.",
            Order=6, GroupName="4. Beep")]
        public int AlertSoundCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="AlertReminderSecs",
            Description="Wall-clock seconds between beeps. Default 1.",
            Order=7, GroupName="4. Beep")]
        public int AlertReminderSecs { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableChartMarkers",
            Description="Master toggle for all chart drawings (PT/S, PT/F, alert diamond).",
            Order=8, GroupName="5. Visuals")]
        public bool EnableChartMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name="ShowOutcomeLabels",
            Description="Show 'PT/S' (green) or 'PT/F' (red) text at each outcome brick. '*' suffix means EMA-qualified.",
            Order=9, GroupName="5. Visuals")]
        public bool ShowOutcomeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath",
            Description="Folder for CSV files. Auto-created. Default C:\\temp.",
            Order=10, GroupName="6. Logging")]
        public string AuditLogPath { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name="MaxBitsKept",
            Description="Max in-memory bit string length before trimming. Default 5000.",
            Order=11, GroupName="7. Advanced")]
        public int MaxBitsKept { get; set; }

        #endregion
    }
}
