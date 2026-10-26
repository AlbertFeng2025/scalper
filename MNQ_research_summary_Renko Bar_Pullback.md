# MNQ Research Summary — Renko Bar Trend-Pullback Strategy
**Date:** 2026-10-26 · **Instrument:** MNQ (Micro Nasdaq, $2/pt) · **Platform:** NinjaTrader 8 · **Code:** `Renko_TrendPullback_LONG.cs` / `Renko_TrendPullback_SHORT.cs` v1.11

> **In brief:** The Renko trend-pullback signal looked profitable in-sample (Jul–Oct 2026) but
> **failed out-of-sample** (Apr–May 2026). Every variant tested — brick sizes 30–200 ticks, EMA
> settings, F2/F3 filters, LONG / SHORT / merged books, dynamic brick size, ES futures — either lost
> or could not be told apart from luck. **The signal is not reliable and should not be traded.**
> The NinjaTrader code (order safety, rescue, reconnect handling) is solid but its **v1.11 fixes
> still need a fast-market Sim test** before the code is reused. **Testing is paused here.**

---

## 1. Strategy design

| Item | Setting |
|---|---|
| Bars | Built-in Renko, 80-tick (20 pt) main case; reversal brick = 2 × brick size |
| SHORT setup (F1=`110`) | last three bricks red, red, green (1 = red/down in the SHORT book) |
| LONG setup (F1=`10`) | last two bricks green, red (1 = green/up in the LONG book) |
| Trend gate | brick-close EMA 9 < 20 < 30 (SHORT) / 9 > 20 > 30 (LONG); blank = gate off |
| Entry | market at the setup brick close (LONG option: limit, cancelled at next brick close) |
| Exits | stop 1 brick, target 2–3 bricks, server-side bracket |
| Layers | Layer 1 = every setup · Layer 2 = F2 on the virtual-outcome string (e.g. `0` = trade after a virtual loss) · Layer 3 = F3 |
| Virtual tracker | every setup scored from price as 1 (target, needs 1 tick through), 0 (stop) or N (limit never filled) |
| Sizing | 1 contract in research; optional qty rule in code (research: it amplifies losses) |

---

## 2. In-sample result (MNQ, Jul 6 – Oct 15 2026, 12 weeks)

| Version (SHORT, 80-tick) | Trades | Win rate | pt/trade | Weeks 1–6 | Weeks 7–12 |
|---|---|---|---|---|---|
| Pattern only, no EMA | 938 | 26.0% | −1.00 | −0.96 | −1.11 |
| Layer 1: pattern + EMA | 514 | 30.5% | +2.70 | +2.35 | +3.53 |
| Layer 2: + skip after a win (F2=`0`) | 305 | 32.5% | +4.30 | +4.06 | +4.98 |
| Layer 2, 2-brick target | 276 | 42.4% | +3.73 | +2.94 | +6.08 |

Break-even with 1:3 payoff ≈ 26%. These numbers formed the basis of the Oct 22 write-up.

---

## 3. Robustness checks — where it started to break

### 3.1 Brick-grid starting price
Same rules, same 20-pt brick, only the starting price of the brick grid shifted:

| Grid shift (pt) | 0 | 2.5 | 5 | 7.5 | 10 | 12.5 | 15 | 17.5 | **Avg** |
|---|---|---|---|---|---|---|---|---|---|
| Layer 1 pt/trade | +0.89 | +3.27 | +2.54 | −0.29 | +2.05 | +2.35 | −0.37 | +0.28 | **+1.34** |
| F2=`0` pt/trade | +0.23 | +3.57 | +3.33 | −0.15 | +1.19 | +2.89 | +1.35 | +0.59 | **+1.63** |

The +2.70 came from a favorable grid. NinjaTrader's grid start depends on how much history the
chart loads, so live results are a random draw from this table. Weeks 7–12 were negative for most
grid starts.

### 3.2 Brick size (SHORT F1=`110`, EMA, target 3, averaged over 4 grid starts)

| Ticks | 30 | 40 | 60 | 80 | 100 | 120 | 160 | 200 |
|---|---|---|---|---|---|---|---|---|
| Layer 1 pt/trade | −1.80 | −1.23 | +0.48 | +1.28 | −0.45 | +0.41 | +3.67 | +10.30* |

\* 200-tick: only ~67 trades in 12 weeks; ±8 pt of chance variation per trade.
The 60–80 tick hump came almost entirely from weeks 1–6.

### 3.3 Other variants tested (all in-sample, Jul–Oct)
- **EMA settings** (9/20/30, 3/20/30, 5/20/30, 3/9/20, 3/10/30, 9/20/50): the fast EMA barely
  matters — the 20 < 30 part does the work. None turned 40-tick positive.
- **F1=`10` vs `110`:** `10` gives more trades but weaker, ≈0 in weeks 7–12.
- **F2 / F3 outcome filters:** move the win rate by < 1 point in almost every case — trade
  outcomes are close to independent.
- **Dynamic brick size** (each day sized to make N bricks on the previous day, N = 50–180):
  results swing from +5 to −2 with no stable range.
- **Reversal catcher** (`0001`, stop 1–2 bricks, target 5): LONG loses on ES and MNQ; one SHORT
  cell (+1.6) within chance.
- **LONG and merged books:** LONG alone unreliable; merged 200-tick looked best (+4.6 to +10.2)
  but on 126–258 trades.
- **ES futures** (9 days, 8–20 ticks; ≈14 ticks matches MNQ 80-tick): SHORT about breakeven,
  LONG negative; too little data.

**Simulator lesson:** checking stops only at brick closes is correct when the setup brick runs
against the trade (`10`, `110`), but **wrong** when it runs with the trade (`01`) — the stop sits
inside the next brick. F1=`01` looked like +1.5–2 pt until re-tested tick by tick; then it lost.

---

## 4. Out-of-sample test (MNQ, Apr 6 – May 29 2026, 32 trading days)

Rules frozen, averaged over 4 grid starts. Market: MNQ ≈ 24,250 → 30,400 (+25% rally).

| Candidate | Jul–Oct | **Apr–May trades** | **Apr–May win** | **Apr–May pt/trade** |
|---|---|---|---|---|
| SHORT 80, `110`, EMA 9/20/30, L1 | +1.28 | 166 | 19.0% | **−6.53** |
| SHORT 80, `110`, EMA 9/20/30, F2=`0` | +1.53 | 110 | 20.6% | **−5.29** |
| SHORT 200, `110`, no EMA | +10.57 | 72 | 26.7% | −2.80 |
| SHORT 200, `110`, EMA 9/20/50 | +13.89 | 23 | 26.9% | +2.14 |
| LONG 200, `110`, EMA 9/20/50 | +6.66 | 47 | 29.4% | −0.65 |
| Merged 200, `110`, no EMA | +4.55 | 143 | 27.5% | −1.79 |
| Merged 200, `110`, EMA 9/20/50 | +10.20 | 70 | 28.7% | +0.46 |

40-tick on both periods: every book (LONG / SHORT / merged), F1 `10` and `110`, with or without
EMA, target 2 or 3 → **−0.8 to −1.8 pt/trade**. Daily win rates scatter around the coin-flip level.

---

## 5. Conclusions

1. **The Renko trend-pullback signal has no reliable edge.** The in-sample profit was the market
   regime of Jul–Aug plus selection from >100 tested versions.
2. **40-tick and smaller bricks behave close to random;** costs make every version lose.
3. **Outcome-string filters (F2/F3) add almost nothing** — Renko outcomes are close to independent.
4. **The qty rule increases losses** on a coin-flip-like stream (e.g. Oct 23 Sim: −$25.5k flat
   → −$46k with ×3).
5. **Discretionary use** is possible as personal judgment, but it should be measured honestly
   against the coin-flip win rate, not assumed.

**Research rules learned (apply to any new idea):**
- Average every Renko test over several brick-grid starting prices.
- Use tick-level stop/target checks.
- Decide rules on one period, then test them **frozen** on another before believing them.
- Distrust any result with < ~300 trades or that depends on the best of many settings.

---

## 6. Code status — `Renko_TrendPullback_LONG/SHORT.cs` v1.11

**Built and Sim-tested (v1.3 → v1.10, 10- and 20-tick, up to 900 contracts):**
naked-position guard · fast-market rescue (closes only unprotected contracts, hands the rest back
to their bracket) · stop-past-market and entry-past-stop handling · one close at a time · sizing
from the strategy's own fill count · start-up stale-brick guard · connection-loss logging ·
restore of today's real results on restart · margin-cutoff flatten · real P&L from average exit.

**v1.11 additions — NOT yet tested in a fast market:**
1. Stop resize rejected while the target is partly filled → close the rest (v1.10).
2. Wrong-direction position caught at the fill, not at the next brick (v1.10).
3. Position sizing trusts NinjaTrader's Position when it agrees with the own fill count; if all
   three sources disagree on direction, send nothing.
4. Late entry fills (after the entry is cancelled) count as entry, not as an exit loss.
5. Naked-position guard runs every second, and closes only the contracts with no live stop.
6. Mixed trades (part target, part stop) score 1/0 by real P&L sign.

The only v1.11 run was a slow Friday afternoon on 20-tick (Oct 23): clean, but none of the new
paths were exercised.

### ⚠️ Future test needed before reusing the code
- Run v1.11 in Sim on **10-tick through a busy open (6:30 PT) or a data release**.
- Check the logs for: rescues, `NAKED GUARD`, late entry fills (`LATE`), `disagree` lines,
  mixed-trade scoring, and that every trade ends flat with no leftover orders.
- Compare with NinjaTrader's Orders and Executions export for any over-fill.
- Confirm the "On connection loss" platform setting.

---

## 7. Status

**Testing paused 2026-10-26.** The signal is not to be traded. The code is kept as reusable safety
infrastructure for future strategies, pending the v1.11 fast-market test above. Active research
moves to the fixed-slice SHORT reversion book (see `MNQ_research_summary_2026-07-15 (1).md`,
§13), which is the only candidate that has passed an out-of-sample test so far.
