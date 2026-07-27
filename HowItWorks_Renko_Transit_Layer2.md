# Scalper Renko Transit-repeat — Layer 2 (SHORT & LONG): How It Works

**Files:** `Scalper_Renko_TransitSHORTrepeat_Layer2` and `Scalper_Renko_TransitLONGrepeat_Layer2`
**Instrument / data:** MNQ, 80-tick Renko bricks (= 20 points per brick)
**Bracket:** 20 pt stop / 40 pt target, brick-close-anchored
**Default filter:** `F1 = 10,100,1000` (multi-pattern OR) · `F2 = 000` (single pattern)

> This guide assumes you have read the Layer 1 guide. Layer 2 shares the bit
> encoding, the 20/40 bracket, sizing, session mechanics, and the seed feature.
> **What is different in Layer 2 is the F2 meta-label gate — that is the whole
> point of this file.**

---

## 1. Layer 1 vs Layer 2 in one sentence

Layer 1 fires on **every** F1 match. Layer 2 adds a second gate, F2, and fires
only when **F2 is armed *and* F1 matches** — i.e. only after the recent history of
F1 outcomes matches the F2 pattern.

```csharp
nextIsMoney = isArmed && TailMatchesAny(raw);   // Layer 2: BOTH conditions
```

---

## 2. The bit (same as Layer 1)

Each closed brick becomes one bit from this close vs the previous close.

| Brick | SHORT book | LONG book |
|---|---|---|
| Up (green) | `0` = loss for short | `1` = win for long |
| Down (red) | `1` = win for short | `0` = loss for long |

In both books the bit is `1` when the brick went *your* way and `0` when it went
*against* you. The encodings mirror, so the **same** F1/F2 strings describe the
mirror setup in each book.

---

## 3. The F1 → F2 pipeline (what arms, what fires)

Every closed brick, in order:

1. Append the bit to `rawString`.
2. **Collect the F1 outcome.** If the previous brick matched F1, append *this*
   brick's bit to `filter1Outcome`. That bit is the **outcome** of the F1 event:
   `1` = the reversal came (win), `0` = the run continued (loss).
3. **Re-check the F2 gate.** `isArmed = TailMatches(filter1Outcome, F2)`. With
   `F2 = 000`, armed means "the last 3 F1 outcomes were all losses."
4. **Check F1.** If the `rawString` tail matches any F1 pattern, arm the outcome
   collector for the next brick.
5. **Fire?** `nextIsMoney = isArmed && (F1 matches now)`. If armed *and* F1 matches
   this brick, enter a real trade.

So `filter1Outcome` is the **win/loss history of F1 events**, and F2 is a pattern on
that history. `F2 = 000` = "only trade the setup after it has failed 3 times."

---

## 4. The bracket, sizing, sessions, seed — all identical to Layer 1

The 20/40 brick-close-anchored bracket, the loss-ratchet qty rule, the
`MaxRealLossInARow` breaker, the trade-outcome exit, the trading-hours filter, the
3 PM PT daily reset, fresh-start-on-interrupt, and the optional **Seed pending
brick on start** toggle all behave exactly as documented in the Layer 1 guide. The
seed brick still never places an order; in Layer 2 it also cannot by itself arm F2
(a one-bit string can't match a multi-bit F1). See the Layer 1 guide, Sections 4,
7, 8, and 8.1.

---

## 5. Multi-pattern F1 in Layer 2 — read this whole section

Layer 2 now accepts **multiple comma-delimited F1 patterns** (default
`10,100,1000`), matched as an OR. **F2 remains a single pattern.** Combining a
multi-pattern F1 with F2 fundamentally changes what "armed" means, and that change
is intentional — but you must understand it exactly.

### 5.1 One long run arms F2 (fast arming)

With a **single** F1 (`1000`), each run contributes at most one outcome bit, so
`F2 = 000` needs **three separate runs** that each hit `1000` and fail — a rare
condition.

With **multi** F1 (`10,100,1000`), one developing run matches `10`, then `100`,
then `1000` on consecutive bricks. Each match feeds an outcome bit into
`filter1Outcome`. So a **single** against-you run of length ≥ 4 drops `000` into
the history and arms F2 by itself. That is the design intent: *"after I see one
long run, treat the reversion as due."*

**The arming run does not fire.** By the time the 3rd loss is collected and
`isArmed` flips true, the tail is all zeros (`10000…`) and no F1 pattern matches
(they all need the leading `1`). So the run that arms F2 produces **zero trades**.
Firing begins on the **next** run.

### 5.2 What fires, and the loss stacking

Traced from a faithful model of the pipeline (SHORT book, `F1=10,100,1000`,
`F2=000`), for two long against-you runs back-to-back:

| Phase | What happens |
|---|---|
| Run A (long) | Arms F2 at the 4th brick. **0 fires.** |
| Reversal | No F1 match. |
| Run B, brick 1 | Tail `…10` matches, armed → **FIRE**. |
| Run B, brick 2 | Run continued → previous fire **loses**; tail `…100` → **FIRE**. |
| Run B, brick 3 | Loss; tail `…1000` → **FIRE**. |
| Run B, brick 4 | Loss; tail `…10000` no match → firing stops. |

The table above shows which patterns *match*. What actually *trades* is fewer —
about **2 per run** (`10` and `1000`; `100`'s order is blocked) — for the market-entry
reason detailed in **5.4**. Critically, **arming is sticky**: a losing run leaves
`filter1Outcome` all-zeros, so F2 stays armed and the *next* long run fires again, and
so on. Consecutive long runs keep stacking losses — bounded in practice only by
`MaxRealLossInARow`. (And note: even the blocked `100` still feeds arming — see 5.4.)

### 5.3 A win disarms F2 (the reset)

When a fire **wins**, the reversal brick's bit (`1` for a win) is appended to
`filter1Outcome`, breaking the `000` tail → `isArmed = false`. The strategy then
goes dormant until a fresh long run re-arms it. This is the "stop after a win, wait
for the next setup" behavior:

| Phase | What happens |
|---|---|
| Run A (long) | Arms F2. |
| New run, brick 1 | Tail `…10`, armed → **FIRE**. |
| Next brick | Reversal (win). Outcome bit `1` → `filter1Outcome` ends `…0001` → **disarmed**. |

### 5.4 ⚠️ Market entry occupies the next brick — TRADES vs ARMING differ

This is the subtlest point in Layer 2, and the most important to get right before you
trim the pattern list.

Entries are **market orders** (`UseMarketEntry = true`), so an order fills at the
**next brick's open** and its trade occupies that whole brick (its 20 pt stop sits on
that brick's close). The strategy is strictly **one trade at a time** — it refuses a
new entry while any position or working order exists. So on a continuing run, exactly
as in Layer 1, only **every other** pattern actually *trades*:

- `10` fires; its order fills next brick and occupies it.
- `100` matches on that occupied brick → **order blocked** (`WOULDBE_TRADE` /
  `OBS_ACCOUNT_BUSY`).
- `1000` fires on the next, now-free brick.

**But here is the Layer-2-specific twist: blocking only stops the *order*, not the
*pipeline*.** `UpdatePipeline` runs on every brick regardless, so the blocked `100`
match **still** sets `waitingForF1Outcome` and drops its outcome bit into
`filter1Outcome`. Therefore:

- **Trades per continuing run ≈ 2** (`10`, `1000`; `100` blocked).
- **Arming still counts all 3 matches** (`10`, `100`, `1000`) — so F2 still arms off a
  single long run, exactly as Section 5.1 describes. The blocked `100` is *not* wasted
  here; it does the arming work even though it never trades.

**Consequence for your pattern list.** In Layer 1 it makes sense to trim to `10,1000`
so the list matches what trades. **Do not do that in Layer 2.** Removing `100` drops a
bit from every run's arming contribution (`000` → `00`), so a single long run no longer
arms — arming slows to roughly two runs. Keep `10,100,1000` in Layer 2: `100` earns its
place through arming even though its order is always blocked.

This is expected, correct behavior — not a bug. Trading all three would require
overlapping positions 20 pt apart (pyramiding), which is not recommended.

---

## 6. ⚠️ Two operational subtleties you must plan around

**(A) A freshly-enabled strategy is NOT armed.** On enable, `rawString` starts
empty and `isArmed = false`. The long run you eyeballed *before* enabling does not
count — the strategy never saw it. Layer 2 therefore sits dormant until it
**witnesses its own** against-you run of length ≥ 4 *after* enable, then fires on
*subsequent* runs. This is the key difference from Layer 1 (which fires on the very
next F1 match). If you enable right at a reversal, expect a wait. The Seed toggle
recovers at most one brick — it cannot supply a whole arming run.

**(B) Arming is sticky — losses stack until a win or the breaker.** Once armed, a
losing run does *not* disarm; only a win does. In a strong trend (many consecutive
long runs) the strategy keeps firing and losing 3 per run. Your `MaxRealLossInARow`
breaker is the real cap — with the breaker at 4, it halts at 4 losses, not at any
natural "6." Set the breaker to your true per-session pain limit and honor it.

---

## 7. ⚠️ Discipline — this is a gamble, not an edge

Everything in the Layer 1 discipline section applies here unchanged: 33.3% is the
**breakeven**, not a win rate; trend persistence drags the real rate down toward and
below it; a 33%-breakeven setup can miss 10–15 attempts in a row. Layer 2's F2 gate
does **not** manufacture an edge — it only changes *when* you place the same
discretionary bet (after an observed losing run instead of on every match). Enable
only when you actually want to take the gamble, honor the breaker, and if you lose
the day's allowance, walk away and look again tomorrow. Do not chase it in one
session.

---

## 8. Setup checklist

1. Data series: MNQ, **80-tick Renko**, Trading Hours template **`CME US Index
   Futures ETH`**.
2. `Stop loss = 20`, `Profit target = 40`.
3. `F1 = 10,100,1000` (or `1000` for classic single-pattern L2). Confirm the
   `[F1 PATTERNS] ACTIVE` line in the diag log.
4. `F2 = 000` (single pattern — commas are not parsed for F2).
5. `Base quantity`; if ratcheting, `Enable Qty Increment = true` and confirm
   `[QTY RULE] ACTIVE`.
6. `Max Real Loss In A Row`: your real per-session cap (this, not "6," is your
   protection).
7. Optional `Seed pending brick on start` (default OFF; see Layer 1 §8.1).
8. Start with `Enable Real Order = false`; watch the diag log until you see F2 arm
   (`isArmed=true`) and `WOULDBE_TRADE` rows behave as expected, then go live.

---

## 9. Reading the log

CSV columns are the same as Layer 1: `timestamp, bar_num, side, quantity,
entry_price, exit_price, realized_pnl, win_loss_bit, rawString, filter1Outcome,
realTradeOutcome`.

For Layer 2, watch two extra things in the `*-diagLog.csv`:

- **`[PIPELINE] … isArmed=…`** — whether F2 is currently armed. You should see it
  flip to `true` after a long against-you run, and back to `false` after a win.
- **`filter1Outcome`** — the F1 win/loss history F2 is matching against. A run of
  `000` at its tail is what arms `F2=000`.

Side values are as in Layer 1, plus `WOULDBE_TRADE` appears when
`isArmed && F1` matched but no real order was placed (observation mode, position
open, or a guard). Only `Short` / `Long` close rows are read back as real outcomes.

---

*Applies to the Layer 2 Renko Transit-repeat files. In Layer 2, F1 is multi-pattern
and F2 is single-pattern; the multi-pattern-arming behavior in Section 5 is specific
to running several F1 patterns at once.*
