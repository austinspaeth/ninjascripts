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
//  OFS_RiskZones — Stop-Loss & Take-Profit Zones
// ----------------------------------------------------------------------------
//  Paints, for the CURRENT bias, where a protective stop belongs and where to
//  take profits:
//      - a shaded RED "stop zone" just beyond the nearest structure (recent
//        swing) with a volatility (ATR) cushion so noise doesn't tag you
//      - GREEN target lines at the logical profit spots: nearby key levels
//        (prior day / value / VWAP / IB from the Auction Context tool) and,
//        where none exist, risk-multiple (R) targets
//      - a gold ENTRY reference line
//
//  Direction comes from the Auction Context regime (shared SuiteContext) unless
//  you override it Long/Short. Entry defaults to current price (or set manually).
//
//  NOTE: this is a LIVE planning overlay — it always shows the suggestion for the
//  current bar and updates as price/structure change. It is not a historical
//  signal, so the lines moving over time is expected, not repainting.
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class RiskZones : Indicator
	{
		private SwingLevelDetector	swings;
		private ContextState		ctx;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"Draws a structural stop-loss zone and key-level / R-multiple take-profit lines for the current bias. Part of OrderFlowSuite.";
				Name				= "OFS_RiskZones";
				Calculate			= Calculate.OnEachTick;
				IsOverlay			= true;
				DisplayInDataBox	= false;
				DrawOnPricePanel	= true;
				PaintPriceMarkers	= false;
				IsSuspendedWhileInactive = true;

				OverrideDirection	= TradeDirection.None;	// None = use Auction Context bias
				ManualEntry			= 0;					// 0 = current price
				ATRPeriod			= 14;
				StopATRMult			= 1.25;
				SwingLookback		= 10;
				StopPadTicks		= 4;
				StopZoneTicks		= 6;
				TargetCount			= 3;
				UseKeyLevelTargets	= true;
				Target1R			= 1.5;
				Target2R			= 2.5;
				Target3R			= 4.0;
				ExtendBars			= 12;
				LookbackBars		= 5;
				ShowLabels			= true;
			}
			else if (State == State.DataLoaded)
			{
				swings = new SwingLevelDetector(SwingLookback);
			}
			else if (State == State.Historical)
			{
				ctx = SuiteContext.Get(Instrument != null ? Instrument.FullName : "__default__");
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			if (CurrentBar < Math.Max(ATRPeriod, SwingLookback * 2) + 2) return;

			swings.Update(High, Low, CurrentBar);

			TradeDirection dir = ResolveDirection();
			if (dir == TradeDirection.None) { ClearAll(); return; }

			double atr		= ATR(ATRPeriod)[0];
			if (atr <= 0) return;
			double entry	= ManualEntry > 0 ? ManualEntry : Close[0];
			double pad		= StopPadTicks * TickSize;

			SessionLevels lv = HydrateLevels();

			// ---- stop (structure + ATR cushion, capped by ATR distance) ----
			double stop;
			double maxDist = StopATRMult * atr;
			if (dir == TradeDirection.Long)
			{
				double struc = swings.HasLow() ? swings.LastSwingLow - pad : entry - maxDist;
				if (entry - struc > maxDist) struc = entry - maxDist;	// don't let it get too wide
				stop = struc;
			}
			else
			{
				double struc = swings.HasHigh() ? swings.LastSwingHigh + pad : entry + maxDist;
				if (struc - entry > maxDist) struc = entry + maxDist;
				stop = struc;
			}

			double risk = Math.Abs(entry - stop);
			if (risk <= 0) { ClearAll(); return; }

			// ---- targets ----
			List<double> targets = BuildTargets(dir, entry, stop, risk, lv);

			// ---- draw ----
			DrawZones(dir, entry, stop, risk, targets);
		}

		private TradeDirection ResolveDirection()
		{
			if (OverrideDirection != TradeDirection.None)
				return OverrideDirection;
			if (ctx == null) return TradeDirection.None;
			switch (ctx.Mode)
			{
				case MarketMode.TrendUp:
				case MarketMode.BreakoutUp:
				case MarketMode.FailedBreakoutDown:
					return TradeDirection.Long;
				case MarketMode.TrendDown:
				case MarketMode.BreakoutDown:
				case MarketMode.FailedBreakoutUp:
					return TradeDirection.Short;
				default:
					return TradeDirection.None;	// balanced / reversal-risk / unknown -> no auto plan
			}
		}

		private List<double> BuildTargets(TradeDirection dir, double entry, double stop, double risk, SessionLevels lv)
		{
			List<double> result = new List<double>();
			double[] rMults = new double[] { Target1R, Target2R, Target3R };

			// gather key levels in the trade direction, at least 1R away
			List<double> levelTargets = new List<double>();
			if (UseKeyLevelTargets)
			{
				foreach (KeyLevel k in lv.BuildKeyLevels())
				{
					if (dir == TradeDirection.Long && k.Price > entry + risk * 0.9)
						levelTargets.Add(k.Price);
					else if (dir == TradeDirection.Short && k.Price < entry - risk * 0.9)
						levelTargets.Add(k.Price);
				}
				levelTargets.Sort();
				if (dir == TradeDirection.Short) levelTargets.Reverse();	// nearest first
			}

			int li = 0;
			for (int i = 0; i < TargetCount; i++)
			{
				if (li < levelTargets.Count)
				{
					result.Add(levelTargets[li]);
					li++;
				}
				else
				{
					double rm = i < rMults.Length ? rMults[i] : (i + 1);
					result.Add(dir == TradeDirection.Long ? entry + rm * risk : entry - rm * risk);
				}
			}
			return result;
		}

		// --------------------------------------------------------------------
		private static Brush Frozen(byte a, byte r, byte g, byte b)
		{
			SolidColorBrush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
			br.Freeze();
			return br;
		}

		private void DrawZones(TradeDirection dir, double entry, double stop, double risk, List<double> targets)
		{
			int startAgo = LookbackBars;
			int endAgo	= -ExtendBars;	// project to the right of the last bar

			// entry line
			Draw.Line(this, "rz_entry", false, startAgo, entry, endAgo, entry, Brushes.Goldenrod, DashStyleHelper.Solid, 2);
			if (ShowLabels)
				Draw.Text(this, "rz_entry_t", false, "ENTRY " + entry.ToString("0.00"), endAgo, entry, 0,
					Brushes.Goldenrod, new SimpleFont("Arial", 10), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);

			// stop zone (shaded band beyond structure) + stop line
			double zone = StopZoneTicks * TickSize;
			double zHi = dir == TradeDirection.Long ? stop : stop + zone;
			double zLo = dir == TradeDirection.Long ? stop - zone : stop;
			Draw.Rectangle(this, "rz_stopzone", false, startAgo, zHi, endAgo, zLo,
				Brushes.Transparent, Frozen(60, 200, 40, 40), 60);
			Draw.Line(this, "rz_stop", false, startAgo, stop, endAgo, stop, Brushes.Red, DashStyleHelper.Dash, 2);
			if (ShowLabels)
				Draw.Text(this, "rz_stop_t", false, "STOP " + stop.ToString("0.00") + "  (" + (int)Math.Round(risk / TickSize) + " tk)",
					endAgo, stop, 0, Brushes.Red, new SimpleFont("Arial", 10), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);

			// target lines
			for (int i = 0; i < 3; i++)
			{
				string tag = "rz_t" + i;
				if (i < targets.Count)
				{
					double tp = targets[i];
					double rr = risk > 0 ? Math.Abs(tp - entry) / risk : 0;
					Draw.Line(this, tag, false, startAgo, tp, endAgo, tp, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
					if (ShowLabels)
						Draw.Text(this, tag + "_t", false,
							"T" + (i + 1) + " " + tp.ToString("0.00") + "  (" + rr.ToString("0.0") + "R)",
							endAgo, tp, 0, Brushes.LimeGreen, new SimpleFont("Arial", 10), TextAlignment.Left,
							Brushes.Transparent, Brushes.Transparent, 0);
				}
				else
				{
					RemoveDrawObject(tag);
					RemoveDrawObject(tag + "_t");
				}
			}
		}

		private void ClearAll()
		{
			RemoveDrawObject("rz_entry");    RemoveDrawObject("rz_entry_t");
			RemoveDrawObject("rz_stop");     RemoveDrawObject("rz_stop_t");
			RemoveDrawObject("rz_stopzone");
			for (int i = 0; i < 3; i++) { RemoveDrawObject("rz_t" + i); RemoveDrawObject("rz_t" + i + "_t"); }
		}

		private SessionLevels HydrateLevels()
		{
			SessionLevels lv = new SessionLevels();
			if (ctx != null)
			{
				lv.Vwap			= ctx.Vwap;
				lv.PriorDayHigh	= ctx.PriorHigh;
				lv.PriorDayLow	= ctx.PriorLow;
				lv.POC			= ctx.Poc;
				lv.VAH			= ctx.Vah;
				lv.VAL			= ctx.Val;
				lv.IbHigh		= ctx.IbHigh;
				lv.IbLow		= ctx.IbLow;
			}
			return lv;
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Override direction (None = auto)", Order = 1, GroupName = "Plan")]
		public TradeDirection OverrideDirection { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Manual entry (0 = current price)", Order = 2, GroupName = "Plan")]
		public double ManualEntry { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR period", Order = 3, GroupName = "Plan")]
		public int ATRPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Max stop (ATR mult)", Order = 4, GroupName = "Plan")]
		public double StopATRMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Swing lookback (bars)", Order = 5, GroupName = "Plan")]
		public int SwingLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Stop pad (ticks)", Order = 6, GroupName = "Plan")]
		public int StopPadTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Stop zone thickness (ticks)", Order = 7, GroupName = "Plan")]
		public int StopZoneTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 3)]
		[Display(Name = "Target count (1-3)", Order = 8, GroupName = "Plan")]
		public int TargetCount { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use key-level targets", Order = 9, GroupName = "Plan")]
		public bool UseKeyLevelTargets { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Target 1 (R multiple)", Order = 10, GroupName = "Plan")]
		public double Target1R { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Target 2 (R multiple)", Order = 11, GroupName = "Plan")]
		public double Target2R { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Target 3 (R multiple)", Order = 12, GroupName = "Plan")]
		public double Target3R { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Extend right (bars)", Order = 1, GroupName = "Display")]
		public int ExtendBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Anchor back (bars)", Order = 2, GroupName = "Display")]
		public int LookbackBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show labels", Order = 3, GroupName = "Display")]
		public bool ShowLabels { get; set; }
		#endregion
	}
}
