#region Using declarations
using System;
using System.IO;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// LONG_SHORT_rawString_recorder  v3
//
// PURPOSE:
//   Records raw market slice outcomes (0=loss, 1=win) to a CSV file.
//   Runs fake slices continuously using real bid/ask price levels.
//   No filters, no pipeline, no real orders.
//
// =====================================================================
// WHAT CHANGED IN v3  (the important part)
// =====================================================================
//   PROBLEM v2 had:
//     On every new strategy instance, InitLogFile() did File.WriteAllText,
//     which WIPED the file and started over. NinjaTrader automatically
//     disables + re-enables strategies whenever the data/price connection
//     drops and recovers. Each auto-re-enable created a fresh instance,
//     which WIPED the file. A 3 AM connection blip therefore destroyed
//     days of accumulated recording. (Confirmed in the NT Log: the feed
//     dropped ~3:33 AM, NT auto-disabled then auto-re-enabled the
//     recorder, and the file was overwritten.)
//
//   FIX in v3:
//     1) FILE NAME IS AUTO-COMPUTED FROM THE TRADING DAY.
//        We ask NinjaTrader (via SessionIterator.GetTradingDay) which
//        trading day we are in, and name the file with that date:
//            <base>_<YYYY-MM-DD>.csv
//        The trading-day boundary is defined by the data series'
//        Trading Hours TEMPLATE -- NOT by this code. So you MUST set the
//        chart's Trading Hours template correctly (see reminder below).
//
//     2) RESUME INSTEAD OF WIPE.
//        On startup, if the file for the current trading day ALREADY
//        EXISTS, we OPEN it and RELOAD the cumulative human string from
//        its last line, then keep appending. We only create a fresh file
//        (with header) when the file does NOT exist yet. This means a
//        reconnect / auto-re-enable in the middle of a session simply
//        CONTINUES the same file instead of wiping it.
//
//     3) NEW TRADING DAY -> NEW FILE.
//        When GetTradingDay returns a new date (genuine session rollover),
//        we roll to a new file and reset the human string. Guarded by a
//        stored "last trading day" so a double-fire of the session-start
//        flag rolls the file only once.
//
//   HOW TO FORCE A CLEAN RE-RECORD (no parameter for this on purpose):
//     Just delete or rename today's .csv file on disk, then re-enable.
//     With no file present, the recorder creates a fresh one.
//
// =====================================================================
// !!! REQUIRED SETUP -- TRADING HOURS TEMPLATE !!!
// =====================================================================
//   This recorder follows the Trading Hours template on the chart's data
//   series to decide when one trading day ends and the next begins.
//   For MNQ / CME index futures, set the data series Trading Hours to:
//
//        "CME US Index Futures ETH"
//
//   (ETH = Electronic Trading Hours = the full ~23h session.)
//   With that template the trading day runs from the afternoon reopen
//   (~2-3 PM Pacific) through the next afternoon close, so the recorded
//   bit string is one continuous overnight session per file -- which is
//   what the live Layer strategies need.
//
//   If you instead pick an RTH (regular hours) template, the file will
//   cut at the RTH open/close and you will NOT get the overnight string.
//   Search "CME US Index Futures ETH" in NinjaTrader to understand the
//   exact session times and the daily maintenance break.
//
// SLICE DIRECTION (unchanged from v2):
//   LONG:  entry=bid, stop=entry-StopLoss, target=entry+ProfitTarget
//          bit=1 if ask>=target, bit=0 if bid<=stop
//   SHORT: entry=ask, stop=entry+StopLoss, target=entry-ProfitTarget
//          bit=1 if bid<=target, bit=0 if ask>=stop
//
// CRITICAL: Direction, StopLossPoints, ProfitTargetPoints must match the
//           Layer strategy that loads the file.

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum SliceDirection
    {
        LONG,
        SHORT
    }

    public class LONG_SHORT_rawString_recorder : Strategy
    {
        // ── slice state ───────────────────────────────────────────────────────
        private bool   inSlice          = false;
        private double sliceEntryPrice  = 0.0;
        private double sliceStopPrice   = 0.0;
        private double sliceTargetPrice = 0.0;
        private int    sliceCount       = 0;
        private int    bitCount         = 0;

        // ── cumulative bit string for human reading ───────────────────────────
        private System.Text.StringBuilder bitStringForHuman = new System.Text.StringBuilder();

        // ── trading-day / file tracking ───────────────────────────────────────
        private SessionIterator sessionIter   = null;
        private string          currentTradingDay = null;   // "yyyy-MM-dd" of the file we are writing
        private string          currentFilePath   = null;   // full path of the active file

        // ── timing ────────────────────────────────────────────────────────────
        private DateTime lastCheckTime  = DateTime.MinValue;
        private bool     lifeStarted    = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Raw market slice outcome recorder (v3). "
                            + "Auto-names the log file by TRADING DAY and RESUMES "
                            + "(does not wipe) on reconnect/restart. "
                            + "REQUIRES the data series Trading Hours template to be set "
                            + "(e.g. 'CME US Index Futures ETH') so the trading day "
                            + "is the full overnight session. "
                            + "MUST match Direction/Stop/Profit of the Layer strategy.";
                Name        = "LONG_SHORT_rawString_recorder";

                Calculate                    = Calculate.OnEachTick;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                IsFillLimitOnTouch           = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelClose;
                BarsRequiredToTrade          = 0;
                IsUnmanaged                  = false;

                // ── defaults ──────────────────────────────────────────────────
                Direction            = SliceDirection.LONG;
                StopLossPoints       = 20;
                ProfitTargetPoints   = 20;
                CheckIntervalSeconds = 1;
                // Folder + base name. The trading-day date is appended automatically,
                // e.g.  rawString_LONG_20stop_20profit_2026-06-23.csv
                LogFolder            = @"C:\temp";
                LogBaseName          = "rawString_LONG_20stop_20profit";
            }
            else if (State == State.Configure)
            {
                // SessionIterator needs the bars; create it once bars exist.
            }
            else if (State == State.DataLoaded)
            {
                // Build the SessionIterator from the primary bars series.
                // (DataLoaded is the correct place: BarsArray[0] is available.)
                sessionIter = new SessionIterator(BarsArray[0]);
            }
        }

        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;

            // ── one-time startup for THIS instance ────────────────────────────
            // Runs once per instance. A reconnect/auto-re-enable makes a NEW
            // instance, so this runs again -- but OpenOrResumeFile() will RESUME
            // the existing trading-day file rather than wipe it.
            //
            // IMPORTANT (boundary-safe startup): we do NOT open any file until we
            // can read a VALID trading day from NinjaTrader. At the 3 PM CME halt /
            // right after a reconnect the SessionIterator may not be ready yet and
            // GetTradingDayString() returns null. If we guessed (e.g. calendar date)
            // we could open the WRONG session's file and then never correct. Instead
            // we leave lifeStarted=false and simply return; the next tick retries,
            // and the moment a real trading day is available we open the correct file.
            if (!lifeStarted)
            {
                if (sessionIter == null && BarsArray != null && BarsArray.Length > 0)
                    sessionIter = new SessionIterator(BarsArray[0]);

                string td = GetTradingDayString();
                if (td == null)
                {
                    // Session not knowable yet (halt / feed warming up). Wait; do NOT
                    // guess a file name. Retry on the next tick.
                    return;
                }

                lifeStarted = true;
                sliceCount  = 0;
                bitCount    = 0;
                inSlice     = false;

                OpenOrResumeFile(td);

                Print(Name + " started."
                    + " Direction=" + Direction
                    + " Stop=" + StopLossPoints + "pt"
                    + " Target=" + ProfitTargetPoints + "pt"
                    + " TradingDay=" + currentTradingDay
                    + " File=" + currentFilePath);
            }

            // ── monitor current slice FIRST ───────────────────────────────────
            // While a slice is open we just track it tick-by-tick. If the day
            // boundary arrives mid-slice the slice is ABANDONED (no bit), per design.
            if (inSlice)
            {
                string tdWhileInSlice = GetTradingDayString();
                if (tdWhileInSlice != null && tdWhileInSlice != currentTradingDay
                    && string.Compare(tdWhileInSlice, currentTradingDay, StringComparison.Ordinal) > 0)
                {
                    Print(Name + " day boundary crossed mid-slice -> ABANDON open slice"
                          + " (no bit). " + currentTradingDay + " -> " + tdWhileInSlice);
                    AbandonSlice();
                    OpenOrResumeFile(tdWhileInSlice);   // roll to new day's file
                    // fall through: no longer in a slice; throttle gates a new start
                }
                else
                {
                    CheckSlice();
                    return;
                }
            }

            // ── throttle new slice starts ─────────────────────────────────────
            if ((DateTime.Now - lastCheckTime).TotalSeconds < CheckIntervalSeconds)
                return;
            lastCheckTime = DateTime.Now;

            // ── detect trading-day rollover (once per throttle, NOT in a slice) ──
            // Guarded by stored-date comparison, so the IsFirstBarOfSession
            // double-fire quirk rolls the file at most once.
            //
            // Self-heal: if sessionIter went null (can happen after a reconnect),
            // rebuild it so GetTradingDayString() can answer. If we still can't get
            // a valid day, we skip the rollover this pass (never guess) and try again.
            if (sessionIter == null && BarsArray != null && BarsArray.Length > 0)
                sessionIter = new SessionIterator(BarsArray[0]);

            string nowTradingDay = GetTradingDayString();
            if (nowTradingDay != null && nowTradingDay != currentTradingDay)
            {
                // Never roll BACKWARD to an earlier session (safety against a stale
                // read locking us onto the wrong day). Only roll forward.
                if (string.Compare(nowTradingDay, currentTradingDay, StringComparison.Ordinal) > 0)
                {
                    Print(Name + " trading-day rollover: " + currentTradingDay
                          + " -> " + nowTradingDay);
                    OpenOrResumeFile(nowTradingDay);   // new date -> new file, fresh string
                }
                else
                {
                    Print(Name + " IGNORING backward day change " + currentTradingDay
                          + " -> " + nowTradingDay + " (stale read; keeping current file).");
                }
            }

            StartNextSlice();
        }

        // =====================================================================
        // AbandonSlice — discard an in-progress slice with NO bit recorded.
        //   Used when the trading-day boundary arrives mid-slice: a slice that
        //   cannot resolve before the session close is incomplete data and is
        //   thrown away rather than guessed or misfiled.
        // =====================================================================
        private void AbandonSlice()
        {
            inSlice          = false;
            sliceEntryPrice  = 0.0;
            sliceStopPrice   = 0.0;
            sliceTargetPrice = 0.0;
        }

        // =====================================================================
        // GetTradingDayString
        //   Asks NinjaTrader which trading day we are in, per the data series'
        //   Trading Hours TEMPLATE. Returns "yyyy-MM-dd" or null if unavailable.
        // =====================================================================
        private string GetTradingDayString()
        {
            try
            {
                if (sessionIter == null) return null;
                // GetTradingDay takes local time; returns the exchange trading date.
                DateTime td = sessionIter.GetTradingDay(DateTime.Now);
                return td.ToString("yyyy-MM-dd");
            }
            catch (Exception ex)
            {
                Print("GetTradingDayString error: " + ex.Message);
                return null;
            }
        }

        private string BuildFilePath(string tradingDay)
        {
            string fname = LogBaseName + "_" + tradingDay + ".csv";
            return Path.Combine(LogFolder, fname);
        }

        // =====================================================================
        // OpenOrResumeFile
        //   If the trading-day file exists  -> RESUME (reload human string, append).
        //   If it does NOT exist            -> CREATE fresh (write header).
        //   Either way, sets currentTradingDay + currentFilePath and prepares
        //   bitStringForHuman so the human-string column stays continuous.
        // =====================================================================
        private void OpenOrResumeFile(string tradingDay)
        {
            try
            {
                if (string.IsNullOrEmpty(tradingDay))
                {
                    // SAFETY: callers now guarantee a valid trading day before calling
                    // (see OnBarUpdate startup + rollover). We must NEVER guess a file
                    // name from the calendar date here — at the 3 PM boundary that could
                    // open the WRONG session's file and then never self-correct. If we
                    // somehow got here with no day, do nothing and let the next tick
                    // retry once a real trading day is available.
                    Print(Name + " OpenOrResumeFile called with no trading day -> "
                          + "SKIPPING (will retry when session is known). No file opened.");
                    return;
                }

                currentTradingDay = tradingDay;
                currentFilePath   = BuildFilePath(tradingDay);

                string dir = Path.GetDirectoryName(currentFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(currentFilePath))
                {
                    // RESUME: reload the cumulative human string from the last data line
                    // so the column continues seamlessly across reconnect/restart.
                    string lastHuman = ReadLastHumanString(currentFilePath);
                    bitStringForHuman.Clear();
                    if (!string.IsNullOrEmpty(lastHuman))
                        bitStringForHuman.Append(lastHuman);

                    Print(Name + " RESUMING existing file (" + currentFilePath
                          + ") with " + bitStringForHuman.Length + " prior bits.");
                }
                else
                {
                    // FRESH: new trading day (or first ever) -> write header, empty string.
                    bitStringForHuman.Clear();
                    TimeZoneInfo tz = TimeZoneInfo.Local;
                    TimeSpan off = tz.GetUtcOffset(DateTime.Now);
                    string offStr = (off < TimeSpan.Zero ? "-" : "+")
                                  + Math.Abs(off.Hours).ToString("00") + ":"
                                  + Math.Abs(off.Minutes).ToString("00");
                    File.WriteAllText(currentFilePath,
                        "# LONG_SHORT_rawString_recorder v3\n"
                        + "# trading_day: " + tradingDay + "\n"
                        + "# created_local: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                        + "# created_utc:   " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                        + "# local_timezone: " + tz.Id + " (" + tz.DisplayName + ")\n"
                        + "# local_utc_offset_at_creation: UTC" + offStr + "\n"
                        + "#   NOTE: local timestamps below are in the timezone above.\n"
                        + "#   If you later change your PC timezone, the utc_time column\n"
                        + "#   is the unambiguous reference for WHEN each bit was recorded.\n"
                        + "# direction: "   + Direction + "\n"
                        + "# stop_pts: "    + StopLossPoints + "\n"
                        + "# profit_pts: "  + ProfitTargetPoints + "\n"
                        + "# trading_hours_template: (set on data series; e.g. CME US Index Futures ETH)\n"
                        + "# format: local_time, utc_time, bit, bitStringForHuman\n"
                        + "#   local_time       = system local time when slice resolved\n"
                        + "#   utc_time         = same instant in UTC (never shifts; the anchor)\n"
                        + "#   bit              = outcome of this slice (0=loss 1=win)\n"
                        + "#   bitStringForHuman= cumulative string oldest->newest\n"
                        + "#                      COPY FROM LAST DATA LINE for analysis\n"
                        + "#\n");

                    Print(Name + " CREATED fresh file: " + currentFilePath
                          + " (local tz: " + tz.Id + " UTC" + offStr + ")");
                }
            }
            catch (Exception ex)
            {
                Print("OpenOrResumeFile error: " + ex.Message);
            }
        }

        // =====================================================================
        // ReadLastHumanString
        //   Reads the last non-comment, non-empty data line and returns its
        //   3rd field (the cumulative human string). Returns "" if none.
        //   Lines look like:  "2026-06-23 18:42:01, 1, 110110100111"
        // =====================================================================
        private string ReadLastHumanString(string path)
        {
            try
            {
                string lastData = null;
                // Read all lines; files are bits-per-line so this is small/fast.
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#")) continue;
                    lastData = line;     // keep overwriting; ends on the last data line
                }
                if (lastData == null) return "";

                string[] parts = lastData.Split(',');
                // format: local_time, utc_time, bit, bitStringForHuman  -> human is index 3
                if (parts.Length >= 4)
                    return parts[3].Trim();   // the cumulative human string
                // backward-compat: old 3-column files (timestamp, bit, human)
                if (parts.Length == 3)
                    return parts[2].Trim();
                return "";
            }
            catch (Exception ex)
            {
                Print("ReadLastHumanString error: " + ex.Message);
                return "";
            }
        }

        // =====================================================================
        // StartNextSlice (unchanged logic)
        // =====================================================================
        private void StartNextSlice()
        {
            double refPrice;

            if (Direction == SliceDirection.LONG)
            {
                refPrice = GetCurrentBid();
                if (refPrice <= 0) return;
                sliceEntryPrice  = refPrice;
                sliceStopPrice   = Instrument.MasterInstrument.RoundToTickSize(
                                       sliceEntryPrice - StopLossPoints);
                sliceTargetPrice = Instrument.MasterInstrument.RoundToTickSize(
                                       sliceEntryPrice + ProfitTargetPoints);
            }
            else // SHORT
            {
                refPrice = GetCurrentAsk();
                if (refPrice <= 0) return;
                sliceEntryPrice  = refPrice;
                sliceStopPrice   = Instrument.MasterInstrument.RoundToTickSize(
                                       sliceEntryPrice + StopLossPoints);
                sliceTargetPrice = Instrument.MasterInstrument.RoundToTickSize(
                                       sliceEntryPrice - ProfitTargetPoints);
            }

            sliceCount++;
            inSlice = true;

            Print(string.Format(
                "{0} slice #{1} started: entry={2:F2} stop={3:F2} target={4:F2}",
                Direction, sliceCount, sliceEntryPrice, sliceStopPrice, sliceTargetPrice));
        }

        // =====================================================================
        // CheckSlice (unchanged logic)
        // =====================================================================
        private void CheckSlice()
        {
            try
            {
                double bid = GetCurrentBid();
                double ask = GetCurrentAsk();
                if (bid <= 0 || ask <= 0) return;

                bool stopHit   = false;
                bool targetHit = false;

                if (Direction == SliceDirection.LONG)
                {
                    stopHit   = bid <= sliceStopPrice;
                    targetHit = ask >= sliceTargetPrice;
                }
                else // SHORT
                {
                    stopHit   = ask >= sliceStopPrice;
                    targetHit = bid <= sliceTargetPrice;
                }

                if (!stopHit && !targetHit) return;

                int bit = stopHit ? 0 : 1;

                inSlice          = false;
                sliceEntryPrice  = 0.0;
                sliceStopPrice   = 0.0;
                sliceTargetPrice = 0.0;

                AppendBit(bit);
                bitCount++;

                Print(string.Format(
                    "{0} slice #{1} {2}: bit={3} totalBits={4}",
                    Direction, sliceCount,
                    stopHit ? "STOP" : "TARGET",
                    bit, bitCount));
            }
            catch (Exception ex)
            {
                Print("CheckSlice error: " + ex.Message);
                inSlice = false;
            }
        }

        // =====================================================================
        // AppendBit  (writes to the CURRENT trading-day file)
        // =====================================================================
        private void AppendBit(int bit)
        {
            try
            {
                if (string.IsNullOrEmpty(currentFilePath))
                {
                    // Safety: ensure a file is open (e.g. if startup was odd).
                    OpenOrResumeFile(GetTradingDayString());
                }

                if (string.IsNullOrEmpty(currentFilePath))
                {
                    // Still no file (session not knowable yet). Drop this one bit
                    // rather than write to a wrong/guessed file. A single missing
                    // bit at a boundary is acceptable (gaps are allowed by design).
                    Print(Name + " AppendBit: no file open yet (session unknown) -> "
                          + "bit dropped. Will record once the session file is open.");
                    return;
                }

                bitStringForHuman.Append(bit.ToString());

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                            + ", " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                            + ", " + bit
                            + ", " + bitStringForHuman.ToString()
                            + "\n";
                File.AppendAllText(currentFilePath, line);
            }
            catch (Exception ex)
            {
                Print("AppendBit error: " + ex.Message);
            }
        }

        // =====================================================================
        // Properties
        // =====================================================================
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Direction",
            Description = "LONG or SHORT — must match the Layer strategy that loads this file.",
            Order = 1, GroupName = "1. Slice Settings")]
        public SliceDirection Direction { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Stop Loss (points)",
            Description = "Stop loss in points — must match the Layer strategy.",
            Order = 2, GroupName = "1. Slice Settings")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Profit Target (points)",
            Description = "Profit target in points — must match the Layer strategy.",
            Order = 3, GroupName = "1. Slice Settings")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Check Interval (seconds)",
            Description = "How often to check for a new slice start. Default 1 second.",
            Order = 4, GroupName = "1. Slice Settings")]
        public int CheckIntervalSeconds { get; set; }

        // ---- Reminder shown right in the properties (read-only-ish label) ----
        // This is a real property so it appears in the UI; its DESCRIPTION is the
        // reminder. Set the data series Trading Hours template to ETH.
        [Display(Name = "Template: CME US Index Futures ETH",
            Description = "REQUIRED. This recorder follows the data series' Trading Hours "
                        + "template to decide the trading-day boundary (one file per "
                        + "session). For MNQ set 'CME US Index Futures ETH' so the file "
                        + "covers the full overnight session (~2-3 PM Pacific to next "
                        + "afternoon). Search that template name in NinjaTrader to see "
                        + "exact session times. RTH templates will cut the file short.",
            Order = 1, GroupName = "2. REQUIRED SETUP — read me")]
        [ReadOnly(true)]
        public string TemplateReminder
        {
            get { return "Set data series Trading Hours = CME US Index Futures ETH"; }
            set { /* ignored — reminder only */ }
        }

        [NinjaScriptProperty]
        [Display(Name = "Log Folder",
            Description = "Folder for output files. The trading-day date is appended "
                        + "automatically: <base>_<YYYY-MM-DD>.csv",
            Order = 1, GroupName = "3. Logging")]
        public string LogFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Base Name",
            Description = "Base file name (no extension). The trading-day date and .csv "
                        + "are appended automatically. Name it to reflect settings, e.g. "
                        + "rawString_LONG_20stop_20profit",
            Order = 2, GroupName = "3. Logging")]
        public string LogBaseName { get; set; }

        #endregion
    }
}
