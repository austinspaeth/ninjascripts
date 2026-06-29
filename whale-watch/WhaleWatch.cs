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
#endregion

// WhaleWatch — Institutional / "whale" activity detector for NinjaTrader 8
// ----------------------------------------------------------------------------
// Detects and visually marks aggressive institutional footprints so you can
// trade WITH large players instead of against them. It flags three signals:
//
//   1. VOLUME SPIKES   — bars trading far above their recent average volume.
//   2. ORDER-FLOW DELTA — net aggressive buying vs selling within a bar,
//                          measured live from the bid/ask tape (real-time).
//   3. BLOCK PRINTS    — single large trades hitting the tape (a whale lifting
//                          the offer or hitting the bid in one shot).
//
// It also tags ABSORPTION bars (huge volume, tiny range = big player soaking up
// supply/demand — often a reversal tell) and draws horizontal "footprint" levels
// where the whales were most active, which tend to act as future support/resistance.
//
// NOTE on data: historical bars only have total volume (no per-trade bid/ask on
// the free plan), so delta + block detection are REAL-TIME only. Volume-spike and
// absorption detection work on both historical and live bars.

namespace NinjaTrader.NinjaScript.Indicators
{
	public class WhaleWatch : Indicator
	{
		// --- runtime state for order-flow (real-time tape reading) ---
		private double	currentBid	= 0;
		private double	currentAsk	= 0;
		private long	barBuyVolume	= 0;	// aggressive buys (traded at/above ask) this bar
		private long	barSellVolume	= 0;	// aggressive sells (traded at/below bid) this bar
		private int		lastDeltaBar	= -1;	// guards the per-bar delta reset

		// per-bar delta history so we can reference closed bars
		private Series<double>	barDelta;

		// brushes (created once, frozen for performance)
		private Brush	bullWhaleBrush;
		private Brush	bearWhaleBrush;
		private Brush	absorptionBrush;
		private Brush	blockBrush;
		private Brush	levelBrush;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= @"Detects institutional/whale activity (volume spikes, order-flow delta, and block prints) and marks it visually. Tuned for ES/NQ index futures.";
				Name						= "WhaleWatch";
				Calculate					= Calculate.OnEachTick;	// needed for live delta/blocks
				IsOverlay					= true;					// draw on the price panel
				DisplayInDataBox			= true;
				DrawOnPricePanel			= true;
				PaintPriceMarkers			= false;
				IsSuspendedWhileInactive	= true;

				// ---- user inputs (ES/NQ-friendly defaults) ----
				VolumePeriod		= 20;		// lookback for "average" volume
				VolumeMultiplier	= 2.0;		// bar is a whale if vol >= avg * this
				AbsorptionRangePct	= 0.50;		// "tiny range" = <= 50% of avg range
				BlockTradeSize		= 50;		// single tape print >= this = a block (ES; raise for NQ)
				DeltaWhaleThreshold	= 1500;		// |bar delta| >= this = aggressive institutional bar
				ShowFootprintLevels	= true;
				ColorWhaleBars		= true;
				EnableAlerts		= true;
				MarkerOffsetTicks	= 4;		// how far arrows sit off the bar
			}
			else if (State == State.Configure)
			{
				bullWhaleBrush	= Brushes.Lime;
				bearWhaleBrush	= Brushes.Red;
				absorptionBrush	= Brushes.Gold;
				blockBrush		= Brushes.Magenta;
				levelBrush		= new SolidColorBrush(Color.FromArgb(120, 255, 215, 0)); // semi-transparent gold
				levelBrush.Freeze();
			}
			else if (State == State.DataLoaded)
			{
				barDelta = new Series<double>(this);
			}
		}

		// --------------------------------------------------------------------
		// Real-time tape reader: classify every executed trade as an aggressive
		// buy (filled at/above the ask) or aggressive sell (at/below the bid),
		// and watch for oversized single prints (blocks).
		// Only fires live / during Market Replay — never on historical loads.
		// --------------------------------------------------------------------
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType == MarketDataType.Ask)
			{
				currentAsk = e.Price;
				return;
			}
			if (e.MarketDataType == MarketDataType.Bid)
			{
				currentBid = e.Price;
				return;
			}
			if (e.MarketDataType != MarketDataType.Last)
				return;

			// reset the running delta when a fresh bar begins
			if (CurrentBar != lastDeltaBar)
			{
				barBuyVolume	= 0;
				barSellVolume	= 0;
				lastDeltaBar	= CurrentBar;
			}

			long vol = e.Volume;

			// classify aggressor side using the resting bid/ask at execution time
			if (currentAsk > 0 && e.Price >= currentAsk)
				barBuyVolume += vol;
			else if (currentBid > 0 && e.Price <= currentBid)
				barSellVolume += vol;
			// trades between the spread are left unclassified (neutral)

			// --- block print: one whale-sized trade in a single fill ---
			if (vol >= BlockTradeSize)
			{
				bool buyBlock	= currentAsk > 0 && e.Price >= currentAsk;
				Brush bBrush	= buyBlock ? bullWhaleBrush : (currentBid > 0 && e.Price <= currentBid ? bearWhaleBrush : blockBrush);

				Draw.Dot(this, "block_" + CurrentBar + "_" + e.Time.Ticks, false, 0, e.Price, bBrush);

				if (EnableAlerts && State == State.Realtime)
					Alert("whaleBlock", Priority.High,
						string.Format("WhaleWatch: BLOCK print {0} @ {1}", vol, e.Price),
						"", 10, Brushes.Black, blockBrush);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < VolumePeriod)
				return;

			// store the live delta for this bar so closed bars can be referenced
			barDelta[0] = barBuyVolume - barSellVolume;

			double avgVolume	= SMA(Volume, VolumePeriod)[0];
			double avgRange		= SMA(Range(), VolumePeriod)[0];
			double thisVolume	= Volume[0];
			double thisRange	= High[0] - Low[0];
			double rvol			= avgVolume > 0 ? thisVolume / avgVolume : 0;

			bool isVolumeSpike	= thisVolume >= avgVolume * VolumeMultiplier;
			bool isAbsorption	= isVolumeSpike && thisRange <= avgRange * AbsorptionRangePct;
			bool isDeltaWhale	= Math.Abs(barDelta[0]) >= DeltaWhaleThreshold;
			bool isBullish		= Close[0] >= Open[0];

			// reset any prior bar coloring decision (NT re-evaluates current bar on each tick)
			if (ColorWhaleBars && (isVolumeSpike || isDeltaWhale))
				BarBrush = isAbsorption ? absorptionBrush : (isBullish ? bullWhaleBrush : bearWhaleBrush);

			// only draw permanent markers once the bar closes (avoids flicker / repaint)
			if (IsFirstTickOfBar && CurrentBar > 0)
				EvaluateClosedBar(CurrentBar - 1, avgVolume, avgRange);
		}

		// Evaluate the bar that JUST closed (barsAgo == 1 at this moment).
		private void EvaluateClosedBar(int barIndex, double avgVolume, double avgRange)
		{
			int barsAgo			= CurrentBar - barIndex;	// == 1
			double vol			= Volume[barsAgo];
			double range		= High[barsAgo] - Low[barsAgo];
			double delta		= barDelta[barsAgo];
			double rvol			= avgVolume > 0 ? vol / avgVolume : 0;

			bool isVolumeSpike	= vol >= avgVolume * VolumeMultiplier;
			bool isAbsorption	= isVolumeSpike && range <= avgRange * AbsorptionRangePct;
			bool isDeltaWhale	= Math.Abs(delta) >= DeltaWhaleThreshold;

			if (!isVolumeSpike && !isDeltaWhale)
				return;

			bool isBullish		= Close[barsAgo] >= Open[barsAgo];
			double tick			= TickSize * MarkerOffsetTicks;
			string tag			= "whale_" + barIndex;

			if (isAbsorption)
			{
				// big effort, little result — potential reversal / large player absorbing
				Draw.Square(this, tag, false, barsAgo, High[barsAgo] + tick, absorptionBrush);
				Draw.Text(this, tag + "_t", false, "ABSORB", barsAgo, High[barsAgo] + tick * 2, 0,
					absorptionBrush, new SimpleFont("Arial", 9), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}
			else if (isBullish)
			{
				Draw.ArrowUp(this, tag, false, barsAgo, Low[barsAgo] - tick, bullWhaleBrush);
			}
			else
			{
				Draw.ArrowDown(this, tag, false, barsAgo, High[barsAgo] + tick, bearWhaleBrush);
			}

			// label with relative volume + delta so you can size up the conviction
			string info = string.Format("{0:0.0}x{1}", rvol, isDeltaWhale ? string.Format(" Δ{0:+#;-#;0}", delta) : "");
			Draw.Text(this, tag + "_info", false, info, barsAgo,
				isBullish ? Low[barsAgo] - tick * 3 : High[barsAgo] + tick * 3, 0,
				isBullish ? bullWhaleBrush : bearWhaleBrush, new SimpleFont("Arial", 8),
				TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);

			// draw a horizontal "footprint" level at the whale's price (institutional S/R)
			if (ShowFootprintLevels)
			{
				double levelPrice = (High[barsAgo] + Low[barsAgo]) / 2.0;
				Draw.HorizontalLine(this, "level_" + barIndex, levelPrice, levelBrush,
					DashStyleHelper.Dash, 1);
			}

			if (EnableAlerts && State == State.Realtime)
			{
				string msg = string.Format("WhaleWatch: {0} {1} @ {2:0.0}x vol{3}",
					isAbsorption ? "ABSORPTION" : (isBullish ? "Bullish whale" : "Bearish whale"),
					Instrument.MasterInstrument.Name, rvol,
					isDeltaWhale ? string.Format(", delta {0:+#;-#;0}", delta) : "");
				Alert("whaleBar", Priority.High, msg, "", 10, Brushes.Black,
					isBullish ? bullWhaleBrush : bearWhaleBrush);
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Volume lookback (bars)", Description = "Bars used to compute the average/normal volume.", Order = 1, GroupName = "Whale Detection")]
		public int VolumePeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, double.MaxValue)]
		[Display(Name = "Volume spike multiplier", Description = "A bar is a whale when its volume >= average volume x this value. 2.0 = double normal.", Order = 2, GroupName = "Whale Detection")]
		public double VolumeMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 1.0)]
		[Display(Name = "Absorption range %", Description = "A volume spike with a range <= this fraction of average range is flagged as absorption (potential reversal).", Order = 3, GroupName = "Whale Detection")]
		public double AbsorptionRangePct { get; set; }

		[NinjaScriptProperty]
		[Range(1, long.MaxValue)]
		[Display(Name = "Block trade size", Description = "A single tape print of this size or larger is marked as a block (real-time only). ES ~50, NQ ~25.", Order = 4, GroupName = "Whale Detection")]
		public long BlockTradeSize { get; set; }

		[NinjaScriptProperty]
		[Range(1, long.MaxValue)]
		[Display(Name = "Delta whale threshold", Description = "Absolute net aggressive buy/sell volume in a bar to flag it as institutional (real-time only).", Order = 5, GroupName = "Whale Detection")]
		public long DeltaWhaleThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Marker offset (ticks)", Description = "Distance arrows/labels sit off the bar.", Order = 6, GroupName = "Whale Detection")]
		public int MarkerOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show footprint levels", Description = "Draw horizontal support/resistance lines at whale prices.", Order = 1, GroupName = "Display")]
		public bool ShowFootprintLevels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Color whale bars", Description = "Paint whale bars (green=bull, red=bear, gold=absorption).", Order = 2, GroupName = "Display")]
		public bool ColorWhaleBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable alerts", Description = "Fire NinjaTrader alerts when a whale signal prints (real-time).", Order = 3, GroupName = "Display")]
		public bool EnableAlerts { get; set; }
		#endregion
	}
}
