# MNQ SHORT Book Research Summary: Renko vs. Fixed Slicer

**Prepared:** October 3, 2026
**Instrument:** MNQ (Micro Nasdaq-100), contracts 09-26 and 12-26
**Data:** NinjaTrader tick exports, July 6 – September 25, 2026 (48–50 trading days)
**Status:** Research results, not yet confirmed on new data

---

## 1. Data and Method

### 1.1 Data coverage

The research used ten weekly tick files covering July 6 through September 25, 2026. Two weeks are missing: August 17–21 and September 8–11. Two days were excluded because the tick data stops mid-session: August 28 (data ends 10:54 NY) and September 1 (data ends 11:31 NY and resumes 03:00 NY on September 2). August 7 is missing its last 33 minutes but was kept. September 14–25 is MNQ 12-26; earlier data is 09-26. The contract switch does not affect results, because both methods measure price moves from each entry.

### 1.2 Costs and breakeven

All net figures use about **1.3 pt per round trip** (measured entry slippage plus commission). The simulations already include the bid-ask spread.

| Setup | Win | Loss | Breakeven before costs | Breakeven after costs |
|---|---|---|---|---|
| Fixed slicer 32 stop / 19 target | +19 | −32 | 62.7% | 65.3% |
| Fixed slicer 19 stop / 32 target | +32 | −19 | 37.3% | 39.8% |
| Renko 40-tick transit (reversal) | +20 | −10 | 33.3% | 37.7% |
| Renko 40-tick continuation | +10 | −20 | 66.7% | 71.0% |
| Renko 80-tick transit | +40 | −20 | 33.3% | 35.5% |

### 1.3 How the two methods map to each other

In the **fixed slicer**, every slice is a SHORT bracket trade entered at the ask, so its raw string is already a string of trade outcomes. In **Renko**, the brick colors are not outcomes until F1=10 turns them into transit trades. So the equivalent Renko pipeline needs one extra layer:

| Fixed slicer | Renko |
|---|---|
| raw string (slice outcomes) | F1=10 → filter1Outcome (transit outcomes) |
| F1=`10?` | F2=`10?` |
| F2=`00` | F3=`00` |

Pattern notation: `?` means one or more 1s, so `10?` matches tails ending in 101, 1011, 10111, and so on.

### 1.4 Replication of the live strategy

The fixed-slicer simulation reproduces `Scalper_Shortrepeat_Layer2` v4: entry at the ask, stop and target resolved on bid/ask, 1-second throttle, 24-hour slicing, pipeline reset at 18:00 NY (3:00 PM PT), no slicing during the 16:30–17:50 NY margin-cutoff block, and a fresh start after any data gap longer than 10 minutes.

### 1.5 Evaluation standard

Every result is compared with the **base rate** (the win rate of all unfiltered trades in the same window), not just with breakeven. "Chance it's luck" is the binomial probability of seeing that win rate by chance if the true rate were the base rate. Losing streaks are compared with what independent trades at the same win rate would produce.

---

## 2. What Did Not Work (Renko and Pattern Research)

These negative results narrow down where the edge can be.

### 2.1 Renko outcome strings behave like coin flips

Across many tested patterns on 40-tick bricks, F1 transit win rates land at about **34%**, the rate a random walk produces given Renko's two-brick reversal rule:

| F1 pattern | MERGED | SHORT | LONG |
|---|---|---|---|
| 10 | 34.3% | 34.0% | 34.6% |
| 100 | 34.1% | 35.1% | 33.1% |
| 1000 | 35.0% | 35.5% | 34.6% |
| 1110 | 34.0% | 34.0% | 34.0% |
| 111110 | 35.5% | 35.1% | 35.8% |

Continuation patterns (F1=01, F1=0011) land at about **66%**, the mirror image, which is still below their 71% breakeven.

### 2.2 Layered string filters do not raise the win rate

A large grid of F2/F3/F4 combinations (including 1, 01, 10, 011, 0111, 1101, 100–100000, and OR combinations) cut trade counts sharply while win rates stayed at the base rate. Losing streaks matched coin-flip expectations almost exactly. For example, MERGED F1=1000 produced 26 streaks of 8+ losses, where independent trades predict about 27.

### 2.3 No day-level regimes

Daily win rates of F1=10, 100, and 1000 were uncorrelated (r ≈ −0.06 to −0.09), varied no more than chance allows, and the morning did not predict the afternoon.

### 2.4 External conditions did not predict the next brick

Fourteen conditions were tested with a first-half / second-half split: VWAP distance, move since the open, day high/low distance, prior close, opening range, 30-minute trend, efficiency ratio (chop), volatility, run length, order-flow delta (brick and 5-minute), brick volume, brick duration, and time of day. None moved the continuation rate outside about 63–67%. Combined models scored an AUC of 0.50–0.52 on the held-out half, no better than chance.

### 2.5 Other dead ends

- **Overnight trading (18:00–09:30):** no edge in any method or bracket.
- **Weekly size changes:** doubling after winning or losing weeks adds risk without adding edge. Doubling after losses (martingale) lost about 4× more than flat sizing when the simulated edge was zero.
- **"01 or 10" OR filters:** effectively random selection. On the fixed slicer 19/32, they produced a 19-loss streak in August.

### 2.6 One recurring signal

Wins and losses bunch together **within** the day (trend stretches and chop stretches). Streaks of 21–28 continuation wins in a row appeared repeatedly, beyond what independent trades produce. String filters cannot see which stretch is underway.

---

## 3. What Worked: The `10?` → `00` Pipeline

### 3.1 Fixed slicer, SHORT book, morning window (09:30–11:30 NY)

| Bracket | Rule | Trades (per day) | Win rate | Base | Chance it's luck | Max losses in a row | Net after costs | September (new data) |
|---|---|---|---|---|---|---|---|---|
| 32/19 | F1=`10?`, F2=00 | 183 (3.7) | 70.5% | 63.8% | 1 in 15 | 3 | +485 pt | 19 / 74% |
| 32/19 | F1=101, F2=00 | 58 (1.2) | 69.0% | 63.8% | — | 2 | +109 pt | 4 / 75% |
| 19/32 | F1=`10?`, F2=00 | 168 (3.4) | 44.0% | 38.1% | 1 in 8 | 10 | +364 pt | 23 / 39% |
| 19/32 | F1=1000, F2=00 | 114 (2.3) | 43.9% | 38.1% | 1 in 5 | 6 | +236 pt | 18 / 44% |
| 19/32 | **Union of `10?`/00 and `1000`/00** | **282 (5.6)** | **44.0%** | 38.1% | **1 in 20** | 9 | **+599 pt** | 41 / 41% |

Notes:

- The filter lifts the win rate by about **6 points with both brackets**. The two brackets produce different bit strings, yet both point the same way: **after the signal, price tends to fall.** This happened while MNQ rose overall from about 29,800 to about 30,900.
- `10?` with F2=00 was the live rule **before** this data was collected, and September was collected afterward, so both serve as a fair test rather than a tuned result.
- **Union versus OR:** running `10?`/00 and `1000`/00 as two separate pipelines and trading whenever either fires keeps the 44% win rate. Putting both patterns into one F1 string dilutes it to 41.6%. The two pipelines never chose the same slice.
- F2=00 is the common ingredient: it was the winning second layer with three different F1 patterns (101, `10?`, 1000) and both brackets.

### 3.2 Fixed slicer by trading window (48 days)

| Bracket | Window | Rule | Trades (per day) | Win rate | Base | Chance it's luck | Max losses in a row | Net after costs | September |
|---|---|---|---|---|---|---|---|---|---|
| 32/19 | Morning | `10?` | 182 (3.8) | 70.3% | 64.0% | 1 in 13 | 3 | +467 pt | 19 / 74% |
| 32/19 | **Regular hours 09:30–16:00** | **`10?`** | **273 (5.7)** | **70.7%** | 63.6% | **1 in 60** | 5 | **+752 pt** | **29 / 83%** |
| 32/19 | Overnight | `10?` | 194 (4.0) | 63.4% | 63.9% | — | 4 | −187 pt | 26 / 62% |
| 19/32 | **Morning** | **Union** | **276 (5.8)** | **44.2%** | 38.3% | 1 in 21 | 9 | **+619 pt** | 39 / 41% |
| 19/32 | Regular hours | Union | 469 (9.8) | 40.5% | 37.7% | — | 14 | +169 pt | 73 / 40% |
| 19/32 | Overnight | Union | 257 (5.4) | 38.5% | 37.9% | — | 10 | −168 pt | 24 / 33% |

- **32/19:** the edge holds all session. Afternoon-only trades won about 71%, the same as the morning.
- **19/32:** the edge is morning only. The `10?` branch's afternoon trades ran about 32%.
- **Overnight:** no edge for either bracket.
- The regular-hours window for 32/19 was chosen after seeing results and needs confirmation on new data.

### 3.3 Renko version of the same pipeline (40-tick, SHORT)

Pipeline: F1=10 (transit) → F2=`10?` or `1000` → F3=00.

| Version | Branch | Trades (per day) | Win rate | Base | Net after costs | September |
|---|---|---|---|---|---|---|
| RTH bricks only, daily reset, morning | `10?` | 39 (0.8) | 48.7% | 33.2% | +129 pt | 2 / 50% |
| RTH bricks only, carried, morning | `10?` | 91 (1.9) | 40.7% | 33.2% | +82 pt | 12 / 42% |
| 24h bricks with overnight warm-up, morning | `10?` | 91 (1.9) | 39.6% | 33.3% | +52 pt | 11 / 36% |
| 24h bricks with overnight warm-up, morning | `1000` | 83 (1.7) | 25.3% | 33.3% | −308 pt | 6 / 33% |
| 24h bricks with overnight warm-up, morning | Union | 174 (3.6) | 32.8% | 33.3% | −256 pt | 17 / 35% |

- The `10?` branch lifts the Renko transit win rate by **about 6–15 points** in every version, matching the fixed slicer's direction.
- The `1000` branch does **not** carry over to Renko, so the union dilutes below breakeven there.
- Renko results are weaker and closer to breakeven than the fixed slicer's. A likely reason: fixed-slice outcomes match a real bracket set from the actual entry price, while Renko outcomes are tied to a fixed price grid.
- The LONG book moved the opposite way under the same pipeline (26–28% transit wins), which also means price fell after the signal.

### 3.4 Losing streaks of the leading candidates

| Setup | Worst streak (48 days) | Cost of worst streak |
|---|---|---|
| 32/19, `10?`, regular hours | 5 losses | about 167 pt ($334 per MNQ) |
| 19/32, union, morning | 9 losses | about 183 pt ($366 per MNQ) |

32/19 hurts more per loss; 19/32 hurts more often, in longer streaks. The cost of the worst streak is about the same.

---

## 4. A Possible Explanation

Stock indexes tend to fall faster than they rise. The `10?` → `00` pipeline waits for a "loss, win, loss" shape in the SHORT outcomes and then for two failed attempts in a row. One interpretation is that this marks moments when upside momentum is fading and a quick drop is more likely. This is a plausible explanation, not a proven one.

---

## 5. Annualized Projection (per 1 MNQ, if the 48 days are representative)

**Note:** the Candidate A column below uses the regular-hours window, which later failed the robustness check (Section 6.6). The morning-only version earned +467 pt over 48 days (about +9.7 pt or $19 per day, roughly $4,900 a year per MNQ before any haircut).

| | 32/19, `10?`, regular hours | 19/32, union, morning |
|---|---|---|
| Average per day | +15.7 pt ($31) | +12.9 pt ($26) |
| Annual profit (252 days) | about $7,900 | about $6,500 |
| 5th percentile to median (resampled days) | $5,400 – $7,900 | $3,600 – $6,600 |
| Largest drawdown in sample | 389 pt ($778) | 249 pt ($498) |
| Return on $5,000 capital per contract | about 160% | about 130% |

**Planning figure:** results chosen as the best of many tests usually come in a third to a half lower on new data. A realistic expectation is about **$3,000–$5,000 per MNQ per year if the edge is real**. If the true win rate is at base, the strategy loses about 1.3 pt per trade (roughly −$1,800 a year at this trade count).

Moving to NQ would lower costs from about 1.3 pt to about 0.9 pt per trade (commission shrinks relative to contract size; slippage does not), improving net by about 15–18%. Large size adds market impact, worse stop fills, and proportionally larger drawdowns.

---

## 6. Performance on Up Days vs. Down Days

An **up day** is a day when MNQ closed above its 09:30 open at 16:00 NY. Of the 48 days, **25 were up days and 23 were down days**. The largest up days were August 4 (+2.16%), September 21 (+1.87%), and August 3 (+1.70%).

### 6.1 Candidate A: 32 stop / 19 target, `10?`/00, regular hours (breakeven after costs 65.3%)

| Days | Trades | Win rate | Base (all slices) | Lift | Max losses in a row | Net after costs |
|---|---|---|---|---|---|---|
| All | 273 | 70.7% | 63.6% | +7.1 | 5 | +752 pt |
| **Up days** | **117** | **70.9%** | **59.4%** | **+11.5** | **3** | **+337 pt** |
| Down days | 156 | 70.5% | 66.7% | +3.8 | 5 | +415 pt |
| Strong up (> +0.5%) | 81 | 70.4% | 58.4% | +12.0 | 3 | +210 pt |
| Strong down (< −0.5%) | 108 | 69.4% | 67.1% | +2.3 | 5 | +229 pt |

Plain shorting gets harder on up days (the base rate falls to 59%), but the filtered trades still won about 71%, the same as on down days. The filter adds the most value exactly when the market is rising. The up-day lift (+11.5 points) has about a 1-in-100 chance of being luck. A 19-pt target is reachable even on strong up days, because those days still have pullbacks of that size.

### 6.2 Candidate B: 19 stop / 32 target, union, morning (breakeven after costs 39.8%)

| Days | Trades | Win rate | Base (all slices) | Lift | Max losses in a row | Net after costs |
|---|---|---|---|---|---|---|
| All | 276 | 44.2% | 38.3% | +5.9 | 9 | +619 pt |
| **Up days** | **132** | **37.9%** | **33.8%** | +4.1 | 6 | **−130 pt** |
| Down days | 144 | 50.0% | 42.1% | +7.9 | 5 | +749 pt |
| Strong up (> +0.5%) | 90 | 37.8% | 33.0% | +4.8 | 6 | −93 pt |
| Strong down (< −0.5%) | 94 | 47.9% | 42.8% | +5.1 | 5 | +387 pt |

Candidate B loses money on up days and earns all of its profit on down days. A 32-pt drop before a 19-pt rise rarely happens when the market is grinding higher.

### 6.3 Price at signal vs. the 09:30 open (corrected)

Whether a day is up is only known at 16:00, so the split above is a diagnosis, not a rule. A live equivalent is where price sits relative to the day's 09:30 open when the signal fires.

**Correction:** an earlier version of this section used the close of the minute in which the trade started, which includes a few seconds of future price. That look-ahead created a false "below the open" effect. The table below uses the previous minute's close (no look-ahead).

| Candidate | Price at signal | Trades | Win rate | Base | Net after costs |
|---|---|---|---|---|---|
| A | above open | 136 | 71.3% | 63.1% | +418 pt |
| A | at or below open | 137 | 70.1% | 64.0% | +334 pt |
| B | above open | 109 | 45.0% | 38.1% | +286 pt |
| B | at or below open | 167 | 43.7% | 38.4% | +333 pt |

**The position relative to the open makes no meaningful difference** for either candidate, and plain slices win about equally on both sides. The previous-close split and the "gap up fading" idea also disappeared once the look-ahead was removed.

### 6.4 What this changes

- **Candidate A is the more robust strategy:** profitable on up and down days alike, with a worst streak of 3 losses on up days. Its drawback is the size of each individual loss.
- **Candidate B behaves like a bet on down days** after the fact, but there is no live condition (open or previous close) that identifies those days in advance.

### 6.5 Renko SHORT book with the price-vs-open condition (corrected)

The same condition was tested on Renko SHORT transits (24-hour bricks with overnight warm-up, 48 days, 40-tick, morning), using the previous minute's close (no look-ahead). Breakeven after costs is 37.7%.

| Rule | Price at signal | Trades | Win rate | Net after costs |
|---|---|---|---|---|
| F1=10 alone | at or below open | 1,058 | 33.5% | −1,335 pt |
| F1=10 alone | above open | 694 | 33.0% | −972 pt |
| F2=`10?` → F3=00 | at or below open | 57 | 49.1% | +196 pt |
| F2=`10?` → F3=00 | above open | 34 | 23.5% | −144 pt |

Plain Renko transits win the same on both sides of the open. The `10?` → `00` pipeline split looks large, but only 57 and 34 trades are involved, so this is not reliable evidence. An earlier version of this section reported a 7–10 point base-rate gap; that was produced by the look-ahead bug and has been removed.

### 6.6 Neighboring brackets (robustness check)

Morning `10?`/00 lift over plain slices:

| Bracket (stop / target) | Lift |
|---|---|
| 32 / 19 | +6.3 |
| 32 / 18 | +4.4 |
| 19 / 32 | +6.2 |
| 18 / 32 | +3.5 |
| 19 / 36 | −2.4 |
| 19 / 42 | +0.3 |
| 19 / 19 | −0.9 |

- **Candidate A's morning edge survives at 32/18** (still profitable, max 3 losses in a row). The true lift is probably closer to +4 points than +6.
- **The regular-hours extension of Candidate A fails at 32/18** (lift +0.1), confirming it was chosen after the fact. Use the morning window only.
- **Candidate B is fragile:** its lift halves at 18/32 and disappears at 19/36 and 19/42.

### 6.7 Limit-entry slicer (5-pt limit, 5-second refresh, 14 stop / 32 target)

Limit entries avoid entry slippage but suffer adverse selection: a limit 5 pt away fills mainly when price is moving toward it, and that move often continues into the stop. Plain slices won only 28.8% (SHORT) and 22.6% (LONG) versus about 30.4% for a random 14/32 bracket. Neither pipeline was profitable in either book; the LONG union lifted the win rate to 29.3% but stayed below the 31.7% breakeven. **Limit entries are not recommended.**

---

## 7. Limitations

1. **Sample size:** 48–50 trading days, 180–280 trades per candidate. The best result has about a 1-in-60 chance of being luck, but it is the best of many tests.
2. **Selection effect:** dozens of combinations were tested. Some lucky winners are expected.
3. **Missing data:** August 17–21 and September 8–11 are absent.
4. **Volatility dependence:** July had high volatility and many signals. Since mid-August, slices last longer and trades are fewer.
5. **Simulation versus live fills:** live market entries show extra slippage, stops can slip in fast markets, and outages create gaps.
6. **Live slicer phase:** the live strategy's slices will not match the simulation bit for bit, because slice timing depends on when the strategy starts.

---

## 8. Recommended Actions

1. **Do not trade overnight.** No method or bracket showed an edge between 18:00 and 09:30 NY. Keep `EnableTradingHours` on.

2. **Forward-test the two leading candidates side by side on Sim101:**
   - **Candidate A:** fixed slicer, 32 stop / 19 target, F1=`10?`, F2=00, **morning 09:30–11:30 NY** (the regular-hours version failed the robustness check in Section 6.6).
   - **Candidate B:** fixed slicer, 19 stop / 32 target, union of `10?`/00 and `1000`/00, morning 09:30–11:30 NY.
   - Candidate A is the more robust of the two: it held about a 71% win rate on both up and down days (Section 6).

   **2a. Narrow Candidate A to the morning window.** The regular-hours extension failed the neighboring-bracket check (Section 6.6). Treat Candidate B as unproven and run it in observation mode only.

3. **Build Candidate B as one strategy with two pipelines on one slicer**, not two strategy instances. Separate instances would slice at different times, and the AccountBusy guard would block one whenever the other holds a trade.

4. **Make the trading window and the second pipeline parameters** in the v4 code, so windows and patterns can be changed without recompiling.

5. **Log every would-be trade into the SPRT monitor**, using observation mode (`WOULDBE_TRADE` rows) until each candidate has a few hundred new trades. Judge each candidate only on data collected after today.

6. **Set loss-streak breakers in points, not just counts.** Candidate A: a stop after 5 losses is about 167 pt. Candidate B: 8–9 losses in a row is normal at a 44% win rate; decide in advance whether to allow it or pause at 5.

7. **Keep 1 MNQ flat sizing during testing.** Do not use loss-based doubling. Size up only after the SPRT confirms an edge, and then hold the larger size fixed.

8. **Measure actual fill slippage** on every live entry and exit, and compare it with the 1.3 pt cost assumption.

9. **Fill the data gaps.** Upload August 17–21, September 8–11, and September 28 onward so the full results can be rerun.

10. **Review after 2–3 months.** If live P&L runs at half of the backtest baseline (+$26–31 per day per MNQ) or better, consider moving to 1 NQ, then scaling slowly while monitoring slippage. If results fall back to the base rate, retire the candidate.

11. **Drop Renko as a main strategy.** If it is kept at all, run the SHORT book's F1=10 → F2=`10?` → F3=00 pipeline in observation mode only. Do not use a price-vs-open condition (Section 6.5).

12. **Do not use limit entries** for the slicer (Section 6.7). Stay with market entries.
