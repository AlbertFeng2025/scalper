# MNQ SHORT/LONG Pattern-Filter Strategy — Summary & Training Guide

**Date:** 2026-07-11 · **Instrument:** MNQ (Micro E-mini Nasdaq-100, $2/pt) · **Platform:** NinjaTrader 8
**Method:** fixed-slice 32/19 bracket with meta-label filtering (this is the *slice* method, **not** the Renko method).

**How to read this document:**
- **Part A — Training (from zero):** if you have never seen this project, read this first. It explains every idea from scratch.
- **Part B — Research findings:** the actual results, patterns, and theory (the "what we found").
- **Part C — Running SHORT and LONG together:** what happens on one account vs. two, and the recommended setup.
- **Glossary** at the end defines every term.

---
---

# PART A — TRAINING: the whole story from zero

*Read this if you don't already know what a "slice," "meta-label," or "62.7% breakeven" means. Nothing here is assumed.*

## A1. What are we trading?
**MNQ** = Micro E-mini Nasdaq-100 futures — a small futures contract that tracks the Nasdaq-100. Key numbers:
- **$2 per point.** 1 point move = $2 per contract.
- **1 tick = 0.25 point = $0.50.** The smallest price step.

We trade it **intraday only**, mostly in a short morning window, and we go **SHORT** (bet price falls). Why short and not long is explained in A6.

## A2. The base bet — a "slice"
We do **not** use normal time-based candles. We chop the live price stream into small bets called **slices**.

- Roughly **once per second** (a "1-second throttle"), if we're not already in a slice, we start a new one.
- Starting a slice = record the current price and set **two exit levels**:
  - **SHORT book:** enter at the **ask** price.
    - **Target = 19 points below entry** (if price falls 19 → we WIN).
    - **Stop = 32 points above entry** (if price rises 32 → we LOSE).
- The slice **ends** the instant price touches either level. Then the next slice starts.

So a slice is a tiny, self-contained bet: *"will price drop 19 points before it rises 32 points?"*

## A3. Turning slices into a string of 1s and 0s
Each finished slice produces **one bit**:
- **`1` = the slice WON** (for SHORT, price hit the −19 target).
- **`0` = the slice LOST** (price hit the +32 stop).

This is called a **direction-relative** convention: `1` always means "this book's bet worked," regardless of which book. A trading day becomes a long string like:

```
1 1 0 1 0 0 1 1 1 0 ...
```

From here on, everything is **pure math on this string of 1s and 0s.** The string *is* the market for our purposes.

## A4. The breakeven math — why we need 62.7%
Every slice wins **+19** or loses **−32** points. To just break even (make zero money), how often must we win? Let `p` = win rate:

```
win × p   =   loss × (1 − p)
19 × p    =   32 × (1 − p)
p = 32 / (19 + 32) = 32/51 = 62.7%
```

> **Breakeven = 62.7%.** We must win nearly **2 out of every 3** slices just to not lose money.

This is a **high-win-rate, small-target** style. It only makes sense if we can find moments where price is very likely to make a small drop. That is exactly what the filters (Part A7) hunt for.

## A5. Why this bracket? (32-point stop, 19-point target)
It looks backwards — a *bigger* stop than target. Here's the intuition, sometimes called **"elevator down, stairs up"**:
- Markets tend to **fall fast and rise slow.** Down-moves are quicker and sharper; up-grinds are slow.
- A **near target (19) + wide stop (32)** lets us grab the **frequent small drops** while **surviving the noise** — a fast little dip hits our 19 target before a slow grind up could ever reach the far 32 stop.
- The reverse (tight stop, far target) would get **shaken out** by fast down-bursts before the slow grind reached a far target.

The technical name is the **leverage effect**: volatility is higher right after prices fall. (More in Part B5.)

## A6. Why SHORT and not LONG?
The Nasdaq trends **up** over years — so shorting sounds crazy. But we operate on a **19-point, few-second scale**, where the long-term drift (~0.03%/day) is invisible. At that tiny scale:
- Sharp **up-moves over-extend and snap back** ("intraday reversal," strongest *after* big up-moves).
- So a quick short into an over-extended pop tends to catch the snap-back down.

The LONG book (betting price rises) is the mirror image, and it only works in genuinely up-trending conditions — so it stays a **manual, up-days-only** book (see Part B & C).

## A7. Meta-labeling — the Layer 1 / Layer 2 idea
This is the concept the strategy is built on. It is simpler than the name.

> **Meta-labeling = a second filter whose only job is to judge the first signal.**

Everyday analogy: a weather app (signal #1) says "rain." A friend who tracks how reliable that app has been lately (filter #2) tells you whether to actually grab an umbrella. Filter #2 doesn't predict weather — it predicts *whether signal #1 is worth trusting right now.*

Here it works in layers on the 1s-and-0s string:

**Layer 1 (the primary signal), a pattern called `F1`:**
1. Slide along the slice-string.
2. Whenever the recent bits match `F1`, that's a **candidate signal**.
3. The **next slice's** result (win/loss) is that signal's outcome — collect these into a second string, the signal's **"report card."**

**Layer 2 (the meta-label), a pattern called `F2`:**
4. Now look at the **report card**, not the market.
5. Only actually place a trade when the report card's recent history matches `F2`.

Written as `F1 / F2` (e.g. `10? / 00`): `F1` finds candidates in the market; `F2` decides which to take by reading the signal's track record. You can even add a **Layer 3** (`F1 / F2 / F3`) — a filter on the filter's report card.

### Worked example — `F1 = 101` / `F2 = 10`, step by step

Say the day's raw slice-string (each bit = one slice; `1` = that slice **won**, `0` = **lost**) is:

```
position:  1  2  3  4  5  6  7  8  9  10 11 12
raw bit:   1  0  1  1  0  1  0  1  1  0  1  0
```

**Step 1 — `F1 = 101` finds the candidate signals; the *next* bit is each signal's outcome.**

Slide left to right and mark every spot where the **last three bits are `1 0 1`**. The bit **immediately after** each match is that signal's win/loss result:

| Signal | `101` ends at position | next bit = its outcome | result |
|---|---|---|---|
| **#1** | position 3 (bits 1-0-1) | position 4 = **`1`** | win |
| **#2** | position 6 (bits 1-0-1) | position 7 = **`0`** | loss |
| **#3** | position 8 (bits 1-0-1) | position 9 = **`1`** | win |
| **#4** | position 11 (bits 1-0-1) | position 12 = **`0`** | loss |

Collect those four outcomes **in order** → this is the **report card** (the code calls it `filter1Outcome`):

```
report card:  1  0  1  0        (win, loss, win, loss)
```

**Step 2 — `F2 = 10` reads the *report card* (not the market) and decides which signals actually trade.**

A candidate becomes a **real trade** only if, **at the moment it fires**, the report card *so far* ends in `10` (a win then a loss). Walk it signal by signal:

| Signal fires | report card *before* it fires | ends in `10`? | action |
|---|---|---|---|
| **#1** | *(empty)* | no | **skip** — observe only |
| **#2** | `1` | no | **skip** |
| **#3** | `10` | **YES** | ✅ **REAL TRADE** → its outcome = win |
| **#4** | `101` | no | **skip** |

**Result:** the market produced **4 candidate signals**, but `F2 = 10` let only **one** become a real trade — signal **#3**, a win. The other three were watched and recorded (their bits still fed the report card), but **no order was placed**. That is why, in the finalist tables, `F1` matches far more often than the strategy actually "Fires."

> **The whole idea in one line:** `F1` turns the market into a stream of candidate signals and grades each one win/loss → that grade-stream is the **report card** → `F2` reads the report card and only pulls the trigger when the *recent grades* match its pattern. The second search — judging the first signal by its own recent results — **is meta-labeling.**

## A8. The wildcard language
Patterns can use two wildcards, matched at the **end (tail)** of the string:
- `*` = **one or more `0`s** (a run of losses/zeros). E.g. `0*` = `00+`.
- `?` = **one or more `1`s** (a run of wins/ones). E.g. `10?` = `1` then one-or-more `1`s = `101+`.
- Plain `0`/`1` match themselves. Example: `1*1` = `1`, some `0`s, `1` = `10+1`.

## A9. The single most important lesson for a beginner: overfitting
If you test enough patterns, some will look great **by pure luck.** Two defenses, used throughout this research:
- **Out-of-sample (OOS) testing:** find a pattern on one set of days, then test it on **different days it never saw.** A real edge repeats; a lucky one collapses.
- **Big samples only:** a pattern that fired 17 times and won 82% means almost nothing; the same pattern over 164 fires told the truth (62%, a loser). *Small samples lie.*

> **Never trust a pattern found on a small sample or a single good week.** This rule killed several "exciting" findings below.

---
---

# PART B — RESEARCH FINDINGS

## B1. Locked methodology

| Item | Setting |
|---|---|
| Slicing | bid/ask, **1-second throttle**, stop-priority tie-break |
| SHORT entry | at **ask** (stop = ask **+32**, target = ask **−19**) |
| LONG entry | at **bid** (mirror) |
| Bit convention | direction-relative: `1` = this book's slice **won**, `0` = lost |
| Trading day | **3:00 PM PT (prior day) → 3:00 PM PT** |
| Trade window (`morning_open`) | **6:30–8:30 AM PT = 9:30–11:30 ET** |
| Filter state | **resets each trading day** (warms up overnight, fires in the morning) |
| Breakeven (32/19) | **62.7%** = 32 / (32+19) |
| Data | raw NinjaTrader tick exports; **timestamps are UTC** → slicers convert UTC to PT |
| Samples | **In-sample = 55 trading days.** Out-of-sample = 7/6, 7/7, 7/8 2026 |

## B2. The finalists — SHORT 32/19, morning window

| Filter | Layer | Fires | Win rate | MCL | $/fire | Net $ | Green days | OOS result |
|---|---|---|---|---|---|---|---|---|
| **`101/00`** *(sniper)* | L2 | 39 | **84.6%** | 2 | **+$22.3** | +$870 | 85% | 5 fires, 80%, +$88 |
| **`10?/00`** *(workhorse)* | L2 | 148 | 75.0% | 2 | +$12.5 | **+$1,850** | 76% | 15 fires, 66.7%, +$60 |
| **`0101`** *(single-layer)* | **L1** | 169 | 73.4% | 3 | +$10.8 | +$1,832 | 78% | **12 fires, 75.0%, +$150** |
| `1/0/00` *(candidate)* | L3 | 75 | 77.3% | 2 | +$14.9 | +$1,116 | 77% | 5 fires, 100%, +$190 |

**Plain-language reading:**
- **`101/00` (sniper):** highest precision (84.6%) and best dollars-per-trade, but fires rarely (~0.7×/day). Small sample → wide uncertainty. Choose it if you value win rate over volume.
- **`10?/00` (workhorse):** the validated primary. Most trades, most total profit, most trustworthy (148 fires). Works across regimes (UP 68% / NEUTRAL 82% / DOWN 71%) — **not** just a down-market fluke.
- **`0101` (the standout discovery):** the simplest possible — **one layer, one literal pattern, no F2 gate.** In-sample 73.4% and out-of-sample 75.0% agree tightly → strong evidence it's **not overfit.** It beat the primary out-of-sample on win rate, dollars, *and* drawdown, and was green on 7/6 (a day the primary lost).
- **`1/0/00` (L3, probation):** hits the target profile, but was found by searching ~8,000 combinations → carries selection risk. Watch only.

## B3. Why `0101` works (the mechanism)
`0101` = **loss-win-loss-win**, ending on `...01` (a reversion). It directly detects *"the market is oscillating and just reverted."* That **alternation is itself the regime confirmation** that an F2 gate usually provides — which is why it works as a single layer.

**The ending phase matters, not the length.** Every extension breaks it:

| Variant | Win rate | Verdict |
|---|---|---|
| `0101` | **73.4%** | ✅ the sweet spot |
| `10101` | 71.6% | ✅ still works (same `...01` ending) |
| `1010` | 68.2% | ⚠️ opposite phase, weaker |
| `01011` | 61.0% | ❌ below breakeven — adds a momentum tail |
| `0101?` | 65.4% | ❌ same problem (`?` = momentum tail) |
| `01010` | 60.0% | ❌ wrong ending phase |

**Lesson: longer ≠ more selective.** Extending `0101` either adds a momentum tail or flips the phase — both destroy the reversion edge.

## B4. Rejected — do not revisit

| Tested | Result | Why it failed |
|---|---|---|
| **L1 simple gates** (`10?`, `1`, `101` alone) | 64–68%, MCL 4–8 | Just re-samples the ~65% raw baseline. **The F2 gate is load-bearing**: adding it to `10?` lifts 65.8%→75.0%, halves MCL 5→2, quadruples $/fire. |
| **`111/11?`** | 64.3%, only 42% green days | Momentum bet — opposite of the reversion logic. Profit was outlier-carried (top 5 days = +$1,092 of a +$408 total). |
| **`1001`** | **62.2% in-sample (below BE), −$92** | ⚠️ Showed 82.4% / +$340 OOS — a **pure fluke.** 17 fires can't overturn 164. Textbook small-sample trap. |
| **`010/1*1`** | 69.8%, MCL 4 | Positive but dominated on every axis by the finalists. `1*1` admits wide-bottom V's → dilutes the signal. |
| **`010/11`, `010/1?1`** | 59.3%, 60.3% | Below breakeven. `010` is a weak first gate. |
| **Overnight window** | 66.2%, MCL 4 | Looked like 83% on the test week → washed out on 55 days. Barely above BE; wider overnight spreads would erase it. **Not a second window.** |
| **Permissive filters** (`1/0`, `10?/0`, F2=`0`) | 700–1,400 fires, ~65–70%, +$2–5/fire | **Friction trap.** Big gross totals, but per-trade edge is *below* the ~2-pt MNQ round-trip cost → losing after costs. |

## B5. Theory backing (academic literature)
Both "anti-intuitive" findings are well-supported:

**Why does SHORT work when the market trends up?**
→ **Intraday reversal is stronger after UP moves.** A 15-year study of US index futures (Grant, Wolf & Yu) found significant intraday reversal after large opening moves, *more pronounced after large positive moves.* The macro uptrend is invisible inside a 19-point intrabar move; at tick scale, up-moves over-extend and snap back — exactly what SHORT harvests.

**Why does 32-stop/19-target beat 19-stop/32-target?**
→ **Gain/loss asymmetry ("elevator down, stairs up").** Down-moves are faster than up-moves; a near target + wide stop harvests frequent small reversions while surviving noise. Root cause: the **leverage effect** — volatility is higher after negative returns.

**VIX test (does volatility change the edge?):**

| Regime | Days | Fires | Win rate | $/fire |
|---|---|---|---|---|
| HIGH VIX (≥17.6) | 29 | **100** | 75.0% | +$12.5 |
| LOW VIX (<17.6) | 26 | **48** | 75.0% | +$12.5 |

→ **Volatility drives FREQUENCY (2× the fires), not edge-per-trade.** Win rate is VIX-neutral → the edge is robust across regimes, a point in favor of it being real.

⚠️ **The same literature warns:** transaction costs decide tradeability. A ~2-point round-trip cost eliminates the gross edge in most thin signal families. **The finalists clear this (+$10.8 to +$22.3/fire ≈ 5–11 pts gross); the rejected thin filters do not** — which is *why* they were rejected.

## B6. Position-sizing (qty) rule
Longest-match-wins, order-independent, applied to the recent real-outcome history:
```
("10",2)  ("100",2)  ("1000",2)          -> after a win, losses 1-3 = size ×2
("10000",0) ("100000",0) ("1000000",0)   -> losses 4-6 = SKIP (qty 0)
```
- **Leading `1` is a deliberate bug-guard:** a loss-run straight from the open (`0000…`, e.g. a data/logic bug) matches *nothing* → stays at base size ×1, and **never doubles into a disaster.**
- ⚠️ **Coupling:** re-check this table whenever `MaxRealLossInARow` changes; the ×0 skip lines only cover loss-runs up to their length.

## B7. Standing conclusions
1. **Two layers is the floor** for a real edge — *unless* the pattern self-confirms the regime (`0101`).
2. **Per-trade edge > fire count.** Friction eats thin edges; only trade filters clearing ~$10+/fire.
3. **60–70% raw baseline is the sweet spot** for this geometry.
4. **Backtest MCL is a floor, not a ceiling** — plan for 5–6 consecutive losses.
5. **Live win rate will likely run below backtest.** Treat roughly *half* the edge above breakeven as durable.
6. **Small samples lie** (`1001`: 82% on 17 → 62% on 164; overnight: 83% on 3 days → 66% on 55). Never promote on one week.
7. **LONG stays manual-enable, up-days only** (LONG `10?/110`: UP days 87.9%, NEUTRAL 67.1% — regime-dependent).
8. **Stay on MNQ futures.** QQQ adds borrow fees, PDT, and the uptick rule (which blocks shorts on exactly the volatile days the edge fires). 0DTE options are worse (delta, spread, theta, gamma all break the 32/19 geometry).

## B8. Forward-log discipline — the real kill switch
Track **weekly** per finalist: fires · W/L · win rate · MCL · net $ · green-day %.
- If win rate drifts toward the ~65% raw baseline over several weeks → **the edge is gone; stop.**
- If P(win | last loss) drops toward baseline → **kill the qty rule.**
- Promote a candidate only after **several weeks** of consistency — never on one good week.

> The edge is a small, real inefficiency in a niche too small for large players. It is **fragile and does not regenerate.** Forward-testing discipline is the only thing between a working strategy and a slow bleed.

---
---

# PART C — RUNNING SHORT AND LONG TOGETHER

## C1. The bottom line (recommended default)
> **Run the SHORT book only.** It is the validated edge. Keep the LONG book **manual-enable, up-days only**, and preferably in **observation mode** (`EnableRealOrder=false`) so it logs its would-be trades without competing for real positions.

Everything below explains *why*, and what actually happens if you do run both.

## C2. What happens on the SAME account (e.g. "simBOTH")
The strategies contain a deliberate **shared-account guard** (`AccountBusyOnThisInstrument()`), checked the instant a filter arms a real trade:

- If the account **already holds any MNQ position, or has any working/pending order** on MNQ — from the other book, or a manual/ATM trade — the newly-armed trade is **demoted to observation**: **no order is placed**, and the log marks it `OBS_ACCOUNT_BUSY`.
- So **the first book to fire takes the slot; the other sits out** until the position closes (stop or target) and the account is flat again.

**Result: on one shared account you will never be long and short MNQ at the same time.** This is the intended safety behavior — no double-positioning, no accidental self-hedge.

**One honest caveat:** the guard is a *soft check at fire-time*, not a hard lock. If both books armed on the exact same tick — before either order registered on the account — both could slip through and open. In practice their throttle phases and patterns differ, so they almost never arm on the same tick, but it is not impossible. (On any error reading the account, it fails **safe** → treats the account as busy → places no order.)

## C3. What happens on SEPARATE accounts (what you observed)
If SHORT runs on one account (e.g. `simSHORT`) and LONG on another (`simLONG`), the guard only ever inspects **its own** account. Each sees its own account as free, so **both fire independently** — which is exactly why you heard *"order placed… order placed"* back-to-back. **That is expected, not a bug.**

But note the downside of separate accounts: you can end up **short MNQ on one account and long MNQ on the other at the same time.** Across the two accounts that's a **net-flat hedge that earns nothing** while paying **double commissions and double slippage.** It also means each book's real behavior no longer matches its solo backtest.

## C4. The "50-50" problem — blocking is not smart selection
You observed that when the guard *does* block the second book, it's **roughly a coin-flip** whether that was the right call: sometimes the first (kept) trade wins and blocking the second was correct; sometimes the blocked trade would have been the better one.

That's the crux, and it's expected: **the guard does not pick the *better* trade — it picks the *earlier* one,** and "earlier" is set by the two books' independent 1-second throttle phases, which is essentially arbitrary. So over many events the blocking neither reliably helps nor reliably hurts — it roughly washes out. **This interaction was never rigorously backtested** (each book was validated *standalone*), which is why the numbers in Part B do **not** describe a two-book live account.

## C5. Pipeline continuity — a blocked book only "sleeps," it never breaks
This is the reassuring part, and it's by design. When a book is blocked (demoted to observation), **its filter pipeline keeps running normally:**
- The slice still resolves, and its bit is still appended to **`rawString`**.
- **`filter1Outcome`** still updates, and **`isArmed`** still re-computes.
- So the filter stays fully alive and continuous **in memory** — it did not reset, warm-up, or lose its place.
- Only the **order** is suppressed. The **real-trade audit** (`realTradeOutcome`), the **loss-streak breaker** (`realLossesInARow`), and the **qty session** are **not** touched — they only advance on *actual fills*.

**Consequence:** while blocked, the book is "asleep" for real orders but its brain is fully awake. The **filter behavior is identical to standalone** (it arms on exactly the same bits), so the moment the account frees up and it re-arms, it **fires a real trade again** with no interruption. What differs from the solo backtest is only *which* armed trades became real orders — the blocked ones are logged as `OBS_ACCOUNT_BUSY` (the trades it *would* have taken).

## C6. Practical guidance
1. **Default: SHORT only, one account.** Simplest, and it's the validated book.
2. **If you want to study both together, use one shared account (`simBOTH`)** so the guard is active and you observe the *real* contention — never two separate live accounts (that double-positions and double-pays costs).
3. **Keep the weaker/newer book in observation** (`EnableRealOrder=false`). It will still log its full would-be book (`WOULDBE_TRADE` / `OBS_*` rows) for analysis without stealing the position slot from the stronger book. Per Part B, LONG is the weaker, regime-dependent book — so LONG is the natural one to keep in observation.
4. **Do not expect solo-backtest numbers from a two-book account.** Live fire counts will be **lower** (each book misses the trades that fired while the other held the slot), and that combined behavior is untested. Treat two-book live trading as a **new experiment with its own forward-log**, per Part B8.
5. **NT setting reminder:** set `ConnectionLossHandling` = **`Keep`** (not `Recalculate`) to stop restart churn from feed flapping; the strategies survive reconnects and RESUME their pipeline from their own log.

---
---

# Glossary

- **MNQ** — Micro E-mini Nasdaq-100 futures. $2/point; 1 tick = 0.25 pt = $0.50.
- **Slice** — one small fixed-bracket bet, started ~once per second: enter at current price, exit at a target or a stop. The base unit of this strategy (not a Renko brick, not a time candle).
- **32/19 bracket (SHORT)** — enter at ask; **stop = +32 pts** (loss), **target = −19 pts** (win).
- **Bit** — one slice's result: `1` = won, `0` = lost (direction-relative to the book).
- **Breakeven win rate** — the win rate needed to make zero profit. For 32/19 it is **62.7%**.
- **F1 (Layer 1 / primary signal)** — a pattern in the slice-string that produces candidate trades.
- **F2 (Layer 2 / meta-label)** — a pattern applied to F1's *report card* (its win/loss history) that decides which candidates to actually trade. **Meta-labeling** = using a second model to judge the first.
- **Layer 3** — a further filter applied on top of Layer 2's outcomes.
- **Report card / `filter1Outcome`** — the string of win/loss results of an F1 signal, which F2 reads.
- **Wildcards** — `*` = one-or-more `0`s; `?` = one-or-more `1`s; matched at the tail of the string.
- **Raw baseline** — the ~65% win rate of an unfiltered slice book; a filter must beat this meaningfully to matter.
- **MCL (Max Consecutive Losses)** — the longest losing streak; the key risk number for sizing.
- **In-sample / Out-of-sample (OOS)** — days used to *find* a pattern vs. *different* days used to *test* it. OOS agreement is the main proof an edge is real.
- **Overfitting / small-sample trap** — a pattern looking good by luck on too little data (e.g. `1001`: 82% on 17 fires, 62% on 164).
- **Friction / cost ceiling** — the ~2-point MNQ round-trip cost; filters earning less than this per trade lose after costs.
- **Leverage effect** — volatility rises after price falls; the theory behind "elevator down, stairs up" and the 32/19 bracket.
- **Intraday reversal** — the tendency of sharp intraday moves (especially up-moves) to snap back; the theory behind why SHORT works.
- **Qty rule** — the position-sizing table that scales size with recent outcomes (and skips deep loss-runs).
- **Loss-streak breaker (`realLossesInARow`)** — a cumulative safety stop that halts real trading after too many real losses; survives reconnects.
- **Observation / `OBS_*` / `WOULDBE_TRADE`** — a slice the filter armed but that placed **no** real order (outside hours, account busy, qty-0, or `EnableRealOrder=false`). The bit is still recorded; it's the trade the book *would* have taken.
- **`AccountBusyOnThisInstrument()`** — the shared-account guard that demotes a trade to observation when MNQ already has a position/order, preventing double-positioning on one account.
- **`morning_open`** — the trade window, 6:30–8:30 AM PT = 9:30–11:30 ET.
- **Trading day** — 3:00 PM PT → 3:00 PM PT; the filter state resets at the boundary and warms up overnight.

---

*Research numbers verified against the 55-day in-sample set and the 7/6–7/8 out-of-sample week. Slicer cross-checked: 7/7 session = 296 bits / 200 ones (67.6%), exact match with the independent slicer. Companion document (different method): the Renko-bar research summary + training guide, 2026-07-25.*
