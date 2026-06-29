# ninjascripts

NinjaTrader 8 (NinjaScript / C#) trading tools.

| Folder | What it is |
|---|---|
| `whale-watch/` | Standalone indicator: institutional volume-spike / delta / block-print detector. See its own README. |
| `shared/` | **OrderFlowSuite** shared library — enums + helper classes used by the three suite indicators below. |
| `auction-context/` | **Indicator 1** — Auction Context / Market Regime panel. |
| `absorption-exhaustion/` | **Indicator 2** — Absorption / Exhaustion / Delta-Divergence detector. |
| `trade-quality/` | **Indicator 3** — Trade Quality / Risk grader (A/B/C/D/Skip). |

---

# OrderFlowSuite — a 3-part context / order-flow / risk suite

The goal is **not** a magic buy/sell signal. It is to help you trade better by making three things explicit:

1. **Market auction context** — what regime are we in? (trend / balance / breakout / failed breakout / reversal risk)
2. **Order-flow events** — where is size absorbing or exhausting? (bid/ask absorption, exhaustion, delta divergence)
3. **Trade quality** — is *this specific* entry/stop/target any good? (graded A→Skip with reasons)

The three indicators are independent but **share state at runtime** through a small in-memory blackboard (`SuiteContext`). Indicator 1 publishes the regime + levels; Indicator 2 publishes the latest order-flow event; Indicator 3 reads both to grade trades. If an indicator isn't on the chart, the others **degrade gracefully** — every consumer treats shared data as optional.

> Instrument-agnostic. Tuned defaults are provided for ES / NQ / MES / MNQ but nothing is hard-coded to one product.

---

## Installation (NinjaTrader 8)

NinjaScript compiles every `.cs` file under `Documents\NinjaTrader 8\bin\Custom\` into one assembly, so the shared helpers and the indicators just need to be in the right sub-folders.

1. **Shared helpers → AddOns**
   Copy **all** files from `shared/` into:
   `Documents\NinjaTrader 8\bin\Custom\AddOns\`
   (`OrderFlowSuiteEnums.cs`, `KeyLevel.cs`, `SessionLevels.cs`, `VWAPCalculator.cs`, `VolumeProfile.cs`, `OrderFlowBarStats.cs`, `SwingLevelDetector.cs`, `VolatilityHelper.cs`, `TradeQualityScore.cs`, `SuiteContext.cs`)

2. **Indicators → Indicators**
   Copy the three indicator files into:
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`
   (`AuctionContext.cs`, `AbsorptionExhaustion.cs`, `TradeQuality.cs`)

3. **Compile:** open the **NinjaScript Editor** in NinjaTrader and press **F5** (or click Compile). All files compile together. Fix nothing if it says "0 errors".

4. **Add to a chart:** right-click chart → **Indicators…**. The three appear as:
   - `OFS_AuctionContext`
   - `OFS_AbsorptionExhaustion`
   - `OFS_TradeQuality`

   **Add them in that order** (1 → 2 → 3). Order isn't strictly required, but it makes the dependency obvious and keeps the shared context populated for the consumers.

5. **For historical order flow** (optional but recommended): in the chart's **Data Series** window set **Tick Replay = true** and make sure tick data is downloaded for the instrument. Without it, Indicator 2's delta-based events only appear in real time (see Data Requirements).

---

## Data requirements & limitations

| Feature | Needs | Without it |
|---|---|---|
| Regime panel, VWAP, IB, prior-day/value levels (Ind. 1) | Standard bar data (any feed) | Works fully. Volume profile POC/VAH/VAL is an **approximation** from bar data; turn `UseVolumeProfile` off to fall back to session range + VWAP. |
| **Real** per-bar delta / absorption / exhaustion (Ind. 2) | Real-time **bid/ask tape** (free), via `OnMarketData` | In real time: works on the free plan (your Time & Sales tape). |
| Order-flow events on **historical** bars | **Tick Replay** enabled + tick data downloaded | Falls back to `ApproximateDeltaWithoutTicks` (delta estimated from bar shape). Approximated events are marked with a `~` and scored lower. |
| Exact volume-at-price profile | NinjaTrader **Order Flow+** (Volumetric bars) | The suite isolates this in `OrderFlowBarStats` / `VolumeProfile.AddLevel()`. Not required — the approximations above are used instead. |

**Bid/ask classification** (when no Volumetric data): a trade is a buy if it prints at/above the ask, a sell if at/below the bid; otherwise an uptick/downtick fallback is used. This is the industry-standard approximation and is documented in `OrderFlowBarStats.cs`.

**No repainting / no lookahead:** every signal uses only closed-bar (or current) data. Order-flow events require `ConfirmationBars` closed bars *after* the event before they're drawn, so markers appear with a deliberate lag and never move once printed. Swing-based levels are confirmed `SwingStrength` bars after they form, for the same reason.

---

## How to read each indicator

### 1. Auction Context (top panel)
Tells you the **bias**, not the trade:
- **Trend Up/Down** → favor pullback entries *with* the trend.
- **Balanced / Rotational** → fade the extremes of the range, don't chase the middle.
- **Breakout Up/Down** → favor continuation in the breakout direction.
- **Failed Breakout** → favor reversal back into the prior range.
- **Reversal Risk** → price is stretched from VWAP into a level / a big order-flow event hit; tighten up, consider fading or taking profit.
- **Price Location** line tells you where you are vs prior day, value area, and VWAP.

### 2. Absorption / Exhaustion (chart markers)
- **Bid Absorb** (green ▲ below bar): aggressive selling got absorbed; buyers defending → bias up.
- **Ask Absorb** (red ▼ above bar): aggressive buying got absorbed; sellers defending → bias down.
- **Sell Exhaust** (aqua ▲): selling spike into a low with no follow-through → downside may be done.
- **Buy Exhaust** (orange ▼): buying spike into a high with no follow-through → upside may be done.
- **Delta Div** (violet): price made a new extreme but cumulative delta didn't — momentum weakening.
- Label suffix `H/M/L` = confidence; `~` = delta was approximated (no tick data).

### 3. Trade Quality (panel + optional lines)
Shows a graded plan: **A / B / C / D / Skip**, the entry/stop/target, R/R, distance from VWAP, nearest level, volatility, and a list of `+` reasons and `x` warnings. In **Auto** mode it estimates the plan from structure + regime; in **Manual** mode enter your own direction/entry/stop/target and it grades exactly those.

---

## What these do NOT do
- They do **not** place, modify, or manage orders.
- They do **not** predict the future or guarantee outcomes.
- They are decision-support context, **not** automated financial advice. You are responsible for every trade.
- Approximated (non-Tick-Replay) order flow is a rough proxy — treat `~` events with extra skepticism.

---

## Suggested defaults

Tick size for all four is **0.25**. Point values differ (used by the position-size helper in Indicator 3):
ES **$50**, MES **$5**, NQ **$20**, MNQ **$2**.

Order-flow thresholds scale with how much volume an instrument trades, and with your **chart timeframe** — these assume roughly a **1–2 minute** (or ~1000–2000 tick) chart. On faster charts, lower the delta/volume thresholds; on slower charts, raise them.

### Indicator 2 — Absorption / Exhaustion

| Property | ES | NQ | MES | MNQ |
|---|---|---|---|---|
| DeltaThreshold | 1200 | 700 | 250 | 150 |
| UseDynamicDeltaThreshold | true | true | true | true |
| DeltaDynamicMultiplier | 2.0 | 2.0 | 2.0 | 2.0 |
| DeltaLookback | 50 | 50 | 50 | 50 |
| VolumeMultiplier | 1.5 | 1.5 | 1.5 | 1.5 |
| KeyLevelDistanceTicks | 8 | 10 | 8 | 10 |
| ConfirmationBars | 2 | 2 | 2 | 2 |
| MaxAdverseTicks | 4 | 6 | 4 | 6 |

> With dynamic thresholds ON, the static `DeltaThreshold` is just a floor — the live threshold rises and falls with recent activity, which is why the same settings work across micros and minis. The micros mostly need the lower floor.

### Indicator 1 — Auction Context (same for all four)
`InitialBalanceMinutes 60`, `VWAPSlopeLookback 20`, `VWAPNearThresholdTicks 8`, `VWAPExtensionATR 1.5`, `BreakoutConfirmBars 2`, `FailedBreakoutBars 5`, `ATRPeriod 14`, `SwingStrength 4`, `RTHOpenTime 09:30` (set to the cash open in **your platform's exchange time zone**).

### Indicator 3 — Trade Quality
`MaxStopATR 1.25`, `MinRewardRisk 1.5`, `VWAPExtensionATR 1.5`, `SwingLookback 10`, `StopPadTicks 4`. Set **PointValue** per the table above (or leave 0 to auto-detect), and **AccountRiskDollars** to your per-trade risk to get a suggested contract size.

---

## Common false positives (and what to do)

- **Absorption that immediately fails.** Big delta with a small range can precede a *break*, not a hold. The `ConfirmationBars` follow-through check filters most, but in fast trends absorption against the trend gets run over — respect the regime panel.
- **Exhaustion in a strong trend.** New highs with big delta often just... keep going. Exhaustion is highest-quality at/near a key level (keep `UseKeyLevelFilter` on) and when it disagrees with an overextended VWAP reading.
- **Delta divergence early.** Divergence can persist for many bars before price turns. It's a *warning*, not a trigger.
- **Approximated events (`~`).** Without Tick Replay these are derived from bar shape and will be noisier. Enable Tick Replay for anything you rely on.
- **Volume-profile levels right after the open.** POC/VAH/VAL need enough session volume to be meaningful; early-session value-area levels move around. They stabilize as the session develops.
- **Regime flapping in chop.** In balance the mode can oscillate (Balanced ↔ soft trend). That itself is information: if it won't commit, it's a fade/no-trade environment.

---

*Built as decision-support tooling. Markets involve risk of loss; nothing here is a recommendation to buy or sell any instrument.*
