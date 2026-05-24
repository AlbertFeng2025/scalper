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
// STRATEGY: scalper_RenkoStringPatternAlertEMA v1.3.3
// AUTHOR:   Albert Feng / Drafted with help from Claude
// REPLACES: v1.3.2
// =============================================================================
//
// v1.3.3 CHANGES vs v1.3.2
// ------------------------
//
// CHANGE 1 - AlertPattern accepts a COMMA-SEPARATED LIST of patterns
//   Old: AlertPattern was a single string, e.g. "01"
//   New: AlertPattern is a comma-separated list, e.g. "01, 011, 0111"
//        Whitespace around commas is trimmed. Each entry must be 1-10 chars
//        of '0' or '1'. Empty entries (e.g. from stray commas like "01,,011")
//        are skipped silently. Duplicates are deduplicated with a Print
//        warning. If the entire list is empty or all entries invalid, the
//        strategy refuses to start (existing behavior).
//
//   Backward compatible: a single pattern like "01" still works exactly the
//   same as v1.3.2.
//
// CHANGE 2 - Alert firing rule: ONE alert per brick close (boolean OR)
//   On every EMA-qualified outcome bit appended to tradedOutcomeString,
//   we check each pattern in the list. If ANY pattern's suffix-match
//   succeeds (tradedOutcomeString ends with that pattern), the strategy
//   fires EXACTLY ONE alert for that brick close - regardless of how many
//   patterns matched. One sound, one diamond, one CSV row, one increment
//   of DailyAlertNumber, one increment of pendingPostAlertCaptures.
//
//   Rationale: this is a "pattern-on-pattern-on-pattern" composite
//   strategy. Without a weighting scheme, multiple simultaneous matches
//   contribute no extra information today. User can do weighted/per-pattern
//   analysis later by post-processing the CSV.
//
// CHANGE 3 - Suffix-overlap warning at startup
//   For every pair of patterns (a, b) in the list where len(a) < len(b),
//   if b.EndsWith(a), the strategy prints a [VALIDATE] WARN to the Output
//   window AND writes a warning to NinjaTrader's Log tab. The strategy
//   still runs. The warning explains that the suffix pattern is redundant
//   under the current "one alert per brick" rule, since whenever the
//   longer pattern matches, the shorter one also matches but no extra
//   alert fires.
//
//   Examples:
//     "1, 11"           -> WARN: "1" is a suffix of "11"
//     "101, 10101"      -> WARN: "101" is a suffix of "10101"
//     "01, 011, 0111"   -> no warning (none is a suffix of another)
//     "01, 1010"        -> no warning
//
// CHANGE 4 - CSV columns UNCHANGED
//   The AlertPattern column in both occurrence CSV and alert CSV continues
//   to log the FULL LIST as entered by the user (e.g. "01, 011, 0111").
//   No new MatchedPattern column. Rationale: composite-strategy logging.
//   For per-pattern analysis, the user re-runs with one pattern at a time.
//
// CHANGE 5 - CSV filenames bumped to _v133_
//   To prevent collision with v1.3.2 data files.
//
// CHANGE 6 - Description text updated on AlertPattern parameter
//   Now documents comma-separated list and suffix-overlap caveat.
//
// =============================================================================
//
// (v1.3.2 changes documented in prior versions retained below for history.)
//
// v1.3.2 CHANGES vs v1.3.1
//   - EMA qualification uses OPEN (not CLOSE) of the pattern's last brick
//   - Added PostAlertOutcomeString column capturing chase-trade outcomes
//
// v1.3 CHANGES vs v1.2.1
//   - Bit encoding: S=1, F=0 (matches brick green=1/red=0)
//   - AlertMode/Threshold removed; replaced by AlertPattern suffix match
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
        private StringBuilder outcomeString = new StringBuilder();
        private StringBuilder tradedOutcomeString = new StringBuilder();
        private StringBuilder postAlertOutcomeString = new StringBuilder();
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

        // [v1.3.3] AlertPattern is now a LIST of patterns. Stored as a List<string>
        // (each entry is the raw "01"/"011"/etc. string used for tail compare).
        private List<string> compiledAlertPatterns = null;

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
            public double    OpenAtDetect;

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
                Description = "Renko string-pattern alerter (v1.3.3). EMA qualification uses Open. AlertPattern now accepts a comma-separated list (e.g. '01, 011, 0111'); one alert fires per brick close if ANY pattern in the list matches the EMA-qualified outcome tail. Places NO orders.";
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

                // ---- [v1.3.3] AlertPattern now accepts a comma-separated list ----
                AlertPattern     = "01,011,0111";

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
                Print(string.Format("[INIT] scalper_RenkoStringPatternAlertEMA v1.3.3 at {0}",
                    DateTime.Now.ToString("HH:mm:ss.fff")));

                Print(string.Format("[INIT] BarsPeriod.BarsPeriodType = {0}", BarsPeriod.BarsPeriodType));
                Print(string.Format("[INIT] BarsPeriod.Value  = {0}  (for standard Renko: brick size in ticks)", BarsPeriod.Value));
                Print(string.Format("[INIT] BarsPeriod.Value2 = {0}  (UniRenko/BetterRenko only)", BarsPeriod.Value2));
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

                // [v1.3.3] Log the parsed alert pattern list
                Print(string.Format("[INIT] AlertPattern (raw input) = \"{0}\"", AlertPattern));
                Print(string.Format("[INIT] AlertPattern (parsed list, {0} pattern(s)): [{1}]",
                    compiledAlertPatterns.Count, string.Join(", ", compiledAlertPatterns)));

                // [v1.3.3] Run suffix-overlap validation and print warnings
                CheckForSuffixOverlaps();

                Print("[INIT] Bit encoding: SUCCESS=1, FAILURE=0 (matches brick green=1/red=0).");
                Print("[INIT] Each pattern in AlertPattern list is suffix-matched against OutcomeString_EmaQualified.");
                Print("[INIT] Rule: at each EMA-qualified brick close, if ANY pattern matches the tail, fire ONE alert.");
                Print(string.Format("[INIT] EMA1 period = {0}, EMA2 period = {1}", EMA1Period, EMA2Period));
                Print("[INIT] EMA filter: trade only when Open[0] > EMA1 AND Open[0] > EMA2.");
                Print("[INIT] PostAlertOutcomeString: after each alert, captures the bit of the NEXT EMA-qualified outcome.");
                Print(string.Format("[INIT] Daily reset: 9:30 AM NY (DST-safe)"));
                Print(string.Format("[INIT] Beep: {0} beeps, {1}s apart", AlertSoundCount, AlertReminderSecs));
                Print(string.Format("[INIT] CSV path: {0}", AuditLogPath));
                Print("[INIT] This strategy places NO orders.");
            }
        }

        // =====================================================================
        // [v1.3.3] TryCompilePatterns - now parses comma-separated AlertPattern
        // =====================================================================
        private bool TryCompilePatterns()
        {
            compiledPattern  = TryCompileOne(PatternToMatch,  "PatternToMatch");
            compiledFollowUp = TryCompileOne(FollowUpPattern, "FollowUpPattern");

            // [v1.3.3] Parse AlertPattern as comma-separated list
            compiledAlertPatterns = ParseAlertPatternList(AlertPattern);

            return compiledPattern != null
                && compiledFollowUp != null
                && compiledAlertPatterns != null
                && compiledAlertPatterns.Count > 0;
        }

        // [v1.3.3] Parse comma-separated AlertPattern input into a list of valid patterns.
        // - Trims whitespace around each entry
        // - Skips empty entries silently (handles stray commas like "01,,011")
        // - Each entry must be 1-10 chars of '0' or '1'
        // - Invalid entries are reported with a Print() and skipped
        // - Duplicates are deduplicated with a Print() warning
        // - Returns null if input is empty/whitespace, empty list if no valid entries
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
                if (p.Length == 0) continue;       // skip empty entries silently
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

        // [v1.3.3] Warn user if any pattern is a suffix of another. Such overlaps
        // are technically allowed but produce no extra information under the
        // current "one alert per brick" rule — when the longer pattern matches,
        // the shorter one also matches, but only one alert fires.
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
                        try { Log(msg, LogLevel.Warning); } catch { /* Log may not be available in all contexts */ }
                        warnCount++;
                    }
                }
            }
            if (warnCount == 0)
                Print("[VALIDATE] AlertPattern list: no suffix overlaps detected.");
            else
                Print(string.Format("[VALIDATE] AlertPattern list: {0} suffix-overlap warning(s) above. Strategy will still run.", warnCount));
        }

        private int[] TryCompileOne(string s, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                Print(string.Format("[VALIDATE] *** {0} is empty. Must be 1-10 chars of '0' or '1' only. ***", fieldName));
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
                    Print(string.Format("[RESET] Starting fresh on trading day {0:yyyy-MM-dd}", effectiveTradingDate));
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
                        OpenAtDetect      = openPrice
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
        // [v1.3.3] AlertPattern is now a list. Fire ONE alert per brick if ANY
        // pattern in the list matches the tail of tradedOutcomeString.
        // =====================================================================
        private void HandleOccurrenceOutcome(PendingOccurrence po, string outcome, double currentPrice)
        {
            outcomeString.Append(outcome == "S" ? '1' : '0');

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

                // Post-alert capture (BEFORE checking for new alert, so alert
                // doesn't capture itself)
                if (pendingPostAlertCaptures > 0)
                {
                    capturedBit = outcome == "S" ? '1' : '0';
                    postAlertOutcomeString.Append(capturedBit);
                    pendingPostAlertCaptures--;
                    capturedPostAlert = true;
                    Print(string.Format("[POSTALERT] Captured outcome bit '{0}' for prior alert. PostAlertOutcomeString = \"{1}\". Pending captures remaining = {2}.",
                        capturedBit, postAlertOutcomeString.ToString(), pendingPostAlertCaptures));
                }

                // [v1.3.3] One alert per brick if ANY pattern matches
                if (AnyAlertPatternMatchesTail())
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
        // [v1.3.3] AnyAlertPatternMatchesTail
        // Returns true iff ANY pattern in compiledAlertPatterns is a suffix of
        // the current tradedOutcomeString. Boolean OR across all patterns.
        // =====================================================================
        private bool AnyAlertPatternMatchesTail()
        {
            string tail = tradedOutcomeString.ToString();
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

        // =====================================================================
        // FireAlert
        // [v1.3.3] No changes to body; semantics unchanged (one call = one alert).
        // =====================================================================
        private void FireAlert()
        {
            dailyAlertNumber++;
            pendingPostAlertCaptures++;

            Print("================================================================");
            Print(string.Format("[ALERT] *** AlertPattern list matched the tail of OutcomeString_EmaQualified at {0}. Daily alert #{1}. PendingPostAlertCaptures = {2}. ***",
                Time[0].ToString("HH:mm:ss"), dailyAlertNumber, pendingPostAlertCaptures));
            Print(string.Format("[ALERT] OutcomeString_EmaQualified tail (last 20): \"...{0}\"",
                tradedOutcomeString.Length > 20
                    ? tradedOutcomeString.ToString().Substring(tradedOutcomeString.Length - 20)
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
                // [v1.3.3] Show the user's full list (not a single pattern) on chart
                Draw.Text(this, txt, string.Format("ALERT #{0}\nAP=\"{1}\"", dailyAlertNumber, AlertPattern),
                    0, High[0] + (10 * TickSize), Brushes.Magenta);
            }

            WriteAlertRow();
        }

        // =====================================================================
        // CSV: per-occurrence rows
        // [v1.3.3] Filename bumped to _v133_occ.csv. AlertPattern column logs
        // the full user input string (e.g. "01, 011, 0111").
        // =====================================================================
        private void WriteOccurrenceRow(PendingOccurrence po, string outcome, double endPrice, bool firedAlert, bool capturedPostAlert, char capturedBit)
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_v133_occ.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.3.3");
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
                        sw.WriteLine(string.Format("# AlertPattern={0}", AlertPattern));   // raw user input (may be list)
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
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
                        sw.WriteLine("# PatternEndOpen                       : Open of the pattern's last brick (used for EMA qualification)");
                        sw.WriteLine("# PatternEndClose                      : Close of the pattern's last brick");
                        sw.WriteLine("# OutcomePrice                         : Close of the brick that completed the follow-up sequence");
                        sw.WriteLine("# AlertPattern                         : the AlertPattern user input (may be a comma-separated list)");
                        sw.WriteLine("# DailyOccurrenceNumber                : 1-based count of ALL pattern occurrences since the 9:30 NY reset");
                        sw.WriteLine("# OutcomeString_NoEmaFilter            : running string of every occurrence's outcome bit (S=1, F=0), today");
                        sw.WriteLine("# Ema1AtDetect / Ema2AtDetect          : EMA values at the moment of detection");
                        sw.WriteLine("# EmaQualified                         : YES if PatternEndOpen was above BOTH EMAs");
                        sw.WriteLine("# DailyEmaQualifiedOccurrenceNumber    : 1-based count of EMA-qualified occurrences today (0 if not qualified)");
                        sw.WriteLine("# OutcomeString_EmaQualified           : running string of EMA-qualified outcome bits today");
                        sw.WriteLine("# FiredAlertOnThisRow                  : YES if this row's appended bit caused ANY pattern in AlertPattern list to match the tail");
                        sw.WriteLine("# CapturedPostAlert                    : YES if this row's bit was captured as a 'next outcome after a prior alert'");
                        sw.WriteLine("# CapturedBit                          : the bit captured (1=S, 0=F), or blank if no capture");
                        sw.WriteLine("# PostAlertOutcomeString               : growing string of post-alert capture bits; analyze for consecutive runs / hit rate");
                        sw.WriteLine("# PendingPostAlertCapturesAfter        : how many alerts are still waiting for their next qualified outcome");
                        sw.WriteLine("#");

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

                    // [v1.3.3] If user entered a comma in AlertPattern, the raw value would break the CSV.
                    // Wrap it in double quotes when it contains a comma.
                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5},{6},{7:F2},{8:F2},{9:F2},{10},{11},{12},{13:F2},{14:F2},{15},{16},{17},{18},{19},{20},{21},{22}",
                        TimeZoneInfo.ConvertTime(po.OccurrenceTime, nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        po.PatternEndIndex,
                        PatternToMatch,
                        FollowUpPattern,
                        actualFollow,
                        outcome,
                        po.OpenAtDetect,
                        po.PatternEndPrice,
                        endPrice,
                        apForCsv,                                  // [v1.3.3] quoted if it contains a comma
                        po.OccurrenceNumberAtDetect,
                        outcomeString.ToString(),
                        po.Ema1AtDetect,
                        po.Ema2AtDetect,
                        po.EmaQualified ? "YES" : "NO",
                        po.TradedOccurrenceNumberAtDetect,
                        tradedOutcomeString.ToString(),
                        firedAlert ? "YES" : "NO",
                        capturedPostAlert ? "YES" : "NO",
                        capturedPostAlert ? capturedBit.ToString() : "",
                        postAlertOutcomeString.ToString(),
                        pendingPostAlertCaptures));
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[CSV-OCC] ERROR: {0}", ex.Message));
            }
        }

        // =====================================================================
        // CSV: per-alert rows
        // [v1.3.3] Filename bumped to _v133_alert.csv. AlertPattern column logs
        // the full user input string.
        // =====================================================================
        private void WriteAlertRow()
        {
            try
            {
                if (!Directory.Exists(AuditLogPath))
                    Directory.CreateDirectory(AuditLogPath);

                string logFile = Path.Combine(AuditLogPath, "scalper_RenkoStringPatternAlertEMA_v133_alert.csv");
                bool fileExists = File.Exists(logFile);

                using (StreamWriter sw = new StreamWriter(logFile, true))
                {
                    if (!fileExists)
                    {
                        sw.WriteLine("# schema_version=1.3.3");
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
                        sw.WriteLine(string.Format("# AlertPatternParsed=[{0}]", string.Join(", ", compiledAlertPatterns)));
                        sw.WriteLine(string.Format("# EMA1Period={0}", EMA1Period));
                        sw.WriteLine(string.Format("# EMA2Period={0}", EMA2Period));
                        sw.WriteLine("# all timestamps in this file are NEW YORK time (America/New_York, DST-aware)");
                        sw.WriteLine("#");
                        sw.WriteLine("# ---- COLUMN LEGEND ----");
                        sw.WriteLine("# AlertTime_NY                  : NY time when this alert fired");
                        sw.WriteLine("# DailyAlertNumber              : 1-based alert count since the 9:30 NY reset");
                        sw.WriteLine("# AlertPattern                  : the user input (may be a comma-separated list)");
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

                    string apForCsv = AlertPattern.Contains(",") ? "\"" + AlertPattern + "\"" : AlertPattern;

                    sw.WriteLine(string.Format(
                        "{0},{1},{2},{3},{4},{5:F2},{6},{7},{8},{9}",
                        TimeZoneInfo.ConvertTime(Time[0], nyTz).ToString("yyyy-MM-dd HH:mm:ss"),
                        dailyAlertNumber,
                        apForCsv,
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
            postAlertOutcomeString.Clear();
            pendingPostAlertCaptures = 0;
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name="PatternToMatch",
            Description="Bit pattern to detect, 1-10 chars of '0' (red) or '1' (green). Default \"011\".",
            Order=1, GroupName="1. Layer 1 - Pattern")]
        public string PatternToMatch { get; set; }

        [NinjaScriptProperty]
        [Display(Name="FollowUpPattern",
            Description="Required follow-up bricks for SUCCESS, 1-10 chars of '0' or '1'. Must match EXACTLY. Default \"1\".",
            Order=2, GroupName="1. Layer 1 - Pattern")]
        public string FollowUpPattern { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AlertPattern (comma-separated list OK)",
            Description="[v1.3.3] One or more suffix patterns, comma-separated. Each entry is 1-10 chars of '0'/'1'. At each EMA-qualified brick close, if ANY pattern matches the tail of OutcomeString_EmaQualified, ONE alert fires (sound + diamond + CSV row). Examples: \"01\" (single pattern), \"01, 011, 0111\" (any of three). WARNING: avoid lists where one entry is a suffix of another (e.g. \"1, 11\" or \"101, 10101\") - the shorter pattern is redundant under the one-alert-per-brick rule. The strategy will print a warning but still run.",
            Order=3, GroupName="2. Alert")]
        public string AlertPattern { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name="EMA1Period",
            Description="Period for EMA1. A trade is qualified only when pattern's last-brick OPEN > EMA1 AND > EMA2. Default 20.",
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
            Description="Show 'PT/S' (green) or 'PT/F' (red) text at each outcome brick. '###' prefix means EMA-qualified.",
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
