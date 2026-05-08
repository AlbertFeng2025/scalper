# LongScalper_FixedLimitEntry v2.6 - How It Works

## Overview

**LongScalper_FixedLimitEntry** is a **long-only scalping strategy** that places a limit buy order and manages the full trade with profit target, stop loss, and trailing stop.

It is the **simpler, non-tracking** version of the LongScalper family. While `LongScalper_TrackingLimitEntry` continuously adjusts the limit price, this version uses a **fixed** (static) limit order.

It is designed to work together with the companion alert strategy `scalper_TrendPullbackAlert_beta`.

---

## Main Purpose

- Enter long at a favorable price during pullback recoveries.
- Automatically attach profit target and stop loss brackets upon fill.
- Trail the stop loss to lock in profits.
- Provide strong defensive protection against orphaned orders.

---

## How It Works (Simple Version)

1. When enabled → submits a **Buy Limit** order at a calculated price below the market.
2. Upon fill → immediately attaches PT and SL brackets.
3. Monitors the position and trails the stop loss upward.
4. After the trade ends (PT, SL, or manual exit), the strategy cleans up and disables itself.

---

## Detailed Operation

### 1. Entry Logic

The strategy offers two entry modes (`LimitMode`):

- **OffsetFromLastPrice** (Default & Recommended):
  - Calculates `avgBarSize` = EMA of recent 1-minute bar ranges (High - Low).
  - Places Buy Limit at: `Current Price - (EntryOffsetMultiplier × avgBarSize)`.

- **FixedPrice**:
  - Uses the exact price you enter in `FixedLimitPrice`.

The order is cancelled if not filled within `OrderLifeSeconds`.

### 2. Exit Logic

- **Profit Target**: Fixed points above the actual fill price.
- **Stop Loss**: Two modes available (see parameters).
- **Trailing Stop**: Automatically moves the stop loss higher as price moves in your favor (with pullback tolerance).

### 3. Safety & Defensive Features (v2.6)

- Thorough pre-entry checks (strategy flat + account flat + no working orders).
- **Orphan Protection**: If the account becomes flat while the strategy thinks it has a position, it automatically cleans up all brackets and disables itself.
- This protects against manual closes, external fills, or broker actions.

---

## Important Usage Notes

**This strategy is primarily designed and optimized for MNQ on 1-minute charts.**

- For other instruments or timeframes, **adjust parameters carefully** (especially multipliers and point values).
- Best used after receiving an alert from `scalper_TrendPullbackAlert_beta`.

### Critical Rules After Entry

- Once the strategy has opened a position and attached brackets, **do not** add manual orders on top of it.
- If price goes strongly against you:
  - Let the stop loss handle the exit, **or**
  - Manually flatten the position + cancel all orders, then **disable and re-enable** the strategy for the next setup.
- Adding manual orders while the strategy is managing brackets can cause conflicts and unexpected behavior.

---

## Parameter Guide

### 1. Trade Size
- **Quantity**: Number of contracts (default 1).

### 2. Entry
- **LimitMode**:
  - `OffsetFromLastPrice` (default): Dynamic offset from current price.
  - `FixedPrice`: Uses exact price set in `FixedLimitPrice`.
- **EntryOffsetMultiplier** (0.10): Distance below current price (used in Offset mode).
- **BarSizeAveragePeriod** (10): Lookback period for volatility calculation.
- **FixedLimitPrice**: Target price when using FixedPrice mode.
- **OrderLifeSeconds** (5): Maximum time the limit order stays active.

### 3. Profit Target
- **ProfitTargetPoints** (10): Take-profit in points above fill price.

### 4. Stop Loss & Trailing
- **StopMode**:
  - `OffsetFromFilledPrice` (recommended): Adaptive using `avgBarSize × multiplier`.
  - `FixedPoints`: Static point values.
- **StopOffsetMultiplier** (2.0): Initial stop distance.
- **TrailOffsetMultiplier** (0.8): Trailing stop distance (tighter).
- **HardStopPoints** (20): Used only in FixedPoints mode.
- **TrailDistancePoints** (8): Used only in FixedPoints mode.

### 5. Trailing Behavior
- **MonitorIntervalSeconds** (3): How often trailing logic runs.
- **PullbackTolerancePoints** (2): Allowed pullback before considering reversal.

### 6. Others
- **DisableDelaySeconds** (1.5): Delay before auto-disable after trade ends.
- **EnableSoundOnFill**: Play alert sound on entry fill.
- **AuditLogPath**: Folder for trade result CSV.

---

## Recommended Workflow

1. Run `scalper_TrendPullbackAlert_beta` on MNQ 1-minute chart.
2. Wait for the alert beep.
3. Quickly enable `LongScalper_FixedLimitEntry`.
4. Strategy places the limit order.
5. If filled → brackets are managed automatically.
6. After trade completes → strategy disables itself automatically.
7. Wait for next alert and repeat.

---

## Version History Highlights

- **v2.6**: Added strong orphaned bracket protection + switched to bar-size-based adaptive stops (no more ATR).

**Author**: Albert Feng (with help from Claude)  
**Version**: 2.6  
**Primary Instrument**: MNQ (1-minute chart)

---

*Use with proper risk management. Always monitor your positions.*
