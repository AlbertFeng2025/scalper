# SHORT Repeat Layer 2 — Complete User Guide
### `Scalper_Shortrepeat_Layer2` · NinjaTrader 8 · MNQ

*A full operator's manual: what it does, the theory, every parameter, and how it behaves in every situation (day rollover, disconnect, crash, disable/enable).*

---

> ## ⚠️ TWO SETTINGS TO LEAVE AT THEIR DEFAULTS
>
> Two parameters control features that were **never backtested** and have **known problems** if turned on. They are **OFF by default — keep them that way.** The strategy you validated uses **market entry + fixed stop**; that is what the research and live results are based on.
>
> **1. `EnableTrailingStop` — leave `false`.**
> If enabled, it *corrupts the bit string that IS the edge.* A trailing stop fills at a trailed price, but the code records the outcome from the order name only ("Stop" → recorded as a full loss). So a trailing stop that locked in a **profit** would be logged as a **−32-point loss**, feeding the filter wrong data. Do not enable.
>
> **2. `UseMarketEntry` — leave `true` (i.e. do NOT switch to limit entry).**
> Limit entry is untested and has a latent **hang risk**: if a limit order doesn't fill, the slice can freeze the pipeline (no new slices) until it fills or the 24h life expires. Live slippage analysis (2026-07-17) also showed market entry costs only ~0.84 pt/trade and guarantees fills — better than risking missed fills on a reversion strategy that must catch its signals. Keep market entry.
>
> **If you ever want to use either feature, they must first be fixed and tested** (the trailing-stop fix is the same fill-based-P&L change discussed for slippage tracking). Until then: **`EnableTrailingStop = false`, `UseMarketEntry = true`.**

---

## TABLE OF CONTENTS
1. What this strategy is (in one paragraph)
2. The core idea — meta-labeling, with a worked bit-string example
3. How a single slice works
4. The two-layer filter (`10?` → `00`) explained
5. The complete pipeline, step by step
6. Every parameter — what it does, default, and whether it needs a recompile
7. The qty rule (position sizing)
8. The safety systems (loss breaker, account guard, trading hours)
9. Behavior in every situation (rollover, disconnect, crash, disable/enable)
10. The log files — how to read them
11. Recommended settings & go-live checklist
12. Quick troubleshooting

---

## 1. What this strategy is

`Scalper_Shortrepeat_Layer2` is a **short-only, mean-reversion scalping strategy** for MNQ. It does **not** trade every signal. Instead it continuously runs tiny "test trades" in memory (called **slices**), records whether each would have won or lost as a **bit** (`1`=win, `0`=loss), and only places a **real order** when the recent sequence of bits matches a specific **pattern** that research showed precedes a high-probability reversion. This pattern-on-outcomes approach is called **meta-labeling**.

The whole design rests on one validated edge: **after a fast up-move, MNQ tends to snap back down** ("elevator down, stairs up"). Shorting that snap-back, but *only when the recent outcome sequence confirms an oscillating regime*, is the edge.

---

## 2. The core idea — meta-labeling (with a bit-string example)

### The problem with normal strategies
A normal strategy says: "when X happens, trade." But most signals are ~50/50. Meta-labeling adds a second layer: **"given that a signal happened, should I actually take it?"** The primary model generates candidates; a second model decides which to keep.

### How this strategy does it
- **Primary "model" = the slice.** Every ~1 second, the strategy opens an imaginary short (a slice) with a fixed stop and target and watches which is hit first. Win → `1`, loss → `0`. This produces a continuous **raw string** of bits, e.g.:

  ```
  raw:  0 1 1 0 1 0 0 1 0 1 ...
  ```

  Each bit = "if I had shorted right then, would the 19-pt target have hit before the 32-pt stop?"

- **Meta-label = the filter.** The strategy scans the raw string for a pattern that historically preceded a *real* winning short. Only when that pattern appears does it place a **real order**.

### Worked example
Say the recent raw string ends in:

```
... 1 0 1
```

- `1` = a slice won (reverted down — the target hit)
- `0` = a slice lost (the stop hit — price kept rising)
- `1` = reverted again

That `...101` ending says: *"the market just lost, then recovered — it's oscillating and just reverted."* That is the **V-recovery** the filter looks for. It is a *reversion* signature, not a *momentum* one. The strategy's research showed patterns like this precede real winning shorts ~75% of the time, versus ~65% for a random slice.

**Key insight:** the bits are *direction-relative*. In the SHORT book, `1` always means "this short would have won." The filter reads the **shape of recent wins and losses**, not price levels or indicators. That's why it survived when SMA, MACD, and candle triggers all failed — the edge is in the *sequence structure of outcomes*, not in any single price feature.

---

## 3. How a single slice works

Every slice is an imaginary short trade:

1. **Entry:** at the current **ask** price.
2. **Stop:** `ask + 32` points (above — where a short loses).
3. **Target:** `ask − 19` points (below — where a short wins).
4. **Resolve:** whichever is hit first. Target first → bit `1`. Stop first → bit `0`. If both are touched in the same instant, **stop wins the tie** (records `0` — the conservative choice).
5. **Record** the bit into the raw string, then immediately look for the next slice.

Slices are throttled to **one per second** (`CheckIntervalSeconds`), so the strategy doesn't record thousands of near-identical bits.

**The 32/19 geometry is deliberate and validated.** A near target (19) with a wide stop (32) harvests the frequent small reversions while surviving noise. Break-even for this geometry is **62.7%** = 32 ÷ (32+19). The filter lifts the win rate well above that.

---

## 4. The two-layer filter: `10?` → `00`

This is the heart of the strategy. Two patterns must line up.

### The wildcard language
- `0` = a literal loss
- `1` = a literal win
- `*` = **one or more** `0`s (`0+`)
- `?` = **one or more** `1`s (`1+`)
- Patterns match the **tail** (the most recent bits).

Examples: `10?` means `1`, then `0`, then one-or-more `1`s = `101`, `1011`, `10111`… (a loss followed by a recovery of at least one win).

### The two filters (defaults)
- **Filter 1 (`Filter1Pattern` = `10?`):** the *trigger*. It fires when the raw string's tail is a **V-recovery** — a win, a loss, then a recovery. This is the reversion signature.
- **Filter 2 (`Filter2Pattern` = `00`):** the *confirmation gate*. After F1 fires, the strategy watches the **filtered outcome string** (the bits that occur right after each F1 match). Only when *that* string ends in `00` does the strategy actually arm a real trade.

### Why two layers?
A single pattern (`10?` alone) just re-samples the ~65% baseline. The second gate (`00`) is **load-bearing**: it confirms the regime is genuinely oscillating (recent post-signal outcomes were losses, meaning the market is choppy and due to revert). Adding F2 lifts the win rate from ~66% to ~75% and roughly halves the drawdown. **Do not remove the F2 gate** — it's what makes the edge real.

### The flow in bits
```
raw string:        ... 1 0 1        <- F1 (10?) matches the tail  ->  "watch next outcome"
filtered string:   ... 0 0          <- F2 (00) matches           ->  ARM a real short
```
When both align, the **next** slice becomes a **real order** instead of an imaginary one.

---

## 5. The complete pipeline, step by step

Each tick, in order:

1. **Trading-day rollover check** (only when not mid-slice): if the clock crossed **3 PM PT**, reset the pipeline for the new trading day (see §9).
2. **Resolve the open slice** if one is running (record its bit).
3. **Guards before starting a real trade** (in order):
   - **Trading hours** — outside 9:30–11:30 ET? → the slice still runs and records a bit, but can't become a real order (`OBS_OUTSIDE_HOURS`).
   - **Account busy** — is this instrument already in a position/order on this account? → demote to observation (`OBS_ACCOUNT_BUSY`).
   - **Qty = 0** — did the qty rule say "skip"? → observe only (`OBS_QTY_SKIP`).
4. **Start the next slice** — real order if armed *and* all guards pass; otherwise an imaginary slice.
5. **Record** the bit and update the filter strings.

The pipeline runs **24 hours a day** (so it can warm up overnight), but **real orders** are only placed inside the trading-hours window.

---

## 6. Every parameter

### Trading behavior
| Parameter | Default | What it does | Recompile to change? |
|---|---|---|---|
| `EnableRealOrder` | **false** | Master switch. `false` = observation only (logs `WOULDBE_TRADE` where it *would* have traded). `true` = places real orders. | No — dialog |
| `Filter1Pattern` | `10?` | The trigger pattern (F1). | No — dialog |
| `Filter2Pattern` | `00` | The confirmation gate (F2). | No — dialog |
| `StopLossPoints` | `32` | Slice/real stop distance. | No — dialog |
| `ProfitTargetPoints` | `19` | Slice/real target distance. | No — dialog |
| `UseMarketEntry` | `true` | Market vs limit entry for real orders. ⚠️ **Keep `true` — limit entry is untested (hang risk). See warning at top.** | No — dialog |
| `CheckIntervalSeconds` | `1` | Min seconds between slices (the throttle). | No — dialog |

### Position sizing
| Parameter | Default | What it does | Recompile? |
|---|---|---|---|
| `BaseQuantity` | `1` | Base lot size. | No — dialog |
| `EnableQtyIncrement` | **false** | Turns the loss-based sizing rule on/off (see §7). | No — dialog |
| *(the qty multiplier table)* | `{10:2, 100:2, 1000:2, 10000:0…}` | The actual sizing rule — **hardcoded in the source.** | **YES — edit .cs + F5** |

### Trading hours (the researched window)
| Parameter | Default | Meaning |
|---|---|---|
| `EnableTradingHours` | `true` | Restrict real orders to the window. Slicing still runs 24h. |
| `TradingStartHour/Minute` | `9` / `30` | 9:30 ET = 6:30 PT (morning_open start) |
| `TradingEndHour/Minute` | `11` / `30` | 11:30 ET = 8:30 PT (morning_open end) |

### Safety & recovery
| Parameter | Default | What it does |
|---|---|---|
| `MaxRealLossInARow` | `5` | Circuit breaker: after this many consecutive real losses today, stop trading for the day. (Backtest MCL is ~2, but **plan for 5–6** live.) |
| `GapToleranceMinutes` | `7` | On restart, gaps under this many *market-open* minutes → RESUME. |
| `GapCeilingHours` | `4` | Wall-clock gap over this → FRESH start. |
| `StrategyLifeMinutes` | `1440` | 24h lifetime cap (no longer the main control). |
| `MaxTotalSliceCount` | `100000` | High cap; not the main control. |
| `LogBaseName` | `scalper_SHORTrepeat_Layer2` | **The log filename prefix. Critical: each running strategy needs a UNIQUE name, or two strategies corrupt each other's recovery history.** |

> **The three parameters you'll actually touch:** `EnableRealOrder` (observation → live), `BaseQuantity` (size), and `LogBaseName` (when running a second copy). All three are dialog changes — no recompile.

---

## 7. The qty rule (position sizing)

When `EnableQtyIncrement = true`, the strategy adjusts size based on **today's** real-trade outcomes (the `sessionRealOutcome` string):

```
("10",      2)   after a win then 1 loss   -> size x2
("100",     2)   after a win then 2 losses  -> size x2
("1000",    2)   after a win then 3 losses  -> size x2
("10000",   0)   after 4 losses  -> SKIP (observe only, no real order)
("100000",  0)   after 5 losses  -> SKIP
("1000000", 0)   after 6 losses  -> SKIP
```

**The leading `1` is a deliberate safety guard.** A loss-run *from the day's open* (`000…`, e.g. from a bug) matches **nothing** in the table → stays at base size, never doubles into a disaster. Doubling only happens *after* at least one win has anchored the day.

**Important (as of the 2026-07-16 fix):** the qty history now **survives reconnects** — on RESUME it rebuilds today's real-trade string from the log rather than wiping it. You'll see `[QTY RESUME] ... sessionReal='...'` in the diag log confirming this. (See the separate Disable/Enable guide, Part G.)

**The qty table itself is hardcoded** — changing the multipliers or patterns requires editing the `.cs` and recompiling (F5). Turning the rule on/off (`EnableQtyIncrement`) and the base size (`BaseQuantity`) are dialog settings.

---

## 8. The safety systems

**Loss-streak breaker (`MaxRealLossInARow`, default 5).** Counts *today's* consecutive real losses. Hit the limit → no more real trades until the next trading day. It resets at the 3 PM PT rollover. It correctly counts only *today's* real trades (skips observation rows and stops at yesterday), and it survives reconnects (rebuilt from the log via the same shared reader the qty rule uses).

**Account guard.** Before a real order, it checks whether *this instrument* is already in a position/order **on this account**. If so, it demotes the slice to observation (`OBS_ACCOUNT_BUSY`). This lets a LONG and SHORT book coexist on one account (first-to-fire wins the slot). *Note: if you run LONG and SHORT on separate accounts, this guard is inactive between them — each gets a full uncontaminated fire count.*

**Trading-hours gate.** Real orders only inside 9:30–11:30 ET. Outside, slices still run and record bits (`OBS_OUTSIDE_HOURS`) — this is the overnight warm-up that fills the pipeline before the morning.

**EOD flatten.** `IsExitOnSessionCloseStrategy = true` — any open position is flattened at session close. *Session close is defined by the data series' Trading Hours template — use the CME **ETH** (full electronic) session, not RTH (see the setup checklist), so this fires at the real futures close and the overnight warm-up is treated as in-session.*

---

## 9. Behavior in every situation

This is the most important operational section. The strategy's design principle: **the log file on disk is the source of truth.** On any restart, a new instance reads its own log and decides what to do.

### 9.1 Trading-day rollover (3 PM PT)
The research resets the pipeline every trading day, so the strategy does too.
- At **3 PM PT**, the pipeline **clears**: rawString, filter strings, armed/waiting flags all reset to empty. The breaker resets to 0. The qty session resets.
- **What is KEPT:** the cumulative real-trade audit (`realTradeOutcome`).
- **Timing subtlety:** if a slice is mid-flight at 3 PM, the reset waits until that slice completes (its bit is attributed to the *old* day). The next slice starts the new day fresh. Correct.
- **Log:** `[TRADING DAY ROLLOVER] … 3:00 PM PT boundary` and `[BREAKER RESET]`.

### 9.2 Brief disconnect (feed blip, comes back in minutes)
- NT auto-restarts the strategy → new instance.
- Gap is small (< 7 market-open min, < 4h) → **RESUME**.
- rawString, filter state, breaker, and qty history all **restored from the log**. Nothing lost.
- **Log:** `[RESUME] gap small: N market-open min …`.

### 9.3 Manual disable → enable (a few minutes)
- **Identical to a brief disconnect** — NT builds a new instance either way.
- Small gap → **RESUME**. Pipeline continues seamlessly.
- If you changed `BaseQuantity` or `EnableRealOrder` while disabled, the new value takes effect immediately (no recompile).
- **Log:** `[RESUME] …` and, if real trades happened today, `[QTY RESUME] … sessionReal='…'`.

### 9.4 Long outage / left disabled for hours
- Wall-clock gap > 4h (`GapCeilingHours`) → **FRESH START**.
- A stale bit string from hours ago isn't valid warm-up, so it starts a **new log file** with an empty pipeline.
- The real-trade audit is preserved for the breaker.
- **Log:** `[FRESH START] wall-clock gap N.Nh exceeds ceiling 4h …`.

### 9.5 Hard crash (NT hangs / force-closed / power loss)
- Same as any restart: new instance reads the log.
- If you get it back quickly → **RESUME**, rawString continues with **zero gaps** (proven live 2026-07-14: crash mid-session, rawString continued 1→254 unbroken).
- **Untested edge:** a crash with a **real position open mid-trade** — verify in sim before a real account whether the fill is recognized on the new instance.

### 9.6 Disable before 3 PM PT, enable after
- New instance detects the snapshot is from a previous trading day.
- **Starts the new day EMPTY** (correct — matches the research reset), even though it's a RESUME.
- **Log:** `[RESUME ACROSS DAY BOUNDARY] … starting the new day EMPTY`.

### The decision rule (in order)
```
no log / empty file                          -> FRESH
a weekend falls in the gap                   -> FRESH
wall-clock gap > 4h (GapCeilingHours)        -> FRESH
market-open minutes in gap > 7 (GapTolerance)-> FRESH
otherwise                                    -> RESUME
```

---

## 10. The log files

Two files per session, in the log folder:

**Main CSV** (`<LogBaseName>_<timestamp>.csv`): one row per slice.
Columns: `timestamp, slice_num, side, quantity, entry_price, exit_price, realized_pnl, win_loss_bit, rawString, filter1Outcome, realTradeOutcome`

The **`side` column** tells you what each row is:
| `side` value | meaning |
|---|---|
| `FAKE_Short` | ordinary imaginary slice (filter not armed) |
| `Short` | **a real order was placed** |
| `WOULDBE_TRADE` | filter armed, but `EnableRealOrder=false` (would have traded) |
| `OBS_OUTSIDE_HOURS` | armed but outside the trading window |
| `OBS_ACCOUNT_BUSY` | armed but the account was already in this instrument |
| `OBS_QTY_SKIP` | armed but the qty rule said skip (qty 0) |

> **`slice_num` restarts at 1 on every new instance** while rawString continues from the log. So `slice_num` will jump back to 1 mid-file after a reconnect. **Order events by timestamp, not slice_num.**

**Diag log** (`…-diagLog.csv`): human-readable events — `ready(...)`, `[RESUME]`/`[FRESH START]`, `[QTY RESUME]`, `[TRADING DAY ROLLOVER]`, `[BREAKER RESET]`, guard demotions. **This is the first place to look** to confirm the strategy is behaving.

---

## 11. Recommended settings & go-live checklist

### Validated live-eligible configuration
```
EnableRealOrder     = (see checklist)
Filter1Pattern      = 10?
Filter2Pattern      = 00
StopLossPoints      = 32
ProfitTargetPoints  = 19
EnableTradingHours  = true   (9:30–11:30 ET)
BaseQuantity        = 1
EnableQtyIncrement  = false  (until live-validated)
MaxRealLossInARow   = 5
UseMarketEntry      = true   (⚠️ keep true — limit entry untested, hang risk)
EnableTrailingStop  = false  (⚠️ keep false — corrupts the bit string if enabled)
LogBaseName         = unique per running copy
```

### Go-live checklist (observation → real)
1. Run with **`EnableRealOrder = false`** first. Confirm `WOULDBE_TRADE` rows appear in the window and the diag log shows clean RESUME/rollover behavior.
2. Review a few days of would-be trades — do they match the research win rate?
3. Set **`EnableRealOrder = true`**, keep **`BaseQuantity = 1`** and **`EnableQtyIncrement = false`**.
4. Watch **slippage**: compare the real fill price to the signal price in the log. This is the #1 live risk — the edge assumes clean fills.
5. Keep `EnableQtyIncrement = false` until you have **weeks** of clean live results. Only then consider turning sizing on (and test the qty code on a separate sim account first).

### NT settings
- Change `ConnectionLossHandling` from `Recalculate` → **`Keep`** to reduce restart churn from feed flapping.
- If running a second copy (e.g. an A/B test), give it a **different `LogBaseName`** — otherwise the two corrupt each other's recovery history.

---

## 12. Quick troubleshooting

| Symptom | Likely cause | Check |
|---|---|---|
| No real trades all morning | Filter never armed, or signals landed outside window | Diag log: are there `WOULDBE_`/`OBS_OUTSIDE_HOURS` rows? The filter needs its `10?`→`00` pattern to line up inside 9:30–11:30 ET. |
| Empty log file | Wrong log folder, or a very quiet feed | Confirm `LogBaseName` and the log path; check the feed is live. |
| rawString jumps back to a low number | Normal after a FRESH start or new trading day | Look for `[FRESH START]` or `[TRADING DAY ROLLOVER]` in the diag log. |
| `slice_num` restarted at 1 | Normal — per-instance counter after any restart | Order by timestamp, not slice_num. |
| Changed a setting, nothing happened | Didn't disable first, or edited a hardcoded value | Params lock while enabled — disable, change, enable. Qty *table* & filter patterns need F5 recompile. |
| Qty rule never doubles | `EnableQtyIncrement=false`, or reconnects (pre-fix) | Confirm the toggle is on; confirm `[QTY RESUME]` shows a rebuilt string. |
| Breaker won't let it trade | Hit `MaxRealLossInARow` today | Diag log for the loss count; it resets at 3 PM PT. |

---

*This guide documents `Scalper_Shortrepeat_Layer2` as of 2026-07-16 (post qty-persistence fix). Parameter defaults read directly from the strategy's `SetDefaults`. For disconnect/disable mechanics in even more detail, see `Strategy_DisableEnable_and_Settings_Guide.md`. For the research behind the edge, see `MNQ_research_summary_2026-07-15.md`.*

---
<div style="page-break-before: always"></div>

# 📋 SETUP CHECKLIST — print this page

**Use this every time you add `Scalper_Shortrepeat_Layer2` to the Strategies tab.**
Go through **every** row, top to bottom. Don't assume a default is correct — read it and confirm. A wrong setting here is the #1 cause of live problems. The rows below follow the order you'll see them in the "add strategy" dialog: **first the NT-level settings (instrument, account, data), then the strategy's own parameters.**

**Strategy:** ____________________  **Account:** ____________________  **Date:** __________

---

### ⓪ Before you start — the right window

| ✓ | Step | Note |
|---|---|---|
| ☐ | Open the **Control Center → Strategies tab** | NOT a chart strategy. This runs account-based. |
| ☐ | Confirm your **data feed is connected** | Control Center → Connections. The strategy needs live data to slice. |
| ☐ | Click **New Strategy** (or right-click → Add) | The strategy configuration dialog opens. |
| ☐ | Select **`Scalper_Shortrepeat_Layer2`** from the list | Make sure it's the SHORT L2 — not LONG, not L3. Check the name carefully. |

### ① Data Series — the top section of the dialog (easy to forget)

*These are NinjaTrader's own fields, set in the "Data Series" section of the add-strategy window. Field names below match the NT8 help guide.*

| ✓ | Field | Set to | Note |
|---|---|---|---|
| ☐ | **Instrument** | **the front-month MNQ** (e.g. `MNQ 09-26`) | ⚠️ **Pick the ACTIVE contract month.** An expired/wrong month = no data, no trades. Roll to the new front month near expiry. |
| ☐ | **Type** | **Minute** (or as you tested) | The bar type NT builds. The strategy slices on live price updates regardless, but pick a small/standard type — a 1-minute series is typical. Match what you used in testing. |
| ☐ | **Value** | e.g. `1` | The period for the Type (e.g. 1-minute). |
| ☐ | **Days to load** | **at least `10`, `30` recommended** | How much history NT loads on start. Not critical for this strategy (it warms up from its own log + overnight slices), but load some so it initializes cleanly. |
| ☐ | **Load data based on / From–To** | default (days) | Leave on the days-based load unless you have a reason. |
| ☐ | **Trading Hours (template)** | ⚠️ **`CME US Index Futures ETH`** (or the full electronic/24h session for MNQ) — **NOT an RTH-only template** | **This matters — see the box below.** The strategy slices overnight for warm-up and uses this template to (a) judge reconnect gaps and (b) time the EOD flatten. An RTH-only template would break overnight handling. |

> **⚠️ Why the Trading Hours template MUST be the full electronic session (ETH), not RTH:**
> This is separate from the strategy's own `EnableTradingHours` (which limits *real orders* to 9:30–11:30 ET). The **NT session template** controls two things in the code:
> 1. **Reconnect gap math.** On restart, the strategy counts "market-open minutes" in the gap (via the template) to decide RESUME vs FRESH. With an **RTH-only** template, the platform thinks the market is *closed* overnight — so an overnight disconnect counts as 0 open-minutes and the strategy could wrongly RESUME across a big gap, stitching a broken overnight bit string.
> 2. **EOD flatten timing.** `IsExitOnSessionCloseStrategy=true` flattens at *session close*. With RTH that's 4:00 PM ET; with the **ETH** template it's the real futures close (~5:00 PM ET, with the ~1-hour maintenance break). The strategy is written assuming the ETH session (its code notes the "~1h maintenance break counts as 0 open-minutes").
>
> **Use the CME Index Futures ETH template (or NT's full 24-hour session for MNQ). Do NOT use an RTH/day-session template.** If unsure, confirm the template shows the overnight session as *open* (e.g. Globex hours), since the strategy warms up overnight.


### ② Account — WHICH account trades (the most costly mistake)

| ✓ | Field | Set to | Note |
|---|---|---|---|
| ☐ | **Account** | **the intended account** (e.g. `Sim101`, `SIMshortA`, or your live account) | ⚠️ **THE most important non-strategy setting.** Picking a live account when you meant sim — or the wrong sim — is the worst error. Read it twice. |
| ☐ | Sim vs Live is correct | confirm the account name | If testing, it MUST be a Sim account. Only go live deliberately. |
| ☐ | This account isn't already running another copy on MNQ | check the Strategies tab | Two strategies on the same account + instrument interact (account-busy guard demotes one). Intentional for LONG+SHORT coexistence; unintentional = confusion. |

### ③ NT execution properties — the lower half of the dialog

*These are NinjaTrader's built-in strategy properties (below your strategy's own parameters). Most stay at default; the ⚠️ rows matter.*

| ✓ | Field | Set to | Note |
|---|---|---|---|
| ☐ | **Calculate** | **`On each tick`** | ⚠️ **Important.** The strategy slices on price updates — it needs tick-level calculation. `On bar close` would starve the slice loop. |
| ☐ | **Start behavior** | `Wait until flat` | The strategy default. Won't assume an existing position on start. |
| ☐ | **Bars Required to Trade** | low (e.g. `1`–`20`) | Minimum bars before trading. Keep low — this strategy doesn't depend on a big bar history (it uses its own rawString/log). A high value would delay the morning start. |
| ☐ | **Maximum Bars Look Back** | `TwoHundredFiftySix` | The memory-friendly default. Fine as-is. |
| ☐ | **Set order quantity** | **`Strategy`** | ⚠️ Use the size the strategy computes (BaseQuantity / qty rule), NOT "Default Quantity". If set to Default, NT overrides your sizing. |
| ☐ | **Exit on close** | `True` | Flattens any open position at session end. Keep on. |
| ☐ | **Order Fill Resolution / Slippage / Fill limit on touch** | defaults | These affect *historical* backtest fills only, not live. Leave default. |
| ☐ | **Time in force** | `GTC` or `Day` | Either is fine; GTC is typical for brackets. |
| ☐ | **Enabled** | set **True** last | This actually starts the strategy — do it after everything else is confirmed. |

---

### A. The edge parameters — these define the strategy. Do not change unless you know why.

| ✓ | Parameter | Set to | Note |
|---|---|---|---|
| ☐ | `Filter1Pattern` | **`10?`** | **TESTED.** The V-recovery trigger. Leave as-is. |
| ☐ | `Filter2Pattern` | **`00`** | **TESTED.** The confirmation gate. Leave as-is — removing it kills the edge. |
| ☐ | `StopLossPoints` | **`32`** | **TESTED.** The researched SHORT stop. |
| ☐ | `ProfitTargetPoints` | **`19`** | **TESTED.** The researched SHORT target. Break-even = 62.7%. |
| ☐ | `CheckIntervalSeconds` | **`1`** | **TESTED.** One slice per second. Leave as-is. |

### B. Trading window — confirm these match your intended hours.

| ✓ | Parameter | Set to | Note |
|---|---|---|---|
| ☐ | `EnableTradingHours` | **`true`** | Restricts *real orders* to the window (slicing still runs 24h for warm-up). |
| ☐ | `TradingStartHour` / `Minute` | **`9` / `30`** | 9:30 ET. **Times are in ET (New York).** |
| ☐ | `TradingEndHour` / `Minute` | **`11` / `30`** | 11:30 ET = the researched morning window. *Set later if you want a longer window — but only the morning is backtested.* |

### C. Real orders & sizing — set deliberately for this account.

| ✓ | Parameter | Set to | Note |
|---|---|---|---|
| ☐ | `EnableRealOrder` | **`false`** to start | `false` = observation (logs `WOULDBE_TRADE`). Flip to `true` only when you're ready to trade real. |
| ☐ | `BaseQuantity` | **`1`** | Lot size. Keep at 1 until the edge is live-validated. |
| ☐ | `EnableQtyIncrement` | **`false`** | The loss-doubling rule. Keep OFF until validated on a separate sim account. |
| ☐ | `MaxRealLossInARow` | **`5`** | Daily circuit breaker. Backtest MCL≈2, but plan for 5–6 live. |

### D. ⚠️ Untested features — MUST stay at these values.

| ✓ | Parameter | Set to | Note |
|---|---|---|---|
| ☐ | `UseMarketEntry` | **`true`** | ⚠️ **NOT TESTED as limit.** Keep `true` (market). Limit entry can hang the pipeline. |
| ☐ | `EnableTrailingStop` | **`false`** | ⚠️ **NOT TESTED.** Keep `false`. If enabled it corrupts the bit string (the edge). |
| ☐ | `LimitOffsetPoints` | `5` | Only used if limit entry — leave at default (won't matter while `UseMarketEntry=true`). |
| ☐ | `TrailDistancePoints` | `10` | Only used if trailing on — leave at default (won't matter while trail is off). |

### E. Logging — the most common setup mistake. Read carefully.

| ✓ | Parameter | Set to | Note |
|---|---|---|---|
| ☐ | `LogBaseName` | **a UNIQUE name per running copy** | See the box below. |
| ☐ | `LogFolder` | `C:\temp` (default) | Confirm this folder exists and you know where to find the logs. |

> **How `LogBaseName` works — important:**
> The system **automatically adds a date-and-time stamp** to whatever you type. So if you enter
> `SHORT_simA`, the actual file becomes:
> `SHORT_simA_2026-07-17_06-30-00.csv` (plus a matching `…-diagLog.csv`).
> **You do NOT add a date yourself — the system does it.**
>
> **The rule:** every strategy running at the same time needs a **different `LogBaseName`.**
> If two strategies share the same base name, they read each other's logs on restart and
> **corrupt each other's recovery** (wrong rawString, wrong breaker, wrong qty).
> - Running SHORT on simA and SHORT on simB? → give them **different** names (e.g. `SHORT_simA`, `SHORT_simB`).
> - Running your A/B test (qty off vs qty on)? → **different** names, always.

### F. Final confirmation — after you click OK and Enable.

| ✓ | Check | How |
|---|---|---|
| ☐ | **Right account + instrument** | Glance at the Strategies-tab row: it shows the account and instrument. Confirm they're what you intended (sim vs live, correct MNQ month). |
| ☐ | Strategy is enabled and running | Green/enabled in the Strategies tab. |
| ☐ | The `ready (...)` line shows YOUR settings | Open the `…-diagLog.csv`; the newest `ready (SHORT)` line prints EnableRealOrder, Filter1, Filter2, Stop, Target. Confirm they match what you set. |
| ☐ | Slices are being recorded | The main `.csv` is growing with `FAKE_Short` rows. |
| ☐ | (If real) watch the first fills | Compare fill price to signal price in the log — this is the #1 live risk. |

---

**Signed off by:** ____________________  **Time:** __________

*Reminder: a correct setup is the whole game. Nine of the rows above are "leave at default," but you still confirm each one — because the one you skip is the one that bites. When in doubt, stop and check the diag log's `ready` line before letting it trade real.*

---
<div style="page-break-before: always"></div>

# APPENDIX — Complete Parameter Reference & Review

*Every setting you can see when adding this strategy, cross-checked against the strategy code and the NinjaTrader 8 help guide. Each row says whether to **SET** it, **CONFIRM** it, or **LEAVE DEFAULT** — and why. "Leave default (not used by this strategy)" means the field belongs to NinjaTrader's generic engine and this strategy's logic doesn't depend on it.*

## Part 1 — The strategy's OWN parameters (defined in the code)

These appear under the strategy's parameter groups (Hours, Timing, Entry, Bracket, Filter, Quantity, Limits, Logging).

| Parameter (group) | Default | Action | Why |
|---|---|---|---|
| **Filter 1 Pattern** (5) | `10?` | **LEAVE** | The tested V-recovery trigger. Core edge. |
| **Filter 2 Pattern** (5) | `00` | **LEAVE** | The tested confirmation gate. Removing it kills the edge. |
| **Enable Real Order** (5) | `false` | **SET** | `false`=observation, `true`=live. The main go-live switch. |
| **Stop loss (points)** (4) | `32` | **LEAVE** | Tested SHORT geometry. |
| **Profit target (points)** (4) | `19` | **LEAVE** | Tested SHORT geometry. BE=62.7%. |
| **Enable Trailing Stop** (4) | `false` | **LEAVE (must)** | ⚠️ Untested; corrupts the bit string if on. |
| **Trail distance (points)** (4) | `10` | **LEAVE** | Only used if trailing on (it isn't). |
| **Use market entry (else limit)** (3) | `true` | **LEAVE (must)** | ⚠️ Limit entry untested (hang risk). Keep market. |
| **Limit offset (points)** (3) | `5` | **LEAVE** | Only used if limit entry (it isn't). |
| **Enable Trading Hours filter** (1) | `true` | **CONFIRM** | Restricts real orders to the window. |
| **Start hour / minute (NY/Eastern)** (1) | `9` / `30` | **CONFIRM** | 9:30 ET window start. Times are Eastern. |
| **End hour / minute (NY/Eastern)** (1) | `11` / `30` | **CONFIRM** | 11:30 ET window end (researched morning). |
| **Check interval (seconds)** (2) | `1` | **LEAVE** | One slice/second throttle. Tested. |
| **Strategy life (minutes)** (2) | `1440` | **LEAVE** | 24h lifetime cap; not the main control. |
| **Base quantity (fixed)** (6) | `1` | **SET** | Your lot size. Keep 1 until validated. |
| **Enable Qty Increment** (6) | `false` | **SET** | The `{'0':2}` sizing rule. Keep off until validated. |
| **Max Real Loss In A Row** (7) | `5` | **CONFIRM** | Daily circuit breaker. |
| **Max Total Slice Count** (7) | `100000` | **LEAVE** | High cap; not the main control. |
| **Log Folder** (8) | `C:\temp` | **CONFIRM** | Where logs are written. Make sure it exists. |
| **Log Base Name** (8) | `scalper_SHORTrepeat_Layer2` | **SET** | ⚠️ UNIQUE per running copy. System appends a date-time stamp automatically. |
| **Gap Tolerance (min)** | `7` | **LEAVE** | RESUME/FRESH threshold. Tested. |
| **Gap Ceiling (hours)** | `4` | **LEAVE** | RESUME/FRESH ceiling. Tested. |

## Part 2 — NinjaTrader built-in properties THIS STRATEGY SETS in code

The strategy pre-configures these in `SetDefaults`, so the dialog already shows the right value. **Do not change them** — the strategy depends on them.

| NT Property | Strategy sets it to | Action | Why it matters |
|---|---|---|---|
| **Calculate** | `On each tick` | **LEAVE (must)** | ⚠️ The slice loop runs on every tick. `On bar close` would starve it — no slices. |
| **Bars Required to Trade** | `0` | **LEAVE** | The strategy warms up from its own log/rawString, not from bar history. Don't raise it or you delay the start. |
| **Exit on close** (`IsExitOnSessionCloseStrategy`) | `true` | **LEAVE** | Flattens at session close. Required behavior. |
| **Exit on close seconds** | `30` | **LEAVE** | Flattens 30s before session close. |
| **Entries per direction** | `1` | **LEAVE** | One position at a time. Matches the sequential-slice design. |
| **Entry handling** | `AllEntries` | **LEAVE** | Standard for this single-entry design. |
| **Time in force** | `GTC` | **LEAVE** | Bracket orders rest as GTC. Fine. |
| **Start behavior** | `Wait until flat` | **LEAVE** | Won't assume an existing position on start. |
| **Realtime error handling** | `StopCancelClose` | **LEAVE** | Safe error handling. |
| **Stop/target handling** | `PerEntryExecution` | **LEAVE** | Correct for the bracket design. |
| **Set order quantity** | `Strategy` (implied by design) | **CONFIRM** | Must use the strategy's computed size, NOT "Default Quantity". If the dialog shows "Default Quantity", switch it to "Strategy". |

## Part 3 — NinjaTrader fields you SET at add-time (not strategy logic, but you must choose)

| NT Field | Action | Why |
|---|---|---|
| **Instrument** | **SET** | Front-month MNQ. Wrong/expired month = no data. |
| **Account** | **SET** | ⚠️ The single most important non-strategy choice (sim vs live, which account). |
| **Type / Value** (data series) | **SET** | e.g. Minute / 1. Match what you tested. |
| **Days to load** | **SET** | ≥10 (30 recommended). The strategy warms up from its own log, so this is not critical, but load some. |
| **Trading Hours (template)** | **SET** | ⚠️ **CME US Index Futures ETH** (full electronic session), NOT RTH — the strategy slices overnight and uses this for gap math + EOD timing. |

## Part 4 — NinjaTrader fields to LEAVE DEFAULT (nothing to do with this strategy)

These belong to NT's generic backtest/execution engine. This strategy either doesn't use them or they only affect *historical backtest* fills, not live trading.

| NT Field | Action | Why it's irrelevant here |
|---|---|---|
| **Order Fill Resolution** | **LEAVE DEFAULT** | Governs how *historical/backtest* orders are simulated. No effect on live slicing. |
| **Slippage** | **LEAVE DEFAULT** | Backtest-only simulated slippage. Live fills come from the broker. |
| **Fill Limit Orders on Touch** | **LEAVE DEFAULT** | Backtest-only fill assumption. Not used live. |
| **Maximum Bars Look Back** | **LEAVE DEFAULT** (`256`) | Memory setting for indicator history. This strategy keeps its own state in strings, so the default is fine. |
| **Label** | **LEAVE DEFAULT** | Cosmetic chart label. No effect. |
| **Order Quantity value box** (when "Default Quantity") | **LEAVE / N/A** | Ignored once "Set order quantity = Strategy". |
| **From / To dates** | **LEAVE DEFAULT** | Historical load range for the chart; not used by the live slice loop. |
| **Optimization / IsInstantiatedOnEachOptimizationIteration** | **LEAVE DEFAULT** | Only relevant in the Strategy Analyzer optimizer, not live Strategies-tab runs. |

---

### The 10-second version
- **SET (you choose):** Instrument, Account, Trading-Hours template (ETH!), EnableRealOrder, BaseQuantity, EnableQtyIncrement, LogBaseName.
- **CONFIRM (should already be right):** the trading-window hours, MaxRealLossInARow, Calculate=OnEachTick, Set order quantity=Strategy, LogFolder.
- **LEAVE (don't touch):** filters, stop/target, trail, limit, check interval, all the Part 2 NT props, and all the Part 4 backtest-only fields.

*Appendix cross-checked against `Scalper_Shortrepeat_Layer2.cs` (SetDefaults + property attributes) and the NinjaTrader 8 help guide "Running a NinjaScript Strategy" (Strategy Properties list), 2026-07-17.*
