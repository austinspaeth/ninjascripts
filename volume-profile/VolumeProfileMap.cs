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
//  OFS_VolumeProfile — Horizontal Volume-at-Price Profile
// ----------------------------------------------------------------------------
//  Draws a sideways histogram of how much volume traded at each price for the
//  current session, so you can see WHERE the action is:
//      - POC (Point of Control): the most-traded price = strongest magnet / S-R,
//        drawn as a highlighted line.
//      - Value Area (VAH..VAL): the price band holding ~70% of volume; the bars
//        inside it are drawn brighter than the bars outside.
//  Bars grow from the right edge of the chart; longer bar = more volume there.
//
//  DATA / ACCURACY: true volume-at-each-price needs tick data. With chart
//  "Tick Replay" ON this is accurate; otherwise each bar's volume is spread
//  across the prices it covered (an approximation — good for locating the key
//  levels, not exact). No Order Flow+ required. See suite README.
//
//  Rendering is done in OnRender (SharpDX) for clean pixel-space bars. The bar
//  data is snapshotted on each new bar so the render thread never reads the live
//  accumulator while it changes.
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class VolumeProfileMap : Indicator
	{
		private VolumeProfile	profile;
		private int				lastBuiltBar = -1;

		// snapshot read by OnRender (assigned atomically; never mutated after assignment)
		private volatile List<KeyValuePair<double, double>> renderLevels;
		private volatile object snapshotBox;	// boxes a small struct of POC/VA/max/tick

		private class Snap
		{
			public double Poc, Vah, Val, MaxVol, Tick;
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"Horizontal volume-at-price profile (POC, value area) for the current session. Part of OrderFlowSuite.";
				Name				= "OFS_VolumeProfile";
				Calculate			= Calculate.OnBarClose;	// profile is a closed-bar build
				IsOverlay			= true;
				DisplayInDataBox	= false;
				DrawOnPricePanel	= true;
				PaintPriceMarkers	= false;
				IsSuspendedWhileInactive = true;

				WidthPercent		= 0.22;
				ValueAreaPercent	= 0.70;
				MaxProfileLevelsPerBar = 100;
				Opacity				= 45;
				ShowPocLine			= true;
				ShowValueAreaLines	= true;
				ProfileOnRight		= true;
			}
			else if (State == State.DataLoaded)
			{
				profile = new VolumeProfile();
				profile.Reset(TickSize);
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			if (CurrentBar < 1) return;

			if (Bars.IsFirstBarOfSession)
			{
				profile = new VolumeProfile();
				profile.Reset(TickSize);
			}

			profile.AddBar(High[0], Low[0], Volume[0], MaxProfileLevelsPerBar);

			// rebuild the render snapshot once per new bar
			if (CurrentBar != lastBuiltBar)
			{
				profile.Compute(ValueAreaPercent);
				renderLevels = profile.GetLevels();
				Snap s = new Snap();
				s.Poc = profile.POC; s.Vah = profile.VAH; s.Val = profile.VAL;
				s.MaxVol = profile.MaxLevelVolume; s.Tick = TickSize;
				snapshotBox = s;
				lastBuiltBar = CurrentBar;

				// POC / value-area reference lines (NinjaScript drawing, with labels)
				if (ShowPocLine && !double.IsNaN(profile.POC))
					Draw.HorizontalLine(this, "vp_poc", profile.POC, Brushes.Orange, DashStyleHelper.Solid, 2);
				if (ShowValueAreaLines && !double.IsNaN(profile.VAH) && !double.IsNaN(profile.VAL))
				{
					Draw.HorizontalLine(this, "vp_vah", profile.VAH, Brushes.SlateGray, DashStyleHelper.Dot, 1);
					Draw.HorizontalLine(this, "vp_val", profile.VAL, Brushes.SlateGray, DashStyleHelper.Dot, 1);
				}
			}
		}

		// --------------------------------------------------------------------
		//  Pixel-space histogram. Runs on the render thread.
		// --------------------------------------------------------------------
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null) return;

			List<KeyValuePair<double, double>> levels = renderLevels;
			Snap s = snapshotBox as Snap;
			if (levels == null || s == null || s.MaxVol <= 0) return;

			float panelLeft		= (float)ChartPanel.X;
			float panelRight	= (float)(ChartPanel.X + ChartPanel.W);
			float panelTop		= (float)ChartPanel.Y;
			float panelBottom	= (float)(ChartPanel.Y + ChartPanel.H);
			float maxWidth		= (float)(ChartPanel.W * WidthPercent);

			// Build WPF brushes for the current opacity, then convert to device
			// brushes via NinjaTrader's ToDxBrush() helper (version-safe, avoids
			// constructing SharpDX color types directly). Dispose the DX brushes per frame.
			byte op			= (byte)Opacity;
			byte opIn		= (byte)Math.Min(255, Opacity + 40);
			Brush mIn		= new SolidColorBrush(Color.FromArgb(opIn, 70, 140, 200)); mIn.Freeze();
			Brush mOut		= new SolidColorBrush(Color.FromArgb(op, 90, 90, 120));   mOut.Freeze();
			Brush mPoc		= new SolidColorBrush(Color.FromArgb(220, 235, 150, 30)); mPoc.Freeze();

			SharpDX.Direct2D1.Brush brushIn  = mIn.ToDxBrush(RenderTarget);
			SharpDX.Direct2D1.Brush brushOut = mOut.ToDxBrush(RenderTarget);
			SharpDX.Direct2D1.Brush brushPoc = mPoc.ToDxBrush(RenderTarget);

			try
			{
				double tick = s.Tick > 0 ? s.Tick : TickSize;
				foreach (KeyValuePair<double, double> lvl in levels)
				{
					double price = lvl.Key;
					double vol   = lvl.Value;
					if (vol <= 0) continue;

					float y		= chartScale.GetYByValue(price);
					if (y < panelTop || y > panelBottom) continue;	// off-screen

					float yNext	= chartScale.GetYByValue(price - tick);
					float h		= Math.Abs(yNext - y);
					if (h < 1f) h = 1f;

					float w		= (float)(vol / s.MaxVol) * maxWidth;
					if (w < 1f) w = 1f;

					float x		= ProfileOnRight ? panelRight - w : panelLeft;

					SharpDX.Direct2D1.Brush b;
					bool isPoc = !double.IsNaN(s.Poc) && Math.Abs(price - s.Poc) < tick * 0.5;
					bool inVa  = !double.IsNaN(s.Vah) && !double.IsNaN(s.Val) && price <= s.Vah + tick * 0.5 && price >= s.Val - tick * 0.5;
					b = isPoc ? brushPoc : (inVa ? brushIn : brushOut);

					RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y - h / 2f, w, h), b);
				}
			}
			finally
			{
				brushIn.Dispose();
				brushOut.Dispose();
				brushPoc.Dispose();
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(0.02, 0.9)]
		[Display(Name = "Profile width (% of panel)", Order = 1, GroupName = "Display")]
		public double WidthPercent { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 0.95)]
		[Display(Name = "Value area percent", Order = 2, GroupName = "Logic")]
		public double ValueAreaPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10000)]
		[Display(Name = "Max profile levels per bar", Order = 3, GroupName = "Logic")]
		public int MaxProfileLevelsPerBar { get; set; }

		[NinjaScriptProperty]
		[Range(10, 255)]
		[Display(Name = "Bar opacity (10-255)", Order = 4, GroupName = "Display")]
		public int Opacity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show POC line", Order = 5, GroupName = "Display")]
		public bool ShowPocLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show value-area lines", Order = 6, GroupName = "Display")]
		public bool ShowValueAreaLines { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Profile on right side", Order = 7, GroupName = "Display")]
		public bool ProfileOnRight { get; set; }
		#endregion
	}
}
