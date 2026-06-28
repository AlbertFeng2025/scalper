# MNQ Scalper — Filter Combo Research Summary

*Pure-math analysis of the 1/0 rawString filtration. All numbers are
P(1) = "target percentage" pooled across the full archive (22 tagged days,
April–June 2026, ~12k bits per book), validated out-of-sample.*

---

## 1. The core convention (locked)

- The bit is **direction-relative**: in the **LONG book** `1` = price up;
  in the **SHORT book** `1` = price down. In *both* books `1` = "this book's
  trade won." We always look for **higher P(1)**, on either book.
- Analysis is **pure math**: report P(1) + fire count. No win/lose/up/down
  language during analysis. Book name only tells which order a real trade
  places (LONG→buy, SHORT→sell) — a reminder, not part of the math.
- Each book analyzed self-contained. "Universal" = works across many strings
  of the **same book** out-of-sample (except `011/1*11`, which works on both).

---

## 2. The validated results table

Format per book: **P(1)% / fire count / max-0-run / out-of-sample / Up-day% Down-day%**

| Combo | Type | SHORT book | LONG book |
|---|---|---|---|
| **`011`/`1*11`** | **UNIVERSAL** | 59% / 111 / mx3 / **PASS** / U73 D60 | 58% / 102 / mx4 / **PASS** / U73 D46 |
| `11`/`1` | both-book | 58% / 1228 / mx8 / **PASS** / U37 D74 | 65% / 1341 / mx7 / **PASS** / U81 D69 |
| `11`/`11` | SHORT-lean | 62% / 708 / mx11 / PASS / U33 D82 | 70% / 872 / mx11 / fail / U89 D73 |
| `11`/`111` | SHORT | 69% / 440 / mx6 / PASS / U27 D87 | 78% / 609 / mx8 / fail / U92 D81 |
| `11`/`11`/`11` | SHORT deep | 78% / 305 / mx4 / PASS / U25 D92 | 86% / 475 / mx5 / fail / U97 D88 |
| `11`/`1*1*1` | LONG wild | 54% / 222 / mx6 / fail / U42 D65 | 61% / 185 / mx5 / **PASS** / U66 D71 |
| `0101`/`11` | LONG old | 54% / 95 / mx3 / PASS / U50 D61 | 59% / 110 / mx3 / fail / U44 D57 |
| `11111` (deep run) | momentum | 69% / 440 / mx6 / PASS / U27 D87 | 78% / 609 / mx8 / fail / U92 D81 |

*(Baseline P(1) is ~50% SHORT, ~51.5% LONG. "PASS" = pooled, first-half,
second-half AND REC-holdout all above baseline, ≥30 fires.)*

---

## 3. Recommended defaults

**If you want ONE universal combo for both books (you can't know direction ahead):**
> ### `011` / `1*11`  (Layer 2)
> The only combo that lifts **both** books out-of-sample AND survives the
> up-day / down-day regime split (SHORT 73/60, LONG 73/46). Tightest losing
> streaks of any validated combo (max-0-run 3–4). Fires ~1 per 60–70 bits
> (few trades — matches the goal). **This is the safest single choice.**

**If you set each book separately (per-book best, accepting regime risk):**
> - **SHORT Layer 3:** `11`/`11`/`11` — 78%, mx4, passes all slices. BUT it is
>   a **down-day specialist** (Down 92% / Up 25%). Lethal on the right day,
>   loses on the wrong one. Use only with the breaker as hard protection.
> - **LONG Layer 2:** `11`/`1` — 65%, huge sample (1341), passes, regime-balanced
>   (Up 81 / Down 69). Or `11`/`1*1*1` (61%, REC +16, tighter risk mx5).

---

## 4. What we learned (the durable findings)

**The signal is momentum continuation — "after 1s, expect 1."**
- First gates that are runs of 1s (`11`, `111`, `1`) lift **both** books.
- First gates starting with `0` (`01`, `010`, `0111…`) are flat or negative,
  especially on LONG. They condition against the momentum.
- P(1) **rises** with run depth: after 1 one ≈55%, after 5 ones ≈78% (LONG).
  Deep runs are rare (~4% of runs ≥6 long) and high-probability — but the edge
  is **regime-conditional** (it breaks exactly at the trend turn).

**The two books are different past the first gate.**
- SHORT has rich structure: many patterns lift it (wildcards, deep runs).
- LONG is resistant: only clean `11`-run patterns work; gap/wildcard patterns
  (`1*1*1*1`, `1*11*11`, `01*1`) fail on LONG, work on SHORT.

**Wildcards (`*` = one-or-more 0s):**
- A **single** wildcard ≈ a fixed pattern after the first occurrence
  (`1*1`≈`01`, differ only when the 0-run reaches the string start). Use the
  fixed form for clarity.
- **Two+ wildcards** (`1*1*1`) do real work a fixed pattern cannot
  (independent variable gaps). This is where the wildcard earns its place.
- A **trailing** `*` (e.g. `*111*`) causes repeated matching as the run grows
  — over-samples one region. Avoid for trade filters.

**Big losing streaks are normal, not a contradiction.**
- At P(1)=65%, P(0)=35%; over ~1300 trades the *expected* longest 0-run ≈6–7.
  The same autocorrelation that lifts P(1) also clusters the 0s when the
  regime turns. You cannot pattern them away — they are the companion of the
  edge. The `MaxRealLossInARow=3` breaker is the structural counterweight.

**Regime frequency (22-day archive, your "not my day" question):**
- ~32% up, ~18% down, ~18% chop, ~14% strong-trend, ~18% no-data.
- A "not my day" that actually triggers losses (mostly chop + wrong-way
  strong-trend) is ~15–25% of days. On those the breaker caps at 3 and the
  strategy stands by. Wrong-direction days mostly go *quiet* (pattern rarely
  fires on a 0-heavy string) rather than losing big.

**F3 (third layer) on `011`/`1*11`:** adds nothing reliable. The outcome
string is already saturated and daily outcome strings are too short for F3 to
fire meaningfully. Sweep-selected F3s (`001`, `01`) looked great in-sample but
**failed out-of-sample**. Stop at 2 layers for `011`/`1*11`.

---

## 5. Honest caveats

- All numbers rest on **22 days** (~2 weeks of June daily files do most of the
  work; April–May days barely fire the validated combos). Expect live
  regression — treat the percentages as best estimates, not guarantees.
- The high SHORT numbers (`11`/`11`/`11` = 78%) are **regime-inflated** by a
  down-heavy archive. On up days those combos *fail*. `011`/`1*11`'s lower but
  regime-robust ~58% is the more trustworthy real-world expectation.
- More recent daily data is the single most valuable thing to collect. Re-run
  the per-day tables as data accumulates.

---

## 6. Tools built (in outputs folder)

- `A_layer2_wildcard_tester.py` — 2-layer pipeline, wildcard `*`, step-by-step
  raw→layer1→trade. Default `011`/`1*11`.
- `A_layer3_wildcard_tester.py` — 3-layer version, wildcard in any layer.
- `A_window_threshold_pattern_analyzer.py`, `A_prewindow_subwindow_density.py`,
  `A_prewindow_subwindow_waterlevel.py`, `A_zone_signal_pregate.py` — the
  window/density/water-level/zone experiments (all concluded: window-density
  ≈ baseline; the exact pattern always beats it).

---

## 7. Open directions (materially different ideas to consider)

1. **Dynamic position sizing by run-depth.** Since P(1) climbs from ~56%
   (after 1) to ~78% (after 5), size *up* deeper in a confirmed run rather than
   changing the entry filter. The deep-run trades are rare and high-probability.
2. **Regime detector as a switch**, not a filter. Use a simple, separate
   measure (e.g. recent window 1-density, or realized range) to *enable* the
   down-day-specialist combos only when the day looks like their regime — and
   fall back to `011`/`1*11` otherwise.
3. **Record real fill prices** (already partly built) to test whether the
   2pt/trade cost assumption holds, and whether slippage erodes the thinner
   edges.
4. **Two-book "whoever fires" live test.** Run `011`/`1*11` on both books
   simultaneously in sim; whichever book's string produces the signal trades
   its direction. Confirms the universal claim live.
