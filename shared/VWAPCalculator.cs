// ============================================================================
//  OrderFlowSuite — VWAPCalculator
// ----------------------------------------------------------------------------
//  Session-anchored Volume Weighted Average Price.
//
//  NinjaTrader's built-in VWAP indicator ships only with Order Flow+, so the
//  suite computes its own session VWAP from bar data. This works on any data
//  feed (free included). Feed it one closed bar at a time; reset each session.
//
//  No lookahead: only closed-bar values are ever added.
// ============================================================================

namespace OrderFlowSuite
{
	public class VWAPCalculator
	{
		private double	sumPriceVolume;
		private double	sumVolume;

		// Latest VWAP value (double.NaN until at least one bar with volume is added).
		public double Value { get; private set; }

		public VWAPCalculator()
		{
			Reset();
		}

		// Call at the start of every new session.
		public void Reset()
		{
			sumPriceVolume	= 0;
			sumVolume		= 0;
			Value			= double.NaN;
		}

		// Add one CLOSED bar. typicalPrice is usually (H+L+C)/3, but the caller
		// may pass any price source it prefers.
		public void AddBar(double typicalPrice, double volume)
		{
			if (volume <= 0)
				return;

			sumPriceVolume	+= typicalPrice * volume;
			sumVolume		+= volume;

			if (sumVolume > 0)
				Value = sumPriceVolume / sumVolume;
		}

		public bool IsReady()
		{
			return sumVolume > 0;
		}
	}
}
