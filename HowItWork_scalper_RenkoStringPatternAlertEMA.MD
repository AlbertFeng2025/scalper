# scalper_RenkoStringPatternAlertEMA — User Guide (v1.3.2)

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
| `AlertPattern` | `01` | Suffix that arms the alert (matched against EMA-qualified outcomes only). |

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

> Does the **tail** of `OutcomeString_EmaQualified` equal `AlertPattern`?

If yes → **beep + chart diamond + CSV row**. You are now "armed" — watch for the next setup to actually trade.

---

## 6. Full Worked Example

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

---

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

This third number is the one that matters for real money — it tells you whether the "chase after `01`" edge actually works.

---

## 7. Overlapping Alerts (Why a Counter Exists)

If a second alert fires **before** the first alert's chase trade has resolved, the strategy doesn't lose track. An internal counter `PendingPostAlertCaptures` increments on each alert and decrements as each future EMA-qualified outcome consumes a slot. Captures happen in **FIFO order**.

You normally don't need to think about this — just know that `PendingPostAlertCapturesAfter` in the CSV lets you audit it.

---

## 8. The CSV Output

Two files are written to your configured folder (default `C:\temp`):

- `scalper_RenkoStringPatternAlertEMA_v132_occ.csv` — one row per resolved occurrence
- `scalper_RenkoStringPatternAlertEMA_v132_alert.csv` — one row per alert

Both files start with `#`-prefixed header lines containing:

- Schema version
- File creation time (NY and UTC)
- Instrument, bar type, brick size, tick size
- All parameter values in effect
- A full column legend

Lines starting with `#` are **comment lines** — Excel will show them; pandas can skip them with `pd.read_csv(path, comment='#')`. **All timestamps in the row data are in New York time** (column names carry a `_NY` suffix).

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
- **PostAlertOutcomeString** answers: "does the meta-pattern (alert-after-`01`) further improve it?"

If all three hit-rates are roughly equal, the filters aren't doing anything useful. If they rise across the three, your edge is sharpening with each layer.

---

*v1.3.2 — EMA filter uses Open (whole green-brick body above EMAs) — PostAlertOutcomeString added — NY-time stamps — CSV headers self-documenting.*
