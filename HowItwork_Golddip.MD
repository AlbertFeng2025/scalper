# Michelle_Golddip — TradingView Indicator User Guide

**Version:** 1.0 (Pine Script v5)  
**Converted from:** NinjaTrader (C#) by Albert Feng / Claude  
**Purpose:** Detect institutional bottom-fishing entries on any intraday chart

---

## Table of Contents

1. [What This Indicator Does](#1-what-this-indicator-does)
2. [How the Signal Works](#2-how-the-signal-works)
3. [Installation](#3-installation)
4. [The Full Pine Script Code](#4-the-full-pine-script-code)
5. [Parameters](#5-parameters)
6. [Reading the Chart](#6-reading-the-chart)
7. [Recommended Settings](#7-recommended-settings)
8. [Setting Up Alerts](#8-setting-up-alerts)
9. [Trading Guidelines](#9-trading-guidelines)
10. [Limitations and Warnings](#10-limitations-and-warnings)
11. [Technical Reference](#11-technical-reference)

---

## 1. What This Indicator Does

**Michelle_Golddip** detects moments when institutional buyers ("smart money") are likely
bottom-fishing — aggressively buying into a sell-off at extreme price lows.

The signal fires when **two conditions align simultaneously:**

1. Price has just made a **new 33-bar low** (a genuine capitulation dip, not a minor pullback)
2. The internal dip-magnitude oscillator **starts rising** (absorption of selling pressure begins)

The combination means: price dipped to an extreme low AND the character of that dip is
changing from panic to recovery. This is the earliest signal that a reversal may be forming.

**This is a LONG-only signal.** It is designed for buying dips, not shorting rallies.

---

## 2. How the Signal Works

The indicator computes a five-step oscillator internally:

### Step 1 — Previous bar's typical price (var1)
```
var1 = (Low[1] + Open[1] + Close[1] + High[1]) / 4
```
This is the HLC4 (average of all four prices) of the **previous** bar. It acts as a
reference level — where was "fair value" one bar ago?

### Step 2 — Dip ratio (var2)
```
var2Num = EMA( |Low - var1|, 25 )      ← total distance from prev HLC4
var2Den = EMA( Max(Low - var1, 0), 19) ← only the UPWARD portion
var2    = var2Num / var2Den
```
When today's Low is **below** var1, the denominator (Max term) equals zero, making
the ratio spike upward. This ratio measures how aggressively price dipped below the
previous bar's typical price. A high ratio = a sharp, one-sided dip downward.

### Step 3 — Smoothing (var3)
```
var3 = EMA(var2, 10)
```
A 10-period EMA smooths the noisy ratio into a cleaner oscillator line.

### Step 4 — 33-bar low filter (condValue)
```
var4     = Lowest Low of last 33 bars
condValue = var3 if Low <= var4, else 0
```
The oscillator only passes through when price is at or below its **33-bar lowest low**.
At all other times, condValue = 0. This ensures the signal only fires at genuine
extreme lows, not ordinary pullbacks.

### Step 5 — Final smoothing (var5)
```
var5 = EMA(condValue, 3)
```
A 3-period EMA gives the final oscillator value that the signal logic reads.

### Signal trigger
```
isRising    = var5 > var5[1]                              ← oscillator turning up
isFirstRise = isRising AND (var5[1] <= var5[2] OR var5[1] == 0)
```
The signal fires on the **first bar** where var5 starts rising, after it was flat or
falling. Subsequent rising bars do NOT re-trigger — only the first turn-up fires.
This prevents alert flooding during a sustained recovery.

---

## 3. Installation

### Step-by-step

1. **Open TradingView** at [tradingview.com](https://www.tradingview.com) and open any chart.

2. **Open Pine Editor** — click the `Pine Editor` tab at the very bottom of the screen.
   If you don't see it, click `...` in the bottom toolbar and select Pine Editor.

3. **Clear the editor** — select all existing code (Ctrl+A / Cmd+A) and delete it.

4. **Paste the code** — copy the entire Pine Script from [Section 4](#4-the-full-pine-script-code)
   below and paste it into the editor.

5. **Save the script** — click the **Save** button (floppy disk icon, top-right of editor).
   When prompted, name it `Michelle_Golddip` and click Save.

6. **Add to chart** — click the **Add to chart** button next to Save.

7. **Verify** — you should now see:
   - A new pane below the main price chart with red (and optionally green) oscillator bars
   - Green `▲` triangles and `ENTRY` labels on the main price chart wherever signals fired

> **Note:** TradingView may show a warning: *"The script may take a long time to execute."*
> This is normal for indicators using `label.new()`. Click **OK** to proceed.

---

## 4. The Full Pine Script Code

Copy everything below this line, from `//@version=5` to the last line:

```pine
//@version=5
indicator(title="Michelle_Golddip", shorttitle="GoldDip", overlay=false)

// ---- Parameters ----
barThickness = input.int(5, "Bar Thickness", minval=1, maxval=15,
    tooltip="Thickness of the oscillator bars. Range 1-15, default 5.")
showWashout = input.bool(false, "Show Washout Bars",
    tooltip="Show green bars when oscillator is falling. Off by default.")

// ---- Core calculation ----
// var1: HLC4 of previous bar (reference level)
var1 = (low[1] + open[1] + close[1] + high[1]) / 4.0

// var2: dip-magnitude ratio
var2Num = math.abs(low - var1)
var2Den = math.max(low - var1, 0.0)
var2NumEMA = ta.ema(var2Num, 25)
var2DenEMA = ta.ema(var2Den, 19)
var2 = var2DenEMA != 0 ? var2NumEMA / var2DenEMA : 0.0

// var3: smoothed dip ratio
var3 = ta.ema(var2, 10)

// var4: 33-bar lowest low
var4 = ta.lowest(low, 33)

// condValue: pass var3 only at new 33-bar lows
condValue = low <= var4 ? var3 : 0.0

// var5: final oscillator
var5 = ta.ema(condValue, 3)

// ---- Signal detection ----
isRising    = var5 > var5[1]
isFirstRise = isRising and (var5[1] <= var5[2] or var5[1] == 0)

// ---- Oscillator plots (in indicator pane) ----
plot(isRising ? var5 : na,
    title     = "Main Force Entry",
    color     = color.red,
    style     = plot.style_columns,
    linewidth = barThickness)

plot(showWashout and not isRising ? var5 : na,
    title     = "Washout",
    color     = color.green,
    style     = plot.style_columns,
    linewidth = barThickness)

hline(0, "Zero", color=color.new(color.gray, 70), linestyle=hline.style_dotted)

// ---- Signal markers (on price chart) ----
if isFirstRise and bar_index >= 34
    label.new(
        x         = bar_index,
        y         = low - syminfo.mintick * 30,
        text      = "▲",
        xloc      = xloc.bar_index,
        yloc      = yloc.price,
        color     = color.new(color.lime, 0),
        textcolor = color.lime,
        style     = label.style_none,
        size      = size.normal)
    label.new(
        x         = bar_index,
        y         = low - syminfo.mintick * 60,
        text      = "ENTRY",
        xloc      = xloc.bar_index,
        yloc      = yloc.price,
        color     = color.new(color.white, 100),
        textcolor = color.white,
        style     = label.style_none,
        size      = size.small)

// ---- Alert condition ----
alertcondition(isFirstRise and bar_index >= 34,
    title   = "GoldDip Entry Signal",
    message = "Michelle_Golddip: ENTRY signal fired on {{ticker}} {{interval}}")
```

---

## 5. Parameters

Open indicator settings by clicking the **gear icon** next to the indicator name on the chart,
or double-clicking any oscillator bar in the indicator pane.

| Parameter | Type | Default | Range | Description |
|---|---|---|---|---|
| **Bar Thickness** | Integer | 5 | 1 – 15 | Visual thickness of the oscillator bars in the indicator pane. Increase if bars are hard to see on smaller screens. |
| **Show Washout Bars** | Boolean | OFF | ON / OFF | When ON, displays green bars in the indicator pane when the oscillator is falling (the "washout" phase). When OFF, only the rising red bars are shown. Beginners should leave this OFF. |

---

## 6. Reading the Chart

### Main price chart
| Visual element | Meaning |
|---|---|
| Green `▲` triangle below a candle | Signal bar — first bar of oscillator recovery at a 33-bar low |
| White `ENTRY` text below triangle | Confirms the same signal bar visually |

### Indicator pane (below price chart)
| Visual element | Meaning |
|---|---|
| **Red bars** | Oscillator is rising — dip absorption in progress |
| **Green bars** (if enabled) | Oscillator is falling — washout / selling pressure continuing |
| **No bars / gap** | Price is not at a 33-bar low — signal is inactive |
| **Height of bars** | Magnitude of the dip relative to the previous bar's typical price |

### What a complete signal sequence looks like

```
Bars:    [falling] [falling] [NEW LOW] [▲ ENTRY] [recovery] [recovery]
Osc:          0        0       green     RED         RED        RED
Chart:                              ▲ ENTRY label appears here
```

1. Price falls to a new 33-bar low → oscillator activates (may show green washout bars)
2. Oscillator turns up for the first time → **ENTRY signal fires** (red bars begin, triangle appears)
3. If price recovers, oscillator continues red while rising
4. Signal does not re-fire until a new sequence begins

---

## 7. Recommended Settings

### Timeframe
| Timeframe | Use case |
|---|---|
| **1-minute** | Aggressive intraday scalping (MNQ, NQ, ES) |
| **3-minute** | Balanced intraday — fewer but higher quality signals |
| **5-minute** | Conservative intraday — best for beginners |
| **15-minute+** | Swing trade entries — signals are rarer but more significant |

### Instrument
The indicator was designed for **index futures** (MNQ, NQ, ES, MES) but works on any
liquid instrument. It performs best on instruments with:
- Clear intraday trends
- Regular dip-and-recover patterns
- High liquidity (tight spreads)

### Suggested stop loss placement
Place your stop loss **below the signal bar's low**, with additional buffer based on
recent volatility. The indicator does not compute stops — that is your responsibility.

### Suggested profit target
The indicator does not compute targets. Common approaches:
- Fixed points (e.g. 7-10 points on MNQ)
- Next resistance level
- Trailing stop once in profit

---

## 8. Setting Up Alerts

TradingView can send you a notification (pop-up, email, SMS, webhook) when the signal fires.

### Steps to create an alert

1. Click the **bell icon** (🔔) in the top toolbar of TradingView, or right-click on
   the chart and select **Add alert**.

2. In the alert dialog:
   - **Condition:** Select `Michelle_Golddip` from the first dropdown
   - **Condition (second dropdown):** Select `GoldDip Entry Signal`
   - **Trigger:** `Once per bar close` is recommended (avoids false alerts mid-bar)

3. **Notification options** — enable whichever you need:
   - `Notify on app` — push notification to TradingView mobile app
   - `Send email` — email to your registered TradingView address
   - `Webhook URL` — for integration with bots or external systems

4. **Alert message** — the default message is:
   ```
   Michelle_Golddip: ENTRY signal fired on {{ticker}} {{interval}}
   ```
   You can customize this. The `{{ticker}}` and `{{interval}}` placeholders are
   replaced automatically with the current symbol and timeframe.

5. Click **Create** to save the alert.

> **Important:** TradingView alerts only fire on **closed bars** when set to
> `Once per bar close`. This prevents false signals from intrabar price movement.
> Always use this setting for this indicator.

---

## 9. Trading Guidelines

### Entry
- Wait for the green `▲` triangle and `ENTRY` label to appear
- Enter **long** at market or with a limit order slightly below the signal bar's close
- Do NOT chase — if price has already moved 5+ points from the signal bar, skip the trade

### Stop loss
- Place below the **low of the signal bar** (the bar with the triangle)
- Add a small buffer (1-3 points on MNQ) to avoid being stopped out by noise
- Never move your stop loss further away once set

### Profit target
- Consider a fixed reward-to-risk ratio (minimum 1:1, ideally 1:1.5 or better)
- Watch for nearby resistance levels — take profit before reaching them
- Consider a trailing stop once the trade is in profit by at least 1× your stop distance

### Position sizing
- Risk no more than 1-2% of your account per trade
- For MNQ: each point = $2. A 10-point stop on 1 contract = $20 risk.
- Scale position size based on your stop distance, not a fixed contract count

### What to avoid
- **Do not trade every signal** — use context. If the broader trend is strongly down,
  skip long signals or use smaller size.
- **Do not ignore commission and slippage** in live trading. A 7-point target with
  a 20-point stop requires a very high win rate just to break even.
- **Do not use this as your only indicator** — combine with trend direction,
  support/resistance levels, and volume if possible.

---

## 10. Limitations and Warnings

### This indicator is NOT a complete trading system
It provides entry signals only. Stop loss, profit target, position sizing, and risk
management are entirely your responsibility.

### Backtesting results may be misleading
TradingView's built-in backtester fills limit orders on bar touch, which is optimistic.
Real fills depend on order book depth, spread, and broker execution speed.

### The signal can repaint on the current bar
Like all indicators, the signal is only final when the bar **closes**. During an open
bar, the signal may appear and then disappear before bar close. Always wait for bar
close confirmation before acting — use `Once per bar close` in alerts.

### High win rate ≠ profitable strategy
A signal with a tight profit target and wide stop loss can have a high win rate but
still lose money. Always evaluate your **profit factor** (gross profit / gross loss)
and **expectancy** (average win × win rate − average loss × loss rate), not just win rate.

### Past performance does not guarantee future results
Market conditions change. A signal that worked well in trending markets may underperform
in choppy or low-volatility periods.

---

## 11. Technical Reference

### Indicator metadata
| Property | Value |
|---|---|
| Pine Script version | v5 |
| Overlay | No (separate pane below price) |
| Minimum bars required | 34 |
| Plots | 2 (Main Force Entry, Washout) |
| Labels | On price chart (triangle + text) |
| Alert conditions | 1 (GoldDip Entry Signal) |

### Internal variable summary
| Variable | Formula | Purpose |
|---|---|---|
| `var1` | HLC4 of previous bar | Previous bar reference price |
| `var2Num` | EMA(\|Low − var1\|, 25) | Smoothed total distance from reference |
| `var2Den` | EMA(Max(Low − var1, 0), 19) | Smoothed upward-only distance |
| `var2` | var2Num / var2Den | Dip-magnitude ratio (spikes when dipping below reference) |
| `var3` | EMA(var2, 10) | Smoothed dip ratio |
| `var4` | Lowest(Low, 33) | 33-bar lowest low |
| `condValue` | var3 if Low ≤ var4, else 0 | Filtered: only active at new 33-bar lows |
| `var5` | EMA(condValue, 3) | Final oscillator |
| `isRising` | var5 > var5[1] | Oscillator turning up |
| `isFirstRise` | isRising AND (var5[1] ≤ var5[2] OR var5[1] == 0) | First bar of new rising move |

### Differences from NinjaTrader original
| Aspect | NinjaTrader | TradingView |
|---|---|---|
| Language | C# / NinjaScript | Pine Script v5 |
| Series objects | `Series<double>` (explicit) | Native Pine series (implicit) |
| EMA function | `EMA(series, period)` | `ta.ema(series, period)` |
| Lowest low | `MIN(Low, 33)` | `ta.lowest(low, 33)` |
| Triangle drawing | `Draw.TriangleUp()` | `label.new()` with `▲` character |
| Text drawing | `Draw.Text()` | `label.new()` |
| Alert system | NinjaTrader alert manager | `alertcondition()` + TradingView alerts |
| Oscillator plots | `AddPlot()` with Bar style | `plot()` with `plot.style_columns` |

---

*Document prepared by Albert Feng / Claude. For questions or updates, refer to the
original NinjaTrader source code and compare against this Pine Script implementation.*
