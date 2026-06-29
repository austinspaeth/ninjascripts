// ============================================================================
//  OrderFlowSuite — Shared enumerations
// ----------------------------------------------------------------------------
//  Part of the OrderFlowSuite indicator suite for NinjaTrader 8.
//  Place this file (and all other shared files) in:
//      Documents\NinjaTrader 8\bin\Custom\AddOns\
//  They compile into the same assembly as the indicators, so every indicator
//  in the suite can reference these types via:  using OrderFlowSuite;
// ============================================================================

namespace OrderFlowSuite
{
	// What kind of market are we in right now? Drives the bias of the suite.
	public enum MarketMode
	{
		Unknown,			// insufficient data
		TrendUp,
		TrendDown,
		Balanced,			// rotational / mean-reverting
		BreakoutUp,
		BreakoutDown,
		FailedBreakoutUp,
		FailedBreakoutDown,
		ReversalRisk
	}

	// The kind of trade the current regime favors.
	public enum PreferredTradeType
	{
		NoTrade,
		LongPullbacks,
		ShortPullbacks,
		FadeExtremes,
		BreakoutContinuation,
		FailedBreakoutReversal
	}

	public enum TradeDirection
	{
		None,
		Long,
		Short
	}

	// Ordered worst -> best so numeric comparison (>=) works for "grade above B".
	public enum TradeGrade
	{
		Skip,
		D,
		C,
		B,
		A
	}

	public enum OrderFlowEventType
	{
		None,
		BullishAbsorption,	// heavy selling absorbed, price holds -> buyers in control
		BearishAbsorption,	// heavy buying absorbed, price holds -> sellers in control
		BullishExhaustion,	// selling spike into a low with no follow-through
		BearishExhaustion,	// buying spike into a high with no follow-through
		DeltaDivergence
	}

	public enum ConfidenceLevel
	{
		Low,
		Medium,
		High
	}

	// Identifies what a KeyLevel represents (used for labels / strength weighting).
	public enum KeyLevelType
	{
		PriorDayHigh,
		PriorDayLow,
		PriorVAH,
		PriorVAL,
		PriorPOC,
		SessionHigh,
		SessionLow,
		SessionOpen,
		Vwap,
		IbHigh,
		IbLow,
		OvernightHigh,
		OvernightLow
	}

	public enum VolatilityState
	{
		Compressed,
		Normal,
		Expanded
	}

	public enum TradePlanMode
	{
		Auto,		// indicator estimates entry/stop/target from chart structure
		Manual		// trader supplies entry/stop/target
	}
}
