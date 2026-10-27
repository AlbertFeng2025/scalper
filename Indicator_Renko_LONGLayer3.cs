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
// It has its OWN name (LongHudCornerType) so it never collides with the enums of the other
// observer indicators — all of them can be installed at the same time, and each can be removed alone.
public enum LongHudCornerType { BottomLeft, BottomRight, TopLeft, TopRight, Center }

// =============================================================================
// Indicator_Renko_LONGLayer3  —  LONG-BOOK TRANSIT OUTCOME STRINGS (observer)
// =============================================================================
// CHANGES 2026-10-26 (b) — LAST-LAYER ALERT
//   * NEW group "6. Alert": "Last Layer Alert Pattern" (default 1000). Watches the deepest
//     active string (F3, else F2, else F1). Each new bit whose tail matches -> ONE sound +
//     one row in NinjaTrader's Alerts window + "<<< ALARMING NOW" on that HUD line. After the
//     next bit the line keeps "last alert hh:mm:ss" so you can read it later.
//   * Uses the BACKFILL block when back fill is on, else LIVE. Replayed history never sounds.
//   * Alert hours (New York time, ET), default 9:30-16:00 ET (= 6:30 AM - 1:00 PM PT).
//     Outside the hours: no sound, but the HUD text still updates.
//   * "Include back fill" now defaults to ON.
//
// CHANGES 2026-10-26
//   * Cleaner screen when layers are switched off:
//       F2 and F3 blank  -> only the F1 line is shown (Layer 1 view, no "armed" line)
//       F3 blank only    -> F1 + F2 lines + the "armed" line (Layer 2 view)
//       all three set    -> F1 + F2 + F3 lines + the "armed" line (Layer 3 view)
//   * F3 needs F2: if F2 is blank, F3 is ignored even if it has a value (noted in the
//     NinjaScript Output window and the log header).
//   * The header line shows which layers are on, e.g. "showing F1, F2".
//   * Settings labels now say "leave blank to skip". Nothing else changed.
//
// CHANGES 2026-10-18
//   * NEW option "Include back fill" (default OFF). When ON, a second block
//     (BACKFILL) shows the F1 / F2 / F3 strings rebuilt from the chart's stored
//     bricks back to the CURRENT SESSION start (max "Back-fill max bricks",
//     default 1000) — the same back fill the strategies do on enable — and then
//     keeps growing with every live brick, next to the LIVE block.
//       * BACKFILL's "NEXT TRANSIT = MONEY" line is what a back-filled strategy
//         would see right now.
//       * F1 line: '|' marks where LIVE begins; everything right of it should equal
//         the LIVE F1 string, otherwise "(!) differs from LIVE" is shown.
//         (F2 / F3 have no divider: LIVE F2/F3 start empty at enable, so they are
//          legitimately different from the full-session BACKFILL F2/F3.)
//       * The back fill runs on the FIRST live brick after enable (same moment and
//         same bricks as the strategies). After a brick gap LIVE restarts, BACKFILL
//         is rebuilt from history.
//       * LOG: one '# BACKFILL:' summary line + extra bf_* columns on every row.
//   * The pipeline is now one small class used twice (LIVE and BACKFILL), so both
//     follow exactly the same rules. LIVE behaviour is unchanged.
//   * New settings are plain properties: the generated-code region is unchanged.
//
// PURPOSE
//   Does NOT trade. Shows the LONG book's transit-outcome strings on the chart,
//   layer by layer, with the 1's percentage for the session and for a rolling
//   window, so you can watch the "health" of the setup through the day.
//
// DEFAULT SETUP
//   F1 = 10    -> a green brick followed by a red dip; chase the next GREEN
//   F2 = 10?   -> on the F1 outcome string: a win, a loss, then one-or-more wins
//   F3 = 00    -> on the F2 outcome string: the last two F2 outcomes both failed
////   Same default patterns as SHORT. Research note from the LONG Layer-3 strategy:
//   this LONG pipeline LOST in the 48-day study -> observation only.
//   Leave F2 and F3 blank to show F1 only; leave only F3 blank for F1 + F2. F3 needs F2.
//
// HOW THE STRINGS ARE BUILT (LONG encoding)
//   Brick bit:  GREEN brick (close rose) = 1 ,  RED brick (close fell) = 0.
//
//   Layer 0  raw string : one bit per brick (green=1).
//   Layer 1  F1 string  : each time the raw tail matches F1, the NEXT brick is
//                         scored: GREEN = '1' (transit won), RED = '0'.
//   Layer 2  F2 string  : each time the F1-string tail matches F2, the NEXT F1
//                         outcome is copied into the F2 string.
//   Layer 3  F3 string  : each time the F2-string tail matches F3, the NEXT F2
//                         outcome is copied into the F3 string.
//                         -> The F3 string = the would-be MONEY trades.
//   "NEXT TRANSIT = MONEY" is shown when both F2 and F3 are armed, i.e. the next
//   F1 transit would be a real trade under this pipeline.
//
// PERCENTAGES (per line)
//   "1s=12 = 40.0%"   -> share of 1's in that string so far this session
//   "last 20: 35.0%"  -> share of 1's in the most recent N bits (rolling window)
//
// SESSION / GAP RESETS, SEED, ONE-BRICK LAG
//   * LIVE builds only from enable-time forward (historical bars ignored),
//   * optional clean restart on a new session (default ON),
//   * clean restart if bricks were skipped (disconnect hole),
//   * optional 1-brick seed with the previously-closed brick on enable (default ON),
//   * an F1 match is scored one brick later (inherent).
//
// LOG (optional CSV, open-append-close). One row per new LIVE F1 outcome:
//   timestamp,bit,inF2,inF3,f1_pct,f1_roll,f2_pct,f2_roll,f3_pct,f3_roll,f1,f2,f3,
//   bf_f1_pct,bf_f1_roll,bf_f2_pct,bf_f2_roll,bf_f3_pct,bf_f3_roll,bf_f1,bf_f2,bf_f3,bf_money
//   (bf_* empty when back fill is off)
//   File: <LogBaseName>_F1-<p>_F2-<p>_F3-<p>_<yyyy-MM-dd_HH-mm-ss>.csv
// =============================================================================
namespace NinjaTrader.NinjaScript.Indicators
{
    public class Indicator_Renko_LONGLayer3 : Indicator
    {
        private const bool IS_LONG = true;   // LONG: green=1  |  SHORT: red=1

        // =====================================================================
        // One F1 -> F2 -> F3 pipeline. Used twice: LIVE and BACKFILL.
        // =====================================================================
        private class Pipe
        {
            public readonly StringBuilder Raw = new StringBuilder();
            public readonly StringBuilder F1  = new StringBuilder();   // transit outcomes
            public readonly StringBuilder F2  = new StringBuilder();   // F1 outcomes after an F2 match
            public readonly StringBuilder F3  = new StringBuilder();   // F2 outcomes after an F3 match (money)
            public int  O1, O2, O3;                // 1's counts
            public int  Prev = -1;                 // previous brick bit (for an equal-close carry)
            public bool Wait, A2, A3;              // F1 match waiting / F2 armed / F3 armed

            public void Reset()
            {
                Raw.Clear(); F1.Clear(); F2.Clear(); F3.Clear();
                O1 = O2 = O3 = 0; Prev = -1; Wait = A2 = A3 = false;
            }

            // Brick bit from two closes in this book's encoding; equal close repeats the last bit.
            public int BitFrom(double close, double prevClose)
            {
                if (close > prevClose) return IS_LONG ? 1 : 0;
                if (close < prevClose) return IS_LONG ? 0 : 1;
                return Prev >= 0 ? Prev : 0;
            }

            // Add a raw bit WITHOUT scoring (the LIVE 1-brick seed).
            public void SeedRaw(int bit) { Raw.Append(bit == 1 ? '1' : '0'); Prev = bit; }

            // One brick. Returns true if an F1 outcome was scored on this brick
            // (b = the outcome, inF2 / inF3 = whether it also went into F2 / F3).
            public bool Process(int bit, List<string> p1, List<string> p2, List<string> p3,
                                out char b, out bool inF2, out bool inF3)
            {
                b = '0'; inF2 = false; inF3 = false;
                bool scored = false;
                Prev = bit;
                if (Wait)
                {
                    Wait = false; scored = true;
                    b = bit == 1 ? '1' : '0';                 // next brick in our direction = win
                    if (p2.Count > 0 && A2)
                    {
                        inF2 = true;
                        if (p3.Count > 0 && A3) { inF3 = true; F3.Append(b); if (b == '1') O3++; }
                        F2.Append(b); if (b == '1') O2++;
                        A3 = p3.Count > 0 && MatchAny(F2.ToString(), p3);
                    }
                    F1.Append(b); if (b == '1') O1++;
                    A2 = p2.Count > 0 && MatchAny(F1.ToString(), p2);
                }
                Raw.Append(bit == 1 ? '1' : '0');
                if (Raw.Length > 2048) Raw.Remove(0, Raw.Length - 2048);
                if (MatchAny(Raw.ToString(), p1)) Wait = true;
                return scored;
            }

            public bool MoneyNext(List<string> p2, List<string> p3)
            { return (p2.Count == 0 || A2) && (p3.Count == 0 || A3) && (p2.Count + p3.Count > 0); }
        }

        private readonly Pipe live = new Pipe();
        private readonly Pipe bf   = new Pipe();

        // ── life / session / gap ──────────────────────────────────────────────
        private bool lifeStarted      = false;
        private bool seedPending      = false;
        private bool firstBrickDone   = false;
        private int  lastProcessedBar = -1;
        private string activeLogFilePath = null;
        private DateTime liveSince = DateTime.MinValue;

        // ── back fill state ───────────────────────────────────────────────────
        private bool     bfNeedReplay = false;
        private bool     bfReady      = false;
        private DateTime bfSince      = DateTime.MinValue;

        // ── parsed patterns (comma = OR) ──────────────────────────────────────
        private readonly List<string> p1 = new List<string>();
        private readonly List<string> p2 = new List<string>();
        private readonly List<string> p3 = new List<string>();

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
        { return "LONG F1=" + Filter1Pattern + (p2.Count > 0 ? " F2=" + Filter2Pattern : "") + (p3.Count > 0 ? " F3=" + Filter3Pattern : ""); }

        private static StringBuilder Deepest(Pipe x, List<string> q2, List<string> q3)
        { return q3.Count > 0 ? x.F3 : q2.Count > 0 ? x.F2 : x.F1; }

        private const string HUD_TAG = "Renko_LONGLayer3_HUD";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Indicator_Renko_LONGLayer3";
                Description = "Observer: LONG-book Renko transit strings (F1 / F2 / F3) with session and "
                            + "rolling 1's percentages, optional session back fill. Does not trade.";

                Calculate        = Calculate.OnBarClose;
                IsOverlay        = false;
                DrawOnPricePanel = false;
                IsAutoScale      = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;   // back fill may read far back

                Filter1Pattern        = "10";
                Filter2Pattern        = "10?";
                Filter3Pattern        = "00";
                DisplayLastNBits      = 60;
                RollingWindowBits     = 20;
                BitFontSize           = 14;
                HudCorner             = LongHudCornerType.BottomLeft;
                SeedPendingBarOnStart = true;
                RestartOnNewSession   = true;
                EnableStringLog       = true;
                LogFolder             = @"C:\temp";
                LogBaseName           = "Indicator_Renko_LONGLayer3";
                IncludeBackFill       = true;    // 2026-10-26: back fill ON by default (the alert reads it)
                AlertPattern          = "1000";
                AlertSoundFile        = "Alert2.wav";
                EnableAlertHours      = true;
                AlertStartHourET      = 9;  AlertStartMinuteET = 30;
                AlertEndHourET        = 16; AlertEndMinuteET   = 0;
                BackFillMaxBricks     = 1000;
                ShowLiveDivider       = true;
            }
            else if (State == State.Realtime)
            {
                if (!lifeStarted)
                {
                    lifeStarted = true;
                    Parse(Filter1Pattern, p1); Parse(Filter2Pattern, p2); Parse(Filter3Pattern, p3);
                    Parse(AlertPattern, pAlert);
                    if (p2.Count == 0 && p3.Count > 0)
                    {
                        p3.Clear();   // F3 works on the F2 string -> without F2 there is nothing for F3
                        Print("[Indicator_Renko_LONGLayer3] F2 is blank -> F3 ignored (F3 needs F2).");
                    }
                    Print("[Indicator_Renko_LONGLayer3] F1=[" + string.Join(",", p1) + "] F2=["
                        + string.Join(",", p2) + "] F3=[" + string.Join(",", p3) + "]");
                    FreshStart("indicator enabled (fresh start)", SeedPendingBarOnStart);
                    bfNeedReplay = IncludeBackFill;   // back fill runs on the first live brick
                }
            }
        }

        // =====================================================================
        // OnBarUpdate — brick close -> (BACKFILL) -> LIVE -> HUD
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;
            if (CurrentBar < 1) return;

            // new session / skipped bricks -> clean restart (no seed)
            if (firstBrickDone && Bars.IsFirstBarOfSession)
            {
                if (RestartOnNewSession)
                {
                    FreshStart("new trading session", false);
                    if (IncludeBackFill) { bf.Reset(); bfReady = true; bfSince = Time[0]; }   // nothing before a new session
                }
            }
            else if (lastProcessedBar >= 0 && CurrentBar > lastProcessedBar + 1)
            {
                FreshStart("brick gap: CurrentBar " + lastProcessedBar + " -> " + CurrentBar
                    + " (missed " + (CurrentBar - lastProcessedBar - 1) + " brick(s))", false);
                if (IncludeBackFill) bfNeedReplay = true;   // rebuild BACKFILL from history (keeps the session)
            }
            lastProcessedBar = CurrentBar;
            firstBrickDone   = true;

            // ── BACKFILL: replay once (enable / after a gap), then this brick ──
            //    (processed BEFORE live so each log row includes the current brick)
            if (IncludeBackFill && bfNeedReplay) { bfNeedReplay = false; BfReplay(); }
            int bfLen0 = Deepest(bf, p2, p3).Length;        // after any replay: replayed bits never alert
            if (IncludeBackFill && bfReady)
            {
                char bb; bool x2, x3;
                bf.Process(bf.BitFrom(Close[0], Close[1]), p1, p2, p3, out bb, out x2, out x3);
            }

            // ── LIVE: one-time seed with the previously-closed brick ──
            if (seedPending)
            {
                seedPending = false;
                if (CurrentBar >= 2) live.SeedRaw(live.BitFrom(Close[1], Close[2]));
            }

            // ── LIVE: this brick ──
            int liveLen0 = Deepest(live, p2, p3).Length;
            char b; bool inF2, inF3;
            if (live.Process(live.BitFrom(Close[0], Close[1]), p1, p2, p3, out b, out inF2, out inF3))
                WriteLog(b, inF2, inF3);

            // ── LAST-LAYER ALERT (BACKFILL block if on, else LIVE) ──
            string lvl = p3.Count > 0 ? "F3" : p2.Count > 0 ? "F2" : "F1";
            if (IncludeBackFill && bfReady)
            { StringBuilder d = Deepest(bf, p2, p3);   CheckAlert(d.ToString(), d.Length > bfLen0,   "BACKFILL " + lvl); }
            else
            { StringBuilder d = Deepest(live, p2, p3); CheckAlert(d.ToString(), d.Length > liveLen0, "LIVE " + lvl); }

            UpdateDisplay();
        }

        // =====================================================================
        // BACKFILL replay: the already-closed bricks of the CURRENT SESSION
        // (Close[1..k], oldest first). Same session gate as the strategies: stop at
        // the session's first brick or after BackFillMaxBricks, whichever is first.
        // =====================================================================
        private void BfReplay()
        {
            bf.Reset();
            bfReady = true;
            if (Bars.IsFirstBarOfSession)
            {
                bfSince = Time[0];
                LogNote("# BACKFILL: first live brick opens a new session -> nothing to back-fill.");
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
                LogNote("# BACKFILL: no closed bricks available (" + stopReason + ").");
                return;
            }
            char bb; bool x2, x3;
            for (int i = k; i >= 1; i--)
                bf.Process(bf.BitFrom(Close[i], Close[i + 1]), p1, p2, p3, out bb, out x2, out x3);
            bfSince = Time[k];
            LogNote(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "# BACKFILL: {0} bricks {1:HH:mm:ss} -> {2:HH:mm:ss} (stopped: {3}) | F1({4})={5} | F2({6})={7} | F3({8})={9} | money next={10}",
                k, Time[k], Time[1], stopReason,
                bf.F1.Length, bf.F1, bf.F2.Length, bf.F2, bf.F3.Length, bf.F3,
                bf.MoneyNext(p2, p3) ? "YES" : "no"));
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

        // One HUD line pair: header + (last N bits). 'divider' = index in s where '|' goes (-1 = none).
        private string Line(string label, string pattern, StringBuilder s, int ones, int divider, string note)
        {
            string head = label + " (" + pattern + ")  bits=" + s.Length;
            if (s.Length > 0)
            {
                head += "  1s=" + ones + " = " + Pct(ones, s.Length).ToString("F1") + "%";
                int w; double r = Rolling(s, out w);
                if (r >= 0) head += "  |  last " + w + ": " + r.ToString("F1") + "%";
            }
            if (!string.IsNullOrEmpty(note)) head += note;
            string full = s.ToString();
            int n = (DisplayLastNBits > 0 && full.Length > DisplayLastNBits) ? DisplayLastNBits : full.Length;
            int start = full.Length - n;
            string tail = full.Length == 0 ? "-" : full.Substring(start);
            if (divider > start && divider < full.Length)
                tail = tail.Substring(0, divider - start) + "|" + tail.Substring(divider - start);
            return head + Environment.NewLine + "   " + tail;
        }

        private string Block(Pipe p, int f1Divider, string f1Note)
        {
            // the alert text goes on the deepest line of the block the alert watches
            bool src = (IncludeBackFill && bfReady) ? ReferenceEquals(p, bf) : ReferenceEquals(p, live);
            string an = src ? AlarmNote() : "";
            var sb = new StringBuilder();
            sb.Append(Line("F1", Filter1Pattern, p.F1, p.O1, f1Divider, (f1Note ?? "") + (p2.Count == 0 ? an : "")));
            if (p2.Count > 0) sb.Append(Environment.NewLine).Append(Line(p3.Count > 0 ? "F2" : "F2 = money", Filter2Pattern, p.F2, p.O2, -1, p3.Count == 0 ? an : null));
            if (p3.Count > 0) sb.Append(Environment.NewLine).Append(Line("F3 = money", Filter3Pattern, p.F3, p.O3, -1, an));
            // the "armed" line only makes sense when F2 (and maybe F3) is on; Layer 1 view = F1 line only
            if (p2.Count > 0)
                sb.Append(Environment.NewLine).Append(p.MoneyNext(p2, p3)
                    ? ">>> NEXT TRANSIT = MONEY (F2" + (p3.Count > 0 ? "+F3" : "") + " armed)"
                    : "    not armed");
            return sb.ToString();
        }

        // ── HUD ───────────────────────────────────────────────────────────────────
        private void UpdateDisplay()
        {
            var sb = new StringBuilder();
            sb.Append("LONG book  (green=1)   showing " + (p3.Count > 0 ? "F1, F2, F3" : p2.Count > 0 ? "F1, F2" : "F1 only"));
            if (IncludeBackFill) sb.Append("   LIVE since " + liveSince.ToString("HH:mm:ss"));
            sb.Append(Environment.NewLine).Append(Block(live, -1, null));

            if (IncludeBackFill)
            {
                sb.Append(Environment.NewLine).Append(Environment.NewLine);
                if (!bfReady)
                    sb.Append("BACKFILL  <runs on the first live brick>");
                else
                {
                    sb.Append("BACKFILL  (this session since " + bfSince.ToString("HH:mm:ss")
                        + "  = what a back-filled strategy sees)").Append(Environment.NewLine);
                    string lf1 = live.F1.ToString(), bf1 = bf.F1.ToString();
                    bool same = bf1.EndsWith(lf1);
                    int div = (ShowLiveDivider && same && lf1.Length > 0 && bf1.Length > lf1.Length)
                              ? bf1.Length - lf1.Length : -1;
                    sb.Append(Block(bf, div, same ? null : "   (!) differs from LIVE"));
                }
            }

            LongHudCornerType c = HudCorner;
            NinjaTrader.NinjaScript.DrawingTools.TextPosition pos =
                c == LongHudCornerType.BottomRight ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomRight :
                c == LongHudCornerType.TopLeft     ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.TopLeft :
                c == LongHudCornerType.TopRight    ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.TopRight :
                c == LongHudCornerType.Center      ? NinjaTrader.NinjaScript.DrawingTools.TextPosition.Center :
                                                      NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomLeft;

            Draw.TextFixed(this, HUD_TAG + "_" + Filter1Pattern + "_" + Filter2Pattern + "_" + Filter3Pattern,
                sb.ToString(), pos, Brushes.White, new SimpleFont("Consolas", BitFontSize),
                Brushes.DimGray, Brushes.Black, 50);
        }

        // ── log ───────────────────────────────────────────────────────────────────
        private string Cols(Pipe p)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int w;
            double r1 = Rolling(p.F1, out w), r2 = Rolling(p.F2, out w), r3 = Rolling(p.F3, out w);
            Func<double, string> f = v => v < 0 ? "" : v.ToString("F1", ci);
            return Pct(p.O1, p.F1.Length).ToString("F1", ci) + "," + f(r1) + ","
                 + (p.F2.Length > 0 ? Pct(p.O2, p.F2.Length).ToString("F1", ci) : "") + "," + f(r2) + ","
                 + (p.F3.Length > 0 ? Pct(p.O3, p.F3.Length).ToString("F1", ci) : "") + "," + f(r3) + ","
                 + p.F1 + "," + p.F2 + "," + p.F3;
        }

        private void WriteLog(char b, bool inF2, bool inF3)
        {
            if (!EnableStringLog || activeLogFilePath == null) return;
            try
            {
                string row = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "," + b + ","
                    + (inF2 ? "1" : "0") + "," + (inF3 ? "1" : "0") + "," + Cols(live) + ","
                    + ((IncludeBackFill && bfReady) ? Cols(bf) + "," + (bf.MoneyNext(p2, p3) ? "1" : "0")
                                                    : ",,,,,,,,,");
                File.AppendAllText(activeLogFilePath, row + Environment.NewLine);
            }
            catch { /* never let a locked file break the indicator */ }
        }

        private void LogNote(string line)
        {
            Print("[Indicator_Renko_LONGLayer3] " + line);
            if (!EnableStringLog || activeLogFilePath == null) return;
            try { File.AppendAllText(activeLogFilePath, line + Environment.NewLine); } catch { }
        }

        // ── fresh start (LIVE) ────────────────────────────────────────────────────
        private static string Safe(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "off";
            return p.Replace("*", "z").Replace("?", "o").Replace(",", "_");
        }

        private void FreshStart(string reason, bool seed)
        {
            seedPending = seed;
            live.Reset();
            liveSince = DateTime.Now;

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
                        "# Indicator_Renko_LONGLayer3  F1=" + Filter1Pattern + "  F2=" + Filter2Pattern
                            + "  F3=" + (p2.Count == 0 && !string.IsNullOrWhiteSpace(Filter3Pattern) ? Filter3Pattern + "(ignored: F2 blank)" : Filter3Pattern) + "  reason=" + reason + "  seed=" + seed
                            + "  rollingWindow=" + RollingWindowBits
                            + "  backfill=" + (IncludeBackFill ? ("ON(max " + BackFillMaxBricks + ")") : "OFF")
                            + Environment.NewLine
                        + "timestamp,bit,inF2,inF3,f1_pct,f1_roll,f2_pct,f2_roll,f3_pct,f3_roll,f1,f2,f3,"
                        + "bf_f1_pct,bf_f1_roll,bf_f2_pct,bf_f2_roll,bf_f3_pct,bf_f3_roll,bf_f1,bf_f2,bf_f3,bf_money"
                            + Environment.NewLine);
                }
                catch { activeLogFilePath = null; }
            }
            else activeLogFilePath = null;

            Print("[Indicator_Renko_LONGLayer3] FRESH START: " + reason + " | seed=" + seed
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

        [Display(Name="F2 Pattern (on F1 string; leave blank to skip F2 and F3)", Order=2, GroupName="1. Patterns",
            Description="Blank = Layer 1 view: only the F1 line is shown. F3 is then ignored too.")]
        public string Filter2Pattern { get; set; }

        [Display(Name="F3 Pattern (on F2 string; leave blank to skip; needs F2)", Order=3, GroupName="1. Patterns",
            Description="Blank = Layer 2 view: F1 + F2 lines. Only used when F2 has a value.")]
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
        public LongHudCornerType HudCorner { get; set; }

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

        // Plain properties (NOT [NinjaScriptProperty]) so the generated code region stays unchanged.
        [Display(Name="Include back fill", Order=1, GroupName="5. Back fill",
            Description="ON = add a BACKFILL block: F1/F2/F3 rebuilt from this session's stored bricks "
                      + "(back to the session start), then updated live. Default ON (the alert reads this block).")]
        public bool IncludeBackFill { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name="Back-fill max bricks", Order=2, GroupName="5. Back fill",
            Description="Most bricks to replay; stops earlier at the session start. Default 1000.")]
        public int BackFillMaxBricks { get; set; }

        [Display(Name="Show '|' where LIVE begins (F1 line)", Order=3, GroupName="5. Back fill")]
        public bool ShowLiveDivider { get; set; }

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
		private Indicator_Renko_LONGLayer3[] cacheIndicator_Renko_LONGLayer3;
		public Indicator_Renko_LONGLayer3 Indicator_Renko_LONGLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, LongHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return Indicator_Renko_LONGLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicator_Renko_LONGLayer3 Indicator_Renko_LONGLayer3(ISeries<double> input, string filter1Pattern, int displayLastNBits, int bitFontSize, LongHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			if (cacheIndicator_Renko_LONGLayer3 != null)
				for (int idx = 0; idx < cacheIndicator_Renko_LONGLayer3.Length; idx++)
					if (cacheIndicator_Renko_LONGLayer3[idx] != null && cacheIndicator_Renko_LONGLayer3[idx].Filter1Pattern == filter1Pattern && cacheIndicator_Renko_LONGLayer3[idx].DisplayLastNBits == displayLastNBits && cacheIndicator_Renko_LONGLayer3[idx].BitFontSize == bitFontSize && cacheIndicator_Renko_LONGLayer3[idx].HudCorner == hudCorner && cacheIndicator_Renko_LONGLayer3[idx].SeedPendingBarOnStart == seedPendingBarOnStart && cacheIndicator_Renko_LONGLayer3[idx].RestartOnNewSession == restartOnNewSession && cacheIndicator_Renko_LONGLayer3[idx].EnableStringLog == enableStringLog && cacheIndicator_Renko_LONGLayer3[idx].LogFolder == logFolder && cacheIndicator_Renko_LONGLayer3[idx].LogBaseName == logBaseName && cacheIndicator_Renko_LONGLayer3[idx].EqualsInput(input))
						return cacheIndicator_Renko_LONGLayer3[idx];
			return CacheIndicator<Indicator_Renko_LONGLayer3>(new Indicator_Renko_LONGLayer3(){ Filter1Pattern = filter1Pattern, DisplayLastNBits = displayLastNBits, BitFontSize = bitFontSize, HudCorner = hudCorner, SeedPendingBarOnStart = seedPendingBarOnStart, RestartOnNewSession = restartOnNewSession, EnableStringLog = enableStringLog, LogFolder = logFolder, LogBaseName = logBaseName }, input, ref cacheIndicator_Renko_LONGLayer3);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Indicator_Renko_LONGLayer3 Indicator_Renko_LONGLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, LongHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_LONGLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicators.Indicator_Renko_LONGLayer3 Indicator_Renko_LONGLayer3(ISeries<double> input , string filter1Pattern, int displayLastNBits, int bitFontSize, LongHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_LONGLayer3(input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Indicator_Renko_LONGLayer3 Indicator_Renko_LONGLayer3(string filter1Pattern, int displayLastNBits, int bitFontSize, LongHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_LONGLayer3(Input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}

		public Indicators.Indicator_Renko_LONGLayer3 Indicator_Renko_LONGLayer3(ISeries<double> input , string filter1Pattern, int displayLastNBits, int bitFontSize, LongHudCornerType hudCorner, bool seedPendingBarOnStart, bool restartOnNewSession, bool enableStringLog, string logFolder, string logBaseName)
		{
			return indicator.Indicator_Renko_LONGLayer3(input, filter1Pattern, displayLastNBits, bitFontSize, hudCorner, seedPendingBarOnStart, restartOnNewSession, enableStringLog, logFolder, logBaseName);
		}
	}
}

#endregion
