// ============================================================================
//  OrderFlowSuite — TradeQualityScore
// ----------------------------------------------------------------------------
//  Accumulates penalties/bonuses against a base of 100 and resolves a letter
//  grade. Keeps human-readable reasons so the panel can explain itself.
// ============================================================================

using System.Collections.Generic;

namespace OrderFlowSuite
{
	public class TradeQualityScore
	{
		public int					Score;
		public readonly List<string>	Warnings	= new List<string>();	// penalties (shown red)
		public readonly List<string>	Positives	= new List<string>();	// bonuses (shown green)

		public TradeQualityScore()
		{
			Score = 100;
		}

		public void Penalty(int points, string reason)
		{
			Score -= points;
			if (!string.IsNullOrEmpty(reason))
				Warnings.Add(reason);
		}

		public void Bonus(int points, string reason)
		{
			Score += points;
			if (!string.IsNullOrEmpty(reason))
				Positives.Add(reason);
		}

		// Clamped 0..100 for display.
		public int DisplayScore
		{
			get { return Score < 0 ? 0 : (Score > 100 ? 100 : Score); }
		}

		// Grade thresholds per spec. Note bonuses can push raw score above 100;
		// grade uses the raw score so strong confluence is rewarded.
		public TradeGrade Grade
		{
			get
			{
				if (Score >= 85) return TradeGrade.A;
				if (Score >= 70) return TradeGrade.B;
				if (Score >= 55) return TradeGrade.C;
				if (Score >= 40) return TradeGrade.D;
				return TradeGrade.Skip;
			}
		}
	}
}
