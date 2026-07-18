# Meta Labeling: Layer-2 Pattern Filtration — Detailed Step-by-Step Example

> **Purpose:** This document walks through a complete, position-by-position example of how the Layer-2 filter decides **which bits become real trades** and which are ignored. If you're new to meta labeling, read this carefully — every single decision is explained.

---

## 1. Setup

### The Raw String

We use the following 80-bit sample (positions 1–80, 1-based indexing):

```
Position:  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19 20
Bit:       1  1  0  0  1  1  1  1  1  1  1  1  1  0  1  1  0  0  0  1

Position: 21 22 23 24 25 26 27 28 29 30 31 32 33 34 35 36 37 38 39 40
Bit:       1  1  1  1  1  1  1  0  0  0  1  0  0  1  0  1  0  1  1  1

Position: 41 42 43 44 45 46 47 48 49 50 51 52 53 54 55 56 57 58 59 60
Bit:       1  0  1  1  1  0  1  0  0  1  0  1  0  1  0  0  1  1  1  1

Position: 61 62 63 64 65 66 67 68 69 70 71 72 73 74 75 76 77 78 79 80
Bit:       1  1  0  0  1  0  1  0  0  0  0  1  0  1  1  0  1  0  1  0
```

**Full string:** `11001111111110110001111111100010010101111011101001010100111111001010000101101010`

### Filter Definitions

| Filter | Pattern | Meaning |
|--------|---------|---------|
| **F1** (Layer 1) | `"101"` | rawString tail must end with `1, 0, 1` |
| **F2** (Layer 2) | `"00"` | f1 tail must end with `0, 0` |
| **Target** | `1` | We chase `1`s (win = 1, loss = 0) |

### The Pipeline (One Iteration Per Bit)

For each bit at position *i*:

```
1. If pending == TRUE: record current bit as a REAL TRADE outcome
2. Append bit to rawString
3. If waitF1 == TRUE:
       feed bit into f1 (Layer 1 outcome string)
       check if f1 ends with F2 → set isArmed
4. If rawString ends with F1 → set waitF1 = TRUE
5. If isArmed == TRUE AND rawString ends with F1 → set pending = TRUE
   (the NEXT bit will be a real trade)
```

---

## 2. Key Concepts Explained

Before diving into positions, understand these four state variables:

| Variable | Purpose |
|----------|---------|
| **`rawString`** | Accumulates every bit from the raw data. We check if its **tail** matches F1. |
| **`f1`** | The **Layer 1 outcome string**. Only grows when `waitF1` is active. We check if its **tail** matches F2. |
| **`waitF1`** | Set to `TRUE` when rawString ends with F1. The **next** bit will be fed into f1 instead of just sitting in rawString. |
| **`isArmed`** | Set to `TRUE` when f1 ends with F2. This means the filter is "armed" and ready to fire a real trade. |
| **`pending`** | Set to `TRUE` when `isArmed` AND rawString ends with F1. The **next** bit becomes a real trade. |

### The Two-Stage Trigger Mechanism

```
Stage 1: rawString tail matches "101" (F1)
         → waitF1 = TRUE
         → Next bit feeds into f1

Stage 2: f1 tail matches "00" (F2)
         → isArmed = TRUE

Stage 3: isArmed AND rawString tail matches "101" (F1 again)
         → pending = TRUE
         → NEXT bit is a REAL TRADE
```

> **Critical insight:** A real trade fires on the bit **after** the trigger is set. The trigger is set at position *i*, but the trade bit is recorded at position *i+1*.

---

## 3. Position-by-Position Walkthrough

### Positions 1–14: No Activity

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending | What Happened |
|-----|-----|------------------|----|--------|---------|---------|---------------|
| 1 | 1 | `1` | `` | F | F | F | Just appending. No `101` tail yet. |
| 2 | 1 | `11` | `` | F | F | F | Still no `101`. |
| 3 | 0 | `110` | `` | F | F | F | Ends with `110`, not `101`. |
| 4 | 0 | `1100` | `` | F | F | F | Ends with `100`, not `101`. |
| 5 | 1 | `11001` | `` | F | F | F | Ends with `001`, not `101`. |
| 6 | 1 | `110011` | `` | F | F | F | Ends with `011`, not `101`. |
| 7–13 | 1s | `...111` | `` | F | F | F | All end with `111`, not `101`. |
| 14 | 0 | `...110` | `` | F | F | F | Ends with `110`, not `101`. |

> Nothing happens for the first 14 positions because `rawString` never ends with `"101"`.

---

### Position 15: First F1 Match

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **15** | **1** | `...101` | `` | **T** | F | F |

**Step-by-step:**
1. `rawString += '1'` → `110011111111101`
2. **📍 F1 MATCH!** `rawString` ends with `"101"` → `waitF1 = TRUE`
3. The next bit (position 16) will be fed into `f1`

> This is a **setup**. We found pattern `"101"` in the raw data. Now we wait to see what the next bit is.

---

### Position 16: Bit Feeds into f1

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **16** | **1** | `...011` | `1` | F | F | F |

**Step-by-step:**
1. `waitF1` was `TRUE` → bit `'1'` feeds into `f1` → `f1 = "1"`
2. Check F2 on `f1`: `"1"` does NOT end with `"00"` → `isArmed = FALSE`
3. Check F1 on `rawString`: ends with `"011"`, not `"101"` → `waitF1` stays `FALSE`

> The setup at position 15 produced a Layer 1 outcome of `1`. But `f1="1"` doesn't end with `"00"`, so the filter is NOT armed.

---

### Positions 17–35: Quiet Period

For 19 consecutive positions, `rawString` never ends with `"101"`. `f1` remains `"1"` from position 16. `isArmed` stays `FALSE`.

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 17–35 | various | various | `1` | F | F | F | No `"101"` tail in rawString. |

> The filter is dormant. No setups, no trades.

---

### Position 36: Second F1 Match

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **36** | **1** | `...101` | `1` | **T** | F | F |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 34=1, 35=0, 36=1)
2. **📍 F1 MATCH!** `rawString` ends with `"101"` → `waitF1 = TRUE`
3. The next bit (position 37) will feed into `f1`

> Another setup found. The next bit goes to Layer 1.

---

### Position 37: Bit Feeds into f1

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **37** | **0** | `...010` | `10` | F | F | F |

**Step-by-step:**
1. `waitF1` was `TRUE` → bit `'0'` feeds into `f1` → `f1 = "10"`
2. Check F2 on `f1`: `"10"` does NOT end with `"00"` → `isArmed = FALSE`
3. Check F1 on `rawString`: ends with `"010"`, not `"101"` → `waitF1 = FALSE`

> Layer 1 outcome is `0`. Still not armed.

---

### Position 38: Third F1 Match

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **38** | **1** | `...101` | `10` | **T** | F | F |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 36=1, 37=0, 38=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`

---

### Position 39: Bit Feeds into f1

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **39** | **1** | `...011` | `101` | F | F | F |

**Step-by-step:**
1. `waitF1` was `TRUE` → bit `'1'` feeds into `f1` → `f1 = "101"`
2. Check F2 on `f1`: `"101"` does NOT end with `"00"` → `isArmed = FALSE`
3. Check F1 on `rawString`: ends with `"011"`, not `"101"` → `waitF1 = FALSE`

> Layer 1 outcome is `1`. Still not armed.

---

### Positions 40–42: Quiet

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 40 | 1 | `...111` | `101` | F | F | F | No `101` tail. |
| 41 | 1 | `...111` | `101` | F | F | F | No `101` tail. |
| 42 | 0 | `...110` | `101` | F | F | F | No `101` tail. |

---

### Position 43: Fourth F1 Match

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **43** | **1** | `...101` | `101` | **T** | F | F |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 41=1, 42=0, 43=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`

---

### Position 44: Bit Feeds into f1

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **44** | **1** | `...011` | `1011` | F | F | F |

**Step-by-step:**
1. `waitF1` was `TRUE` → bit `'1'` feeds into `f1` → `f1 = "1011"`
2. Check F2 on `f1`: `"1011"` does NOT end with `"00"` → `isArmed = FALSE`
3. Check F1 on `rawString`: ends with `"011"`, not `"101"` → `waitF1 = FALSE`

> Layer 1 outcome is `1`. Still not armed.

---

### Positions 45–46: Quiet

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 45 | 1 | `...111` | `1011` | F | F | F | No `101` tail. |
| 46 | 0 | `...110` | `1011` | F | F | F | No `101` tail. |

---

### Position 47: Fifth F1 Match

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **47** | **1** | `...101` | `1011` | **T** | F | F |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 45=1, 46=0, 47=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`

---

### Position 48: Bit Feeds into f1

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **48** | **0** | `...010` | `10110` | F | F | F |

**Step-by-step:**
1. `waitF1` was `TRUE` → bit `'0'` feeds into `f1` → `f1 = "10110"`
2. Check F2 on `f1`: `"10110"` does NOT end with `"00"` → `isArmed = FALSE`
3. Check F1 on `rawString`: ends with `"010"`, not `"101"` → `waitF1 = FALSE`

> Layer 1 outcome is `0`. Still not armed.

---

### Positions 49–51: Quiet

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 49 | 0 | `...100` | `10110` | F | F | F | No `101` tail. |
| 50 | 1 | `...001` | `10110` | F | F | F | No `101` tail. |
| 51 | 0 | `...010` | `10110` | F | F | F | No `101` tail. |

---

### Position 52: Sixth F1 Match

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **52** | **1** | `...101` | `10110` | **T** | F | F |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 50=1, 51=0, 52=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`

---

### Position 53: Bit Feeds into f1 — 🎯 F2 MATCH! isArmed = TRUE

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **53** | **0** | `...010` | `101100` | F | **T** | F |

**Step-by-step:**
1. `waitF1` was `TRUE` → bit `'0'` feeds into `f1` → `f1 = "101100"`
2. **✅ F2 MATCH!** `f1 = "101100"` ends with `"00"` → **`isArmed = TRUE`**
3. Check F1 on `rawString`: ends with `"010"`, not `"101"` → `waitF1 = FALSE`

> **🎯 BREAKTHROUGH!** The filter is now **ARMED**. `f1` ended with `"00"`. Now we need `rawString` to end with `"101"` again to fire a real trade.

---

### Position 54: F1 Match + isArmed → 💰 TRIGGER SET!

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **54** | **1** | `...101` | `101100` | **T** | T | **T** |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 52=1, 53=0, 54=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`
3. **💰 TRIGGER SET!** `isArmed = TRUE` AND rawString ends with `"101"` → **`pending = TRUE`**

> **THE TRIGGER IS SET!** The next bit (position 55) will be a **REAL TRADE**.

---

### Position 55: 🔥 FIRST REAL TRADE!

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **55** | **0** | `...010` | `1011000` | F | **T** | F |

**Step-by-step:**
1. **`pending` was `TRUE`** → **🔥 REAL TRADE!** Bit `'0'` at position 55 is recorded as trade outcome
2. `rawString += '0'`
3. `waitF1` was `TRUE` → bit `'0'` feeds into `f1` → `f1 = "1011000"`
4. **✅ F2 MATCH!** `f1 = "1011000"` ends with `"00"` → `isArmed = TRUE` (stays armed!)
5. Check F1 on `rawString`: ends with `"010"`, not `"101"` → `waitF1 = FALSE`

> **Trade #1 recorded: `0` (LOSS)** at position 55.
>
> Even though this trade lost, the filter **stays armed** because `f1` still ends with `"00"`. The system is still looking for the next `"101"` in rawString.

---

### Positions 56–66: Armed but Waiting for F1

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 56 | 0 | `...100` | `1011000` | F | T | F | No `101` tail. |
| 57 | 1 | `...001` | `1011000` | F | T | F | No `101` tail. |
| 58 | 1 | `...011` | `1011000` | F | T | F | No `101` tail. |
| 59 | 1 | `...111` | `1011000` | F | T | F | No `101` tail. |
| 60 | 1 | `...111` | `1011000` | F | T | F | No `101` tail. |
| 61 | 1 | `...111` | `1011000` | F | T | F | No `101` tail. |
| 62 | 1 | `...111` | `1011000` | F | T | F | No `101` tail. |
| 63 | 0 | `...110` | `1011000` | F | T | F | No `101` tail. |
| 64 | 0 | `...100` | `1011000` | F | T | F | No `101` tail. |
| 65 | 1 | `...001` | `1011000` | F | T | F | No `101` tail. |
| 66 | 0 | `...010` | `1011000` | F | T | F | No `101` tail. |

> The filter is **armed** (`isArmed = TRUE`) but `rawString` hasn't ended with `"101"` yet. We're waiting for the next setup.

---

### Position 67: F1 Match + isArmed → 💰 TRIGGER SET!

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **67** | **1** | `...101` | `1011000` | **T** | T | **T** |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 65=1, 66=0, 67=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`
3. **💰 TRIGGER SET!** `isArmed = TRUE` AND rawString ends with `"101"` → **`pending = TRUE`**

> **TRIGGER SET AGAIN!** The next bit (position 68) will be a real trade.

---

### Position 68: 🔥 SECOND REAL TRADE!

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **68** | **0** | `...010` | `10110000` | F | **T** | F |

**Step-by-step:**
1. **`pending` was `TRUE`** → **🔥 REAL TRADE!** Bit `'0'` at position 68 is recorded as trade outcome
2. `rawString += '0'`
3. `waitF1` was `TRUE` → bit `'0'` feeds into `f1` → `f1 = "10110000"`
4. **✅ F2 MATCH!** `f1 = "10110000"` ends with `"00"` → `isArmed = TRUE` (stays armed!)
5. Check F1 on `rawString`: ends with `"010"`, not `"101"` → `waitF1 = FALSE`

> **Trade #2 recorded: `0` (LOSS)** at position 68.
>
> Another loss, but the filter stays armed. The `"00"` tail in `f1` persists.

---

### Positions 69–73: Armed but Waiting

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 69 | 0 | `...100` | `10110000` | F | T | F | No `101` tail. |
| 70 | 0 | `...000` | `10110000` | F | T | F | No `101` tail. |
| 71 | 0 | `...000` | `10110000` | F | T | F | No `101` tail. |
| 72 | 1 | `...001` | `10110000` | F | T | F | No `101` tail. |
| 73 | 0 | `...010` | `10110000` | F | T | F | No `101` tail. |

---

### Position 74: F1 Match + isArmed → 💰 TRIGGER SET!

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **74** | **1** | `...101` | `10110000` | **T** | T | **T** |

**Step-by-step:**
1. `rawString += '1'` → ends with `"101"` (positions 72=1, 73=0, 74=1)
2. **📍 F1 MATCH!** → `waitF1 = TRUE`
3. **💰 TRIGGER SET!** `isArmed = TRUE` AND rawString ends with `"101"` → **`pending = TRUE`**

> **TRIGGER SET!** Next bit (position 75) will be a real trade.

---

### Position 75: 🔥 THIRD REAL TRADE!

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| **75** | **1** | `...011` | `101100001` | F | **F** | F |

**Step-by-step:**
1. **`pending` was `TRUE`** → **🔥 REAL TRADE!** Bit `'1'` at position 75 is recorded as trade outcome
2. `rawString += '1'`
3. `waitF1` was `TRUE` → bit `'1'` feeds into `f1` → `f1 = "101100001"`
4. **❌ F2 NO MATCH!** `f1 = "101100001"` does NOT end with `"00"` → **`isArmed = FALSE`**
5. Check F1 on `rawString`: ends with `"011"`, not `"101"` → `waitF1 = FALSE`

> **Trade #3 recorded: `1` (WIN)** at position 75!
>
> **Important:** This trade finally broke the losing streak. But notice: `isArmed` is now `FALSE` because `f1` no longer ends with `"00"`. The filter is **disarmed** and must wait for another F2 match before firing again.

---

### Positions 76–80: Disarmed, Waiting for New Cycle

| Pos | Bit | rawString (tail) | f1 | waitF1 | isArmed | pending |
|-----|-----|------------------|----|--------|---------|---------|
| 76 | 0 | `...110` | `101100001` | F | F | F | No `101` tail. |
| 77 | 1 | `...101` | `101100001` | **T** | F | F | 📍 F1 MATCH! waitF1=TRUE |
| 78 | 0 | `...010` | `1011000010` | F | F | F | LAYER1: bit '0' → f1="1011000010". ❌ F2 no match. |
| 79 | 1 | `...101` | `1011000010` | **T** | F | F | 📍 F1 MATCH! waitF1=TRUE |
| 80 | 0 | `...010` | `10110000100` | F | **T** | F | LAYER1: bit '0' → f1="10110000100". ✅ F2 MATCH! isArmed=TRUE |

> At position 80, the filter becomes **armed again** (`f1` ends with `"00"`), but the string ends here. No more bits to trade on.

---

## 4. Summary of Real Trades

| Trade # | Position | Bit | Outcome | Trigger Set At |
|---------|----------|-----|---------|----------------|
| 1 | **55** | `0` | ❌ LOSS | Position 54 |
| 2 | **68** | `0` | ❌ LOSS | Position 67 |
| 3 | **75** | `1` | ✅ WIN | Position 74 |

**Trade string:** `001`

**Win rate:** 1/3 = **33.3%**

> **Note:** With only 80 bits, this is a very small sample. The full 774-bit string yields a 50% win rate with this particular F1/F2 combo. The purpose here is to show **HOW** the decisions are made, not to prove profitability.

---

## 5. Visual Timeline

```
Position:  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19 20
Bit:       1  1  0  0  1  1  1  1  1  1  1  1  1  0  1  1  0  0  0  1
Action:    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  S  F1 ·  ·  ·  ·

Position: 21 22 23 24 25 26 27 28 29 30 31 32 33 34 35 36 37 38 39 40
Bit:       1  1  1  1  1  1  1  0  0  0  1  0  0  1  0  1  0  1  1  1
Action:    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  S  F1 S  F1 ·

Position: 41 42 43 44 45 46 47 48 49 50 51 52 53 54 55 56 57 58 59 60
Bit:       1  0  1  1  1  0  1  0  0  1  0  1  0  1  0  0  1  1  1  1
Action:    ·  ·  S  F1 ·  ·  S  F1 ·  ·  ·  S  F2 S  🔥  ·  ·  ·  ·  ·
                                              ↑     ↑  ↑
                                           armed trigger trade

Position: 61 62 63 64 65 66 67 68 69 70 71 72 73 74 75 76 77 78 79 80
Bit:       1  1  0  0  1  0  1  0  0  0  0  1  0  1  1  0  1  0  1  0
Action:    ·  ·  ·  ·  ·  ·  S  🔥  ·  ·  ·  ·  ·  S  🔥  ·  S  F1 S  F2
                                              ↑  ↑        ↑  ↑
                                           trigger trade  trigger trade

Legend:
  ·  = No action
  S  = F1 match (setup) — waitF1 set
  F1 = Bit fed into f1 (Layer 1 outcome)
  F2 = f1 ends with F2 pattern — isArmed set
  🔥 = REAL TRADE (bit recorded as outcome)
```

---

## 6. The Decision Tree

```
                    Start
                      │
                      ▼
            ┌─────────────────┐
            │  Append bit to  │
            │    rawString    │
            └────────┬────────┘
                     │
                     ▼
         ┌───────────────────────┐
         │  waitF1 == TRUE?      │
         └───────────┬───────────┘
                     │
         ┌───────────┴───────────┐
         │ YES                   │ NO
         ▼                       ▼
    ┌─────────┐            ┌─────────┐
    │ Feed bit│            │ Do not  │
    │ into f1 │            │ feed f1 │
    └────┬────┘            └────┬────┘
         │                      │
         ▼                      ▼
    ┌─────────┐            ┌─────────┐
    │ f1 ends │            │ Check if│
    │ with F2?│            │rawString│
    └────┬────┘            │ends with│
         │                │   F1?   │
    ┌────┴────┐           └────┬────┘
    │ YES     │ NO             │
    ▼         ▼          ┌─────┴─────┐
┌───────┐ ┌───────┐      │ YES       │ NO
│isArmed│ │isArmed│      ▼           ▼
│= TRUE │ │= FALSE│  ┌────────┐  ┌────────┐
└───┬───┘ └───────┘  │waitF1= │  │waitF1= │
    │                │ TRUE   │  │ FALSE  │
    │                └────────┘  └────────┘
    │                     │
    └─────────────────────┘
                          │
                          ▼
              ┌─────────────────────┐
              │ isArmed==TRUE AND   │
              │ rawString ends with │
              │        F1?          │
              └──────────┬──────────┘
                         │
              ┌──────────┴──────────┐
              │ YES                 │ NO
              ▼                     ▼
        ┌───────────┐         ┌───────────┐
        │ pending=  │         │ pending=  │
        │  TRUE     │         │  FALSE    │
        │(next bit  │         │(no trade  │
        │ = trade)  │         │ this pos) │
        └─────┬─────┘         └───────────┘
              │
              ▼
        ┌───────────┐
        │ Next bit:  │
        │ Record as  │
        │ REAL TRADE │
        └───────────┘
```

---

## 7. Key Insights from This Example

### 7.1 The Filter is "Stateful"

The filter remembers its state across positions:
- `isArmed` can stay `TRUE` for many positions (e.g., positions 53–74)
- Once armed, every subsequent F1 match triggers a trade
- The filter only disarms when f1 no longer ends with F2

### 7.2 Trades Happen on the *Next* Bit

When `pending = TRUE` at position *i*, the trade is recorded at position *i+1*. This is because:
- The trigger is set when we *detect* the pattern
- The trade outcome is the *next* bit that arrives

### 7.3 Not Every F1 Match Leads to a Trade

Out of 9 F1 matches in this 80-bit sample, only 3 became real trades. Why?
- Some F1 matches happened when `isArmed = FALSE` (filter not ready)
- The filter only fires when BOTH conditions are met: `isArmed = TRUE` AND F1 match

### 7.4 The Layer 1 String (f1) Grows Slowly

`f1` only grows when `waitF1 = TRUE` — i.e., only the bits *immediately after* F1 matches are fed into `f1`. In this example:
- `f1` started empty
- After 80 positions, `f1 = "10110000100"` (only 11 bits fed)
- Most bits never enter `f1` — they only live in `rawString`

---

## 8. Full Results for the 774-Bit String

Running the same F1="101", F2="00" combo on the complete string:

| Metric | Value |
|--------|-------|
| String length | 774 |
| Baseline P(1) | 57.1% (442/774) |
| Layer 1 P(1) | 50.5% (56/111) |
| **Layer 2 (Trade) P(1)** | **50.0% (14/28)** |
| Fire rate | 3.6% of bits (~1 per 27.6 bits) |
| Max 0-in-a-row (losing) | 2 |
| Max 1-in-a-row (winning) | 3 |
| Trade string | `0010101101110011011001001001` |

> **Note:** This particular F1/F2 combo (`"101"` / `"00"`) is NOT a good filter for this data — the win rate didn't improve over baseline. The default combo (`"011"` / `"1*11"`) achieves 85.7%. This example was chosen for clarity of explanation, not for profitability. In practice, you would optimize F1 and F2 by testing many combinations.

---

## 9. How to Read the Complete Trace Table

For reference, here is the compact trace of all 80 positions:

| Pos | Bit | rawString (last 15) | f1 | wait | armed | pend | Key Events |
|-----|-----|---------------------|----|------|-------|------|------------|
| 1–14 | various | various | `` | F | F | F | Nothing — no `"101"` tail |
| **15** | **1** | `...101` | `` | **T** | F | F | 📍 First F1 match |
| 16 | 1 | `...011` | `1` | F | F | F | F1: bit fed, no F2 match |
| 17–35 | various | various | `1` | F | F | F | Quiet period |
| **36** | **1** | `...101` | `1` | **T** | F | F | 📍 F1 match |
| 37 | 0 | `...010` | `10` | F | F | F | F1: bit fed, no F2 match |
| **38** | **1** | `...101` | `10` | **T** | F | F | 📍 F1 match |
| 39 | 1 | `...011` | `101` | F | F | F | F1: bit fed, no F2 match |
| 40–42 | various | various | `101` | F | F | F | Quiet |
| **43** | **1** | `...101` | `101` | **T** | F | F | 📍 F1 match |
| 44 | 1 | `...011` | `1011` | F | F | F | F1: bit fed, no F2 match |
| 45–46 | various | various | `1011` | F | F | F | Quiet |
| **47** | **1** | `...101` | `1011` | **T** | F | F | 📍 F1 match |
| 48 | 0 | `...010` | `10110` | F | F | F | F1: bit fed, no F2 match |
| 49–51 | various | various | `10110` | F | F | F | Quiet |
| **52** | **1** | `...101` | `10110` | **T** | F | F | 📍 F1 match |
| **53** | **0** | `...010` | `101100` | F | **T** | F | ✅ **F2 MATCH! isArmed=TRUE** |
| **54** | **1** | `...101` | `101100` | **T** | T | **T** | 📍 F1 match + 💰 TRIGGER SET |
| **55** | **0** | `...010` | `1011000` | F | T | F | 🔥 **REAL TRADE: 0 (LOSS)** |
| 56–66 | various | various | `1011000` | F | T | F | Armed, waiting for F1 |
| **67** | **1** | `...101` | `1011000` | **T** | T | **T** | 📍 F1 match + 💰 TRIGGER SET |
| **68** | **0** | `...010` | `10110000` | F | T | F | 🔥 **REAL TRADE: 0 (LOSS)** |
| 69–73 | various | various | `10110000` | F | T | F | Armed, waiting |
| **74** | **1** | `...101` | `10110000` | **T** | T | **T** | 📍 F1 match + 💰 TRIGGER SET |
| **75** | **1** | `...011` | `101100001` | F | **F** | F | 🔥 **REAL TRADE: 1 (WIN)** — F2 lost, disarmed |
| 76 | 0 | `...110` | `101100001` | F | F | F | Quiet |
| **77** | **1** | `...101` | `101100001` | **T** | F | F | 📍 F1 match |
| 78 | 0 | `...010` | `1011000010` | F | F | F | F1: bit fed, no F2 match |
| **79** | **1** | `...101` | `1011000010` | **T** | F | F | 📍 F1 match |
| **80** | **0** | `...010` | `10110000100` | F | **T** | F | ✅ **F2 MATCH! isArmed=TRUE** (string ends) |

---

## 10. Conclusion

The Layer-2 filter is a **state machine** that:

1. **Watches** `rawString` for pattern F1 (`"101"`)
2. **Captures** the next bit into `f1` (Layer 1 outcome)
3. **Checks** if `f1` ends with pattern F2 (`"00"`) → arms the filter
4. **Waits** for another F1 match in `rawString`
5. **Fires** a real trade on the *next* bit after the armed F1 match

> **The trade bit is always one position AFTER the trigger.** This is because the trigger detects a pattern in the *past*, and the trade outcome is the *future* bit that arrives next.

This is **meta labeling** in its purest form: instead of trading every bit, we trade only when the historical context (captured by F1 and F2 patterns) suggests a high-probability outcome.

---

*Document generated for educational purposes. The step-by-step trace uses 1-based indexing and the exact pipeline from the uploaded Layer-2 wildcard tester.*
