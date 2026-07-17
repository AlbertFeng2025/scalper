# Disable / Enable & Settings-Change Guide
### What the NT8 strategy does when you interrupt it or change a setting

**Applies to:** all four strategies (`Scalper_Shortrepeat_Layer2`, `scalper_LONGrepeat_Layer2`, `scalper_SHORTrepeat_Layer3`, `scalper_LONGrepeat_Layer3`)

---

## PART A — The most important thing to understand first

**NinjaTrader cannot tell the difference between these three events:**

1. A brief price-feed disconnect (NT auto-restarts the strategy)
2. You manually disabling then re-enabling the strategy
3. You disabling, changing a setting, then re-enabling

**All three do the same thing internally:** NT destroys the running strategy instance and builds a **brand-new one**. The new instance then reads its own log file from disk and decides whether to **RESUME** (continue where it left off) or **FRESH START** (wipe and begin new).

So "what happens when I disable and re-enable?" has the same answer as "what happens on a reconnect." The strategy was **designed** for this — the log file on disk is the source of truth, not the running memory.

---

## PART B — RESUME vs FRESH START: what decides?

When the new instance starts, it looks at the **time gap** since the last logged slice. **The decision is based on the GAP, not on whether you changed a setting.**

| Gap since last slice | Decision |
|---|---|
| Small (< ~7 market-open minutes, < 4 hours wall-clock) | **RESUME** — continue the bit string |
| Large (> 4 hours wall-clock, OR > 7 min of *market-open* time) | **FRESH START** — wipe and begin new |
| Crosses the 3 PM PT trading-day boundary | **New day starts empty** (matches research) |

**So if you disable and re-enable within a few minutes → RESUME.** Your bit string continues, no data lost. This is the normal, safe case.

> **Tunable:** `GapToleranceMinutes` (default 7) and `GapCeilingHours` (default 4) are in the code. These control the RESUME/FRESH threshold.

### What RESUME preserves vs resets

| Item | On RESUME (quick disable/enable) |
|---|---|
| **rawString** (the bit sequence) | ✅ **PRESERVED** — restored from log, keeps growing |
| **Filter state** (armed / waiting / pending signal) | ✅ **PRESERVED** — re-derived from the restored strings |
| **Loss-streak breaker** | ✅ **RESTORED** — counts today's real losses from the log |
| **Qty rule history** (`sessionRealOutcome`) | ⚠️ **RESET to base** — today's sizing memory is cleared |
| **slice_num** | 🔄 restarts at 1 (per-instance counter — cosmetic, order by timestamp) |

---

## PART C — YOUR SPECIFIC QUESTION: "I disabled and changed the qty — what happens?"

This depends on **which** qty thing you changed. There are two very different things:

### C1 — Changing `BaseQuantity` (the base lot size) — NO RECOMPILE NEEDED

`BaseQuantity` is a **strategy parameter** you set in the strategy dialog. To change it:

1. Disable the strategy
2. Change `BaseQuantity` in the parameter grid (e.g. 1 → 2)
3. Re-enable

**What happens:** the new instance starts with the new base quantity **immediately**. If the gap was small, it RESUMES the bit string (so the pipeline continues), but every *new* real trade from that point uses the new `BaseQuantity`. **No recompile. Takes effect on re-enable.**

✅ **This is a runtime setting. Change it freely — just disable first, change, re-enable.**

### C2 — Changing `EnableQtyIncrement` (turn the doubling rule on/off) — NO RECOMPILE NEEDED

Also a strategy parameter (`[NinjaScriptProperty]`). Same procedure: disable → toggle → enable. Takes effect immediately. No recompile.

### C3 — Changing the QTY RULE ITSELF (the `{'10':2, '100':2, '10000':0...}` table) — **RECOMPILE REQUIRED**

**This is the key answer to your question.** The actual multiplier table:

```csharp
private static readonly (string pattern, int multiplier)[] QtyMultiplierTable =
{
    ("10",      2),   // after a win, then 1 loss  -> qty x2
    ("100",     2),   // after a win, then 2 losses -> qty x2
    ("1000",    2),   // after a win, then 3 losses -> qty x2
    ("10000",   0),   // 4 losses -> SKIP (observe only)
    ("100000",  0),   // 5 losses -> SKIP
    ("1000000", 0),   // 6 losses -> SKIP
};
```

This is **hardcoded in the source** (`private static readonly`), **NOT** a parameter in the dialog. To change *these numbers* — the patterns or the multipliers — you must:

1. Edit the `.cs` file (change the table)
2. **Recompile** (F5 in the NinjaScript editor)
3. Then disable/enable the strategy

> ### ⚠️ SUMMARY OF THE RECOMPILE QUESTION
> | What you change | Recompile? | How to apply |
> |---|---|---|
> | `BaseQuantity` (lot size) | **NO** | Disable → change in dialog → enable |
> | `EnableQtyIncrement` (on/off) | **NO** | Disable → toggle in dialog → enable |
> | The **qty multiplier table** (`{'10':2...}`) | **YES** | Edit `.cs` → **F5 recompile** → disable/enable |
> | Filter patterns (F1/F2, e.g. `10?`/`00`) | **YES** | Edit `.cs` → **F5 recompile** → disable/enable |
> | Stop / Target points | **NO** | Disable → change in dialog → enable |
> | Trading hours | **NO** | Disable → change in dialog → enable |

**Rule of thumb:** if it shows up as a field in the strategy's parameter grid, you can change it with no recompile. If it's inside the code (the qty table, the filter patterns), you must recompile.

---

## PART D — Settings you CAN change in the dialog (no recompile)

These are all `[NinjaScriptProperty]` — editable in the strategy parameter grid, no recompile:

- `EnableRealOrder` — real orders on/off
- `BaseQuantity` — base lot size
- `EnableQtyIncrement` — qty doubling rule on/off
- `StopLossPoints` / `ProfitTargetPoints` — the geometry
- `EnableTradingHours` + `TradingStartHour/Minute`, `TradingEndHour/Minute`
- `UseMarketEntry`, `LimitOffsetPoints`
- `EnableTrailingStop`, `TrailDistancePoints`
- `StrategyLifeMinutes`, `CheckIntervalSeconds`
- `LogBaseName` (⚠️ changing this points the strategy at a *different* log file — can trigger a FRESH start because it won't find the old history)

---

## PART E — Walkthroughs of your exact scenarios

### E1 — Brief disconnect, comes back in a few minutes
- NT auto-restarts the strategy (new instance)
- Gap is small → **RESUME**
- rawString continues, filter state restored, breaker restored
- Qty session resets to base (minor)
- **Net effect: seamless. Nothing lost.** ✅

### E2 — You manually disable, then re-enable right away (a few minutes)
- **Identical to E1** — NT can't tell the difference
- Small gap → **RESUME**
- **Net effect: seamless.** ✅

### E3 — You disable, change `BaseQuantity` from 1 to 2, re-enable (a few minutes)
- New instance, small gap → **RESUME** (bit string continues)
- New base quantity **active immediately** for all new real trades
- No recompile needed
- **Net effect: pipeline continues, new size takes effect.** ✅

### E4 — You disable, edit the qty TABLE in code, re-enable
- You must **F5 recompile first**, or NT runs the old compiled version
- After recompile + re-enable: small gap → RESUME (bit string continues), new table active
- **Net effect: needs recompile, otherwise like E3.** ⚠️

### E5 — You leave it disabled for hours, then re-enable
- Large gap (> 4h) → **FRESH START**
- rawString wiped, new log file created, pipeline starts empty
- Real-trade audit trail (`realTradeOutcome`) is preserved for the breaker
- **Net effect: pipeline restarts from scratch (by design — a stale bit string from hours ago is not valid warm-up).** ✅

### E6 — You disable before 3 PM PT, re-enable after 3 PM PT
- New instance detects the trading-day boundary was crossed
- Logs `[RESUME ACROSS DAY BOUNDARY]` → **starts the new day empty**
- Breaker recomputes for the new day (= 0 if no real trades yet today)
- **Net effect: correct new-day reset, as if it had rolled over live.** ✅

---

## PART F — Practical checklist

**To change lot size or toggle the qty rule (common):**
1. Disable strategy
2. Change `BaseQuantity` and/or `EnableQtyIncrement` in the dialog
3. Re-enable (within a few minutes → RESUME, seamless)
4. **No recompile.**

**To change the qty multiplier table or filter patterns (rare):**
1. Edit the `.cs` file
2. **F5 to recompile** in the NinjaScript editor
3. Delete the compiled DLL cache if doing a clean rebuild (per your OneDrive/Michelle workflow)
4. Disable → re-enable the strategy
5. Verify the startup diag line shows the new values

**Always, after any re-enable, check the diag log for:**
- `[RESUME ...]` or `[FRESH START ...]` — confirms which path it took
- The `ready (...)` line — confirms your current settings (`EnableRealOrder`, filters, stop/target) are what you expect

---

---

## PART G — One caution about the qty rule and reconnects

Because a quick disable/enable (and every auto-reconnect) **resets the qty session** (`sessionRealOutcome`), and your feed reconnects often, the qty-doubling rule may **rarely actually engage** in live trading — its "I just lost one" memory keeps getting erased before the next trade.

This is **safe** (it can only under-size, never over-size), but it means the researched `{'0':2}` sizing benefit is largely lost in practice. If you want the qty rule to survive reconnects, the fix is to rebuild today's real-outcome string from the log on RESUME (the same technique the loss-streak breaker already uses). Ask when you want this done.

---

## PART H — HOW TO CHANGE THE LOT SIZE (step by step, nothing skipped)

**This is the most common thing you'll do. Read every step. Do not skip any.**

### The one rule to remember
> **You do NOT need to remove the strategy from the list.**
> **You DO need to DISABLE it before you can change the number.**
> NinjaTrader LOCKS the settings while a strategy is running. You cannot type a new
> quantity into a strategy that is enabled — the box is greyed out / read-only.
> So the order is always: **DISABLE first → change the number → ENABLE again.**

### Exact clicks (NT Control Center → Strategies tab)

**STEP 1 — Find the strategy in the list.**
Open the NinjaTrader Control Center. Click the **Strategies** tab. You will see your strategy in the list with a checkbox in the "Enabled" column.
👉 *Do NOT remove it. Do NOT right-click → Remove. Just leave it in the list.*

**STEP 2 — DISABLE it.**
Uncheck the **Enabled** checkbox for that strategy (or right-click the row → **Disable**).
The strategy is now stopped, but still sitting in the list. Good.
👉 *If you skip this step, the quantity box will be locked and you can't change it.*

**STEP 3 — Open its settings.**
**Double-click** the strategy's row (or right-click → the option that opens its parameters).
A window opens showing all the parameters (Stop, Target, BaseQuantity, etc.).

**STEP 4 — Change the number.**
Find **BaseQuantity**. Click the value. Type the new number (for example, change `1` to `2`).
Click **OK** to close the window.
👉 *Just re-enabling WITHOUT doing this step changes NOTHING. You have to actually type the new number here.*

**STEP 5 — ENABLE it again.**
Check the **Enabled** checkbox again (or right-click → **Enable**).
The strategy restarts with the new quantity.

**STEP 6 — CONFIRM it worked.**
Open the diag log file. Look at the newest `ready (...)` line near the bottom.
It prints the active settings. Check that the new quantity / settings are what you typed.
👉 *Never assume it took effect — look at the log and confirm.*

### That's it. Recap in one line:
> **Disable ✅ → double-click → type new number → OK → Enable ✅ → check the log.**
> **No removing. No recompiling. Do it within a few minutes and the bit string continues (RESUME).**

### Common mistakes (do not do these)
- ❌ Trying to change the number while the strategy is still enabled → **the box is locked, nothing happens.**
- ❌ Just unchecking and re-checking Enabled without opening the settings → **the number does not change.**
- ❌ Removing the strategy from the list and re-adding it → **works, but pointless — never necessary. Just disable/enable.**
- ❌ Assuming it worked without checking the diag log → **always confirm with the `ready (...)` line.**

### ⚠️ Remember the difference (from Part C)
- Changing **BaseQuantity** (the lot size number) = the steps above, **NO recompile.**
- Changing the **qty RULE table** (`{'10':2, '100':2...}` inside the code) = you must **edit the .cs file and press F5 to recompile** first. That is a code change, not a settings change.
