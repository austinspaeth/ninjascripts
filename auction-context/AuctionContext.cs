#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
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
//  INDICATOR 1 — Auction Context / Market Regime
// ----------------------------------------------------------------------------
//  Tells you what KIND of market you are in (trend / balance / breakout /
//  failed breakout / reversal risk), where price is located relative to the key
//  references, and which trade type the context favors. It does NOT give buy/sell
//  signals — it sets the bias for the rest of the suite.
//
//  Publishes its regime + levels to SuiteContext so Indicators 2 and 3 can use
//  them. All decisions use only closed-bar data (no lookahead). Failed-breakout
//  detection is inherently lagged by design: it can only be known AFTER price
//  closes back inside, so it is reported on the bar that confirms it.
//
//  DATA: works on any feed. Volume profile (POC/VAH/VAL) is an approximation
//  from bar data (see VolumeProfile.cs); a fallback using session range + VWAP
//  is used when UseVolumeProfile is off.
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class AuctionContext : Indicator
	{
		// shared helpers
		private VWAPCalculator	vwapCalc;
		private VolumeProfile	profileCalc;
		private VolumeProfile	priorProfileCalc;
		private SessionLevels	levels;
		private SwingLevelDetector swings;
		private Series<double>	vwapSeries;
		private ContextState	ctx;

		// session bookkeeping
		private DateTime	sessionStart		= DateTime.MinValue;
		private bool		ibComplete			= false;
		private TimeSpan	rthOpen				= new TimeSpan(9, 30, 0);

		// market-structure memory (confirmed swings, no lookahead)
		private double	prevSwingHigh	= double.NaN;
		private double	prevSwingLow	= double.NaN;

		// breakout state machine
		private int		breakoutDir			= 0;	// +1 up, -1 down, 0 none
		private int		breakoutBar			= -1;
		private double	breakoutLevel		= double.NaN;
		private bool	breakoutConfirmed	= false;

		private MarketMode	lastPublishedMode = MarketMode.Unknown;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"Auction context / market regime panel: trend, balance, breakout, failed breakout, reversal risk, price location, and preferred trade type. Part of OrderFlowSuite.";
				Name				= "OFS_AuctionContext";
				Calculate			= Calculate.OnBarClose;	// regime is a closed-bar decision
				IsOverlay			= true;
				DisplayInDataBox	= false;
				DrawOnPricePanel	= true;
				PaintPriceMarkers	= false;
				IsSuspendedWhileInactive = true;

				ShowPanel			= true;
				PanelPosition		= TextPosition.TopRight;
				ColorBackground		= true;
				UseVWAP				= true;
				UseVolumeProfile	= true;
				UseOvernightLevels	= true;
				RTHOpenTime			= "09:30";
				InitialBalanceMinutes = 60;
				VWAPSlopeLookback	= 20;
				VWAPNearThresholdTicks = 8;
				VWAPExtensionATR	= 1.5;
				BreakoutConfirmBars	= 2;
				FailedBreakoutBars	= 5;
				ATRPeriod			= 14;
				SwingStrength		= 4;
				ValueAreaPercent	= 0.70;
				MaxProfileLevelsPerBar = 100;
				AlertOnRegimeChange	= false;
			}
			else if (State == State.Configure)
			{
				// Daily series for prior-day high/low (robust across session templates).
				AddDataSeries(BarsPeriodType.Day, 1);
			}
			else if (State == State.DataLoaded)
			{
				vwapCalc			= new VWAPCalculator();
				profileCalc			= new VolumeProfile();
				priorProfileCalc	= new VolumeProfile();
				levels				= new SessionLevels();
				swings				= new SwingLevelDetector(SwingStrength);
				vwapSeries			= new Series<double>(this);
				ModeCodeSeries		= new Series<double>(this);

				profileCalc.Reset(TickSize);
				priorProfileCalc.Reset(TickSize);

				TimeSpan parsed;
				if (TimeSpan.TryParse(RTHOpenTime, out parsed))
					rthOpen = parsed;
			}
			else if (State == State.Historical)
			{
				ctx = SuiteContext.Get(Instrument != null ? Instrument.FullName : "__default__");
			}
		}

		protected override void OnBarUpdate()
		{
			// Daily series only feeds prior-day levels; primary series drives logic.
			if (BarsInProgress != 0)
				return;

			if (CurrentBars[0] < ATRPeriod || CurrentBars[0] < SwingStrength * 2)
				return;

			// ---- prior-day high/low from the daily series (index 1 = last completed day) ----
			if (CurrentBars[1] >= 1)
			{
				levels.PriorDayHigh	= Highs[1][1];
				levels.PriorDayLow	= Lows[1][1];
			}

			// ---- session rollover ----
			if (Bars.IsFirstBarOfSession)
			{
				// roll the developing profile into "prior" and start fresh
				if (profileCalc.IsReady())
				{
					priorProfileCalc = profileCalc;
					priorProfileCalc.Compute(ValueAreaPercent);
					levels.PriorVAH = priorProfileCalc.VAH;
					levels.PriorVAL = priorProfileCalc.VAL;
					levels.PriorPOC = priorProfileCalc.POC;
				}

				vwapCalc.Reset();
				profileCalc = new VolumeProfile();
				profileCalc.Reset(TickSize);
				levels.StartSession(Open[0]);

				sessionStart		= Times[0][0];
				ibComplete			= false;
				breakoutDir			= 0;
				breakoutConfirmed	= false;
				breakoutLevel		= double.NaN;
				levels.OvernightHigh = double.NaN;
				levels.OvernightLow  = double.NaN;
			}

			// ---- per-bar updates ----
			levels.UpdateSessionExtremes(High[0], Low[0]);

			double typical = (High[0] + Low[0] + Close[0]) / 3.0;
			vwapCalc.AddBar(typical, Volume[0]);
			levels.Vwap = vwapCalc.Value;
			vwapSeries[0] = double.IsNaN(vwapCalc.Value) ? Close[0] : vwapCalc.Value;

			profileCalc.AddBar(High[0], Low[0], Volume[0], MaxProfileLevelsPerBar);
			if (UseVolumeProfile)
			{
				profileCalc.Compute(ValueAreaPercent);
				levels.POC = profileCalc.POC;
				levels.VAH = profileCalc.VAH;
				levels.VAL = profileCalc.VAL;
			}

			// ---- initial balance (first N minutes of the session) ----
			if (sessionStart != DateTime.MinValue && !ibComplete)
			{
				if ((Times[0][0] - sessionStart).TotalMinutes <= InitialBalanceMinutes)
					levels.UpdateInitialBalance(High[0], Low[0]);
				else
					ibComplete = true;
			}

			// ---- overnight levels (session bars before RTH open) ----
			if (UseOvernightLevels && Times[0][0].TimeOfDay < rthOpen)
			{
				if (double.IsNaN(levels.OvernightHigh) || High[0] > levels.OvernightHigh) levels.OvernightHigh = High[0];
				if (double.IsNaN(levels.OvernightLow)  || Low[0]  < levels.OvernightLow)  levels.OvernightLow  = Low[0];
			}

			// ---- market structure: track last two confirmed swings ----
			double prevHigh = swings.LastSwingHigh;
			double prevLow  = swings.LastSwingLow;
			if (swings.Update(High, Low, CurrentBars[0]))
			{
				if (!double.IsNaN(swings.LastSwingHigh) && swings.LastSwingHigh != prevHigh && !double.IsNaN(prevHigh))
					prevSwingHigh = prevHigh;
				if (!double.IsNaN(swings.LastSwingLow) && swings.LastSwingLow != prevLow && !double.IsNaN(prevLow))
					prevSwingLow = prevLow;
			}

			// ---- compute regime ----
			double atr		= ATR(ATRPeriod)[0];
			MarketMode mode;
			PreferredTradeType pref;
			ComputeRegime(atr, out mode, out pref);

			// ---- publish to the shared blackboard ----
			levels.Vwap = vwapCalc.Value;
			if (ctx != null)
			{
				ctx.Mode		= mode;
				ctx.Preferred	= pref;
				ctx.ModeTime	= Times[0][0];
				ctx.Vwap		= vwapCalc.Value;
				ctx.PriorHigh	= levels.PriorDayHigh;
				ctx.PriorLow	= levels.PriorDayLow;
				ctx.Poc			= levels.POC;
				ctx.Vah			= levels.VAH;
				ctx.Val			= levels.VAL;
				ctx.IbHigh		= levels.IbHigh;
				ctx.IbLow		= levels.IbLow;
				ctx.Atr			= atr;
			}

			// ---- alert on regime change ----
			if (AlertOnRegimeChange && State == State.Realtime && mode != lastPublishedMode && mode != MarketMode.Unknown)
			{
				Alert("ofsRegime", Priority.Medium,
					"Auction context: " + ModeText(mode), "", 30, Brushes.DimGray, Brushes.White);
			}
			lastPublishedMode = mode;

			// MarketMode as a numeric code, kept on the public Series for any
			// strategy/export that wants to read it (no chart plot -> no autoscale impact).
			ModeCodeSeries[0] = (int)mode;

			// ---- draw the panel ----
			if (ShowPanel)
				DrawPanel(mode, pref, atr);
		}

		// --------------------------------------------------------------------
		//  Regime heuristic. Returns the dominant mode + preferred trade type.
		// --------------------------------------------------------------------
		private void ComputeRegime(double atr, out MarketMode mode, out PreferredTradeType pref)
		{
			mode = MarketMode.Unknown;
			pref = PreferredTradeType.NoTrade;

			double vwap = vwapCalc.Value;
			if (double.IsNaN(vwap) || atr <= 0)
				return;

			double close = Close[0];

			// VWAP slope (in ticks over the lookback) with a small flat deadband
			double slopeTicks = 0;
			if (CurrentBars[0] > VWAPSlopeLookback && !double.IsNaN(vwapSeries[VWAPSlopeLookback]))
				slopeTicks = (vwapSeries[0] - vwapSeries[VWAPSlopeLookback]) / TickSize;
			double flat = Math.Max(1.0, VWAPNearThresholdTicks / 2.0);
			int slopeSign = slopeTicks > flat ? 1 : (slopeTicks < -flat ? -1 : 0);

			// extension from VWAP in ATR units
			double extAtr = (close - vwap) / atr;

			// market structure
			bool higherStructure = !double.IsNaN(prevSwingHigh) && !double.IsNaN(swings.LastSwingHigh)
									&& swings.LastSwingHigh > prevSwingHigh
									&& !double.IsNaN(prevSwingLow) && !double.IsNaN(swings.LastSwingLow)
									&& swings.LastSwingLow > prevSwingLow;
			bool lowerStructure  = !double.IsNaN(prevSwingHigh) && !double.IsNaN(swings.LastSwingHigh)
									&& swings.LastSwingHigh < prevSwingHigh
									&& !double.IsNaN(prevSwingLow) && !double.IsNaN(swings.LastSwingLow)
									&& swings.LastSwingLow < prevSwingLow;

			// breakout reference levels
			double upLevel   = MaxValid(levels.PriorDayHigh, levels.OvernightHigh, levels.IbHigh);
			double downLevel = MinValid(levels.PriorDayLow,  levels.OvernightLow,  levels.IbLow);

			UpdateBreakoutState(close, upLevel, downLevel);

			// ---- decision tree (most specific first) ----

			// Failed breakout: we had confirmed a breakout, then closed back inside.
			if (breakoutDir > 0 && breakoutConfirmed && !double.IsNaN(breakoutLevel)
				&& close < breakoutLevel && (CurrentBars[0] - breakoutBar) <= FailedBreakoutBars)
			{
				mode = MarketMode.FailedBreakoutUp;
				pref = PreferredTradeType.FailedBreakoutReversal;
				return;
			}
			if (breakoutDir < 0 && breakoutConfirmed && !double.IsNaN(breakoutLevel)
				&& close > breakoutLevel && (CurrentBars[0] - breakoutBar) <= FailedBreakoutBars)
			{
				mode = MarketMode.FailedBreakoutDown;
				pref = PreferredTradeType.FailedBreakoutReversal;
				return;
			}

			// Confirmed breakout still holding
			if (breakoutDir > 0 && breakoutConfirmed && close >= breakoutLevel)
			{
				mode = MarketMode.BreakoutUp;
				pref = PreferredTradeType.BreakoutContinuation;
				return;
			}
			if (breakoutDir < 0 && breakoutConfirmed && close <= breakoutLevel)
			{
				mode = MarketMode.BreakoutDown;
				pref = PreferredTradeType.BreakoutContinuation;
				return;
			}

			// Reversal risk: extended from VWAP AND stalling at a level / recent OF event
			bool nearExtremeLevel = NearAnyLevel(close, VWAPNearThresholdTicks)
									&& (Math.Abs(extAtr) >= VWAPExtensionATR);
			bool recentOfEvent = ctx != null && ctx.EventIsRecent(Times[0][0], 300);
			if ((Math.Abs(extAtr) >= VWAPExtensionATR) && (nearExtremeLevel || recentOfEvent))
			{
				mode = MarketMode.ReversalRisk;
				pref = PreferredTradeType.FadeExtremes;
				return;
			}

			// Trend up / down
			if (close > vwap && slopeSign > 0 && higherStructure)
			{
				mode = MarketMode.TrendUp;
				pref = PreferredTradeType.LongPullbacks;
				return;
			}
			if (close < vwap && slopeSign < 0 && lowerStructure)
			{
				mode = MarketMode.TrendDown;
				pref = PreferredTradeType.ShortPullbacks;
				return;
			}

			// Balanced: flat VWAP, price rotating around it, inside prior value/range
			bool insidePriorRange = !double.IsNaN(levels.PriorDayHigh) && !double.IsNaN(levels.PriorDayLow)
									&& close <= levels.PriorDayHigh && close >= levels.PriorDayLow;
			if (slopeSign == 0 || insidePriorRange)
			{
				mode = MarketMode.Balanced;
				pref = PreferredTradeType.FadeExtremes;
				return;
			}

			// fall back to a soft trend lean if structure is unclear
			if (close > vwap && slopeSign >= 0) { mode = MarketMode.TrendUp;   pref = PreferredTradeType.LongPullbacks; }
			else if (close < vwap && slopeSign <= 0) { mode = MarketMode.TrendDown; pref = PreferredTradeType.ShortPullbacks; }
			else { mode = MarketMode.Balanced; pref = PreferredTradeType.NoTrade; }
		}

		// Track breakout cross + confirmation (holds above/below for N bars).
		private void UpdateBreakoutState(double close, double upLevel, double downLevel)
		{
			// new up-break: cross above upLevel
			if (!double.IsNaN(upLevel) && Close[0] > upLevel && Close[1] <= upLevel)
			{
				breakoutDir = 1; breakoutBar = CurrentBars[0]; breakoutLevel = upLevel; breakoutConfirmed = false;
			}
			else if (!double.IsNaN(downLevel) && Close[0] < downLevel && Close[1] >= downLevel)
			{
				breakoutDir = -1; breakoutBar = CurrentBars[0]; breakoutLevel = downLevel; breakoutConfirmed = false;
			}

			// confirm if price has stayed beyond the level for BreakoutConfirmBars
			if (breakoutDir != 0 && !breakoutConfirmed && (CurrentBars[0] - breakoutBar) >= BreakoutConfirmBars)
			{
				bool held = true;
				for (int i = 0; i < BreakoutConfirmBars; i++)
				{
					if (breakoutDir > 0 && Close[i] < breakoutLevel) held = false;
					if (breakoutDir < 0 && Close[i] > breakoutLevel) held = false;
				}
				if (held) breakoutConfirmed = true;
			}
		}

		// ---- helpers ----
		private bool NearAnyLevel(double price, int ticks)
		{
			int d;
			KeyLevel k = levels.Nearest(price, TickSize, out d);
			return k != null && d <= ticks;
		}

		private static double MaxValid(params double[] vals)
		{
			double m = double.NaN;
			foreach (double v in vals)
				if (!double.IsNaN(v) && (double.IsNaN(m) || v > m)) m = v;
			return m;
		}
		private static double MinValid(params double[] vals)
		{
			double m = double.NaN;
			foreach (double v in vals)
				if (!double.IsNaN(v) && (double.IsNaN(m) || v < m)) m = v;
			return m;
		}

		// ---- price-location summary string ----
		private string PriceLocationText()
		{
			double c = Close[0];
			List<string> parts = new List<string>();

			if (!double.IsNaN(levels.PriorDayHigh) && c > levels.PriorDayHigh) parts.Add("> PD High");
			else if (!double.IsNaN(levels.PriorDayLow) && c < levels.PriorDayLow) parts.Add("< PD Low");
			else parts.Add("Inside PD range");

			if (UseVolumeProfile && !double.IsNaN(levels.VAH) && !double.IsNaN(levels.VAL))
			{
				if (c > levels.VAH) parts.Add("> Value");
				else if (c < levels.VAL) parts.Add("< Value");
				else parts.Add("In Value");
			}

			if (UseVWAP && !double.IsNaN(levels.Vwap))
			{
				int d = (int)Math.Round(Math.Abs(c - levels.Vwap) / TickSize);
				if (d <= VWAPNearThresholdTicks) parts.Add("~VWAP");
				else if (c > levels.Vwap) parts.Add("> VWAP");
				else parts.Add("< VWAP");
			}
			return string.Join("  |  ", parts);
		}

		private static string ModeText(MarketMode m)
		{
			switch (m)
			{
				case MarketMode.TrendUp:			return "TREND UP";
				case MarketMode.TrendDown:			return "TREND DOWN";
				case MarketMode.Balanced:			return "BALANCED / ROTATION";
				case MarketMode.BreakoutUp:			return "BREAKOUT UP";
				case MarketMode.BreakoutDown:		return "BREAKOUT DOWN";
				case MarketMode.FailedBreakoutUp:	return "FAILED BREAKOUT UP";
				case MarketMode.FailedBreakoutDown:	return "FAILED BREAKOUT DOWN";
				case MarketMode.ReversalRisk:		return "REVERSAL RISK";
				default:							return "UNKNOWN / WAIT";
			}
		}

		private static string PrefText(PreferredTradeType p)
		{
			switch (p)
			{
				case PreferredTradeType.LongPullbacks:			return "Long pullbacks";
				case PreferredTradeType.ShortPullbacks:			return "Short pullbacks";
				case PreferredTradeType.FadeExtremes:			return "Fade extremes";
				case PreferredTradeType.BreakoutContinuation:	return "Breakout continuation";
				case PreferredTradeType.FailedBreakoutReversal:	return "Failed-breakout reversal";
				default:									return "No trade / wait";
			}
		}

		private static Brush Frozen(byte a, byte r, byte g, byte b)
		{
			SolidColorBrush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
			br.Freeze();	// required: created on calc thread, rendered on UI thread
			return br;
		}

		private Brush ModeBrush(MarketMode m)
		{
			switch (m)
			{
				case MarketMode.TrendUp:
				case MarketMode.BreakoutUp:			return Frozen(150, 0, 120, 40);
				case MarketMode.TrendDown:
				case MarketMode.BreakoutDown:		return Frozen(150, 150, 20, 20);
				case MarketMode.FailedBreakoutUp:
				case MarketMode.FailedBreakoutDown:
				case MarketMode.ReversalRisk:		return Frozen(150, 150, 110, 0);
				case MarketMode.Balanced:			return Frozen(150, 60, 60, 70);
				default:							return Frozen(150, 40, 40, 40);
			}
		}

		private void DrawPanel(MarketMode mode, PreferredTradeType pref, double atr)
		{
			string vwapStr = double.IsNaN(levels.Vwap) ? "n/a" : levels.Vwap.ToString("0.00");
			double extAtr = (atr > 0 && !double.IsNaN(levels.Vwap)) ? (Close[0] - levels.Vwap) / atr : 0;

			string text =
				"AUCTION CONTEXT\n" +
				"Mode:  " + ModeText(mode) + "\n" +
				"Trade: " + PrefText(pref) + "\n" +
				"Loc:   " + PriceLocationText() + "\n" +
				"VWAP:  " + vwapStr + "   (" + extAtr.ToString("+0.0;-0.0") + " ATR)";

			Brush back = ColorBackground ? ModeBrush(mode) : Brushes.Transparent;
			Draw.TextFixed(this, "ofsContextPanel", text, PanelPosition,
				Brushes.White, new SimpleFont("Consolas", 13), Brushes.DimGray, back, ColorBackground ? 80 : 0);
			// note: Brushes.* statics are already frozen; ModeBrush() returns frozen brushes too.
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Show panel", Order = 1, GroupName = "Display")]
		public bool ShowPanel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Panel position", Order = 2, GroupName = "Display")]
		public TextPosition PanelPosition { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Color background by regime", Order = 3, GroupName = "Display")]
		public bool ColorBackground { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use VWAP", Order = 1, GroupName = "Logic")]
		public bool UseVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use volume profile (POC/VAH/VAL)", Order = 2, GroupName = "Logic")]
		public bool UseVolumeProfile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use overnight levels", Order = 3, GroupName = "Logic")]
		public bool UseOvernightLevels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RTH open time (HH:mm, exchange tz)", Order = 4, GroupName = "Logic")]
		public string RTHOpenTime { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Initial balance minutes", Order = 5, GroupName = "Logic")]
		public int InitialBalanceMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "VWAP slope lookback (bars)", Order = 6, GroupName = "Logic")]
		public int VWAPSlopeLookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "VWAP near threshold (ticks)", Order = 7, GroupName = "Logic")]
		public int VWAPNearThresholdTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "VWAP extension (ATR mult)", Order = 8, GroupName = "Logic")]
		public double VWAPExtensionATR { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Breakout confirm bars", Order = 9, GroupName = "Logic")]
		public int BreakoutConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Failed breakout window (bars)", Order = 10, GroupName = "Logic")]
		public int FailedBreakoutBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR period", Order = 11, GroupName = "Logic")]
		public int ATRPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Swing strength (bars each side)", Order = 12, GroupName = "Logic")]
		public int SwingStrength { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 0.95)]
		[Display(Name = "Value area percent", Order = 13, GroupName = "Logic")]
		public double ValueAreaPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10000)]
		[Display(Name = "Max profile levels per bar", Order = 14, GroupName = "Logic")]
		public int MaxProfileLevelsPerBar { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert on regime change", Order = 1, GroupName = "Alerts")]
		public bool AlertOnRegimeChange { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> ModeCodeSeries { get; private set; }
		#endregion
	}
}
