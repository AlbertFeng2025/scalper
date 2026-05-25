# scalper_QEMA_Order v1.0.0 — Design Specification

**Status:** Spec for review. No code written yet.
**Author:** Albert Feng / Drafted with help from Claude
**Architecture:** Single-file unified strategy, copied from `scalper_MACD_Order v1.0.1`. Event generator swapped from MACD signal-line cross → Qualified EMA cross with slope filter. Alert subsystem unchanged. Order subsystem unchanged (including v1.0.1 force-flat fill recognition).

---

## 1. Overview

`scalper_QEMA_Order` is a long/short Qualified-EMA-cross strategy. Same three-subsystem architecture as the MACD strategy:

1. **Event Generator (QEMA-specific):** detects EMA crossovers, applies slope qualification (BOTH EMAs must be sloping in the cross direction beyond their respective thresholds), watches each qualified cross over a pending window, and produces a `1` (would-trade profitable) or `0` (would-trade lost) bit per resolved event.
2. **Alert Subsystem (universal):** accumulates bits into `QualifiedEMAOutcomeString`. Suffix-matches against comma-separated `AlertPattern` list (boolean OR). Fires alerts. Maintains `isAlerted` boolean.
3. **Order Subsystem (universal, gated by `EnableOrders`):** when `isAlerted` is true and a new qualified cross opens a trade, places real order with server-side bracket. Includes v1.0.1 force-flat fill recognition.

The math (two EMAs, slope calc, cross detection) is **inlined** in the strategy. No dependency on the external `QualifiedEMA_CrossOver` indicator file. You can still add that indicator to the chart manually for the visual arrows — both will agree because they use the same math.

---

## 2. The fundamental model

**Two states: Free or Pending.**

### Free state
Strategy watches for a qualified EMA cross. When one occurs and ALL gates pass:
- Trading hours: bar time is within `[start, end]` NY time window
- Direction: cross direction is allowed by `TradeDirection` (LongOnly accepts only golden, ShortOnly accepts only death, Both accepts both)
- No external position: no position on this instrument in this account (from any source — manual, this strategy, other strategy)

→ Open a trade. Transition to Pending.

### Pending state
Strategy watches for **any** of 5 close events. Whichever fires first ends the trade:

| Event | Outcome bit | Resolution type |
|---|---|---|
| Stop hit | 0 | STOP |
| Target hit | 1 | TARGET |
| Hold window expired | 1 if Close > Entry (long) / Close < Entry (short), else 0 | WINDOW_WIN / WINDOW_LOSS |
| Trading-hours end | Same rule | HOURS_WIN / HOURS_LOSS |
| New qualified EMA cross | Same rule | NEW_QEMA_WIN / NEW_QEMA_LOSS |

After close:
1. Append bit to `QualifiedEMAOutcomeString`
2. Post-alert capture if pending captures > 0
3. Re-evaluate `isAlerted` (boolean OR over `AlertPattern` list against tail)
4. Fire alert if AlertPattern just matched (sound + diamond + alert CSV row)
5. Write the cross row to occ CSV
6. If `EnableOrders=true` and a live order is open, force-flat at market via `ExitLong/ExitShort`
7. Return to Free

The cross that closed the trade is **consumed** — it doesn't open a new trade on the same bar. The next qualified cross (a different one) is the next entry candidate.

### Cross-as-killer is just another event

Conceptually, when a position is open the strategy is watching for "any event that closes the trade." Stop hits, target hits, window expiry, hours end, AND a new qualified cross are all in the same category. The new cross has no special status — it's just one of the things that can close a trade.

### Key rule for the bit stream

`isAlerted` is NOT a gate for opening would-trades. The would-trade math runs for **every qualified cross that passes hours/direction/position gates**. This keeps `QualifiedEMAOutcomeString` growing whether you're alerted or not — so the alert state can flip back to true after a string of losses. `isAlerted` only gates live orders (when `EnableOrders=true`).

---

## 3. Parameters (in NinjaScript UI order)

### Group 1: EMA Source

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `PeriodEMA_A` | int | 8 | 1-500 | Fast EMA period |
| `PeriodEMA_B` | int | 30 | 1-500 | Slow EMA period |
| `SlopePeriod` | int | 5 | 1-50 | Bars to look back for slope: `slope = EMA[0] − EMA[SlopePeriod]` |
| `SlopeThresholdFast` | double | 0.25 | 0.0-100.0 | Min absolute slope (points) for Fast EMA to qualify cross |
| `SlopeThresholdSlow` | double | 0.05 | 0.0-100.0 | Min absolute slope (points) for Slow EMA to qualify cross |

Qualification rule (matches the `QualifiedEMA_CrossOver` indicator exactly):
- **Golden cross qualified** when `CrossAbove(emaA, emaB)` AND `slopeA > +SlopeThresholdFast` AND `slopeB > +SlopeThresholdSlow`
- **Death cross qualified** when `CrossBelow(emaA, emaB)` AND `slopeA < −SlopeThresholdFast` AND `slopeB < −SlopeThresholdSlow`

Set `SlopeThresholdFast = 0` AND `SlopeThresholdSlow = 0` for "pure crossover, no slope filter" mode (equivalent to default behavior with all crosses qualifying as long as cross math is right).

### Group 2: Direction & Outcome

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `TradeDirection` | enum | LongOnly | LongOnly / ShortOnly / Both | Which qualified crosses open trades |
| `EventWindowBars` | int | 30 | 1-500 | Max bars to hold a trade. After this many bars, force-resolve by Close vs Entry. |
| `StopPoints` | double | 20.0 | 0.5-500.0 | Stop distance in points from entry |
| `TargetPoints` | double | 50.0 | 0.5-500.0 | Target distance in points from entry |

### Group 3: Trading Hours (NY)

Same as MACD. Defaults 09:30 – 16:00 NY.

### Group 4: Alert Subsystem

Same as MACD. Default `AlertPattern = "01"`.

### Group 5: Order Execution

Same as MACD. Default `EnableOrders = false`, `MaxConsecutiveLosses = 3`.

### Group 6: Visuals

| Name | Type | Default | Description |
|---|---|---|---|
| `EnableChartMarkers` | bool | true | Master toggle |
| `ShowOutcomeLabels` | bool | true | W / L labels |
| `AutoPlotEMAs` | bool | true | Auto-add Fast EMA and Slow EMA to the price panel using strategy's parameters |

(Note: no `AutoPlotATR` — no ATR in this strategy.)

### Group 7: Logging

`AuditLogPath` (default `C:\temp`). Same as MACD.

### Group 8: Advanced

`MaxBitsKept` (default 5000). Same as MACD.

---

## 4. CSV files

Three files. Same architecture as MACD, with name prefix `scalper_QEMA_`:

- `scalper_QEMA_occ.csv` — per-cross-event row
- `scalper_QEMA_alert.csv` — per-alert row
- `scalper_QEMA_orders.csv` — per-order-event row (only when `EnableOrders=true`)

### 4a. `scalper_QEMA_occ.csv`

One row per **raw EMA cross** (whether qualified, skipped, or no-bit). Columns:

| Column | Notes |
|---|---|
| `CrossTime_NY` | Bar close where cross detected |
| `Direction` | LONG (golden) or SHORT (death) |
| `EventType` | QUALIFIED (opened a trade) / SKIPPED_HOURS / SKIPPED_DIR / SKIPPED_POSITION / SKIPPED_UNQUALIFIED (slope filter failed) |
| `EmaAValueAtCross` | Fast EMA value at cross bar close |
| `EmaBValueAtCross` | Slow EMA value at cross bar close |
| `SlopeA` | Fast EMA slope |
| `SlopeB` | Slow EMA slope |
| `SlopeAQualified` | YES if `|slopeA|` > `SlopeThresholdFast` in cross direction |
| `SlopeBQualified` | YES if `|slopeB|` > `SlopeThresholdSlow` in cross direction |
| `EntryPrice` | Close at cross bar (only for QUALIFIED rows) |
| `StopPrice` | Computed (only for QUALIFIED) |
| `TargetPrice` | Computed (only for QUALIFIED) |
| `ResolutionTime_NY` | When this trade ended (blank for SKIPPED rows) |
| `BarsToResolve` | Bars from open to close |
| `ResolutionType` | STOP / TARGET / WINDOW_WIN / WINDOW_LOSS / HOURS_WIN / HOURS_LOSS / **NEW_QEMA_WIN / NEW_QEMA_LOSS** |
| `ResolutionPrice` | Price at close |
| `MaxFavorable` | Max favorable excursion (price points) during hold |
| `MaxAdverse` | Max adverse excursion (negative number) |
| `OutcomeBit` | 0 or 1, blank for SKIPPED rows |
| `DailyCrossNumber` | All raw crosses today |
| `DailyQualifiedNumber` | QUALIFIED rows only |
| `AlertPattern` | Quoted if contains comma |
| `QualifiedEMAOutcomeString` | Running string |
| `FiredAlertOnThisRow` | YES/NO |
| `CapturedPostAlert` | YES/NO |
| `CapturedBit` | 0 or 1 if captured, else blank |
| `PostAlertOutcomeString` | Running |
| `PendingPostAlertCapturesAfter` | Counter after this row |
| `IsAlertedAfter` | State after this row |
| `OrderStateAfter` | Order subsystem state (Idle/Working/Position) |
| `LiveOrderNumber` | If a live order was placed for this trade; blank otherwise |

### 4b. `scalper_QEMA_alert.csv`

Same columns as MACD's alert CSV. Lists every alert fire.

### 4c. `scalper_QEMA_orders.csv`

Same event types as MACD's orders CSV (including `EXIT_FORCE_FLAT_FILLED` for new-cross / window / hours / desync exits). Plus one new exit-name set for the new-cross-driven force-flat: `ExitNewCross` (long position closed by new cross) and `ExitNewCrossShort` (short position closed by new cross). These are added to the v1.0.1 `isOurForceFlat` recognition block.

---

## 5. Chart visuals

Same as MACD, with these specific items:

| Event | Visual | Notes |
|---|---|---|
| QUALIFIED cross opens trade (long) | Cyan up-triangle below Low | At cross bar |
| QUALIFIED cross opens trade (short) | Yellow down-triangle above High | At cross bar |
| Resolution: target hit | "W" green | At resolution bar |
| Resolution: stop hit | "L" red | At resolution bar |
| Resolution: window/hours expired win | "Ww" light green | At resolution bar |
| Resolution: window/hours expired loss | "Ll" dark red | At resolution bar |
| Resolution: closed by new cross (win) | "Nw" cyan-green | At resolution bar (NEW marker) |
| Resolution: closed by new cross (loss) | "Nl" orange-red | At resolution bar (NEW marker) |
| Alert fires | Magenta diamond + text | At alert bar |
| Live order events | NT default | When EnableOrders=true |

`AutoPlotEMAs=true` adds Fast and Slow EMA lines on the price panel.

The marker letters "Nw" / "Nl" distinguish "trade closed by new cross" from other resolutions, so you can visually scan the chart and see which trades were cut short by an opposing or repeated EMA signal.

---

## 6. Per-bar decision flow (pseudo-code)

```
on each bar close:
    if EnableOrders:
        CheckPositionSync()              # defensive
    DailyResetIfNeeded()
    if EnableOrders:
        OrderSubsystemPerBarTick()       # working state housekeeping, hours-end check

    # --- Update pending trade if any ---
    if state == PENDING:
        bars_watched++
        check STOP first (conservative)  → resolve(0, STOP), state=FREE
        elif check TARGET                → resolve(1, TARGET), state=FREE
        elif hours boundary reached      → resolve(bit, HOURS_*), state=FREE
        elif bars_watched >= WindowBars  → resolve(bit, WINDOW_*), state=FREE

    # --- Detect new qualified cross ---
    cross = detect_raw_cross()           # CrossAbove or CrossBelow
    if cross:
        compute_slope_a, slope_b
        qualified = slope_a beats fast_threshold in cross direction
                   AND slope_b beats slow_threshold in cross direction

        if not qualified:
            log SKIPPED_UNQUALIFIED to CSV
            return

        if state == PENDING:
            # KILLER: close the current trade
            resolve(Close-vs-Entry, NEW_QEMA_*), state=FREE
            # cross is consumed — no new trade opens this bar
            # the trade row in CSV captures this killer event
            return

        # state == FREE — try to open
        run gates: hours, direction, position
        if gate fails → log SKIPPED_HOURS / SKIPPED_DIR / SKIPPED_POSITION
        else → open trade, state=PENDING
            if EnableOrders && isAlerted && !safetyBrakeTripped:
                submit_live_order()
```

Notes:
- Pending close checks are in priority order: STOP > TARGET > HOURS > WINDOW.
- If a new qualified cross fires on the same bar that the stop/target/hours/window already resolved, the cross is a fresh event for entry (state is Free by then). It goes through normal gates.
- If a new qualified cross fires on a bar where the trade was still pending (stop/target/hours/window did NOT fire), the cross is the killer.

This natural priority means **only one role per cross**: it can be a fresh entry OR a killer, never both. And the same bar can have at most one trade-close event.

---

## 7. Differences from `scalper_MACD_Order v1.0.1`

| Item | MACD | QEMA |
|---|---|---|
| Event source | MACD line crosses Signal line | Fast EMA crosses Slow EMA, with slope qualification on both |
| Filter | ATR confirmation | Slope thresholds (built into qualification) |
| Default direction | Both | LongOnly |
| StopPoints default | 20 | 20 |
| TargetPoints default | 20 | 50 (asymmetric) |
| EventWindowBars default | 3 | 30 (10× longer hold) |
| 5th exit trigger ("new qualified cross") | N/A | YES — this is the new behavior |
| Auto-plot indicator | MACD + ATR | Two EMAs |
| Resolution types | TARGET/STOP/WINDOW_*/HOURS_* | Same + NEW_QEMA_WIN/LOSS |
| EventType values | QUALIFIED / SKIPPED_ATR / SKIPPED_HOURS / SKIPPED_DIR / SKIPPED_POSITION / SKIPPED_BUSY | QUALIFIED / SKIPPED_UNQUALIFIED / SKIPPED_HOURS / SKIPPED_DIR / SKIPPED_POSITION (note: no SKIPPED_BUSY — busy crosses become killers, not skips) |
| Order subsystem | v1.0.1 with EXIT_FORCE_FLAT_FILLED handling | Same, plus `ExitNewCross`/`ExitNewCrossShort` added to `isOurForceFlat` list |

Everything else identical.

---

## 8. Init log

When the strategy enables, prints:

```
================================================================
[INIT] scalper_QEMA_Order v1.0.0 at HH:mm:ss.fff
[INIT] Instrument: MNQ JUN26  TickSize: 0.25
[INIT] BarsPeriod: 1 Minute  (any bar type works)
[INIT] EMAs: Fast=8  Slow=30
[INIT] Slope: Period=5  ThresholdFast=0.25  ThresholdSlow=0.05
[INIT] TradeDirection: LongOnly
[INIT] EventWindowBars: 30
[INIT] Stop: 20.00 pt = $40.00 RISK/contract
[INIT] Target: 50.00 pt = $100.00 REWARD/contract
[INIT] Risk:Reward = 1:2.50
[INIT] Trading hours: 09:30 - 16:00 NY
[INIT] AlertPattern (raw): "01"
[INIT] AlertPattern (parsed, 1 pattern(s)): [01]
[VALIDATE] AlertPattern list: no suffix overlaps detected.
[INIT] AlertPattern tip: enter "0, 1" to alert on EVERY qualified cross (no filter).
[INIT] Auto-plot EMAs: true
[INIT] CSV path: C:\temp
[INIT] Daily reset: 9:30 AM NY (DST-safe)
[INIT]
[INIT] ----- ORDER SUBSYSTEM -----
[INIT] EnableOrders = false   ← alert-only mode, NO real orders will be placed.
[INIT] To enable trading: set EnableOrders=true in parameters.
[INIT] STRONGLY RECOMMENDED: run with EnableOrders=false for at least one full
[INIT] session on YOUR setup to validate alert behavior in CSV before enabling.
[INIT] ---------------------------------
[INIT]
[INIT] NOTE: 5th exit trigger — a new qualified cross fired during pending state
[INIT] will close the current trade. The cross is consumed as a killer and does
[INIT] NOT open a new trade on the same bar. The next qualified cross (later)
[INIT] is the next entry candidate.
================================================================
```

---

## 9. Outcome resolution rules (recap)

In Pending state, on each subsequent bar:
- `barsWatched++`
- Compute max favorable / max adverse excursion
- Check stop FIRST (conservative tied-bar rule):
  - LONG: `Low[0] <= pendingStop` → bit=0, STOP
  - SHORT: `High[0] >= pendingStop` → bit=0, STOP
- Check target:
  - LONG: `High[0] >= pendingTarget` → bit=1, TARGET
  - SHORT: `Low[0] <= pendingTarget` → bit=1, TARGET
- Check trading-hours boundary:
  - LONG: bit = (Close[0] > Entry) ? 1 : 0, HOURS_*
  - SHORT: bit = (Close[0] < Entry) ? 1 : 0, HOURS_*
- Check window expiry (barsWatched >= EventWindowBars):
  - Same rule, WINDOW_*

(Cross-as-killer is checked separately — after the above resolution and only if still in Pending state. It uses the same Close-vs-Entry rule for the bit.)

---

## 10. Order subsystem additions (when `EnableOrders=true`)

| Action | Order name | Notes |
|---|---|---|
| Stop hit (broker) | (NT auto-name) | OCO bracket leg |
| Target hit (broker) | (NT auto-name) | OCO bracket leg |
| Trading-hours end exit | "ExitHours" / "ExitHoursShort" | Existing v1.0.1 |
| Hours-end ghost cleanup | "ExitHoursGhost" / "ExitHoursGhostShort" | Existing v1.0.1 |
| Desync force-flat | "ExitDesync" / "ExitDesyncShort" | Existing v1.0.1 |
| Window expiry exit | "ExitWindow" / "ExitWindowShort" | Existing v1.0.1 |
| **NEW: new-cross-driven exit** | **"ExitNewCross" / "ExitNewCrossShort"** | NEW — added to v1.0.1 `isOurForceFlat` block |

All seven names are recognized in `OnExecutionUpdate` for proper state transition to Idle. Window exits, hours exits, desync exits, and new-cross exits do NOT increment `consecutiveLosses` (only genuine STOP fills do).

---

## 11. Daily reset (9:30 NY)

Same as MACD:
- Clear `QualifiedEMAOutcomeString`, `PostAlertOutcomeString`
- Reset all daily counters
- Reset `pendingPostAlertCaptures = 0` and `isAlerted = false`
- DO NOT reset order subsystem state, `consecutiveLosses`, or `safetyBrakeTripped`

---

## 12. Code structure

Single file, three clearly marked sections:

```
// 1. EVENT GENERATOR (QEMA-specific) — between marked comments
//    - Two EMA fields: emaFastIndicator, emaSlowIndicator
//    - Inline slope calculation: emaFast[0] - emaFast[SlopePeriod]
//    - CrossAbove / CrossBelow detection via NT primitives
//    - Slope qualification check
//    - PendingState machine for trade lifecycle
//    - 5 close triggers
//
// 2. ALERT SUBSYSTEM (universal) — unchanged from MACD v1.0.1
//
// 3. ORDER SUBSYSTEM (universal) — copied from MACD v1.0.1
//    - Same SetStopLoss / SetProfitTarget approach (Ticks mode from Points / TickSize)
//    - Same v1.0.1 force-flat fill recognition, plus "ExitNewCross" / "ExitNewCrossShort"
```

Class name: `scalper_QEMA_Order`. Same `OrderEntryType`, `OrderSubState`, `PendingState`, `TradeDirectionMode` enums as MACD.

---

## 13. Validation checklist (user-facing)

Same checklist as MACD before enabling orders, with an additional point:

- [ ] At least 50 QUALIFIED rows in `_QEMA_occ.csv`
- [ ] Spot-check 5 random outcome bits manually against chart
- [ ] Verify SKIPPED rows have correct reasons
- [ ] **NEW: Verify NEW_QEMA_WIN/LOSS rows make sense** — find a trade that was killed by a new cross and confirm the timing matches what you see on the chart (a qualified arrow appeared during the pending period, and the bit reflects Close vs Entry)
- [ ] Verify `IsAlertedAfter` transitions match AlertPattern tail-match by hand
- [ ] Run for at least 1 full session in EnableOrders=false mode
- [ ] Then enable on sim account for at least 1 day
- [ ] Verify orders fire correctly via `_QEMA_orders.csv`
- [ ] Then and only then, go live with real money

---

## 14. Schema version & file naming

- Strategy class: `scalper_QEMA_Order`
- Schema version: `1.0.0`
- CSV files:
  - `scalper_QEMA_occ.csv`
  - `scalper_QEMA_alert.csv`
  - `scalper_QEMA_orders.csv` (only when EnableOrders=true)

---

## 15. Out of scope for v1.0.0

- Auto-plot the Qualified EMA arrows (the existing `QualifiedEMA_CrossOver` indicator can be added to chart manually for this)
- Per-direction outcome strings
- Cooldown after a new-cross-killed trade
- Different stop/target for long vs short
- Same-bar entry after non-cross resolution (this is allowed by default in this spec — see section 6 notes)

---

## 16. Summary of architectural decisions

| Decision | Rationale |
|---|---|
| Inline EMA + slope math (not use external indicator) | Single self-contained file, no external dependency |
| 5th close trigger ("new qualified cross") | Core trading hypothesis: in choppy markets, repeated crosses signal noise; exit early to limit damage |
| New cross is "consumed" as killer | Avoids same-cross-doing-two-jobs ambiguity |
| Cross-as-killer fires only if it's QUALIFIED | Unqualified noise crosses don't randomly close good trades |
| New-cross-killed trades do NOT increment consecutiveLosses | Window/hours/new-cross exits aren't true stop hits; safety brake only counts true stops |
| AsymmetricStop/Target (20/50) | Reflects user's hypothesis that good QEMA setups should run; cuts losers quickly |
| EventWindowBars default 30 | 10× MACD's window — QEMA is a slower-frequency, longer-hold setup |
| Same alert subsystem as MACD | Proven, reusable, identical user experience |
| Default LongOnly | Conservative; user can flip to Both for full functionality |

---

## 17. Ready for code?

If this spec looks right, next step is the code. I'll:

1. Copy `scalper_MACD_Order_v101.cs` to `scalper_QEMA_Order_v100.cs`
2. Swap the event generator section (lines ~620-900) with QEMA logic
3. Add the new "ExitNewCross" / "ExitNewCrossShort" names to the `isOurForceFlat` block
4. Rename CSV file prefixes from `scalper_MACD_` to `scalper_QEMA_`
5. Rename all field/method/CSV references from `MACD` to `QEMA` (e.g. `qualifiedMacdOutcomeString` → `qualifiedEmaOutcomeString`)
6. Rename the class
7. Replace MACD-specific params with QEMA-specific params (Group 1)
8. Update init log

Everything else identical to MACD v1.0.1.

**Speak up now if anything in this spec needs to change before I write code.**
