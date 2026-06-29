// ============================================================================
//  OrderFlowSuite — SuiteContext (shared runtime "blackboard")
// ----------------------------------------------------------------------------
//  Lets the three indicators share live state WITHOUT instantiating each other.
//  Indicator 1 publishes the market regime + session levels; Indicator 2
//  publishes the latest order-flow event; Indicator 3 reads both to grade trades.
//
//  Keyed by instrument full name. If an indicator that would publish a value is
//  not on the chart, readers simply see defaults (Unknown / None) and degrade
//  gracefully — every consumer treats shared data as OPTIONAL.
//
//  LIMITATION: keying by instrument name means two charts of the SAME instrument
//  with different settings share one slot (last writer wins). For a single
//  working chart per instrument — the normal case — this is exactly what we want.
//  Access is locked because NinjaTrader may update different charts on different
//  threads.
// ============================================================================

using System;
using System.Collections.Generic;

namespace OrderFlowSuite
{
	public class ContextState
	{
		// --- published by Indicator 1 (Auction Context) ---
		public MarketMode			Mode			= MarketMode.Unknown;
		public PreferredTradeType	Preferred		= PreferredTradeType.NoTrade;
		public DateTime				ModeTime		= DateTime.MinValue;

		public double	Vwap		= double.NaN;
		public double	PriorHigh	= double.NaN;
		public double	PriorLow	= double.NaN;
		public double	Poc			= double.NaN;
		public double	Vah			= double.NaN;
		public double	Val			= double.NaN;
		public double	IbHigh		= double.NaN;
		public double	IbLow		= double.NaN;
		public double	Atr			= double.NaN;
		public VolatilityState Volatility = VolatilityState.Normal;

		// --- published by Indicator 2 (Absorption / Exhaustion) ---
		public OrderFlowEventType	LastEvent		= OrderFlowEventType.None;
		public DateTime				LastEventTime	= DateTime.MinValue;
		public double				LastEventPrice	= double.NaN;
		public ConfidenceLevel		LastEventConfidence = ConfidenceLevel.Low;

		// True if an order-flow event landed within the last 'maxAgeSeconds'.
		public bool EventIsRecent(DateTime now, double maxAgeSeconds)
		{
			if (LastEvent == OrderFlowEventType.None) return false;
			return (now - LastEventTime).TotalSeconds <= maxAgeSeconds;
		}
	}

	public static class SuiteContext
	{
		private static readonly object gate = new object();
		private static readonly Dictionary<string, ContextState> states =
			new Dictionary<string, ContextState>();

		// Get (or create) the shared state slot for an instrument.
		public static ContextState Get(string instrumentKey)
		{
			if (string.IsNullOrEmpty(instrumentKey))
				instrumentKey = "__default__";

			lock (gate)
			{
				ContextState s;
				if (!states.TryGetValue(instrumentKey, out s))
				{
					s = new ContextState();
					states[instrumentKey] = s;
				}
				return s;
			}
		}
	}
}
