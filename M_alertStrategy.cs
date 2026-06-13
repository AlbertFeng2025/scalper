//=============================================================================
// Michelle_AlertStrategy  v4
//=============================================================================

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Michelle_AlertStrategy : Strategy
    {
        private const int BARS_GMMA = 1;
        private const int BARS_DIP  = 2;

        private bool indicatorsReady  = false;
        private bool dipClusterActive = false;

        private Indicators.Michelle_GMMA    gmmaInd = null;
        private Indicators.Michelle_GoldDip dipInd  = null;

        private static readonly int[] StdPeriods = { 3, 5, 8, 10, 12, 15, 30, 35, 40, 45, 50, 60 };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                  = "Beep alert: Michelle_GMMA price filter + Michelle_GoldDip first red bar.";
                Name                         = "Michelle_AlertStrategy";
                Calculate                    = Calculate.OnBarClose;
                BarsRequiredToTrade          = 40;
                IsExitOnSessionCloseStrategy = false;
                TraceOrders                  = false;
                IsOverlay                    = true;   // MUST be true to draw on price panel
                DrawOnPricePanel             = true;
                IsAutoScale                  = false;

                GmmaInterval  = 1;
                GmmaEmaPeriod = 30;
                PriceFilter   = 0;
                DipInterval   = 5;
                MinRedBarSize = 0.5;
            }
            else if (State == State.Configure)
            {
                // Use plain AddDataSeries overload — no TradingHours ref which can be null
                AddDataSeries(BarsPeriodType.Minute, GmmaInterval);
                AddDataSeries(BarsPeriodType.Minute, DipInterval);
            }
        }

        protected override void OnBarUpdate()
        {
            try
            {
                if (BarsInProgress != 0) return;

                if (CurrentBars[BARS_GMMA] < 40) return;
                if (CurrentBars[BARS_DIP]  < 40) return;

                // Lazy indicator init
                if (!indicatorsReady)
                {
                    gmmaInd = Michelle_GMMA(BarsArray[BARS_GMMA],
                        3, 5, 8, 10, 12, 15, 30, 35, 40, 45, 50, 60,
                        GmmaEmaPeriod,
                        true, true, true, true, true, true,
                        true, true, true, true, true, true,
                        true);

                    dipInd = Michelle_GoldDip(BarsArray[BARS_DIP], 10, false);

                    if (gmmaInd == null || dipInd == null)
                    {
                        Print("Michelle_AlertStrategy: indicator init failed — ensure Michelle_GMMA and Michelle_GoldDip are compiled.");
                        return;
                    }

                    dipClusterActive = false;
                    indicatorsReady  = true;
                    Print("Michelle_AlertStrategy v4: indicators ready.");
                    return;
                }

                bool gmmaPass = CheckGmmaCondition();
                bool dipPass  = CheckDipCondition();

                if (gmmaPass && dipPass)
                {
                    Alert("MichelleAlert",
                        Priority.High,
                        "GMMA + GoldDip signal",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                        10,
                        Brushes.White,
                        Brushes.DarkViolet);

                    // ── mark the bar on the price panel ──────────────────────
                    string tag       = "Alert_" + CurrentBar;
                    string timeStamp = Time[0].ToString("MM/dd HH:mm");
                    double markPrice = High[0] + 8 * TickSize;

                    // Downward triangle above the bar — solid and visible
                    Draw.TriangleDown(this, tag,
                        true,
                        0,
                        markPrice,
                        Brushes.DarkViolet);

                    // Timestamp text just above the triangle
                    Draw.Text(this, tag + "_txt",
                        timeStamp,
                        0,
                        markPrice + 6 * TickSize,
                        Brushes.DarkViolet);

                    Print("Michelle_AlertStrategy: ALERT fired bar=" + CurrentBar
                        + " time=" + timeStamp
                        + " price=" + markPrice.ToString("F2"));
                }
            }
            catch (Exception ex)
            {
                Print("Michelle_AlertStrategy OnBarUpdate error: " + ex.Message);
            }
        }

        private bool CheckGmmaCondition()
        {
            if (PriceFilter == 0) return true;

            double closePrice = Closes[BARS_GMMA][0];

            for (int i = 0; i < StdPeriods.Length; i++)
            {
                if (StdPeriods[i] > GmmaEmaPeriod) break;
                double emaVal = gmmaInd.Values[i][0];
                if (emaVal == 0) continue;
                if (PriceFilter == 1 && closePrice <= emaVal) return false;
                if (PriceFilter == 2 && closePrice >= emaVal) return false;
            }

            // Check P_custom (Values[12]) if GmmaEmaPeriod not in standard list
            bool inStd = false;
            foreach (int p in StdPeriods) if (p == GmmaEmaPeriod) { inStd = true; break; }
            if (!inStd)
            {
                double emaCustom = gmmaInd.Values[12][0];
                if (emaCustom != 0)
                {
                    if (PriceFilter == 1 && closePrice <= emaCustom) return false;
                    if (PriceFilter == 2 && closePrice >= emaCustom) return false;
                }
            }

            return true;
        }

        private bool CheckDipCondition()
        {
            if (CurrentBars[BARS_DIP] < 3) return false;

            double v0 = dipInd.Values[0][0];
            double v1 = dipInd.Values[0][1];
            double v2 = dipInd.Values[0][2];

            bool isRedBar   = v0 > v1 && v0 >= MinRedBarSize;
            bool isGreenBar = v0 < v1;
            bool isFirstBar = isRedBar && (v1 <= v2);

            if (isGreenBar)  { dipClusterActive = false; return false; }

            if (isFirstBar && !dipClusterActive)
            {
                dipClusterActive = true;
                Print("Michelle_AlertStrategy: GoldDip first red bar v0=" + v0.ToString("F4"));
                return true;
            }

            return false;
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "GMMA interval (min)", Order = 1, GroupName = "GMMA Condition",
            Description = "Bar interval in minutes for GMMA. Typical: 1, 3, 5, 30, 60.")]
        public int GmmaInterval { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "GMMA EMA period (default P7=30)", Order = 2, GroupName = "GMMA Condition",
            Description = "Price filter checks all GMMA EMAs from P1(3) up to and including this period.")]
        public int GmmaEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2)]
        [Display(Name = "Price Filter (0=None 1=Above ALL 2=Below ALL)", Order = 3, GroupName = "GMMA Condition",
            Description = "0=No filter. 1=Close above ALL EMAs P1 to chosen period. 2=Close below ALL.")]
        public int PriceFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "GoldDip interval (min)", Order = 1, GroupName = "GoldDip Condition",
            Description = "Bar interval in minutes for GoldDip. Typical: 1, 3, 5, 30, 60.")]
        public int DipInterval { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Min red bar size (var5 threshold)", Order = 2, GroupName = "GoldDip Condition",
            Description = "Minimum var5 value to count as a visible red bar. Start at 0.5, tune upward.")]
        public double MinRedBarSize { get; set; }

        #endregion
    }
}
