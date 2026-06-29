// ============================================================================
//  OrderFlowSuite — VolumeProfile
// ----------------------------------------------------------------------------
//  Lightweight session volume profile producing POC / VAH / VAL.
//
//  DATA LIMITATION (read this):
//  A true volume-at-price profile needs volume broken down per price level,
//  which requires tick data / Order Flow+ Volumetric bars. With only standard
//  bar data we APPROXIMATE by spreading each bar's total volume evenly across
//  the price levels the bar covered (high..low in tick increments). This is a
//  reasonable proxy for POC/value-area location on intraday charts and needs no
//  paid data, but it is NOT a true volumetric profile. If Order Flow+ Volumetric
//  bars are available, feed real per-price volume into AddLevel() instead.
//
//  CPU: each bar distributes across (range / tick) buckets, capped by maxLevels
//  to keep wide-range bars from blowing up the dictionary.
// ============================================================================

using System;
using System.Collections.Generic;

namespace OrderFlowSuite
{
	public class VolumeProfile
	{
		// key = price expressed as integer tick index; value = accumulated volume
		private readonly Dictionary<long, double> bins = new Dictionary<long, double>();
		private double	tickSize;
		private double	totalVolume;

		public double POC { get; private set; }		// price of highest-volume level
		public double VAH { get; private set; }		// value area high (top of 70% volume)
		public double VAL { get; private set; }		// value area low

		public VolumeProfile()
		{
			Reset(0.25);
		}

		public void Reset(double instrumentTickSize)
		{
			bins.Clear();
			tickSize		= instrumentTickSize > 0 ? instrumentTickSize : 0.25;
			totalVolume		= 0;
			POC = VAH = VAL = double.NaN;
		}

		private long ToKey(double price)
		{
			return (long)Math.Round(price / tickSize);
		}

		// Spread one closed bar's volume across the levels it traded through.
		public void AddBar(double high, double low, double volume, int maxLevels)
		{
			if (volume <= 0 || high < low)
				return;

			long hiKey		= ToKey(high);
			long loKey		= ToKey(low);
			int levels		= (int)(hiKey - loKey) + 1;
			if (levels < 1) levels = 1;
			if (levels > maxLevels) levels = maxLevels;

			double volPerLevel	= volume / levels;
			// distribute across 'levels' evenly spaced buckets between low and high
			for (int i = 0; i < levels; i++)
			{
				long key = loKey + (long)Math.Round((double)i * (hiKey - loKey) / Math.Max(1, levels - 1));
				double cur;
				bins.TryGetValue(key, out cur);
				bins[key] = cur + volPerLevel;
			}
			totalVolume += volume;
		}

		// Direct insertion when real volume-at-price is available (Order Flow+).
		public void AddLevel(double price, double volume)
		{
			if (volume <= 0) return;
			long key = ToKey(price);
			double cur;
			bins.TryGetValue(key, out cur);
			bins[key] = cur + volume;
			totalVolume += volume;
		}

		// Compute POC and the 70% value area. Call after the session's bars are in
		// (or incrementally — it's cheap relative to a full session).
		public void Compute(double valueAreaPercent)
		{
			if (bins.Count == 0 || totalVolume <= 0)
			{
				POC = VAH = VAL = double.NaN;
				return;
			}

			// POC = highest-volume bucket
			long pocKey		= 0;
			double pocVol	= -1;
			long minKey		= long.MaxValue;
			long maxKey		= long.MinValue;
			foreach (KeyValuePair<long, double> kv in bins)
			{
				if (kv.Value > pocVol) { pocVol = kv.Value; pocKey = kv.Key; }
				if (kv.Key < minKey) minKey = kv.Key;
				if (kv.Key > maxKey) maxKey = kv.Key;
			}
			POC = pocKey * tickSize;

			// Expand outward from POC, always grabbing the heavier neighbor, until
			// we've captured the requested share of total volume (classic VA method).
			double target	= totalVolume * valueAreaPercent;
			double captured	= pocVol;
			long upper		= pocKey;
			long lower		= pocKey;

			while (captured < target && (upper < maxKey || lower > minKey))
			{
				double upVol	= 0;
				double downVol	= 0;
				bool canUp		= upper < maxKey;
				bool canDown	= lower > minKey;
				if (canUp)   bins.TryGetValue(upper + 1, out upVol);
				if (canDown) bins.TryGetValue(lower - 1, out downVol);

				if (canUp && (!canDown || upVol >= downVol))
				{
					upper++;
					captured += upVol;
				}
				else if (canDown)
				{
					lower--;
					captured += downVol;
				}
				else
					break;
			}

			VAH = upper * tickSize;
			VAL = lower * tickSize;
		}

		public bool IsReady()
		{
			return bins.Count > 0 && !double.IsNaN(POC);
		}
	}
}
