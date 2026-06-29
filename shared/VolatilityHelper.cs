// ============================================================================
//  OrderFlowSuite — VolatilityHelper (a.k.a. ATRHelper)
// ----------------------------------------------------------------------------
//  Classifies the current volatility regime by comparing the current ATR to its
//  own longer-run average. Pure function — no state, no lookahead.
// ============================================================================

namespace OrderFlowSuite
{
	public static class VolatilityHelper
	{
		// atr        = current ATR value
		// atrAverage = a longer SMA of ATR (the "normal" baseline)
		// Ratios are configurable so the suite can expose them.
		public static VolatilityState Classify(double atr, double atrAverage,
			double compressedRatio, double expandedRatio)
		{
			if (atrAverage <= 0 || double.IsNaN(atr) || double.IsNaN(atrAverage))
				return VolatilityState.Normal;

			double ratio = atr / atrAverage;

			if (ratio <= compressedRatio) return VolatilityState.Compressed;
			if (ratio >= expandedRatio)   return VolatilityState.Expanded;
			return VolatilityState.Normal;
		}
	}
}
