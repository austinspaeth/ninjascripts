// ============================================================================
//  OrderFlowSuite — SessionLevels
// ----------------------------------------------------------------------------
//  Container for the structural reference levels of the current session, plus
//  helpers to turn them into a KeyLevel list and to find the nearest support /
//  resistance. The owning indicator is responsible for populating the fields
//  (prior day high/low typically come from a daily AddDataSeries; VWAP/profile
//  from the shared calculators). Indicators 2 and 3 can also hydrate this object
//  from SuiteContext when Indicator 1 is on the chart.
// ============================================================================

using System;
using System.Collections.Generic;

namespace OrderFlowSuite
{
	public class SessionLevels
	{
		public double	PriorDayHigh	= double.NaN;
		public double	PriorDayLow		= double.NaN;
		public double	PriorVAH		= double.NaN;
		public double	PriorVAL		= double.NaN;
		public double	PriorPOC		= double.NaN;

		public double	SessionHigh		= double.NaN;
		public double	SessionLow		= double.NaN;
		public double	SessionOpen		= double.NaN;

		public double	IbHigh			= double.NaN;
		public double	IbLow			= double.NaN;

		public double	OvernightHigh	= double.NaN;
		public double	OvernightLow	= double.NaN;

		public double	Vwap			= double.NaN;
		public double	POC				= double.NaN;	// current/developing session
		public double	VAH				= double.NaN;
		public double	VAL				= double.NaN;

		// Reset the intraday-developing fields at a new session (keep prior-day data).
		public void StartSession(double sessionOpen)
		{
			SessionOpen		= sessionOpen;
			SessionHigh		= sessionOpen;
			SessionLow		= sessionOpen;
			IbHigh			= double.NaN;
			IbLow			= double.NaN;
		}

		public void UpdateSessionExtremes(double high, double low)
		{
			if (double.IsNaN(SessionHigh) || high > SessionHigh) SessionHigh = high;
			if (double.IsNaN(SessionLow)  || low  < SessionLow)  SessionLow  = low;
		}

		public void UpdateInitialBalance(double high, double low)
		{
			if (double.IsNaN(IbHigh) || high > IbHigh) IbHigh = high;
			if (double.IsNaN(IbLow)  || low  < IbLow)  IbLow  = low;
		}

		// Build the active set of key levels (skips any that are NaN).
		public List<KeyLevel> BuildKeyLevels()
		{
			List<KeyLevel> levels = new List<KeyLevel>();
			Add(levels, PriorDayHigh,	"PD High",	KeyLevelType.PriorDayHigh,	2.0);
			Add(levels, PriorDayLow,	"PD Low",	KeyLevelType.PriorDayLow,	2.0);
			Add(levels, PriorPOC,		"P-POC",	KeyLevelType.PriorPOC,		1.5);
			Add(levels, PriorVAH,		"P-VAH",	KeyLevelType.PriorVAH,		1.5);
			Add(levels, PriorVAL,		"P-VAL",	KeyLevelType.PriorVAL,		1.5);
			Add(levels, OvernightHigh,	"ON High",	KeyLevelType.OvernightHigh,	1.5);
			Add(levels, OvernightLow,	"ON Low",	KeyLevelType.OvernightLow,	1.5);
			Add(levels, IbHigh,			"IB High",	KeyLevelType.IbHigh,		1.0);
			Add(levels, IbLow,			"IB Low",	KeyLevelType.IbLow,			1.0);
			Add(levels, Vwap,			"VWAP",		KeyLevelType.Vwap,			1.5);
			Add(levels, POC,			"POC",		KeyLevelType.PriorPOC,		1.0);
			Add(levels, VAH,			"VAH",		KeyLevelType.PriorVAH,		1.0);
			Add(levels, VAL,			"VAL",		KeyLevelType.PriorVAL,		1.0);
			return levels;
		}

		private static void Add(List<KeyLevel> list, double price, string name, KeyLevelType type, double strength)
		{
			if (!double.IsNaN(price) && price > 0)
				list.Add(new KeyLevel(price, name, type, strength));
		}

		// Nearest level strictly ABOVE the given price (next resistance).
		public KeyLevel NearestAbove(double price)
		{
			KeyLevel best = null;
			foreach (KeyLevel k in BuildKeyLevels())
			{
				if (k.Price > price && (best == null || k.Price < best.Price))
					best = k;
			}
			return best;
		}

		// Nearest level strictly BELOW the given price (next support).
		public KeyLevel NearestBelow(double price)
		{
			KeyLevel best = null;
			foreach (KeyLevel k in BuildKeyLevels())
			{
				if (k.Price < price && (best == null || k.Price > best.Price))
					best = k;
			}
			return best;
		}

		// Nearest level in either direction, with its distance in ticks.
		public KeyLevel Nearest(double price, double tickSize, out int distanceTicks)
		{
			KeyLevel best = null;
			int bestDist = int.MaxValue;
			foreach (KeyLevel k in BuildKeyLevels())
			{
				int d = k.DistanceTicks(price, tickSize);
				if (d < bestDist) { bestDist = d; best = k; }
			}
			distanceTicks = bestDist;
			return best;
		}
	}
}
