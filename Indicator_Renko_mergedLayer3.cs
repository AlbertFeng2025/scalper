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

// HUD corner choices exposed to the user (mapped to NT's TextPosition at draw time).
// IMPORTANT: declared in the GLOBAL namespace (outside NinjaTrader.NinjaScript.Indicators) on purpose.
// At compile time NinjaTrader auto-generates wrapper methods for this indicator in several OTHER
// namespaces (Strategies, MarketAnalyzers, ...) that reference this enum by its simple name; only a
// global-namespace type is visible from all of them. Nesting it in the Indicators namespace causes
// CS0246 "HudCornerType could not be found" in that generated code.
public enum HudCornerType { BottomLeft, BottomRight, TopLeft, TopRight, Center }

// =============================================================================
// Indicator_Renko_mergedLayer3  —  F1 MERGED-TRANSIT OUTCOME STRING (observer)
// =============================================================================
// CHANGES 2026-10-26 (b) — LAST-LAYER ALERT
//   * NEW group "6. Alert": "Last Layer Alert Pattern" (default 1000). Watches the deepest
//     active string (F3, else F2, else the merged F1 string). Each new bit whose tail matches
//     -> ONE sound + one row in NinjaTrader's Alerts window + "<<< ALARMING NOW" on that HUD
//     line. After the next bit the line keeps "last alert hh:mm:ss".
//   * Uses the BACKFILL block when back fill is on, else LIVE. Replayed history never sounds.
//   * Alert hours (New York time, ET), default 9:30-16:00 ET (= 6:30 AM - 1:00 PM PT).
//     Outside the hours: no sound, but the HUD text still updates.
//   * "Include back fill" now defaults to ON.
//
// CHANGES 2026-10-26 — real Layer 2 / Layer 3 on the MERGED string
//   NEW "F2 Pattern" and "F3 Pattern" (default BLANK -> the indicator behaves and looks
//   exactly like before: Layer 1, F1 line only).
//     * F2 (on the merged F1 outcome string): each time the F1-string tail matches F2,
//       the NEXT merged F1 outcome is copied into the F2 string.
//     * F3 (on the F2 string): each time the F2-string tail matches F3, the NEXT F2
//       outcome is copied into the F3 string.
//     * The deepest active string is the would-be MONEY trades ("= money" on the HUD).
//     * ">>> NEXT TRANSIT = MONEY" when F2 (and F3, if set) is armed.
//   F3 needs F2: if F2 is blank, F3 is ignored (noted in the Output window + log).
//   Same rules for LIVE and BACKFILL. Same wildcards (?=1+ ones, *=1+ zeros, comma = OR).
//   LOG: 8 columns ADDED AT THE END (old columns keep their positions):
//     inF2,inF3,f2_pct,f2_string,f3_pct,f3_string,bf_f2_string,bf_f3_string
//   Log file name gets _F2-<p>_F3-<p> only when F2 is set (Layer-1 file names unchanged).
//   F2/F3 are plain properties, so the generated-code region is unchanged.
//
// CHANGES 2026-10-16 — "Include back fill" option (default OFF)
//   When ON, a second HUD line (BACKFILL) shows the F1 outcome string rebuilt from
//   the chart's stored bricks back to the CURRENT SESSION start (max "Back-fill max
//   bricks", default 1000) — the same back-fill the strategies do on enable — and
//   then keeps growing with every live brick, side by side with the LIVE line.
//     (F1=10)  LIVE      bits=58  1s=19 = 32.8%  |  last 20: 35.0%   since 17:50:08
//     0000010011000000100001010001110011100000100001010011000101
//     (F1=10)  BACKFILL  bits=68  1s=22 = 32.4%  |  last 20: 35.0%   since 15:00:08
//     0000000101|0000010011000000100001010001110011100000100001010011000101
//   * '|' (optional) marks where the LIVE line begins: everything right of it
//     should be identical to LIVE. If BACKFILL does NOT end with the LIVE string,
//     the BACKFILL header shows "(!) differs from LIVE".
//   * The back-fill runs on the FIRST live brick after enable (same timing as the
//     strategies, so it reads exactly the bricks they would). After a brick gap the
//     LIVE line restarts but BACKFILL is rebuilt from history, so it keeps the session.
//   * LOG: one '# BACKFILL:' summary line, plus 4 extra columns on every row:
//     bf_bits,bf_pct_session,bf_pct_rolling,bf_string (empty when the option is off).
//   * The 3 new settings are plain properties (like "Rolling window"), so the
//     generated-code region at the bottom is unchanged.
//
// WHAT CHANGED IN THE PREVIOUS VERSION
//   The HUD header now also shows the 1's percentage:
//     (F1=10)  bits=123  1s=45 = 36.6%  |  last 20: 40.0%
//   * "1s=45 = 36.6%"  -> wins so far THIS SESSION (resets on a fresh start /
//                         new session, exactly like the string itself).
//   * "last 20: 40.0%" -> rolling 1's percentage over the most recent N bits
//                         (N = "Rolling window (bits)"; 0 = hide). This works like
//                         an RSI for the string: it shows the CURRENT stretch,
//                         while the session % shows the whole day.
//   The CSV log gains two columns: pct_session, pct_rolling.
//
//   The rolling-window setting is a plain (non-NinjaScriptProperty) property on
//   purpose: it shows in the indicator dialog and is saved with the chart, but it
//   does NOT change the indicator's generated method signature, so the
//   "NinjaScript generated code" region at the bottom stays valid unchanged.
//
// PURPOSE
//   This indicator does NOT trade. It is a *pre-processing lens*: it converts the
//   raw Renko brick stream into the same merged 1/0 outcome string that the
//   Renko_mergedTransit strategies build internally, and prints it on the chart so
//   your eye can read the transit history at a glance and THEN decide — by
//   intuition — which layer (1/2/3) and which F1/F2/F3 pattern combo to deploy.
//
//   The merge logic below (bit derivation, both-book test, next-brick resolution,
//   seeding, and session/gap resets) is a VERBATIM port of Renko_mergedTransit_Layer1
//   so the string you read here equals what a deployed strategy holds — provided
//   the two parity switches (Seed / Restart-on-new-session) are set the same.
//
// MULTIPLE INSTANCES ON ONE CHART
//   * IsOverlay = false and DrawOnPricePanel = false: each instance draws its HUD on
//     ITS OWN panel (set "Panel" in the indicator dialog, e.g. F1=10 -> panel 2,
//     F1=1000 -> panel 3). After compiling, REMOVE and RE-ADD the indicator so new
//     defaults apply. The panel shows only the HUD (this indicator has no plots).
//   * The HUD tag includes the F1 pattern, so instances never share a drawing tag.
//   * The log file name includes the F1 pattern, so two instances started in the
//     same second never write into the same CSV.
//
// -----------------------------------------------------------------------------
// HOW THE STRING IS BUILT
//   Brick bit (LONG encoding):   Close rose = GREEN = 1 ,  Close fell = RED = 0.
//   shortStr = the bitwise flip of longStr (GREEN=0 / RED=1).
//
//   Each brick, F1 is tested against BOTH strings' tails:
//     - longStr  tail matches F1  -> a LONG  transit setup (e.g. "10" = green->red,
//                                     chase the next GREEN)
//     - shortStr tail matches F1  -> a SHORT transit setup (e.g. "10" = real
//                                     red->green, chase the next RED)
//   For F1="10" the two never fire on the same brick.
//
//   A match ARMS an outcome that is RESOLVED on the NEXT brick, and appends ONE bit
//   to the merged outcome string:
//     - LONG  match:  next brick GREEN (bit==1) -> WIN -> '1' , else '0'
//     - SHORT match:  next brick RED   (bit==0) -> WIN -> '1' , else '0'
//   So: one bit per F1 occurrence, long and short interleaved in time,
//       1 = the chase would have won (transit completed) , 0 = missed.
//
// ONE-BRICK LAG (inherent, not a bug)
//   A match on the just-closed brick cannot be scored until the FOLLOWING brick
//   closes. So the newest occurrence's bit appears one brick later.
//
// PARITY — IMPORTANT
//   To make your eye-read equal a deployed strategy's internal string, run this
//   observer with the SAME "Seed forming brick on start" and "Restart clean on new
//   session" values you'll deploy. Defaults here match the strategy defaults (both ON).
//
// LOG
//   Optional CSV (open-append-close, never holds the file open). One row per new
//   bit:  timestamp,side,bit,pct_session,pct_rolling,string
//   File name: <LogBaseName>_F1-<pattern>_<yyyy-MM-dd_HH-mm-ss>.csv
//   ('*' is written as 'z', '?' as 'o', ',' as '_').
// =============================================================================
namespace NinjaTrader.NinjaScript.Indicators
{
    public class Indicator_Renko_mergedLayer3 : Indicator
    {
        // ── merged pipeline strings (LONG encoding: green=1/red=0) ────────────
        private readonly StringBuilder longStr        = new StringBuilder();
        private readonly StringBuilder shortStr       = new StringBuilder();
        private readonly StringBuilder filter1Outcome = new StringBuilder(); // merged, win-encoded

        // ── 1's counter for the session percentage (reset with the string) ───
        private int onesCount = 0;

        // ── pipeline state ────────────────────────────────────────────────────
        private int  prevBarBit       = -1;    // 1=green, 0=red
        private bool waitLongOutcome  = false;
        private bool waitShortOutcome = false;

        // ── life / session / gap ──────────────────────────────────────────────
        private bool lifeStarted     = false;
        private bool seedPending      = false;
        private bool firstBrickDone   = false;
        private int  lastProcessedBar = -1;
        private string activeLogFilePath = null;

        private DateTime liveSince = DateTime.MinValue;   // when the LIVE line last (re)started

        // ── BACKFILL pipeline (separate state; same rules as LIVE) ────────────
        private readonly StringBuilder bfLong    = new StringBuilder();
        private readonly StringBuilder bfShort   = new StringBuilder();
        private readonly StringBuilder bfOutcome = new StringBuilder();
        private int      bfOnes      = 0;
        private int      bfPrevBit   = -1;
        private bool     bfWaitLong  = false;
        private bool     bfWaitShort = false;
        private bool     bfNeedReplay = false;              // replay history on the next brick
        private bool     bfReady      = false;              // replay has run (line is valid)
        private DateTime bfSince      = DateTime.MinValue;  // time of the first brick in BACKFILL

        // ── parsed F1 list ────────────────────────────────────────────────────
        private readonly List<string> filter1Patterns = new List<string>();
        private readonly List<string> filter2Patterns = new List<string>();   // blank = Layer 1
        private readonly List<string> filter3Patterns = new List<string>();   // blank = Layer 2 (needs F2)

        // ── F2 / F3 chain on top of a merged F1 outcome string (LIVE and BACKFILL) ──
        private class Chain
        {
            public readonly StringBuilder F2 = new StringBuilder();
            public readonly StringBuilder F3 = new StringBuilder();
            public int  O2, O3;          // 1's counts
            public bool A2, A3;          // F2 armed (next F1 outcome goes into F2) / F3 armed
            public void Reset() { F2.Clear(); F3.Clear(); O2 = O3 = 0; A2 = A3 = false; }
        }
        private readonly Chain liveChain = new Chain();
        private readonly Chain bfChain   = new Chain();

        // Call right AFTER a new outcome b was appended to the F1 string f1.
        // Uses the arm state from BEFORE this outcome, then re-arms from the new strings.
        private void ChainFeed(Chain c, StringBuilder f1, char b, out bool inF2, out bool inF3)
        {
            inF2 = false; inF3 = false;
            if (filter2Patterns.Count > 0 && c.A2)
            {
                inF2 = true;
                if (filter3Patterns.Count > 0 && c.A3) { inF3 = true; c.F3.Append(b); if (b == '1') c.O3++; }
                c.F2.Append(b); if (b == '1') c.O2++;
                c.A3 = filter3Patterns.Count > 0 && MatchAny(c.F2.ToString(), filter3Patterns);
            }
            c.A2 = filter2Patterns.Count > 0 && MatchAny(f1.ToString(), filter2Patterns);
        }

        private bool MoneyNext(Chain c)
        { return filter2Patterns.Count > 0 && c.A2 && (filter3Patterns.Count == 0 || c.A3); }

        // =====================================================================
        // LAST-LAYER ALERT (2026-10-26)
        //   Watches the DEEPEST active string (F3 if set, else F2 if set, else F1) of the
        //   BACKFILL block when back fill is on, otherwise of the LIVE block.
        //   Each time a NEW bit is added to that string and its tail matches the alert
        //   pattern: one sound + one Alerts-window row (only inside the alert hours), and
        //   the HUD line shows "<<< ALARMING NOW". The text stays as "last alert hh:mm:ss"
        //   after the next bit, so you can read it when you come back to the desk.
        //   Bits rebuilt by the back-fill replay itself never sound (only new live bricks).
        // =====================================================================
        private readonly List<string> pAlert = new List<string>();
        private bool     alarmNow      = false;
        private DateTime lastAlertTime = DateTime.MinValue;
        private int      alertCount    = 0;
        private TimeZoneInfo nyZone    = null;

        private DateTime NyNow()
        {
            try
            {
                if (nyZone == null) nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.Local, nyZone);
            }
            catch { return DateTime.Now; }
        }

        private bool InAlertHours()
        {
            if (!EnableAlertHours) return true;
            DateTime ny = NyNow();
            int now = ny.Hour * 60 + ny.Minute;
            int a = AlertStartHourET * 60 + AlertStartMinuteET, b = AlertEndHourET * 60 + AlertEndMinuteET;
            return a <= b ? (now >= a && now < b) : (now >= a || now < b);   // also allows an overnight window
        }

        // Called once per brick with the deepest string and whether it got a new bit on this brick.
        private void CheckAlert(string deepest, bool grew, string what)
        {
            if (!grew || pAlert.Count == 0) return;
            alarmNow = MatchAny(deepest, pAlert);
            if (!alarmNow) return;
            lastAlertTime = DateTime.Now; alertCount++;
            bool sound = InAlertHours();
            string msg = AlertTitle() + ": " + what + " ends with " + AlertPattern + " (" + lastAlertTime.ToString("HH:mm:ss") + ")";
            Print("[" + Name + "] ALERT " + msg + (sound ? "" : "  (outside alert hours: no sound)"));
            if (sound)
            {
                try
                {
                    string snd = AlertSoundFile ?? "";
                    if (snd.Length > 0 && !Path.IsPathRooted(snd))
                        snd = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", snd);
                    Alert("LastLayerAlert_" + alertCount, Priority.High, msg, snd, 0, Brushes.DarkOrange, Brushes.Black);
                }
                catch (Exception ex) { Print("[" + Name + "] alert error: " + ex.Message); }
            }
        }

        private string AlarmNote()
        {
            if (pAlert.Count == 0) return "";
            if (alarmNow) return "   <<< ALARMING NOW (" + AlertPattern + ") " + lastAlertTime.ToString("HH:mm:ss");
            if (lastAlertTime != DateTime.MinValue) return "   last alert (" + AlertPattern + ") " + lastAlertTime.ToString("HH:mm:ss");
            return "";
        }

        private string AlertTitle()
        { return "MERGED F1=" + Filter1Pattern + (filter2Patterns.Count > 0 ? " F2=" + Filter2Pattern : "") + (filter3Patterns.Count > 0 ? " F3=" + Filter3Pattern : ""); }

        private StringBuilder Deepest(StringBuilder f1, Chain c)
        { return filter3Patterns.Count > 0 ? c.F3 : filter2Patterns.Count > 0 ? c.F2 : f1; }

        private bool AlertOnBackfill() { return IncludeBackFill && bfReady; }

        private const string HUD_TAG = "Renko_mergedLayer1_F1HUD";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Indicator_Renko_mergedLayer3";
                Description = "Observer: prints the MERGED F1 transit-outcome 1/0 string (1=transit completed, "
                            + "0=missed) plus the session and rolling 1's percentage. Does not trade.";

                Calculate        = Calculate.OnBarClose;   // match the strategy: score at brick close
                IsOverlay        = false;                  // own panel by default (one panel per instance)
                DrawOnPricePanel = false;                  // draw the HUD on THIS indicator's panel
                IsAutoScale      = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                // ── defaults (mirror the strategy) ──
                Filter1Pattern        = "10";          // comma-OR; wildcards ?=1+ ones, *=1+ zeros supported
                DisplayLastNBits      = 100;           // how many trailing bits to show on the chart
                BitFontSize           = 18;            // HUD font size
                HudCorner             = HudCornerType.BottomLeft;
                SeedPendingBarOnStart = true;          // parity: same as strategy
                RestartOnNewSession   = true;          // parity: string clears each session
                EnableStringLog       = true;
                LogFolder             = @"C:\temp";
                LogBaseName           = "Indicator_Renko_mergedLayer3";
                RollingWindowBits     = 20;            // rolling 1's % over the last N bits (0 = hide)
                IncludeBackFill       = true;          // 2026-10-26: ON by default (the alert reads it)
                AlertPattern          = "1000";
                AlertSoundFile        = "Alert2.wav";
                EnableAlertHours      = true;
                AlertStartHourET      = 9;  AlertStartMinuteET = 30;
                AlertEndHourET        = 16; AlertEndMinuteET   = 0;
                BackFillMaxBricks     = 1000;          // stops earlier at the session start
                ShowLiveDivider       = true;          // '|' in BACKFILL where LIVE begins
                MaximumBarsLookBack   = MaximumBarsLookBack.Infinite;   // back fill may read far back
                Filter2Pattern        = "";            // blank = Layer 1 (F1 only)
                Filter3Pattern        = "";            // blank = Layer 2 when F2 is set
            }
            else if (State == State.Realtime)
            {
                // Build from ENABLE-TIME forward (historical bars are ignored below), exactly
                // like the strategy which only processes State.Realtime.
                if (!lifeStarted)
                {
                    lifeStarted = true;
                    ParseFilter1Patterns();
                    ParseList(Filter2Pattern, filter2Patterns);
                    ParseList(Filter3Pattern, filter3Patterns);
                    ParseList(AlertPattern, pAlert);
                    if (filter2Patterns.Count == 0 && filter3Patterns.Count > 0)
                    {
                        filter3Patterns.Clear();   // F3 works on the F2 string -> nothing to do without F2
                        Print("[Indicator_Renko_mergedLayer3] F2 is blank -> F3 ignored (F3 needs F2).");
                    }
                    Print("[Indicator_Renko_mergedLayer3] F2=[" + string.Join(",", filter2Patterns)
                        + "] F3=[" + string.Join(",", filter3Patterns) + "]  -> Layer "
                        + (filter3Patterns.Count > 0 ? "3" : filter2Patterns.Count > 0 ? "2" : "1"));
                    FreshStart("indicator enabled (fresh start)", SeedPendingBarOnStart);
                    bfNeedReplay = IncludeBackFill;   // back fill runs on the first live brick
                }
            }
        }

        // =====================================================================
        // OnBarUpdate — brick close -> bit -> merged pipeline -> display + log
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;   // ignore historical; build forward from enable
            if (CurrentBar < 1) return;

            // ── NEW SESSION: optional clean roll (no seed across days) ──────────
            if (firstBrickDone && Bars.IsFirstBarOfSession)
            {
                if (RestartOnNewSession)
                {
                    FreshStart("new trading session", false);
                    if (IncludeBackFill) { BfReset(); bfReady = true; bfSince = Time[0]; }   // new session: nothing before it
                }
            }
            // ── HOLE: bricks were skipped (disconnect that MISSED bricks) -> clean restart, NO seed
            else if (lastProcessedBar >= 0 && CurrentBar > lastProcessedBar + 1)
            {
                FreshStart("brick gap: CurrentBar " + lastProcessedBar + " -> " + CurrentBar
                    + " (missed " + (CurrentBar - lastProcessedBar - 1) + " brick(s))", false);
                if (IncludeBackFill) bfNeedReplay = true;   // rebuild BACKFILL from history (keeps the session)
            }
            lastProcessedBar = CurrentBar;
            firstBrickDone   = true;

            // ── BACKFILL: replay this session's closed bricks once (enable / after a gap) ──
            if (IncludeBackFill && bfNeedReplay)
            {
                bfNeedReplay = false;
                BfReplay();
            }
            int bfLen0 = Deepest(bfOutcome, bfChain).Length;   // after any replay: replayed bits never alert
            // ── BACKFILL: this live brick (processed BEFORE live so log rows include it) ──
            if (IncludeBackFill && bfReady)
            {
                int bfBit;
                if (Close[0] > Close[1])      bfBit = 1;
                else if (Close[0] < Close[1]) bfBit = 0;
                else                          bfBit = (bfPrevBit >= 0) ? bfPrevBit : 0;
                BfProcess(bfBit);
            }

            // ── SEED (one time, after a clean enable) ───────────────────────────
            if (seedPending)
            {
                seedPending = false;
                if (CurrentBar >= 2)
                {
                    int seedBit;
                    if (Close[1] > Close[2])      seedBit = 1;   // green
                    else if (Close[1] < Close[2]) seedBit = 0;   // red
                    else                          seedBit = 0;
                    longStr.Append(seedBit == 1 ? "1" : "0");
                    shortStr.Append(seedBit == 1 ? "0" : "1");
                    prevBarBit = seedBit;
                }
            }

            // ── derive the brick bit (green=1 / red=0) from close-vs-close ──────
            int bit;
            if (Close[0] > Close[1])      bit = 1;   // up brick  (green)
            else if (Close[0] < Close[1]) bit = 0;   // down brick (red)
            else                          bit = (prevBarBit >= 0) ? prevBarBit : 0;
            prevBarBit = bit;

            int liveLen0 = Deepest(filter1Outcome, liveChain).Length;

            // ── resolve the prior F1 match into ONE merged outcome bit ──────────
            if (waitLongOutcome)
            {
                waitLongOutcome = false;
                AppendOutcome('L', bit == 1 ? '1' : '0');
            }
            if (waitShortOutcome)
            {
                waitShortOutcome = false;
                AppendOutcome('S', bit == 0 ? '1' : '0');
            }

            // ── append bit to both books ────────────────────────────────────────
            longStr.Append(bit == 1 ? "1" : "0");
            shortStr.Append(bit == 1 ? "0" : "1");
            if (longStr.Length  > 2048) longStr.Remove(0, longStr.Length - 2048);
            if (shortStr.Length > 2048) shortStr.Remove(0, shortStr.Length - 2048);

            // ── test F1 against BOTH books; arm the next-brick outcome ──────────
            if (TailMatchesAny(longStr.ToString()))  waitLongOutcome  = true;
            if (TailMatchesAny(shortStr.ToString())) waitShortOutcome = true;

            // ── LAST-LAYER ALERT (BACKFILL block if on, else LIVE) ──────────────
            string lvl = filter3Patterns.Count > 0 ? "F3" : filter2Patterns.Count > 0 ? "F2" : "F1";
            if (AlertOnBackfill())
            { StringBuilder d = Deepest(bfOutcome, bfChain);        CheckAlert(d.ToString(), d.Length > bfLen0,   "BACKFILL " + lvl); }
            else
            { StringBuilder d = Deepest(filter1Outcome, liveChain); CheckAlert(d.ToString(), d.Length > liveLen0, "LIVE " + lvl); }

            // ── refresh the on-chart string ─────────────────────────────────────
            UpdateDisplay();
        }

        // ── percentages ─────────────────────────────────────────────────────────
        // Session %: 1's so far since the last fresh start / new session.
        private double SessionPct()
        {
            int n = filter1Outcome.Length;
            return n > 0 ? 100.0 * onesCount / n : 0.0;
        }

        // Rolling %: 1's in the most recent N bits (N = RollingWindowBits, capped
        // at the number of bits available so far). Returns -1 when hidden.
        private double RollingPct(out int windowUsed)
        {
            windowUsed = 0;
            if (RollingWindowBits <= 0 || filter1Outcome.Length == 0) return -1;
            int n = filter1Outcome.Length;
            windowUsed = Math.Min(RollingWindowBits, n);
            int ones = 0;
            for (int i = n - windowUsed; i < n; i++) if (filter1Outcome[i] == '1') ones++;
            return 100.0 * ones / windowUsed;
        }

        // ── BACKFILL pipeline ───────────────────────────────────────────────────
        private void BfReset()
        {
            bfLong.Clear(); bfShort.Clear(); bfOutcome.Clear();
            bfOnes = 0; bfPrevBit = -1; bfWaitLong = false; bfWaitShort = false;
            bfReady = false; bfSince = DateTime.MinValue;
            bfChain.Reset();
        }

        // One brick through the BACKFILL pipeline (identical rules to the LIVE pipeline).
        private void BfProcess(int bit)
        {
            bfPrevBit = bit;
            bool x2, x3;
            if (bfWaitLong)  { bfWaitLong  = false; char b = bit == 1 ? '1' : '0'; bfOutcome.Append(b); if (b == '1') bfOnes++; ChainFeed(bfChain, bfOutcome, b, out x2, out x3); }
            if (bfWaitShort) { bfWaitShort = false; char b = bit == 0 ? '1' : '0'; bfOutcome.Append(b); if (b == '1') bfOnes++; ChainFeed(bfChain, bfOutcome, b, out x2, out x3); }
            bfLong.Append(bit == 1 ? "1" : "0");
            bfShort.Append(bit == 1 ? "0" : "1");
            if (bfLong.Length  > 2048) bfLong.Remove(0, bfLong.Length - 2048);
            if (bfShort.Length > 2048) bfShort.Remove(0, bfShort.Length - 2048);
            if (TailMatchesAny(bfLong.ToString()))  bfWaitLong  = true;
            if (TailMatchesAny(bfShort.ToString())) bfWaitShort = true;
        }

        // Replay the already-closed bricks of the CURRENT SESSION (Close[1..k], oldest first).
        // Close[0] is the live brick being processed now; it is fed separately afterwards.
        // Same session gate as the strategies: stop at the session's first brick or after
        // BackFillMaxBricks, whichever comes first.
        private void BfReplay()
        {
            BfReset();
            bfReady = true;
            if (Bars.IsFirstBarOfSession)
            {
                bfSince = Time[0];
                LogBf("# BACKFILL: first live brick opens a new session -> nothing to back-fill.");
                return;
            }
            int maxN = Math.Max(1, BackFillMaxBricks);
            int k = 0;
            string stopReason = "Back-fill max bricks (" + maxN + ") reached";
            for (int i = 1; i <= maxN; i++)
            {
                if (i + 1 > CurrentBar) { stopReason = "no older bricks loaded (increase Days to load)"; break; }
                k = i;
                if (Bars.IsFirstBarOfSessionByIndex(CurrentBar - i)) { stopReason = "session start reached"; break; }
            }
            if (k == 0)
            {
                bfSince = Time[0];
                LogBf("# BACKFILL: no closed bricks available (" + stopReason + ").");
                return;
            }
            for (int i = k; i >= 1; i--)
            {
                int bit;
                if (Close[i] > Close[i + 1])      bit = 1;
                else if (Close[i] < Close[i + 1]) bit = 0;
                else                              bit = (bfPrevBit >= 0) ? bfPrevBit : 0;
                BfProcess(bit);
            }
            bfSince = Time[k];
            int w; double roll = PctRolling(bfOutcome, out w);
            LogBf(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "# BACKFILL: {0} bricks {1:HH:mm:ss} -> {2:HH:mm:ss} (stopped: {3}) | bits={4} 1s={5} = {6:F1}%{7} | {8}",
                k, Time[k], Time[1], stopReason, bfOutcome.Length, bfOnes,
                bfOutcome.Length > 0 ? 100.0 * bfOnes / bfOutcome.Length : 0.0,
                roll >= 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, " last {0}: {1:F1}%", w, roll) : "",
                bfOutcome.ToString()));
        }

        // Rolling 1's % over the last RollingWindowBits bits of any outcome string (-1 = hidden).
        private double PctRolling(StringBuilder s, out int windowUsed)
        {
            windowUsed = 0;
            if (RollingWindowBits <= 0 || s.Length == 0) return -1;
            int n = s.Length;
            windowUsed = Math.Min(RollingWindowBits, n);
            int ones = 0;
            for (int i = n - windowUsed; i < n; i++) if (s[i] == '1') ones++;
            return 100.0 * ones / windowUsed;
        }

        // Write a back-fill note to the CSV log (as a '#' comment line) and the Output window.
        private void LogBf(string line)
        {
            Print("[Indicator_Renko_mergedLayer3] " + line);
            if (!EnableStringLog || activeLogFilePath == null) return;
            try { File.AppendAllText(activeLogFilePath, line + Environment.NewLine); } catch { }
        }

        // The 4 BACKFILL columns for a log row (empty when the option is off).
        private string BfColumns()
        {
            if (!IncludeBackFill || !bfReady) return ",,,";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int w; double roll = PctRolling(bfOutcome, out w);
            double sess = bfOutcome.Length > 0 ? 100.0 * bfOnes / bfOutcome.Length : 0.0;
            return bfOutcome.Length + "," + sess.ToString("F1", ci) + ","
                 + (roll < 0 ? "" : roll.ToString("F1", ci)) + "," + bfOutcome.ToString();
        }

        // The 8 F2/F3 columns added at the END of each log row (empty strings when off).
        private string ChainColumns(bool inF2, bool inF3)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Func<int, StringBuilder, string> pct = (o, sb) => sb.Length > 0 ? (100.0 * o / sb.Length).ToString("F1", ci) : "";
            bool bfOn = IncludeBackFill && bfReady;
            return (inF2 ? "1" : "0") + "," + (inF3 ? "1" : "0") + ","
                 + pct(liveChain.O2, liveChain.F2) + "," + liveChain.F2 + ","
                 + pct(liveChain.O3, liveChain.F3) + "," + liveChain.F3 + ","
                 + (bfOn ? bfChain.F2.ToString() : "") + "," + (bfOn ? bfChain.F3.ToString() : "");
        }

        // One extra HUD line pair for F2 or F3: header + last N bits.
        private string ChainLine(string label, string pattern, StringBuilder s, int ones, string note)
        {
            string head = "(" + label + "=" + pattern + ")  bits=" + s.Length;
            if (s.Length > 0)
            {
                head += "  1s=" + ones + " = " + (100.0 * ones / s.Length).ToString("F1") + "%";
                int w; double r = PctRolling(s, out w);
                if (r >= 0) head += "  |  last " + w + ": " + r.ToString("F1") + "%";
            }
            string full = s.ToString();
            int n = (DisplayLastNBits > 0 && full.Length > DisplayLastNBits) ? DisplayLastNBits : full.Length;
            if (full.Length > n) head += "  (showing last " + n + ")";
            if (!string.IsNullOrEmpty(note)) head += note;
            return head + Environment.NewLine + (full.Length == 0 ? "<none yet>" : full.Substring(full.Length - n));
        }

        // F2 / F3 / armed lines under a block (empty in Layer 1 = F1 only).
        private string ChainBlock(Chain c, string note)
        {
            if (filter2Patterns.Count == 0) return "";
            bool l3 = filter3Patterns.Count > 0;
            var sb = new StringBuilder();
            sb.Append(Environment.NewLine).Append(ChainLine(l3 ? "F2" : "F2 = money", Filter2Pattern, c.F2, c.O2, l3 ? null : note));
            if (l3) sb.Append(Environment.NewLine).Append(ChainLine("F3 = money", Filter3Pattern, c.F3, c.O3, note));
            sb.Append(Environment.NewLine).Append(MoneyNext(c)
                ? ">>> NEXT TRANSIT = MONEY (F2" + (l3 ? "+F3" : "") + " armed)"
                : "    not armed");
            return sb.ToString();
        }

        // ── append one outcome bit to the merged string + log it ───────────────
        private void AppendOutcome(char side, char bit)
        {
            filter1Outcome.Append(bit);
            if (bit == '1') onesCount++;
            bool inF2, inF3;
            ChainFeed(liveChain, filter1Outcome, bit, out inF2, out inF3);

            if (!EnableStringLog || activeLogFilePath == null) return;
            try
            {
                int w;
                double roll = RollingPct(out w);
                string row = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                           + "," + side + "," + bit
                           + "," + SessionPct().ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                           + "," + (roll < 0 ? "" : roll.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                           + "," + filter1Outcome.ToString()
                           + "," + BfColumns()
                           + "," + ChainColumns(inF2, inF3);
                File.AppendAllText(activeLogFilePath, row + Environment.NewLine);
            }
            catch { /* a reader momentarily locking the file must never break the indicator;
                       the next bit re-writes the full string, so nothing is lost. */ }
        }

        // ── draw the running string as a fixed HUD (one object, updated in place) ─
        private void UpdateDisplay()
        {
            string full = filter1Outcome.ToString();
            int n = (DisplayLastNBits > 0 && full.Length > DisplayLastNBits) ? DisplayLastNBits : full.Length;
            string tail = (n == full.Length) ? full : full.Substring(full.Length - n);

            // header: F1, bit count, session 1's %, rolling 1's %
            string header = "(F1=" + Filter1Pattern + ")" + (IncludeBackFill ? "  LIVE    " : "") + "  bits=" + full.Length;
            if (full.Length > 0)
            {
                header += "  1s=" + onesCount + " = " + SessionPct().ToString("F1") + "%";
                int w;
                double roll = RollingPct(out w);
                if (roll >= 0)
                    header += "  |  last " + w + ": " + roll.ToString("F1") + "%";
            }
            if (IncludeBackFill && liveSince != DateTime.MinValue) header += "   since " + liveSince.ToString("HH:mm:ss");
            if (full.Length > n) header += "  (showing last " + n + ")";
            string liveNote = AlertOnBackfill() ? "" : AlarmNote();
            string bfNote   = AlertOnBackfill() ? AlarmNote() : "";
            if (filter2Patterns.Count == 0) header += liveNote;     // Layer 1: alert text on the F1 line

            string body = ((tail.Length == 0) ? "<no transit yet>" : tail) + ChainBlock(liveChain, liveNote);

            // ── second line: BACKFILL (whole session, rebuilt from history + live) ──
            if (IncludeBackFill)
            {
                string bfHeader, bfBody;
                if (!bfReady)
                {
                    bfHeader = "(F1=" + Filter1Pattern + ")  BACKFILL";
                    bfBody   = "<runs on the first live brick>";
                }
                else
                {
                    string bf = bfOutcome.ToString();
                    bfHeader = "(F1=" + Filter1Pattern + ")  BACKFILL  bits=" + bf.Length;
                    if (bf.Length > 0)
                    {
                        bfHeader += "  1s=" + bfOnes + " = " + (100.0 * bfOnes / bf.Length).ToString("F1") + "%";
                        int w2; double roll2 = PctRolling(bfOutcome, out w2);
                        if (roll2 >= 0) bfHeader += "  |  last " + w2 + ": " + roll2.ToString("F1") + "%";
                    }
                    if (bfSince != DateTime.MinValue) bfHeader += "   since " + bfSince.ToString("HH:mm:ss");

                    bool matchesLive = bf.EndsWith(full);
                    if (!matchesLive) bfHeader += "   (!) differs from LIVE";

                    // where LIVE begins inside BACKFILL (only meaningful when BACKFILL ends with LIVE)
                    int divider = (ShowLiveDivider && matchesLive && full.Length > 0 && bf.Length > full.Length)
                                  ? bf.Length - full.Length : -1;
                    int bn = (DisplayLastNBits > 0 && bf.Length > DisplayLastNBits) ? DisplayLastNBits : bf.Length;
                    int start = bf.Length - bn;
                    string shown = bf.Substring(start);
                    if (divider > start) shown = shown.Substring(0, divider - start) + "|" + shown.Substring(divider - start);
                    if (bf.Length > bn) bfHeader += "  (showing last " + bn + ")";
                    if (filter2Patterns.Count == 0) bfHeader += bfNote;   // Layer 1: alert text on the F1 line
                    bfBody = ((shown.Length == 0) ? "<no transit yet>" : shown) + ChainBlock(bfChain, bfNote);
                }
                body += Environment.NewLine + bfHeader + Environment.NewLine + bfBody;
            }

            NinjaTrader.NinjaScript.DrawingTools.TextPosition pos;
            switch (HudCorner)
            {
                case HudCornerType.BottomRight: pos = NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomRight; break;
                case HudCornerType.TopLeft:     pos = NinjaTrader.NinjaScript.DrawingTools.TextPosition.TopLeft;     break;
                case HudCornerType.TopRight:    pos = NinjaTrader.NinjaScript.DrawingTools.TextPosition.TopRight;    break;
                case HudCornerType.Center:      pos = NinjaTrader.NinjaScript.DrawingTools.TextPosition.Center;      break;
                default:                        pos = NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomLeft;  break;
            }

            Draw.TextFixed(this, HUD_TAG + "_" + Filter1Pattern
                + (filter2Patterns.Count > 0 ? "_" + Filter2Pattern + "_" + (filter3Patterns.Count > 0 ? Filter3Pattern : "") : ""), header + Environment.NewLine + body,
                pos,
                Brushes.White, new SimpleFont("Consolas", BitFontSize),
                Brushes.DimGray, Brushes.Black, 50);
        }

        // ── clear pipeline + open a fresh log file ──────────────────────────────
        private void FreshStart(string reason, bool seed)
        {
            seedPending = seed;
            longStr.Clear(); shortStr.Clear(); filter1Outcome.Clear();
            onesCount = 0;                                   // session % restarts with the string
            waitLongOutcome = false; waitShortOutcome = false;
            prevBarBit = -1;
            liveSince = DateTime.Now;
            liveChain.Reset();

            if (EnableStringLog)
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                try
                {
                    if (!Directory.Exists(LogFolder)) Directory.CreateDirectory(LogFolder);
                    string safeF1 = (Filter1Pattern ?? "").Replace("*", "z").Replace("?", "o").Replace(",", "_");
                    string layerTag = filter2Patterns.Count > 0
                        ? "_F2-" + SafeName(Filter2Pattern) + "_F3-" + (filter3Patterns.Count > 0 ? SafeName(Filter3Pattern) : "off")
                        : "";
                    activeLogFilePath = Path.Combine(LogFolder, LogBaseName + "_F1-" + safeF1 + layerTag + "_" + stamp + ".csv");
                    File.AppendAllText(activeLogFilePath,
                        "# Indicator_Renko_mergedLayer3  F1=" + Filter1Pattern
                            + "  F2=" + (filter2Patterns.Count > 0 ? Filter2Pattern : "(off)")
                            + "  F3=" + (filter3Patterns.Count > 0 ? Filter3Pattern
                                         : (!string.IsNullOrWhiteSpace(Filter3Pattern) ? "(ignored: F2 blank)" : "(off)"))
                            + "  reason=" + reason + "  seed=" + seed
                            + "  rollingWindow=" + RollingWindowBits
                            + "  backfill=" + (IncludeBackFill ? ("ON(max " + BackFillMaxBricks + ")") : "OFF") + Environment.NewLine
                        + "timestamp,side,bit,pct_session,pct_rolling,string,bf_bits,bf_pct_session,bf_pct_rolling,bf_string,"
                        + "inF2,inF3,f2_pct,f2_string,f3_pct,f3_string,bf_f2_string,bf_f3_string"
                        + Environment.NewLine);
                }
                catch { activeLogFilePath = null; }
            }
            else activeLogFilePath = null;

            Print("[Indicator_Renko_mergedLayer3] FRESH START: " + reason
                + " | F1=[" + string.Join(",", filter1Patterns) + "] | seed=" + seed
                + " | log=" + (activeLogFilePath ?? "(off)"));
        }

        // =====================================================================
        // F1 parser + wildcard tail matcher  (verbatim port from the strategy)
        // =====================================================================
        private void ParseFilter1Patterns()
        {
            filter1Patterns.Clear();
            if (!string.IsNullOrEmpty(Filter1Pattern))
                foreach (string tok in Filter1Pattern.Split(','))
                {
                    string t = (tok ?? "").Trim();
                    if (t.Length == 0) continue;
                    bool ok = true;
                    foreach (char c in t) if (c != '0' && c != '1' && c != '*' && c != '?') { ok = false; break; }
                    if (ok) filter1Patterns.Add(t);
                }
            Print("[Indicator_Renko_mergedLayer3] F1 active=[" + string.Join(",", filter1Patterns) + "]");
        }

        private static string SafeName(string p)
        { return (p ?? "").Replace("*", "z").Replace("?", "o").Replace(",", "_"); }

        // Generic comma-OR parser for F2 / F3 (same token rules as F1).
        private static void ParseList(string src, List<string> dst)
        {
            dst.Clear();
            if (string.IsNullOrWhiteSpace(src)) return;
            foreach (string tok in src.Split(','))
            {
                string t = (tok ?? "").Trim();
                if (t.Length == 0) continue;
                bool ok = true;
                foreach (char ch in t) if (ch != '0' && ch != '1' && ch != '*' && ch != '?') { ok = false; break; }
                if (ok) dst.Add(t);
            }
        }

        private static bool MatchAny(string text, List<string> pats)
        {
            for (int i = 0; i < pats.Count; i++) if (TailMatches(text, pats[i])) return true;
            return false;
        }

        private bool TailMatchesAny(string text)
        {
            for (int i = 0; i < filter1Patterns.Count; i++)
                if (TailMatches(text, filter1Patterns[i])) return true;
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
        [Display(Name="F1 Pattern (comma OR; ?=1+ ones *=1+ zeros)", Order=1, GroupName="1. Pattern")]
        public string Filter1Pattern { get; set; }

        // Plain properties (NOT [NinjaScriptProperty]) so the generated code region stays unchanged.
        [Display(Name="F2 Pattern (on merged F1 string; leave blank to skip F2 and F3)", Order=2, GroupName="1. Pattern",
            Description="Blank (default) = Layer 1: only the F1 line is shown. F3 is then ignored too.")]
        public string Filter2Pattern { get; set; }

        [Display(Name="F3 Pattern (on F2 string; leave blank to skip; needs F2)", Order=3, GroupName="1. Pattern",
            Description="Blank = Layer 2 (F1 + F2 lines). Only used when F2 has a value.")]
        public string Filter3Pattern { get; set; }

        [NinjaScriptProperty] [Range(1, int.MaxValue)]
        [Display(Name="Display last N bits", Order=1, GroupName="2. Display")]
        public int DisplayLastNBits { get; set; }

        [NinjaScriptProperty] [Range(6, 72)]
        [Display(Name="HUD font size", Order=2, GroupName="2. Display")]
        public int BitFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name="HUD corner", Order=3, GroupName="2. Display")]
        public HudCornerType HudCorner { get; set; }

        // Plain property (NOT [NinjaScriptProperty]) so the generated code region stays unchanged.
        [Range(0, 1000)]
        [Display(Name="Rolling window (bits, 0 = hide)", Order=4, GroupName="2. Display",
            Description="Shows the 1's percentage over the most recent N bits, next to the session %. 0 hides it.")]
        public int RollingWindowBits { get; set; }

        // Plain properties (NOT [NinjaScriptProperty]) so the generated code region stays unchanged.
        [Display(Name="Include back fill", Order=1, GroupName="5. Back fill",
            Description="ON = add a second line (BACKFILL): the F1 outcome string rebuilt from this session's "
                      + "stored bricks (back to the session start), then updated live. Default ON (the alert reads this block).")]
        public bool IncludeBackFill { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name="Back-fill max bricks", Order=2, GroupName="5. Back fill",
            Description="Most bricks to replay; stops earlier at the session start. Default 1000.")]
        public int BackFillMaxBricks { get; set; }

        [Display(Name="Show '|' where LIVE begins", Order=3, GroupName="5. Back fill",
            Description="Marks inside BACKFILL where the LIVE line starts; everything right of it should equal LIVE.")]
        public bool ShowLiveDivider { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Seed forming brick on start (match strategy)", Order=1, GroupName="3. Parity")]
        public bool SeedPendingBarOnStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Restart clean on new session (match strategy)", Order=2, GroupName="3. Parity")]
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

        // ── 6. Alert (plain properties: the generated-code region stays unchanged) ──
        [Display(Name="Last Layer Alert Pattern (leave blank = no alert)", Order=1, GroupName="6. Alert",
            Description="Watches the deepest active string (F3, else F2, else F1). Each new bit whose tail matches -> one sound + "
                      + "'ALARMING NOW' on that line. Same rules as F1/F2/F3 (comma OR, ?=1+ ones, *=1+ zeros). "
                      + "Wildcards can alert very often. Default 1000.")]
        public string AlertPattern { get; set; }

        [Display(Name="Alert sound file (in NinjaTrader 'sounds' folder, or full path; blank = silent)", Order=2, GroupName="6. Alert")]
        public string AlertSoundFile { get; set; }

        [Display(Name="Use alert hours (no sound outside; the HUD text still updates)", Order=3, GroupName="6. Alert")]
        public bool EnableAlertHours { get; set; }

        [Range(0, 23)]
        [Display(Name="Alert start hour (New York time, ET)", Order=4, GroupName="6. Alert",
            Description="Default 9:30 ET = 6:30 AM Pacific.")]
        public int AlertStartHourET { get; set; }

        [Range(0, 59)]
        [Display(Name="Alert start minute (ET)", Order=5, GroupName="6. Alert")]
        public int AlertStartMinuteET { get; set; }

        [Range(0, 23)]
        [Display(Name="Alert end hour (New York time, ET)", Order=6, GroupName="6. Alert",
            Description="Default 16:00 ET = 1:00 PM Pacific.")]
        public int AlertEndHourET { get; set; }

        [Range(0, 59)]
        [Display(Name="Alert end minute (ET)", Order=7, GroupName="6. Alert")]
        public int AlertEndMinuteET { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Indicator_Renko_mergedLayer3[] cacheIndicator_Renko_mergedLayer3;
		public Indicator_Renko_mergedLayer3 Indicator_Renko_mergedLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, HudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return Indicator_Renko_mergedLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicator_Renko_mergedLayer3 Indicator_Renko_mergedLayer3(ISeries<double> input, string filter1Pattern, int displayLastNBits, int bitFontSize, HudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			if (cacheIndicator_Renko_mergedLayer3 != null)
				for (int idx = 0; idx < cacheIndicator_Renko_mergedLayer3.Length; idx++)
					if (cacheIndicator_Renko_mergedLayer3[idx] != null && cacheIndicator_Renko_mergedLayer3[idx].Filter1Pattern == filter1Pattern && cacheIndicator_Renko_mergedLayer3[idx].DisplayLastNBits == displayLastNBits && cacheIndicator_Renko_mergedLayer3[idx].BitFontSize == bitFontSize && cacheIndicator_Renko_mergedLayer3[idx].HudCorner == hudCorner && cacheIndicator_Renko_mergedLayer3[idx].SeedPendingBarOnStart == seedPendingBarOnStart && cacheIndicator_Renko_mergedLayer3[idx].RestartOnNewSession == restartOnNewSession && cacheIndicator_Renko_mergedLayer3[idx].EnableStringLog == enableStringLog && cacheIndicator_Renko_mergedLayer3[idx].LogFolder == logFolder && cacheIndicator_Renko_mergedLayer3[idx].LogBaseName == logBaseName && cacheIndicator_Renko_mergedLayer3[idx].EqualsInput(input))
						return cacheIndicator_Renko_mergedLayer3[idx];
			return CacheIndicator<Indicator_Renko_mergedLayer3>(new Indicator_Renko_mergedLayer3(){ Filter1Pattern = filter1Pattern, DisplayLastNBits = displayLastNBits, BitFontSize = bitFontSize, HudCorner = hudCorner, SeedPendingBarOnStart = seedPendingBarOnStart, RestartOnNewSession = restartOnNewSession, EnableStringLog = enableStringLog, LogFolder = logFolder, LogBaseName = logBaseName }, input, ref cacheIndicator_Renko_mergedLayer3);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Indicator_Renko_mergedLayer3 Indicator_Renko_mergedLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, HudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_mergedLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicators.Indicator_Renko_mergedLayer3 Indicator_Renko_mergedLayer3(ISeries<double> input , string filter1Pattern, int displayLastNBits, int bitFontSize, HudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_mergedLayer3(input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Indicator_Renko_mergedLayer3 Indicator_Renko_mergedLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, HudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_mergedLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicators.Indicator_Renko_mergedLayer3 Indicator_Renko_mergedLayer3(ISeries<double> input , string filter1Pattern, int displayLastNBits, int bitFontSize, HudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_mergedLayer3(input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}
	}
}

#endregion
