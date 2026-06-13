//=============================================================================
// Michelle_AlertStrategy  v1
//=============================================================================
//
// Fires a BEEP alert when TWO independent conditions are BOTH true:
//
// CONDITION 1 — GMMA Price Filter (evaluated on GmmaInterval bar series):
//   Uses Michelle_GMMA indicator.
//   User picks which EMA to compare against (GmmaEmaPeriod, default P7=30).
//   PriceFilter:
//     0 = No Filter       — always passes
//     1 = Price > ALL     — Close must be above ALL of P1..GmmaEmaPeriod EMAs
//     2 = Price < ALL     — Close must be below ALL of P1..GmmaEmaPeriod EMAs
//   EMAs checked: 3,5,8,10,12,15,GmmaEmaPeriod (P1 through chosen EMA).
//
// CONDITION 2 — GoldDip first red bar (evaluated on DipInterval bar series):
//   Uses Michelle_GoldDip indicator.
//   Detects the FIRST red bar of a new dip cluster:
//     red bar  = var5 > var5[1]  AND  var5[0] >= MinRedBarSize
//     first    = var5[1] <= var5[2]  (previous bar was NOT rising)
//     cluster ends when a green bar arrives (var5 < var5[1])
//     → resets and waits for the next first red bar
//   After a first-red-bar fires, the cluster is "active" until green bar resets it.
//   Only ONE beep per cluster regardless of how many red bars follow.
//
// ALERT: PlaySound beep when both conditions true on the same OnBarUpdate tick.
//
//=============================================================================

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

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Michelle_AlertStrategy : Strategy
    {
        // ── secondary bar series indices ─────────────────────────────────────
        private int barsGmma = -1;   // bar series index for GMMA interval
        private int barsDip  = -1;   // bar series index for GoldDip interval

        // ── GoldDip cluster state ────────────────────────────────────────────
        private bool dipClusterActive = false;  // true = inside a red-bar cluster, beep already fired

        // ── cached indicator references ──────────────────────────────────────
        private Indicators.Michelle_GMMA    gmmaInd;
        private Indicators.Michelle_GoldDip dipInd;

        // ── alert sound path ─────────────────────────────────────────────────
        // NT8 built-in alert sound — works without any file on disk
        private const string BEEP_SOUND = "Alert1.wav";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Beep alert: Michelle_GMMA price filter + Michelle_GoldDip first red bar.";
                Name         = "Michelle_AlertStrategy";

                Calculate                    = Calculate.OnBarClose;
                IsOverlay                    = false;
                IsAutoScale                  = false;
                BarsRequiredToTrade          = 35;     // GoldDip needs 34 bars minimum
                IsExitOnSessionCloseStrategy = false;
                TraceOrders                  = false;

                // ── defaults ──────────────────────────────────────────────────
                GmmaInterval    = 1;
                GmmaEmaPeriod   = 30;    // P7 default
                PriceFilter     = 0;     // 0=NoFilter, 1=Above ALL, 2=Below ALL

                DipInterval     = 5;
                MinRedBarSize   = 0.5;   // minimum var5 value to count as visible red bar
            }
            else if (State == State.Configure)
            {
                // AddDataSeries returns void in NT8 — indices are assigned in order:
                // BarsInProgress 0 = primary (chart) series
                // BarsInProgress 1 = first AddDataSeries call  → barsGmma
                // BarsInProgress 2 = second AddDataSeries call → barsDip
                barsGmma = 1;
                barsDip  = 2;

                AddDataSeries(Instrument.FullName, new BarsPeriod
                    { BarsPeriodType = BarsPeriodType.Minute, Value = GmmaInterval },
                    Bars.TradingHours.Name);

                AddDataSeries(Instrument.FullName, new BarsPeriod
                    { BarsPeriodType = BarsPeriodType.Minute, Value = DipInterval },
                    Bars.TradingHours.Name);
            }
            else if (State == State.DataLoaded)
            {
                // Attach indicators to their respective bar series
                // GMMA: all 12 standard periods + chosen anchor; show all so Values[] are computed
                gmmaInd = Michelle_GMMA(BarsArray[barsGmma],
                    3, 5, 8, 10, 12, 15,          // P1-P6
                    30, 35, 40, 45, 50, 60,        // P7-P12
                    GmmaEmaPeriod,                 // P_custom used as the user-chosen anchor
                    true, true, true, true, true, true,   // show P1-P6
                    true, true, true, true, true, true,   // show P7-P12
                    true);                                 // show P_custom

                dipInd = Michelle_GoldDip(BarsArray[barsDip], 10, false);

                dipClusterActive = false;
            }
        }

        protected override void OnBarUpdate()
        {
            // Only act on primary bar series updates to avoid double-firing
            if (BarsInProgress != 0) return;

            // Need enough bars on both secondary series
            if (BarsArray[barsGmma].Count < 35) return;
            if (BarsArray[barsDip].Count  < 35) return;

            // ── CONDITION 1: GMMA Price Filter ───────────────────────────────
            bool gmmaPass = CheckGmmaCondition();

            // ── CONDITION 2: GoldDip first red bar ───────────────────────────
            bool dipPass = CheckDipCondition();

            // ── ALERT ────────────────────────────────────────────────────────
            if (gmmaPass && dipPass)
            {
                Alert("MichelleAlert",
                    Priority.High,
                    "GMMA + GoldDip signal",
                    BEEP_SOUND,
                    10,               // rearm seconds (short — cluster logic handles dedup)
                    System.Windows.Media.Brushes.Yellow,
                    System.Windows.Media.Brushes.Black);
            }
        }

        // =====================================================================
        // CheckGmmaCondition
        // Evaluates the price filter against Michelle_GMMA on GmmaInterval bars.
        //
        // PriceFilter == 0: always true
        // PriceFilter == 1: Close[barsGmma] must be > ALL EMAs from P1 up to GmmaEmaPeriod
        // PriceFilter == 2: Close[barsGmma] must be < ALL EMAs from P1 up to GmmaEmaPeriod
        //
        // Standard GMMA periods: P1=3, P2=5, P3=8, P4=10, P5=12, P6=15,
        //                         P7=30, P8=35, P9=40, P10=45, P11=50, P12=60
        // Plus P_custom = GmmaEmaPeriod (mapped to Values[12])
        //
        // We compare against all plots whose default period <= GmmaEmaPeriod.
        // Values[] indices: 0=P1(3), 1=P2(5), 2=P3(8), 3=P4(10), 4=P5(12),
        //                   5=P6(15), 6=P7(30), 7=P8(35), 8=P9(40), 9=P10(45),
        //                   10=P11(50), 11=P12(60), 12=P_custom(GmmaEmaPeriod)
        // =====================================================================
        private bool CheckGmmaCondition()
        {
            if (PriceFilter == 0) return true;

            // Standard GMMA periods in order
            int[] stdPeriods = new int[] { 3, 5, 8, 10, 12, 15, 30, 35, 40, 45, 50, 60 };

            double closePrice = BarsArray[barsGmma].GetClose(BarsArray[barsGmma].Count - 1);

            bool allPass = true;

            // Check standard GMMA plots (indices 0-11) whose period <= GmmaEmaPeriod
            for (int idx = 0; idx < stdPeriods.Length; idx++)
            {
                if (stdPeriods[idx] > GmmaEmaPeriod) break;

                double emaVal = gmmaInd.Values[idx].GetValueAt(
                    BarsArray[barsGmma].Count - 1);

                if (emaVal == 0) continue; // not yet computed

                if (PriceFilter == 1 && closePrice <= emaVal) { allPass = false; break; }
                if (PriceFilter == 2 && closePrice >= emaVal) { allPass = false; break; }
            }

            // Also check P_custom (index 12) if GmmaEmaPeriod is not already in stdPeriods
            bool alreadyCovered = false;
            foreach (int p in stdPeriods) if (p == GmmaEmaPeriod) { alreadyCovered = true; break; }

            if (allPass && !alreadyCovered)
            {
                double emaCustom = gmmaInd.Values[12].GetValueAt(
                    BarsArray[barsGmma].Count - 1);

                if (emaCustom != 0)
                {
                    if (PriceFilter == 1 && closePrice <= emaCustom) allPass = false;
                    if (PriceFilter == 2 && closePrice >= emaCustom) allPass = false;
                }
            }

            return allPass;
        }

        // =====================================================================
        // CheckDipCondition
        // Evaluates Michelle_GoldDip on DipInterval bars.
        //
        // var5 is exposed as dipInd.Value[0] (plot index 0 = MainForceEntry).
        // We need var5[0] and var5[1] and var5[2].
        //
        // Red bar  : var5[0] > var5[1]  AND  var5[0] >= MinRedBarSize
        // First bar: var5[1] <= var5[2]  (previous was NOT rising = not a red bar)
        //            → i.e. the cluster just started
        // Cluster tracking:
        //   dipClusterActive=false → watching for first red bar → if found, set true + return true
        //   dipClusterActive=true  → cluster ongoing, no more beeps until green bar resets
        //   Green bar (var5[0] < var5[1]) → dipClusterActive=false, reset
        //   No bar  (var5[0] == var5[1] == 0) → stay in current state
        // =====================================================================
        private bool CheckDipCondition()
        {
            int dipCount = BarsArray[barsDip].Count;
            if (dipCount < 3) return false;

            // Get var5 values for current and previous two bars
            double v0 = dipInd.Values[0].GetValueAt(dipCount - 1);  // current
            double v1 = dipInd.Values[0].GetValueAt(dipCount - 2);  // previous
            double v2 = dipInd.Values[0].GetValueAt(dipCount - 3);  // two bars ago

            bool isRedBar  = v0 > v1 && v0 >= MinRedBarSize;
            bool isGreenBar = v0 < v1;
            bool isFirstBar = isRedBar && (v1 <= v2);  // previous was not rising

            // Green bar → end cluster, reset
            if (isGreenBar)
            {
                dipClusterActive = false;
                return false;
            }

            // First red bar of a new cluster → fire
            if (isFirstBar && !dipClusterActive)
            {
                dipClusterActive = true;
                return true;
            }

            // Subsequent red bars in active cluster → no beep
            // No bar → no change
            return false;
        }

        // =====================================================================
        // Properties
        // =====================================================================
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "GMMA bar interval (min)",
            Description = "Bar interval in minutes for GMMA evaluation. Options: 1, 3, 5, 30, 60.",
            Order = 1, GroupName = "GMMA Condition")]
        public int GmmaInterval { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "GMMA EMA period (P7=30 default)",
            Description = "Which EMA period to use as the upper boundary. Price filter checks P1 through this period. Default 30 (P7).",
            Order = 2, GroupName = "GMMA Condition")]
        public int GmmaEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2)]
        [Display(Name = "Price Filter (0=None 1=Above ALL 2=Below ALL)",
            Description = "0=No filter. 1=Price must be above ALL EMAs from P1 to chosen period. 2=Price must be below ALL.",
            Order = 3, GroupName = "GMMA Condition")]
        public int PriceFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "GoldDip bar interval (min)",
            Description = "Bar interval in minutes for GoldDip evaluation. Options: 1, 3, 5, 30, 60.",
            Order = 1, GroupName = "GoldDip Condition")]
        public int DipInterval { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Min red bar size (var5 threshold)",
            Description = "Minimum var5 value for a red bar to be counted. Typical range 0.5-3.0. Default 0.5.",
            Order = 2, GroupName = "GoldDip Condition")]
        public double MinRedBarSize { get; set; }

        #endregion
    }
}
