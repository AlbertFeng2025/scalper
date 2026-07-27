# Scalper Renko Transit-repeat — Layer 1 (SHORT & LONG): How It Works

**Files:** `Scalper_Renko_TransitSHORTrepeat_Layer1` and `Scalper_Renko_TransitLONGrepeat_Layer1`
**Instrument / data:** MNQ, 80-tick Renko bricks (= 20 points per brick)
**Bracket:** 20 pt stop / 40 pt target, brick-close-anchored
**Default filter:** `10,100,1000` (multi-pattern, comma-delimited OR)

---

## 1. One-paragraph summary

Layer 1 turns each closed Renko brick into a single bit, appends it to a running
string, and fires a real trade the moment the tail of that string matches any one
of your Filter-1 patterns. It is the Layer-2 strategy with the Layer-2 meta-label
gate removed — there is **no arming condition**. You point it at a setup you have
spotted by eye, and it trades the very next matching pattern. The SHORT book bets a
green run flips to red; the LONG book is the exact mirror and bets a red run flips
to green.

This is a **discretionary tool, not an autonomous edge.** Read Section 9 before you
enable it with real orders.

---

## 2. The bit: how a brick becomes a 0 or a 1

Every time a Renko brick **closes**, the strategy compares this brick's close to the
**previous** brick's close (never close-vs-open — NinjaTrader fabricates the open of
a reversal brick, which would mislabel it).

| Brick built | Color | SHORT book bit | LONG book bit |
|---|---|---|---|
| Close rose (up) | GREEN | `0` (loss for short) | `1` (win for long) |
| Close fell (down) | RED | `1` (win for short) | `0` (loss for long) |

The encodings are mirror images. In **both** books the bit is `1` when the brick
went *your* way and `0` when it went *against* you. Because the encoding flips
between the two books, **the same pattern string means the mirror setup in each**:

- SHORT `1000` = red, green, green, green → a red then a 3-brick UP move.
- LONG  `1000` = green, red, red, red → a green then a 3-brick DOWN move.

That symmetry is why both files ship with the identical default `10,100,1000`.

---

## 3. The pipeline (what happens on every closed brick)

1. **Determine the bit** from this close vs the previous close.
2. **Append** the bit to `rawString`.
3. **Test the tail** of `rawString` against your Filter-1 pattern list. If it matches
   any one of them → `nextIsMoney = true`. *(Layer 1 stops here — there is no F2
   arming gate.)*
4. If `nextIsMoney` **and** `EnableRealOrder = true` **and** no position is already
   open → enter a real trade this brick with the bracket.

The observational `filter1Outcome` / `isArmed` values are still computed and written
to the log so you can see what Layer 2 *would* have done — but in Layer 1 they do
**not** gate anything. F2 is ignored for trading.

---

## 4. The 20/40 bracket and why it is exact

The whole design rests on one alignment: under the standard Renko 2×-reversal rule,
from the close of a brick the next move is either **1 brick (20 pt) as a
continuation against you** or **2 bricks (40 pt) as a reversal your way.**

- **Stop = 1 × brick size = 20 pt** (the continuation level)
- **Target = 2 × brick size = 40 pt** (the reversal level)

Both stop and target are anchored to `Close[0]` (the just-closed trigger brick),
**not** to your fill price, using absolute prices. This keeps them exactly on the
brick grid, so whichever brick prints next *is* the trade outcome even if your entry
slips off-grid. (Anchoring to the fill is what caused the trade-3 whipsaw failure in
the 2026-07-19 sim run; that is fixed here.)

- SHORT: stop = `brickClose + 20` (green level), target = `brickClose − 40` (red level).
- LONG:  stop = `brickClose − 20` (red level),   target = `brickClose + 40` (green level).

P&L is booked from the **actual fills** (so it reflects real slippage), but the
win/loss bit comes from **which bracket filled** — stop = loss (`0`), target = win
(`1`) — which is exactly the color of the brick that resolved the trade.

> **Brick size, stop, and target are one setting in three places.** If you ever
> change the data-series brick size, you must change stop/target to 1× / 2× the new
> size, or trades stop resolving in one brick and "brick color == outcome" silently
> breaks. On MNQ keep it 80-tick brick ⇒ 20/40.

---

## 5. The single-transition limitation (why patterns must end in `0`)

The 20/40 geometry is valid **only at the flip**:

- **SHORT:** the entry brick's *preceding* brick must be **green**. You fire at a
  green → first-red flip. Only there does reversal (2× = 40) point down = your
  target and continuation (1× = 20) point up = your stop → a clean 33.3%-breakeven
  trade that one brick resolves.
- **LONG:** the mirror — the preceding brick must be **red**; you fire at a
  red → first-green flip.

Practically: **every usable pattern must end with the against-you bar** (`0` in both
encodings). `10`, `100`, `1000` all end in `0`, so all three fire at the flip and are
geometry-valid. A pattern that fires *inside* a with-you run (chasing a
continuation) would need the mirror 40/20 bracket (66.7% breakeven) and is **not**
what this strategy is for.

---

## 6. Multi-pattern firing (the new behavior)

`Filter 1 Pattern` now accepts **one or more comma-delimited tail patterns.**

- Comma-separated; **all whitespace is stripped** (`10, 100 , 1000` = `10,100,1000`).
- A trade fires if the `rawString` tail matches **any** pattern (logical **OR**).
- **Wildcards work per pattern:** `*` = one-or-more `0`s, `?` = one-or-more `1`s.
- **Blank or all-invalid input parses to an empty list → NO trade ever fires.** This
  is an intentional safeguard: a mistyped filter trades *nothing* rather than trading
  the wrong thing. Confirm the `[F1 PATTERNS] ACTIVE ...` line in the diag log.

Pick your aggressiveness by typing the parameter — no recompile:

- **`1000`** — the tight bet. Fires only when a run ends at exactly length 3.
- **`10,100,1000`** — also catches runs that flip at length 1 or 2.

### 6.1 How overlapping patterns fire *within one run* — read this carefully

With `10,100,1000` all live, a single developing run fires **sequentially as it
grows**, because each stage matches the next pattern and resolves in one brick before
the next brick prints:

| Run flips at | Fires | Result sequence |
|---|---|---|
| length 1 | once (`10`) | **win** |
| length 2 | twice (`10`, then `100`) | loss, **win** |
| length 3 | 3× (`10`, `100`, `1000`) | loss, loss, **win** |
| length 4+ | 3× then stops | loss, loss, loss, then **silence** (tail is now `10000`, matches nothing) |

So the long runs you are betting *against* produce up to **three stacked losses**,
and a run of 4+ gives exactly three losses and then goes quiet — it does **not**
keep shorting into the trend. `1000` alone fires **once per run** (only at length 3),
so a bad run costs one loss, not three.

---

## 7. Position sizing and the loss breaker

**Qty ratchet (`Qty rule`, applied only when `Enable Qty Increment` is ON).**
Loss-ratchet on the **real trade-outcome** string (`1`=win, `0`=loss), longest
matching tail wins. Default `("00":2),("000":3)`:

- 2 losses in a row → next real trade ×2
- 3+ losses in a row → ×3

Any multiplier over 20 is refused (a typo can never inflate size). Verify the
`[QTY RULE] ACTIVE` line in the diag log.

**Breaker (`Max Real Loss In A Row`, default 4).** When consecutive real losses reach
this number, the strategy halts the session (terminates). The counter resets at the
3:00 PM PT day boundary if the strategy is still running.

### 7.1 The interaction you must understand

The ratchet is a **reverse-martingale**: it puts your **biggest size on the reversal
attempt**. In a mean-reverting regime (your edge), that means the winning flip is
sized up — good. In a strong trend (your risk), it means your largest size lands on
a *losing* trade just as the trend proves you wrong — a martingale-into-a-trend.

Worked example, `10,100,1000` + default ratchet + breaker 4, in a strong wrong-way
trend (two long runs back-to-back):

| Fire | Session tail | Qty | Outcome |
|---|---|---|---|
| Run A, len-1 | `` | 1 | loss → `0` |
| Run A, len-2 | `0` | 1 | loss → `00` |
| Run A, len-3 | `00` | **2** | loss → `000` |
| Run B, len-1 | `000` | **3** | loss → `0000` → breaker hits 4 → **HALT** |

Bounded exposure ≈ 1 + 1 + 2 + 3 = **7 contracts × 20 pt ≈ $280** on MNQ, then it
stops. It is bounded — but notice the x3 lands on the fourth loss. If maximum safety
is the goal, run **flat qty + a tighter breaker**; if you want the reversal upside,
keep the ratchet and go in knowing the last loss is the largest.

**`Enable trade-outcome exit`** (optional): halt the session when the real-outcome
tail ends with a plain pattern (default `1` = stop after a win).

---

## 8. Session mechanics

- **Trading hours** (`Enable Trading Hours filter`): New-York time, default
  09:30–11:30. **Requires the data-series Trading Hours template = `CME US Index
  Futures ETH`.**
- **Daily boundary (3:00 PM PT):** `rawString`, `filter1Outcome`, the per-day qty
  string, and the loss-breaker counter all reset. The cumulative `realTradeOutcome`
  is kept.
- **EOD flatten:** `IsExitOnSessionCloseStrategy` is ON; a position closed by the
  session close is booked conservatively as a **loss** (bit `0`).
- **Fresh start on any interrupt:** any disable/enable, disconnect, or reconnect
  starts a **brand-new** pipeline (empty `rawString`, everything disarmed) and
  re-warms from live bricks. Renko resume is intentionally OFF (`AllowLogResume =
  false`) because a brick gap cannot be measured in minutes. Prefer changing
  parameters at the 3:00 PM PT reset or before the open so you are not throwing away
  mid-session context.
- **Guards before every entry:** outside trading hours → skip; account already
  busy on this instrument → skip; qty rule returns 0 → skip. Each writes an `OBS_*`
  row instead of trading.
- **`Enable Real Order = false`** means observation only: the pipeline runs and logs
  `WOULDBE_TRADE` rows, but no order is sent.

### 8.1 Seeding the pending brick (`Seed pending brick on start`, default OFF)

When you enable the strategy mid-brick, the brick that was *already forming* at that
instant (the "pending brick") is normally dropped: NinjaTrader hands its close to the
strategy still classified as historical/transition, so the realtime guard skips it,
and your first recorded bit is the **next** brick that fully closes after enable.

Turn this toggle **ON** to recover that pending brick. On the first realtime brick the
strategy looks back one slot (`Close[1]` vs `Close[2]`), computes the pending brick's
bit, and records it as **bar #1** — so `rawString` starts one brick earlier. It is the
brick immediately before your first live brick (contiguous, no gap), so this does *not*
reintroduce the stale-data problem that made full Renko log-resume unsafe.

**The seed brick is recorded, but it NEVER fires a trade — for any pattern, including
`1` or `0`.** This is the key point:

- The seed brick **counts** for building `rawString` (it is bit #1, and it is the first
  row in the log).
- The seed brick **does not count** as a trade trigger. Order placement is force-suppressed
  on the seed. Even if you use a single-character pattern like `1` or `0` that the seed
  bit matches, the seed will **not** place an order — it only logs a `WOULDBE_TRADE_SEED`
  row to show it matched. **Only bricks from bar #2 onward (the first fully-live brick)
  can place a real order.**

Why it can't fire: after the seed runs, the strategy immediately processes the current
(first live) brick, whose pipeline recomputes the trade trigger from the updated tail.
The trade decision is made for that live brick, never for the seed.

Practical consequence by pattern length:

- **Multi-character patterns (e.g. `10`, `1000`):** the seed helps. Its leading bit lets
  the pattern *complete* one brick sooner on a live brick. Example: pattern `10`, seed
  brick `1`, next live brick `0` → tail `10` matches → fires on the live brick, one brick
  earlier than without the seed.
- **Single-character patterns (`1` or `0`):** the seed makes **no difference** to firing,
  because a one-bit pattern only ever looks at the most recent bit. The seed bit is
  recorded but can't fire (suppressed), and every later matching brick fires on its own
  merits regardless of the seed. *(Note also that `1` ends on a with-you bar and is not
  20/40-geometry-valid, and `0` fires on every single against-you brick — neither is a
  run-reversion setup; see Section 5.)*

**Logging.** The seed row is bar #1 with side `FAKE_Short_SEED` / `FAKE_Long_SEED` (or
`WOULDBE_TRADE_SEED` if it matched), and a `[SEED BAR #1]` diag line records the brick's
**true close time**. (The CSV row's own timestamp column is the moment of recovery, not
the brick's original close; the true close time is in the diag line.)

**Caution.** A Renko brick has no fixed duration, so the pending brick may have largely
formed *before* you enabled. Leaving this **OFF** is the stricter choice (only trade
bricks that fully formed after you chose to be in); turning it **ON** arms you one brick
sooner at the cost of that first brick possibly being partly pre-enable. Default is OFF,
so existing behavior is unchanged unless you set it.

---

## 9. ⚠️ Discipline — this is a gamble, not an edge

**33.3% is the BREAKEVEN, not the win rate.** It comes purely from the 2:1 geometry
(risk 20 to make 40 → 20/60 = 33.3%). It says nothing about how often you actually
win. Under a coin-flip market a flip-entry wins ~50% (well above breakeven). But
markets trend, runs persist, and **trend persistence drags the real win rate down
toward — and in a strong trend below — that 33% line.** Baseline testing on this
geometry has sat around ~34%, i.e. right at the edge. The code gives you **no
statistical edge on its own.** Your discretionary timing is the only thing that can
push the true win rate above breakeven, and it may not.

**A 33%-breakeven setup can miss 10 or 15 attempts in a row in the real world.**
That is normal variance, not a broken strategy. The correct behavior:

1. **Enable only after you have personally seen clustered long (4+) runs** — this is
   an eyeball, run-length-reversion bet. Taking the chance on a gambler's-fallacy
   read is a *choice you make*, not something the math earns for you.
2. **Set the breaker tight and honor it.** When it halts you, you are done.
3. **If you lose the day's allowance, WALK AWAY and look again tomorrow.** Do not
   double down trying to win it back in one session. Re-enabling after a breaker halt
   to chase losses is the single fastest way to turn a bounded loss into an unbounded
   one.

Your **qty rule and breaker are the only real protection.** Keep them conservative.

---

## 10. Setup checklist

1. Data series: MNQ, **80-tick Renko**, Trading Hours template **`CME US Index
   Futures ETH`**.
2. `Stop loss = 20`, `Profit target = 40` (must equal 1× / 2× the brick).
3. `Filter 1 Pattern`: `1000` (tight) or `10,100,1000` (default). Confirm the
   `[F1 PATTERNS] ACTIVE` line in the diag log.
4. `Base quantity`, and if using the ratchet: `Enable Qty Increment = true`, verify
   `[QTY RULE] ACTIVE`.
5. `Max Real Loss In A Row`: set to your true per-session pain limit.
6. Trading hours window to taste.
7. **Start with `Enable Real Order = false`** and watch `WOULDBE_TRADE` rows for a
   session before going live.
8. Flip `Enable Real Order = true` only when you have eyeballed a setup you want to
   trade, then honor Section 9.

---

## 11. Reading the log

CSV columns: `timestamp, bar_num, side, quantity, entry_price, exit_price,
realized_pnl, win_loss_bit, rawString, filter1Outcome, realTradeOutcome`.

Common `side` values:

- `FAKE_Short` / `FAKE_Long` — ordinary observation brick (`win_loss_bit` = raw brick bit).
- `WOULDBE_TRADE` — a brick where the pipeline fired (F1 matched).
- `Short_ENTRY` / `Long_ENTRY` — real entry submitted (carries the fill price).
- `Short` / `Long` — real trade **close** row. **Only these are read back as real
  outcomes**; `win_loss_bit` here is the real win(1)/loss(0).
- `OBS_OUTSIDE_HOURS` / `OBS_ACCOUNT_BUSY` / `OBS_QTY_SKIP` — a guard suppressed the trade.

A separate `*-diagLog.csv` holds the human-readable pipeline trace, including the
`[F1 PATTERNS]`, `[QTY RULE]`, `[PIPELINE]`, and breaker lines.

---

*Applies to the Layer 1 Renko Transit-repeat files only. Other layers and the
fixed-slice strategies are unchanged.*
