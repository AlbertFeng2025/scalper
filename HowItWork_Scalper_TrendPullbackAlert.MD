# scalper_TrendPullbackAlert_beta v3.9 (Modified) - How It Works

## Overview

**scalper_TrendPullbackAlert_beta** is a **notification-only** tool (it places **no orders**).  

It scans for high-probability **buy-the-dip** setups in uptrending markets and alerts you with sound + chart markers when a good moment to enter arrives. You then manually (or via LongScalper) decide whether to take the trade.

---

## Main Purpose

- Identify **trend + meaningful pullback + early recovery** setups.
- Alert the trader in real-time so they can react quickly.
- Designed to work as the **trigger companion** for `LongScalper_FixedLimitEntry` or `LongScalper_TrackingLimitEntry`.

---

## How It Works

### The 3 Gates (All Must Pass)

**Gate 1 — TREND**
- Finds the highest high (`H`) in the lookback window.
- Checks if the **close** of that bar was **above** its SMA (`TrendMaPeriod`).
- Ensures the pullback is happening in a real uptrend.

**Gate 2 — SIZE**
- Measures the range from `H` to the lowest low (`L`) after it.
- Requires the pullback to be at least `MinPullbackPoints` (default 100 on MNQ).

**Gate 3 — RECOVERY + MOMENTUM**
- Current price must have recovered between `AlertLowPct` (25%) and `AlertHighPct` (50%) of the pullback.
- **AND** current close must be above its short SMA (`CloseUpMaPeriod` = 3).

Only when all three gates pass (and safety rules are satisfied) does the strategy fire the alert.

### Additional Smart Features

- **Truncated Lookback**: After an alert, it limits future scans to bars after the last alert (prevents repeated signals on the same setup).
- **Daily Reset**: Resets at session start.
- **Cooldowns**: Minimum bars between alerts + time-based cooldown.
- **Forward Result Tracking**: Logs performance 5 bars after each alert (including max high/low reached).

---

## Important Usage Notes

**This alert system is primarily designed and optimized for MNQ on 1-minute charts.**

- Performance on other instruments or timeframes may vary significantly — adjust parameters carefully.
- **Best Workflow**: Run this strategy + one of the LongScalper strategies on the same chart.
- When you hear the beep → quickly enable the LongScalper (it will handle entry + brackets).

### Market Regime Tips (from experience)

- **Down-dominated / strong bearish days** → Increase `MinPullbackPoints` to 100–150 and consider raising `AlertLowPct` to 0.30+ for stronger confirmation.
- **Choppy days** → Default 100 points works well.
- **Super strong uptrend days** → You can lower `MinPullbackPoints` to 50–80.

---

## Parameter Guide

### 1. Trend Filter
- **TrendMaPeriod** (30): SMA period for trend confirmation.

### 2. Pullback Size
- **LookbackBars** (50): Maximum bars to look back for H/L.
- **MinPullbackPoints** (100): Minimum range (H - L) required.

### 3. Trigger Zone
- **AlertLowPct** (0.25): Minimum recovery % to alert.
- **AlertHighPct** (0.50): Maximum recovery % to alert.
- **CloseUpMaPeriod** (3): Short SMA — price must be trading above this for momentum confirmation.

### 4. Alert Spacing & Safety
- **MinBarsBetweenAlerts** (10): Minimum bars between alerts.
- **CooldownMinutes** (5): Additional time-based safety cooldown.

### 5. Result Tracking
- **ResultEvalBars** (5): How many bars forward to evaluate each alert.

### 6. Time Window & Visuals
- **ActiveStartTime / ActiveEndTime**: Trading hours filter.
- **EnableChartMarkers**: Draw arrow + label on chart when alert fires.
- **AlertSoundCount** (3) & **AlertReminderSecs** (1): Beep behavior.

---

## Recommended Workflow

1. Add `scalper_TrendPullbackAlert_beta` to your MNQ 1-minute chart.
2. Add `LongScalper_FixedLimitEntry` or `LongScalper_TrackingLimitEntry` (disabled).
3. When you hear the **3 beeps** + see the green arrow:
   - Quickly enable the LongScalper.
4. The LongScalper will attempt entry with limit order + brackets.
5. After the trade finishes, the LongScalper disables itself automatically.
6. Wait for the next alert.

---

## Parameter Defaults (Current)

- TrendMaPeriod = 30
- LookbackBars = 50
- MinPullbackPoints = 100
- CloseUpMaPeriod = 3
- AlertLowPct = 0.25
- AlertHighPct = 0.50
- MinBarsBetweenAlerts = 10

---

**Version**: 3.9 (Modified 2026-05-07)  
**Author**: Albert Feng (with help from Claude)  
**Primary Instrument**: MNQ (1-minute chart)

---

*This is a notification tool only. Always use proper risk management and monitor your trades.*

Happy trading!
