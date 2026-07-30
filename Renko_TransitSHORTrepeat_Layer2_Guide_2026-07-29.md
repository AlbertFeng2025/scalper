# Scalper_Renko_TransitSHORTrepeat_Layer2 — Strategy Guide

*A Renko-brick Layer-2 meta-labeling short for MNQ. This document records the full design rationale, the mechanics, the known risks, and a setup checklist. Read the "Critical" boxes before running.*

---

## 0. What's new & research status — 2026-07-29 (read this first)

The mechanism below (bricks, geometry, pipeline) is unchanged and correct. What changed since the original draft:

**The current code default is now `F1 = 100`, `F2 = 0100`** — not `1000 / 000`. This is the out-of-sample research *candidate*, a different bet from the "three failures then revert" gamble the casino-logic sections (§4.1/§5) describe. `100/0100` = "after a red-green-green pullback, once the recent hypothetical-outcome history reads `0100`, take the next one." The `1000/000` config is retained below as the clearest teaching example of how F2 works, and remains available as the discretionary gamble.

> ### ‼ Brick size is a load-bearing parameter — and `100/0100` is NOT brick-size-robust
> Research (`MNQ_research_Renkobar_summary_2026-07-29v2`, §14) found `100/0100` scored ~43% out-of-sample at **40-tick** bricks but **collapsed to 0%** when re-sliced at **80-tick** (same 33.3% breakeven, same pipeline). A real edge degrades gracefully with brick size; it does not invert to zero. So treat `100/0100`'s 40-tick result as **unconfirmed**, and treat **brick size exactly like the stop/target** — a first-class parameter you never change without a full re-test. The only combo showing any cross-brick consistency was `10/0100`. **Decide your brick size deliberately and match the filter *and* the bracket to it** (40-tick → Stop 10 / Target 20; 80-tick → Stop 20 / Target 40). Do not run a 40-tick filter on an 80-tick bracket or vice-versa.

**Trend Gate — new, ON by default.** Right before a real entry it measures `net = (up-bricks − down-bricks)` over the last **N** closed bricks (default N=10) and **skips the real order** when the tape is a strong up-run — SHORT: skip if `net > TrendGateMaxNetUp` (default 3, i.e. 7+ of the last 10 bricks were up). It filters out fades that a rally would run over. Crucially it gates **only the order** — never `rawString`, arming, or logging — so the live bit-stream stays identical to the research pipeline; a gated brick logs `OBS_TREND_GATE` and still writes its `WOULDBE_TRADE` row. Params live in group **"9. Trend Gate"**: `Enable Trend Gate` (true), `bricks looked back (N)` (10), `max net UP-bricks to allow SHORT` (3). On the 40-tick `100/0100` case it lifted OOS win rate 42.9% → 48.8%, cut the worst streak 5 → 3, and pushed the 95% lower bound above breakeven — **but validated at 40-tick only; re-tune N/max for any other brick size.**

**Reconnect re-warm — new, OFF by default** (group "2. Timing"). NinjaTrader auto-reconnect does **not** restart the strategy, so a mid-session blip can leave bricks missing from `rawString` (a silent hole). ON = a lost/dropped price connection clears the pipeline so it re-warms cleanly from live bricks; open positions keep their broker-side GTC bracket and are untouched. Leave **OFF** unless you see reconnect issues — the tail-anchored patterns self-heal a small hole within a few bricks (§9).

**Sizing (research conclusion, reinforcing §7).** Fired outcomes are ~IID, so the loss-ratchet qty rule adds **no** expected edge and only worsens drawdown — keep it **OFF**; to press a confirmed edge use flat fractional-Kelly, never escalation.

---

## 1. What this strategy is, in one paragraph

Every closed Renko brick is turned into a single bit. On the SHORT book we encode **red = 1 (our target) and green = 0**. A fixed trigger pattern on the bit-string arms a *hypothetical* short and tracks whether it would have won or lost; only after that hypothetical setup has **failed three times in a row** do we take a **real** short on the next occurrence — a counter-trend "the run is due to revert" bet. The trade is 20-point stop / 40-point target, and — this is the whole point — that geometry is chosen to line up exactly with the Renko brick grid, so the **next brick's color already tells you whether the trade won or lost**, with no need to simulate the intrabar price path.

---

## 2. The building block: bricks, ticks, and points

- Instrument: **MNQ** (Micro E-mini Nasdaq-100). Tick size = **0.25 index points**.
- Brick size used: **80 ticks = 20 points** (80 × 0.25).
- Each closed brick produces one bit.

**Bit encoding (SHORT book):**

| Brick | Direction | Bit | Meaning for SHORT |
|-------|-----------|-----|-------------------|
| Green | up        | `0` | loss (price rose) |
| Red   | down      | `1` | win (price fell) — this is our **target** |

We chase `1`s. "Target = 1" means we are hunting for the red/down bricks.

> **How the bit is derived (important):** direction comes from **this brick's close vs the previous brick's close** (`Close[0]` vs `Close[1]`), **not** `Close[0]` vs `Open[0]`. NinjaTrader's native Renko fabricates the *open* of reversal bricks for cosmetic reasons ("the open is not real"), which can mislabel a reversal brick and flip the foundational bit. Close-vs-close is immune to that, because consecutive Renko closes always differ by exactly one brick size.

---

## 3. The geometric insight that makes the whole thing work

This is the heart of the design, and it took real work to get right, so it is worth stating precisely.

### 3.1 The standard Renko 2× reversal rule

In standard Renko, from the close of the current brick:

- a **continuation** brick (same direction) prints after price moves **1 × brick size**;
- a **reversal** brick (opposite direction) prints only after price moves **2 × brick size** against the trend.

With a 20-point brick: continuation needs ±20; reversal needs ∓40.

### 3.2 Why "next brick color = trade outcome"

We enter the short at the **close of the 3rd green brick** of the trigger (reference price `P`). We set:

- **stop = 20 points above P** (= `P + 20`)
- **target = 40 points below P** (= `P − 40`)

Now watch what the Renko grid can do from `P`:

- To print a **continuation green** brick, price must reach **P + 20** — which is **exactly your stop**.
- To print a **reversal red** brick, price must reach **P − 40** — which is **exactly your target**.
- Nothing prints in between; a brick only forms at one of those two thresholds.

Therefore **whichever brick prints next is the trade result**, unambiguously:

| Next brick | Price event | Bit | Trade result |
|------------|-------------|-----|--------------|
| Green (up) | hit +20 first | `0` | **STOP — loss** |
| Red (down) | hit −40 first | `1` | **TARGET — win** |

Because Renko brick printing is real-time and touch-based (the instant price crosses the threshold), and the stop/target sit *exactly* on the brick thresholds, there is **no hidden intrabar path** that can make brick color disagree with the trade outcome. A red brick can only form if price reached −40 *without first reaching +20* (otherwise the green brick would have formed first). This is why we can grade the outcome straight off the brick stream and do not need tick-level simulation.

> **The trap we avoided:** with a 40-point brick and a 20-point stop, the stop would sit *inside* the brick's blind zone (the 0-to-40 range where no brick forms), so a "+20 stop-out then reverse to −40" would look like a win on the brick stream but was actually a loss. The 20-point brick (= stop = 1× brick) plus the 2× reversal rule removes that blind zone entirely. **The geometry only holds because stop = 1 brick and target = 2 bricks.**

> ### ⚠️ CRITICAL — stop/target must match brick size
> Brick size, stop, and target are **one setting expressed in three places**:
> - Stop = **1 × brick size**
> - Target = **2 × brick size**
>
> With an 80-tick (20-pt) brick that means **Stop = 20, Target = 40**. If you ever change the data-series brick size, you **must** change the stop and target to 1× / 2× the new size. If you don't, trades stop resolving in exactly one brick, brick color stops equaling the trade outcome, and the pipeline and P&L silently diverge.

---

## 3.3 Structural limitation — this is a *single-transition* strategy

> **⚠️ READ THIS — it constrains which filter patterns are even usable.**

The clean 20/40 geometry from §3.2 is valid at **exactly one place**: the **green → first red** flip, i.e. an entry whose *preceding* brick is green. That is the only point where the reversal direction (2×, far) points toward your **target** and the continuation direction (1×, near) points toward your **stop**. Enter anywhere else and the distances flip.

Concretely — the same "short" trade behaves differently depending on the brick you enter *after*:

| Entry point | Brick before entry | Down (continuation vs reversal) | Up (continuation vs reversal) | Matching geometry | Breakeven |
|---|---|---|---|---|---|
| green → **first** red (`…0 → 1`) | green (up) | reversal → −40 (2×) = **target** | continuation → +20 (1×) = **stop** | **stop 20 / target 40** | **33.3%** |
| inside a red run (`…1 → 1`, e.g. `011→1`) | red (down) | continuation → −20 (1×) | reversal → +40 (2×) | **stop 40 / target 20** | **66.7%** |

**Why:** after a **green** brick, "down" is the *reversal* (needs 2× = 40), so your down-target is far and your up-stop is near → 20/40. After a **red** brick, "down" is the *continuation* (needs only 1× = 20), so your down-target is near and your up-stop is far → the mirror, 40/20. The direction you're betting (down) never changes; what changes is whether "down" is currently the far (reversal) move or the near (continuation) move.

**The consequence for filter design:** you **cannot** freely pick any high-`1`-probability pattern. A pattern that fires *inside a red run* — e.g. `011` chasing a continuation red — is **incompatible with the fixed 20/40 bracket**. If you ran 20/40 there:
- one red brick (−20) would **no longer resolve** the trade (your 40-pt target needs two bricks down, while your 20-pt stop sits back in the blind zone), so
- **brick color would stop equaling the trade outcome**, and the filter's measured win rate would **not match** what the real trade does.

**Rule:** every usable filter in this strategy must fire at a **green → first-red** flip. The default `F1 = "1000"` obeys this (red-green-green-green → fire at the 3rd green's close → chase the first red). Any continuation-entry idea needs a **separate 40/20 mirror-geometry variant**, not this file.

The **LONG** book has the exact symmetric limitation — it fits **only** the **red → first green** flip (see §13).



The pipeline is identical in spirit to the offline Python `trade_filter.py`. Three strings and three flags drive it.

**Strings:**
- `rawString` — every brick's bit (Layer 0).
- `filter1Outcome` — the outcome bit that follows each Filter-1 match (Layer 1).
- `realTradeOutcome` — real money trade results only (audit trail).

**Per brick, in order:**
1. Compute the bit from brick direction; append to `rawString`.
2. If we were *waiting* for a Filter-1 outcome, append this bit to `filter1Outcome`, then check whether `filter1Outcome`'s tail matches **Filter 2** → set `isArmed`.
3. If `rawString`'s tail matches **Filter 1**, set "waiting for the next bit" (that next bit becomes this trigger's outcome).
4. `nextIsMoney = isArmed AND (rawString tail matches Filter 1)`.
5. If `nextIsMoney` and real orders are enabled and no position is open → **enter the real short this bar**.

**Defaults (see §0 — the CODE default is now `100 / 0100`).** The `1000 / 000` pair below is the original illustrative "gamble" config, kept because it is the clearest way to see how F2 works. The current research-candidate default is `F1 = 100`, `F2 = 0100` — same pipeline, tighter F2 history (arms on `…0100` rather than `…000`), and brick-size-fragile per §0.
- **Filter 1 = `1000`** (illustration) — in the SHORT encoding this is **red, green, green, green** (a 3-brick up-bounce inside/after a down context). Fully known the instant the 3rd green closes, so it is live-fireable. (`100` = red-green-green fires one brick earlier; both obey the green→first-red rule of §3.3.)
- **Filter 2 = `000`** (illustration) — the last **three** Filter-1 outcomes were all losses. (The candidate `0100` arms on a fail-win-fail-fail history instead.)

### 4.1 In plain English (the "casino" logic)

Every time `1000` appears, imagine taking the 20/40 short and record whether it would have won (`1`) or lost (`0`) into `filter1Outcome`. When the **last three such attempts all lost** (`filter1Outcome` ends in `000`), you are **armed**. The **next** time `1000` appears, you take the **real** short — betting that after three straight failures, this one reverts.

- A **loss keeps you armed** (appends `0`, so the tail stays `…000`) → you keep firing, attempt after attempt.
- A **win disarms you** (appends `1`, breaking the `000` tail) → you go back to hunting for three fresh failures.

Real trading continues until one of: the loss breaker trips, the session closes (EOD flatten = recorded loss), you fall outside the trading-hours window, or the strategy-life timer expires.

---

## 5. The bet: breakeven, edge, and what "success" means

- Payoff is **2:1** in your favor (risk 20 to make 40).
- Breakeven win rate: `p·40 = (1−p)·20` → **p = 33.33%**.

**The research question** is therefore: *is there some conditioning that lifts the empirical win rate meaningfully above 33.33%, with enough margin to survive slippage and commission?* The self-referential streak filter (arm after 3 failures, bet the reversion) is the hypothesis on trial here. It is **not yet validated** — external filters (time, volume, EMA) did not hold up in the earlier slice research, and this internal-streak idea is a different, untested bet.

> **Measure the base rate first.** Before believing any "lift," confirm the unconditioned rate: does `1000` → *next brick red* happen **more than 33.3%** of the time? Equivalently, does the next brick go green (stop) *less than 66.7%*? And check it **within regime buckets** (down / chop / up days), not just pooled — a base rate that clears 33.3% on average can still bleed on grind-up days.

> **Fire count matters as much as win rate.** A great-looking rate on a dozen fires is not validated. Always read win rate *and* number of fires together.

---

## 6. Regime dependence (where it makes and loses money)

This is a pure **counter-trend / mean-reversion** short, so its P&L is regime-dependent by construction:

- **Down day** — red bricks common; `1000` is a small bounce inside a downtrend; betting the next brick is red tends to win. **Favorable.**
- **Whipsaw** — reversals cluster; "3 failures then reverts" holds more often. **Mixed-to-OK.**
- **Grinding / choppy up day** — the **damage zone.** Enough small dips to keep forming `1000`, each fails, and you keep firing shorts into strength that keep hitting the +20 stop. This is what walks the book into the loss breaker.
- **Near-vertical melt-up** — few red bricks form, so `1000` rarely even appears; you barely trade. Less dangerous than the grind-up, counterintuitively.

There is **no long hedge** — directional risk is inherent to a short-only book. Your structural protections are: the arming delay (don't fire until 3 failures), the loss breaker, the RTH hours window, and the qty-skip.

---

## 7. Position sizing (the qty rule)

**Default: OFF** (`EnableQtyIncrement = false`) → every trade is `BaseQuantity` (1). Turn it on deliberately.

When on, the multiplier is a **capped loss-ratchet** driven by the tail of the per-day real-outcome string (`sessionRealOutcome`, where `1` = win, `0` = loss). The table:

| Tail of real W/L | Multiplier |
|------------------|-----------|
| `00` (2 losses)  | ×2 |
| `000` (3 losses) | ×3 |
| `0000` (4 losses) | ×3 (plateau) |
| `00000` (5 losses) | ×4 |
| anything else    | ×1 (base) |

*(This is the current default table `("00":2),("000":3),("0000":3),("00000":4)`. With the breaker at 6, a full 6-loss run sizes 1,1,2,3,3,4 = **14 units** vs **6** flat — a **2.3× larger worst day** if enabled.)*

- **Longest match wins** (so `000` overrides `00`).
- Because the patterns have **no leading `1`**, they escalate on a loss run **from the day's open too** (unlike the older slice table, which only escalated after a win).
- It is **capped at ×3** — not an unbounded martingale.

> **⚠️ This is still loss-direction escalation.** The ×3 bet lands on the **4th consecutive loss** — the biggest size at the deepest point of a losing streak, which (per §6) tends to coincide with the grind-up regime where the premise is false. Capped at ×3 it will not blow the account, but understand what it is.
>
> **Sizing does not create edge.** If each trade has positive expectancy (win rate > 33.3%), you make money at *flat* size. Escalating after losses only reshapes the distribution (many small wins traded for a rare large loss). The statistically sound way to press a real edge is **flat fractional (fraction-of-Kelly)** sizing — which is *flat* and *shrinks* near breakeven, the opposite of a loss-ratchet. Kelly here: `f* = p − (1−p)/2` (e.g. ≈10% of capital at 40% win rate for full Kelly; most use ¼–½ of that; only ≈2.5% at 35%). Consider fixed-fractional once the win rate is confirmed on real fills.

### 7.1 The breaker interaction (read before enabling qty)

The loss breaker is `MaxRealLossInARow` (default **6**, was 4). It is checked **before** each trade.

- For the **×3** line to ever fire you need 3 prior losses **and** a 4th trade, so `MaxRealLossInARow ≥ 4`. At exactly 4 you take **one** ×3 trade, then the breaker halts. Higher values let ×3 repeat.
- The qty table here has **no ×0 skip lines**, so every armed trade is real and every loss counts toward the breaker — the breaker cannot be "starved." (This is cleaner than the slice book, where ×0 skips can freeze the streak.)

---

## 8. Engineering: what the file does under the hood

These are the deliberate implementation choices (several were fixes to earlier drafts):

- **Per-brick logging.** A data row is written for **every** brick, not just trades, so the CSV mirrors `rawString` bit-for-bit. This is what makes both resume and offline Python re-checks trustworthy.
- **No time-throttle.** With `Calculate.OnBarClose`, the bar handler fires once per closed brick and **every** brick is processed. (An earlier draft had a 1-second throttle that could silently drop bricks in fast moves — removed. Note: the *slice* strategy legitimately keeps its throttle, because there the throttle *is* the slice cadence. Do not copy that fix across.)
- **Close-vs-close coloring** (see §2) — robust against NT's fabricated reversal-bar open.
- **Interrupt = fresh start** (see §9) — resume is disabled by default.
- **Fixed stop only.** Trailing-stop options are hidden, because a trailing stop moves the stop off the brick grid and breaks the "brick color = outcome" invariant.

---

## 9. Interrupt behavior — **any interrupt = brand-new start**

> **⚠️ IMPORTANT.** Any disable→enable, disconnect, reconnect, or interrupt starts a **brand-new pipeline**: empty `rawString`, `isArmed = false`, warmed up again from live bricks. Pre-interrupt arming/context is **discarded**. This is intentional and safe.

Why fresh rather than resume: the old resume logic measured the gap in *minutes*, but the Renko pipeline advances in *bricks*, and a fast move can print many bricks in a few minutes. There is no reliable way to know how many bricks were missed during a disconnect, so resuming risks appending live bricks onto a **holed** string. Fresh-start avoids that; the daily 3 PM PT reset means the warm-up cost is at most one trading day. (An `AllowLogResume` switch exists in source but is hidden and off; leave it off unless you have validated brick continuity across your own reconnect pattern.)

**Practical consequence:** change parameters at a natural boundary (right after the 3 PM PT reset, or pre-open) so you are not throwing away mid-session arming context.

---

## 10. Trading-day reset and the two "hours" controls

- **`rawString` resets once per trading day at the 3 PM PT boundary.** The CME trading day *starts* at 3 PM PT (the Sunday/weekday reopen), so each new day begins with an **empty** pipeline and warms up **during the thin overnight tape.** It also resets on any interrupt (§9).
- It does **not** reset at the daily maintenance break (4–5 PM PT); the string persists across that break. Only the 3 PM PT rollover (and interrupts) clear it.
- **What resets per day:** `rawString`, `filter1Outcome`, `isArmed`, waiting flags, `sessionRealOutcome` (qty history). **What is kept:** `realTradeOutcome` (audit) and `realLossesInARow` (cumulative breaker), though the breaker also clears on a new day.

**Two independent "hours" settings — don't conflate them:**

| Setting | What it controls |
|---------|------------------|
| Data-series **Trading Hours template** (CME US Index Futures ETH) | session boundaries used for EOD flatten / day rollover. **Keep ON (ETH).** |
| Strategy **`EnableTradingHours`** filter (NY-time window) | the intraday window in which real trades may fire. **Recommended ON.** |

> **Keep the intraday hours filter ON (default 9:30–11:30 ET / RTH).** With it off, the first thing the book trades is the Sunday-evening reopen and overnight session — thin volume, wide spreads, gap risk — the worst possible environment for a 20-pt-stop mean-reversion short, where a single gap can jump straight through the stop. That is where a low-breakeven edge quietly bleeds out.

---

## 11. Enable-time and Sunday-open behavior

- **Data series must be the Renko 80-tick series, not minute bars.** Every bar handler call must be one closed brick, or the whole pipeline is meaningless.
- **Sunday reopen:** MNQ opens **Sunday 3:00 PM Pacific** (6:00 PM ET / 5:00 PM CT). If you enable the strategy earlier (e.g. 2 PM PT Sunday), it goes live but sits **idle** — no ticks means no bricks — until 3 PM PT, when bricks begin and the pipeline warms from empty.
- **On enable, the string starts from the first brick that CLOSES after enable.** Historical bricks already on the chart are skipped (the realtime-only guard). So `rawString`'s tail **matches the chart's recent bricks color-for-color**; only the *length* is short until it fills in. You will **never** see the chart print `1000` while the string's last four bits say something else — the only difference is coverage, not color.
- **You cannot "time" an entry off a pattern you already see on the chart.** Because enabling wipes the string to empty, it must rebuild both the `1000` and the three-failure arming from *post-enable* bricks before it can fire. Manual timing works for *starting the warm-up*, not for catching a specific visible pattern.
- **EOD flatten is recorded as a loss** (conservative), regardless of the position's true P&L at session close — it can inflate the loss count and feed both the breaker and the qty table.

---

## 12. Reading the log

The CSV columns: `timestamp, slice/bar_num, side, quantity, entry_price, exit_price, realized_pnl, win_loss_bit, rawString, filter1Outcome, realTradeOutcome`.

**The `side` column tells you what each row is:**

| `side` value | Meaning |
|--------------|---------|
| `FAKE_Short` | ordinary observation brick (pipeline not armed/firing) |
| `WOULDBE_TRADE` | pipeline **armed and fired** this brick |
| `Short_ENTRY` | a real order was placed (entry audit row, carries entry price) |
| `Short` | a real trade **closed** — this row's `win_loss_bit` is the real outcome |
| `OBS_OUTSIDE_HOURS` / `OBS_ACCOUNT_BUSY` / `OBS_QTY_SKIP` | armed, but the order was suppressed by a guard |
| `OBS_TREND_GATE` | armed & fired, but the Trend Gate suppressed the order (strong up-run; §0) |

- Only `side == "Short"` close rows are read back as real-trade outcomes.
- Startup writes a clear `[FRESH START]` line to the diag log — the Strategies tab **cannot** tell you fresh-vs-resume, so verify via the log.

> **`realized_pnl` is nominal, not fill-based.** It is computed from the stop/target points, not from actual fills. The real entry/exit prices **are** in their own columns, so reconstruct slippage from those before trusting any P&L — with a 20-pt stop, a tick or two of slippage matters a lot to a low-breakeven edge.

---

## 13. The LONG mirror (for reference — testing SHORT only for now)

A `Scalper_Renko_TransitLONGrepeat_Layer2` exists as a clean direction mirror. Only the encoding and bracket flip: **green = 1 (target), red = 0**; `F1 = "1000"` then reads as **green-red-red-red** (a 3-brick *down* move); enter LONG at the close of the 3rd red brick; **stop = 20 below**, **target = 40 above**; next brick red = stop (loss), green = target (win). Everything else is identical. Its worst regime is a grinding **down** day (mirror of the short's grind-up weakness). Current plan is to test the **SHORT** book only; the LONG is on the shelf.

**Same single-transition limitation, mirrored (§3.3):** the LONG's 20/40 bracket is valid **only** at the **red → first green** flip (entry bar's preceding brick must be red). Entering *inside a green run* (chasing a continuation green) flips the matching geometry to stop 40 / target 20 (66.7% breakeven), which the 20/40 bracket does not fit. Every usable LONG filter must fire at a red→first-green flip.

---

## 14. Known caveats (quick reference)

- **Single-transition only (§3.3)** — the 20/40 bracket fits *only* the green→first-red flip (SHORT) / red→first-green flip (LONG). Filters that enter mid-run need a separate 40/20 variant; you cannot just pick any high-`1` pattern.
- **Brick size is load-bearing (§0)** — `100/0100` collapsed from ~43% (40-tick) to 0% (80-tick). Never change brick size without a full re-test; match the bracket to it.
- **Trend Gate is 40-tick-validated only (§0)** — on ON by default; re-tune `N`/`max` for other brick sizes.
- **Untested hypothesis** — confirm the base rate clears 33.3% (with regime buckets) before believing anything.
- **Nominal P&L** — reconstruct slippage from the entry/exit columns.
- **Regime risk** — grind-up days are the bleed zone; no long hedge.
- **Real vs. ideal** — entry slippage shifts the bracket slightly off the brick grid, so the real fill outcome and the brick-color bit can differ on boundary ticks.
- **Fire count** — always read it alongside win rate.
- **Observation first** — keep `EnableRealOrder = false` long enough to build a forward-logged sample you would actually trust.

---

## 15. SETUP CHECKLIST

Work top to bottom. Items marked **(critical)** will corrupt results or risk the account if wrong.

### Data series
- [ ] **(critical)** Strategy attached to a **Renko** series with brick size **80 ticks (= 20 pt)** on **MNQ** — **not** minute/other bars.
- [ ] **(critical)** Data-series **Trading Hours template = "CME US Index Futures ETH"** (needed for session boundaries / EOD).
- [ ] `Calculate` is **OnBarClose** behavior confirmed (one handler call per closed brick).

### Bracket geometry — the one setting in three places
- [ ] **(critical)** `StopLossPoints = 20` (= 1 × brick size).
- [ ] **(critical)** `ProfitTargetPoints = 40` (= 2 × brick size).
- [ ] If you ever change brick size, you changed **both** stop and target to 1× / 2× the new size.
- [ ] Trailing stop **off** (it is hidden by default — confirm you did not re-enable it in source).

### Filter / pipeline
- [ ] **(critical)** Decide brick size FIRST (§0), then set the bracket to match: 40-tick → Stop 10 / Target 20; 80-tick → Stop 20 / Target 40.
- [ ] `Filter1Pattern` / `Filter2Pattern`: **research candidate `100` / `0100`** (40-tick), or the illustrative gamble `1000` / `000`. Do NOT run the 40-tick `100/0100` on an 80-tick bracket — that is the combo that collapsed (§0).
- [ ] **(new) Trend Gate:** `Enable Trend Gate = true` (default); `N = 10`, `max = 3` (tuned for 40-tick — re-tune otherwise).
- [ ] **(critical)** Any filter you choose fires at a **green → first-red** flip (SHORT) / **red → first-green** flip (LONG). Do **not** use a pattern that enters mid-run (e.g. `011` chasing a continuation) — the fixed 20/40 bracket does not fit it (§3.3).

### Safety / mode (start conservative)
- [ ] **(critical)** `EnableRealOrder = false` — **observation mode first.** Only flip to true after you trust the forward-logged sample.
- [ ] `EnableTradingHours = true`, window **09:30–11:30 ET** (keep RTH; do not trade the Sunday/overnight tape).
- [ ] `IsExitOnSessionCloseStrategy = ON` (EOD flatten; recorded as a conservative loss).
- [ ] `EnableQtyIncrement = false` initially. If/when you enable it:
  - [ ] qty table `{"00":2,"000":3,"0000":3,"00000":4}`; understand it escalates on the deepest loss (§7) and roughly 2.3×-es the worst day. Research says leave it OFF (flat, ~IID).
- [ ] `MaxRealLossInARow = 6`.
- [ ] `BaseQuantity = 1`.
- [ ] (optional) `Re-warm pipeline on disconnect = false` unless you have a reconnect problem (§0/§9).

### Logging
- [ ] `LogFolder` set to a real writable path (e.g. `C:\temp`) and `LogBaseName` set.
- [ ] After enabling, confirm a new CSV appears and a `[FRESH START]` line is in the diag log.
- [ ] Confirm ordinary `FAKE_Short` rows are being written (per-brick logging is working).

### Operating discipline
- [ ] You know: **any interrupt = brand-new start** (pipeline re-warms; pre-interrupt arming lost).
- [ ] Change parameters at the **3 PM PT reset** or pre-open, not mid-session.
- [ ] You do **not** try to time an entry off a pattern already visible on the chart (§11).
- [ ] For sharing with a second machine (OneDrive): copy the `.cs`, **delete the compiled DLL cache**, then **F5 recompile**.

### First-review discipline (after a week or two)
- [ ] Measure the **base rate** of `1000 → next brick red`; is it > 33.3%?
- [ ] Break it out by **regime bucket** (down / chop / up), not just pooled.
- [ ] Read **win rate and fire count together.**
- [ ] Reconstruct **slippage from entry/exit columns** (ignore the nominal `realized_pnl`).
- [ ] Keep SHORT and slice-book conclusions **separate** — they are different hypotheses.

---

*End of guide. This is documentation, not financial advice; validate on your own forward-logged data before committing real capital.*
