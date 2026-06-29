// ============================================================================
//  OrderFlowSuite — KeyLevel
// ----------------------------------------------------------------------------
//  A single horizontal price reference (prior day high, VWAP, POC, etc.) with a
//  name, a type, and a "strength" weight used when grading proximity to levels.
// ============================================================================

using System;

namespace OrderFlowSuite
{
	public class KeyLevel
	{
		public double			Price;
		public string			Name;
		public KeyLevelType		Type;
		public double			Strength;	// relative importance, e.g. 1.0 = normal, 2.0 = major

		public KeyLevel(double price, string name, KeyLevelType type, double strength)
		{
			Price		= price;
			Name		= name;
			Type		= type;
			Strength	= strength;
		}

		// Absolute distance from a given price, expressed in ticks.
		public int DistanceTicks(double fromPrice, double tickSize)
		{
			if (tickSize <= 0 || double.IsNaN(Price) || double.IsNaN(fromPrice))
				return int.MaxValue;
			return (int)Math.Round(Math.Abs(fromPrice - Price) / tickSize);
		}

		public bool IsValid()
		{
			return !double.IsNaN(Price) && Price > 0;
		}
	}
}
