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
//  INDICATOR 3 — Trade Quality / Risk Filter
// ----------------------------------------------------------------------------
//  Grades a proposed trade BEFORE entry (A / B / C / D / Skip) so you can pass
//  on low-quality setups. Two modes:
//    Manual : you supply direction + entry/stop/target; it grades them.
//    Auto   : it estimates entry (current price), a structural stop (beyond the
//             recent swing / key level) and a target (nearest opposing level or
//             an R-multiple), then grades that plan.
//
//  It consumes the shared SuiteContext: market regime from Indicator 1 and the
//  latest order-flow event from Indicator 2 (both OPTIONAL — if those indicators
//  aren't on the chart, the related rules are simply skipped).
//
//  This is a decision-support tool, NOT automated trading advice. It never
//  submits orders. All math uses closed-bar / current data only (no lookahead).
// ============================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
	public class TradeQuality : Indicator
	{
		private SwingLevelDetector	swings;
		private ContextState		ctx;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"Grades a proposed trade (A/B/C/D/Skip) on reward/risk, location, regime alignment, order-flow confirmation and volatility. Part of OrderFlowSuite.";
				Name				= "OFS_TradeQuality";
				Calculate			= Calculate.OnEachTick;
				IsOverlay			= true;
				DisplayInDataBox	= false;
				DrawOnPricePanel	= true;
				PaintPriceMarkers	= false;
				IsSuspendedWhileInactive = true;

				Mode				= TradePlanMode.Auto;
				ManualDirection		= TradeDirection.None;
				ManualEntry			= 0;
				ManualStop			= 0;
				ManualTarget1		= 0;
				ManualTarget2		= 0;
				AccountRiskDollars	= 0;
				PointValue			= 0;	// 0 = auto-detect from instrument
				ATRPeriod			= 14;
				MaxStopATR			= 1.25;
				MinRewardRisk		= 1.5;
				VWAPExtensionATR	= 1.5;
				SwingLookback		= 10;
				StopPadTicks		= 4;
				NearVwapTicks		= 8;
				ShowTradeLines		= true;
				ShowPanel			= true;
				PanelPosition		= TextPosition.TopLeft;
				AlertOnGradeAtOrAbove = TradeGrade.B;
				EnableAlerts		= false;
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

		private TradeGrade lastAlertGrade = TradeGrade.Skip;

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			if (CurrentBar < Math.Max(ATRPeriod, SwingLookback * 2) + 2) return;

			swings.Update(High, Low, CurrentBar);

			double atr		= ATR(ATRPeriod)[0];
			double atrAvg	= SMA(ATR(ATRPeriod), 50)[0];
			if (atr <= 0) return;

			// ---- build the trade plan (manual or auto) ----
			TradeDirection dir;
			double entry, stop, target1, target2;
			bool haveTarget;
			BuildPlan(atr, out dir, out entry, out stop, out target1, out target2, out haveTarget);

			if (dir == TradeDirection.None || entry <= 0 || stop <= 0)
			{
				if (ShowPanel) DrawEmptyPanel();
				RemoveTradeLines();
				return;
			}

			// ---- grade it ----
			TradeQualityScore q = new TradeQualityScore();
			double riskPts		= Math.Abs(entry - stop);
			double rewardPts	= haveTarget ? Math.Abs(target1 - entry) : 0;
			double rr			= riskPts > 0 && haveTarget ? rewardPts / riskPts : 0;

			GradeRewardRisk(q, rr, haveTarget);
			GradeVwap(q, dir, entry, atr);
			GradeStop(q, dir, entry, stop, atr);
			GradeTarget(q, dir, entry, target1, haveTarget);
			GradeRegime(q, dir);
			GradeOrderFlow(q, dir, entry);
			VolatilityState vol = GradeVolatility(q, atr, atrAvg);

			// ---- output ----
			if (ShowTradeLines)
				DrawTradeLines(dir, entry, stop, target1, target2, haveTarget);
			else
				RemoveTradeLines();

			if (ShowPanel)
				DrawPanel(q, dir, entry, stop, target1, target2, haveTarget, riskPts, rewardPts, rr, atr, vol);

			// ---- alert when a fresh bar produces a good grade ----
			if (EnableAlerts && State == State.Realtime && IsFirstTickOfBar
				&& q.Grade >= AlertOnGradeAtOrAbove && q.Grade != lastAlertGrade)
			{
				Alert("ofsGrade", Priority.High,
					"Trade grade " + q.Grade + " (" + dir + ", R/R " + rr.ToString("0.0") + ")",
					"", 30, Brushes.Black, Brushes.White);
			}
			if (IsFirstTickOfBar) lastAlertGrade = q.Grade;
		}

		// --------------------------------------------------------------------
		//  Plan construction
		// --------------------------------------------------------------------
		private void BuildPlan(double atr, out TradeDirection dir, out double entry,
			out double stop, out double target1, out double target2, out bool haveTarget)
		{
			SessionLevels lv = HydrateLevels();

			if (Mode == TradePlanMode.Manual)
			{
				dir		= ManualDirection;
				entry	= ManualEntry > 0 ? ManualEntry : Close[0];
				if (dir == TradeDirection.None) { stop = 0; target1 = 0; target2 = 0; haveTarget = false; return; }

				stop	= ManualStop > 0 ? ManualStop : AutoStop(dir, entry, atr, lv);
				if (ManualTarget1 > 0) { target1 = ManualTarget1; haveTarget = true; }
				else { target1 = AutoTarget(dir, entry, stop, lv, out haveTarget); }
				target2	= ManualTarget2 > 0 ? ManualTarget2 : double.NaN;
				return;
			}

			// ---- AUTO ----
			dir		= AutoDirection(lv);
			entry	= Close[0];
			if (dir == TradeDirection.None) { stop = 0; target1 = 0; target2 = double.NaN; haveTarget = false; return; }

			stop	= AutoStop(dir, entry, atr, lv);
			target1	= AutoTarget(dir, entry, stop, lv, out haveTarget);

			// target2 = next opposing level beyond target1, else 2R
			double risk = Math.Abs(entry - stop);
			if (dir == TradeDirection.Long)
			{
				KeyLevel above = lv.NearestAbove(target1);
				target2 = above != null ? above.Price : entry + 2.0 * risk;
			}
			else
			{
				KeyLevel below = lv.NearestBelow(target1);
				target2 = below != null ? below.Price : entry - 2.0 * risk;
			}
		}

		private TradeDirection AutoDirection(SessionLevels lv)
		{
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
				case MarketMode.Balanced:
				case MarketMode.ReversalRisk:
					// fade the nearer extreme
					if (!double.IsNaN(lv.SessionHigh) && !double.IsNaN(lv.SessionLow))
					{
						double mid = (lv.SessionHigh + lv.SessionLow) / 2.0;
						return Close[0] >= mid ? TradeDirection.Short : TradeDirection.Long;
					}
					return TradeDirection.None;
				default:
					return TradeDirection.None;
			}
		}

		private double AutoStop(TradeDirection dir, double entry, double atr, SessionLevels lv)
		{
			double pad = StopPadTicks * TickSize;
			if (dir == TradeDirection.Long)
			{
				double swing = swings.HasLow() ? swings.LastSwingLow : entry - atr;
				KeyLevel support = lv.NearestBelow(entry);
				double basis = swing;
				if (support != null && support.Price < entry && support.Price > swing) basis = support.Price;
				return Math.Min(swing, basis) - pad;
			}
			else
			{
				double swing = swings.HasHigh() ? swings.LastSwingHigh : entry + atr;
				KeyLevel resistance = lv.NearestAbove(entry);
				double basis = swing;
				if (resistance != null && resistance.Price > entry && resistance.Price < swing) basis = resistance.Price;
				return Math.Max(swing, basis) + pad;
			}
		}

		private double AutoTarget(TradeDirection dir, double entry, double stop, SessionLevels lv, out bool haveTarget)
		{
			double risk = Math.Abs(entry - stop);
			if (dir == TradeDirection.Long)
			{
				KeyLevel above = lv.NearestAbove(entry);
				if (above != null && (above.Price - entry) >= risk * 0.5) { haveTarget = true; return above.Price; }
				haveTarget = true;	// fall back to R-multiple
				return entry + MinRewardRisk * risk;
			}
			else
			{
				KeyLevel below = lv.NearestBelow(entry);
				if (below != null && (entry - below.Price) >= risk * 0.5) { haveTarget = true; return below.Price; }
				haveTarget = true;
				return entry - MinRewardRisk * risk;
			}
		}

		// --------------------------------------------------------------------
		//  Grading rules (start 100, apply penalties/bonuses)
		// --------------------------------------------------------------------
		private void GradeRewardRisk(TradeQualityScore q, double rr, bool haveTarget)
		{
			if (!haveTarget) { q.Penalty(20, "No clear target"); return; }
			if (rr >= 2.0) { /* no penalty */ }
			else if (rr >= 1.5) q.Penalty(10, "Modest R/R (1.5-2.0)");
			else if (rr >= 1.0) q.Penalty(25, "Poor R/R (1.0-1.5)");
			else q.Penalty(40, "Poor R/R (<1.0)");
		}

		private void GradeVwap(TradeQualityScore q, TradeDirection dir, double entry, double atr)
		{
			if (ctx == null || double.IsNaN(ctx.Vwap)) return;
			double extAtr = (entry - ctx.Vwap) / atr;	// + = above VWAP
			double dirExt = dir == TradeDirection.Long ? extAtr : -extAtr;	// extension in trade direction

			if (dirExt > VWAPExtensionATR)
				q.Penalty(20, "Chasing: extended from VWAP");
			else
			{
				int distTicks = (int)Math.Round(Math.Abs(entry - ctx.Vwap) / TickSize);
				bool favorablePullback = distTicks <= NearVwapTicks &&
					((dir == TradeDirection.Long && ctx.Mode == MarketMode.TrendUp) ||
					 (dir == TradeDirection.Short && ctx.Mode == MarketMode.TrendDown));
				if (favorablePullback) q.Bonus(5, "Pullback near VWAP");
			}
		}

		private void GradeStop(TradeQualityScore q, TradeDirection dir, double entry, double stop, double atr)
		{
			double risk = Math.Abs(entry - stop);
			if (risk > MaxStopATR * atr)
				q.Penalty(20, "Stop too wide");
			else if (risk < 0.20 * atr)
				q.Penalty(15, "Stop too tight (inside noise)");
			// else: assumed to sit beyond logical structure (auto-stop does this) -> no penalty
		}

		private void GradeTarget(TradeQualityScore q, TradeDirection dir, double entry, double target1, bool haveTarget)
		{
			if (!haveTarget) return;	// already penalized in R/R rule
			SessionLevels lv = HydrateLevels();
			if (dir == TradeDirection.Long)
			{
				KeyLevel opposing = lv.NearestAbove(entry);
				if (opposing != null && opposing.Price < target1 - TickSize)
					q.Penalty(20, "Trade into major level (before target)");
			}
			else
			{
				KeyLevel opposing = lv.NearestBelow(entry);
				if (opposing != null && opposing.Price > target1 + TickSize)
					q.Penalty(20, "Trade into major level (before target)");
			}
		}

		private void GradeRegime(TradeQualityScore q, TradeDirection dir)
		{
			if (ctx == null) return;
			MarketMode m = ctx.Mode;

			if (dir == TradeDirection.Long && (m == MarketMode.TrendUp || m == MarketMode.BreakoutUp))
				q.Bonus(10, "Aligned with up regime");
			else if (dir == TradeDirection.Short && (m == MarketMode.TrendDown || m == MarketMode.BreakoutDown))
				q.Bonus(10, "Aligned with down regime");
			else if (m == MarketMode.Balanced)
				q.Bonus(10, "Fade in balance");

			// trading against a strong trend
			if ((dir == TradeDirection.Long && m == MarketMode.TrendDown) ||
				(dir == TradeDirection.Short && m == MarketMode.TrendUp))
				q.Penalty(25, "Against market regime");

			// Entering the MIDDLE of a balance/IB range is low quality: in balance you
			// want to fade the extremes, not chase the middle. Penalize mid-range entries.
			if (m == MarketMode.Balanced && !double.IsNaN(ctx.IbHigh) && !double.IsNaN(ctx.IbLow) && ctx.IbHigh > ctx.IbLow)
			{
				double pad = KeyLevelPadTicks();
				bool nearEdge = (Close[0] >= ctx.IbHigh - pad) || (Close[0] <= ctx.IbLow + pad);
				if (!nearEdge)
					q.Penalty(20, "Inside chop/balance");
			}
		}

		// pad (in price) used to decide "near a range edge"
		private double KeyLevelPadTicks()
		{
			return NearVwapTicks * TickSize;
		}

		private void GradeOrderFlow(TradeQualityScore q, TradeDirection dir, double entry)
		{
			if (ctx == null || !ctx.EventIsRecent(Times[0][0], 600)) return;
			OrderFlowEventType ev = ctx.LastEvent;

			bool bullishEvent = ev == OrderFlowEventType.BullishAbsorption || ev == OrderFlowEventType.BullishExhaustion;
			bool bearishEvent = ev == OrderFlowEventType.BearishAbsorption || ev == OrderFlowEventType.BearishExhaustion;

			if (dir == TradeDirection.Long && bullishEvent)
				q.Bonus(10, "Bullish order-flow confirmation");
			else if (dir == TradeDirection.Short && bearishEvent)
				q.Bonus(10, "Bearish order-flow confirmation");
			else if (dir == TradeDirection.Long && ev == OrderFlowEventType.BearishAbsorption)
				q.Penalty(20, "Long into bearish absorption");
			else if (dir == TradeDirection.Short && ev == OrderFlowEventType.BullishAbsorption)
				q.Penalty(20, "Short into bullish absorption");
		}

		private VolatilityState GradeVolatility(TradeQualityScore q, double atr, double atrAvg)
		{
			VolatilityState vol = VolatilityHelper.Classify(atr, atrAvg, 0.80, 1.25);
			if (vol == VolatilityState.Compressed)
			{
				bool breakoutConfirmed = ctx != null && (ctx.Mode == MarketMode.BreakoutUp || ctx.Mode == MarketMode.BreakoutDown);
				if (!breakoutConfirmed) q.Penalty(10, "Compressed, no breakout confirmation");
			}
			else if (vol == VolatilityState.Expanded)
				q.Penalty(15, "Expanded vol / slippage risk");
			return vol;
		}

		// --------------------------------------------------------------------
		//  Drawing
		// --------------------------------------------------------------------
		private void DrawTradeLines(TradeDirection dir, double entry, double stop, double t1, double t2, bool haveTarget)
		{
			Draw.HorizontalLine(this, "ofsq_entry", entry, Brushes.Goldenrod, DashStyleHelper.Solid, 1);
			Draw.HorizontalLine(this, "ofsq_stop", stop, Brushes.Red, DashStyleHelper.Dash, 1);
			if (haveTarget)
				Draw.HorizontalLine(this, "ofsq_t1", t1, Brushes.Lime, DashStyleHelper.Dash, 1);
			if (!double.IsNaN(t2))
				Draw.HorizontalLine(this, "ofsq_t2", t2, Brushes.SeaGreen, DashStyleHelper.Dot, 1);
		}

		private void RemoveTradeLines()
		{
			RemoveDrawObject("ofsq_entry");
			RemoveDrawObject("ofsq_stop");
			RemoveDrawObject("ofsq_t1");
			RemoveDrawObject("ofsq_t2");
		}

		private static Brush Frozen(byte a, byte r, byte g, byte b)
		{
			SolidColorBrush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
			br.Freeze();	// required: created on calc thread, rendered on UI thread
			return br;
		}

		private Brush GradeBrush(TradeGrade g)
		{
			switch (g)
			{
				case TradeGrade.A: return Frozen(160, 0, 130, 40);
				case TradeGrade.B: return Frozen(160, 60, 110, 30);
				case TradeGrade.C: return Frozen(160, 130, 110, 0);
				case TradeGrade.D: return Frozen(160, 140, 60, 0);
				default:           return Frozen(160, 140, 20, 20);
			}
		}

		private double PointVal()
		{
			if (PointValue > 0) return PointValue;
			if (Instrument != null && Instrument.MasterInstrument != null) return Instrument.MasterInstrument.PointValue;
			return 0;
		}

		private void DrawPanel(TradeQualityScore q, TradeDirection dir, double entry, double stop,
			double t1, double t2, bool haveTarget, double riskPts, double rewardPts, double rr,
			double atr, VolatilityState vol)
		{
			double pv = PointVal();
			int riskTicks = (int)Math.Round(riskPts / TickSize);
			double riskDollars = riskPts * pv;
			int distToLevel; SessionLevels lv = HydrateLevels();
			KeyLevel near = lv.Nearest(entry, TickSize, out distToLevel);
			double vwapDistAtr = (ctx != null && !double.IsNaN(ctx.Vwap) && atr > 0) ? (entry - ctx.Vwap) / atr : 0;

			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			sb.AppendLine("TRADE QUALITY   [" + q.Grade + "]   score " + q.DisplayScore);
			sb.AppendLine("Dir:    " + dir);
			sb.AppendLine("Entry:  " + entry.ToString("0.00") + "   Stop: " + stop.ToString("0.00"));
			if (haveTarget) sb.AppendLine("T1:     " + t1.ToString("0.00") + (double.IsNaN(t2) ? "" : "   T2: " + t2.ToString("0.00")));
			else sb.AppendLine("T1:     (none)");
			sb.AppendLine("Risk:   " + riskPts.ToString("0.00") + " pt / " + riskTicks + " tk" + (pv > 0 ? "  ($" + riskDollars.ToString("0") + "/contract)" : ""));
			if (haveTarget) sb.AppendLine("Reward: " + rewardPts.ToString("0.00") + " pt   R/R " + rr.ToString("0.00"));
			sb.AppendLine("VWAP:   " + vwapDistAtr.ToString("+0.0;-0.0") + " ATR   Vol: " + vol);
			if (near != null) sb.AppendLine("Nearest level: " + near.Name + " (" + distToLevel + " tk)");
			if (AccountRiskDollars > 0 && riskDollars > 0)
				sb.AppendLine("Size for $" + AccountRiskDollars.ToString("0") + ": " + Math.Floor(AccountRiskDollars / riskDollars) + " contract(s)");

			if (q.Warnings.Count > 0)
			{
				sb.AppendLine("-- warnings --");
				foreach (string w in q.Warnings) sb.AppendLine("x " + w);
			}
			if (q.Positives.Count > 0)
			{
				foreach (string p in q.Positives) sb.AppendLine("+ " + p);
			}

			Draw.TextFixed(this, "ofsTradePanel", sb.ToString(), PanelPosition,
				Brushes.White, new SimpleFont("Consolas", 12), Brushes.DimGray, GradeBrush(q.Grade), 85);
		}

		private void DrawEmptyPanel()
		{
			Draw.TextFixed(this, "ofsTradePanel",
				"TRADE QUALITY\nNo trade plan.\n" +
				(Mode == TradePlanMode.Manual ? "Enter direction/entry to grade." : "Auto: waiting for a directional regime."),
				PanelPosition, Brushes.White, new SimpleFont("Consolas", 12), Brushes.DimGray,
				Frozen(150, 50, 50, 50), 80);
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
			// add local swing context so auto mode still works without Indicator 1
			if (swings.HasHigh()) lv.SessionHigh = swings.LastSwingHigh;
			if (swings.HasLow())  lv.SessionLow  = swings.LastSwingLow;
			return lv;
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Mode (Auto/Manual)", Order = 1, GroupName = "Trade Plan")]
		public TradePlanMode Mode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Manual direction", Order = 2, GroupName = "Trade Plan")]
		public TradeDirection ManualDirection { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Manual entry (0 = auto)", Order = 3, GroupName = "Trade Plan")]
		public double ManualEntry { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Manual stop (0 = auto)", Order = 4, GroupName = "Trade Plan")]
		public double ManualStop { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Manual target 1 (0 = auto)", Order = 5, GroupName = "Trade Plan")]
		public double ManualTarget1 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Manual target 2 (0 = none)", Order = 6, GroupName = "Trade Plan")]
		public double ManualTarget2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Account risk dollars (0 = off)", Order = 7, GroupName = "Trade Plan")]
		public double AccountRiskDollars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Point value (0 = auto)", Order = 8, GroupName = "Trade Plan")]
		public double PointValue { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR period", Order = 1, GroupName = "Logic")]
		public int ATRPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Max stop (ATR mult)", Order = 2, GroupName = "Logic")]
		public double MaxStopATR { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Min reward/risk", Order = 3, GroupName = "Logic")]
		public double MinRewardRisk { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "VWAP extension (ATR mult)", Order = 4, GroupName = "Logic")]
		public double VWAPExtensionATR { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Swing lookback (bars)", Order = 5, GroupName = "Logic")]
		public int SwingLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Stop pad (ticks)", Order = 6, GroupName = "Logic")]
		public int StopPadTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Near-VWAP threshold (ticks)", Order = 7, GroupName = "Logic")]
		public int NearVwapTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show trade lines", Order = 1, GroupName = "Display")]
		public bool ShowTradeLines { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show panel", Order = 2, GroupName = "Display")]
		public bool ShowPanel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Panel position", Order = 3, GroupName = "Display")]
		public TextPosition PanelPosition { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Alert on grade at/above", Order = 1, GroupName = "Alerts")]
		public TradeGrade AlertOnGradeAtOrAbove { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable alerts", Order = 2, GroupName = "Alerts")]
		public bool EnableAlerts { get; set; }
		#endregion
	}
}
