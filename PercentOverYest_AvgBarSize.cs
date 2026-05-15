#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class PercentOverYest_AvgBarSize : Indicator
    {
        private PriorDayOHLC priorDayOHLC;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "Percent over Yesterday Close + Average Bar Range (High - Low)";
                Name                                        = "PercentOverYest_AvgBarSize";
                Calculate                                   = Calculate.OnEachTick;
                IsOverlay                                   = false;
                DisplayInDataBox                            = true;
                DrawOnPricePanel                            = false;
                DrawHorizontalGridLines                     = true;
                DrawVerticalGridLines                       = true;
                ScaleJustification                          = ScaleJustification.Right;
                IsSuspendedWhileInactive                    = true;

                AvgPeriod = 10;

                AddPlot(Brushes.Lime,       "PctOverYestClose");
                AddPlot(Brushes.Orange,     "AvgBarRange");
            }
            else if (State == State.Configure)
            {
                priorDayOHLC = PriorDayOHLC();
            }
        }

        
		protected override void OnBarUpdate()
		{
		    // 1. Safety check for sufficient data
		    if (CurrentBar < AvgPeriod || priorDayOHLC == null || priorDayOHLC.PriorClose.Count == 0)
		        return;
		
		    // 2. Percent over Yesterday's Close calculation
		    double yestClose = priorDayOHLC.PriorClose[0];
		    PctOverYestClose[0] = yestClose > 0 ? ((Close[0] - yestClose) / yestClose) * 100 : 0;
		
		    // 3. Average Bar Range (High - Low) calculation
		    double sum = 0;
		    for (int i = 0; i < AvgPeriod; i++)
		    {
		        sum += High[i] - Low[i];
		    }
		    AvgBarRange[0] = sum / AvgPeriod;
		
		    // 4. Drawing Labels (Only on the last bar to save resources)
		    if (IsFirstTickOfBar && State == State.Realtime)
		    {
		        // We use the simplest overload: (Owner, Tag, Text, BarsAgo, Y-Value, Brush)
		        
		        Draw.Text(this, "PctLabel", 
		            "Pct: " + PctOverYestClose[0].ToString("0.00") + "%", 
		            0, PctOverYestClose[0], Brushes.Lime);
		
		        Draw.Text(this, "AvgLabel", 
		            "AvgRange: " + AvgBarRange[0].ToString("0.0000"), 
		            0, AvgBarRange[0], Brushes.Orange);
		    }
		}

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Avg Period (bars)", GroupName = "Parameters", Order = 1)]
        public int AvgPeriod { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> PctOverYestClose
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> AvgBarRange
        {
            get { return Values[1]; }
        }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private PercentOverYest_AvgBarSize[] cachePercentOverYest_AvgBarSize;
		public PercentOverYest_AvgBarSize PercentOverYest_AvgBarSize(int avgPeriod)
		{
			return PercentOverYest_AvgBarSize(Input, avgPeriod);
		}

		public PercentOverYest_AvgBarSize PercentOverYest_AvgBarSize(ISeries<double> input, int avgPeriod)
		{
			if (cachePercentOverYest_AvgBarSize != null)
				for (int idx = 0; idx < cachePercentOverYest_AvgBarSize.Length; idx++)
					if (cachePercentOverYest_AvgBarSize[idx] != null && cachePercentOverYest_AvgBarSize[idx].AvgPeriod == avgPeriod && cachePercentOverYest_AvgBarSize[idx].EqualsInput(input))
						return cachePercentOverYest_AvgBarSize[idx];
			return CacheIndicator<PercentOverYest_AvgBarSize>(new PercentOverYest_AvgBarSize(){ AvgPeriod = avgPeriod }, input, ref cachePercentOverYest_AvgBarSize);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.PercentOverYest_AvgBarSize PercentOverYest_AvgBarSize(int avgPeriod)
		{
			return indicator.PercentOverYest_AvgBarSize(Input, avgPeriod);
		}

		public Indicators.PercentOverYest_AvgBarSize PercentOverYest_AvgBarSize(ISeries<double> input , int avgPeriod)
		{
			return indicator.PercentOverYest_AvgBarSize(input, avgPeriod);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.PercentOverYest_AvgBarSize PercentOverYest_AvgBarSize(int avgPeriod)
		{
			return indicator.PercentOverYest_AvgBarSize(Input, avgPeriod);
		}

		public Indicators.PercentOverYest_AvgBarSize PercentOverYest_AvgBarSize(ISeries<double> input , int avgPeriod)
		{
			return indicator.PercentOverYest_AvgBarSize(input, avgPeriod);
		}
	}
}

#endregion
