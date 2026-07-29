# MNQ Renko-Bar Strategy — Research Summary

**Instrument:** MNQ (Micro E-mini Nasdaq-100 futures)
**Bar type:** NinjaTrader **built-in** Renko
**Date of summary:** 2026-07-29  *(supersedes 2026-07-28 / 2026-07-25; new material in §13, updated headline + appendix + §9)*
**Research window:** 09:30–16:00 New York time (RTH). Core research = 21 clean days (Oct 2025 → Jul 2026, calibrated Python slicer). 07-28/07-29 addenda = 18 days re-run on an independent Renko reconstruction (13 held out-of-sample) + live-log slippage review + SHORT/LONG sizing study.

---

> **⚠ 2026-07-29 headline (read §13 for detail; §12 = 07-28 addendum):**
> - **Lead SHORT filter is now `F1=100 / F2=0100`.** Out-of-sample (13 unseen days) it beat both `10/010` and `10/0100`: **42.9% WR, MCL 5, +2.86 pt/trade** — ~3.4× the per-trade margin of `10/0100`, which is what survives costs. `10/0100` is retained as a corroborating cross-check (the `0100` family holds under two entry depths).
> - **LONG is a real *second* book — via `100/00100` (42.9% OOS, MCL 3).** This refines the old "LONG fails": LONG *momentum* patterns fail, but the LONG `0100`-*selectivity* filter clears breakeven OOS. **Avoid LONG `10/0100`** — high WR but MCL **12** and +0.30 loss-clustering (a drawdown trap).
> - **Sizing guideline (§13.3): NO outcome-based qty ratchet on either book.** Fired outcomes are ~IID, so a ratchet adds zero EV and only worsens drawdown (proven: same ratchet *helped* one book and *hurt* the other on identical days = pure variance). Use **flat size within each book, base sizes ≈ 3:1 (SHORT-edge : weaker book) from Kelly, a small fraction of Kelly (¼ or less, because the 95% edge bound spans zero), and a daily $ loss cap** for drawdown control.
> - **★ Best improvement found — a brick-native trend gate (§13.5).** Suppressing SHORT `100/0100` fires when the last 10 bricks are strongly up (`net10 ≥ 4`) survived a *clean* train/test split (threshold picked on 5 training days only, tested on 13 untouched): WR 42.9% → **48.8%**, MCL 5 → **3**, per-trade +2.86 → **+4.65 pt**, and — a first for this research — the **95% lower bound (34.6%) clears the 33.3% breakeven.** Theory-driven, threshold-insensitive, helps only the trend-sensitive `100` book. Strongest signal we have.
> - Still true from 07-28: `1010` rejected (overfit, 54%→21% OOS); slippage distorts geometry only under fast-spike+lagged-fill; **`10/010` did not hold on the reconstruction** (calibration-gated vs the slicer's 39.3%).
> - **#1 open gate unchanged:** calibrate one live `rawString` bit-for-bit (two reconstructions already disagree), then re-run **net-of-costs** — the thin per-trade margin makes commission + adverse SHORT slippage decisive.

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
- Win rate **39.3%**, n = 163, ≈ +$580. OOS train 37.0% / test 42.3% — holds *(on the calibrated slicer)*.
- **Tighter risk:** worst single-day loss streak **6** (vs 13 for `1000`).
- `10 / 0010` similar (41.7%, n=103, pooled loss streak 6).

> **⚠ Revisited 2026-07-28 (§12.1):** on an *independent* reconstruction `10/010` fell to 30.6% (below breakeven) and **`10/0100` overtook it** (36.1% OOS, MCL 5). The slicer-vs-reconstruction disagreement is unresolved and calibration-gated — `0100` is now the lead candidate, but neither absolute number is final until §8.1 calibration is done.

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

1. **STRING CALIBRATION — the #1 gate (still not done, now doubly urgent).** All numbers rest on reconstructed bricks, not a live production `rawString`. The slicer's *travel* logic is verified (continuation 40 ticks, reversal 80 ticks; seeding fix confirmed), but NT anchor/session/gap handling is unconfirmed. **New 07-28 evidence this matters:** the 07-28 addendum used a *fresh, independent* close-based reconstruction (2× reversal, B=10) and got materially different absolute win rates for the same pattern (`10/010` = 30.6% vs the calibrated slicer's 39.3%). Two independent reconstructions disagreeing on absolute level is exactly the symptom calibration would resolve. **Relative** rankings (e.g. `0100` > `010`) appear robust across both; **absolute** win rates are not trustworthy until calibrated. **Action:** capture one recorded **live** session `rawString` and diff (`--expect`) until a day matches bit-for-bit — and reconcile the two reconstructions against it.
2. **Sample size.** 21 days is enough to *find* candidates and to reject non-edges, but thin for confirming small edges. The `1000` and `10/010` results are OOS-stable but their confidence intervals still include breakeven at the margins.
3. **Regime balance.** The sample is up-trending. Results are short-biased for that reason. Add days spanning a **down regime** (and re-apply UP/DOWN/CHOP weighting) to test whether the long side revives.
4. **Costs / slippage untested.** Only brick-close idealized outcomes are measured. The `EnableRealOrder=false` forward-test measures the *real fill* and is the true arbiter.
5. **Live vs historical bricks.** On a chart opened mid-session, bars **left of the open are historical reconstruction (can be wrong)**; only bars formed **after** you opened (once closed) are faithful. The still-forming rightmost brick is provisional until it closes.

---

## 9. Recommended path forward

> *Superseded in part by §12.7 (2026-07-28): the lead SHORT filter is now `10/0100`, `10/010` is retired pending calibration, and the decisive open test is net-of-costs. Read §12 alongside this list.*

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
| `1000` alone | SHORT | 470 | 37.2% | 33.3% | **EDGE, OOS-stable** (calibrated slicer) |
| `10000` alone | SHORT | 295 | 37.6% | 33.3% | EDGE (confirms 1000) |
| **`10 / 0100`** | SHORT | 150 (18d) | **38.0% / 36.1% OOS** | 33.3% | **NEW lead candidate (07-28), OOS-survived, MCL 5** |
| `10 / 00100` | SHORT | 101 (18d) | 37.6% / 35.7% OOS | 33.3% | EDGE, confirms 0100 family |
| `10 / 010` | SHORT | 163 (slicer) / 219 (recon) | 39.3% *slicer* / **30.6% recon** | 33.3% | **CONFLICTED — slicer says edge, recon says below-BE; calibration-gated** |
| `10 / 0010` | SHORT | 103 | 41.7% | 33.3% | EDGE (slicer sample) |
| `10 / 1010` | SHORT | 71 (18d) | 54%→**21% OOS** | 33.3% | **REJECTED — overfit mirage (07-28)** |
| `10` alone | SHORT | 1075 | 32.6% | 33.3% | fails (OOS 30.8%) |
| `1000` alone | LONG | 478 | 32.8% | 33.3% | fails (regime) |
| `11` alone (continuation) | LONG | 2082 | 65.0% | 66.7% | fails (no momentum) |
| `1000 / 1000` | SHORT | 35 | 20.0% | 33.3% | fails (outcome-streak) |
| de-escalating qty on `1000` | SHORT | — | — | — | backfires (−80% profit) |
| skip-losses-then-size-up qty | SHORT | — | — | — | **impossible as coded (07-28), IID-doomed** |
| `110` / `1100` qty buckets | SHORT | 31 / 18 | 42% / 50% | 33.3% | weak overlay; cap at 1100 |

**Bottom line (updated 07-29):** the SHORT-book reversal edge is real but **thin and cost-sensitive**. **Lead = SHORT `100/0100` + the `net10 ≤ 3` trend gate** (§13.5: 48.8% OOS, MCL 3, +4.65 pt/trade, first CI-lo to clear breakeven on a clean split), with **`10/0100`** as corroboration and **`1000` (calibrated slicer)** as the original. **LONG is a viable second book via `100/00100`** (42.9% OOS, MCL 3) — but **not** LONG `10/0100` (MCL 12, loss-clustering). **Retire `10/010`**; **never use `1010`**. **No qty ratchet** on either book (IID); size flat, fractional-Kelly, ~3:1 by edge, daily $ cap (§13.3). **Net-of-costs (of the gated book) is the decisive open test**, alongside the unresolved **live-`rawString` calibration**.

---

## 12. Update — 2026-07-28 session (SHORT-filter OOS test + live-log slippage review)

**What was done today:** (a) rebuilt 40-tick Renko (B = 10 pt) tick-by-tick from an *independent* close-based reconstructor (2× reversal) over 18 usable days (2 upload files came in empty), and re-ran the Layer-2 pipeline to hunt a filter that cuts max-consecutive-losses (MCL); (b) reviewed same-day live NinjaScript logs (Layer-1 Transit, SHORT and LONG) to evaluate real-fill slippage against the brick-close-anchored bracket. All P&L in §12 is **flat qty = 1** (win +20 pt, loss −10 pt); **no qty rule applied.**

### 12.1 New SHORT filter — `F1=10 / F2=0100` ✓ (OOS-survived)

Motivation: the previous workhorse `10/010` carries an MCL of 6–7, which stresses any breaker. Tightening F2 selects a smaller, higher-quality subset and shortens the streak. Sweep on 5 days flagged `0100` (42.9%) and `1010` (54.2%) as candidates; both were then tested on **13 unseen days** (candidates were picked without them):

| F2 (F1=`10`) | fires (18d) | WR all | WR **OOS** | BE 33.3 | MCL | P&L (18d / OOS) |
|---|---|---|---|---|---|---|
| **`0100`** | 150 | 38.0% | **36.1%** | ✓ over | **5** | +210 / **+90** |
| `00100` | 101 | 37.6% | 35.7% | ✓ over | 5 | +130 / +50 |
| `010` (old) | 219 | 30.6% | 28.8% | ✗ under | 7 | −180 / −210 |
| `1010` | 71 | 32.4% | **21.3%** | ✗ under | 5 | −20 / −170 |
| `01000` | 90 | 31.1% | 29.9% | ✗ under | 5 | −60 / −70 |

- **`0100` is the new lead SHORT candidate:** it regressed from the inflated 42.9% pick-set figure to **36.1% out-of-sample** but stayed *above breakeven and profitable on days it had never seen*, and cut MCL 7 → 5 vs `010`. Neighbour `00100` confirms the family.
- **`1010` rejected** — 54% → 21% OOS. Pure sweep luck (best of ~12 patterns on 5 days). Documented as the canonical overfit lesson: *sweep winners are hypotheses, not results, until OOS-confirmed.*
- **`010` did not survive the reconstruction** (below breakeven). Note this *conflicts* with the calibrated slicer's 39.3% (§5.1) — see §12.6 / §8.1; treat as calibration-gated, not a settled regression.
- **MCL correction:** the 5-day teaser showed `0100` capping at 3; on 18 days it is **5** (top day-streaks 5,5,4). Set the breaker to **6**, not 4.
- **Fire rate:** `0100` ≈ **8.3 fires/day** but highly variable (range **3–24**; quiet days ~3, choppy days 15–24). Any within-day qty ladder would behave very differently across that spread.

### 12.2 Clustering question resolved — losses are ~IID; **selectivity**, not timing, is the lever
Raw bricks are positively autocorrelated (lag-1 ≈ **+0.30**, i.e. they trend), but the **fired-trade outcomes are ~IID** (lag-1 ≈ −0.01 for `10` alone, +0.11 for `0100`). So consecutive losses do **not** cluster in a dodgeable way — you cannot shorten MCL by skipping or by "due-for-a-win" logic. What *does* shorten it is a more selective filter (higher WR + fewer fires both compress the worst run). This re-confirms §5.4 and directly kills the skip-idea in §12.4.

### 12.3 Live-fill slippage review — geometry distortion is **conditional**, and it interacts with the qty rule
The bracket is anchored to the **brick close** (correct for bit-integrity), but a **market** entry fills seconds later at a possibly different price, so slippage lands entirely on one side of the bracket.

- **Pathological case (1 SHORT trade, fast spike):** a 3-brick up-spike printed in 0.15 s; the short market order filled **2.3 s later, 13.75 pt below the brick close**. Result: "win" that captured only **6.25 pt while risking 23.75 pt** — the intended 1:2 geometry inverted to ~1:4, and the `bit=1` win label hid it.
- **Ordinary case (8 LONG trades, 4 files):** every fill within **<2 pt** of the brick close, sub-0.5 s, geometry intact — **including a fill at the 09:30 open.** So "busy hour" alone does **not** cause the damage; the pathology needs *fast spike + lagged fill*.
- **Directional tilt:** SHORT fills lean **adverse** (shorting into a falling tape sells below the brick close; worst observed −3.25 pt), while LONG fills in the same tape are favorable/neutral. Slippage bias depends on trade-vs-move alignment.
- **Fix & its condition:** switching to **limit entry at the brick close** (`UseMarketEntry=false`) restores the geometry, at the cost of *missing* trades that gap past the limit (mostly runaways you didn't want on a reversion short). **BUT** limit entry is only safe with **no qty rule** — a missed fill deletes a character from `sessionRealOutcome` and re-indexes every multiplier after it. With `EnableQtyIncrement=false`, a miss is local and cheap, so limit entry becomes the better default (esp. on the adverse-tilting SHORT book). Left dial: `LimitOffsetPoints` (0 = cleanest but fills least; 1–2 ticks fills more while still capping slippage).

### 12.4 Qty-rule mechanics — "skip early losses, then size up" is impossible as coded ✗
The qty ladder reads `sessionRealOutcome`, which **only advances on real executed fills**. A `qty:0` skip takes the `OBS_QTY_SKIP` path → no order → nothing appended. So after the first real loss the string **freezes at `"0"`** and never reaches `"0000"`/`"00000"` — the escalation lines are **dead code**, and the net behaviour is "take exactly one flat trade per day, then skip forever on a loss." To even attempt the idea, the ladder would have to key on the pipeline's *would-be/brick* outcomes, not `sessionRealOutcome` (a code change). And it is IID-doomed anyway (§12.2), sizing up deepest exactly where the depth-trend edge is weakest (§5.1). **Not pursued.**
*Confirmed separately:* the trade-outcome exit with pattern `1` halts the session immediately after the first winning trade (e.g. first `100`→`1` win → exit), as designed.

### 12.5 Cost sensitivity — the decisive open test
`0100` gross expectancy is **≈ +1.4 pt/trade (18d) / +0.83 pt/trade OOS** (~$1.66/trade at $2/pt, ~$14–23/day at 1 contract). That thin margin means costs are not a footnote: MNQ commission (~$1+/round-turn) at ~8 fires/day is ~$8–10/day, plus the measured adverse SHORT slippage. **Net-of-cost P&L is now the gating test** — subtract commission + ~1 pt adverse fill/trade and confirm the OOS +0.83 pt/trade is still positive before any real size.

### 12.6 Method caveats specific to this addendum
- **Independent reconstruction, not the calibrated slicer.** Today's bricks come from a fresh close-based 2×-reversal builder (B=10), *not* `BUILTIN_Renko_TickSlicer_StringBuilder.py`. Absolute win rates differ from §5 (e.g. `10/010`: 30.6% here vs 39.3% there). **Relative** rankings (`0100` > `010`, `1010` overfit) are the robust output; **absolute** levels await calibration (§8.1).
- 18 days (2 files empty); 13 of them out-of-sample vs the 5-day pick set. Wilson 95% lower bound on `0100` still dips *below* breakeven (30.6% all / 27.7% OOS) — profitable on sample, not yet statistically bulletproof.

### 12.7 Revised near-term actions
1. **Calibrate** one live `rawString` bit-for-bit and reconcile *both* reconstructions against it (now the clear #1 gate — two reconstructions already disagree).
2. **Net-of-cost re-run** of `0100` (commission + measured adverse slippage) — the real go/no-go.
3. Forward-test `SHORT 10/0100` at `EnableRealOrder=false`; set breaker MCL = **6**; trade **flat, no qty ratchet**.
4. If adopting **limit entry**, keep `EnableQtyIncrement=false` and tune `LimitOffsetPoints`.
5. Add more days (esp. a down regime) to lift the `0100` confidence interval clear of breakeven.

---

## 13. Update — 2026-07-29 session (deeper F1, LONG book, sizing guideline)

Extends §12 on the same 18-day independent reconstruction (13 days out-of-sample vs the 5-day pick set). All P&L flat qty = 1 (win +20, loss −10 pt) unless stated.

### 13.1 Lead SHORT filter promoted: `F1=100 / F2=0100`

Testing a deeper transit (F1 = `100` = chase the reversal after **2** trailing greens) with the same F2 family:

| SHORT filter | fires OOS | WR OOS | CI-lo | MCL | P&L OOS | **pt/trade OOS** |
|---|---|---|---|---|---|---|
| **`100 / 0100`** | 63 | **42.9%** | 31.4 | 5 | **+180** | **+2.86** |
| `100 / 00100` | 43 | 41.9% | 28.4 | 4 | +110 | +2.56 |
| `10 / 0100` (07-28 lead) | 108 | 36.1% | 27.7 | 5 | +90 | +0.83 |
| `100` alone (L1) | 688 | 35.0% | 31.6 | 10 | +350 | +0.51 |

- **`100/0100` is the new lead SHORT candidate.** Higher WR than `10/0100` and, decisively, **~3.4× the per-trade margin (+2.86 vs +0.83 pt/trade)** — the number that determines cost-survival. Same MCL (5), ~5 fires/day.
- **Anti-overfit signal:** the `0100` family clears breakeven OOS under *both* F1 depths (`10` and `100`), and neighbour `00100` corroborates on both. A pattern surviving OOS under two entry filters is far harder to dismiss as luck than a single sweep cell (contrast `1010`).
- `100` alone is a high-volume, razor-thin play (MCL 10, +0.51 pt/trade) — fragile to costs; not recommended as a standalone.

### 13.2 LONG book — a viable *second* book, with a sharp exception

Same pipeline on LONG-encoded bricks (up-brick = win, chase reversal *up*). OOS:

| LONG filter | fires OOS | WR OOS | MCL | P&L OOS | autocorr | note |
|---|---|---|---|---|---|---|
| **`100 / 00100`** | 42 | **42.9%** | **3** | +120 | −0.30 | **best LONG — tame, anti-clustering** |
| `100 / 0100` | 67 | 37.3% | 4 | +80 | +0.08 | positive, IID-ish |
| `10 / 0100` | 96 | 40.6% | **12** | +210 | **+0.30** | **AVOID — losses cluster, drawdown trap** |
| `100` alone | 670 | 32.8% | 11 | −100 | — | fails |

- **This refines "LONG fails."** The old LONG rejections were *momentum/continuation* patterns (LONG `1000`, `11`) — still dead. But the LONG `0100`-**selectivity** filter clears breakeven OOS. So: LONG momentum ✗, LONG `0100`-selection ✓.
- **Best LONG = `100/00100`** (42.9% OOS, MCL 3) — near-mirror of SHORT `100/0100` in quality, reassuring that the edge is two-sided, not an artifact.
- **Explicitly avoid LONG `10/0100`:** its fat P&L hides an **MCL of 12** and **+0.30 loss-clustering** — unlike SHORT (IID), LONG `10/0100` losses beget losses, so the streak is structural, not luck.
- Caveats: LONG samples thin (42–96 fires), autocorr signs noisy at that size, `100`-variant CI-los still dip below breakeven.

### 13.3 Position-sizing guideline (both books)

Grounded in the re-confirmed fact that **fired outcomes are ~IID** (SHORT autocorr ~0; LONG mostly ~0, the one exception being LONG `10/0100`).

1. **No outcome-based qty ratchet — proven, not asserted.** A loss-ratchet (00→2,000→3,…) run on identical OOS days **helped `10/0100` (+300 vs +90) but hurt `100/0100` (+120 vs +180)** — same rule, opposite sign = pure variance, exactly what IID predicts. Its *only* consistent effect was **worse drawdown** (maxDD −140/−120 vs −80/−80). So `EnableQtyIncrement = false` on both books. (The earlier de-escalation and "skip-then-size-up" ideas fail for the same reason — §5.4, §12.4.)
2. **Size = a fixed fraction of bankroll (Kelly), never a function of recent outcomes.** With 2:1 payoff, f* = (3p−1)/2. OOS point estimates: SHORT `100/0100` f* ≈ **0.14**, `10/0100` ≈ 0.04. So **base sizes should sit in ≈ 3:1 ratio by edge** (the stronger book carries ~3× the size of the weaker one). Run SHORT `100/0100` as the anchor; weaker books scale down from it.
3. **Use a small fraction of Kelly (¼ or less).** At the 95% *lower* bound, both books' Kelly goes **negative** — the edge estimate can't yet exclude zero. Fractional Kelly sacrifices almost no growth for a large cut in ruin risk. Contracts ≈ (fraction × bankroll) ÷ $20-risk-per-contract (stop = 10 pt × $2); round **down** while the edge is unconfirmed.
4. **Drawdown control = a daily $ (or point) loss cap, not a ratchet.** Size so the MCL streak (−$100/contract at MCL 5, flat) and worst day (−40 to −60 pt at q1) stay inside tolerance. This is the §6.2 conclusion, reconfirmed.
5. **Running SHORT + LONG together:** they are opposite bets on the *same* instrument — correlated, not diversifying, and they draw down in opposite regimes. On one account your `AccountBusyOnThisInstrument` guard **serializes** them (one blocks the other while in a position), so effective exposure is naturally one-at-a-time — decide this deliberately rather than discover it live. Size the *pair* as one risk unit.
6. **LONG exception to the flat rule:** LONG `10/0100`'s +0.30 clustering is the one spot where "IID ⇒ flat is optimal" weakens — but the correct response is to **pick the low-MCL variant (`100/00100`)**, not to add a ratchet.

### 13.4 Revised near-term actions  *(superseded by §13.6)*
1. **Calibrate** one live `rawString` bit-for-bit; reconcile both reconstructions against it (still #1).
2. **Net-of-cost re-run** of SHORT `100/0100` (and LONG `100/00100`) — the go/no-go, given the thin margins.
3. Forward-test SHORT `100/0100` (lead) + LONG `100/00100` (second book) at `EnableRealOrder=false`; breaker MCL **6** SHORT / **4** LONG; **flat size, ratchet OFF**, base ≈ 3:1, fractional-Kelly, daily cap.
4. **Never** run LONG `10/0100` (clustering) or `1010` (overfit).
5. Add more days (esp. down-regime) to lift both books' CI clear of breakeven.

### 13.5 ★ Brick-native trend gate — confirmed out-of-sample (the strongest result)

**Rationale.** The strategy is a mean-reversion *fade*; it structurally loses when a trend runs it over. A regime filter native to the bricks (no new feed, smallest overfit surface) should suppress fades during strong trends.

**The gate — `net10`, in plain terms.** Of the **last 10 closed bricks**, count up-bricks minus down-bricks = `net10` (+10 = ripping up, 0 = choppy, −10 = falling). **Rule: take the SHORT `100/0100` entry only if `net10 ≤ 3`** — i.e. skip the trade when **7+ of the last 10 bricks were up** (don't fade a strong rally).

**First, the signal is real (win% by trend bucket, holds both in- and out-of-sample):** SHORT fade wins best when recent tape is flat/mildly-down (flat 61–63%, down 50–67%) and worst in a strong uptrend (`net10 ≥ 4`: **26.9% / 30.0% OOS**, below breakeven). Monotone and theory-consistent.

**Clean train/test (threshold chosen on 5 training days only, then locked and applied to the 13 untouched days):**

| SHORT `100/0100`, 13 untouched days | fires | WR | **CI-lo** | MCL | P&L | pt/trade |
|---|---|---|---|---|---|---|
| ungated | 63 | 42.9% | 31.4 | 5 | +180 | +2.86 |
| **gate `net10 ≤ 3`** | 43 | **48.8%** | **34.6** | **3** | +200 | **+4.65** |

- **First candidate in the whole study whose 95% lower bound (34.6%) clears the 33.3% breakeven** under a strict split.
- The training days were actually a *bad* sample (gated `100/0100` was still negative on them), yet the locked threshold *improved* the untouched test set — the opposite of overfit behaviour.
- **Helps `100/0100` but NOT `10/0100`** (shallow fade unaffected) — coherent, since a deeper 2-green pullback is more trend-vulnerable. A filter that helps exactly where theory predicts is the trustworthy kind.
- Likely a twofer: strong uptrends are also where shorting-into-a-rip produced the worst adverse fills (§12.3), so the gate should cut slippage damage too.
- **Caveats:** 43 test fires (CI-lo clears BE but not widely); uncalibrated reconstruction; one gate. Direction is pre-specified from theory and the cutoff is insensitive (`≤2` ≈ `≤3`), which is what makes it credible.

**Implementation sketch (NinjaScript, ~6 lines).** Track the last-10 brick directions and gate the fire in `OnBarUpdate`, right where `nextIsMoney` is consumed:
```csharp
// field:  private readonly System.Collections.Generic.Queue<int> last10 = new();
// after computing this brick's bit (0=up,1=down), BEFORE using nextIsMoney:
last10.Enqueue(bit == 0 ? 1 : -1);
while (last10.Count > 10) last10.Dequeue();
int net10 = 0; foreach (var d in last10) net10 += d;
// gate (SHORT book): only allow the fade if not in a strong up-run
bool trendOK = (last10.Count < 10) || (net10 <= TrendGateMax);   // TrendGateMax default 3
if (nextIsMoney && EnableRealOrder && !hasOpenPosition && trendOK) { ... TryOpenRealTrade(); }
```
Expose `TrendGateMax` (default **3**) as a parameter; log `net10` per fire so it can be re-verified. For the LONG book the mirror is `net10 ≥ −3` (skip fades in strong *down*-runs) — untested, add only after LONG is validated.

### 13.6 Revised near-term actions (supersedes §13.4 / §12.7)
1. **Calibrate** one live `rawString` bit-for-bit; reconcile both reconstructions (still #1).
2. **Net-of-cost re-run** of SHORT `100/0100` **+ the `net10 ≤ 3` gate** — the go/no-go.
3. Forward-test **SHORT `100/0100` with the trend gate** (lead) + LONG `100/00100` (second book) at `EnableRealOrder=false`; breaker MCL **6** SHORT / **4** LONG; **flat size, ratchet OFF**, base ≈ 3:1, fractional-Kelly, daily cap.
4. **Never** run LONG `10/0100` (clustering) or `1010` (overfit).
5. Add more days (esp. down-regime) to widen the gated CI clear of breakeven, and re-confirm the gate on the LONG book.

*End of summary — updated 2026-07-29 (07-28 + 07-25 addenda retained above; core research 2026-07-25).*
