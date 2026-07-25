# MNQ Renko-Bar Strategy — A Training Guide (from zero)

**Who this is for:** someone brand new to this project who does **not** already know what a Renko bar is, what "meta-labeling" means, or how the strategy works. Read it top to bottom; each part builds on the one before.

**What it covers:** the ideas first (Parts 1–4), then what our research actually found (Parts 5–7), then the honest warnings (Part 8) and a glossary (Part 10).

**Date:** 2026-07-25 · **Instrument:** MNQ (Micro Nasdaq-100 futures) · **Bar type:** NinjaTrader built-in Renko.

---

## Part 1 — The building block: what is a Renko bar?

Normal candlestick charts draw one bar per unit of **time** (e.g. one bar every 5 minutes). **Renko bars ignore time entirely.** They draw a new "brick" only when **price moves a fixed distance**. If price sits still for an hour, no brick forms. If price runs fast, many bricks form quickly.

We use a **brick size of 40 ticks**. On MNQ, 1 tick = 0.25 points, so:

> **1 Renko brick = 40 ticks = 10 points.**

There are two colors:
- **Green brick** = price moved **up** by one brick.
- **Red brick** = price moved **down** by one brick.

### The most important rule: continuation vs. reversal

This is the single most important thing to understand about Renko, because the whole strategy depends on it.

- To **continue** in the same direction, price only needs to move **1 brick (40 ticks = 10 points)**.
  - After a green brick, another green forms once price rises 10 more points.
- To **reverse** to the opposite color, price must move **2 bricks (80 ticks = 20 points)**.
  - After a green brick, a red brick only forms once price *falls* 20 points.

This "**2× reversal**" is standard Renko behavior. Remember it as:

> **Continuation = 10 points. Reversal = 20 points.**

That asymmetry — reversals cost twice as much movement as continuations — is what makes the strategy's math work (Part 3).

### A tiny example

Say price is 20,000 and we just printed a green brick (close = 20,000):
- Price rises to 20,010 → **new green brick** (continued up 10 pts). ✓
- Instead, price falls to 19,990 (down 10 pts) → **no red brick yet** (a reversal needs 20 pts).
- Price keeps falling to 19,980 (down 20 pts) → **now a red brick prints.**

---

## Part 2 — Turning bricks into a string of 1s and 0s

Once we have a sequence of colored bricks, we write each one as a single **bit**:

- **"LONG book" encoding:** green = `1`, red = `0`.
- **"SHORT book" encoding:** red = `1`, green = `0`. (Just the opposite.)

So one trading day becomes a string like:

```
1 1 0 1 0 0 1 1 1 0 0 0 ...
```

Everything after this point is **pure math on that string of 1s and 0s**. We don't need charts anymore — the string *is* the market, brick by brick. (A "book" just tells us which color we call `1`. The tradeable edge we found lives in the **SHORT book**, where `1 = red = down`.)

---

## Part 3 — The math that decides everything: breakeven

Before any pattern, you must understand what a single trade wins or loses, because that sets the **breakeven win rate** — the win rate you need just to not lose money.

We enter a trade at the close of a brick, and the trade's result is decided by the **very next brick**. Two very different situations:

### Situation A — chasing a REVERSAL ("transit" trade)
You bet the market will **flip** to the opposite color.
- If you're right, the flip is a **reversal = 2 bricks = 20 points → you WIN +20.**
- If you're wrong, the market **continues = 1 brick = 10 points against you → you LOSE −10.**

Break-even math: you win 20 when right, lose 10 when wrong. Let `p` = win rate.
```
win × p  =  loss × (1 − p)
20 × p   =  10 × (1 − p)
p = 10 / (20 + 10) = 1/3 = 33.3%
```
> **Reversal trade → breakeven is 33.3%.** You only need to be right 1 time in 3.

### Situation B — chasing a CONTINUATION
You bet the market will **keep going** the same direction.
- If right, that's a **continuation = 1 brick = 10 points → WIN +10.**
- If wrong, it **reverses = 2 bricks = 20 points against you → LOSE −20.**
```
p = 20 / (10 + 20) = 2/3 = 66.7%
```
> **Continuation trade → breakeven is 66.7%.** Much harder — you must be right 2 times in 3.

### Why this decides the whole strategy
Because reversals need only a 33.3% win rate, the **reversal (transit) trade is the favorable one.** Continuation is a bad deal. This is why, later, we insist a signal pattern **ends in a `0`** — that's the setup that makes the next trade a *reversal* chase (win 2 bricks, risk 1 brick).

### The "fair coin" warning (gambler's ruin)
Here's the humbling part. Imagine the market is a pure coin flip (no skill possible). Basic probability (the "gambler's ruin" formula) says that under a random market:
- A reversal chase wins **exactly 33.3%** of the time — **exactly breakeven.**
- A continuation chase wins **exactly 66.7%** — **exactly breakeven.**

So **the geometry alone gives you no edge.** Both trades are fair coins that make zero money on a random market. You only make money if the market is **not** random in your favor:
- **Mean-reversion** (moves snap back) → reversal chase beats 33.3%. ← *this is our edge.*
- **Momentum** (trends persist) → continuation chase beats 66.7%.

Everything we tested is really asking one question: *does this pattern beat its fair-coin breakeven?*

---

## Part 4 — What is "meta-labeling"? (the Layer 1 / Layer 2 idea)

This is the concept the strategy is named after, and it's simpler than it sounds.

### The plain-English idea
> **Meta-labeling = a second model whose only job is to judge the first model's calls.**

The first model says *"take this trade."* The second model — the "meta" model — looks at how the first model has been doing lately and says *"…yes, actually take it"* or *"…no, skip it this time."* The second model is trained on the **outcomes (win/loss labels) of the first model** — hence *meta*-label.

An everyday analogy: a weather app (model 1) says "it'll rain." A friend who tracks how often that app has been right lately (model 2) tells you whether to actually bring an umbrella. Model 2 doesn't predict weather — it predicts *whether model 1 is worth trusting right now.*

### How it works here, step by step

**Layer 1 (the primary signal), controlled by a pattern we call `F1`:**
1. Walk along the brick string.
2. Whenever the recent bricks match the pattern `F1`, we have a **signal**.
3. The **next brick decides the outcome**: it's a **win (1)** or a **loss (0)**.
4. Collect those outcomes into a second string — call it the **"report card"** of `F1`.

**Layer 2 (the meta-label), controlled by a pattern we call `F2`:**
5. Now look at the **report card**, not the market.
6. Only actually place a trade when the report card's recent history matches pattern `F2`.

So `F1` finds candidate trades in the *market*; `F2` decides which of them to take by reading `F1`'s *track record*.

### A worked example
Market string ends in `...1000`, and our signal pattern is `F1 = 1000`.
- Every time the string ends in `1000`, we note the **next** brick. Say over the day those next-bricks were: `0, 1, 1, 1, 0, 1, 0 …` — that's `F1`'s **report card** (`0111010…`), where 1 = the signal won, 0 = it lost.
- Now suppose our meta-pattern is `F2 = 010`. Layer 2 says: *only take the next `1000` signal when the report card currently ends in `0,1,0`* (loss, win, loss).
- So most `1000` signals are watched but skipped; we only trade the ones that arrive right after a `010` stretch in the report card.

That's the entire pipeline: **`F1` on the bricks → report card → `F2` on the report card → real trades.** (In shorthand you'll see it written `F1 / F2`, e.g. `10 / 010`.)

### Two rules that follow from Part 3
- **`F1` must end in `0`.** Only then is the trade a *reversal* chase (favorable 33.3% breakeven). If `F1` ends in `1`, you're chasing continuation (66.7% breakeven, a bad deal). Valid signals: `10`, `110`, `1000`, `10000` — all end in 0.
- **`F2` can be anything.** It only *selects* which signals to take; it never changes the win/loss size, so it never changes the 33.3% breakeven.

---

## Part 5 — What we found: the edge that works ✓

We built 21 clean trading days (Oct 2025 → Jul 2026) of real MNQ ticks into these strings and tested many patterns. We always checked results **out-of-sample (OOS)** — find a pattern on one set of days, then test it on *different* days it never saw. A pattern that only looks good on the days used to find it is worthless; a real edge repeats on unseen days.

**The winner: SHORT-book `F1 = 1000` (by itself, no F2 filter).**
- In plain terms: after the market makes **one down-brick then three up-bricks** (a stretched little rally), **short it**, betting it reverses back down.
- **Win rate 37.2%** over 470 trades — comfortably above the 33.3% breakeven.
- **Out-of-sample it held almost perfectly:** 37.3% on the training days, 37.1% on the unseen test days. That near-identical repeat is the strongest sign it's real, not luck.

**A "just-right" depth:** we tried shallower and deeper versions. The edge is an **upside-down U** — it peaks at a 3–4 brick stretch and falls apart on either side:

| Signal (rally depth) | Win rate | Verdict |
|---|---|---|
| `10` (1 up-brick) | 32.6% | too shallow — fails |
| `100` (2) | 34.6% | weak |
| **`1000` (3)** | **37.2%** | **best, and stable** |
| **`10000` (4)** | **37.6%** | also good |
| `100000` (5) | 31.7% | too deep — fails |
| `1000000` (6) | 28.8% | fails |

*Why:* a 3–4 brick over-extension tends to snap back (great to fade), but a 1-brick blip is just noise, and a 5–6 brick run is usually a genuine trend you should **not** fight.

**A cleaner-risk version: `F1 = 10 / F2 = 010`.**
- Win rate **39.3%**, and it also held out-of-sample.
- It trades less often but its worst losing streak in a day is only **6** (vs **13** for plain `1000`) — which matters a lot for position sizing (Part 7).

---

## Part 6 — What we found: what does NOT work (and why) ✗

These are the most valuable lessons, because they're *general* — they tell you which whole families of ideas to avoid.

**1. Buying dips (the LONG book) fails — but it's about the era, not the idea.**
The mirror trade — after a 3-brick *down*-move, buy expecting a bounce — came in at **32.8%, below breakeven.** Our data period was a broadly **rising** market, where sharp rallies snap back (short works) but dips tend to keep falling a bit before bouncing (long fails). In a falling market, the long side might well be the one that works. *Lesson: the edge is regime-dependent; test across up, down, and choppy periods.*

**2. Momentum / continuation bets fail.** Every "keep-going" pattern landed right at its 66.7% fair-coin line (e.g. `11` = 65.0% over 2,082 trades — a hair on the losing side). *Lesson: there is no momentum edge in these bricks; the market is near-random on continuation, with a slight tilt toward reversing.* (This also proved our whole pipeline was sound: theory predicted "fair at 66.7%," and the data agreed to within a point.)

**3. Betting on your own streaks fails — the big one.** We repeatedly tried filters like "trade only after 3 losses in a row" (the *"I'm due for a win"* idea) and "increase/decrease size based on recent wins and losses." **None worked.** The reason: the trade outcomes are essentially **independent** (statisticians say *IID*) — a losing streak does **not** make the next trade more likely to win. Roulette doesn't get "hot" or "cold," and neither does this.

> **Golden rule that came out of all this:**
> **Filter on the market's *brick structure*, never on your *own recent win/loss run*.**

---

## Part 7 — Position sizing (how many contracts) and the "MCL" number

**MCL = Maximum Consecutive Losses** — the longest run of losing trades in a row. It's the number that decides how a size-changing rule survives (or dies), because our sizing resets each day, so the **worst single-day streak** is what matters.

| Signal | Win rate | Worst-day MCL |
|---|---|---|
| `1000` | 37.2% | **13** (one very bad day) |
| `10 / 010` | 39.3% | **6** (much calmer) |

**Lesson A — a "reduce size when losing" rule backfired.** We simulated cutting size (and stopping) during losing streaks on `1000`. It **cut profit ~80%** ($3,300 → $660) **and made the worst day worse.** Why? Because outcomes are independent (Part 6 #3), changing size based on recent results can't add value — and the "stop after 4 losses" rule kept shutting the day right before it recovered. *With independent outcomes, flat (constant) size makes the most money.*

**Lesson B — control risk with a daily loss cap, not a streak rule.** Instead of shrinking after losses, just say "stop trading for the day at −60 points." That protects against the one disaster day without touching the 15 normal days.

**Lesson C — if you *must* vary size (call it what it is: gambling), cap it shallow.** We found the higher-win-rate sub-buckets:

| Recent report card ends in… | Win rate | Note |
|---|---|---|
| `110` (2 wins, 1 loss) | 42% | ok |
| `1100` (2 wins, 2 losses) | 50% | best — supports slightly bigger size |
| `11000` (2 wins, 3 losses) | **11%** | **collapses — never size up here** |

So a rule like "`110` → 2 contracts, `1100` → 3 contracts" is *directionally* supported, **but you must cap it at `1100`** — one step deeper (`11000`) craters to 11%. And the samples are tiny (18–31 trades), so keep the bumps small and treat it as a side-bet, not the strategy.

---

## Part 8 — Honest warnings (do not skip this)

A beginner reading the win rates above might get excited. Please internalize these five cautions first:

1. **The edge is thin.** 37% vs a 33.3% breakeven is only a **3.7-point cushion.** Trading costs and **slippage** (getting a slightly worse fill than the ideal price) can eat 1–2 points of that. The real, after-cost edge may be small — or gone. Treat the win rates as a **best case ceiling**.

2. **The numbers are not yet verified against the live platform.** All results come from a Python program that *rebuilds* the Renko bricks from raw ticks. It has **not yet been checked** against the bricks NinjaTrader actually produces live. This check (called *calibration*) is the **single most important unfinished task** — until it passes, every number here is provisional.

3. **Only 21 days, mostly one market mood.** Enough to find candidates and reject bad ideas, not enough to be certain. More days — especially from a *falling* market — are needed.

4. **Independent outcomes mean no "systems" on your own results.** No martingale (grow after losses), no "due for a win," no size-scaling on streaks. These feel clever and reliably lose. (Part 6 #3.)

5. **The market changes.** This edge is short-only *in this rising market*. When the regime changes, re-test. Nothing here is "set and forget."

---

## Part 9 — The whole thing in one paragraph

On MNQ 40-tick built-in Renko, turn each brick into a 1 or 0. Look for a **stretched rally of 3–4 up-bricks and short it, betting it reverses** (this is a "reversal/transit" trade, which only needs a **33.3%** win rate because a reversal pays 2 bricks while the stop risks 1). Our best signals — **SHORT `1000` (≈37%)** and **SHORT `10/010` (≈39%)** — clear that bar and repeat on unseen days. Trade them at **flat size**; the edge is real but **thin and cost-sensitive**. Do **not** build size rules on your own win/loss streaks — trade outcomes are independent. Before trading real money, **verify the bricks against the live platform.**

---

## Part 10 — Glossary

- **MNQ** — Micro E-mini Nasdaq-100 futures. Small-size Nasdaq futures contract. $2 per point; 1 tick = 0.25 pt = $0.50.
- **Renko bar / brick** — a bar drawn only when price moves a fixed distance (here 40 ticks / 10 pts), ignoring time. Green = up, red = down.
- **2× reversal** — a Renko rule: continuing the trend needs 1 brick (10 pts); reversing needs 2 bricks (20 pts).
- **Bit string** — the sequence of 1s and 0s made by writing each brick's color as a digit.
- **LONG book / SHORT book** — the two ways to label colors: LONG (green=1), SHORT (red=1). Our edge is in the SHORT book.
- **F1 (Layer 1 / primary signal)** — the pattern in the brick string that generates candidate trades. Must end in `0` to be a favorable reversal trade.
- **F2 (Layer 2 / meta-label)** — a pattern applied to F1's *track record* (report card) that decides which candidate trades to actually take.
- **Meta-labeling** — using a second model to judge the first model's signals, trained on the first model's win/loss outcomes.
- **Reversal (transit) chase** — betting the next brick flips color. Win = 2 bricks (+20), loss = 1 brick (−10). Breakeven **33.3%**.
- **Continuation chase** — betting the next brick keeps the color. Win = 1 brick (+10), loss = 2 bricks (−20). Breakeven **66.7%**.
- **Breakeven win rate** — the win rate needed to make zero profit. Below it you lose; above it you profit.
- **Gambler's ruin / "fair coin"** — the fact that, on a purely random market, each trade wins exactly at its breakeven rate. Any real edge must come from the market being non-random (mean-reversion or momentum).
- **Mean-reversion** — the tendency of moves to snap back. Favors reversal trades.
- **Momentum** — the tendency of trends to persist. Favors continuation trades.
- **IID (independent outcomes)** — trade results don't depend on previous results; no streaks to exploit.
- **Out-of-sample (OOS)** — testing a pattern on days that were **not** used to discover it. The real test of an edge.
- **MCL (Max Consecutive Losses)** — longest run of losses in a row; the key risk number for sizing.
- **Slippage** — the difference between the ideal price and the price you actually get filled at; a hidden cost.
- **Calibration** — checking the Python-rebuilt bricks match the live NinjaTrader bricks, bit for bit. The essential unfinished step.
- **Regime** — the market's prevailing character (up-trend, down-trend, chop). Which side has an edge depends on it.

---

*End of training guide — 2026-07-25. Companion technical version: `MNQ_research_Renkobar_summary_2026-07-25.md`.*
