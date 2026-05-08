# ShortScalper v2.6 - How It Works

## Overview

**ShortScalper** is the **short-side mirror** of the LongScalper family. It is a **short-only scalping strategy** that places a sell limit order and fully manages the trade with profit target, stop loss, and trailing stop.

It is designed to short "sell-the-rip" opportunities during up-move exhaustion, working in tandem with the companion alert strategy `scalper_TrendPullbackAlert_beta`.

---

## Main Purpose

- Enter **short** at a favorable (higher) price during counter-trend rallies.
- Automatically attach profit target and stop loss brackets upon fill.
- Trail the stop loss downward to protect profits as price falls.
- Provide strong defensive protection against orphaned orders.

---

## How It Works (Simple Version)

1. When enabled → submits a **Sell Limit** order **above** the current market price.
2. Upon fill → immediately attaches PT and SL brackets.
3. Monitors the position and trails the stop loss **downward**.
4. After the trade ends, the strategy cleans up and disables itself.

---

## Detailed Operation

### 1. Entry Logic

The strategy offers two entry modes (`LimitMode`):

- **OffsetFromLastPrice** (Default & Recommended):
  - Calculates `avgBarSize` = EMA of recent 1-minute bar ranges (High - Low).
  - Places **Sell Limit** at: `Current Price + (EntryOffsetMultiplier × avgBarSize)`.

- **FixedPrice**:
  - Uses the exact price you set in `FixedLimitPrice` (typically above current market).

The order is cancelled if not filled within `OrderLifeSeconds`.

### 2. Exit Logic (Short-Specific)

- **Profit Target**: Fixed points **below** the actual fill price.
- **Stop Loss**: Placed **above** the fill price (two modes available).
- **Trailing Stop**: Moves the stop **down** as price moves in your favor (price falling).

### 3. Safety & Defensive Features (v2.6)

- Thorough pre-entry checks (strategy flat + account flat + no working orders).
- **Orphan Protection**: If the account becomes flat while the strategy thinks it has a short position, it automatically cleans up all brackets and disables itself.
- Protects against manual buy-to-cover, broker actions, etc.

---

## Important Usage Notes

**This strategy is primarily designed and optimized for MNQ on 1-minute charts.**

- For other instruments or timeframes, **adjust parameters carefully** (especially multipliers and point values).
- Best used after receiving a short signal from a suitable alert system.

### Critical Rules After Entry

- Once the strategy has opened a short position and attached brackets, **do not** add manual orders on top.
- If price goes strongly against you (rallies hard):
  - Let the stop loss handle the exit, **or**
  - Manually flatten the position + cancel all orders, then **disable and re-enable** the strategy for the next setup.
- Adding manual orders while the strategy is managing brackets can cause conflicts.

---

## Parameter Guide

### 1. Trade Size
- **Quantity**: Number of contracts (default 1).

### 2. Entry
- **LimitMode**:
  - `OffsetFromLastPrice` (default): Dynamic offset **above** current price.
  - `FixedPrice`: Uses exact price set in `FixedLimitPrice`.
- **EntryOffsetMultiplier** (0.10): Distance **above** current price.
- **BarSizeAveragePeriod** (10): Lookback for volatility calculation.
- **FixedLimitPrice**: Target sell price when using FixedPrice mode.
- **OrderLifeSeconds** (5): Maximum time the limit order stays active.

### 3. Profit Target
- **ProfitTargetPoints** (10): Take-profit in points **below** fill price.

### 4. Stop Loss & Trailing
- **StopMode**:
  - `OffsetFromFilledPrice` (recommended): Adaptive using `avgBarSize × multiplier`.
  - `FixedPoints`: Static point values.
- **StopOffsetMultiplier** (2.0): Initial stop distance **above** fill.
- **TrailOffsetMultiplier** (0.8): Trailing stop distance.
- **HardStopPoints** (20): Used only in FixedPoints mode.
- **TrailDistancePoints** (8): Used only in FixedPoints mode.

### 5. Trailing Behavior
- **MonitorIntervalSeconds** (3): How often trailing logic runs.
- **PullbackTolerancePoints** (2): Tolerated upward rally before considering reversal.

### 6. Others
- **DisableDelaySeconds** (1.5): Delay before auto-disable after trade ends.
- **EnableSoundOnFill**: Play alert sound on entry fill.
- **AuditLogPath**: Folder for trade result CSV.

---

## Recommended Workflow

1. Run your alert strategy on MNQ 1-minute chart.
2. Wait for a short setup alert.
3. Quickly enable `ShortScalper`.
4. Strategy places the sell limit order above the market.
5. If filled → brackets are managed automatically.
6. After trade completes → strategy disables itself.
7. Wait for next signal and repeat.

---

## Key Directional Differences (vs LongScalper)

| Feature              | LongScalper              | ShortScalper                  |
|----------------------|--------------------------|-------------------------------|
| Entry Limit          | Below market             | **Above** market              |
| Profit Target        | Above fill               | **Below** fill                |
| Initial Stop         | Below fill               | **Above** fill                |
| Trailing Direction   | Stop moves **up**        | Stop moves **down**           |
| PnL Calculation      | Exit - Fill              | Fill - Exit                   |
| Pullback Definition  | Price drops             | **Price rallies**             |

---

**Version**: 2.6 (2026)  
**Author**: Albert Feng (with help from Claude)  
**Primary Instrument**: MNQ (1-minute chart)

---

*Use with proper risk management. Always monitor your positions.*
