// ============================================================================
//  OrderFlowSuite — SwingLevelDetector
// ----------------------------------------------------------------------------
//  Detects confirmed swing highs/lows with NO lookahead bias.
//
//  A swing high at bar B is only CONFIRMED once 'strength' bars have closed on
//  BOTH sides of B. We therefore report the swing 'strength' bars after it
//  formed. This introduces a deterministic confirmation lag (= strength bars),
//  which is the correct, non-repainting way to do it: the level never moves once
//  reported, because it is derived entirely from already-closed bars.
//
//  Feed it the High/Low series each closed bar via Update().
// ============================================================================

using NinjaTrader.NinjaScript;

namespace OrderFlowSuite
{
	public class SwingLevelDetector
	{
		public double	LastSwingHigh		= double.NaN;
		public double	LastSwingLow		= double.NaN;
		public int		LastSwingHighBar	= -1;	// CurrentBar index where the swing actually sits
		public int		LastSwingLowBar		= -1;

		private readonly int strength;

		public SwingLevelDetector(int strength)
		{
			this.strength = strength < 1 ? 1 : strength;
		}

		// Call once per closed bar. currentBar = the indicator's CurrentBar.
		// Returns true if a NEW swing was confirmed on this call.
		public bool Update(ISeries<double> high, ISeries<double> low, int currentBar)
		{
			// need 'strength' bars on each side of the candidate (candidate is barsAgo = strength)
			if (currentBar < strength * 2)
				return false;

			int candidate = strength;	// barsAgo of the bar being tested
			double candHigh = high[candidate];
			double candLow  = low[candidate];

			bool isHigh = true;
			bool isLow  = true;
			for (int i = 1; i <= strength; i++)
			{
				// left and right neighbors of the candidate
				if (high[candidate + i] >= candHigh || high[candidate - i] >= candHigh) isHigh = false;
				if (low[candidate + i]  <= candLow  || low[candidate - i]  <= candLow ) isLow  = false;
			}

			bool found = false;
			if (isHigh)
			{
				LastSwingHigh		= candHigh;
				LastSwingHighBar	= currentBar - candidate;
				found = true;
			}
			if (isLow)
			{
				LastSwingLow		= candLow;
				LastSwingLowBar		= currentBar - candidate;
				found = true;
			}
			return found;
		}

		public bool HasHigh() { return !double.IsNaN(LastSwingHigh); }
		public bool HasLow()  { return !double.IsNaN(LastSwingLow);  }
	}
}
