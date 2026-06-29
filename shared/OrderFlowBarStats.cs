// ============================================================================
//  OrderFlowSuite — OrderFlowBarStats
// ----------------------------------------------------------------------------
//  Per-bar order-flow accumulator: bid/ask volume, delta, cumulative delta,
//  intrabar delta extremes, and close location within the bar.
//
//  TRADE CLASSIFICATION (no Order Flow+ required):
//  Fed from OnMarketData Last prints together with the current best bid/ask:
//      last >= ask  -> aggressive BUY  (ask/offer lifted)  -> AskVolume
//      last <= bid  -> aggressive SELL (bid hit)           -> BidVolume
//      otherwise    -> uptick/downtick fallback vs the previous last price
//  This is the standard "bid/ask" classification used when true volumetric data
//  is unavailable. It is an APPROXIMATION; see suite README for accuracy notes.
//
//  HISTORICAL DATA: OnMarketData only fires in real time UNLESS the chart's
//  "Tick Replay" option is enabled (requires downloaded tick data). Without Tick
//  Replay, historical bars have no per-trade bid/ask and delta is unavailable
//  for them — order-flow events therefore populate live (or on replay) only.
// ============================================================================

namespace OrderFlowSuite
{
	public class OrderFlowBarStats
	{
		public long		BidVolume;			// aggressive selling
		public long		AskVolume;			// aggressive buying
		public long		TotalVolume;
		public long		CumulativeDelta;	// running across the session (managed by caller)
		public long		MaxDelta;			// highest running delta reached intrabar
		public long		MinDelta;			// lowest running delta reached intrabar

		public double	Open;
		public double	High;
		public double	Low;
		public double	Close;

		private long	runningDelta;		// intrabar running delta
		private double	lastTradePrice;
		private bool	hasLastTrade;

		public long Delta { get { return AskVolume - BidVolume; } }

		// Fraction of the bar's range the close sits at: 0 = at low, 1 = at high.
		public double CloseLocation
		{
			get
			{
				if (High <= Low) return 0.5;
				return (Close - Low) / (High - Low);
			}
		}

		// Begin a fresh bar. cumulativeStart carries the session cumulative delta forward.
		public void StartBar(double open, long cumulativeStart)
		{
			BidVolume		= 0;
			AskVolume		= 0;
			TotalVolume		= 0;
			runningDelta	= 0;
			MaxDelta		= 0;
			MinDelta		= 0;
			Open			= open;
			High			= open;
			Low				= open;
			Close			= open;
			CumulativeDelta	= cumulativeStart;
		}

		// Classify and accumulate one executed trade.
		//   hasBidAsk: true when bid/ask are known (real-time / Tick Replay).
		public void AddTrade(double price, long volume, double bid, double ask, bool hasBidAsk)
		{
			if (volume <= 0) return;

			bool isBuy;

			if (hasBidAsk && ask > 0 && bid > 0 && price >= ask)
				isBuy = true;
			else if (hasBidAsk && ask > 0 && bid > 0 && price <= bid)
				isBuy = false;
			else
			{
				// uptick/downtick fallback (no usable bid/ask): compare to prior last
				if (!hasLastTrade)
					isBuy = true;					// neutral default on the very first print
				else if (price > lastTradePrice)
					isBuy = true;
				else if (price < lastTradePrice)
					isBuy = false;
				else
					isBuy = runningDelta >= 0;		// unchanged: carry prevailing direction
			}

			if (isBuy) AskVolume += volume;
			else       BidVolume += volume;

			TotalVolume		+= volume;
			runningDelta	= AskVolume - BidVolume;
			if (runningDelta > MaxDelta) MaxDelta = runningDelta;
			if (runningDelta < MinDelta) MinDelta = runningDelta;

			Close			= price;
			if (price > High) High = price;
			if (price < Low)  Low  = price;

			lastTradePrice	= price;
			hasLastTrade	= true;
		}

		// Finalize the running cumulative delta once the bar is complete.
		public void CloseBar()
		{
			CumulativeDelta += Delta;
		}
	}
}
