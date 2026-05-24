# scalper_RenkoStringPatternAlertEMA — User Guide (v1.3.3)

A plain-English walkthrough of how the strategy detects patterns, filters with EMAs, fires alerts, and tracks chase-trade outcomes.

The strategy **places no orders**. It beeps when conditions are met. You enter manually.

---

## 1. The Brick Stream

Every closed Renko brick produces one bit:

- **Green brick** (Close > Open) → bit `1`
- **Red brick**   (Close < Open) → bit `0`

These bits are appended to an in-memory string every time a new brick closes. The list resets at **9:30 AM New York time** every trading day.

Example brick stream over a session:

```
G G R G G G R G G G R G G ...
1 1 0 1 1 1 0 1 1 1 0 1 1 ...
```

---

## 2. The Three Parameters You Set

| Parameter | Example | Meaning |
|---|---|---|
| `PatternToMatch` | `011` | The setup. Scanned across the brick stream. |
| `FollowUpPattern` | `1` | What the next brick(s) must be for a SUCCESS. |
| `AlertPattern` | `01` *or* `01, 011, 0111` | One or more suffix patterns that arm the alert (matched against EMA-qualified outcomes only). **[v1.3.3]** Now accepts a comma-separated list. |

### 2a. AlertPattern as a list (new in v1.3.3)

`AlertPattern` is no longer just a single string — it can be a **comma-separated list of patterns**. The strategy treats them as a logical OR: at each EMA-qualified brick close, if the tail of `OutcomeString_EmaQualified` ends with **any** entry in your list, **one** alert fires (one sound, one chart diamond, one CSV row).

**Examples:**

| Input | Behavior |
|---|---|
| `01` | Single pattern (identical to v1.3.2) |
| `01, 011, 0111` | Alert if tail ends with `01` OR `011` OR `0111` |
| `01,011,0111` | Same — whitespace around commas is optional |

**Same brick can only fire one alert.** Even if your list is `1, 11` and the tail is `011` (which ends with both `1` and `11`), exactly one alert fires for that brick. There is no weighted or multi-alert mode in this version.

### 2b. Avoid suffix-overlap in your list

If one pattern in your list is a **suffix** of another, the shorter one becomes redundant: whenever the longer pattern matches, the shorter one also matches, but only one alert fires. The strategy will print a warning at startup but still run.

**Overlap examples (will warn):**

| List | Why it overlaps |
|---|---|
| `1, 11` | `11` ends with `1` |
| `101, 10101` | `10101` ends with `101` |
| `01, 1, 11` | `1` is a suffix of `11`; `1` is a suffix of `01` (technically would warn for both pairs) |

**Clean lists (no warning):**

| List | Why it's clean |
|---|---|
| `01, 011, 0111` | None ends with another |
| `101, 1010, 10101` | Wait — `10101` *does* end with `101` → WILL WARN. But `1010` is clean against both. |
| `01, 1010` | Neither ends with the other |

**System limitation to be aware of:** the strategy cannot give you clean, non-overlapping analysis for natural sequences like `101, 1010, 10101` because `101` is a suffix of `10101`. If you want to study each of those patterns separately, run them one at a time as the single `AlertPattern` and let the post-alert capture finish.

### 2c. Parser rules

- Whitespace around commas is trimmed.
- Empty entries from stray commas (e.g. `01,, 011`) are skipped silently.
- Duplicate entries (e.g. `01, 01, 011`) are deduplicated with a warning.
- Each entry must be 1-10 chars of `0` and `1` only. Invalid entries are reported and skipped.
- If the entire list ends up empty after parsing, the strategy refuses to start.
- Backward-compatible: a single pattern like `01` still works exactly as before.

---

## 3. The Two Independent Questions Per Occurrence

Every time `PatternToMatch` is found in the brick stream, the strategy answers **two separate questions**:

**Question A — Did the follow-up succeed?**
- Look at the next `FollowUpPattern.Length` brick(s).
- If they match `FollowUpPattern` exactly → **S (success, bit = 1)**
- If any mismatch → **F (failure, bit = 0)**
- *EMA is not consulted here.*

**Question B — Was the pattern's closing brick EMA-qualified?**
- The "closing brick" = the **last brick of the pattern** (e.g. the second `1` in `011`).
- Check: was the **Open** of that brick above **both** EMA1 and EMA2?
- Yes → **EMA-qualified (YES)**
- No  → **not qualified (NO)**
- *The outcome bit is not consulted here.*

**Important rule:** EMA qualifies the **pattern**, not the **follow-up brick**.

---

## 4. The Three Outcome Strings

Each S/F result gets routed into one or more running bit strings:

| String | What goes in | Used for |
|---|---|---|
| `OutcomeString_NoEmaFilter` | **Every** occurrence's bit (S=1, F=0) | Theoretical universe — what the pattern does ignoring the EMA filter |
| `OutcomeString_EmaQualified` | Only bits from **EMA-qualified** occurrences | Filtered universe — what you'd trade if you traded every qualified setup |
| `PostAlertOutcomeString` | Only bits from **EMA-qualified outcomes that landed after an alert fired** | Real-money universe — your actual chase-trade results |

**Important:** A non-qualified occurrence does **NOT** put a `0` into `OutcomeString_EmaQualified`. It puts **nothing**. The string just stays the same length. The EmaQualified string is therefore always shorter than (or equal in length to) the NoEmaFilter string.

---

## 5. The Alert Trigger

After every new bit is appended to `OutcomeString_EmaQualified`, the strategy checks:

> Does the **tail** of `OutcomeString_EmaQualified` end with **any** pattern in the `AlertPattern` list?

If yes → **beep + chart diamond + CSV row**. You are now "armed" — watch for the next setup to actually trade.

For a single-pattern input like `AlertPattern = "01"`, this is the same as v1.3.2. For a list like `AlertPattern = "01, 011, 0111"`, the check is just done once across all listed patterns, and at most one alert is produced per brick.

---

## 6. Worked Example A — Single Pattern (same as v1.3.2)

**Settings**: `PatternToMatch="011"`, `FollowUpPattern="1"`, `AlertPattern="01"`

Assume five `011` patterns occur during the session, with the following circumstances:

| Event | Pattern's closing brick EMA-qualified? | Follow-up brick | Outcome | NoEmaFilter after | EmaQualified after | Alert? |
|---|---|---|---|---|---|---|
| 1 | YES | 1 (green) | **S** → bit `1` | `1` | `1` | tail = `1` ≠ `01`, no |
| 2 | NO  | 1 (green) | **S** → bit `1` | `11` | `1` *(unchanged)* | no (no new bit added) |
| 3 | YES | 1 (green) | **S** → bit `1` | `111` | `11` | tail = `11` ≠ `01`, no |
| 4 | YES | 0 (red)   | **F** → bit `0` | `1110` | `110` | tail = `10` ≠ `01`, no |
| 5 | YES | 1 (green) | **S** → bit `1` | `11101` | `1101` | tail = **`01`** ✅ → **ALERT FIRES** 🔔 |

After event 5: **you are armed**.

### What happens next — the real trade

After the alert, the strategy waits for the **next EMA-qualified `011` occurrence**. When it appears:

- **You enter long** at the close of that pattern's last brick.
- **The very next brick is your verdict:**
  - Green (`1`) → follow-up matched → **WIN** → `PostAlertOutcomeString` gets `1`
  - Red   (`0`) → follow-up failed  → **LOSS** → `PostAlertOutcomeString` gets `0`

It's a one-brick-in, one-brick-out trade.

#### Continuing the example

| Event | EMA-qualified? | Follow-up | Outcome | EmaQualified after | PostAlertOutcomeString after | Alert? |
|---|---|---|---|---|---|---|
| 6 | NO  | 1 | S → `1` | `1101` *(unchanged)* | *(unchanged — capture requires EMA-qualified)* | no |
| 7 | YES | 1 | S → `1` | `11011` | **`1`** ← captured (the chase trade won) | tail = `11` ≠ `01`, no |
| 8 | YES | 0 | F → `0` | `110110` | `1` | tail = `10` ≠ `01`, no |
| 9 | YES | 1 | S → `1` | `1101101` | `1` | tail = **`01`** ✅ → **ALERT FIRES AGAIN** 🔔 |
| 10 | YES | 0 | F → `0` | `11011010` | **`10`** ← chase trade #2 lost | tail = `10`, no |

After all events:

- `OutcomeString_NoEmaFilter`  = `1110110110` (all 10 raw outcomes, including non-qualified ones)
- `OutcomeString_EmaQualified` = `11011010` (only the 8 EMA-qualified outcomes)
- `PostAlertOutcomeString`     = `10` (the 2 chase trades that resolved after alerts)

**Chase-trade hit rate so far** = 1 win / 2 trades = **50%**.

---

## 6b. Worked Example B — Multi-Pattern List (new in v1.3.3)

**Settings**: `PatternToMatch="011"`, `FollowUpPattern="1"`, `AlertPattern="01, 011, 0111"`

Imagine a session where the EMA-qualified outcome stream is `01111111` (one failure, then seven successes in a row). At each new EMA-qualified bit appended, the strategy checks if the tail ends with `01`, `011`, or `0111`:

| Event | EMA-qual? | Outcome | EmaQualified tail after | Ends with `01`? | Ends with `011`? | Ends with `0111`? | Alert? |
|---|---|---|---|---|---|---|---|
| 1 | YES | F → `0` | `0` | no | no | no | no |
| 2 | YES | S → `1` | `01` | **YES** | no | no | 🔔 **alert #1** |
| 3 | YES | S → `1` | `011` | no | **YES** | no | 🔔 **alert #2** |
| 4 | YES | S → `1` | `0111` | no | no | **YES** | 🔔 **alert #3** |
| 5 | YES | S → `1` | `01111` | no | no | no | no |
| 6 | YES | S → `1` | `011111` | no | no | no | no |
| 7 | YES | S → `1` | `0111111` | no | no | no | no |
| 8 | YES | S → `1` | `01111111` | no | no | no | no |

**Three alerts fire** as the success streak extends from 1 → 2 → 3 deep. After the streak passes 4 consecutive successes, none of `01/011/0111` is a suffix anymore, so no further alerts fire until the streak breaks.

This is the "pattern-on-pattern-on-pattern" composite use case — you're being alerted at multiple stages of an unfolding streak.

### A second multi-pattern example: a brief reversal

Stream `0110` (one F, two S, then F):

| Event | Outcome | Tail | Alert? |
|---|---|---|---|
| 1 | F → `0` | `0` | no |
| 2 | S → `1` | `01` | 🔔 alert (matches `01`) |
| 3 | S → `1` | `011` | 🔔 alert (matches `011`) |
| 4 | F → `0` | `0110` | no (no pattern in the list ends with `0`) |

Two alerts. The stream `0110` does not match `0111` because the last bit is `0`, not `1`.

---

## 7. Overlapping Alerts (Why a Counter Exists)

If a second alert fires **before** the first alert's chase trade has resolved, the strategy doesn't lose track. An internal counter `PendingPostAlertCaptures` increments on each alert and decrements as each future EMA-qualified outcome consumes a slot. Captures happen in **FIFO order**.

With a multi-pattern list, this counter is especially relevant — Worked Example B above produces three back-to-back alerts in just three bricks. Each alert queues one future capture, so the next three EMA-qualified outcomes will all be appended to `PostAlertOutcomeString`.

You normally don't need to think about this — just know that `PendingPostAlertCapturesAfter` in the CSV lets you audit it.

---

## 8. The CSV Output

Two files are written to your configured folder (default `C:\temp`):

- `scalper_RenkoStringPatternAlertEMA_v133_occ.csv` — one row per resolved occurrence
- `scalper_RenkoStringPatternAlertEMA_v133_alert.csv` — one row per alert

*(v1.3.2 used `_v132_` in the filenames. v1.3.3 bumps to `_v133_` so the two versions don't collide in the same folder.)*

Both files start with `#`-prefixed header lines containing:

- Schema version (`1.3.3`)
- File creation time (NY and UTC)
- Instrument, bar type, brick size, tick size
- All parameter values in effect, including:
  - `# AlertPattern=...` — the **raw user input** (e.g. `01,011,0111`)
  - `# AlertPatternParsed=[...]` — the **parsed list** after dedup/validation
- A full column legend

Lines starting with `#` are **comment lines** — Excel will show them; pandas can skip them with `pd.read_csv(path, comment='#')`. **All timestamps in the row data are in New York time** (column names carry a `_NY` suffix).

### The `AlertPattern` column in the data rows

The `AlertPattern` column in each data row logs the user input **as a single string** — for example `01,011,0111`. If the input contains commas, the value is wrapped in double quotes (`"01,011,0111"`) so it parses correctly as one CSV field.

**Important:** the strategy does NOT log which specific pattern matched on each alert. This is intentional. If you want to study each pattern in isolation (e.g. to compute per-pattern hit rates or longest-streak statistics), run the strategy multiple times — once with `AlertPattern = "01"`, once with `"011"`, etc. — and analyze the resulting CSVs separately.

### Startup messages to watch for

When you enable the strategy, look at NinjaScript's Output window for these lines (also written to the NT Log tab on warnings):

```
[INIT] AlertPattern (raw input) = "01, 011, 0111"
[INIT] AlertPattern (parsed list, 3 pattern(s)): [01, 011, 0111]
[VALIDATE] AlertPattern list: no suffix overlaps detected.
```

If your list has overlaps, you'll see something like:

```
[VALIDATE] WARN: AlertPattern overlap: "1" is a suffix of "11". Both will match the same brick when the tail ends in "11", but only ONE alert fires per brick (no extra information). Consider removing "1" if you don't need it.
[VALIDATE] AlertPattern list: 1 suffix-overlap warning(s) above. Strategy will still run.
```

The strategy does not block on warnings — it just informs you.

---

## 9. The Key Takeaway

Three nested universes:

```
┌─ NoEmaFilter ──────────────────────────────┐
│  every occurrence the pattern ever made    │
│                                            │
│   ┌─ EmaQualified ───────────────────┐     │
│   │  occurrences worth trading       │     │
│   │                                  │     │
│   │   ┌─ PostAlertOutcomeString ─┐   │     │
│   │   │  the trades you'd take   │   │     │
│   │   │  after alert arming      │   │     │
│   │   └──────────────────────────┘   │     │
│   └──────────────────────────────────┘     │
└────────────────────────────────────────────┘
```

- **NoEmaFilter** answers: "does this pattern have predictive value at all?"
- **EmaQualified** answers: "does the EMA filter improve it?"
- **PostAlertOutcomeString** answers: "does the meta-pattern (alert-after-`AlertPattern`-list) further improve it?"

If all three hit-rates are roughly equal, the filters aren't doing anything useful. If they rise across the three, your edge is sharpening with each layer.

With a multi-pattern `AlertPattern` list, you're asking the third question across a **composite** signal — any of the listed tail patterns can arm a trade. The same `PostAlertOutcomeString` analysis still applies; you're just measuring the combined hit rate of the whole composite.

---

*v1.3.3 — AlertPattern accepts a comma-separated list (boolean OR across patterns) — one alert per brick close maximum — startup warning for suffix-overlap pairs — CSV filenames bumped to `_v133_` — full backward compatibility for single-pattern inputs.*

*v1.3.2 — EMA filter uses Open (whole green-brick body above EMAs) — PostAlertOutcomeString added — NY-time stamps — CSV headers self-documenting.*
