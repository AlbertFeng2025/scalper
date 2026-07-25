# MNQ Renko-Bar Strategy — Research Summary

**Instrument:** MNQ (Micro E-mini Nasdaq-100 futures)
**Bar type:** NinjaTrader **built-in** Renko
**Date of summary:** 2026-07-25
**Research window:** 09:30–16:00 New York time (RTH), 21 clean trading days spanning Oct 2025 → Jul 2026

---

## 0. Executive summary (TL;DR)

- The one **repeatable, out-of-sample-stable edge** found is a **SHORT-book reversal (transit) trade that fades over-extended rallies**:
  - **F1 = `1000` alone** — win rate **37.2%** (n=470), breakeven 33.3%, held OOS (train 37.3% / test 37.1%).
  - **F1 = `10` / F2 = `010`** — win rate **39.3%** (n=163), tighter risk (worst daily loss streak 6 vs 13).
- **What does NOT work** (all tested and rejected):
  - LONG book (buying dips): below breakeven in this up-trending sample.
  - Continuation / momentum bets (F1 ending in `1`): sit right on the 66.7% "fair coin" line — no edge.
  - Any filter or qty rule based on your own recent **win/loss streak**: the trade outcomes are ~IID, so "due for a win" logic fails.
- **The edge is thin and slippage-sensitive** (37% vs a 33.3% breakeven ≈ 3.7-point cushion; costs may eat most of it). Treat headline win rates as a **ceiling**.
- **The single most important unfinished step:** the strings were built by a Python slicer that has **not yet been calibrated** against a live production `rawString`. Do that before committing real size.

---

## 1. Setup & methodology

### 1.1 Bar definition
- **Brick size:** 40 ticks. MNQ tick = 0.25 pt, so **1 brick = 10 points**.
  - (80-tick bricks were tried first but produced too few bars per day; 40 ticks was chosen for sample size.)
- **Reversal rule:** standard **2× reversal**. A brick continues in-trend after **1 bar (40 ticks / 10 pts)**; it reverses only after **2 bars (80 ticks / 20 pts)**.

### 1.2 Data pipeline
- Raw ticks exported from NinjaTrader (format `yyyyMMdd HHmmss fffffff;last;bid;ask;vol`).
- **Confirmed:** NT tick exports are always timestamped in **UTC** (no option to change). The slicer converts UTC → New York for the session window, and logs both NY and PT for verification. DST is handled automatically.
- Each raw file holds **one full trading session** (≈ 6 PM ET open the prior evening → next-day close), which spans two UTC calendar dates. Because each file is self-contained (full overnight warm-up + its RTH day), the daytime string is properly "warmed" and files can be processed independently.
- Bricks are built **tick-by-tick** in Python, replicating built-in Renko (continuation ±1 brick, reversal ∓2 bricks). This sidesteps three problems with chart-based approaches:
  - Built-in Renko **cannot use Tick Replay** (it uses RemoveLastBar — confirmed, no work-around).
  - The Strategy Analyzer builds historical Renko from **minute data** (approximation, not live-faithful).
  - **UniRenko** can tick-replay but is a **different algorithm**, so its bricks would not match the built-in Renko traded in production.

### 1.3 Filter pipeline (Layer 1 / Layer 2)
Matches the production `A_layer2_wildcard_tester` exactly (validated bit-for-bit on literal patterns):
- **Layer 1:** whenever the raw brick string tail matches **F1**, the **next brick** is the trade outcome (1 = target hit / win, 0 = loss). The sequence of those outcomes is the "F1-outcome string."
- **Layer 2:** **F2** is matched against the **F1-outcome string**; a real trade fires only when that meta-condition is armed. Equivalent to `outcomes_after(outcomes_after(bits, F1), F2)`.
- Wildcards: `*` = one-or-more `0`s, `?` = one-or-more `1`s.
- Strings are processed **per day** (reset each session), then pooled — matching production, where `rawString` resets each session.

---

## 2. The core geometry (most important concept)

Because of the 2× reversal rule, **win and loss sizes are asymmetric**, and the asymmetry flips depending on whether you chase a **reversal** or a **continuation**. This sets the breakeven, and it is the key to the whole strategy.

| Chase type | F1 ends in… | Win = | Loss = | Breakeven WR |
|---|---|---|---|---|
| **Reversal (transit)** | opposite of target (e.g. `…0`, chase 1) | reversal = **2 bars = 80 ticks = 20 pts** | continuation = **1 bar = 40 ticks = 10 pts** | **33.3%** |
| **Continuation** | same as target (e.g. `…1`, chase 1) | continuation = **1 bar = 10 pts** | reversal = **2 bars = 20 pts** | **66.7%** |

**Why F1 must end in `0` for the transit trade:** only then are you chasing a reversal (win 2 bars, risk 1 bar), giving the favorable 33.3% breakeven. If F1 ends in `1` you are chasing continuation — 2-bar risk for 1-bar reward, a 66.7% breakeven, which is strictly worse. (`10`, `110`, `1110`, `1000` all end in 0 → all valid transit patterns.)

**Gambler's-ruin sanity check (fundamental):** under a pure random walk, the probability of hitting the near barrier before the far one equals *far / (near + far)*. That makes:
- The **reversal chase** a fair bet at exactly **33.3%**.
- The **continuation chase** a fair bet at exactly **66.7%**.

So **neither method has an inherent edge from geometry** — an edge exists only where the market deviates from a random walk:
- **Mean-reversion** → reversal chase beats 33.3%.
- **Momentum** → continuation chase beats 66.7%.

This framework explains every result below.

### Production trade (SHORT-book transit, the tradeable edge)
- Open **short** on the armed signal.
- **Stop:** 40 ticks (10 pts) = 1 bar. **Target:** 80 ticks (20 pts) = 2 bars.
- **Win +20 pts / loss −10 pts → breakeven 33.3%.** (MNQ = $2/pt, so +$40 / −$20 per contract, gross.)

---

## 3. Bit encoding (the two "books")

The raw string encodes brick color; the two books are bitwise complements:

- **LONG book:** `1 = green (up)`, `0 = red (down)`. Chasing 1 = betting the market goes up.
- **SHORT book:** `1 = red (down)`, `0 = green (up)`. Chasing 1 = betting the market goes down.

The tradeable edge lives in the **SHORT book** (fade rallies). The LONG book is the mirror (buy dips) and did not work on this data.

---

## 4. Data used

**21 clean full RTH days** (plus 1 partial, 2 empty weekend files excluded):

| Month | Days |
|---|---|
| Oct 2025 | 20, 21, 22, 24 |
| Dec 2025 | 9, 11, 12 |
| Feb 2026 | 2, 3, 4, 5, 10 |
| Apr 2026 | 20, 21, 22, 23, 24 |
| Jul 2026 | 6, 7, 8, (9 partial) |

~11,700 in-window bricks total. **Regime caveat:** this span was broadly **up-trending**, which biases results toward the short (fade-rally) side (see §5).

---

## 5. Findings

### 5.1 SHORT-book reversal — THE EDGE ✓

**F1 = `1000` alone (no F2):** the most robust result.
- Win rate **37.2%**, n = **470**, breakeven 33.3%, ≈ +$1,100 at flat qty=1.
- **Out-of-sample: train 37.3% / test 37.1%** — near-identical on days the rule never touched. Strongest evidence of a real (not curve-fit) effect.

**Depth trend (F1 = one red then N greens, chase reversal down):** an **inverted-U**, peaking at 3–4 greens.

| F1 | trailing greens | n | WR | OOS train / test |
|---|---|---|---|---|
| `10` | 1 | 1075 | 32.6% | 33.9 / 30.8 — **FAILS** |
| `100` | 2 | 722 | 34.6% | 33.1 / 36.6 |
| **`1000`** | 3 | 470 | **37.2%** | 37.3 / 37.1 — **stable** |
| **`10000`** | 4 | 295 | **37.6%** | 38.7 / 36.2 |
| `100000` | 5 | 183 | 31.7% | 25.5 / 39.5 — noise |
| `1000000` | 6 | 125 | 28.8% | 28.9 / 28.6 — FAILS |

Interpretation: a **3–4 brick over-extension** reverts reliably; a 1-brick blip is just noise, and a 5–6 brick run is usually a real trend you should not fade.

**Best structural F2 filter — F1 = `10` / F2 = `010`:**
- Win rate **39.3%**, n = 163, ≈ +$580. OOS train 37.0% / test 42.3% — holds.
- **Tighter risk:** worst single-day loss streak **6** (vs 13 for `1000`).
- `10 / 0010` similar (41.7%, n=103, pooled loss streak 6).

### 5.2 LONG-book reversal (buy dips) — FAILS ✗
- **F1 = `1000` (LONG)** = 3-brick down-stretch, buy the bounce: **32.8%** (n=478), below breakeven, worst loss streak 21, ≈ −$140.
- All LONG `10/…` F2 combos below breakeven (F2=010 gave 31.5% but OOS 24.4/41.7 = noise).
- **This is a regime signature, not a strategy flaw.** In an up-trending tape, sharp rallies over-shoot and snap back (short works), while dips tend to keep going before they're bought (long fails). A genuine downtrend would likely flip which side pays.

### 5.3 Continuation / momentum (both books) — FAILS ✗
Every continuation pattern (F1 ending in `1`, chase 1) clustered **right at the 66.7% fair line**:

| combo | n | WR | vs 66.7% |
|---|---|---|---|
| `11` alone | 2082 | 65.0% | below (≈ fair, slight reversion tilt) |
| `011` alone | 722 | 65.4% | below |
| `0011` / `1` | 303 | 62.4% | below |
| `001` / `1` | 474 | 66.2% | below |
| `01` alone | 1075 | 67.4% | on the line (OOS straddles: 66.1 / 69.2) |

Conclusion: **there is no momentum edge** in these bricks. Continuation is a fair coin (65% ≈ 66.7% breakeven, a hair to the losing side). This also served as a **pipeline sanity check** — theory said "fair at 66.7%," data agreed to within a point.

### 5.4 Outcome-stream filters & "due for a win" logic — FAILS ✗
Filters that condition on your own recent **win/loss streak** do not work, because trade outcomes are ~**IID** (no serial dependence):
- `1000 / 1000` (trade after win-loss-loss-loss): **20%** WR (n=35), well below breakeven.
- `10 / 000` (trade after 3 losses): 33.9% — noise-level, barely at breakeven.
- F2 = `1` (after a win) on `11` continuation: made it worse.

**Rule of thumb learned:** *filter on brick structure, never on your own recent win/loss run.*

---

## 6. Position sizing & MCL (max consecutive losses)

### 6.1 Daily MCL profiles (SHORT book; the qty ratchet resets daily, so the **worst-day** streak is what matters)

| Pattern | pooled WR | daily MCL distribution | worst day | median |
|---|---|---|---|---|
| `1000` alone | 37.2% | 2,2,2,2,3,4,4,4,4,4,4,5,5,6,6,6,7,7,7,9,**13** | **13** | 4 |
| `10 / 010` | 39.3% | 1,1,1,1,1,1,2,3,3,3,3,3,3,4,4,5,5,5,6,6 | **6** | 3 |

→ `10/010` is the **ratchet-friendly** vehicle (tail capped at 6). `1000` gives more volume/profit but has a fat tail (one 13-loss day, Jul 8) that any escalating sizing must survive.

### 6.2 De-escalating ("opposite") qty rule — BACKFIRED ✗
Tested on `1000`: default qty 3, reduce on losses after a win (`100`→2, `1000`→1, `10000`→0/stop).

| sizing | total | worst day | circuit-breaker days |
|---|---|---|---|
| flat qty 3 | **+$3,300** | −150 | 0 |
| de-escalating rule | +$660 | **−240** | 15 of 21 |

It cut profit ~80% **and made the worst day worse** (it took the streak's early losses at full size, then stopped right before the day's recovery winners). Root cause again: **IID outcomes** — sizing off the recent win/loss tail cannot add EV and here removed it. **Flat sizing is optimal for EV; use a daily loss cap (in points/$) for drawdown control, not an outcome-streak rule.**

### 6.3 "Virtual F2" qty overlay (size up, don't filter) — WEAK, use with caution ⚠
Idea: trade all `1000` Layer-1 signals flat, but bump size on a higher-win-rate subset. Only one F2 lifted the base and held OOS: **`11*`** (two wins then a loss run) → 44.9% (n=69), but it was the lone survivor of a ~55-pattern sweep and not statistically significant.

Broken into fixed-length buckets (your qty engine needs these, not wildcards):

| tail | meaning | n | WR | note |
|---|---|---|---|---|
| `110` | 2 wins, 1 loss | 31 | 42% | above base |
| `1100` | 2 wins, 2 losses | 18 | 50% | above base (supports qty 3 > qty 2) |
| `11000` | 2 wins, 3 losses | 9 | **11%** | **CRATERS — do not size up here** |
| `110000` | 2 wins, 4 losses | 8 | 88% | n=8 noise, ignore |

**Verdict:** `110`→2, `1100`→3 is *directionally* supported on this sample, **but treat it as a "gambling" overlay, not the strategy.** Cap the escalation at `1100`; deeper buckets fall back to base size. Small samples (18–31 trades) → keep the bumps modest and forward-test.

---

## 7. Principles / rules of thumb (distilled)

1. **F1 must end in `0`** (chase a reversal) → 33.3% breakeven. Ending in `1` (continuation) → 66.7%, strictly worse.
2. **Filter on brick structure, not on your own win/loss streak** (outcomes are ~IID).
3. **The edge is SHORT-side reversal (fade rallies)** in this up-tape. Long (buy dips) and momentum both fail here; long may revive in a downtrend.
4. **Depth sweet spot = 3–4 bricks** (`1000`, `10000`). Shallower (`10`) and deeper (`100000`+) fail.
5. **Flat sizing is EV-optimal**; qty overlays are risk/variance choices at best, "gambling" at worst. Control drawdown with a **daily loss cap**, not outcome-based ratchets.
6. **The edge is thin (~3.7 pts over breakeven) and slippage-sensitive** — net edge after costs may be 1–2 points or less. Headline WRs are a ceiling.

---

## 8. Caveats & open items

1. **STRING CALIBRATION — the #1 gate (not yet done).** All numbers rest on the Python slicer's bricks, which have **not** been diffed against a live production `rawString`. The slicer's *travel* logic is verified correct (continuation 40 ticks, reversal 80 ticks; a seeding fix was applied and confirmed not to change window output), but the exact NT anchor/session/gap handling is unconfirmed. **Action:** capture one recorded **live** session `rawString` (live-closed bricks are ground truth) and run the slicer's `--expect` diff until a day matches bit-for-bit.
2. **Sample size.** 21 days is enough to *find* candidates and to reject non-edges, but thin for confirming small edges. The `1000` and `10/010` results are OOS-stable but their confidence intervals still include breakeven at the margins.
3. **Regime balance.** The sample is up-trending. Results are short-biased for that reason. Add days spanning a **down regime** (and re-apply UP/DOWN/CHOP weighting) to test whether the long side revives.
4. **Costs / slippage untested.** Only brick-close idealized outcomes are measured. The `EnableRealOrder=false` forward-test measures the *real fill* and is the true arbiter.
5. **Live vs historical bricks.** On a chart opened mid-session, bars **left of the open are historical reconstruction (can be wrong)**; only bars formed **after** you opened (once closed) are faithful. The still-forming rightmost brick is provisional until it closes.

---

## 9. Recommended path forward

1. **Calibrate** the slicer against one live `rawString` (`--expect`). Nothing below is trustworthy until this passes.
2. **Forward-test** `SHORT 1000` and `SHORT 10/010` at `EnableRealOrder=false` to measure real fills / slippage.
3. **Trade the confirmed edge flat**: `1000` (volume) and/or `10/010` (tighter MCL). Use a **daily loss cap** for drawdown, not an outcome ratchet.
4. **Optional gambling overlay:** `110`→2, `1100`→3, capped at `1100`, everything-else → base. Keep bumps small; watch whether `1100` keeps printing above base as real days accumulate.
5. **Collect more days**, especially a **down-regime** stretch, to firm the intervals and test the long side.

---

## 10. Tools produced (this research)

| File | Purpose |
|---|---|
| `BUILTIN_Renko_TickSlicer_StringBuilder.py` | Built-in-Renko tick-by-tick slicer: raw NT ticks → per-NY-day bit strings. Flags: `--brick-ticks`, `--invert` (SHORT book), `--start/--end` (NY window), `--src-tz` (default UTC), `--expect` (calibration diff). |
| `layer2_filter.py` | Layer 1 / Layer 2 filter with **auto-derived geometry** (33.3% reversal vs 66.7% continuation) from F1's last bit; per-day + pooled WR, MCL, P&L. |
| `mcl_sweep.py` | Sweeps F2 patterns (F1 fixed) ranked by daily-worst MCL, above breakeven. |

*(Earlier NinjaScript research builders — `Scalper_Renko_ResearchStringBuilder.cs` and `Scalper_UniRenko_ResearchStringBuilder.cs` — were superseded by the Python tick slicer, which is more faithful to live built-in-Renko formation.)*

---

## 11. Appendix — consolidated headline table

| Setup | Book | n | WR | Breakeven | Verdict |
|---|---|---|---|---|---|
| `1000` alone | SHORT | 470 | 37.2% | 33.3% | **EDGE, OOS-stable** |
| `10000` alone | SHORT | 295 | 37.6% | 33.3% | EDGE (confirms 1000) |
| `10 / 010` | SHORT | 163 | 39.3% | 33.3% | **EDGE, tight MCL (6)** |
| `10 / 0010` | SHORT | 103 | 41.7% | 33.3% | EDGE, fewer trades |
| `10` alone | SHORT | 1075 | 32.6% | 33.3% | fails (OOS 30.8%) |
| `1000` alone | LONG | 478 | 32.8% | 33.3% | fails (regime) |
| `11` alone (continuation) | LONG | 2082 | 65.0% | 66.7% | fails (no momentum) |
| `1000 / 1000` | SHORT | 35 | 20.0% | 33.3% | fails (outcome-streak) |
| de-escalating qty on `1000` | SHORT | — | — | — | backfires (−80% profit) |
| `110` / `1100` qty buckets | SHORT | 31 / 18 | 42% / 50% | 33.3% | weak overlay; cap at 1100 |

**Bottom line:** trade **SHORT-book `1000` and `10/010` flat** as the real (thin, slippage-sensitive) edge; treat `110→2 / 1100→3` as a capped gamble; forward-test for the true fill; and **calibrate the bricks against one live `rawString` before real size.**

*End of summary — 2026-07-25.*
