# scalper_RenkoStringPatternAlert — User Guide

**Strategy:** `scalper_RenkoStringPatternAlert`
**Version:** 1.0
**Platform:** NinjaTrader 8
**Mode:** Notification only — places NO orders
**Intended Chart:** Renko, 20-tick / 5-point bricks on MNQ (configurable)

---

## Table of Contents

1. [What This Strategy Does](#1-what-this-strategy-does)
2. [Core Concepts](#2-core-concepts)
3. [The Two-Layer System Explained](#3-the-two-layer-system-explained)
4. [Prerequisites](#4-prerequisites)
5. [Chart Setup](#5-chart-setup) ⚠️ **READ THIS — brick size is set in the chart, not the strategy**
6. [Installation](#6-installation)
7. [Adding to a Chart](#7-adding-to-a-chart)
8. [Parameter Reference](#8-parameter-reference)
9. [Detailed Examples](#9-detailed-examples)
10. [Output Window & CSV Files](#10-output-window--csv-files)
11. [Use-Case Recipes](#11-use-case-recipes)
12. [Troubleshooting](#12-troubleshooting)

---

## 1. What This Strategy Does

`scalper_RenkoStringPatternAlert` is a **notification-only** strategy. It watches Renko bricks for a user-defined pattern, checks whether a follow-up pattern materializes, tracks success/failure streaks, and **beeps** when conditions you've configured are met.

It does **not** place any orders. When the alert fires, you (the human) decide whether to trade based on what you see on the chart.

The strategy is most useful for:

- **Mean-reversion hunting:** "After 2 false starts, the next time pattern X appears, I want a heads-up"
- **Pattern frequency analysis:** "How often does sequence X appear in a trading day, and what comes after?"
- **Continuation signals:** "After 5 consecutive greens, alert me on every additional green"

---

## 2. Core Concepts

### Bricks → Bits

Every closed Renko brick is one of two colors:

- **Green brick** (Close > Open) = bit `1`
- **Red brick** (Close < Open) = bit `0`

As bricks form, the strategy maintains a running **bit string** for the trading day:

```
Time:    9:30   9:32   9:34   9:37   9:40   9:42   9:45
Brick:   green  green  red    green  green  green  red
Bit:     1      1      0      1      1      1      0
String:  "1" → "11" → "110" → "1101" → "11011" → "110111" → "1101110"
```

### Pattern Match

The strategy looks for a specific **bit pattern** at the end of the bit string. The pattern is a string you configure, e.g. `"011"`, `"100"`, `"0000"`.

After each new brick, the strategy checks: **do the last N bits exactly equal my pattern?**

```
PatternToMatch = "011"

Bit string ends in...    Match?
"...011"                 ✓ yes
"...010"                 ✗ no
"...111"                 ✗ no
"...0011"                ✓ yes (last 3 are "011")
```

When a match is found, that's an **occurrence**.

### Daily Reset

All counters and the bit string reset at **9:30 AM New York time** every day. Uses `TimeZoneInfo` so EST/EDT and DST transitions are handled automatically. Yesterday's bricks don't influence today's pattern detection.

---

## 3. The Two-Layer System Explained

This is the heart of the strategy. Two layers work together.

### Layer 1 — Pattern Detection & Outcome

For each detected occurrence, the strategy then watches the **next M bricks** (where M = length of `FollowUpPattern`) and compares them **bit-by-bit** to your `FollowUpPattern`.

- **Exact match** → **SUCCESS (S)**
- **Any mismatch** → **FAILURE (F)**

Example with `PatternToMatch = "011"`, `FollowUpPattern = "11"`:

```
Bit sequence: 0 1 1 1 1

Brick 3 closes → last 3 bits = "011" → Pattern detected. Occurrence pending.
Brick 4 closes → 1st follow-up bit = 1. Expected = 1. ✓
Brick 5 closes → 2nd follow-up bit = 1. Expected = 1. ✓
  → All follow-up bricks matched → SUCCESS
```

Example failure:

```
Bit sequence: 0 1 1 1 0

Brick 3 closes → pattern "011" detected.
Brick 4 closes → 1st follow-up = 1. ✓
Brick 5 closes → 2nd follow-up = 0. Expected = 1. ✗
  → Mismatch → FAILURE
```

Every resolved occurrence is one row in the **Occurrences CSV** with its S/F outcome.

### Layer 2 — Failure-Streak Alerter

Layer 1 produces a stream of S's and F's, one per resolved occurrence. Layer 2 watches that stream.

**The rule:**

- Count consecutive F's after the most recent S
- When the count reaches `FailuresToArm`, **ALERT FIRES** (3 beeps by default + chart marker + CSV row)
- Counter only resets to 0 when an S occurs
- Alert fires **once** per F-streak that reaches the threshold — does not re-fire on further failures until reset

**Example with FailuresToArm = 2:**

```
Outcome stream:     S  S  F  F  F  F  S  S  F  S
Failure count:      0  0  1  2  3  4  0  0  1  0
Alert fires on:           ↑
                     2nd consecutive F triggers the alert.
                     3rd and 4th F do NOT trigger another alert (already fired).
                     S resets to 0.
                     Next streak starts at the next F.
```

### Special Case: FailuresToArm = 0

When K=0, the meaning changes: alert fires immediately on **every pattern DETECTION**, before the outcome is known. Use this when you want to see every match regardless of streaks.

---

## 4. Prerequisites

- **NinjaTrader 8** installed
- A working data connection
- Familiarity with adding strategies to charts
- A **Renko chart** at the brick size you want (this is set in the chart, NOT in the strategy — see Section 5)

---

## 5. Chart Setup

> ⚠️ **CRITICAL:** Brick size is a **CHART property**, not a strategy parameter. The strategy reads whatever bricks the chart produces. If you set up the wrong chart, the strategy will still run but won't mean what you intend.

### How to create a 20-tick Renko chart for MNQ

1. **New → Chart**
2. **Instrument:** `MNQ` (or `MNQ 12-26` for a specific contract)
3. **Type:** select **Renko** from the dropdown
4. **Value (ticks):** enter **`20`**
5. (Other Renko options like reversal/range stay at defaults)
6. Click **OK**

### Verify your setup

For MNQ:

- Tick size = 0.25 points
- 20 ticks × 0.25 = **5 points per brick** ✓
- Each brick = $10 of price movement on MNQ (5 pts × $2/pt)

For NQ:

- Tick size = 0.25 points (same)
- 20 ticks × 0.25 = 5 points
- Each brick = $100 on NQ (5 pts × $20/pt) — that's 10× more capital risk

### Naming charts to avoid mistakes

Save the chart layout with a clear name:

- **File → Save As Chart Template → "MNQ_Renko_20T_Patterns"**

Then to reopen: **New → Chart → Load Template** → pick by name.

### Adding to multiple charts

You can run this strategy on different charts simultaneously with different brick sizes and different pattern parameters. Each instance is independent. Useful for:

- Fast scalping chart: 10-tick bricks, looking for `"011"`
- Slower swing chart: 50-tick bricks, looking for `"0000"`

Both run side-by-side with their own CSV files (if you change `AuditLogPath` per chart) — though the default behavior is shared CSVs which mix events from all charts. Either is fine; pick what works for your analysis.

---

## 6. Installation

### Step 1 — Open the NinjaScript Editor

Press **F11** or **New → NinjaScript Editor**.

### Step 2 — Add the strategy file

Copy `scalper_RenkoStringPatternAlert.cs` into:

```
Documents\NinjaTrader 8\bin\Custom\Strategies\
```

Or right-click **Strategies → Import** in the editor.

### Step 3 — Compile

Press **F5**. Wait for `Compile succeeded`.

If errors appear:

- **"already contains a definition"** → there's a duplicate file in the Strategies folder. Find and delete it.
- **"could not be found"** → check the using directives at the top of the file are intact.

---

## 7. Adding to a Chart

1. On your Renko chart, right-click → **Strategies...**
2. Click **+** (Add)
3. Select **scalper_RenkoStringPatternAlert**
4. Configure parameters (see Section 8)
5. **IMPORTANT:** verify these top-of-dialog settings:
   - **Account:** Sim101 for testing, or your live account when ready
   - **Stop behavior:** doesn't really matter (no orders are placed) but set to "Close position" for safety
6. Click **OK**
7. Tick the **Enabled** checkbox

You should immediately see in the Output Window:

```
[INIT] scalper_RenkoStringPatternAlert v1.0 at 14:23:45.123
[INIT] PatternToMatch  = "011" (length 3)
[INIT] FollowUpPattern = "11" (length 2)
[INIT] FailuresToArm   = 2 (=> alert when this many consecutive FAILURES accumulate)
[INIT] Daily reset: 9:30 AM NY (DST-safe)
[INIT] Beep: 3 beeps, 1s apart
[INIT] CSV path: C:\temp
[INIT] This strategy places NO orders.
```

If you see `*** CONFIGURATION INVALID ***`, your pattern strings have problems — see Section 12.

---

## 8. Parameter Reference

### Group 1 — Layer 1 - Pattern

#### `PatternToMatch` (default: `"011"`)

The bit pattern the strategy looks for in the bit string. Must be **1 to 10 characters**, containing only `'0'` and `'1'`.

| Value | Meaning |
|---|---|
| `"0"` | Any single red brick |
| `"1"` | Any single green brick |
| `"01"` | Red brick followed by green |
| `"10"` | Green followed by red |
| `"011"` | Default — red then 2 greens (mean-reversion seed) |
| `"100"` | Green then 2 reds |
| `"000"` | 3 consecutive reds |
| `"111"` | 3 consecutive greens |
| `"0000"` | 4 consecutive reds (or more — see overlap behavior in Section 9) |
| `"01010"` | Alternating red-green-red-green-red (zigzag) |
| `"0011011"` | Very specific 7-bit signature; will be rare |

**Length affects frequency:**

- Short pattern (1-2 bits) → many occurrences per day (sometimes hundreds)
- Medium pattern (3-4 bits) → moderate, useful for trading signals
- Long pattern (5+ bits) → rare, possibly zero on calm days

---

#### `FollowUpPattern` (default: `"11"`)

After a pattern detection, the next M bricks (M = length of this string) must match this exactly for SUCCESS. Any mismatch = FAILURE. Same rules: 1-10 chars, only `'0'`/`'1'`.

| Value | Meaning |
|---|---|
| `"1"` | Success if next brick is green |
| `"0"` | Success if next brick is red |
| `"11"` | Default — success if next 2 bricks are both green |
| `"01"` | Success if next 2 bricks are red-then-green |
| `"111"` | Success if next 3 bricks are all green (stricter) |
| `"110"` | Success if green-green-red (oddly specific) |

**Length affects success rate:**

- Length 1 → about 50% success rate by chance alone
- Length 2 → about 25% success rate by chance
- Length 5 → about 3% success rate by chance

If you find your success rate is far above chance, you've found an edge. If it's close to chance, the pattern has no predictive value (which is itself useful information).

---

### Group 2 — Layer 2 - Alert

#### `FailuresToArm` (default: `2`)

Number of consecutive failures required to fire the alert. Strict count — interrupted by any success.

| Value | Behavior |
|---|---|
| `0` | **Special mode:** alert on every pattern DETECTION (before outcome known). Highest beep frequency. Use when you want to see everything. |
| `1` | Alert after the very first failure. Fires immediately when an F appears after an S (or at the start of the day). |
| `2` | Default. Alert after 2 consecutive failures. Suggests the market has "softened up" and may revert. |
| `3` | Alert after 3 consecutive failures. More conservative. |
| `5` | Patient — wait for a longer failure streak before alerting. |
| `10` | Very rare alerts. Only when the pattern has failed 10 times in a row. |

---

### Group 3 — Beep

#### `AlertSoundCount` (default: `3`)

Number of beeps played per alert.

#### `AlertReminderSecs` (default: `1`)

Wall-clock seconds between beeps. The next beep is played when the next brick closes AND at least this many seconds have passed.

> **Note:** Because Renko bricks close at unpredictable times (not on a fixed clock), beep cadence can stretch. If 30 seconds pass between brick closes, you'll hear beep 1 immediately, then beeps 2 and 3 only when the next brick closes. To get crisp 1-second-apart beeps regardless of bricks, you'd need a tick-driven beep mechanism — not implemented in this version.

---

### Group 4 — Visuals

#### `EnableChartMarkers` (default: `true`)

Master toggle for all chart drawings (success arrows, failure arrows, alert diamond). Turn OFF for a clean chart, but you'll still get beeps and CSV logging.

#### `ShowSuccessMarker` (default: `true`)

Draw a **green up-arrow** below the brick where each SUCCESS outcome lands.

#### `ShowFailureMarker` (default: `true`)

Draw an **orange-red down-arrow** above the brick where each FAILURE outcome lands.

(The alert marker — magenta diamond — is controlled by `EnableChartMarkers` master only.)

---

### Group 5 — Logging

#### `AuditLogPath` (default: `C:\temp`)

Folder where the two CSV files are written. Auto-created if missing.

Use a path you can write to (avoid Program Files / system folders).

---

### Group 6 — Advanced

#### `MaxBitsKept` (default: `5000`)

Memory safety cap on the bit string. Daily reset normally clears it long before this matters, but in case of a chart loaded with many days of history, this prevents runaway memory use. Default 5000 = roughly 3-4 hours of bricks on a busy day. Don't change unless you have a specific reason.

---

## 9. Detailed Examples

Each example walks through a concrete sequence of bricks and shows what the strategy does step by step.

### Example A — Default config on a textbook pullback

**Parameters:** `PatternToMatch="011"`, `FollowUpPattern="11"`, `FailuresToArm=2`

**Brick sequence (oldest left to newest right):**

```
Brick: 1  2  3  4  5  6  7  8  9  10
Bit:   1  0  1  1  0  1  1  0  1  1
```

**Walking through:**

| Brick | New bit | Last 3 = "011"? | Pending follow-up | Action |
|---|---|---|---|---|
| 1 | 1 | (too few) | — | nothing |
| 2 | 0 | no (`10`) | — | nothing |
| 3 | 1 | no (`101`) | — | nothing |
| 4 | 1 | **YES** (`011`) | — | Occurrence #1 created, watching next 2 bricks |
| 5 | 0 | no | #1 follow-up bit 1 of 2: actual `0`, expected `1` → mismatch | Continue watching |
| 6 | 1 | no | #1 follow-up bit 2 of 2: actual `1`, expected `1` | Done. `01` ≠ `"11"` → **FAILURE**. failureCount = 1 |
| 7 | 1 | **YES** (`011`) | New occurrence #2, watching next 2 | |
| 8 | 0 | no | #2 follow-up 1: `0` vs `1` → mismatch | Continue |
| 9 | 1 | no | #2 follow-up 2: `1` vs `1` ✓ | Done. `01` ≠ `"11"` → **FAILURE**. failureCount = 2 → **🔔 ALERT FIRES** |
| 10 | 1 | no | — | — |

After brick 10, failureCount is at 2, alert is armed-and-fired. Now we wait for either:

- The next `011` to form → that's our "trial"
- A SUCCESS to reset the counter

---

### Example B — Hunting strong reversals with FailuresToArm=0

**Parameters:** `PatternToMatch="0000"`, `FollowUpPattern="1"`, `FailuresToArm=0`

**Goal:** "Alert me every time 4 reds in a row form. I want to watch for a bounce."

**Brick sequence:**

```
Brick: 1  2  3  4  5  6  7
Bit:   0  0  0  0  0  0  1
```

**Walking through:**

| Brick | Bit | Last 4 = "0000"? | Action |
|---|---|---|---|
| 4 | 0 | **YES** | Occurrence #1, watching 1 follow-up brick. **🔔 ALERT (DETECT mode)** |
| 5 | 0 | **YES** (overlap, last 4 still `0000`) | Occurrence #2, watching 1. **🔔 ALERT again**. Meanwhile #1 resolves: bit 5 = `0` ≠ `1` → **FAILURE** |
| 6 | 0 | **YES** | Occurrence #3. **🔔 ALERT**. #2 resolves as FAILURE |
| 7 | 1 | no | #3 resolves: bit 7 = `1` ✓ → **SUCCESS** |

So a 6-red-then-green run produces 3 alerts and 3 outcomes (2F + 1S).

This is what `FailuresToArm=0` is for — you get alerted **while the down move is unfolding**, not after. Maximum lead time for human reaction.

---

### Example C — Strict 5-brick continuation

**Parameters:** `PatternToMatch="0000"`, `FollowUpPattern="0"`, `FailuresToArm=2`

**Goal:** "Alert me when a long red streak is likely to keep going" (trend continuation, not reversal).

In this config, SUCCESS = 5th red brick comes (`00000`), FAILURE = the 5th brick is green (`00001`).

**Brick sequence:**

```
Brick: 1  2  3  4  5  6  7  8  9  10 11
Bit:   0  0  0  0  1  0  0  0  0  0  0
```

| Brick | Last 4 = "0000"? | Notes |
|---|---|---|
| 4 | YES | Occurrence #1 |
| 5 | no | #1 resolves: bit 5=1, FollowUp wants `0` → mismatch → **FAILURE**. failureCount=1 |
| 8 | YES | Occurrence #2 |
| 9 | YES | Occurrence #3. #2 resolves: bit 9 = `0` ✓ → **SUCCESS**. failureCount reset to 0 |
| 10 | YES | Occurrence #4. #3 resolves: bit 10 = `0` ✓ → **SUCCESS** |
| 11 | YES | Occurrence #5. #4 resolves: bit 11 = `0` ✓ → **SUCCESS** |

So this configuration mostly produces successes during long red streaks. The alert only fires if we see 2 of those rare green interruptions in a row — meaning the streak has been broken 2 times. That's a different kind of signal.

---

### Example D — Maximum frequency (sanity check)

**Parameters:** `PatternToMatch="1"`, `FollowUpPattern="1"`, `FailuresToArm=0`

This is a useful diagnostic config. It fires an alert on **every green brick** (each green matches `"1"`). The next brick determines S/F based on whether it's also green.

Use this to verify the strategy is wired up correctly on a new chart — you should hear beeps and see arrows on virtually every brick during normal market activity.

Then change parameters to your real use case.

---

## 10. Output Window & CSV Files

### Output Window messages

Open via **New → NinjaScript Output**. Look for these tags:

| Tag | Meaning |
|---|---|
| `[INIT]` | Startup and configuration confirmation |
| `[VALIDATE]` | Pattern string validation errors |
| `[RESET]` | Daily 9:30 ET reset firing |
| `[L1]` | Layer 1 — pattern detection and outcome |
| `[ALERT]` | Alert firing (the moment of beep) |
| `[CSV-OCC]` `[CSV-ALERT]` | CSV write errors (rare) |

### `scalper_RenkoStringPatternAlert_Occurrences.csv`

One row per resolved Layer-1 occurrence (S or F). Columns:

| Column | Description |
|---|---|
| `OccurrenceTime` | When the pattern was detected (brick that completed the pattern) |
| `OutcomeTime` | When the outcome was determined (M bricks later) |
| `BrickIndexAtPatternEnd` | Index in today's bit string at the pattern's last bit |
| `Pattern` | The configured `PatternToMatch` (for reference) |
| `FollowUp` | The configured `FollowUpPattern` |
| `ActualFollowUp` | What follow-up bits actually appeared |
| `Outcome` | S or F |
| `PatternEndPrice` | Price at the moment of pattern detection |
| `OutcomePrice` | Price at outcome resolution |
| `FailureCountAfter` | Layer-2 counter after this outcome was recorded |
| `AlertArmedAfter` | YES/NO — was the alert in armed state after this row |
| `DailyOccurrenceNumber` | Sequential counter within the trading day |

Use this CSV for analysis:

- Pivot by `Outcome` → what's your S/F ratio?
- Filter by `ActualFollowUp` → what specific failure modes are common?
- Look at price columns → are successful patterns at certain price levels?

### `scalper_RenkoStringPatternAlert_Alerts.csv`

One row per fired alert. Columns:

| Column | Description |
|---|---|
| `AlertTime` | When the alert fired |
| `DailyAlertNumber` | Sequential alert count for the day |
| `AlertType` | `"DETECT"` (K=0 mode) or `"STREAK"` (K>0 mode) |
| `FailureCountAtAlert` | Layer-2 counter value at alert time |
| `FailuresToArm` | The configured threshold |
| `Pattern` | Configured pattern |
| `FollowUp` | Configured follow-up |
| `CurrentPrice` | Price at alert time |

---

## 11. Use-Case Recipes

Pre-tested parameter combinations for common scenarios. All assume 20-tick Renko on MNQ (5 pts/brick).

### Recipe 1 — Classic dip-buying (your default)

```
PatternToMatch:   "011"     (red then 2 greens = bounce start)
FollowUpPattern:  "11"      (continuation: 2 more greens)
FailuresToArm:    2
```

**Meaning:** Watch for 5-point pullbacks that immediately reverse with 10 points of green. Alert after 2 such bounces fail to continue.

**Expected frequency:** 10-30 occurrences per day. Maybe 1-3 alerts per day on average.

---

### Recipe 2 — Strong drop, looking for reversal

```
PatternToMatch:   "0000"    (4 reds = 20 points down without bounce)
FollowUpPattern:  "11"      (success requires 2 greens back)
FailuresToArm:    0         (alert on every detection)
```

**Meaning:** Maximum lead time. Every time a 20-pt drop forms, hear it. Decide visually whether to fade.

**Expected frequency:** Rare. Maybe 0-5 alerts per day depending on market.

---

### Recipe 3 — Trend continuation

```
PatternToMatch:   "111"     (3 greens = uptrend confirmed)
FollowUpPattern:  "1"       (next brick should be green too)
FailuresToArm:    2
```

**Meaning:** Track up-trend continuation. Alert when momentum has stalled twice in a row.

---

### Recipe 4 — Sharp V-bottom

```
PatternToMatch:   "00011"   (3 reds then 2 greens = sharp V)
FollowUpPattern:  "1"       (one more green = V confirmed)
FailuresToArm:    1
```

**Meaning:** Hunt for specific V-bottom shape. Very picky pattern; few occurrences per day.

---

### Recipe 5 — Diagnostic (verify it's working)

```
PatternToMatch:   "1"
FollowUpPattern:  "1"
FailuresToArm:    0
```

**Meaning:** Alert on every green brick. Use briefly to confirm the strategy is running and chart markers are visible. Switch to a real recipe after.

---

## 12. Troubleshooting

### Compilation errors

| Error | Cause | Fix |
|---|---|---|
| `already contains a definition for scalper_RenkoStringPatternAlert` | Duplicate file in Strategies folder | Find and delete the duplicate |
| `could not be found` for various NinjaTrader types | Wrong NT version, or missing using directives | Check file header is intact |

### `*** CONFIGURATION INVALID ***` at startup

The strategy refused to run because one of your pattern strings is bad. The Output Window will tell you exactly what's wrong:

| Message | Fix |
|---|---|
| `PatternToMatch is empty` | Enter at least one character (`0` or `1`) |
| `FollowUpPattern is 11 chars; max allowed is 10` | Shorten the string |
| `contains invalid char 'x' at position 2` | Only `0` and `1` are allowed — no spaces, letters, etc. |

### No alerts at all

- Check the Output Window for `[INIT]` messages — is the strategy actually running?
- Look for `[L1]` messages — are any pattern detections happening?
- If `[L1]` messages exist but no `[ALERT]` — your `FailuresToArm` may be too high, or every occurrence is succeeding (which is good news!)
- If no `[L1]` at all — your pattern may be too rare for the current market, or you're on a non-Renko chart, or the strategy is on a chart with too few bars (`BarsRequiredToTrade`)

### Too many alerts

- Increase `FailuresToArm` to filter out short failure streaks
- Use a longer/more specific `PatternToMatch` (e.g., `"00011"` instead of `"011"`)
- Make `FollowUpPattern` shorter (length-1 follow-ups succeed ~50% of the time by chance, so failure streaks build slowly)

### Beeps don't sound

- Check Windows volume / mute
- Verify `Alert1.wav` exists in your NinjaTrader install directory under `sounds\`
- Try `AlertSoundCount = 1` first to simplify

### CSV files not being written

- Check `AuditLogPath` is set correctly
- Verify you have write permission to that folder
- Look for `[CSV-OCC]` or `[CSV-ALERT]` ERROR messages in the Output Window

### Brick size doesn't match what I expected

This is a chart problem, not a strategy problem.

- Right-click chart → **Data Series** → check **Type = Renko** and **Value = your intended ticks**
- For MNQ 5-pt bricks: Type=Renko, Value=20 (ticks). 20 × 0.25 = 5 pts.

### `[RESET] New trading day at 9:30 NY` fires at unexpected time

- Verify your computer clock is correct
- The reset uses NY time internally — if you're in another timezone, 9:30 ET will appear at your local equivalent (e.g., 6:30 AM Pacific). That's correct behavior.

---

## Quick Reference Card

```
INSTALL:    Copy .cs to Documents\NinjaTrader 8\bin\Custom\Strategies\
            Press F5 in NinjaScript Editor.

CHART:      Renko, 20-tick (5 pts) on MNQ — set in chart, NOT strategy.

ADD:        Right-click chart → Strategies → + → scalper_RenkoStringPatternAlert
            Set pattern strings + threshold, click OK, tick Enabled.

CORE CONCEPT:
  Brick close > open = '1' (green); brick close < open = '0' (red).
  Strategy maintains a daily bit string from 9:30 ET.

LAYER 1:    PatternToMatch detected → watch FollowUpPattern bricks
            Exact match = SUCCESS. Any mismatch = FAILURE.

LAYER 2:    Consecutive failures counted.
            FailuresToArm reached → ALERT FIRES once per F-streak.
            Counter resets on SUCCESS.

K=0 MODE:   Alert on every pattern DETECTION (before outcome known).

DEFAULTS:   PatternToMatch="011"  FollowUpPattern="11"  FailuresToArm=2
            Beeps=3 (1s apart). Markers on. CSV to C:\temp.

LOGS:       Output Window for live events.
            scalper_RenkoStringPatternAlert_Occurrences.csv (every S/F)
            scalper_RenkoStringPatternAlert_Alerts.csv     (every alert)

REMEMBER:   Strategy places NO orders. Beep = look at chart and decide.
```

---

*End of guide.*
