# LongScalper Pro — User Guide

**Strategy:** `LongScalper_FixedLimitEntry_Pro`
**Version:** 1.0
**Platform:** NinjaTrader 8
**Mode:** Manual / signal-driven long-only scalper with WPF control panel

---

## Table of Contents

1. [What This Strategy Does](#1-what-this-strategy-does)
2. [How It Differs From LongScalper v2.6](#2-how-it-differs-from-longscalper-v26)
3. [Prerequisites](#3-prerequisites)
4. [Installation](#4-installation)
5. [Adding the Strategy to a Chart](#5-adding-the-strategy-to-a-chart)
6. [Parameter Reference](#6-parameter-reference)
7. [Recommended Settings](#7-recommended-settings)
8. [Using the Control Panel](#8-using-the-control-panel)
9. [Trade Lifecycle](#9-trade-lifecycle)
10. [Audit Log](#10-audit-log)
11. [Safety Features](#11-safety-features)
12. [Troubleshooting](#12-troubleshooting)
13. [Future API Integration](#13-future-api-integration)

---

## 1. What This Strategy Does

LongScalper Pro is a **long-only, one-shot, manual-trigger** trading strategy for NinjaTrader 8.
It places **one bracketed buy order** when you click the FIRE button on its control panel, manages the position with a profit target and a trailing stop, and then disables itself after the trade closes.

Each "session" of the strategy = **one trade**.

It is designed for traders who:

- Receive an external trade signal (today: human eyeball, future: API feed)
- Want to fire a single trade per signal with full risk management
- Want a visible control panel rather than driving the strategy via the Strategies tab toggle
- Don't want continuous algo trading — they want a manual launcher with safety rails

---

## 2. How It Differs From LongScalper v2.6

| Aspect | v2.6 (`LongScalper`) | Pro (`LongScalper_FixedLimitEntry_Pro`) |
|---|---|---|
| **Trigger** | Auto-fires the moment the strategy is enabled | Waits for FIRE button click (or `RequestFire()` API call) |
| **UI** | Strategies tab only | Pop-out WPF control panel with status, log, and buttons |
| **Trade lifecycle** | One-shot, auto-disables after exit | One-shot, auto-disables after exit (same) |
| **Force close** | None — user must flatten manually | Force Close button cancels entry or market-exits position |
| **Audit log file** | `C:\temp\LongScalper.csv` | `C:\temp\LongScalper_Pro.csv` (separate file) |
| **Trading logic** | Bar-size-based stops, trailing, orphan check | **Identical** — same proven logic, lifted from v2.6 |

The two strategies are **independent**. Both can be installed at the same time. They do not share state or logs.

---

## 3. Prerequisites

Before installing this strategy, you should have:

- **NinjaTrader 8** installed and running
- A working **data connection** (live or sim feed for NQ futures)
- A **simulation account** for testing (Sim101 is the default)
- Familiarity with adding strategies to charts in NinjaTrader
- The source file `LongScalper_FixedLimitEntry_Pro.cs`

> **Important:** Always test new strategies in **simulation** first. Do not enable on a live account until you've verified at least 5–10 successful sim trades.

---

## 4. Installation

### Step 1 — Open the NinjaScript Editor

1. In NinjaTrader, click **New → NinjaScript Editor** (or press `F11`).

### Step 2 — Add the strategy file

1. In the left-hand tree, expand **Strategies**.
2. Right-click **Strategies → Add → New Strategy**.
3. **Cancel** the wizard that appears (we don't want a generated template).
4. Manually copy `LongScalper_FixedLimitEntry_Pro.cs` into the folder:
   ```
   Documents\NinjaTrader 8\bin\Custom\Strategies\
   ```

   Alternative: Right-click in the **Strategies** folder of the editor and choose **Import** → select the `.cs` file.

### Step 3 — Compile

1. Press **F5** in the NinjaScript Editor (or click **Compile**).
2. Watch the bottom output panel for errors.
3. If compilation succeeds, you'll see:
   ```
   Compile succeeded
   ```
4. If it fails, fix any errors and recompile. Common issues:
   - Wrong NinjaTrader version (this is built for NT8)
   - Missing `using` statements (file should compile as-is)

### Step 4 — Verify

After successful compile, the strategy appears in the strategies list as:

> **LongScalper_FixedLimitEntry_Pro**

---

## 5. Adding the Strategy to a Chart

### Step 1 — Open a chart

1. **New → Chart**
2. Set instrument: **NQ** (or **MNQ** for micros)
3. Set timeframe: **any timeframe** — bar period doesn't drive entry; it just provides price context. **1-minute** is recommended because the strategy adds its own 1-minute data series internally.

### Step 2 — Open the Strategies panel on the chart

1. Right-click on the chart → **Strategies...**
2. Click the **+** button (Add) at the top.
3. Select **LongScalper_FixedLimitEntry_Pro** from the list.

### Step 3 — Configure parameters

The strategy properties dialog opens with these groups:

```
1. Trade Size
2. Entry
3. Profit Target
4. Stop Loss
5. Trailing
6. Cleanup
7. Notifications
8. Logging
```

See [Parameter Reference](#6-parameter-reference) for full details on each setting.

### Step 4 — Critical settings before you click OK

Before pressing OK, make sure these are correct:

| Setting | Where | What |
|---|---|---|
| **Account** | Top of dialog | Set to **Sim101** for testing |
| **Quantity** | 1. Trade Size | Number of contracts (start with 1) |
| **LimitMode** | 2. Entry | `OffsetFromLastPrice` or `FixedPrice` |
| **FixedLimitPrice** | 2. Entry | Set if using FixedPrice mode (otherwise ignored) |
| **AuditLogPath** | 8. Logging | Default `C:\temp` — make sure you can write there |

### Step 5 — Click OK

The strategy is now **added** to the chart but **not yet enabled**.

### Step 6 — Enable the strategy

1. In the Strategies panel on the chart, find the row for `LongScalper_FixedLimitEntry_Pro`.
2. Tick the **Enabled** checkbox.
3. The control panel pops out on screen (top-left by default).

You are now ready to fire trades.

---

## 6. Parameter Reference

### 1. Trade Size

| Parameter | Default | Range | Description |
|---|---|---|---|
| **Quantity** | 1 | 1 – ∞ | Number of contracts placed per trade. Start with 1 for testing. |

---

### 2. Entry

| Parameter | Default | Range | Description |
|---|---|---|---|
| **LimitMode** | OffsetFromLastPrice | enum | How the limit price is calculated. See below. |
| **FixedLimitPrice** | 0 | ≥ 0 | The exact price to use if `LimitMode = FixedPrice`. **Must be > 0** if FixedPrice is selected. Ignored otherwise. |
| **EntryOffsetMultiplier** | 0.10 | 0.01 – 10.0 | In OffsetFromLastPrice mode: limit = lastPrice − (this × avgBarSize). |
| **BarSizeAveragePeriod** | 10 | 2 – 100 | Number of recent 1-minute bars used to calculate the EMA "average bar size" used for entry and stops. |
| **OrderLifeSeconds** | 5 | 1 – 86400 | If the limit order doesn't fill within this many seconds, it's cancelled. The strategy then disables. |

**LimitMode explained:**

- **OffsetFromLastPrice** — Aggressive limit just below the current price. Good when you want a small pullback fill. Limit = `lastPrice − (EntryOffsetMultiplier × avgBarSize)`. With a `0.10` multiplier and avgBarSize ~10pts, you'd buy ~1pt below market.

- **FixedPrice** — You specify the exact limit price in `FixedLimitPrice`. Use this when your J9 signal gives you a specific price level to enter at.

---

### 3. Profit Target

| Parameter | Default | Range | Description |
|---|---|---|---|
| **ProfitTargetPoints** | 10 | 1 – 1000 | Profit target = expectedFillPrice + this many points. For NQ, 1 point = 4 ticks ($20). |

---

### 4. Stop Loss

| Parameter | Default | Range | Description |
|---|---|---|---|
| **StopMode** | OffsetFromFilledPrice | enum | Method for calculating stop distances. |
| **StopOffsetMultiplier** | 2.0 | 0.1 – 20.0 | (OffsetFromFilledPrice mode) Initial stop = fillPrice − (avgBarSize × this). |
| **TrailOffsetMultiplier** | 0.8 | 0.1 – 20.0 | (OffsetFromFilledPrice mode) Trailing distance = avgBarSize × this. |
| **HardStopPoints** | 20 | 1 – 200 | (FixedPoints mode) Initial stop in fixed points below fill. |
| **TrailDistancePoints** | 8 | 1 – 200 | (FixedPoints mode) Trailing stop distance in fixed points. |

**StopMode explained:**

- **OffsetFromFilledPrice** (recommended) — Stops adapt to market volatility. On a calm day, stops are tight; on a volatile day, stops are wide. Same concept as the entry offset.

- **FixedPoints** — Always uses `HardStopPoints` and `TrailDistancePoints` regardless of volatility. Predictable but not adaptive.

> The trailing stop only ever moves **upward** (in your favor). It never widens once a position is in profit.

---

### 5. Trailing

| Parameter | Default | Range | Description |
|---|---|---|---|
| **MonitorIntervalSeconds** | 3 | 1 – 3600 | How often the trailing-stop check runs. |
| **PullbackTolerancePoints** | 2 | 0 – 50 | If price has pulled back more than this from the last check, the strategy stops trailing for this cycle and lets the existing stop handle the exit. |

---

### 6. Cleanup

| Parameter | Default | Range | Description |
|---|---|---|---|
| **DisableDelaySeconds** | 1.5 | 0 – 10 | Delay between position close and strategy auto-disable. Gives NinjaTrader time to finalize the OCO bracket. |

---

### 7. Notifications

| Parameter | Default | Description |
|---|---|---|
| **EnableSoundOnFill** | true | Plays `Alert1.wav` when the entry fills. |

---

### 8. Logging

| Parameter | Default | Description |
|---|---|---|
| **AuditLogPath** | `C:\temp` | Folder for the audit CSV. Auto-created if it doesn't exist. The file inside is always `LongScalper_Pro.csv`. |

---

## 7. Recommended Settings

### For NQ (Nasdaq E-mini, 1pt = $20, tick = 0.25 = $5)

```
Quantity:                 1   (start small)
LimitMode:                FixedPrice    (signal-driven)
FixedLimitPrice:          [from your J9 signal]
EntryOffsetMultiplier:    0.10  (only used if OffsetFromLastPrice)
BarSizeAveragePeriod:     10
OrderLifeSeconds:         5
ProfitTargetPoints:       10    (~$200/contract)
StopMode:                 OffsetFromFilledPrice
StopOffsetMultiplier:     2.0
TrailOffsetMultiplier:    0.8
MonitorIntervalSeconds:   3
PullbackTolerancePoints:  2
DisableDelaySeconds:      1.5
EnableSoundOnFill:        true
AuditLogPath:             C:\temp
```

### For MNQ (Micro NQ, 1pt = $2, tick = 0.25 = $0.50)

Same as NQ — the strategy doesn't care about contract size in dollars, only ticks/points. Use MNQ for safer testing.

---

## 8. Using the Control Panel

When you enable the strategy, a window titled **"LongScalper Pro"** appears. It contains:

```
┌─ LongScalper Pro ────────────────────┐
│         LongScalper Pro              │
│                                      │
│  State:       Idle                   │
│  Position:    Flat qty=0             │
│  Fill:        --                     │
│  Stop:        --                     │
│  Target:      --                     │
│  Unrealized:  --                     │
│                                      │
│  ┌──────────────────────────────┐    │
│  │     🔥  FIRE LONG  🔥         │    │
│  └──────────────────────────────┘    │
│  ┌──────────────────────────────┐    │
│  │  Force Close Position        │    │
│  └──────────────────────────────┘    │
│                                      │
│  Log                                 │
│  ┌──────────────────────────────┐    │
│  │ 09:30:01  Panel opened       │    │
│  │ ...                          │    │
│  └──────────────────────────────┘    │
└──────────────────────────────────────┘
```

### Status fields

| Field | Meaning |
|---|---|
| **State** | Internal state machine: `Idle`, `WaitingForFill`, `InPosition`, `ClosingDelay`, `Done` |
| **Position** | Current account position direction and quantity |
| **Fill** | Actual fill price after entry (`--` until filled) |
| **Stop** | Current stop price (updates as the trailing stop moves up) |
| **Target** | Profit target price |
| **Unrealized** | Current unrealized P&L in points (only while in position) |

### Buttons

| Button | When enabled | What it does |
|---|---|---|
| **🔥 FIRE LONG 🔥** | When State = `Idle` | Places one bracketed buy at the configured limit price. |
| **Force Close Position** | When State = `WaitingForFill` or `InPosition` | If unfilled: cancels the entry. If filled: submits a market sell to flatten immediately. |

### Log

A scrolling log shows the last 200 strategy events with timestamps. New entries are appended at the bottom.

### Closing the panel

You can X the window — the strategy continues running without it. The panel will not reopen automatically. To get it back, disable and re-enable the strategy on the chart.

---

## 9. Trade Lifecycle

A complete trade follows this sequence:

```
[1] Strategy enabled  ──▶  Panel opens, State = Idle
                              │
[2] User clicks FIRE   ◀──────┘
        │
        ▼
[3] State = WaitingForFill
    Buy limit submitted at calculated price
    Profit target & stop loss pre-declared (OCO bracket)
        │
        ├─ Fill within OrderLifeSeconds?
        │      ├─ YES ─▶  State = InPosition
        │      └─ NO  ─▶  Cancel entry, write audit, disable strategy
        ▼
[4] State = InPosition
    Trailing stop active — checks every MonitorIntervalSeconds
    Stop only moves UP (toward fill price), never down
        │
        ├─ Profit target hit? ─────▶  Exit at target
        ├─ Stop loss hit?      ─────▶  Exit at stop
        ├─ User clicks Force Close? ▶  Market exit
        └─ Account flattened externally? ▶ Orphan detected, cleanup
        │
        ▼
[5] State = ClosingDelay (waits DisableDelaySeconds)
        │
        ▼
[6] Audit log written
    Strategy auto-disables (State = Done)
        │
        ▼
[7] Panel closes when strategy is disabled
```

To trade again, **re-enable the strategy** on the Strategies panel of the chart.

---

## 10. Audit Log

Every trade outcome is recorded in:

```
C:\temp\LongScalper_Pro.csv
```

(Path is configurable via `AuditLogPath`.)

### Columns

| Column | Description |
|---|---|
| `Timestamp` | When the row was written |
| `Instrument` | e.g. `NQ 12-25` |
| `Outcome` | `PROFIT_TARGET`, `STOP_HIT`, `MANUAL_OR_OTHER`, `ORPHAN_EXTERNAL_CLOSE`, `ENTRY_NOT_FILLED_*`, or various `BLOCKED_*` codes |
| `FireTime` | When the entry order was placed |
| `FillTime` | When the entry filled (blank if never filled) |
| `ExitTime` | When the position closed |
| `LastPriceAtFire` | Market price when FIRE was clicked |
| `AvgBarSize` | EMA bar size used in calculations |
| `EntryOffsetMultiplier` | The multiplier setting at fire time |
| `LimitMode` | `OffsetFromLastPrice` or `FixedPrice` |
| `LimitPrice` | The calculated limit price |
| `FillPrice` | Actual fill price |
| `ExitPrice` | Actual exit price |
| `PnLPoints` | Profit/loss in points (positive = profit) |
| ... | Plus all parameter values for the trade — useful for parameter analysis |

The CSV is append-only. New trades add to the bottom. The header is written once when the file is first created.

---

## 11. Safety Features

The strategy has multiple guard rails inherited from v2.6:

### Pre-fire safety checks

Before placing an order, FIRE will be blocked if:

1. **Strategy already has a position** — prevents stacking.
2. **Account already has a position on this instrument** — even if not opened by this strategy.
3. **There are working orders on this instrument** — prevents conflicting orders.

In each case, the strategy disables itself and writes a `BLOCKED_*` outcome to the audit log.

### Defensive orphan check

On every bar update, while the strategy thinks it has an active trade:

- If the **account** is flat but the strategy state is `InPosition` or `WaitingForFill`, something happened outside the strategy's control (manual close, broker action, external trade).
- The strategy detects this, cancels any leftover bracket orders, writes a `ORPHAN_*` outcome, and disables.

This prevents the dangerous scenario where:

1. Manual close flattens position
2. Stop and target orders remain alive on the order book
3. Market reaches one → creates an unintended new position

### Order timeout

If the limit order doesn't fill within `OrderLifeSeconds`, it's automatically cancelled and the strategy disables. No "ghost" working orders.

### Session close protection

`IsExitOnSessionCloseStrategy = true` ensures NinjaTrader will flatten any open position 30 seconds before session close.

### Force Close button

Manual kill switch for when something looks wrong.

---

## 12. Troubleshooting

### Compilation errors

| Error | Likely cause | Fix |
|---|---|---|
| `The type or namespace 'X' could not be found` | Wrong NinjaTrader version | Verify NT8 (not NT7) |
| Duplicate enum or class names | v2.6 is also installed and conflicting | Should not happen — Pro uses different enum names (`ProEntryLimitMode`, `ProStopLossMode`) |

### Panel doesn't appear

| Symptom | Cause | Fix |
|---|---|---|
| Strategy enabled but no panel | `Application.Current` is null on this NT install | Strategy still works — drive it via `RequestFire()` calls (see [Section 13](#13-future-api-integration)) |
| Panel appeared once but won't reopen | You X'd it; strategy continues without UI | Disable and re-enable strategy |

### FIRE doesn't fire

| Symptom | Likely cause |
|---|---|
| Click FIRE, nothing happens | State is not `Idle`. Check status display. |
| FIRE works but order rejected | Account not selected, insufficient margin, market closed |
| `BLOCKED_NOT_FLAT` in log | Already have a position — flatten first, re-enable |
| `BLOCKED_INVALID_FIXED_PRICE` | LimitMode=FixedPrice but FixedLimitPrice ≤ 0 |

### Trade doesn't fill

| Symptom | Cause | Fix |
|---|---|---|
| `ENTRY_NOT_FILLED_Cancelled` | OrderLifeSeconds expired before fill | Increase `OrderLifeSeconds`, or move limit closer to market |
| Limit price way off market | Wrong `FixedLimitPrice` set | Double-check the value before re-arming |

### Position opens but stop/target wrong

| Symptom | Cause | Fix |
|---|---|---|
| Stop too tight, gets hit immediately | `StopOffsetMultiplier` too small | Increase to 2.5 or 3.0, or switch to `FixedPoints` with a wider stop |
| Stop too wide | Volatility spiked | Stops are based on avgBarSize at entry; this is intentional |

### Output window is your friend

The strategy logs **every action** to NinjaTrader's output window:

- **Tools → Output**

Look for `[STEP1]`, `[ORDER]`, `[EXEC]`, `[MONITOR]`, `[ORPHAN]`, `[CLEANUP]`, `[FIRE]`, `[FORCE]`, `[PANEL]`, `[AUDIT]` prefixes. They tell you exactly what's happening and why.

---

## 13. Future API Integration

The strategy is designed so a future external signal source (REST API, file feed, J9 hook, etc.) can call into it instead of the human clicking FIRE.

### The hook

A public method on the strategy:

```csharp
public void RequestFire()
{
    fireRequested = true;
}
```

### How an external signal will use it

When you build the J9 signal listener, it will:

1. Find the running strategy instance (via NinjaTrader's `Account.Strategies` collection or a similar lookup).
2. Cast it to `LongScalper_FixedLimitEntry_Pro`.
3. Optionally set `FixedLimitPrice` to the signal's price level.
4. Call `strategy.RequestFire()`.

Example skeleton (for the future signal-listener AddOn):

```csharp
var strat = FindRunningStrategy("LongScalper_FixedLimitEntry_Pro");
if (strat != null)
{
    strat.FixedLimitPrice = signalPrice;
    strat.RequestFire();
}
```

The execution path from there is **identical** to a button click — same safety checks, same bracket logic, same audit log. The button is simply a manual stand-in for the future signal feed.

This means: any improvements you make to the trading logic (better stops, smarter trailing, etc.) automatically apply to both the manual workflow and the future automated workflow.

---

## Quick Reference Card

```
INSTALL:    Copy .cs file to Documents\NinjaTrader 8\bin\Custom\Strategies\
            Open NinjaScript Editor, press F5 to compile.

ADD:        Right-click chart → Strategies → + → LongScalper_FixedLimitEntry_Pro
            Set Quantity, LimitMode, FixedLimitPrice (if FixedPrice mode)
            Click OK, tick Enabled.

FIRE:       Click 🔥 FIRE LONG 🔥 on the panel.

EMERGENCY:  Click "Force Close Position".

RE-ARM:     After trade closes, re-enable strategy on chart's Strategies panel.

LOGS:       NT Output Window for live events.
            C:\temp\LongScalper_Pro.csv for trade history.

REMEMBER:   One enable = one trade. Re-enable for the next signal.
```

---

*End of guide.*
