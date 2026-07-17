# MNQ Pattern-Filter Research — Summary
**Date:** 2026-07-11 · **Updated:** 2026-07-15 (live trading + geometry/signal robustness) · **Instrument:** MNQ (Micro Nasdaq, $2/pt) · **Platform:** NinjaTrader 8

> **2026-07-15 update in brief:** Went live in sim on both books (first real fills — clean).
> Ran six new research directions (SMA, candle triggers, MACD cross, engulfing, tighter/wider
> geometries, geometry neighbors). **All six washed out or confirmed the existing edge.**
> The core finding is unchanged and now much better stress-tested: **SHORT ~32/19 with a
> reversion filter (`10?/00` / `0101`) is the edge; almost everything else is noise.**
> New sections **9–12** at the bottom cover the live results and the washouts.

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
9. **The geometry is a robust PLATEAU, not an overfit spike** (2026-07-15). Raw SHORT win rate rises smoothly with stop width: 30/19 → 63.0%, 32/19 → 64.6%, 34/19 → 65.7%. No cliff, no isolated peak. The edge lives in a *neighborhood* (~30–34 stop / 19 target), so don't over-index on the exact `32/19` point.
10. **Only reversion works; every momentum/indicator/candle trigger fails** (2026-07-15). Six directions tested and rejected — see §11. The consistent tell: single price/candle features (SMA level, candle body, MACD cross, engulfing) carry no edge; only the *sequence structure* of continuous slice outcomes does. Event-triggered strings are sparse and un-filterable.

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

---

# ═══════════════════════════════════════════════════
# 2026-07-15 SESSION ADDENDUM
# ═══════════════════════════════════════════════════

## 9. First live sim trading (2026-07-14)

Both books ran live in sim for the first time (SHORT on simS, LONG on simL — **separate accounts**, so the multi-strategy account guard is inactive between them and each book gets a full uncontaminated fire count). Market: MNQ gapped **+263 pts** overnight, then **sold off −121 pts** in the first 35 min (gap-and-fade).

| Book | Trades | Result | Fills |
|---|---|---|---|
| **SHORT** | 3 (09:31 / 09:42 / 09:50 ET) | **3W / 0L, +$114.50** | **Clean** — 2 of 3 better than signal, 1 worse by 0.5 pt |
| **LONG** | 1 (10:10 ET) | 0W / 1L, −$64.00 | Filled **1.75 pt worse** than signal |
| **Net** | 4 | **+$50.50** | — |

**The single most important live finding: fills are clean (SHORT).** This was the biggest unknown — the 2026 MNQ study warns a ~2-pt round-trip cost kills most edges. SHORT's fills came in neutral-to-favorable, so the ~6-pt gross edge survives so far. **Tiny sample (3 fills) — keep watching slippage every session.**

**Textbook regime confirmation:** on a down-morning, SHORT harvested the selloff (3/3) while LONG correctly sat out most of it (armed but F1 never matched) and lost its one trade. Exactly as the research predicts — SHORT robust, LONG regime-dependent. The LONG trade ate 56% of SHORT's profit → **keep LONG `EnableRealOrder=false`** on a shared account.

> ⚠️ **3/0 is NOT evidence.** At a true 75% win rate you get 3/0 ~42% of the time by chance. Do not raise size or confidence. Watch the win rate over *dozens* of trades and whether it drifts toward the ~65% baseline.

## 10. Live lifecycle machinery — all proven under real conditions

Every lifecycle path was exercised live and **passed**:

| Event | Result |
|---|---|
| **Hard NT crash** (app hung, force-closed, immediate re-enable) | RESUME correct; rawString continuous +1 per slice 1→254, zero gaps; chose RESUME not FRESH (3-min gap < 7-min tolerance); breaker restored. ✅ |
| **Manual disable/enable** (deliberate mid-session) | Identical to auto-restart path (NT makes a new instance either way). rawString preserved. ✅ |
| **Trading-day rollover (3 PM PT)** | SHORT rawString 284→1, `[BREAKER RESET]` 1→0; LONG 272→1. Both grow +1 cleanly after. Boundary slice attributed to the day it entered. ✅ |
| **Overnight warm-up** | 158+ observation slices recorded overnight (didn't exist before the hours-gate fix). ✅ |

**Two known code gaps remain (not yet fixed):**
1. **Qty rule wiped on every reconnect** (`sessionRealOutcome.Clear()` on RESUME). Since the feed reconnects often, the `{'0':2}` sizing edge may rarely engage live. Conservative (can only under-size) so safe, but the researched sizing benefit is largely lost. Fix available: rebuild today's real-outcome string from the log on RESUME (same method as the breaker fix).
2. **Logged P&L is nominal** (`ProfitTargetPoints × qty`), not actual fill prices → hides slippage. Fix: compute from `(entry − exit) × PointValue`.

**One open risk to test before a real account:** crash recovery with a **real position open mid-trade**. Proven *between* trades, never *during* one. Uncertain whether `OnExecutionUpdate` fires on the new instance when a bracket fills post-crash. Test deliberately in sim.

**Code changes shipped this session:** multi-strategy account guard (same-instrument demotion to observation) · trading-hours as an *order* gate only (slicing still runs 24h for warm-up) · trading-day pipeline reset at 3 PM PT · daily circuit-breaker reset (`CountTodaysTrailingLosses`, day-aware) · `WOULDBE_TRADE` / `OBS_*` log labels · concurrency-safe `SafeAppend` file writer · consistent `DateTime.Now` timestamps. See `Strategy_Lifecycle_Guide.md`.

## 11. Six new research directions — ALL rejected

The session's dominant result: **everything except the continuous-stream reversion edge washed out.** Each is instructive.

| # | Direction | Result | Why it failed |
|---|---|---|---|
| 1 | **SMA (30-period, 1-min)** as a filter | Raw slices n=3,382: above SMA 64.1% vs below 64.9% (**p=0.62**). No effect. | Price *level* vs a moving average carries no edge at this timescale. A filtered subset hinted at +12pp but p=0.095 after ~20 looks = multiple-testing noise. |
| 2 | **Candle-qualified slicing** (slice only after a large green Marubozu, ≥1.5 ATR) | **SHORT 62.3%, LONG 61.9%** (223 fires each) — both *below* the ~65% continuous baseline, both at BE. | SHORT ≈ LONG ⇒ **no directional edge**; the ~62% is pure geometry. An early "76%" was a **firing bug** (arm/in-slice interaction suppressed ~99% of fires) — caught by Albert asking "why only 40 fires?". Corrected → effect vanished. Candle *body/shape* carries no edge. |
| 3 | **MACD signal-line cross** → fake trade → filter | Raw string **51%** (coin flip). SHORT 54%, LONG 48% at symmetric 20/20. Filters gave 1–7 fires of noise. | MACD is a *momentum* trigger; momentum carries no edge here. Sparse string (~5–10/day) → no sequence structure for a filter to concentrate. |
| 4 | **Engulfing (reversed: short green-engulf, long red-engulf)** → filter | Raw ~51% at symmetric geometry. Mild consistent SHORT>LONG (+5–9pp = fade-the-up-move confirmed) but too weak to trade. **Filters curve-fit: `11/1?` was +8pp in-sample → −23.5pp OOS (inverted).** | Winning filters were all *momentum* (`11`, `1?`) — the red flag. Momentum filters on a streaky event-string harvest in-sample autocorrelation and collapse OOS. Fires were mostly overnight (untradeable). |
| 5 | **Tighter geometry — SHORT 20/16** | Raw morning 57.5% (BE=**55.6%**, only +1.9pp). Best filters held OOS but thin (~+3pp). `10/00`: +0.75 pt/trade edge — **below the ~2-pt cost.** MCL 5–8 (worse than 32/19). | Break-even (55.6%) leaves too little margin. Smaller per-loss ($40 vs $64) but *longer* losing streaks and an edge friction likely eats. **Note:** BE = STOP/(STOP+TARGET); an early miscalc as 44.4% inverted the risk picture — corrected to 55.6% (Albert caught it). |
| 6 | **Wider geometry — SHORT & LONG 60/40** | SHORT raw 62.5% IS / **59.5% OOS (below BE=60%)**. LONG raw ~60% (at BE). Sweep winners 73–78% IS → 2–6 OOS fires (overfit). No filter held OOS with an adequate sample. | Break-even is a high 60%; raw sits *at or below* it. Slow-resolving slices fire too sparsely → filters sub-sample to unevaluable single-digit OOS counts. LONG 60/40 is the *least-bad* LONG config (Albert's "wide suits the slow up-direction" intuition was directionally right) but still only reaches break-even. |

**The geometry-neighbor test (30/19 vs 32/19 vs 34/19).** Directly addresses "is 32/19 overfit?" Answer: **no — it's a smooth plateau.** Raw wr 63.0 / 64.6 / 65.7% rises monotonically with stop width. *However,* the `10?/00` filter's **OOS** numbers are sample-fragile (12–26 fires, scattered 55–100%); on the clean 50-file split, 30/19 was actually the most *consistent* filter (65% IS / 69% OOS, largest OOS sample). **Takeaway:** trust the neighborhood, not any single geometry's small-sample OOS number. The live log will settle 30 vs 32 vs 34.

**Unifying lesson of §11:** every rejected idea is an *event trigger* or a *single price feature*. They produce sparse or structureless strings. The edge is not in any price level, candle shape, or indicator — it is in the **sequence structure of the continuous slice-outcome stream**, which only the reversion filters (`10?/00`, `0101`, `101/00`) exploit. Six failed alternatives is strong positive evidence the core edge is *real and unusual* — robust things survive stress-testing; artifacts don't.

## 12. Where things stand (2026-07-15)

- **The edge:** SHORT ~32/19 (plateau 30–34 stop / 19 target), morning window, reversion filter `10?/00` (primary) or `0101` (single-layer). Live-eligible and now stress-tested against six alternatives.
- **Live track record:** 6 sim trades total (SHORT 3/3, LONG 0/1). **Far too few to conclude anything.**
- **The research well for *new signals* is largely dry.** Extensive search this session returned no new tradeable edge. Highest-value work is now **forward-testing what exists**, not hunting a seventh idea.
- **Next steps, in priority order:** (1) accumulate live results for manual weekly review; (2) fix the two known code gaps (qty-persistence, fill-based P&L) when convenient; (3) test crash-recovery with a real open position before going to a real account.
- **Do NOT** chase the sweep winners from §11 (they're overfit), raise size on a good week, or enable LONG real orders on a shared account.

---

*2026-07-15 numbers verified against a 50-file in-sample set and the 7/5–7/9 OOS week, morning bucket, with correct break-even = STOP/(STOP+TARGET) and Wilson confidence intervals throughout. Fire counts sanity-checked against qualified-event counts to avoid the firing-bug class of error. Live results read directly from the strategy CSVs for 2026-07-14.*
