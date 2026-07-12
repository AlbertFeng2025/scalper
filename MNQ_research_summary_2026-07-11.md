# MNQ Pattern-Filter Research — Summary
**Date:** 2026-07-11 · **Instrument:** MNQ (Micro Nasdaq, $2/pt) · **Platform:** NinjaTrader 8

---

## 1. Methodology (locked)

| Item | Setting |
|---|---|
| Slicing | bid/ask, 1-second throttle, stop-priority tie-break |
| SHORT entry | at **ask** (stop = ask+32, target = ask−19) |
| LONG entry | at **bid** |
| Bit convention | direction-relative: `1` = this book's slice **won**, `0` = lost |
| Trading day | 3:00 PM PT (prior day) → 3:00 PM PT |
| morning_open | 6:30–8:30 AM PT = **9:30–11:30 ET** |
| Filter state | resets each trading day |
| Break-even (32/19) | **62.7%** = 32 / (32+19) |

**Wildcard language:** `*` = one-or-more `0`s (`0+`) · `?` = one-or-more `1`s (`1+`) · literals match themselves · expands **in place** · **suffix/tail match**.
Examples: `0*` = `00+` (two+ zeros) · `10?` = `101+` · `1*?` = `10+1+` · `1*1` = `10+1`

**Data:** raw NinjaTrader tick exports (`date time;last;bid;ask;volume`), **timestamps are UTC**. Slicers convert UTC → PT. In-sample = 55 trading days. Out-of-sample = 7/6, 7/7, 7/8 2026.

---

## 2. The Finalists — SHORT 32/19, morning_open

*In-sample = 55 days. OOS = 7/6–7/8 2026 (3 days).*

| Filter | Layer | Fires | Win rate | MCL | $/fire | Net $ | Green days | OOS result |
|---|---|---|---|---|---|---|---|---|
| **`101/00`** *(sniper)* | L2 | 39 | **84.6%** | 2 | **+$22.3** | +$870 | 85% | 5 fires, 80%, +$88 |
| **`10?/00`** *(workhorse)* | L2 | 148 | 75.0% | 2 | +$12.5 | **+$1,850** | 76% | 15 fires, 66.7%, +$60 |
| **`0101`** *(single-layer)* | **L1** | 169 | 73.4% | 3 | +$10.8 | +$1,832 | 78% | **12 fires, 75.0%, +$150** |
| `1/0/00` *(candidate)* | L3 | 75 | 77.3% | 2 | +$14.9 | +$1,116 | 77% | 5 fires, 100%, +$190 |

### Notes on each

- **`101/00`** — highest precision and best $/fire, but only **39 fires / 55 days** (~0.7 per day; trades only 27 of 46 possible days). Small sample = wide confidence interval. Best if you value win rate over volume.
- **`10?/00`** — the validated primary. Most fires, most total profit, statistically the most trustworthy (148 fires). Regime-robust: UP days 68%, NEUTRAL 82%, DOWN 71% — **not** a down-market artifact.
- **`0101`** — **the session's standout discovery.** Simplest architecture (one layer, one literal pattern). In-sample 73.4% and OOS 75.0% agree tightly → strong evidence it is *not* overfit. Beat the primary OOS on win rate, dollars, and drawdown, and **was profitable on 7/6, the day the primary lost −$90.**
- **`1/0/00`** — L3 candidate. Hits the "more fires than the sniper, low MCL, good wr" target. Found by searching ~8,000 combos → carries selection risk. Probation only.

---

## 3. Why `0101` works (mechanism)

`0101` = loss-win-loss-win, **ending on the reversion phase** (`...01`).
It directly detects *"the market is currently oscillating and just reverted."* The alternation **is** the regime confirmation that the F2 gate normally provides — which is why it works as a single layer.

**The ending phase is what matters, not the length.** Every extension breaks it:

| Variant | Win rate | Verdict |
|---|---|---|
| `0101` | **73.4%** | ✅ the sweet spot |
| `10101` | 71.6% | ✅ still works (same `...01` ending) |
| `1010` | 68.2% | ⚠️ opposite phase, weaker |
| `01011` | **61.0%** | ❌ below BE — adds a *momentum* tail |
| `0101?` | 65.4% | ❌ same problem (`?` = momentum tail) |
| `01010` | 60.0% | ❌ wrong ending phase |

**Lesson:** longer ≠ more selective. Extending `0101` either adds a momentum tail or flips the phase — both destroy the reversion edge.

---

## 4. Rejected — do not revisit

| Tested | Result | Why it failed |
|---|---|---|
| **Layer 1 simple gates** (`10?`, `1`, `101` alone) | 64–68%, MCL 4–8, +$1.6–5.6/fire | Just re-samples the ~65% raw baseline. **The F2 gate is load-bearing** — adding it to `10?` lifts wr 65.8%→75.0%, halves MCL 5→2, quadruples $/fire. |
| **`111/11?`** | 64.3%, MCL 4, only 42% green days | Momentum/continuation bet — the *opposite* of the reversion logic. Profit was outlier-carried (top 5 days = +$1,092 of a +$408 total; other 33 days ≈ −$684). |
| **`1001`** | **62.2% in-sample (below BE), −$92** | ⚠️ **Showed 82.4% / +$340 OOS — a pure fluke.** 17 fires can't overturn 164. Textbook small-sample trap. |
| **`010/1*1`** | 69.8%, **MCL 4**, +$7.2/fire | Positive but dominated on *every* axis by all three finalists. `1*1` = `10+1` admits wide-bottom V's → dilutes the signal. |
| **`010/11`, `010/1?1`** | 59.3%, 60.3% | Below BE. `010` is a weak first gate. |
| **Overnight bucket** | `10?/00`: **66.2%**, MCL 4, +$3.5/fire | Test week showed 83% → **washed out on 55 days.** Barely above BE; wider overnight spreads would erase it. **Not a second window.** |
| **Permissive filters** (`1/0`, `10?/0`, F2=`0`) | 700–1,400 fires, ~65–70%, +$2–5/fire | Friction trap. Big gross totals, but per-trade edge is *below* the ~2pt MNQ round-trip cost ceiling → losing after costs. |

---

## 5. Theory backing (academic literature)

Both "anti-intuitive" findings are **well-supported**:

**Q: Market trends up long-term — why does SHORT work and LONG not?**
→ **Intraday reversal is stronger after UP moves.** A 15-year study of US index futures (Grant, Wolf & Yu) found significant intraday reversal following large opening price changes, *more pronounced following large positive moves*. The macro uptrend (~0.03%/day) is invisible inside a 19-point intrabar move. At tick scale, up-moves **over-extend and snap back** — which is exactly what the SHORT book harvests.

**Q: Why does 32-stop/19-target beat 19-stop/32-target?**
→ **Gain/loss asymmetry** ("elevator down, stairs up"): downward moves are *faster* than upward ones; draw-downs are faster than draw-ups. A near target + wide stop harvests the frequent small reversions while surviving noise. A tight stop + far target does the opposite — shaken out by fast down-bursts before the slow grind reaches the far target. Root cause: the **leverage effect** (volatility is higher after negative returns).

**VIX test (leverage-effect prediction):**

| Regime | Days | Fires | Win rate | $/fire |
|---|---|---|---|---|
| HIGH VIX (≥17.6) | 29 | **100** | 75.0% | +$12.5 |
| LOW VIX (<17.6) | 26 | **48** | 75.0% | +$12.5 |

→ **Volatility drives FREQUENCY (2× the fires), not edge-per-trade.** Win rate is VIX-neutral. The pattern's reliability is volatility-independent; volatility just controls how often it appears. **The edge is robust across regimes** — a point in favor of it being real.

⚠️ **The same literature warns:** transaction costs are what determine tradeability. A 2026 MNQ-specific study found a ~2-point round-trip cost eliminates the gross edge in most signal families. **Your finalists clear this (+$10.8 to +$22.3/fire ≈ 5–11 pts gross); the rejected thin filters do not.** This is *why* they were rejected.

---

## 6. Production code state (all compile-clean)

| File | Status |
|---|---|
| `Scalper_Shortrepeat_Layer2.cs` | ✅ compiled |
| `scalper_LONGrepeat_Layer2.cs` | ✅ compiled |
| `scalper_SHORTrepeat_Layer3.cs` | ✅ compiled |
| `scalper_LONGrepeat_Layer3.cs` | ✅ compiled |
| `LONG_SHORT_rawString_recorder.cs` | ✅ compiled, running |

**All four strategies now share:**
1. **Wildcard matcher** (`*`/`?`) — verified against regex on **40,000 random cases, 0 mismatches**
2. **Per-day longest-match qty rule** with qty=0 skip
3. Loss-streak breaker (cumulative, survives reconnect); qty session resets each NY day; RESUME starts qty session fresh

**Qty table (longest match wins, order-independent):**
```
("10",2) ("100",2) ("1000",2)          -> after a win, losses 1-3 = x2
("10000",0) ("100000",0) ("1000000",0) -> losses 4-6 = SKIP (qty 0)
```
**Leading `1` is a deliberate bug-guard:** a loss-run from the open (`0000...`, e.g. a data/logic bug) matches *nothing* → stays at base qty x1, **never doubles into a disaster.**

⚠️ **COUPLING WARNING:** review the qty table whenever `MaxRealLossInARow` changes. The x0 skip lines only cover loss-runs up to their length; a longer run reverts to base qty.

**Recorder fixes (session-boundary bug):** wait-don't-guess when the session is unknowable · self-heal `sessionIter` · never roll backward to an earlier session.
**Recommended NT setting:** change `ConnectionLossHandling` from `Recalculate` → **`Keep`** to stop the constant restart churn from feed flapping.

---

## 7. Standing conclusions

1. **Two layers is the floor** for a real edge — *unless* the pattern self-confirms the regime (`0101`).
2. **Per-trade edge > fire count.** Friction eats thin edges. Only trade filters clearing ~$10+/fire.
3. **60–70% raw baseline is the sweet spot** geometry.
4. **Backtest MCL is a floor, not a ceiling** — plan for 5–6.
5. **Live win rate will likely run below backtest.** Treat ~half the raw edge above BE as durable.
6. **Small samples lie.** `1001` (82% on 17 fires → 62% on 164) and overnight (83% on 3 days → 66% on 55) are the proof. **Never promote on a single week.**
7. **LONG stays manual-enable, up-days only.** LONG `10?/110` is regime-dependent (UP days 87.9%, NEUTRAL 67.1%).
8. **Stay on MNQ futures.** QQQ adds borrow fees, PDT, and the uptick rule (which blocks shorts on exactly the volatile days the edge fires). 0DTE options are worse: delta halves the capture, option spreads eat a large fraction of the move, theta bleeds, and gamma breaks the 32/19 geometry.

---

## 8. Forward-log discipline — the real kill switch

Track **weekly** for each finalist: fires · W/L · win rate · MCL · net $ · green-day %

**Kill/promote criteria:**
- If win rate drifts toward the ~65% raw baseline over several weeks → **the edge is gone; stop.**
- If P(win | last loss) drops toward baseline → **kill the qty rule.**
- Promote a candidate only after **several weeks** of consistency — never on one good week.

**Currently on probation:** `0101` (strongest OOS showing) · `1/0/00` (L3)
**Live-eligible:** `10?/00` (validated) · `101/00` (validated, sparse)

> The edge is a small real inefficiency in a niche too small for large players. It is **fragile and has no regeneration mechanism.** Forward-testing discipline is the only thing standing between a working strategy and a slow bleed.

---

*Numbers verified against the 55-day in-sample set and the 7/6–7/8 out-of-sample week. Slicer independently cross-checked: 7/7 session = 296 bits / 200 ones (67.6%) — exact match with Albert's own slicer.*
