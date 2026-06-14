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

// LONG_SHORT_rawString_recorder  v1
//
// PURPOSE:
//   Records raw market slice outcomes (0=loss, 1=win) to a CSV file.
//   Runs fake slices continuously using real bid/ask price levels.
//   No filters, no pipeline, no real orders.
//   Output file is loaded by scalper_LONGrepeat_Layer2/3 or
//   scalper_SHORTrepeat_Layer2/3 via StartMode=1 (Load from file).
//
// SLICE DIRECTION:
//   LONG:  entry=bid, stop=entry-StopLoss, target=entry+ProfitTarget
//          bit=1 if ask>=target (price UP), bit=0 if bid<=stop (price DOWN)
//   SHORT: entry=ask, stop=entry+StopLoss, target=entry-ProfitTarget
//          bit=1 if bid<=target (price DOWN), bit=0 if ask>=stop (price UP)
//
// CRITICAL — MUST MATCH PART B:
//   Direction, StopLossPoints, ProfitTargetPoints must be identical
//   between this recorder and the Layer strategy that loads the file.
//   File name encodes these settings for visual verification.
//
// FILE BEHAVIOR:
//   On enable  → overwrites file (fresh session)
//   Each slice → appends: "timestamp, bit"
//   On disable → file stays as-is
//
// NOTE FOR HISTORICAL RESEARCH:
//   To build a long continuous raw string across multiple days/weeks:
//     1. Set IsExitOnSessionCloseStrategy = false (already default)
//     2. Never disable this strategy manually
//     3. Do NOT use EOD session close in NT8 data series settings
//     4. File will grow continuously across sessions indefinitely
//     5. Part B replays the entire accumulated string on each enable
//        Performance note: even 100,000 bits replays near-instantly in C#
//   WARNING: Very old data (weeks/months) may represent stale market regimes.
//            For active trading, recommended to reset weekly by re-enabling.

namespace NinjaTrader.NinjaScript.Strategies
{
    // ── Direction enum ────────────────────────────────────────────────────────
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
        private int    bitCount         = 0;   // total bits recorded

        // ── timing ────────────────────────────────────────────────────────────
        private DateTime lastCheckTime  = DateTime.MinValue;
        private bool     lifeStarted    = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Raw market slice outcome recorder. "
                            + "Records 0/1 bits to CSV for loading by Layer2/3 strategies. "
                            + "MUST match Direction, StopLossPoints, ProfitTargetPoints "
                            + "of the Layer strategy that loads this file.";
                Name        = "LONG_SHORT_rawString_recorder";

                Calculate                    = Calculate.OnEachTick;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;  // runs overnight by default
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
                LogFilePath          = @"C:\temp\rawString_LONG_20stop_20profit.csv";
            }
        }

        protected override void OnBarUpdate()
        {
            if (State != State.Realtime) return;

            // ── one-time startup ──────────────────────────────────────────────
            if (!lifeStarted)
            {
                lifeStarted    = true;
                sliceCount     = 0;
                bitCount       = 0;
                inSlice        = false;
                InitLogFile();
                Print(Name + " started."
                    + " Direction=" + Direction
                    + " Stop=" + StopLossPoints + "pt"
                    + " Target=" + ProfitTargetPoints + "pt"
                    + " Log=" + LogFilePath);
            }

            // ── monitor current slice ─────────────────────────────────────────
            if (inSlice)
            {
                CheckSlice();
                return;
            }

            // ── throttle new slice starts ─────────────────────────────────────
            if ((DateTime.Now - lastCheckTime).TotalSeconds < CheckIntervalSeconds)
                return;
            lastCheckTime = DateTime.Now;

            StartNextSlice();
        }

        // =====================================================================
        // StartNextSlice
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
        // CheckSlice — tick by tick resolution
        // LONG:  stop  = bid <= sliceStopPrice   → bit=0
        //        target = ask >= sliceTargetPrice → bit=1
        // SHORT: stop  = ask >= sliceStopPrice   → bit=0
        //        target = bid <= sliceTargetPrice → bit=1
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

                // reset slice state
                inSlice          = false;
                sliceEntryPrice  = 0.0;
                sliceStopPrice   = 0.0;
                sliceTargetPrice = 0.0;

                // record bit to file
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
        // Logging
        // =====================================================================
        private void InitLogFile()
        {
            try
            {
                string dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Overwrite file — fresh session
                // (See historical research note in header for continuous mode)
                File.WriteAllText(LogFilePath,
                    "# LONG_SHORT_rawString_recorder v1\n"
                    + "# enable_time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                    + "# direction: "   + Direction + "\n"
                    + "# stop_pts: "    + StopLossPoints + "\n"
                    + "# profit_pts: "  + ProfitTargetPoints + "\n"
                    + "# format: timestamp, bit\n"
                    + "#\n");
            }
            catch (Exception ex)
            {
                Print("InitLogFile error: " + ex.Message);
            }
        }

        private void AppendBit(int bit)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                            + ", " + bit + "\n";
                File.AppendAllText(LogFilePath, line);
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
            Order = 1, GroupName = "Slice Settings")]
        public SliceDirection Direction { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Stop Loss (points)",
            Description = "Stop loss in points — must match the Layer strategy that loads this file.",
            Order = 2, GroupName = "Slice Settings")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Profit Target (points)",
            Description = "Profit target in points — must match the Layer strategy that loads this file.",
            Order = 3, GroupName = "Slice Settings")]
        public double ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Check Interval (seconds)",
            Description = "How often to check for new slice start. Default 1 second.",
            Order = 4, GroupName = "Slice Settings")]
        public int CheckIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log File Path",
            Description = "Output file path. Name it to reflect settings e.g. rawString_LONG_20stop_20profit.csv",
            Order = 1, GroupName = "Logging")]
        public string LogFilePath { get; set; }

        #endregion
    }
}
