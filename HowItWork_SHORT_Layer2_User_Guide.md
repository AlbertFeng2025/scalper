# SHORT Repeat Layer 2 — Complete User Guide
### `Scalper_Shortrepeat_Layer2` · NinjaTrader 8 · MNQ

*A full operator's manual: what it does, the theory, every parameter, and how it behaves in every situation (day rollover, disconnect, crash, disable/enable).*

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
| `UseMarketEntry` | `true` | Market vs limit entry for real orders. | No — dialog |
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

**EOD flatten.** `IsExitOnSessionCloseStrategy = true` — any open position is flattened at session close.

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
