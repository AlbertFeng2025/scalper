# scalper_MACD_Order v1.0.0 — Design Specification

**Status:** Spec for review. No code written yet.
**Author:** Albert Feng / Drafted with help from Claude
**Architecture:** Single-file unified strategy. Alert subsystem always on; order subsystem gated by `EnableOrders`.

---

## 1. Overview

`scalper_MACD_Order` is a long/short MACD strategy that uses the same three-subsystem architecture as the Renko strategy family:

1. **Event Generator (MACD-specific):** detects MACD signal-line crosses, applies ATR confirmation, watches each confirmed cross over an event window, and produces a `1` (would-trade was profitable) or `0` (would-trade lost) bit per resolved event.
2. **Alert Subsystem (universal):** accumulates bits into `MACDOutcomeString`. Suffix-matches against a comma-separated `AlertPattern` list (boolean OR). Fires alerts. Maintains `isAlerted` boolean state for the order subsystem to read.
3. **Order Subsystem (universal, gated by `EnableOrders`):** when `isAlerted` is true and the next qualified MACD cross arrives, places a real order with server-side bracket. Same gates, desync detection, safety brake, and work-end logic as Renko v1.4.3.

The same architecture used in Renko is reused; only the event generator changes. Future strategies for RSI, VWAP, etc. will copy this file, swap the event-generator block, and ship.

---

## 2. Strategy lifecycle (per cross event)

```
┌─────────────────────────────────────────────────────────────┐
│  Bar N closes                                               │
│   ├─ MACD line crosses Signal line?                         │
│   │   ├─ NO  → ignore                                       │
│   │   └─ YES → check ATR confirmation                       │
│   │             └─ FAIL → log raw cross as SKIPPED_ATR      │
│   │             └─ PASS → check trading hours               │
│   │                        └─ OUT  → log SKIPPED_HOURS      │
│   │                        └─ IN   → check direction filter │
│   │                                   └─ FAIL → SKIPPED_DIR │
│   │                                   └─ PASS → check       │
│   │                                              positions  │
│   │                                              └─ EXIST → │
│   │                                                 SKIPPED │
│   │                                              └─ FLAT →  │
│   │                                                 check   │
│   │                                                 busy    │
│   │                                                 └ BUSY: │
│   │                                                  SKIPPED│
│   │                                                 └ FREE: │
│   │                                                  START  │
│   │                                                  PENDING│
└────────────────────────────────────────────────────────────-┘
                                                       │
                                              QUALIFIED CROSS
                                                       │
        ┌──────────────────────────────────────────────┘
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│  PENDING state:                                             │
│   Entry = Close[N], Stop/Target computed, BarsWatched=0     │
│                                                             │
│   IF EnableOrders=true AND isAlerted=true:                  │
│     Place LIVE ORDER (server-side bracket)                  │
│                                                             │
│   On each subsequent bar:                                   │
│     BarsWatched++                                           │
│     IF stop touched → bit=0, log, return                    │
│     IF target touched → bit=1, log, return                  │
│     IF BarsWatched >= EventWindowBars → resolve by Close:   │
│       LONG:  Close > Entry → bit=1; else bit=0              │
│       SHORT: Close < Entry → bit=1; else bit=0              │
│     IF trading hours ended mid-window → force resolve same  │
│                                                             │
│   IF live order open: force-flat at market                  │
└─────────────────────────────────────────────────────────────┘
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│  Resolved. Append bit to MACDOutcomeString.                 │
│   Check post-alert capture: pendingPostAlertCaptures > 0 ?  │
│     YES → append bit to PostAlertOutcomeString, decrement.  │
│   Check alert: AnyAlertPatternMatchesTail() ?               │
│     YES → FireAlert (sound, diamond, log).                  │
│   Recompute isAlerted.                                      │
│   Log row to CSV.                                           │
└─────────────────────────────────────────────────────────────┘
```

---

## 3. Parameters (in NinjaScript UI order)

### Group 1: MACD Source

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `MACDFast` | int | 12 | 1-200 | MACD fast EMA period |
| `MACDSlow` | int | 26 | 1-500 | MACD slow EMA period |
| `MACDSignal` | int | 9 | 1-200 | MACD signal-line period |
| `ATRPeriod` | int | 14 | 1-500 | ATR period for cross-confirmation filter |
| `CrossToleranceATR` | double | 0.0 | 0.0-10.0 | Cross confirmed when \|MACD-Signal\| > ATR × this value. **0.0 = no filter (pure MACD cross).** |

### Group 2: Direction & Outcome

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `TradeDirection` | enum (LongOnly/ShortOnly/Both) | Both | enum | Which crosses produce events. Both = golden→long, death→short. |
| `EventWindowBars` | int | 3 | 1-50 | Bars after cross detection to watch for stop/target. Window ends at bar N+EventWindowBars. Used by both would-trade math AND live-order force-flat. |
| `StopPoints` | double | 20.0 | 0.5-500.0 | Stop distance in points from entry. |
| `TargetPoints` | double | 20.0 | 0.5-500.0 | Target distance in points from entry. |

### Group 3: Trading Hours (NY)

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `TradingStartHourNY` | int | 9 | 0-23 | Start hour NY time |
| `TradingStartMinuteNY` | int | 30 | 0-59 | Start minute NY time |
| `TradingEndHourNY` | int | 16 | 0-23 | End hour NY time |
| `TradingEndMinuteNY` | int | 0 | 0-59 | End minute NY time |

(No EnableTradingHours toggle — always on. Set 0/0 to 23/59 for 24h.)

### Group 4: Alert Subsystem

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `AlertPattern` | string | `"01"` | 1-200 chars | Comma-separated list of suffix patterns. Each entry 1-10 chars of '0'/'1'. At each MACD outcome append, if ANY pattern matches the tail of MACDOutcomeString, ONE alert fires. Examples: `"01"` (single), `"01, 011, 0111"` (any of three), `"0, 1"` (always match — trade EVERY qualified MACD signal). WARNING: avoid lists where one entry is a suffix of another. |
| `AlertSoundCount` | int | 3 | 1-10 | Total beeps per alert |
| `AlertReminderSecs` | int | 1 | 1-60 | Seconds between beeps |

### Group 5: Order Execution (gated by EnableOrders)

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `EnableOrders` | bool | **false** | — | Master switch. When false, strategy runs alert subsystem only — no real orders. When true, the order subsystem activates. **Default false for safety.** |
| `OrderQuantity` | int | 1 | 1-100 | Contracts per entry |
| `OrderType` | enum (Market/Limit) | Market | enum | Market = immediate fill (may slip). Limit = LimitOffsetPoints below close (long) or above (short). |
| `LimitOffsetPoints` | double | 5.0 | 0-100 | (Limit only) offset from close |
| `LimitUnfilledCancelBars` | int | 1 | 1-20 | (Limit only) cancel after this many bars unfilled. Market orders never cancelled. |
| `MaxConsecutiveLosses` | int | **3** | 1-100 | Halt new orders after this many consecutive stops. Disable+re-enable strategy to reset. **Default 3 (conservative).** |

### Group 6: Visuals

| Name | Type | Default | Description |
|---|---|---|---|
| `EnableChartMarkers` | bool | true | Master toggle for all chart drawings |
| `ShowOutcomeLabels` | bool | true | Show W (win) / L (loss) text at resolution bar |
| `AutoPlotMACD` | bool | true | Automatically add MACD indicator to chart |
| `AutoPlotATR` | bool | true | Automatically add ATR indicator to chart |

### Group 7: Logging

| Name | Type | Default | Description |
|---|---|---|---|
| `AuditLogPath` | string | `C:\temp` | Folder for CSV files (auto-created) |

### Group 8: Advanced

| Name | Type | Default | Range | Description |
|---|---|---|---|---|
| `MaxBitsKept` | int | 5000 | 100-100000 | Max in-memory MACDOutcomeString length before trimming |

---

## 4. CSV files

Three files written to `AuditLogPath`. All timestamps NY time.

### 4a. `scalper_MACD_occ.csv` — per-cross-event row

One row per **raw MACD cross** (accepted or skipped). The `EventType` column distinguishes accepted from skipped crosses.

Header lines (start with `#`):
```
# schema_version=1.0.0
# instrument=MNQ JUN26
# bar_type=Minute / Renko / etc.
# tick_size=0.25
# MACDFast=12  MACDSlow=26  MACDSignal=9
# ATRPeriod=14  CrossToleranceATR=0.0
# TradeDirection=Both
# StopPoints=20  TargetPoints=20  EventWindowBars=3
# TradingHours=09:30-16:00 NY
# AlertPattern=01
# AlertPatternParsed=[01]
# EnableOrders=false
# all timestamps NY time
```

Columns:

| Column | Type | Example | Notes |
|---|---|---|---|
| `CrossTime_NY` | datetime | 2026-05-21 10:15:00 | Bar close where cross detected |
| `Direction` | LONG/SHORT | LONG | Golden=LONG, Death=SHORT |
| `EventType` | string | QUALIFIED / SKIPPED_ATR / SKIPPED_HOURS / SKIPPED_DIR / SKIPPED_POSITION / SKIPPED_BUSY | Why this cross was or wasn't processed |
| `MACDValueAtCross` | double | 12.45 | MACD line at bar close |
| `SignalValueAtCross` | double | 12.10 | Signal line at bar close |
| `Diff` | double | 0.35 | MACD - Signal |
| `ATRAtCross` | double | 5.20 | ATR value at bar close |
| `DiffOverATR` | double | 0.067 | Diff / ATR (filter threshold compare) |
| `EntryPrice` | double | 5800.00 | Close of cross bar (only for QUALIFIED) |
| `StopPrice` | double | 5780.00 | Computed (only for QUALIFIED) |
| `TargetPrice` | double | 5820.00 | Computed (only for QUALIFIED) |
| `ResolutionTime_NY` | datetime | 2026-05-21 10:30:00 | When this cross resolved (blank for SKIPPED) |
| `BarsToResolve` | int | 2 | Bars from cross close to resolution (blank for SKIPPED) |
| `ResolutionType` | string | TARGET / STOP / WINDOW_WIN / WINDOW_LOSS / HOURS_WIN / HOURS_LOSS | How it resolved (blank for SKIPPED) |
| `ResolutionPrice` | double | 5820.00 | Price at resolution (blank for SKIPPED) |
| `MaxFavorable` | double | 22.00 | Max High-Entry seen during window (long) / Entry-Low (short) |
| `MaxAdverse` | double | -8.00 | Min/worst excursion during window |
| `OutcomeBit` | 0/1 | 1 | The bit appended to MACDOutcomeString (blank for SKIPPED) |
| `DailyCrossNumber` | int | 5 | Counter, reset 9:30 NY |
| `DailyQualifiedNumber` | int | 3 | Counter of QUALIFIED only, reset 9:30 NY |
| `MACDOutcomeString` | string | 1011 | Full running string (only changes on QUALIFIED rows) |
| `FiredAlertOnThisRow` | YES/NO | YES | Did this row's bit trigger an alert? |
| `CapturedPostAlert` | YES/NO | YES | Was this row's bit captured for PostAlertOutcomeString? |
| `CapturedBit` | 0/1 | 1 | If captured, the bit captured |
| `PostAlertOutcomeString` | string | 101 | Running |
| `PendingPostAlertCapturesAfter` | int | 0 | |
| `IsAlertedAfter` | YES/NO | NO | State after processing this row |
| `OrderStateAfter` | string | Idle / Working / Position | Order subsystem state (always Idle if EnableOrders=false) |
| `LiveOrderNumber` | int | 7 | Reference to orders.csv (blank if no live order) |

### 4b. `scalper_MACD_alert.csv` — per-alert row

Header lines: same metadata as occ.csv.

| Column | Example |
|---|---|
| `AlertTime_NY` | 2026-05-21 10:30:00 |
| `DailyAlertNumber` | 3 |
| `AlertPattern` | `"01,011,0111"` (quoted if contains comma) |
| `CurrentPrice` | 5820.00 |
| `MACDOutcomeString` | 100111 |
| `PostAlertOutcomeString` | 11 |
| `PendingPostAlertCapturesAfter` | 1 |
| `IsAlertedAfter` | YES |
| `OrderStateAtAlert` | Idle |

### 4c. `scalper_MACD_orders.csv` — per-order-event row (only written when `EnableOrders=true`)

Same format as Renko v1.4.3's orders.csv. Event types:

```
SUBMIT_MARKET / SUBMIT_LIMIT      - entry submitted
FILLED_BRACKET_ATTACH             - entry filled, server-side bracket active
EXIT_STOP / EXIT_TARGET           - bracket leg filled
EXIT_WINDOW_EXPIRED               - EventWindow timeout, market exit
CANCEL_UNFILLED                   - limit entry cancelled
CANCEL_HOURS                      - entry cancelled at trading-hours end
FORCEFLAT_HOURS                   - position closed at trading-hours end
FORCEFLAT_HOURS_GHOST             - ghost position closed at hours end
FORCE_RESET_STUCK                 - limit grace expired, callback never arrived
DESYNC_FORCE_FLAT                 - state=Idle but broker has position
DESYNC_RESET_IDLE                 - state=Working/Position but broker Flat
MARKET_HEARTBEAT_WARN             - market order no fill confirm after 30s
SAFETY_BRAKE_TRIPPED              - consecutive losses limit reached
REJECTED                          - entry order rejected
```

---

## 5. Chart visuals

When `EnableChartMarkers = true`:

| Event | Visual | Notes |
|---|---|---|
| Qualified MACD cross detected (long) | Cyan up-triangle below Low | At cross bar |
| Qualified MACD cross detected (short) | Yellow down-triangle above High | At cross bar |
| Skipped cross (any reason) | Small grey dot | Diagnostic; can be hidden via `ShowSkippedMarkers` if added later |
| Resolution: target hit | "W" in green | At resolution bar, above High |
| Resolution: stop hit | "L" in red | At resolution bar, below Low |
| Resolution: window/hours expired win | "Ww" in light green | At resolution bar (different from clean target hit) |
| Resolution: window/hours expired loss | "Ll" in dark red | |
| Alert fires | Magenta diamond above High + "ALERT #N\nAP=..." text | At alert bar |
| Live order placed | Standard NT order arrows (default behavior) | NT default |
| Live order filled | Standard NT fill markers | NT default |

`AutoPlotMACD=true` adds MACD indicator panel below price using strategy's parameters.
`AutoPlotATR=true` adds ATR indicator panel below MACD using strategy's parameter.

---

## 6. Gate logic — when does a cross become QUALIFIED?

All must be true (in order; first failure determines `EventType` skip reason):

1. **MACD cross detected** (golden: MACD line crosses above Signal; death: crosses below).
2. **ATR confirmation:** `|MACD - Signal| > ATR × CrossToleranceATR`. If `CrossToleranceATR = 0.0`, this gate always passes.
3. **Trading hours:** current NY bar time is within `[TradingStartHourNY:TradingStartMinuteNY, TradingEndHourNY:TradingEndMinuteNY]`.
4. **Direction filter:** if `TradeDirection=LongOnly`, only golden crosses pass; if `ShortOnly`, only death crosses pass; `Both` passes everything.
5. **No external position:** `Account.Positions` enumeration shows NO position on this instrument (regardless of source — strategy, manual, other strategy).
6. **Not busy:** the strategy is not currently watching a previous pending cross.

All checks identical for the would-trade outcome math AND the live-order placement decision (when `EnableOrders=true`). This is critical — your "would trade" results predict your live performance because the same filters apply.

---

## 7. Outcome resolution rules (would-trade math)

Once a cross is QUALIFIED, enter PENDING state:
- `Entry = Close[N]` (bar where cross was detected)
- `Stop = Entry - StopPoints` (long) or `Entry + StopPoints` (short)
- `Target = Entry + TargetPoints` (long) or `Entry - TargetPoints` (short)
- `BarsWatched = 0`

On each subsequent bar:
- `BarsWatched++`
- **Check stop FIRST** (conservative tied-bar rule):
  - LONG: if `Low[0] <= Stop` → bit=0, resolution=STOP
  - SHORT: if `High[0] >= Stop` → bit=0, resolution=STOP
- Then check target:
  - LONG: if `High[0] >= Target` → bit=1, resolution=TARGET
  - SHORT: if `Low[0] <= Target` → bit=1, resolution=TARGET
- If `BarsWatched >= EventWindowBars`:
  - LONG: bit = (Close[0] > Entry) ? 1 : 0; resolution=WINDOW_WIN or WINDOW_LOSS
  - SHORT: bit = (Close[0] < Entry) ? 1 : 0
- If current bar's NY time is at or past `TradingEndHourNY:TradingEndMinuteNY`:
  - Resolve same as window expiry; resolution=HOURS_WIN or HOURS_LOSS

Once resolved: append bit to MACDOutcomeString, log row, return to FREE state, ready for next cross.

---

## 8. Order subsystem (when `EnableOrders=true`)

Identical to Renko v1.4.3 architecture. Differences:

| Item | Renko v1.4.3 | MACD v1.0.0 |
|---|---|---|
| Stop/target units | Bricks × brick_ticks | Points (direct) |
| `SetStopLoss` call | `CalculationMode.Ticks` | `CalculationMode.Price`, computed from fill ± StopPoints |
| `SetProfitTarget` call | Same | Same |
| Direction | Long-only | LongOnly/ShortOnly/Both per parameter |
| Event-window force-flat | No equivalent | NEW: at end of bar N+EventWindowBars, if position still open, ExitLong/ExitShort at market |

Order flow (long example, EnableOrders=true, isAlerted=true, all gates passed, MACD golden cross detected at bar N):

1. **Pre-set bracket** via `SetStopLoss(CalculationMode.Price, fillPrice − StopPoints)` and `SetProfitTarget(CalculationMode.Price, fillPrice + TargetPoints)` — but we don't have fillPrice yet, so use `CalculationMode.Ticks` with `StopPoints/TickSize` and `TargetPoints/TickSize` (NT computes from fill).
2. **Submit entry** via `EnterLong(OrderQuantity, "EntryV100")` (Market) or `EnterLongLimit(...)` (Limit).
3. **OnExecutionUpdate** fires when entry fills. State: Working → Position. Bracket already attached server-side.
4. **Watch bars:**
   - Stop fill (broker) → state Position → Idle, consecutiveLosses++, possibly trigger safety brake.
   - Target fill (broker) → state Position → Idle, consecutiveLosses=0.
   - End of bar N+EventWindowBars with no fill → submit `ExitLong("ExitWindow", "EntryV100")` at market. Bracket auto-cancels. State eventually returns to Idle via OnExecutionUpdate.
   - Trading-hours end reached with position still open → submit market exit, same path.
5. **The bit appended to MACDOutcomeString reflects the WOULD-TRADE math, not the live result.** (Spec decision: would-trade math and live-order P&L are tracked separately. Live P&L is recoverable from orders.csv.)

### Phase 2 desync handling

Same as Renko v1.4.3:
- `CheckPositionSync()` at top of every OnBarUpdate
- Throttled logging (30s for ghost, 5s for stale)
- Force-flat or reset-to-idle as appropriate

### Safety brake

`MaxConsecutiveLosses` default = **3** (more conservative than Renko's 99). After 3 consecutive stops, `safetyBrakeTripped = true`, no new orders. Audible warning. Manual reset via disable+re-enable strategy.

---

## 9. Sounds

| Event | Sound file | When |
|---|---|---|
| Alert fires | Alert1.wav | Always (whether EnableOrders or not) |
| Live entry filled | Alert4.wav | EnableOrders=true |
| Target hit | Boxing Bell.wav | EnableOrders=true |
| Stop hit | Glass Break.wav | EnableOrders=true |
| Order cancelled (unfilled) | Alert3.wav | EnableOrders=true |
| Force-flat (hours or window) | Alert2.wav | EnableOrders=true |
| Connection lost | Alert2.wav | EnableOrders=true |
| Safety brake tripped | Alert2.wav | EnableOrders=true |

---

## 10. Daily reset (9:30 AM NY)

At 9:30 NY transition:
- Clear `MACDOutcomeString` and `PostAlertOutcomeString`
- Reset all daily counters (cross #, alert #, order #)
- Reset `pendingPostAlertCaptures = 0` and `isAlerted = false`
- **DO NOT** reset order subsystem state (a position carrying across 9:30 stays managed)
- **DO NOT** reset `consecutiveLosses` or `safetyBrakeTripped` (those persist across days)

---

## 11. Init log at strategy enable

When the strategy is enabled, print to NinjaScript Output:

```
================================================================
[INIT] scalper_MACD_Order v1.0.0 at HH:mm:ss.fff
[INIT] Instrument: MNQ JUN26  TickSize: 0.25
[INIT] Bar type: 5 Minute   (any bar type works)
[INIT] MACD: Fast=12  Slow=26  Signal=9
[INIT] ATR: Period=14  CrossToleranceATR=0.00  (0 = pure MACD, no filter)
[INIT] TradeDirection: Both
[INIT] EventWindowBars: 3
[INIT] Stop: 20.00 pt = $40.00 RISK/contract  (MNQ point value $2.00)
[INIT] Target: 20.00 pt = $40.00 REWARD/contract
[INIT] Risk:Reward = 1:1.00
[INIT] Trading hours: 09:30 - 16:00 NY
[INIT] AlertPattern (raw): "01"
[INIT] AlertPattern (parsed): [01]
[VALIDATE] AlertPattern list: no suffix overlaps detected.
[INIT] AlertPattern tip: enter "0, 1" to alert on EVERY qualified MACD signal.
[INIT] CSV path: C:\temp
[INIT]
[INIT] ----- ORDER SUBSYSTEM -----
[INIT] EnableOrders = false   ← alert-only mode, NO real orders will be placed.
[INIT] To enable trading: set EnableOrders=true in parameters.
[INIT] STRONGLY RECOMMENDED: run with EnableOrders=false for at least one full
[INIT] session on YOUR setup to validate alert behavior in CSV before enabling.
[INIT] ---------------------------------
[INIT] Daily reset: 9:30 AM NY (DST-safe)
================================================================
```

When `EnableOrders=true`, the order subsystem block expands to show sizing summary, safety layers, and order sounds (same format as Renko v1.4.3).

---

## 12. Code structure (single file)

```
namespace NinjaTrader.NinjaScript.Strategies
{
    public class scalper_MACD_Order : Strategy
    {
        // Enums
        public enum TradeDirectionMode { LongOnly, ShortOnly, Both }
        public enum OrderEntryType { Market, Limit }
        private enum OrderSubState { Idle, Working, Position }
        private enum PendingState { Free, Pending }

        #region Fields
        // ----- ALERT SUBSYSTEM STATE (universal) -----
        private StringBuilder macdOutcomeString;
        private StringBuilder postAlertOutcomeString;
        private int pendingPostAlertCaptures;
        private bool isAlerted;
        private List<string> compiledAlertPatterns;
        // daily counters
        private int dailyCrossNumber, dailyQualifiedNumber, dailyAlertNumber, dailyOrderNumber;
        // daily reset
        private DateTime currentTradingDateNy;
        private TimeZoneInfo nyTz;
        private bool configValid;

        // ----- EVENT GENERATOR STATE (MACD-specific) -----
        private MACD macdIndicator;
        private ATR  atrIndicator;
        // pending cross
        private PendingState pendingState;
        private bool pendingIsLong;
        private double pendingEntry, pendingStop, pendingTarget;
        private int pendingBarsWatched;
        private DateTime pendingCrossTime;
        private double pendingMaxFavorable, pendingMaxAdverse;
        private int pendingDailyCrossNumber;
        // link to live order (if any)
        private int pendingLiveOrderNumber;

        // ----- ORDER SUBSYSTEM STATE (universal, used iff EnableOrders) -----
        // ... copied verbatim from Renko v1.4.3 with rename Brick→Point ...
        #endregion

        // ----- LIFECYCLE -----
        protected override void OnStateChange() { ... }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (!configValid) return;

            if (EnableOrders) CheckPositionSync();         // order safety, every bar
            DailyResetCheckIfNeeded();
            UpdatePendingCrossIfAny();                     // resolves stop/target/window
            CheckForNewMACDCross();                        // detects + gates + starts pending
        }

        // ----- EVENT GENERATOR (MACD-SPECIFIC) -----
        private void CheckForNewMACDCross() { ... }
        private void UpdatePendingCrossIfAny() { ... }
        private bool CheckAllGatesAndLog(...) { ... }
        private void StartPending(...) { ... }
        private void ResolvePending(int bit, string resolutionType, double resolutionPrice) { ... }

        // ----- ALERT SUBSYSTEM (UNIVERSAL — copy/paste-able to other strategies) -----
        private List<string> ParseAlertPatternList(string raw) { ... }
        private void CheckForSuffixOverlaps() { ... }
        private bool AnyAlertPatternMatchesTail() { ... }
        private void OnNewOutcomeBit(int bit, ...)
        {
            // append to string, post-alert capture, alert check, recompute isAlerted
            // if EnableOrders && isAlerted: hand off to order subsystem on next qualifying cross
        }
        private void FireAlert() { ... }

        // ----- ORDER SUBSYSTEM (UNIVERSAL — copy/paste-able) -----
        private void TryEnterLiveOrder(bool isLong, double currentPrice, DateTime barTimeNy) { ... }
        private void CheckPositionSync() { ... }
        private void HandleHoursEndForOrders() { ... }
        private void HandleWindowExpiredForOrders() { ... }
        protected override void OnOrderUpdate(...) { ... }
        protected override void OnExecutionUpdate(...) { ... }
        protected override void OnConnectionStatusUpdate(...) { ... }
        private void ResetOrderSubsystemToIdle(string reason) { ... }

        // ----- HELPERS -----
        private bool IsWithinTradingHours(DateTime ny) { ... }
        private bool IsBeyondTradingHoursEnd(DateTime ny) { ... }
        private bool HasAnyPositionOnInstrument() { ... }   // Account.Positions enumeration
        private void PlayOrderSound(string wav, string label) { ... }

        // ----- CSV WRITERS -----
        private void WriteCrossRow(...) { ... }            // occ.csv
        private void WriteAlertRow() { ... }                // alert.csv
        private void WriteOrderRow(...) { ... }             // orders.csv (only when EnableOrders)

        // ----- PROPERTIES -----
        // ... all NinjaScriptProperty params, grouped per section 3 ...
    }
}
```

Clear markers in code:

```csharp
// =============================================================
//   EVENT GENERATOR — MACD-SPECIFIC CODE BEGINS HERE
//   To adapt this strategy to another indicator (RSI, VWAP, etc.):
//     1. Replace the methods in this section with new event-detection logic.
//     2. The new code must:
//        a. Detect events (your indicator's signal).
//        b. Apply gates (ATR or whatever — calling CheckAllGatesAndLog()).
//        c. Watch each event over EventWindowBars (your race-resolution logic).
//        d. Call OnNewOutcomeBit(bit, ...) when resolved.
//        e. If EnableOrders && isAlerted, call TryEnterLiveOrder(...) at event start.
//     3. Leave the ALERT SUBSYSTEM and ORDER SUBSYSTEM sections unchanged.
//     4. Update class name, Name property, CSV file prefixes.
// =============================================================
```

---

## 13. Validation checklist (user-facing, included in user guide)

Before setting `EnableOrders=true`:

- [ ] At least 50 QUALIFIED rows in `_MACD_occ.csv`
- [ ] Spot-check 5 random outcome bits manually against chart (verify the math)
- [ ] Verify SKIPPED rows have correct reasons (`SKIPPED_HOURS` rows outside hours, etc.)
- [ ] Verify `IsAlertedAfter` transitions match AlertPattern tail-match by hand on a few rows
- [ ] Run for at least 1 full session in EnableOrders=false mode
- [ ] Then enable on sim account for at least 1 day
- [ ] Verify orders fire correctly via `_MACD_orders.csv`
- [ ] Verify CSV order numbers match NT's Orders tab
- [ ] Then and only then, go live with real money

---

## 14. Schema version & file naming

- Strategy class: `scalper_MACD_Order`
- Schema version: `1.0.0` (fresh, not continuing Renko numbering)
- CSV files:
  - `scalper_MACD_occ.csv`
  - `scalper_MACD_alert.csv`
  - `scalper_MACD_orders.csv` (only when EnableOrders=true)

When future revisions are needed:
- v1.0.x = bug fixes only
- v1.1.x = feature additions backward-compatible (e.g. new optional parameter)
- v2.0.x = breaking changes (e.g. CSV column changes)

---

## 15. Out of scope for v1.0.0

These can be added in later versions. Listed here so we don't forget:

- [ ] **Short side EMA filter** — currently shorts pass the same ATR check as longs. Some traders want a separate "bearish trend filter" for shorts. Defer.
- [ ] **Multiple symbols** — strategy is single-instrument as written. Multi-symbol = separate instance per chart.
- [ ] **PostAlertOutcomeString analysis tools** — leave to user post-processing in pandas.
- [ ] **Live order EventWindow override** — currently same param. Could split later if users want different paper vs live windows.
- [ ] **Skipped-cross chart markers toggle** — could add `ShowSkippedMarkers` bool.
- [ ] **Per-direction outcome strings** — currently one shared string. Could add per-direction strings for analysis.
- [ ] **Custom sound per alert pattern** — single Alert1.wav for all alerts. Could add per-pattern sounds later.
- [ ] **MACD histogram filter** — could require histogram direction confirms cross direction. Defer.

---

## 16. Summary of architectural decisions

| Decision | Rationale |
|---|---|
| Single unified file (not phase 1 + phase 2 files) | Less duplicated code, mature pattern from Renko v1.4.3 |
| `EnableOrders=false` default | Safety; users opt in to real trading |
| `MaxConsecutiveLosses=3` default | More conservative than Renko's 99; new untested code |
| `CrossToleranceATR=0.0` default | Out-of-box behavior is pure MACD (most familiar) |
| Three CSV files (occ, alert, orders) | Same as Renko v1.4.3, allows independent analysis |
| Single MACDOutcomeString (not per-direction) | Matches user's design philosophy: "the bit means MACD was right" |
| EventWindowBars applies to both would-trade and live | Single source of truth, paper-matches-live |
| Trading-hours boundary forces resolution | Consistency with live behavior (no overnight) |
| AlertPattern comma-list | Same v1.3.3 design, well-tested in Renko |
| Auto-plot MACD and ATR | User sees the actual indicators driving decisions |
| Same alert subsystem code as Renko v1.4.4 | Verified, reused without modification (universal template) |
| Same order subsystem code as Renko v1.4.3 | Verified, reused with Brick→Point substitution |
| MACD-specific event generator section clearly marked | Future strategies copy this file, swap only that section |

---

## 17. Ready for code?

If this spec looks right, next step is the actual C# file. I'll write it as one pass — same scope as Renko v1.4.4 (~1700 lines). Then we follow the test discipline:

1. Compile in NT, no errors
2. Load on chart with `EnableOrders=false`, verify init log
3. Let it run for several MACD crosses, inspect CSV
4. Spot-check outcome bits by eye
5. Verify alert behavior matches AlertPattern config
6. Enable on sim, verify orders
7. Iterate if issues found

**Speak up now if anything in this spec needs to change before I write code.**
