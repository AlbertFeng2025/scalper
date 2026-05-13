#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class Michelle_Golddip : Indicator
	{
		private Series<double> var2NumSeries;
		private Series<double> var2DenSeries;
		private Series<double> var3Series;
		private Series<double> var5Series;
		private Series<double> condValueSeries;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Detects institutional bottom-fishing entries.";
				Name										= "Michelle_Golddip";
				Calculate									= Calculate.OnBarClose;
				IsOverlay									= false; 
				DisplayInDataBox							= true;
				DrawOnPricePanel							= true; 
				
				// Default parameter values
				RedBarThickness								= 5;
				ShowWashout									= false;
				
				AddPlot(new Stroke(Brushes.Red, 3), PlotStyle.Bar, "MainForceEntry");
				AddPlot(new Stroke(Brushes.Green, 3), PlotStyle.Bar, "Washout");
			}
			else if (State == State.Configure)
			{
				// Apply user-selected thickness to both plots (same value for visual consistency)
				int width = Math.Max(1, Math.Min(15, RedBarThickness));
				Plots[0].Width = width;
				Plots[1].Width = width;
			}
			else if (State == State.DataLoaded)
			{
				var2NumSeries = new Series<double>(this);
				var2DenSeries = new Series<double>(this);
				var3Series = new Series<double>(this);
				var5Series = new Series<double>(this);
				condValueSeries = new Series<double>(this);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 34) return;

			double var1 = (Low[1] + Open[1] + Close[1] + High[1]) / 4;
			var2NumSeries[0] = Math.Abs(Low[0] - var1);
			var2DenSeries[0] = Math.Max(Low[0] - var1, 0);

			double var2NumEMA = EMA(var2NumSeries, 25)[0];
			double var2DenEMA = EMA(var2DenSeries, 19)[0];
			
			double var2 = (var2DenEMA != 0) ? (var2NumEMA / var2DenEMA) : 0;
			var3Series[0] = var2;

			double var3 = EMA(var3Series, 10)[0];
			double var4 = MIN(Low, 33)[0];

			condValueSeries[0] = (Low[0] <= var4) ? var3 : 0;
			var5Series[0] = EMA(condValueSeries, 3)[0];

			// Main Force Entry logic (red bars)
			if (var5Series[0] > var5Series[1])
			{
				Value[0] = var5Series[0];
				
				// Try a broader trigger: If current is red and previous was NOT red
				if (var5Series[1] <= var5Series[2] || var5Series[1] == 0) 
				{
					// 1. Draw a Triangle below the candle
					Draw.TriangleUp(this, "Triangle" + CurrentBar, true, 0, Low[0] - (TickSize * 30), Brushes.Lime);
					
					// 2. Draw Text as a backup so we can see if it's triggering at all
					Draw.Text(this, "Text" + CurrentBar, "ENTRY", 0, Low[0] - (TickSize * 60), Brushes.White);
				}
			}
			else if (var5Series[0] < var5Series[1])
			{
				// Washout (green bars) — only drawn when user enables ShowWashout
				if (ShowWashout)
				{
					Values[1][0] = var5Series[0];
				}
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, 15)]
		[Display(Name = "Red Bar Thickness", Description = "Thickness of the red (and green, if shown) bars. Range 1-15, default 5.", Order = 1, GroupName = "Parameters")]
		public int RedBarThickness
		{ get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Washout (green bars)", Description = "Show the green washout bars. Default OFF — beginners typically don't need these.", Order = 2, GroupName = "Parameters")]
		public bool ShowWashout
		{ get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Michelle_Golddip[] cacheMichelle_Golddip;
		public Michelle_Golddip Michelle_Golddip(int redBarThickness, bool showWashout)
		{
			return Michelle_Golddip(Input, redBarThickness, showWashout);
		}

		public Michelle_Golddip Michelle_Golddip(ISeries<double> input, int redBarThickness, bool showWashout)
		{
			if (cacheMichelle_Golddip != null)
				for (int idx = 0; idx < cacheMichelle_Golddip.Length; idx++)
					if (cacheMichelle_Golddip[idx] != null && cacheMichelle_Golddip[idx].RedBarThickness == redBarThickness && cacheMichelle_Golddip[idx].ShowWashout == showWashout && cacheMichelle_Golddip[idx].EqualsInput(input))
						return cacheMichelle_Golddip[idx];
			return CacheIndicator<Michelle_Golddip>(new Michelle_Golddip(){ RedBarThickness = redBarThickness, ShowWashout = showWashout }, input, ref cacheMichelle_Golddip);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Michelle_Golddip Michelle_Golddip(int redBarThickness, bool showWashout)
		{
			return indicator.Michelle_Golddip(Input, redBarThickness, showWashout);
		}

		public Indicators.Michelle_Golddip Michelle_Golddip(ISeries<double> input , int redBarThickness, bool showWashout)
		{
			return indicator.Michelle_Golddip(input, redBarThickness, showWashout);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Michelle_Golddip Michelle_Golddip(int redBarThickness, bool showWashout)
		{
			return indicator.Michelle_Golddip(Input, redBarThickness, showWashout);
		}

		public Indicators.Michelle_Golddip Michelle_Golddip(ISeries<double> input , int redBarThickness, bool showWashout)
		{
			return indicator.Michelle_Golddip(input, redBarThickness, showWashout);
		}
	}
}

#endregion
