# LongScalper_TrackingLimit — User Guide

**Strategy:** `LongScalper_TrackingLimit`
**Version:** 1.1
**Platform:** NinjaTrader 8
**Mode:** Long-only, auto-fire on enable, tracking limit order
**Intended Instrument:** NQ / MNQ (works on any futures, tuned for index futures)

---

## ⚠️ CRITICAL SAFETY WARNING — READ FIRST

**Orphan orders are the #1 risk with bracketed strategies. Read this section before anything else.**

### What is an orphan order?

When this strategy fills, it attaches a **stop loss** and **profit target** (the "bracket") to your position. Those two orders are linked via OCO (One-Cancels-Other) — when one fills, the other should automatically cancel.

**BUT** — OCO only triggers when the strategy itself closes the position. If you (the human) close the position **outside the strategy** by any of these methods:

- Clicking **Flatten** on the chart toolbar
- Manually placing a sell order from the SuperDOM, chart trader, or Order Entry window
- Closing via your broker's mobile app
- Broker auto-flatten (margin call, daily loss limit, etc.)

…then the stop loss and profit target orders STAY ALIVE on the order book. Hours later, the market can drift into one of those orphan orders and **create a new, unintended SHORT position** — because the orphan was originally a "sell to close" but now fires when you're already flat.

### What v1.1 does to protect you

This strategy has a **defensive orphan cleanup** built in. On every bar update, it checks:

> "Is the account flat, but my internal state says I should be in a trade?"

If yes → it cancels all bracket orders, writes an audit row marked `ORPHAN_EXTERNAL_CLOSE`, and disables itself. You'll see this in the Output Window:

```
[ORPHAN] *** ACCOUNT FLATTENED UNEXPECTEDLY at 14:23:01.456 ***
[ORPHAN] Strategy state was InPosition but account shows flat.
[ORPHAN] Likely cause: manual close, broker action, or external trade.
```

### What YOU must still do — every single time

Even with the defensive cleanup, **always verify manually after any external action:**

1. **After ANY manual close of a position** while a strategy is running:
   - Look at the **Strategies tab on the chart** → is the strategy still showing as "Enabled"?
     - If yes → click to disable it.
   - Look at the **Orders tab** on the chart or in Control Center → are there any working/pending orders for this instrument?
     - Look especially for **sell stop** or **sell limit** orders
     - If yes → right-click → Cancel

2. **After the strategy auto-disables** (normal end of trade):
   - The Orders tab should be empty for this instrument
   - If you see anything pending → cancel it manually

3. **Before re-enabling the strategy for a new trade:**
   - Confirm Position = Flat
   - Confirm Orders tab is empty
   - The strategy itself blocks `BLOCKED_ACCOUNT_NOT_FLAT` or `BLOCKED_WORKING_ORDERS`, but don't rely on that — eyeball it yourself first

4. **Treat the Output Window as your co-pilot.** Keep it open. Read `[ORPHAN]`, `[BLOCKED_*]`, `[CLEANUP]` messages as they happen.

**The defensive cleanup is a safety net, not an excuse to be sloppy.** Trading software has many failure modes; a human checking the order book is the only reliable last line of defense.

---

## Table of Contents

1. [What This Strategy Does](#1-what-this-strategy-does)
2. [The Tracking Limit — What Makes It Different](#2-the-tracking-limit--what-makes-it-different)
3. [Prerequisites](#3-prerequisites)
4. [Installation](#4-installation)
5. [Adding to a Chart](#5-adding-to-a-chart)
6. [Parameter Reference (with detailed examples)](#6-parameter-reference-with-detailed-examples)
7. [Trade Lifecycle](#7-trade-lifecycle)
8. [Audit Log](#8-audit-log)
9. [Output Window Reference](#9-output-window-reference)
10. [Safety Features](#10-safety-features)
11. [Troubleshooting](#11-troubleshooting)
12. [Recommended Starting Setups](#12-recommended-starting-setups)

---

## 1. What This Strategy Does

LongScalper_TrackingLimit is a **long-only, one-shot, auto-fire** trading strategy for NinjaTrader 8.

When you enable it, it immediately:

1. Calculates a buy limit price slightly below current market
2. Places the limit order
3. **Continuously recalculates and modifies** the limit every second to track the market down (the "tracking" feature)
4. If filled within `OrderLifeSeconds` → attaches a stop and a profit target
5. Manages the position with a trailing stop
6. After exit (target / stop / manual / orphan), the strategy disables itself

Each "enable" = one trade attempt. After exit, you must manually re-enable for the next trade.

---

## 2. The Tracking Limit — What Makes It Different

Most limit-entry strategies place a limit and wait. If price runs away (down for a long entry), the order never fills.

**LongScalper_TrackingLimit moves the limit DOWN with the price.** Every second:

```
Recalculate target limit = currentPrice − (EntryOffsetMultiplier × avgBarSize)
If new target differs from current limit by ≥ TrackingMinTickChange ticks → modify order
```

This gives you a much better chance of getting filled on a moving market. The trade-off: you must trust the strategy to chase the market without over-extending. The `OrderLifeSeconds` timer (which is **NOT** reset by modifications) caps how long the chase can last.

### Concrete example

```
T=0s:  Price=21,100. avgBarSize=10. Offset=1pt.  Limit placed at 21,099.
T=1s:  Price=21,098. New target=21,097. Delta=2 pts (=8 ticks). Modify to 21,097.
T=2s:  Price=21,098. New target=21,097. Delta=0. No change.
T=3s:  Price=21,096. New target=21,095. Delta=2 pts. Modify to 21,095.
T=4s:  Price=21,096 — FILLED at 21,095.
```

If price had gone the other way (up), the limit would not modify upward (we only chase down — the strategy uses `if (proposedLimit < currentLimit)` semantics via the delta check, but in practice the offset means the new target is always at-or-below currentPrice).

### When the order is cancelled

- After `OrderLifeSeconds` (default 5 sec) from **first** placement, with no fill → cancelled, strategy disables. Tracking modifications do NOT reset this timer.

---

## 3. Prerequisites

- **NinjaTrader 8** installed
- A live or sim data connection for your chosen instrument
- **Sim101** account for initial testing — **do not enable on live before extensive sim verification**
- Familiarity with NT8 strategies (adding to charts, enabling/disabling)
- Read the safety warning above twice

---

## 4. Installation

### Step 1 — Open the NinjaScript Editor

Press **F11** in NinjaTrader, or **New → NinjaScript Editor**.

### Step 2 — Add the strategy file

Copy `LongScalper_TrackingLimit.cs` (or whatever filename you've saved this as) into:

```
Documents\NinjaTrader 8\bin\Custom\Strategies\
```

Or in the NinjaScript Editor: right-click **Strategies → Import** → select the file.

### Step 3 — Compile

Press **F5**. Wait for `Compile succeeded`.

If you see errors:
- **"already contains a definition for LongScalper_TrackingLimit"** → you have two copies of the file. Find and delete the old one in `bin\Custom\Strategies\`.
- **"already contains a definition for TrackingStopLossMode"** → another file is also defining that enum. The strategy uses `TrackingStopLossMode` (separate from LongScalper's `StopLossMode`) to avoid this, but check no third file is conflicting.

### Step 4 — Verify

The strategy appears as **LongScalper_TrackingLimit** in the strategies list.

---

## 5. Adding to a Chart

### Step 1 — Open a chart

- **New → Chart**
- Instrument: **NQ** (or **MNQ** for safer testing)
- Timeframe: 1-minute is recommended (the strategy adds its own 1-minute data series internally for the bar-size calculation, so anything ≥1min works fine)

### Step 2 — Add the strategy

- Right-click chart → **Strategies...**
- Click **+** → select **LongScalper_TrackingLimit**

### Step 3 — Critical settings before clicking OK

In the strategy properties dialog:

| Where | Setting | What |
|---|---|---|
| Top of dialog | **Account** | Set to **Sim101** for testing |
| Top of dialog | **Stop behavior** | **Close position** (NOT "Cancel orders") |
| 1. Trade Size | **Quantity** | Start with **1** |
| 2. Entry | All entry params | See section 6 below |
| 4. Stop Loss | **StopMode** | Default `OffsetFromFilledPrice` is recommended |
| 8. Logging | **AuditLogPath** | Default `C:\temp` — verify you can write there |

**Stop behavior = "Close position"** is critical. If set to "Cancel orders", a strategy crash will leave you with an open position and no stop loss.

### Step 4 — Enable

Tick the **Enabled** box. The strategy fires immediately. No manual trigger — this is auto-fire on enable.

To "arm" again after a trade, re-tick **Enabled**.

---

## 6. Parameter Reference (with detailed examples)

Below, every parameter is explained with concrete examples showing exactly what different values mean.

> **Tick / point reference:** NQ and MNQ both have a tick size of 0.25 points. So 1 point = 4 ticks. NQ value: 1 pt = $20/contract. MNQ value: 1 pt = $2/contract.

---

### Group 1 — Trade Size

#### `Quantity` (default: 1)

Number of contracts placed per trade.

| Value | Meaning |
|---|---|
| 1 | One contract per trade (safest, start here) |
| 2 | Two contracts per trade |
| 5 | Five contracts per trade |
| 10 | Ten contracts per trade — significant risk on NQ ($200/point loss on a 10-pt move) |

> **Recommendation:** Start with 1 on MNQ for at least 50 sim trades. Only scale up after the strategy is profitable and you understand its drawdown characteristics.

---

### Group 2 — Entry

#### `EntryOffsetMultiplier` (default: 0.10)

Determines how far below current price the limit is placed.

```
limit price = currentPrice − (EntryOffsetMultiplier × avgBarSize)
```

`avgBarSize` is the EMA of recent 1-minute bar ranges (high–low), typically 5–15 points on NQ.

| Value | On a 10-pt avgBarSize | Effect |
|---|---|---|
| **0.05** | Offset = 0.5 pt | **Very aggressive** — limit basically at-market; high fill rate but worse price |
| **0.10** | Offset = 1.0 pt | Default — small dip required, balanced |
| **0.25** | Offset = 2.5 pt | Patient — waits for noticeable pullback |
| **0.50** | Offset = 5.0 pt | Demanding — needs significant pullback, lower fill rate |
| **1.00** | Offset = 10 pt | Very patient — basically requires a full bar's worth of dip |

**Practical guidance:**
- Choppy market with small bars → lower multiplier (0.05–0.10) to get filled
- Trending market with big bars → higher multiplier (0.25+) to wait for real pullbacks
- Default 0.10 works well for most conditions

---

#### `BarSizeAveragePeriod` (default: 10)

Number of recent 1-minute bars used in the EMA volatility calculation.

| Value | Meaning |
|---|---|
| **5** | Reacts quickly to changes in volatility (last 5 min dominates) |
| **10** | Default — balances responsiveness and stability |
| **15** | Smoother — slower to react to sudden volatility spikes |
| **30** | Very smooth — uses last half hour as baseline |
| **60** | Hour-long baseline — slow to adapt |

This value also drives the stop-loss distance in `OffsetFromFilledPrice` mode, so don't set it too short or your stops become erratic.

**Practical guidance:** Stay near 10 unless you have a specific reason. Higher → more stable but less adaptive. Lower → too noisy.

---

#### `OrderLifeSeconds` (default: 5)

Total seconds the entry limit will remain working before being cancelled.

> **Important:** This timer starts when the order is **first submitted** and is **NOT reset** by tracking modifications. If you set it to 5 seconds and the order modifies 4 times, the order still cancels at second 5.

| Value | Meaning |
|---|---|
| **2** | Aggressive — only a 2-second window to fill, very tight |
| **5** | Default — typical for scalping a quick pullback |
| **10** | Patient — gives the limit more time to be touched |
| **15** | If you set this to **15**, the order has **15 seconds total** to fill from the moment it was first placed. It will be modified every TrackingIntervalSeconds during that 15-second window. After 15 seconds with no fill → cancelled, strategy disables, audit row `ENTRY_NOT_FILLED_Cancelled`. |
| **30** | Generous — half-minute window. Useful if you want to give a slow market time to reach your level. |
| **60** | Very patient — one full minute. Reasonable for low-volatility periods. |
| **300** | 5 minutes — unusually long for a scalper. The avgBarSize captured at order-placement may no longer reflect current volatility. |

**Recommendation:** 5–10 seconds for normal scalping. Increase to 15–30 if you notice frequent `ENTRY_NOT_FILLED_Cancelled` outcomes in your audit log.

---

#### `TrackingIntervalSeconds` (default: 1)

How often (wall-clock seconds) the limit price is recalculated and potentially modified.

| Value | Meaning |
|---|---|
| **1** | Default — recalculates every second. Tightest tracking. |
| **2** | Every 2 seconds. Slower response but fewer order modifications. |
| **5** | Every 5 seconds. The limit can drift further behind market between updates. |
| **15** | If you set this to **15**, the strategy only recalculates the limit price every 15 seconds. In a fast-moving market, by the time the next update happens, your limit may already be well off-market. Only useful if you have an unusual reason (e.g., broker rate-limits order modifications). |

**Recommendation:** Stay at 1 unless you have a specific issue. Modern brokers handle high modification rates without problems.

---

#### `TrackingMinTickChange` (default: 2)

Minimum number of **ticks** the new calculated limit must differ from the current order before a modification is sent.

This prevents pointless 1-tick modifications on every recalculation. NQ tick size = 0.25 pt, so 2 ticks = 0.50 pt.

| Value | NQ pts equivalent | Meaning |
|---|---|---|
| **1** | 0.25 pt | Modifies on the slightest change. Many modifications, very tight tracking. |
| **2** | 0.50 pt | Default. Balanced — modifies on meaningful moves. |
| **4** | 1.00 pt | Coarser tracking — limit can be up to 1 pt off-market before adjusting. |
| **8** | 2.00 pt | Loose tracking. Useful if too many modifications cause broker latency. |
| **15** | 3.75 pt | If you set this to **15**, the limit only modifies when the recalculated target differs from the current limit by 15 ticks (3.75 pts on NQ). In normal conditions the limit would rarely move. Probably too loose for scalping. |

**Recommendation:** 2 for active scalping. Increase to 4 if your broker complains about modification rates.

---

### Group 3 — Profit Target

#### `ProfitTargetPoints` (default: 10)

Profit target distance in points from the **expected fill price** (which equals the limit price at the moment of order placement / most recent modification).

| Value | NQ $ profit (1 contract) | MNQ $ profit | Meaning |
|---|---|---|---|
| **3** | $60 | $6 | Very tight scalp |
| **5** | $100 | $10 | Quick scalp |
| **10** | $200 | $20 | Default — moderate target |
| **15** | $300 | $30 | If you set this to **15**, the profit target sits 15 points above your fill price. On NQ that's $300/contract profit if hit. Requires a meaningful move in your favor — won't hit on micro-moves. Lower fill-to-target rate but bigger wins. |
| **20** | $400 | $40 | Larger target, suits trending markets |
| **30** | $600 | $60 | Big target — needs strong directional move |

**Recommendation:** 10 points balances win rate with reward size. Pair with your stop choice for sensible R:R.

---

### Group 4 — Stop Loss

#### `StopMode` (default: `OffsetFromFilledPrice`)

Method for computing stop distances:

- **`OffsetFromFilledPrice`** — stops scale with avgBarSize. Adaptive: tight stops on quiet days, wider stops on volatile days. **Recommended.**
- **`FixedPoints`** — stops use the literal `HardStopPoints` and `TrailDistancePoints` values regardless of volatility. Predictable but not adaptive.

---

#### `StopOffsetMultiplier` (default: 2.0)
*(used only when StopMode = OffsetFromFilledPrice)*

```
initial stop = fillPrice − (StopOffsetMultiplier × avgBarSize)
```

| Value | On 10-pt avgBarSize | Stop is | Meaning |
|---|---|---|---|
| **1.0** | 10 pts | Aggressive, tight | Likely stopped out on noise |
| **1.5** | 15 pts | Reasonable for scalping | |
| **2.0** | 20 pts | Default — comfortable buffer | |
| **2.5** | 25 pts | Patient stop, survives chop | |
| **3.0** | 30 pts | Wide — gives plenty of room. On a losing trade, you eat the full 30 pts. |

**Risk math:** If `ProfitTargetPoints = 10` and `StopOffsetMultiplier × avgBarSize = 20`, your risk/reward is 2:1 against you. You need >67% win rate to break even.

**Recommendation:** Default 2.0. Consider 1.5 if you want better R:R, but expect more stop-outs.

---

#### `TrailOffsetMultiplier` (default: 0.8)
*(used only when StopMode = OffsetFromFilledPrice)*

```
trailing distance = TrailOffsetMultiplier × avgBarSize
```

This is **always tighter than the initial stop** (which is why default 0.8 < default 2.0). Once price moves in your favor, the trailing stop locks in profit more aggressively than the initial wide stop allowed.

| Value | On 10-pt avgBarSize | Trail is | Meaning |
|---|---|---|---|
| **0.5** | 5 pts | Very tight trail | Locks profit quickly, easily kicked out by minor pullbacks |
| **0.8** | 8 pts | Default | Reasonable balance |
| **1.0** | 10 pts | Looser trail | Lets winners run longer, gives back more on reversal |
| **1.5** | 15 pts | Loose trail | For trending markets where you want to ride the move |

**Recommendation:** 0.8 default. Increase to 1.0–1.5 if you find trailing stops hitting too often in trending sessions.

---

#### `HardStopPoints` (default: 20)
*(used only when StopMode = FixedPoints)*

Fixed initial stop distance in points. Ignored in `OffsetFromFilledPrice` mode.

| Value | NQ $ risk (1 contract) | MNQ $ risk | Meaning |
|---|---|---|---|
| **5** | $100 | $10 | Very tight |
| **10** | $200 | $20 | Tight scalp stop |
| **15** | $300 | $30 | If you set this to **15** (and use FixedPoints mode), every trade has exactly a 15-point stop loss regardless of market volatility. Max loss = $300/contract on NQ, $30 on MNQ. |
| **20** | $400 | $40 | Default — common scalp stop |
| **30** | $600 | $60 | Comfortable buffer |
| **50** | $1,000 | $100 | Wide stop, big risk |

---

#### `TrailDistancePoints` (default: 8)
*(used only when StopMode = FixedPoints)*

Fixed trailing distance in points after going into profit. Ignored in `OffsetFromFilledPrice` mode.

| Value | Meaning |
|---|---|
| **3** | Very tight trail |
| **5** | Tight |
| **8** | Default |
| **10** | Moderate |
| **15** | If you set this to **15**, once price moves in your favor, the trailing stop will sit 15 points below the highest seen price. Gives wins room to run, gives back more on pullbacks. |
| **20** | Loose |

---

### Group 5 — Trailing Stop Monitor

#### `MonitorIntervalSeconds` (default: 3)

How often (wall-clock seconds) the trailing-stop check runs while in position.

| Value | Meaning |
|---|---|
| **1** | Every second — very reactive |
| **3** | Default — checks 20 times per minute |
| **5** | Every 5 seconds — fewer checks but smoother |
| **15** | If you set this to **15**, the trailing stop only updates every 15 seconds. In fast-moving markets, the stop may lag behind where it should ideally be. Use only for slow instruments. |
| **30** | Half-minute updates — too slow for scalping |

**Recommendation:** 3 is fine. Smaller values create more `[MONITOR]` log spam without meaningful improvement.

---

#### `PullbackTolerancePoints` (default: 2)

Allowable pullback (in points) from the previous check's reference price before the trailing logic considers it a "real reversal" and stops trailing for that check cycle.

| Value | Meaning |
|---|---|
| **0** | No tolerance — any tick down is a pullback. Trail never advances on a wobble. |
| **2** | Default — small noise filter |
| **5** | More tolerant — small pullbacks ignored |
| **15** | If you set this to **15**, the strategy tolerates pullbacks of up to 15 points before deciding "the move has reversed" and holding off on further trail updates. Quite loose; combined with a tight trailing stop, this would have unusual interaction. |

**Recommendation:** Default 2.

---

### Group 6 — Cleanup

#### `DisableDelaySeconds` (default: 1.5)

After the position closes (target hit, stop hit, manual exit), the strategy waits this many seconds before writing the audit log and disabling itself. This gives NinjaTrader time to fully finalize the OCO bracket.

| Value | Meaning |
|---|---|
| **0.5** | Minimal delay — may write audit before OCO finalization |
| **1.5** | Default — safe |
| **3** | Conservative — extra time |
| **15** | If you set this to **15**, after position close the strategy stays "alive" for 15 seconds before fully shutting down. The strategy won't accept a new trade during that window. Probably too long. |

**Recommendation:** Default 1.5.

---

### Group 7 — Notifications

#### `EnableSoundOnFill` (default: true)

If true, plays `Alert1.wav` when the entry fills. Helpful audio cue.

---

### Group 8 — Logging

#### `AuditLogPath` (default: `C:\temp`)

Folder where the audit log CSV is written. Auto-created if missing. The file inside is always named `LongScalper_TrackingLimit.csv`.

Examples:
- `C:\temp`
- `C:\Trading\Logs`
- `D:\NT8\Audits`

Use a path you have write access to. Avoid Program Files and other protected directories.

---

## 7. Trade Lifecycle

A complete trade sequence:

```
[1] User ticks "Enabled" on the chart's strategies panel
        │
        ▼
[2] PlaceEntryOrder() runs (auto-fire)
    SAFETY CHECKS:
      - Strategy position must be flat → else BLOCKED_NOT_FLAT
      - Account must be flat → else BLOCKED_ACCOUNT_NOT_FLAT
      - No working orders → else BLOCKED_WORKING_ORDERS
        │  pass
        ▼
[3] State = WaitingForFill
    Calculate avgBarSize (held constant for trade)
    Submit buy limit at currentPrice − offset
    Pre-declare profit target and stop loss
        │
        ▼
[4] TRACKING LOOP (every TrackingIntervalSeconds):
      - Recalc target limit from currentPrice
      - If ≥ TrackingMinTickChange ticks different → modify order
      - Update bracket levels to match new expected fill
    Total time bounded by OrderLifeSeconds.
        │
        ├─ Filled within OrderLifeSeconds? YES → step 5
        └─ NO → cancel entry, write audit ENTRY_NOT_FILLED_*, disable
        ▼
[5] State = InPosition
    Trailing stop check every MonitorIntervalSeconds.
    Stop only moves UP (toward fill), never down.
        │
        ├─ Profit target hit → exit at target → audit PROFIT_TARGET
        ├─ Stop loss hit    → exit at stop   → audit STOP_HIT
        ├─ Manual flatten   → ORPHAN detected → audit ORPHAN_EXTERNAL_CLOSE
        └─ Session close    → auto-flatten   → audit varies
        ▼
[6] State = ClosingDelay (waits DisableDelaySeconds)
    Cleanup brackets, write audit log
        │
        ▼
[7] State = Done. Strategy auto-disables.
    To trade again: re-enable on chart's Strategies panel.
```

---

## 8. Audit Log

Every trade writes a row to:

```
{AuditLogPath}\LongScalper_TrackingLimit.csv
```

### Outcomes (the "Outcome" column)

| Outcome | Meaning |
|---|---|
| `PROFIT_TARGET` | Target was hit, profitable exit |
| `STOP_HIT` | Stop loss triggered |
| `MANUAL_OR_OTHER` | Position closed by some other means |
| `ORPHAN_EXTERNAL_CLOSE` | Defensive cleanup fired; user/broker flattened externally |
| `ORPHAN_PRE_FILL` | Orphan detected before entry fill |
| `ENTRY_NOT_FILLED_Cancelled` | Order life expired without fill |
| `ENTRY_NOT_FILLED_Rejected` | Broker rejected the entry |
| `BLOCKED_NOT_FLAT` | Pre-fire check: strategy already has position |
| `BLOCKED_ACCOUNT_NOT_FLAT` | Pre-fire check: account has position elsewhere |
| `BLOCKED_WORKING_ORDERS` | Pre-fire check: working orders exist |

### Key columns

`Outcome`, `FillPrice`, `ExitPrice`, `PnLPoints`, `TrackingMods` (how many times the limit was modified), plus all parameter values for the trade.

`TrackingMods` is especially useful — high mod counts suggest a volatile market where the strategy chased a lot. Compare with `PnLPoints` to see if chasing was rewarded.

---

## 9. Output Window Reference

Open via **New → NinjaScript Output**. The strategy prints heavily; here are the key tags:

| Tag | Meaning |
|---|---|
| `[INIT]` | Startup confirmation, parameter dump |
| `[STEP1]` | Pre-fire safety checks and entry placement |
| `[TRACK]` | Tracking-limit modifications |
| `[TIMEOUT]` | OrderLifeSeconds expired |
| `[ORDER]` | Order state updates |
| `[EXEC]` | Order executions (fills) |
| `[MONITOR]` | Trailing stop checks |
| `[ORPHAN]` | Defensive cleanup fired — ALWAYS READ THESE |
| `[CLEANUP]` | Bracket cancellation activity |
| `[AUDIT]` | CSV log write confirmation |
| `[DISABLE]` | Strategy disabling |
| `[POSITION]` | Position state updates |
| `[CLOSE]` | Position close detected |
| `[TERM]` | Strategy termination |

---

## 10. Safety Features

### Pre-fire safety checks

Before placing any order, FIRE is blocked if:

1. **Strategy already has a position** → `BLOCKED_NOT_FLAT`. Prevents stacking.
2. **Account already has a position on this instrument** → `BLOCKED_ACCOUNT_NOT_FLAT`. Even if not opened by this strategy.
3. **There are working orders on this instrument** → `BLOCKED_WORKING_ORDERS`. Prevents conflicts with manual orders.

In each case, the strategy disables itself and logs the outcome. **You must manually flatten / cancel before re-enabling.**

### Defensive orphan check

Detailed at the top of this guide. Runs on every bar update while strategy state is `InPosition` or `WaitingForFill`.

### Order timeout

If the entry doesn't fill within `OrderLifeSeconds` (even after multiple tracking modifications), it's cancelled. No ghost orders.

### Session close auto-flatten

`IsExitOnSessionCloseStrategy = true` means NinjaTrader flattens any open position 30 seconds before session close, regardless of strategy state.

---

## 11. Troubleshooting

### Strategy doesn't fire when I enable it

Check the Output Window:

- `BLOCKED_NOT_FLAT` → strategy already shows a position; reset by disabling/re-enabling
- `BLOCKED_ACCOUNT_NOT_FLAT` → manually flatten the account first
- `BLOCKED_WORKING_ORDERS` → cancel any existing working orders for this instrument
- `Could not get current price` → check data feed
- `Could not calculate bar size` → need more 1-minute bars; wait or reload chart

### `ENTRY_NOT_FILLED_Cancelled` every time

Order keeps timing out without fill. Try:

- Increase `OrderLifeSeconds` to 10 or 15
- Lower `EntryOffsetMultiplier` (e.g., 0.05) to place the limit closer to market
- Check if market is one-directional and never pulled back enough to touch the limit

### Many `[TRACK] Modifying limit` lines but never fills

Market is moving down faster than the strategy can chase. Either:
- Accept it (the strategy is correctly refusing to chase forever; OrderLifeSeconds will cancel)
- Lower `EntryOffsetMultiplier` so the limit is closer to current price

### Stop hits immediately after fill

Stop is too tight relative to current volatility. Either:
- Increase `StopOffsetMultiplier` (try 2.5 or 3.0)
- Switch to `FixedPoints` mode with `HardStopPoints = 30`

### Orphan check fired but I didn't close manually

Something else closed the position — possibly:
- Session close (look for the 4 PM ET timestamp)
- Broker auto-flatten (margin call?)
- Daily loss limit
- Internet/data connection dropped briefly

Check your broker's trade log to verify.

### "ORPHAN_PRE_FILL" outcome

Strategy detected the entry order died (cancelled, rejected) while bracket orders were still pending. The defensive cleanup fired before they could become real orphans. This is the safety net working correctly.

---

## 12. Recommended Starting Setups

### Setup A — Conservative MNQ scalp (recommended start)

```
Quantity:                  1   (MNQ — $2/pt risk per contract)
EntryOffsetMultiplier:     0.10
BarSizeAveragePeriod:      10
OrderLifeSeconds:          5
TrackingIntervalSeconds:   1
TrackingMinTickChange:     2
ProfitTargetPoints:        10
StopMode:                  OffsetFromFilledPrice
StopOffsetMultiplier:      2.0
TrailOffsetMultiplier:     0.8
MonitorIntervalSeconds:    3
PullbackTolerancePoints:   2
DisableDelaySeconds:       1.5
EnableSoundOnFill:         true
AuditLogPath:              C:\temp
```

Expected: max risk ~$40/trade, max win ~$20/trade. Use this for 50+ sim trades before tweaking.

### Setup B — Faster, more aggressive

```
EntryOffsetMultiplier:     0.05
OrderLifeSeconds:          3
ProfitTargetPoints:        5
StopOffsetMultiplier:      1.5
TrailOffsetMultiplier:     0.6
```

Tighter everything. Higher fill rate, lower R:R per trade. Suits high-frequency scalping.

### Setup C — Patient swing scalp

```
EntryOffsetMultiplier:     0.25
OrderLifeSeconds:          15
ProfitTargetPoints:        20
StopOffsetMultiplier:      2.5
TrailOffsetMultiplier:     1.0
```

Waits for bigger pullbacks, aims for bigger moves. Lower trade frequency.

---

## Quick Reference Card

```
INSTALL:    Copy .cs file to Documents\NinjaTrader 8\bin\Custom\Strategies\
            Open NinjaScript Editor, F5 to compile.

ADD:        Right-click chart → Strategies → + → LongScalper_TrackingLimit.
            Set Account to Sim101, Stop behavior to "Close position".
            Configure parameters per section 6.
            Click OK, tick Enabled.

LIFECYCLE:  Enable → auto-fires → tracks limit down → fills →
            manages with trailing stop → exits → auto-disables.
            Re-enable for next trade.

SAFETY:     After ANY manual close: verify strategy disabled +
            Orders tab empty. Read [ORPHAN] messages in Output.

LOGS:       Output Window for live events.
            C:\temp\LongScalper_TrackingLimit.csv for trade history.

KEY OUTCOMES IN CSV:
            PROFIT_TARGET / STOP_HIT — normal completion
            ORPHAN_EXTERNAL_CLOSE — defensive cleanup fired
            ENTRY_NOT_FILLED_Cancelled — order timed out
            BLOCKED_* — pre-fire safety check failed

REMEMBER:   Read section "CRITICAL SAFETY WARNING" before every session.
            One enable = one trade. Re-enable for the next signal.
```

---

*End of guide.*
