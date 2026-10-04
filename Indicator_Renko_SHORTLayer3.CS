#region Using declarations
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;                       // Brushes
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;                       // SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;        // Draw.TextFixed / TextPosition
#endregion

// HUD corner choices for THIS indicator. Declared in the GLOBAL namespace (same reason as in
// Indicator_Renko_mergedLayer1: NinjaTrader's generated wrapper code in other namespaces must see it).
// It has its OWN name (ShortHudCornerType) so it never collides with HudCornerType from the merged
// indicator — both files can be installed at the same time, and either can be removed alone.
public enum ShortHudCornerType { BottomLeft, BottomRight, TopLeft, TopRight, Center }

// =============================================================================
// Indicator_Renko_SHORTLayer3  —  SHORT-BOOK TRANSIT OUTCOME STRINGS (observer)
// =============================================================================
// PURPOSE
//   Does NOT trade. Shows the SHORT book's transit-outcome strings on the chart,
//   layer by layer, with the 1's percentage for the session and for a rolling
//   window, so you can watch the "health" of the setup through the day.
//
// DEFAULT SETUP (the one Renko pattern pipeline worth watching, from research)
//   F1 = 10    -> a red brick followed by a green bounce; chase the next RED
//   F2 = 10?   -> on the F1 outcome string: a win, a loss, then one-or-more wins
//   F3 = 00    -> on the F2 outcome string: the last two F2 outcomes both failed
//   Research (40-tick, 48 days): F3 trades won ~38.6-39.6% vs ~33-34% base,
//   breakeven 37.7% after costs. Promising but NOT proven: observation only.
//   Leave F2 and/or F3 blank to switch those layers off.
//
// HOW THE STRINGS ARE BUILT (SHORT encoding)
//   Brick bit:  RED brick (close fell) = 1 ,  GREEN brick (close rose) = 0.
//
//   Layer 0  raw string : one bit per brick (red=1 / green=0).
//   Layer 1  F1 string  : each time the raw tail matches F1, the NEXT brick is
//                         scored: RED = '1' (transit won), GREEN = '0'.
//   Layer 2  F2 string  : each time the F1-string tail matches F2, the NEXT F1
//                         outcome is copied into the F2 string.
//   Layer 3  F3 string  : each time the F2-string tail matches F3, the NEXT F2
//                         outcome is copied into the F3 string.
//                         -> The F3 string = the would-be MONEY trades.
//   "NEXT TRANSIT = MONEY" is shown when both F2 and F3 are armed, i.e. the next
//   F1 transit would be a real trade under this pipeline.
//
//   This matches the research convention ("the bit after each tail match",
//   overlapping matches allowed) used throughout the Renko studies.
//
// PERCENTAGES (per line)
//   "1s=12 = 40.0%"   -> share of 1's in that string so far this session
//   "last 20: 35.0%"  -> share of 1's in the most recent N bits (rolling window)
//   Research note: a LOW reading does not make a recovery more likely (no
//   "due" effect was found). If anything, stretches tend to persist slightly.
//
// SESSION / GAP RESETS, SEED, ONE-BRICK LAG
//   Same behaviour as Indicator_Renko_mergedLayer1:
//   * builds only from enable-time forward (historical bars ignored),
//   * optional clean restart on a new session (default ON),
//   * clean restart if bricks were skipped (disconnect hole),
//   * optional seed with the previously-closed brick on enable (default ON),
//   * an F1 match is scored one brick later (inherent).
//
// LOG (optional CSV, open-append-close)
//   One row per new F1 outcome:
//   timestamp,bit,inF2,inF3,f1_pct,f1_roll,f2_pct,f2_roll,f3_pct,f3_roll,f1,f2,f3
//   File: <LogBaseName>_F1-<p>_F2-<p>_F3-<p>_<yyyy-MM-dd_HH-mm-ss>.csv
//   ('*' -> 'z', '?' -> 'o', ',' -> '_', empty pattern -> 'off').
//
// MULTIPLE INSTANCES
//   Draws on its own panel (IsOverlay=false, DrawOnPricePanel=false). The HUD tag
//   includes all three patterns, so several instances never overwrite each other.
// =============================================================================
namespace NinjaTrader.NinjaScript.Indicators
{
    public class Indicator_Renko_SHORTLayer3 : Indicator
    {
        // ── strings (SHORT encoding: red=1 / green=0) ─────────────────────────
        private readonly StringBuilder rawStr = new StringBuilder();
        private readonly StringBuilder f1Str  = new StringBuilder();   // transit outcomes
        private readonly StringBuilder f2Str  = new StringBuilder();   // F1 outcomes after an F2 match
        private readonly StringBuilder f3Str  = new StringBuilder();   // F2 outcomes after an F3 match (money)
        private int f1Ones = 0, f2Ones = 0, f3Ones = 0;

        // ── pipeline state ────────────────────────────────────────────────────
        private int  prevBarBit   = -1;      // 1=red, 0=green
        private bool waitOutcome  = false;   // an F1 match is waiting for the next brick
        private bool f2Armed      = false;   // F1-string tail matches F2 -> next F1 outcome goes to F2
        private bool f3Armed      = false;   // F2-string tail matches F3 -> next F2 outcome goes to F3

        // ── life / session / gap ──────────────────────────────────────────────
        private bool lifeStarted      = false;
        private bool seedPending      = false;
        private bool firstBrickDone   = false;
        private int  lastProcessedBar = -1;
        private string activeLogFilePath = null;

        // ── parsed patterns (comma = OR) ──────────────────────────────────────
        private readonly List<string> p1 = new List<string>();
        private readonly List<string> p2 = new List<string>();
        private readonly List<string> p3 = new List<string>();

        private const string HUD_TAG = "Renko_SHORTLayer1_HUD";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Indicator_Renko_SHORTLayer3";
                Description = "Observer: SHORT-book Renko transit strings (F1 / F2 / F3) with session and "
                            + "rolling 1's percentages. Does not trade.";

                Calculate        = Calculate.OnBarClose;
                IsOverlay        = false;
                DrawOnPricePanel = false;
                IsAutoScale      = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                Filter1Pattern        = "10";
                Filter2Pattern        = "10?";
                Filter3Pattern        = "00";
                DisplayLastNBits      = 60;
                RollingWindowBits     = 20;
                BitFontSize           = 14;
                HudCorner             = ShortHudCornerType.BottomLeft;
                SeedPendingBarOnStart = true;
                RestartOnNewSession   = true;
                EnableStringLog       = true;
                LogFolder             = @"C:\temp";
                LogBaseName           = "Indicator_Renko_SHORTLayer3";
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    lifeStarted = true;
                    Parse(Filter1Pattern, p1); Parse(Filter2Pattern, p2); Parse(Filter3Pattern, p3);
                    Print("[Indicator_Renko_SHORTLayer3] F1=[" + string.Join(",", p1) + "] F2=["
                        + string.Join(",", p2) + "] F3=[" + string.Join(",", p3) + "]");
                    FreshStart("indicator enabled (fresh start)", SeedPendingBarOnStart);
                }
            }
        }

        // =====================================================================
        // OnBarUpdate — brick close -> raw bit -> F1 outcome -> F2 / F3 -> HUD
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;
            if (CurrentBar < 1) return;

            // new session / skipped bricks -> clean restart (no seed)
            if (firstBrickDone && Bars.IsFirstBarOfSession)
            {
                if (RestartOnNewSession) FreshStart("new trading session", false);
            }
            else if (lastProcessedBar >= 0 && CurrentBar > lastProcessedBar + 1)
            {
                FreshStart("brick gap: CurrentBar " + lastProcessedBar + " -> " + CurrentBar
                    + " (missed " + (CurrentBar - lastProcessedBar - 1) + " brick(s))", false);
            }
            lastProcessedBar = CurrentBar;
            firstBrickDone   = true;

            // one-time seed with the previously-closed brick (SHORT encoding: red=1)
            if (seedPending)
            {
                seedPending = false;
                if (CurrentBar >= 2)
                {
                    int seedBit = (Close[1] < Close[2]) ? 1 : 0;   // red=1, green (or equal)=0
                    rawStr.Append(seedBit == 1 ? '1' : '0');
                    prevBarBit = seedBit;
                }
            }

            // brick bit: red (close fell) = 1, green (close rose) = 0
            int bit;
            if (Close[0] < Close[1])      bit = 1;
            else if (Close[0] > Close[1]) bit = 0;
            else                          bit = (prevBarBit >= 0) ? prevBarBit : 0;
            prevBarBit = bit;

            // score the previous F1 match: next brick RED = transit won
            if (waitOutcome)
            {
                waitOutcome = false;
                AddF1Outcome(bit == 1 ? '1' : '0');
            }

            // append raw bit, then test F1 on the raw tail
            rawStr.Append(bit == 1 ? '1' : '0');
            if (rawStr.Length > 2048) rawStr.Remove(0, rawStr.Length - 2048);
            if (MatchAny(rawStr.ToString(), p1)) waitOutcome = true;

            UpdateDisplay();
        }

        // ── push one F1 outcome through the layers ───────────────────────────────
        private void AddF1Outcome(char b)
        {
            bool inF2 = false, inF3 = false;

            if (p2.Count > 0 && f2Armed)
            {
                inF2 = true;
                if (p3.Count > 0 && f3Armed)
                {
                    inF3 = true;
                    f3Str.Append(b); if (b == '1') f3Ones++;
                }
                f2Str.Append(b); if (b == '1') f2Ones++;
                f3Armed = p3.Count > 0 && MatchAny(f2Str.ToString(), p3);
            }

            f1Str.Append(b); if (b == '1') f1Ones++;
            f2Armed = p2.Count > 0 && MatchAny(f1Str.ToString(), p2);

            WriteLog(b, inF2, inF3);
        }

        // ── percentages ───────────────────────────────────────────────────────────
        private static double Pct(int ones, int n) { return n > 0 ? 100.0 * ones / n : 0.0; }

        private double Rolling(StringBuilder s, out int w)
        {
            w = 0;
            if (RollingWindowBits <= 0 || s.Length == 0) return -1;
            w = Math.Min(RollingWindowBits, s.Length);
            int ones = 0;
            for (int i = s.Length - w; i < s.Length; i++) if (s[i] == '1') ones++;
            return 100.0 * ones / w;
        }

        private string Line(string label, string pattern, StringBuilder s, int ones)
        {
            string head = label + " (" + pattern + ")  bits=" + s.Length;
            if (s.Length > 0)
            {
                head += "  1s=" + ones + " = " + Pct(ones, s.Length).ToString("F1") + "%";
                int w; double r = Rolling(s, out w);
                if (r >= 0) head += "  |  last " + w + ": " + r.ToString("F1") + "%";
            }
            string full = s.ToString();
            int n = (DisplayLastNBits > 0 && full.Length > DisplayLastNBits) ? DisplayLastNBits : full.Length;
            string tail = full.Length == 0 ? "-" : full.Substring(full.Length - n);
            return head + Environment.NewLine + "   " + tail;
        }

        // ── HUD ───────────────────────────────────────────────────────────────────
        private void UpdateDisplay()
        {
            var sb = new StringBuilder();
            sb.Append("SHORT book  (red=1)").Append(Environment.NewLine);
            sb.Append(Line("F1", Filter1Pattern, f1Str, f1Ones));
            if (p2.Count > 0) sb.Append(Environment.NewLine).Append(Line("F2", Filter2Pattern, f2Str, f2Ones));
            if (p3.Count > 0) sb.Append(Environment.NewLine).Append(Line("F3 = money", Filter3Pattern, f3Str, f3Ones));

            bool moneyNext = (p2.Count == 0 || f2Armed) && (p3.Count == 0 || f3Armed) && (p2.Count + p3.Count > 0);
            sb.Append(Environment.NewLine).Append(moneyNext
                ? ">>> NEXT TRANSIT = MONEY (F2" + (p3.Count > 0 ? "+F3" : "") + " armed)"
                : "    not armed");

            ShortHudCornerType c = HudCorner;
            NinjaTrader.NinjaScript.DrawingTools.TextPosition pos =
                c == ShortHudCornerType.BottomRight ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomRight :
                c == ShortHudCornerType.TopLeft     ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.TopLeft :
                c == ShortHudCornerType.TopRight    ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.TopRight :
                c == ShortHudCornerType.Center      ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.Center :
                                                      NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomLeft;

            Draw.TextFixed(this, HUD_TAG + "_" + Filter1Pattern + "_" + Filter2Pattern + "_" + Filter3Pattern,
                sb.ToString(), pos, Brushes.White, new SimpleFont("Consolas", BitFontSize),
                Brushes.DimGray, Brushes.Black, 50);
        }

        // ── log ───────────────────────────────────────────────────────────────────
        private void WriteLog(char b, bool inF2, bool inF3)
        {
            if (!EnableStringLog || activeLogFilePath == null) return;
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                int w;
                double r1 = Rolling(f1Str, out w), r2 = Rolling(f2Str, out w), r3 = Rolling(f3Str, out w);
                Func<double, string> f = v => v < 0 ? "" : v.ToString("F1", ci);
                string row = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "," + b + ","
                    + (inF2 ? "1" : "0") + "," + (inF3 ? "1" : "0") + ","
                    + Pct(f1Ones, f1Str.Length).ToString("F1", ci) + "," + f(r1) + ","
                    + (f2Str.Length > 0 ? Pct(f2Ones, f2Str.Length).ToString("F1", ci) : "") + "," + f(r2) + ","
                    + (f3Str.Length > 0 ? Pct(f3Ones, f3Str.Length).ToString("F1", ci) : "") + "," + f(r3) + ","
                    + f1Str + "," + f2Str + "," + f3Str;
                File.AppendAllText(activeLogFilePath, row + Environment.NewLine);
            }
            catch { /* never let a locked file break the indicator */ }
        }

        // ── fresh start ───────────────────────────────────────────────────────────
        private static string Safe(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "off";
            return p.Replace("*", "z").Replace("?", "o").Replace(",", "_");
        }

        private void FreshStart(string reason, bool seed)
        {
            seedPending = seed;
            rawStr.Clear(); f1Str.Clear(); f2Str.Clear(); f3Str.Clear();
            f1Ones = f2Ones = f3Ones = 0;
            waitOutcome = false; f2Armed = false; f3Armed = false;
            prevBarBit = -1;

            if (EnableStringLog)
            {
                try
                {
                    if (!Directory.Exists(LogFolder)) Directory.CreateDirectory(LogFolder);
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    activeLogFilePath = Path.Combine(LogFolder, LogBaseName
                        + "_F1-" + Safe(Filter1Pattern) + "_F2-" + Safe(Filter2Pattern)
                        + "_F3-" + Safe(Filter3Pattern) + "_" + stamp + ".csv");
                    File.AppendAllText(activeLogFilePath,
                        "# Indicator_Renko_SHORTLayer3  F1=" + Filter1Pattern + "  F2=" + Filter2Pattern
                            + "  F3=" + Filter3Pattern + "  reason=" + reason + "  seed=" + seed
                            + "  rollingWindow=" + RollingWindowBits + Environment.NewLine
                        + "timestamp,bit,inF2,inF3,f1_pct,f1_roll,f2_pct,f2_roll,f3_pct,f3_roll,f1,f2,f3"
                            + Environment.NewLine);
                }
                catch { activeLogFilePath = null; }
            }
            else activeLogFilePath = null;

            Print("[Indicator_Renko_SHORTLayer3] FRESH START: " + reason + " | seed=" + seed
                + " | log=" + (activeLogFilePath ?? "(off)"));
        }

        // =====================================================================
        // pattern parsing + wildcard tail matcher ('?' = one-or-more 1s, '*' = one-or-more 0s)
        // =====================================================================
        private static void Parse(string src, List<string> dst)
        {
            dst.Clear();
            if (string.IsNullOrWhiteSpace(src)) return;
            foreach (string tok in src.Split(','))
            {
                string t = (tok ?? "").Trim();
                if (t.Length == 0) continue;
                bool ok = true;
                foreach (char c in t) if (c != '0' && c != '1' && c != '*' && c != '?') { ok = false; break; }
                if (ok) dst.Add(t);
            }
        }

        private static bool MatchAny(string text, List<string> pats)
        {
            for (int i = 0; i < pats.Count; i++) if (TailMatches(text, pats[i])) return true;
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

        #region Properties
        [NinjaScriptProperty]
        [Display(Name="F1 Pattern (raw bricks; comma OR; ?=1+ ones *=1+ zeros)", Order=1, GroupName="1. Patterns")]
        public string Filter1Pattern { get; set; }

        [Display(Name="F2 Pattern (on F1 string; blank = off)", Order=2, GroupName="1. Patterns")]
        public string Filter2Pattern { get; set; }

        [Display(Name="F3 Pattern (on F2 string; blank = off)", Order=3, GroupName="1. Patterns")]
        public string Filter3Pattern { get; set; }

        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name="Display last N bits (per line)", Order=1, GroupName="2. Display")]
        public int DisplayLastNBits { get; set; }

        [Range(0, 1000)]
        [Display(Name="Rolling window (bits, 0 = hide)", Order=2, GroupName="2. Display")]
        public int RollingWindowBits { get; set; }

        [NinjaScriptProperty] [Range(6, 72)]
        [Display(Name="HUD font size", Order=3, GroupName="2. Display")]
        public int BitFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name="HUD corner", Order=4, GroupName="2. Display")]
        public ShortHudCornerType HudCorner { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Seed forming brick on start", Order=1, GroupName="3. Parity")]
        public bool SeedPendingBarOnStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Restart clean on new session", Order=2, GroupName="3. Parity")]
        public bool RestartOnNewSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable string log", Order=1, GroupName="4. Log")]
        public bool EnableStringLog { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Log Folder", Order=2, GroupName="4. Log")]
        public string LogFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Log Base Name", Order=3, GroupName="4. Log")]
        public string LogBaseName { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Indicator_Renko_SHORTLayer3[] cacheIndicator_Renko_SHORTLayer3;
		public Indicator_Renko_SHORTLayer3 Indicator_Renko_SHORTLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, ShortHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return Indicator_Renko_SHORTLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicator_Renko_SHORTLayer3 Indicator_Renko_SHORTLayer3(ISeries<double> input, string filter1Pattern, int displayLastNBits, int bitFontSize, ShortHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			if (cacheIndicator_Renko_SHORTLayer3 != null)
				for (int idx = 0; idx < cacheIndicator_Renko_SHORTLayer3.Length; idx++)
					if (cacheIndicator_Renko_SHORTLayer3[idx] != null && cacheIndicator_Renko_SHORTLayer3[idx].Filter1Pattern == filter1Pattern && cacheIndicator_Renko_SHORTLayer3[idx].DisplayLastNBits == displayLastNBits && cacheIndicator_Renko_SHORTLayer3[idx].BitFontSize == bitFontSize && cacheIndicator_Renko_SHORTLayer3[idx].HudCorner == hudCorner && cacheIndicator_Renko_SHORTLayer3[idx].SeedPendingBarOnStart == seedPendingBarOnStart && cacheIndicator_Renko_SHORTLayer3[idx].RestartOnNewSession == restartOnNewSession && cacheIndicator_Renko_SHORTLayer3[idx].EnableStringLog == enableStringLog && cacheIndicator_Renko_SHORTLayer3[idx].LogFolder == logFolder && cacheIndicator_Renko_SHORTLayer3[idx].LogBaseName == logBaseName && cacheIndicator_Renko_SHORTLayer3[idx].EqualsInput(input))
						return cacheIndicator_Renko_SHORTLayer3[idx];
			return CacheIndicator<Indicator_Renko_SHORTLayer3>(new Indicator_Renko_SHORTLayer3(){ Filter1Pattern = filter1Pattern, DisplayLastNBits = displayLastNBits, BitFontSize = bitFontSize, HudCorner = hudCorner, SeedPendingBarOnStart = seedPendingBarOnStart, RestartOnNewSession = restartOnNewSession, EnableStringLog = enableStringLog, LogFolder = logFolder, LogBaseName = logBaseName }, input, ref cacheIndicator_Renko_SHORTLayer3);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Indicator_Renko_SHORTLayer3 Indicator_Renko_SHORTLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, ShortHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_SHORTLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicators.Indicator_Renko_SHORTLayer3 Indicator_Renko_SHORTLayer3(ISeries<double> input , string filter1Pattern, int displayLastNBits, int bitFontSize, ShortHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_SHORTLayer3(input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Indicator_Renko_SHORTLayer3 Indicator_Renko_SHORTLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, ShortHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_SHORTLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicators.Indicator_Renko_SHORTLayer3 Indicator_Renko_SHORTLayer3(ISeries<double> input , string filter1Pattern, int displayLastNBits, int bitFontSize, ShortHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_SHORTLayer3(input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}
	}
}

#endregion
