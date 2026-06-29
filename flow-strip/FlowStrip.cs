#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using OrderFlowSuite;
#endregion

// ============================================================================
//  OFS_FlowStrip — Buying/Selling Pressure Heat Strip
// ----------------------------------------------------------------------------
//  A separate panel under the chart that shows a single "pressure" reading as a
//  colored histogram you can read at a glance:
//      bright LIME  = strong, aggressive buying (likely to keep going up)
//      dark green   = buying but SOFTENING (momentum fading -> consider exiting)
//      gray         = neutral / indecision
//      dark -> bright RED = selling taking over
//
//  The reading blends ORDER FLOW (delta = aggressive buys minus sells) with
//  PRICE MOMENTUM, then smooths it. Bars above zero = net buyers in control,
//  below zero = net sellers. The COLOR encodes strength so a shrinking, dulling
//  green bar is your "running out of gas" warning before price even turns.
//
//  DATA: real delta comes from the live bid/ask tape (OnMarketData), or from
//  historical bars when Tick Replay is on. Without tick data it falls back to a
//  bar-shape delta approximation (toggle ApproximateDeltaWithoutTicks). Momentum
//  always works. No Order Flow+ required.
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class FlowStrip : Indicator
	{
		private double	currentBid;
		private double	currentAsk;
		private OrderFlowBarStats	stats;
		private int		statsBar = -1;

		private Series<double>	absDelta;
		private Series<double>	rawScore;
		private readonly Dictionary<int, Brush> palette = new Dictionary<int, Brush>();

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"Buying/selling pressure heat strip: order-flow delta blended with momentum, color-graded from lime (strong buying) through dark green (softening) to red (selling). Part of OrderFlowSuite.";
				Name				= "OFS_FlowStrip";
				Calculate			= Calculate.OnEachTick;
				IsOverlay			= false;	// separate panel under price
				DisplayInDataBox	= true;
				DrawOnPricePanel	= false;
				IsSuspendedWhileInactive = false;

				DeltaWeight			= 1.0;
				MomentumWeight		= 0.6;
				NormalizationLookback = 50;
				SmoothingPeriod		= 5;
				MomentumLookback	= 10;
				ATRPeriod			= 14;
				NeutralZone			= 10;	// |score| below this renders gray
				ApproximateDeltaWithoutTicks = true;

				AddPlot(new Stroke(Brushes.Gray, 4), PlotStyle.Bar, "Flow");
			}
			else if (State == State.DataLoaded)
			{
				stats		= new OrderFlowBarStats();
				absDelta	= new Series<double>(this);
				rawScore	= new Series<double>(this);
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (stats == null) return;
			if (e.MarketDataType == MarketDataType.Ask) { currentAsk = e.Price; return; }
			if (e.MarketDataType == MarketDataType.Bid) { currentBid = e.Price; return; }
			if (e.MarketDataType != MarketDataType.Last) return;

			if (CurrentBar != statsBar) { stats.StartBar(e.Price, 0); statsBar = CurrentBar; }
			stats.AddTrade(e.Price, e.Volume, currentBid, currentAsk, currentBid > 0 && currentAsk > 0);
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < Math.Max(ATRPeriod, Math.Max(NormalizationLookback, MomentumLookback)) + 1)
			{
				Values[0][0] = 0;
				return;
			}

			// --- per-bar delta (real if we have ticks, else approximate) ---
			bool hadTicks = (statsBar == CurrentBar && stats.TotalVolume > 0);
			double delta = hadTicks ? stats.Delta
						: (ApproximateDeltaWithoutTicks ? ApproxDelta(High[0], Low[0], Close[0], Volume[0]) : 0);
			absDelta[0] = Math.Abs(delta);

			// --- normalize delta by its own recent average magnitude ---
			double avgAbsDelta = SMA(absDelta, NormalizationLookback)[0];
			double normDelta = avgAbsDelta > 0 ? delta / avgAbsDelta : 0;	// ~ -1..+1 typical

			// --- momentum: price change over lookback, normalized by ATR ---
			double atr = ATR(ATRPeriod)[0];
			double mom = atr > 0 ? (Close[0] - Close[MomentumLookback]) / (atr * Math.Sqrt(MomentumLookback)) : 0;

			// --- blend, scale to ~ -100..100, then smooth ---
			double raw = (DeltaWeight * normDelta * 70.0) + (MomentumWeight * mom * 60.0);
			rawScore[0] = Clamp(raw, -150, 150);
			double score = Clamp(EMA(rawScore, SmoothingPeriod)[0], -100, 100);

			Values[0][0] = score;
			PlotBrushes[0][0] = FlowBrush(score);
		}

		// crude per-bar delta proxy when no tick data exists for the bar
		private static double ApproxDelta(double high, double low, double close, double volume)
		{
			double range = high - low;
			double loc = range > 0 ? (close - low) / range : 0.5;
			return volume * ((2.0 * loc) - 1.0);
		}

		private static double Clamp(double v, double lo, double hi)
		{
			return v < lo ? lo : (v > hi ? hi : v);
		}

		// Map a -100..+100 score to a cached, frozen gradient brush.
		private Brush FlowBrush(double score)
		{
			int bucket = (int)(Math.Round(score / 5.0) * 5);
			if (bucket > 100) bucket = 100; if (bucket < -100) bucket = -100;

			Brush cached;
			if (palette.TryGetValue(bucket, out cached)) return cached;

			byte r, g, b;
			if (Math.Abs(bucket) < NeutralZone)
			{
				r = 110; g = 110; b = 110;	// neutral gray
			}
			else if (bucket > 0)
			{
				double t = bucket / 100.0;					// 0..1
				r = (byte)(0 + t * 80);						// dark green -> lime
				g = (byte)(90 + t * 165);
				b = (byte)(0 + t * 80);
			}
			else
			{
				double t = -bucket / 100.0;					// 0..1
				r = (byte)(120 + t * 135);					// dark red -> bright red
				g = (byte)(45 * (1 - t));
				b = (byte)(45 * (1 - t));
			}

			SolidColorBrush br = new SolidColorBrush(Color.FromRgb(r, g, b));
			br.Freeze();
			palette[bucket] = br;
			return br;
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Delta weight", Description = "How much order-flow delta drives the reading.", Order = 1, GroupName = "Logic")]
		public double DeltaWeight { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Momentum weight", Description = "How much price momentum drives the reading.", Order = 2, GroupName = "Logic")]
		public double MomentumWeight { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Normalization lookback (bars)", Order = 3, GroupName = "Logic")]
		public int NormalizationLookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Smoothing period", Description = "Higher = smoother, slower color changes.", Order = 4, GroupName = "Logic")]
		public int SmoothingPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Momentum lookback (bars)", Order = 5, GroupName = "Logic")]
		public int MomentumLookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR period", Order = 6, GroupName = "Logic")]
		public int ATRPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Neutral zone (gray below)", Order = 7, GroupName = "Logic")]
		public int NeutralZone { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Approximate delta without ticks", Order = 8, GroupName = "Logic")]
		public bool ApproximateDeltaWithoutTicks { get; set; }
		#endregion
	}
}
