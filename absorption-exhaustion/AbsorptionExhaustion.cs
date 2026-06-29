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
//  INDICATOR 2 — Absorption / Exhaustion Detector
// ----------------------------------------------------------------------------
//  Flags likely absorption and exhaustion events from order flow:
//      Bid Absorb  : heavy aggressive selling, price refuses to drop (buyers absorb)
//      Ask Absorb  : heavy aggressive buying,  price refuses to rise (sellers absorb)
//      Buy Exhaust : buying spike into a high with no follow-through
//      Sell Exhaust: selling spike into a low with no follow-through
//      Delta Div   : price makes a new extreme but cumulative delta does not
//
//  NON-REPAINTING: an event needs ConfirmationBars closed bars AFTER the event
//  bar to judge follow-through, so each marker is drawn ConfirmationBars later,
//  anchored back on the event bar. It is therefore lagged by ConfirmationBars
//  and never moves once printed.
//
//  ORDER-FLOW DATA (important):
//   - Real bid/ask classification comes from OnMarketData (Last vs best bid/ask).
//   - OnMarketData only fires LIVE, or on historical bars when the chart's
//     "Tick Replay" option is ON (needs downloaded tick data).
//   - Without tick data, set ApproximateDeltaWithoutTicks = true to estimate
//     per-bar delta from bar shape (volume * close-location bias). This is a
//     crude proxy — clearly weaker than real delta — and is labeled as such.
//   - No Order Flow+ subscription is required. (With Order Flow+ Volumetric bars
//     you could feed exact per-price bid/ask volume; that path is isolated to
//     OnMarketData/OrderFlowBarStats and noted in the README.)
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class AbsorptionExhaustion : Indicator
	{
		// live tape state
		private double	currentBid;
		private double	currentAsk;
		private OrderFlowBarStats stats;
		private int		statsBar = -1;

		// per-bar history keyed by absolute bar index
		private readonly List<long>		deltaByBar	= new List<long>();
		private readonly List<long>		cumByBar	= new List<long>();
		private readonly List<bool>		realOfByBar	= new List<bool>();

		private int		sessionStartBar	= 0;
		private long	sessionCumDelta	= 0;
		private int		lastEventBar	= -100;
		private ContextState ctx;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"Detects absorption, exhaustion and delta-divergence order-flow events with non-repainting confirmation. Part of OrderFlowSuite.";
				Name				= "OFS_AbsorptionExhaustion";
				Calculate			= Calculate.OnEachTick;	// need ticks for OnMarketData
				IsOverlay			= true;
				DisplayInDataBox	= false;
				DrawOnPricePanel	= true;
				PaintPriceMarkers	= false;
				// keep reading the tape even when the chart tab is inactive so we
				// don't miss order-flow while you're looking at another window
				IsSuspendedWhileInactive = false;

				EnableAbsorption		= true;
				EnableExhaustion		= true;
				EnableDeltaDivergence	= true;
				UseKeyLevelFilter		= true;
				KeyLevelDistanceTicks	= 8;
				DeltaThreshold			= 1200;
				UseDynamicDeltaThreshold = true;
				DeltaDynamicMultiplier	= 2.0;
				DeltaLookback			= 50;
				VolumeMultiplier		= 1.5;
				MinVolume				= 0;
				ConfirmationBars		= 2;
				MaxAdverseTicks			= 4;
				CloseLocationPercent	= 0.65;
				SwingLookback			= 10;
				ApproximateDeltaWithoutTicks = true;
				ShowLabels				= true;
				ShowConfidence			= true;
				MinConfidenceToShow		= 0;
				AlertOnHighConfidenceEvents = false;
			}
			else if (State == State.DataLoaded)
			{
				stats = new OrderFlowBarStats();
			}
			else if (State == State.Historical)
			{
				ctx = SuiteContext.Get(Instrument != null ? Instrument.FullName : "__default__");
			}
		}

		// --------------------------------------------------------------------
		//  Real-time / Tick-Replay tape reader -> per-bar bid/ask classification
		// --------------------------------------------------------------------
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (stats == null) return;
			if (e.MarketDataType == MarketDataType.Ask) { currentAsk = e.Price; return; }
			if (e.MarketDataType == MarketDataType.Bid) { currentBid = e.Price; return; }
			if (e.MarketDataType != MarketDataType.Last) return;

			if (CurrentBar != statsBar)
			{
				stats.StartBar(e.Price, sessionCumDelta);
				statsBar = CurrentBar;
			}

			bool hasBidAsk = currentBid > 0 && currentAsk > 0;
			stats.AddTrade(e.Price, e.Volume, currentBid, currentAsk, hasBidAsk);
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			if (CurrentBar < 1) return;

			// session reset for cumulative delta
			if (Bars.IsFirstBarOfSession)
			{
				sessionStartBar	= CurrentBar;
				sessionCumDelta	= 0;
			}

			// ensure history lists are long enough for the current bar
			while (deltaByBar.Count <= CurrentBar) { deltaByBar.Add(0); cumByBar.Add(0); realOfByBar.Add(false); }

			// live delta for the forming bar (finalizes naturally once the bar passes)
			bool hadTicks = (statsBar == CurrentBar && stats.TotalVolume > 0);
			long barDelta;
			if (hadTicks)
				barDelta = stats.Delta;
			else if (ApproximateDeltaWithoutTicks)
				barDelta = ApproxDelta(High[0], Low[0], Close[0], Volume[0]);	// proxy when no tick data
			else
				barDelta = 0;

			deltaByBar[CurrentBar]	= barDelta;
			realOfByBar[CurrentBar]	= hadTicks;

			// cumulative delta (running within the session); recompute current live
			long prevCum = (CurrentBar - 1 >= sessionStartBar && CurrentBar - 1 >= 0) ? cumByBar[CurrentBar - 1] : 0;
			cumByBar[CurrentBar] = prevCum + barDelta;
			sessionCumDelta = cumByBar[CurrentBar];

			// Only evaluate on a fresh closed bar, with enough confirmation bars after it.
			if (!IsFirstTickOfBar)
				return;

			int eventBar = CurrentBar - 1 - ConfirmationBars;	// bar with ConfirmationBars closed after it
			if (eventBar <= sessionStartBar + SwingLookback)
				return;

			EvaluateEventBar(eventBar);
		}

		// --------------------------------------------------------------------
		//  Evaluate a single, fully-confirmed candidate bar.
		// --------------------------------------------------------------------
		private void EvaluateEventBar(int eventBar)
		{
			if (eventBar - lastEventBar < 1) { /* allow adjacent but de-dupe below */ }

			int eventBarsAgo	= CurrentBar - eventBar;
			double evHigh		= High[eventBarsAgo];
			double evLow		= Low[eventBarsAgo];
			double evClose		= Close[eventBarsAgo];
			double evOpen		= Open[eventBarsAgo];
			double evMid		= (evHigh + evLow) / 2.0;
			double range		= evHigh - evLow;
			long   evDelta		= deltaByBar[eventBar];
			double evVol		= Volume[eventBarsAgo];
			double closeLoc		= range > 0 ? (evClose - evLow) / range : 0.5;

			// recent baselines (use bars strictly BEFORE the event bar -> no lookahead)
			double avgAbsDelta	= AvgAbsDelta(eventBar, DeltaLookback);
			double avgVol		= AvgVolume(eventBarsAgo + 1, DeltaLookback);

			double effDelta		= UseDynamicDeltaThreshold
									? Math.Max(DeltaThreshold, avgAbsDelta * DeltaDynamicMultiplier)
									: DeltaThreshold;
			bool volumeOK		= evVol >= MinVolume && (avgVol <= 0 || evVol >= avgVol * VolumeMultiplier);

			// follow-through over the ConfirmationBars after the event (all closed)
			double minLowAfter	= double.MaxValue, maxHighAfter = double.MinValue;
			bool madeNewLow		= false, madeNewHigh = false;
			bool closedBelowMid	= false, closedAboveMid = false;
			for (int k = 1; k <= ConfirmationBars; k++)
			{
				int ba = eventBarsAgo - k;
				if (ba < 0) break;
				minLowAfter		= Math.Min(minLowAfter, Low[ba]);
				maxHighAfter	= Math.Max(maxHighAfter, High[ba]);
				if (Low[ba]  < evLow)  madeNewLow  = true;
				if (High[ba] > evHigh) madeNewHigh = true;
				if (Close[ba] < evMid) closedBelowMid = true;
				if (Close[ba] > evMid) closedAboveMid = true;
			}

			// new-extreme detection vs prior SwingLookback bars (proxy for swing/session extreme)
			double priorHigh = HighestPrior(eventBarsAgo + 1, SwingLookback);
			double priorLow  = LowestPrior(eventBarsAgo + 1, SwingLookback);
			bool isNewHigh   = evHigh >= priorHigh;
			bool isNewLow    = evLow  <= priorLow;

			// key-level proximity
			SessionLevels lv = HydrateLevels();
			int distTicks; KeyLevel nearLevel = lv.Nearest(evClose, TickSize, out distTicks);
			bool levelsKnown = nearLevel != null;
			bool nearKeyLevel = levelsKnown && distTicks <= KeyLevelDistanceTicks;
			bool keyFilterPass = !UseKeyLevelFilter || !levelsKnown || nearKeyLevel;

			OrderFlowEventType detected = OrderFlowEventType.None;

			// ---------- ABSORPTION ----------
			if (EnableAbsorption)
			{
				// Bullish absorption: heavy SELL delta, price holds / closes up
				bool bullHold = (closeLoc >= CloseLocationPercent) || !madeNewLow;
				bool bullAdverse = minLowAfter == double.MaxValue || minLowAfter >= evLow - MaxAdverseTicks * TickSize;
				if (evDelta <= -effDelta && volumeOK && keyFilterPass && bullHold && bullAdverse)
					detected = OrderFlowEventType.BullishAbsorption;

				// Bearish absorption: heavy BUY delta, price holds / closes down
				bool bearHold = ((1 - closeLoc) >= CloseLocationPercent) || !madeNewHigh;
				bool bearAdverse = maxHighAfter == double.MinValue || maxHighAfter <= evHigh + MaxAdverseTicks * TickSize;
				if (detected == OrderFlowEventType.None && evDelta >= effDelta && volumeOK && keyFilterPass && bearHold && bearAdverse)
					detected = OrderFlowEventType.BearishAbsorption;
			}

			// ---------- EXHAUSTION ----------
			if (detected == OrderFlowEventType.None && EnableExhaustion)
			{
				// Bearish exhaustion: new high + big buy delta + poor continuation
				bool bearPoor = (closeLoc < CloseLocationPercent) || closedBelowMid;
				if (isNewHigh && evDelta >= effDelta && volumeOK && keyFilterPass && bearPoor)
					detected = OrderFlowEventType.BearishExhaustion;

				// Bullish exhaustion: new low + big sell delta + poor continuation
				bool bullPoor = ((1 - closeLoc) < CloseLocationPercent) || closedAboveMid;
				if (detected == OrderFlowEventType.None && isNewLow && evDelta <= -effDelta && volumeOK && keyFilterPass && bullPoor)
					detected = OrderFlowEventType.BullishExhaustion;
			}

			if (detected != OrderFlowEventType.None && eventBar != lastEventBar)
			{
				int conf = ComputeConfidence(detected, evDelta, avgAbsDelta, evVol, avgVol,
					nearKeyLevel, levelsKnown, closeLoc, madeNewHigh, madeNewLow, realOfByBar[eventBar]);
				if (conf >= MinConfidenceToShow)
				{
					DrawEvent(detected, eventBarsAgo, evHigh, evLow, conf, realOfByBar[eventBar]);
					PublishEvent(detected, eventBarsAgo, evClose, conf);
					lastEventBar = eventBar;
				}
			}

			// ---------- DELTA DIVERGENCE ----------
			if (EnableDeltaDivergence)
				CheckDivergence(eventBar, eventBarsAgo, evHigh, evLow, isNewHigh, isNewLow);
		}

		// crude per-bar delta proxy used only when no tick data exists for the bar
		private static long ApproxDelta(double high, double low, double close, double volume)
		{
			double range = high - low;
			double loc = range > 0 ? (close - low) / range : 0.5;	// 0..1
			double bias = (2.0 * loc) - 1.0;						// -1..+1
			return (long)Math.Round(volume * bias);
		}

		private void CheckDivergence(int eventBar, int eventBarsAgo, double evHigh, double evLow, bool isNewHigh, bool isNewLow)
		{
			// compare event extreme + its cum delta vs the prior swing extreme + cum delta
			long evCum = cumByBar[eventBar];

			if (isNewHigh)
			{
				// find prior high within lookback and its cum delta
				double prevHigh = double.MinValue; int prevIdxAgo = -1;
				for (int k = 1; k <= SwingLookback; k++)
				{
					int ba = eventBarsAgo + k;
					if (ba >= CurrentBar) break;
					if (High[ba] > prevHigh) { prevHigh = High[ba]; prevIdxAgo = ba; }
				}
				if (prevIdxAgo > 0)
				{
					int prevBar = CurrentBar - prevIdxAgo;
					if (prevBar >= 0 && prevBar < cumByBar.Count && evHigh > prevHigh && cumByBar[eventBar] < cumByBar[prevBar] && eventBar != lastEventBar)
					{
						DrawEvent(OrderFlowEventType.DeltaDivergence, eventBarsAgo, evHigh, evLow, 50, realOfByBar[eventBar]);
						PublishEvent(OrderFlowEventType.DeltaDivergence, eventBarsAgo, Close[eventBarsAgo], 50);
						lastEventBar = eventBar;
					}
				}
			}
			else if (isNewLow)
			{
				double prevLow = double.MaxValue; int prevIdxAgo = -1;
				for (int k = 1; k <= SwingLookback; k++)
				{
					int ba = eventBarsAgo + k;
					if (ba >= CurrentBar) break;
					if (Low[ba] < prevLow) { prevLow = Low[ba]; prevIdxAgo = ba; }
				}
				if (prevIdxAgo > 0)
				{
					int prevBar = CurrentBar - prevIdxAgo;
					if (prevBar >= 0 && prevBar < cumByBar.Count && evLow < prevLow && cumByBar[eventBar] > cumByBar[prevBar] && eventBar != lastEventBar)
					{
						DrawEvent(OrderFlowEventType.DeltaDivergence, eventBarsAgo, evHigh, evLow, 50, realOfByBar[eventBar]);
						PublishEvent(OrderFlowEventType.DeltaDivergence, eventBarsAgo, Close[eventBarsAgo], 50);
						lastEventBar = eventBar;
					}
				}
			}
		}

		// ---- confidence 0..100 from weighted factors ----
		private int ComputeConfidence(OrderFlowEventType type, long evDelta, double avgAbsDelta,
			double evVol, double avgVol, bool nearLevel, bool levelsKnown, double closeLoc,
			bool madeNewHigh, bool madeNewLow, bool realOf)
		{
			double score = 0;

			// delta strength (0..30)
			double dRatio = avgAbsDelta > 0 ? Math.Abs(evDelta) / avgAbsDelta : 1.0;
			score += Math.Min(30, dRatio * 12);

			// volume strength (0..20)
			double vRatio = avgVol > 0 ? evVol / avgVol : 1.0;
			score += Math.Min(20, vRatio * 10);

			// key level proximity (0..20)
			if (levelsKnown) score += nearLevel ? 20 : 5;
			else score += 8;

			// continuation failure / close location (0..20)
			bool bullish = type == OrderFlowEventType.BullishAbsorption || type == OrderFlowEventType.BullishExhaustion;
			double locFactor = bullish ? closeLoc : (1 - closeLoc);
			score += Math.Min(20, locFactor * 20);

			// context alignment with regime (0..10)
			if (ctx != null)
			{
				bool alignsBull = (ctx.Mode == MarketMode.TrendUp || ctx.Mode == MarketMode.BreakoutUp || ctx.Mode == MarketMode.Balanced || ctx.Mode == MarketMode.FailedBreakoutDown);
				bool alignsBear = (ctx.Mode == MarketMode.TrendDown || ctx.Mode == MarketMode.BreakoutDown || ctx.Mode == MarketMode.Balanced || ctx.Mode == MarketMode.FailedBreakoutUp);
				if (bullish && alignsBull) score += 10;
				else if (!bullish && alignsBear) score += 10;
			}

			// penalty if delta is only an approximation (no real tick data)
			if (!realOf) score *= 0.65;

			if (score < 0) score = 0;
			if (score > 100) score = 100;
			return (int)Math.Round(score);
		}

		// ---- drawing ----
		private void DrawEvent(OrderFlowEventType type, int barsAgo, double evHigh, double evLow, int confidence, bool realOf)
		{
			double off = TickSize * 6;
			string baseTag = "ofsEvt_" + (CurrentBar - barsAgo);
			Brush brush; string label; bool below;

			switch (type)
			{
				case OrderFlowEventType.BullishAbsorption: brush = Brushes.Lime;       label = "Bid Absorb";   below = true;  break;
				case OrderFlowEventType.BearishAbsorption: brush = Brushes.Red;        label = "Ask Absorb";   below = false; break;
				case OrderFlowEventType.BullishExhaustion: brush = Brushes.Aqua;       label = "Sell Exhaust"; below = true;  break;
				case OrderFlowEventType.BearishExhaustion: brush = Brushes.Orange;     label = "Buy Exhaust";  below = false; break;
				default:                                   brush = Brushes.Violet;     label = "Delta Div";    below = false; break;
			}

			if (below)
				Draw.TriangleUp(this, baseTag, false, barsAgo, evLow - off, brush);
			else
				Draw.TriangleDown(this, baseTag, false, barsAgo, evHigh + off, brush);

			if (ShowLabels)
			{
				string txt = label;
				if (ShowConfidence) txt += " " + ConfBand(confidence);
				if (!realOf) txt += "~";	// tilde = approximated delta (no tick data)
				double y = below ? evLow - off * 2 : evHigh + off * 2;
				Draw.Text(this, baseTag + "_t", false, txt, barsAgo, y, 0, brush,
					new SimpleFont("Arial", 9), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}

			if (AlertOnHighConfidenceEvents && State == State.Realtime && confidence >= 67)
				Alert("ofsEvent", Priority.High, "OrderFlow: " + label + " (" + confidence + ")", "", 15, Brushes.Black, brush);
		}

		private void PublishEvent(OrderFlowEventType type, int barsAgo, double price, int confidence)
		{
			if (ctx == null) return;
			ctx.LastEvent		= type;
			ctx.LastEventTime	= Times[0][barsAgo];
			ctx.LastEventPrice	= price;
			ctx.LastEventConfidence = confidence >= 67 ? ConfidenceLevel.High : (confidence >= 34 ? ConfidenceLevel.Medium : ConfidenceLevel.Low);
		}

		private static string ConfBand(int c)
		{
			if (c >= 67) return "H";
			if (c >= 34) return "M";
			return "L";
		}

		// ---- baselines / utilities ----
		private double AvgAbsDelta(int eventBar, int lookback)
		{
			double sum = 0; int n = 0;
			for (int b = eventBar - 1; b >= 0 && n < lookback; b--, n++)
				sum += Math.Abs(deltaByBar[b]);
			return n > 0 ? sum / n : 0;
		}
		private double AvgVolume(int startBarsAgo, int lookback)
		{
			double sum = 0; int n = 0;
			for (int ba = startBarsAgo; ba < CurrentBar && n < lookback; ba++, n++)
				sum += Volume[ba];
			return n > 0 ? sum / n : 0;
		}
		private double HighestPrior(int startBarsAgo, int lookback)
		{
			double h = double.MinValue;
			for (int ba = startBarsAgo; ba < CurrentBar && ba < startBarsAgo + lookback; ba++)
				if (High[ba] > h) h = High[ba];
			return h == double.MinValue ? double.MaxValue : h;
		}
		private double LowestPrior(int startBarsAgo, int lookback)
		{
			double l = double.MaxValue;
			for (int ba = startBarsAgo; ba < CurrentBar && ba < startBarsAgo + lookback; ba++)
				if (Low[ba] < l) l = Low[ba];
			return l == double.MaxValue ? double.MinValue : l;
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
		[Display(Name = "Enable absorption", Order = 1, GroupName = "Events")]
		public bool EnableAbsorption { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable exhaustion", Order = 2, GroupName = "Events")]
		public bool EnableExhaustion { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable delta divergence", Order = 3, GroupName = "Events")]
		public bool EnableDeltaDivergence { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use key-level filter", Order = 1, GroupName = "Filters")]
		public bool UseKeyLevelFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Key-level distance (ticks)", Order = 2, GroupName = "Filters")]
		public int KeyLevelDistanceTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, long.MaxValue)]
		[Display(Name = "Delta threshold", Order = 3, GroupName = "Filters")]
		public long DeltaThreshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use dynamic delta threshold", Order = 4, GroupName = "Filters")]
		public bool UseDynamicDeltaThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Dynamic delta multiplier", Order = 5, GroupName = "Filters")]
		public double DeltaDynamicMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Delta/volume lookback (bars)", Order = 6, GroupName = "Filters")]
		public int DeltaLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Volume multiplier", Order = 7, GroupName = "Filters")]
		public double VolumeMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(0, long.MaxValue)]
		[Display(Name = "Min volume", Order = 8, GroupName = "Filters")]
		public long MinVolume { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Confirmation bars (marker lag)", Order = 9, GroupName = "Filters")]
		public int ConfirmationBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max adverse ticks", Order = 10, GroupName = "Filters")]
		public int MaxAdverseTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 1.0)]
		[Display(Name = "Close-location percent", Order = 11, GroupName = "Filters")]
		public double CloseLocationPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Swing lookback (bars)", Order = 12, GroupName = "Filters")]
		public int SwingLookback { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Approximate delta without ticks", Order = 13, GroupName = "Filters")]
		public bool ApproximateDeltaWithoutTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show labels", Order = 1, GroupName = "Display")]
		public bool ShowLabels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show confidence", Order = 2, GroupName = "Display")]
		public bool ShowConfidence { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Min confidence to show (0-100)", Order = 3, GroupName = "Display")]
		public int MinConfidenceToShow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert on high-confidence events", Order = 1, GroupName = "Alerts")]
		public bool AlertOnHighConfidenceEvents { get; set; }
		#endregion
	}
}
