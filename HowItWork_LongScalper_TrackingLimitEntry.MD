# LongScalper_TrackingLimitEntry v1.1 - How It Works

## Overview

**LongScalper_TrackingLimitEntry** is a **long-only scalping strategy** designed to catch quick upward moves (pullback recoveries) in volatile instruments, especially **MNQ (Micro Nasdaq futures)**.

Its core innovation is the **Tracking Limit Entry** — instead of placing a static limit order, the strategy continuously adjusts (tracks) the buy limit price downward as the market moves, giving you a better (lower) entry price while the order is still alive.

It is **not** a fully autonomous robot — it is a **semi-automated assistant**. The strategy handles entry + brackets, but the trader decides **when to enable** it (usually triggered by the companion alert `scalper_TrendPullbackAlert_beta`).

---

## Main Purpose

- Enter long on pullback recoveries with a **dynamically improving (lower) limit price**.
- Automatically attach profit target and stop loss immediately upon fill.
- Trail the stop loss to protect profits.
- Provide strong defensive logic to avoid dangerous orphaned orders.

---

## How It Works (Simple Version for Developers)

1. When enabled → submits a **buy limit order** below current price (offset = `EntryOffsetMultiplier × avgBarSize`).
2. Every `TrackingIntervalSeconds` (default 1s), it recalculates the ideal limit and modifies the order if the price difference is large enough.
3. Upon fill → immediately attaches PT and SL brackets based on the actual fill price.
4. Continuously trails the stop loss.
5. Strong orphan-protection logic: if the account becomes flat unexpectedly, the strategy cleans up and disables itself.

---

## Detailed Operation (For Users)

### 1. Entry Logic (Tracking Limit)

- The strategy calculates `avgBarSize` = EMA of 1-minute bar ranges (High-Low).
- Places a **Buy Limit** at: `Current Price - (EntryOffsetMultiplier × avgBarSize)`.
- Every second it checks again and **moves the limit lower** if price goes up (improving your entry).
- The order is cancelled if not filled within `OrderLifeSeconds`.

### 2. Exit Logic

- **Profit Target**: Fixed points above fill price (`ProfitTargetPoints`).
- **Stop Loss**: Two modes available (see parameters below).
- **Trailing Stop**: Moves up as price moves in your favor.

### 3. Safety Features

- Pre-checks before placing any order (strategy flat + account flat + no working orders).
- Orphaned bracket cleanup (`CheckForOrphanedState`).
- Auto-disable after trade completes.

---

## Important Usage Notes

**This strategy is primarily designed and optimized for MNQ on 1-minute charts.**

- For other instruments or timeframes, you **must** adjust parameters carefully (especially `EntryOffsetMultiplier`, `StopOffsetMultiplier`, `ProfitTargetPoints`, etc.).
- **Best used together with** `scalper_TrendPullbackAlert_beta` — wait for the alert beep, then enable this strategy.

### Critical Rules After Entry

- Once the strategy has opened a position and attached brackets, **do not** add manual orders on top.
- If price goes strongly against you:
  - Either **let the stop loss handle it**, **or**
  - Manually flatten the entire position + cancel all orders, then **disable and re-enable** the strategy for the next setup.
- Adding manual orders while the strategy is managing brackets can cause conflicts and unexpected behavior.

---

## Parameter Guide

### 1. Trade Size
- **Quantity**: Number of contracts (default 1).

### 2. Entry
- **EntryOffsetMultiplier** (0.10): How far below current price to place the initial limit. Higher = more aggressive (further from market).
- **BarSizeAveragePeriod** (10): Lookback for calculating average bar volatility.
- **OrderLifeSeconds** (5): Maximum time the limit order lives before cancellation.
- **TrackingIntervalSeconds** (1): How often the limit price is recalculated.
- **TrackingMinTickChange** (2): Minimum price change required to modify the order.

### 3. Profit Target
- **ProfitTargetPoints** (10): Take-profit distance in points.

### 4. Stop Loss & Trailing
- **StopMode**:
  - `OffsetFromFilledPrice` (recommended): Uses `avgBarSize` × multiplier (adaptive).
  - `FixedPoints`: Uses static point values.
- **StopOffsetMultiplier** (2.0): Initial stop distance (× avgBarSize).
- **TrailOffsetMultiplier** (0.8): Trailing stop distance (× avgBarSize) — tighter than initial stop.
- **HardStopPoints** (20): Used only in FixedPoints mode.
- **TrailDistancePoints** (8): Used only in FixedPoints mode.

### 5. Trailing Behavior
- **MonitorIntervalSeconds** (3): How often trailing logic runs.
- **PullbackTolerancePoints** (2): How much pullback to ignore before considering it a reversal.

### 6. Cleanup
- **DisableDelaySeconds** (1.5): Short delay after exit before strategy disables itself.

### 7. Others
- **EnableSoundOnFill**: Play sound when entry is filled.
- **AuditLogPath**: Folder for trade logging.

---

## Recommended Workflow

1. Run `scalper_TrendPullbackAlert_beta` on 1-min MNQ chart.
2. Hear the alert → quickly enable `LongScalper_TrackingLimitEntry`.
3. Strategy attempts entry with tracking limit.
4. If filled → brackets are automatically managed.
5. After trade ends → strategy disables itself.
6. Repeat on next alert.

---

**Author**: Albert Feng (with help from Claude)  
**Version**: 1.1 (2026)  
**Primary Instrument**: MNQ (1-minute chart)

---

*Happy trading! Always use proper risk management.*
