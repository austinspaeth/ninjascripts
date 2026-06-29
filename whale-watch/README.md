# WhaleWatch — Institutional Activity Indicator (NinjaTrader 8)

Spot where the big players (whales/institutions) are moving size, and trade **with**
them. WhaleWatch reads the tape and volume in real time and marks aggressive
institutional footprints right on your chart.

## What it detects

| Signal | What it means | On chart |
|---|---|---|
| **Volume spike** | Bar trades far above its normal volume → big money is active | Green ▲ (bull) / Red ▼ (bear) arrow + `2.4x` relative-volume label |
| **Order-flow delta** | Net aggressive buying vs selling inside the bar (read live from the bid/ask tape) | `Δ+1800` label; colors the bar |
| **Block print** | A single oversized trade hits the tape (whale lifting the offer / hitting the bid) | Magenta/green/red dot at the print price |
| **Absorption** | Huge volume but tiny range → a big player is soaking up orders (often a reversal tell) | Gold ■ + `ABSORB` label |
| **Footprint level** | Price where a whale was most active — tends to become support/resistance | Dashed gold horizontal line |

Whale bars are also painted (green/red/gold) so they pop visually.

## How to read it for "moving with the whales"

- **Green ▲ with positive Δ + price breaking out** → aggressive buyers in control; look to go long with them.
- **Red ▼ with negative Δ + price breaking down** → aggressive sellers; look to go short.
- **Gold ABSORB at a high/low** → big player absorbing the move; momentum may be exhausting → fade / take profit.
- **Footprint levels** → watch for reactions (bounces/rejections) when price returns to them.
- **Block dots clustering** in one direction → real conviction behind the move.

> Confirmation > prediction. Use whale signals to confirm your entries, not in isolation.

## Install

1. Copy `WhaleWatch.cs` into:
   `Documents\NinjaTrader 8\bin\Custom\Indicators\WhaleWatch.cs`
2. In NinjaTrader: **New → NinjaScript Editor**, then press **F5** (Compile).
   - If it's your first custom indicator, the editor's "Compile" button is in the toolbar.
3. Open a chart → right-click → **Indicators…** → find **WhaleWatch** → **Add**.
4. Set `Calculate` to **On each tick** (the default) so live delta/blocks work.

## Recommended settings

These ship as defaults but tune per instrument and timeframe.

| Input | ES (S&P 500) | NQ (Nasdaq) | Notes |
|---|---|---|---|
| Volume spike multiplier | 2.0 | 2.0 | Raise to 2.5–3.0 to see only the biggest bars |
| Block trade size | 50 | 25 | NQ trades smaller clip sizes; lower it |
| Delta whale threshold | 1500 | 800 | Scale down on faster/lower-timeframe charts |
| Volume lookback (bars) | 20 | 20 | 20 is a good general baseline |
| Absorption range % | 0.50 | 0.50 | Lower = stricter (truly tiny-range bars only) |

**On lower timeframes** (e.g. 500-tick, 1-min) the per-bar volumes are smaller —
reduce *Block trade size* and *Delta whale threshold* accordingly, or the signals
will rarely fire.

## Important data note

NinjaTrader's free plan provides **real-time** bid/ask on the tape, which is all
WhaleWatch needs for live trading. Two caveats:

- **Delta and block detection are real-time only.** Historical bars carry total
  volume but not per-trade bid/ask, so when you first load a chart, older bars show
  only volume-spike / absorption signals. Delta + blocks populate as new ticks arrive.
- To see delta on *past* sessions, you'd need tick-by-tick historical data
  (NinjaTrader **Market Replay** download, still free) and replay the session.

## Tuning tips

- Too many signals? Raise the **Volume spike multiplier** and **Delta whale threshold**.
- Chart cluttered with footprint levels? Turn off **Show footprint levels**.
- Want alerts (sound/popup) on whale prints? Keep **Enable alerts** on — it fires on
  live bars only, with a 10-second rearm so it won't spam you.

## Roadmap ideas (ask if you want these)

- Cumulative delta line + divergence detection vs price
- Stacked-imbalance / footprint-style bid×ask detection
- Multi-timeframe whale confirmation (e.g. only signal when higher TF agrees)
- A companion Strategy that auto-enters on whale confirmation (backtestable)
