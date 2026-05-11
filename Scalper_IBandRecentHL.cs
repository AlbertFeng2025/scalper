#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using System.Windows.Media;

#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class Scalper_IBandRecentHL : Indicator
    {
        private SessionIterator sessionIterator;
        private DateTime sessionStartToday;
        private DateTime ibEndTime;

        private double ibHigh;
        private double ibLow;
        private bool ibCompleted;           // Changed name for clarity
        private TimeZoneInfo nyTz;
		private DateTime currentIbDate = DateTime.MinValue;   // NY date of current IB
        protected override void OnStateChange()
			
        {
			
            if (State == State.SetDefaults)
            {
                Description = "Scalper IB + Recent HL - Correct First Hour Only";
                Name        = "Scalper_IBandRecentHL";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;

                IBMinutes     = 60;
                RecentMinutes = 60;

                AddPlot(new Stroke(Brushes.LimeGreen, 3), PlotStyle.Line, "IB High");
                AddPlot(new Stroke(Brushes.LimeGreen, 3), PlotStyle.Line, "IB Low");
                AddPlot(new Stroke(Brushes.LimeGreen, 1), PlotStyle.Line, "IB Mid");

                AddPlot(new Stroke(Brushes.DodgerBlue, 3), PlotStyle.Line, "Recent High");
                AddPlot(new Stroke(Brushes.DodgerBlue, 3), PlotStyle.Line, "Recent Low");
                AddPlot(new Stroke(Brushes.DodgerBlue, 1), PlotStyle.Line, "Recent Mid");

                ShowIBHigh = ShowIBLow = ShowIBMid = true;
                ShowRecentHigh = ShowRecentLow = ShowRecentMid = true;
            }
            
			else if (State == State.DataLoaded)
			{
			    sessionIterator = new SessionIterator(Bars);
			    nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
			}
        }

        // Cache the NY timezone (set this in DataLoaded or as a field)
       // private TimeZoneInfo nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
           
			protected override void OnBarUpdate()
			{
			    if (CurrentBar < 1) return;
			
			    // Convert this bar's time to New York time.
			    // "Eastern Standard Time" is the Windows ID that auto-handles EST<->EDT.
			    DateTime barTimeNy = TimeZoneInfo.ConvertTime(Time[0], nyTz);
			    DateTime tradingDateNy = barTimeNy.Date;
			
			    int minuteOfDay = barTimeNy.Hour * 60 + barTimeNy.Minute;
			    const int IB_START = 9 * 60 + 30;   // 570 = 9:30 ET
			    const int IB_END   = 10 * 60 + 30;  // 630 = 10:30 ET
			
			    bool isInIb    = minuteOfDay >= IB_START && minuteOfDay < IB_END;
			    bool isAfterIb = minuteOfDay >= IB_END;
			
			    // === IB lifecycle, driven purely by NY clock ===
			    if (isInIb)
			    {
			        if (tradingDateNy != currentIbDate)
			        {
			            // First IB-window bar of a new NY trading day → start fresh
			            currentIbDate = tradingDateNy;
			            ibHigh = High[0];
			            ibLow  = Low[0];
			            ibCompleted = false;
			            Print($"[IB] New IB window started on {tradingDateNy:yyyy-MM-dd} (NY 9:30)");
			        }
			        else if (!ibCompleted)
			        {
			            // Continue accumulating
			            ibHigh = Math.Max(ibHigh, High[0]);
			            ibLow  = Math.Min(ibLow,  Low[0]);
			        }
			    }
			    else if (isAfterIb)
			    {
			        // After 10:30 ET — if today's IB was being built, lock it now
			        if (!ibCompleted && tradingDateNy == currentIbDate)
			        {
			            ibCompleted = true;
			            Print($"[IB] Locked at NY 10:30 on {tradingDateNy:yyyy-MM-dd}: H={ibHigh:F2} L={ibLow:F2}");
			        }
			        // If we have no IB for today yet (chart loaded after 10:30 ET, or system
			        // missed the window), we leave it alone. Yesterday's values persist.
			    }
			    // else: before 9:30 ET on the current NY date → yesterday's IB stays
			
			    // === Recent HL (always updating, unchanged) ===
			    DateTime recentStart = Time[0].AddMinutes(-RecentMinutes);
			    int barsBack = Math.Max(1, CurrentBar - Bars.GetBar(recentStart));
			
			    double recHigh = MAX(High, barsBack)[0];
			    double recLow  = MIN(Low,  barsBack)[0];
			
			    // === Plot ===
			    // Only publish IB once it's calculated or in progress for today
			    if (ibCompleted || (isInIb && tradingDateNy == currentIbDate))
			    {
			        IBHigh[0] = ibHigh;
			        IBLow[0]  = ibLow;
			        IBMid[0]  = (ibHigh + ibLow) / 2;
			    }
			
			    RecentHigh[0] = recHigh;
			    RecentLow[0]  = recLow;
			    RecentMid[0]  = (recHigh + recLow) / 2;
			}

        #region Properties
        [NinjaScriptProperty]
        [Display(Name="IB Minutes", GroupName="Initial Balance", Order=1)]
        public int IBMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Recent Minutes", GroupName="Recent HL", Order=1)]
        public int RecentMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show IB High", GroupName="Visibility", Order=1)]
        public bool ShowIBHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show IB Low", GroupName="Visibility", Order=2)]
        public bool ShowIBLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show IB Mid", GroupName="Visibility", Order=3)]
        public bool ShowIBMid { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show Recent High", GroupName="Visibility", Order=4)]
        public bool ShowRecentHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show Recent Low", GroupName="Visibility", Order=5)]
        public bool ShowRecentLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show Recent Mid", GroupName="Visibility", Order=6)]
        public bool ShowRecentMid { get; set; }

        [Browsable(false)][XmlIgnore] public Series<double> IBHigh  => Values[0];
        [Browsable(false)][XmlIgnore] public Series<double> IBLow   => Values[1];
        [Browsable(false)][XmlIgnore] public Series<double> IBMid   => Values[2];
        [Browsable(false)][XmlIgnore] public Series<double> RecentHigh => Values[3];
        [Browsable(false)][XmlIgnore] public Series<double> RecentLow  => Values[4];
        [Browsable(false)][XmlIgnore] public Series<double> RecentMid  => Values[5];
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Scalper_IBandRecentHL[] cacheScalper_IBandRecentHL;
		public Scalper_IBandRecentHL Scalper_IBandRecentHL(int iBMinutes, int recentMinutes, bool showIBHigh, bool showIBLow, bool showIBMid, bool showRecentHigh, bool showRecentLow, bool showRecentMid)
		{
			return Scalper_IBandRecentHL(Input, iBMinutes, recentMinutes, showIBHigh, showIBLow, showIBMid, showRecentHigh, showRecentLow, showRecentMid);
		}

		public Scalper_IBandRecentHL Scalper_IBandRecentHL(ISeries<double> input, int iBMinutes, int recentMinutes, bool showIBHigh, bool showIBLow, bool showIBMid, bool showRecentHigh, bool showRecentLow, bool showRecentMid)
		{
			if (cacheScalper_IBandRecentHL != null)
				for (int idx = 0; idx < cacheScalper_IBandRecentHL.Length; idx++)
					if (cacheScalper_IBandRecentHL[idx] != null && cacheScalper_IBandRecentHL[idx].IBMinutes == iBMinutes && cacheScalper_IBandRecentHL[idx].RecentMinutes == recentMinutes && cacheScalper_IBandRecentHL[idx].ShowIBHigh == showIBHigh && cacheScalper_IBandRecentHL[idx].ShowIBLow == showIBLow && cacheScalper_IBandRecentHL[idx].ShowIBMid == showIBMid && cacheScalper_IBandRecentHL[idx].ShowRecentHigh == showRecentHigh && cacheScalper_IBandRecentHL[idx].ShowRecentLow == showRecentLow && cacheScalper_IBandRecentHL[idx].ShowRecentMid == showRecentMid && cacheScalper_IBandRecentHL[idx].EqualsInput(input))
						return cacheScalper_IBandRecentHL[idx];
			return CacheIndicator<Scalper_IBandRecentHL>(new Scalper_IBandRecentHL(){ IBMinutes = iBMinutes, RecentMinutes = recentMinutes, ShowIBHigh = showIBHigh, ShowIBLow = showIBLow, ShowIBMid = showIBMid, ShowRecentHigh = showRecentHigh, ShowRecentLow = showRecentLow, ShowRecentMid = showRecentMid }, input, ref cacheScalper_IBandRecentHL);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Scalper_IBandRecentHL Scalper_IBandRecentHL(int iBMinutes, int recentMinutes, bool showIBHigh, bool showIBLow, bool showIBMid, bool showRecentHigh, bool showRecentLow, bool showRecentMid)
		{
			return indicator.Scalper_IBandRecentHL(Input, iBMinutes, recentMinutes, showIBHigh, showIBLow, showIBMid, showRecentHigh, showRecentLow, showRecentMid);
		}

		public Indicators.Scalper_IBandRecentHL Scalper_IBandRecentHL(ISeries<double> input , int iBMinutes, int recentMinutes, bool showIBHigh, bool showIBLow, bool showIBMid, bool showRecentHigh, bool showRecentLow, bool showRecentMid)
		{
			return indicator.Scalper_IBandRecentHL(input, iBMinutes, recentMinutes, showIBHigh, showIBLow, showIBMid, showRecentHigh, showRecentLow, showRecentMid);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Scalper_IBandRecentHL Scalper_IBandRecentHL(int iBMinutes, int recentMinutes, bool showIBHigh, bool showIBLow, bool showIBMid, bool showRecentHigh, bool showRecentLow, bool showRecentMid)
		{
			return indicator.Scalper_IBandRecentHL(Input, iBMinutes, recentMinutes, showIBHigh, showIBLow, showIBMid, showRecentHigh, showRecentLow, showRecentMid);
		}

		public Indicators.Scalper_IBandRecentHL Scalper_IBandRecentHL(ISeries<double> input , int iBMinutes, int recentMinutes, bool showIBHigh, bool showIBLow, bool showIBMid, bool showRecentHigh, bool showRecentLow, bool showRecentMid)
		{
			return indicator.Scalper_IBandRecentHL(input, iBMinutes, recentMinutes, showIBHigh, showIBLow, showIBMid, showRecentHigh, showRecentLow, showRecentMid);
		}
	}
}

#endregion
