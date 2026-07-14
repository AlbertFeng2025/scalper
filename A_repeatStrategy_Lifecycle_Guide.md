# Strategy Lifecycle Guide
### What happens to your pipeline on a disconnect, a restart, and the 3 PM rollover

**Applies to:** `Scalper_Shortrepeat_Layer2`, `scalper_LONGrepeat_Layer2`, `scalper_SHORTrepeat_Layer3`, `scalper_LONGrepeat_Layer3`
**Instrument:** MNQ · **Trading day:** 3:00 PM PT → 3:00 PM PT

---

## The three things to track

Whenever the strategy is interrupted or a new day begins, three things can change independently. Always ask about all three:

| # | Thing | What it is |
|---|---|---|
| 1 | **rawString** | The bit string of every slice outcome. Drives the filter (arming, firing). |
| 2 | **Real trade placement** | Whether an actual order gets sent. |
| 3 | **Qty rule** | The per-day sizing rule (`sessionRealOutcome`). |

Plus two supporting pieces:

| Thing | What it is |
|---|---|
| **Loss-streak breaker** (`realLossesInARow`) | Halts trading after N consecutive real losses. **Resets daily.** |
| **realTradeOutcome** | Cumulative audit trail of every real trade, ever. **The only thing that never resets.** |

---

## Case 1 & 2 — Quick disconnect **OR** manual disable/enable
### ⚠️ These are THE SAME THING to the code

NinjaTrader creates a **brand-new strategy instance** in both cases. It cannot tell the difference between:
- its own auto-restart after a lost price connection (`ConnectionLossHandling=Recalculate`, 10-second trigger), and
- you clicking disable then enable.

Both follow the identical path: **new instance → read own log → small gap → RESUME.**

| | What happens | Why |
|---|---|---|
| **1. rawString** | ✅ **PRESERVED** | Restored from the log and keeps growing. No hole, no lost warm-up. `filter1Outcome` restored too. |
| **2. Real trades** | ✅ **RESUMES NORMALLY** | `ReDerivePipelineFlags()` recomputes `isArmed` / `waitingForF1Outcome` / `nextIsMoney` from the restored strings. **Even a pending signal survives.** |
| **3. Qty rule** | ⚠️ **RESET TO BASE (x1)** | `sessionRealOutcome` is cleared. Today's real-trade history for sizing is **lost**. If you'd already taken 2 losses this morning, the next trade is x1, not x2. |
| **Breaker** | ✅ **CORRECTLY RESTORED** | `CountTodaysTrailingLosses()` re-reads the log and counts **today's** trailing real losses. The breaker survives the restart. |
| **Log file** | Same file, keeps appending | A FRESH start (big gap) is what creates a new file. |
| **slice_num** | 🔄 **Restarts at 1** | Per-instance counter. This is why you see `77 → 1` mid-file. **Order events by timestamp, not slice_num.** |

### What you'll see in the log
```
[RESUME] gap small: 3 market-open min (<= 7min), wall-clock 0.05h, no weekend
         | restored rawString.len=77 filter1Outcome.len=22 ...
```

### ⚠️ The qty-rule gotcha
Your feed reconnects **many times a day**. Each reconnect wipes `sessionRealOutcome`.

So once you turn `EnableQtyIncrement` on, **the qty rule may rarely actually engage** — its "I just lost one" memory keeps getting erased before the next trade fires.

It is **conservative** (it can only under-size, never over-size), so it is safe. But the researched `{'0':2}` sizing edge would be partly lost in live trading.

---

## Case 3 — Trading-day rollover (3:00 PM PT)
### Strategy keeps running; no restart

| | What happens | Why |
|---|---|---|
| **1. rawString** | 🔄 **CLEARED** | Starts fresh at 15:00 PT. The new overnight bits (3 PM → 6:30 AM) become **tomorrow morning's warm-up**. `filter1Outcome` / `filter2Outcome` cleared too. |
| **2. Real trades** | 🔄 **PENDING SIGNAL CANCELLED** | `nextIsMoney` cleared. The pipeline must **re-arm from scratch**. No real trade until it re-arms **and** you are inside 9:30–11:30 ET. |
| **3. Qty rule** | 🔄 **CLEARED** → base qty | New day, new sizing sequence. |
| **Breaker** | 🔄 **RESET TO 0** | New trading day = clean slate. Blew through 5 losses yesterday? Today you trade again. |
| **realTradeOutcome** | ✅ **KEPT** | The cumulative audit trail — the **only** thing that survives. |
| **Log file** | ⚠️ **SAME FILE** | The strategy does **NOT** roll to a new file (unlike the recorder, which does). One strategy log can span several trading days, with rawString resetting **inside** it. |

### What you'll see in the log
```
[TRADING DAY ROLLOVER] 20260713 -> 20260714 (3:00 PM PT boundary).
                       Clearing pipeline to match the research. prev raw(244).
[BREAKER RESET] new trading day -> realLossesInARow 2 -> 0
```

### ⏱ Timing nuance
The rollover only runs when the strategy is **NOT in a slice**.

A slice that entered at 14:59 **completes first** — its bit goes into the **old** day's rawString — and *then* the wipe happens.

This is **correct**: the research assigns each slice to the trading day it **ENTERED** in.

---

## Case 4 — A restart that lands *across* the 3 PM boundary

Example: disconnect at 14:58, reconnect at 15:02.

The RESUME detects that the restored snapshot belongs to a **previous** trading day:

```
[RESUME ACROSS DAY BOUNDARY] snapshot is from trading day 20260713, now 20260714.
                             Discarding restored pipeline; new day starts EMPTY.
```

- rawString / filter outcomes → **discarded**, new day starts empty ✅
- Breaker → recomputes to **0** (no real trades today yet) ✅
- Qty rule → cleared ✅

Without this check, a reconnect across 3 PM would have quietly dragged yesterday's bits into today.

---

## Summary table

| | Quick disconnect **or** manual re-enable | 3 PM rollover |
|---|---|---|
| **rawString** | ✅ preserved | 🔄 cleared |
| **Filter arming** | ✅ preserved (pending signal survives) | 🔄 must re-arm |
| **Real trades** | ✅ resume normally | 🔄 none until re-armed + in-hours |
| **Qty rule** | ⚠️ reset to base | 🔄 cleared |
| **Breaker** | ✅ restored (today's losses) | 🔄 reset to 0 |
| **realTradeOutcome** | ✅ kept | ✅ kept |
| **Log file** | same | same |
| **slice_num** | 🔄 restarts at 1 | continues |

---

## Reading the log — the `side` column

| Value | Meaning |
|---|---|
| `Short` / `Long` | **A REAL order was placed.** |
| `FAKE_Short` / `FAKE_Long` | Ordinary observation slice — the filter was **not armed**. |
| `WOULDBE_TRADE` | The filter **ARMED**, but `EnableRealOrder=false`. **This is the trade it would have taken.** Use these to forward-log a book you are not trading live yet. |
| `OBS_OUTSIDE_HOURS` | Armed, but outside the trading window. |
| `OBS_ACCOUNT_BUSY` | Armed, but another strategy (or a manual trade) held the instrument. |
| `OBS_QTY_SKIP` | Armed, but the qty rule returned 0 (deep in a loss run). |
| `CANCELLED_no_fill` | Entry order never filled; cancelled. No bit recorded. |

---

## ⚠️ Three traps when verifying in Python

**1. rawString RESETS at 15:00 PT.**
It grows through the session, then **starts over**. That is correct, not a bug.
→ **Only use bits from ONE trading day. Do NOT concatenate across the 15:00 PT boundary.**

**2. slice_num is per-instance, not global.**
It jumps back to 1 whenever NT makes a new instance.
→ **Order events by TIMESTAMP.**

**3. The strategy's rawString will NEVER match the recorder's bit string, bit for bit.**
Both slice with identical logic, but they are **independent slicers with independent 1-second throttle phases**. Whichever starts first sets its own slice boundaries, so their entry times differ by seconds — and therefore their bits differ.

Each string is internally consistent. **Neither is "wrong."** Do not try to reconcile them slice-by-slice.

---

## Open item to test before going live

**An open real position across a disconnect.**

If a money slice is live (position + broker-side stop/target) when NT drops the strategy:

- ✅ The brackets are **real exchange orders** — they will still fill. Your money is protected.
- ✅ `AccountBusyOnThisInstrument()` on the new instance sees the position and demotes new slices to observation.
- ❓ **Uncertain:** the new instance has `inSlice=false`, `awaitingClose=false` — it does not know it had a trade. **It is not confirmed that `OnExecutionUpdate` fires on the new instance when the bracket fills.** If it does not, that trade's bit never reaches `realTradeOutcome`, and the breaker will not count it.

**Test this deliberately in SIM before enabling real orders:** open a real position, disable/re-enable the strategy mid-trade, and check whether the fill shows up in the log.

---

## Recommended NT setting

Your feed drops frequently, and each drop makes NinjaTrader destroy and rebuild the strategy.

**When enabling the strategy, set `ConnectionLossHandling` from `Recalculate` → `Keep`.**

This tells NT to keep the strategy running through short disconnects instead of restarting it every 10 seconds. The code handles restarts correctly either way — but fewer restarts means fewer wiped qty sessions and a cleaner log.
