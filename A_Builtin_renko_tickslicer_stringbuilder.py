#!/usr/bin/env python3
# =============================================================================
#  BUILTIN_Renko_TickSlicer_StringBuilder.py
# -----------------------------------------------------------------------------
#  *** BUILT-IN RENKO  -  NOT UniRenko ***
#
#  Builds one raw 0/1 bit-string per New-York trading day directly from RAW
#  TICKS, replicating NinjaTrader's BUILT-IN Renko bar construction (80-tick
#  brick, standard 2x reversal). Because bricks are built tick-by-tick, the
#  string is faithful to how your LIVE built-in-Renko production bricks form -
#  it sidesteps Tick Replay (blocked on built-in Renko), the UniRenko
#  algorithm mismatch, and the Strategy Analyzer's minute approximation.
#
#  BIT RULE  (identical to the production Scalper_Renko_Transit* books)
#    Direction of each brick (Close[0] vs Close[1] equivalent):
#        up brick   = green = raw bit 1
#        down brick = red   = raw bit 0
#    INVERT_BITS = False -> green=1/red=0  (LONG book)
#    INVERT_BITS = True  -> red=1/green=0  (SHORT book; complement)
#
#  ** CALIBRATION REQUIRED before trusting output **
#    NinjaTrader's exact built-in Renko reversal/anchor behavior is fiddly and
#    version-dependent. The brick logic below is the STANDARD grid model
#    (continuation at +/-1 brick, reversal at -/+2 bricks). It is isolated in
#    RenkoBuilder so it can be tuned. Validate it by diffing one day against a
#    recorded production rawString (use --expect, see bottom). Tune until the
#    per-day match is bit-for-bit, THEN trust the rest.
#
#  TIME  - window is NEW YORK time, applied to the tick timestamp converted
#    from SOURCE_TZ. Both NY and PT (and source) are logged per day so you can
#    verify the window against a known event (e.g. 09:30 ET cash open).
#
#  INPUT  - NinjaTrader tick export lines, e.g.
#      20260111 230000 0960000;25946.5;25946.5;25946.5;62
#      (yyyyMMdd HHmmss fffffff ; last ; bid ; ask ; volume)
#    Pass one or more files, a glob, or a directory of such files.
#
#  OUTPUT - one line per NY day:  yyyy-MM-dd: 0110100...
#    Same format as the NinjaScript builders, so trade_filter.py reads it as-is.
#
#  Requires Python 3.9+ (zoneinfo in the standard library).
# =============================================================================

import sys
import os
import glob
import argparse
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

# =============================================================================
#  CONFIG  -  clearly labelled; adjust here or via CLI flags
# =============================================================================
TICK_SIZE          = 0.25          # MNQ: 1 tick = 0.25 index points
BRICK_TICKS        = 80            # brick size in ticks (80 ticks = 20.0 pts)
REVERSAL_MULTIPLE  = 2             # built-in Renko flips on a 2x move

# Trading window, NEW YORK time (inclusive both ends). Startup research 09:30-15:59.
# End ceiling for research = 16:59 (1 min before MNQ 17:00 ET close).
WIN_START_H, WIN_START_M = 9, 30
WIN_END_H,   WIN_END_M   = 15, 59

INVERT_BITS        = False         # False: green=1/red=0 (LONG). True: red=1 (SHORT).

# Time zone the RAW TICK timestamps are written in.
# CONFIRMED: NinjaTrader's exported historical/tick data is ALWAYS in UTC
# (there is no option to change it). So keep this UTC unless you pre-converted
# the file yourself. The per-day NY/PT log lets you sanity-check the 09:30 open.
SOURCE_TZ_ID       = "UTC"

OUTPUT_PATH        = "builtin_renko_research_string.txt"

# =============================================================================
#  Zone objects
# =============================================================================
NY_TZ  = ZoneInfo("America/New_York")
PT_TZ  = ZoneInfo("America/Los_Angeles")


# =============================================================================
#  RenkoBuilder  -  the ONE place the brick algorithm lives (calibration target)
# -----------------------------------------------------------------------------
#  Standard grid Renko, brick size B (price), reversal R = B * REVERSAL_MULTIPLE.
#  Continuation forms a brick 1*B beyond the last close; a reversal forms a
#  brick R beyond the last close in the opposite direction. A single tick can
#  complete several bricks (cascade), all tagged with that tick's time.
#  Each completed brick is (close_price, direction) with direction +1 up / -1 down.
# =============================================================================
class RenkoBuilder:
    def __init__(self, brick_price, reversal_mult=2):
        self.B = brick_price
        self.R = brick_price * reversal_mult
        self.last_close = None     # close of the last completed brick
        self.direction  = 0        # +1 up, -1 down, 0 none/seed

    def update(self, price):
        out = []
        if self.last_close is None:
            # Seed reference to the first tick price. (Anchor offset is a small
            # calibration point - affects only the first brick or two.)
            self.last_close = price
            return out

        while True:
            if self.direction >= 0:                       # currently up or neutral
                if price >= self.last_close + self.B:      # continuation up
                    self.last_close += self.B
                    self.direction = 1
                    out.append((self.last_close, 1))
                    continue
                if price <= self.last_close - self.R:       # reversal down (2x move)
                    self.last_close -= self.R
                    self.direction = -1
                    out.append((self.last_close, -1))
                    continue
                break
            else:                                          # currently down
                if price <= self.last_close - self.B:       # continuation down
                    self.last_close -= self.B
                    self.direction = -1
                    out.append((self.last_close, -1))
                    continue
                if price >= self.last_close + self.R:        # reversal up (2x move)
                    self.last_close += self.R
                    self.direction = 1
                    out.append((self.last_close, 1))
                    continue
                break
        return out


# =============================================================================
#  Tick parsing
# =============================================================================
def parse_tick_line(line, source_tz):
    """Return (aware_datetime, last_price) or None if the line can't be parsed."""
    line = line.strip()
    if not line:
        return None
    try:
        ts_part, rest = line.split(";", 1)
    except ValueError:
        return None

    toks = ts_part.split()
    # Expect: yyyyMMdd HHmmss [fffffff]
    if len(toks) < 2:
        return None
    date_s, hms_s = toks[0], toks[1]
    try:
        dt = datetime(
            int(date_s[0:4]), int(date_s[4:6]), int(date_s[6:8]),
            int(hms_s[0:2]),  int(hms_s[2:4]),  int(hms_s[4:6]),
        )
    except (ValueError, IndexError):
        return None
    dt = dt.replace(tzinfo=source_tz)     # tag as source zone (sub-second dropped; ticks stay in file order)

    fields = rest.split(";")
    try:
        last = float(fields[0])           # first field after timestamp = Last
    except (ValueError, IndexError):
        return None
    return dt, last


def iter_tick_files(paths):
    """Expand files/globs/dirs into a sorted list of tick file paths."""
    files = []
    for p in paths:
        if os.path.isdir(p):
            files.extend(glob.glob(os.path.join(p, "*.txt")))
        else:
            files.extend(glob.glob(p))
    # Sort by filename so dates process chronologically. Adjust if your naming differs.
    return sorted(set(files))


# =============================================================================
#  Core: build per-NY-day strings from the tick stream
# =============================================================================
def build_strings(tick_files, source_tz):
    builder = RenkoBuilder(BRICK_TICKS * TICK_SIZE, REVERSAL_MULTIPLE)

    start_min = WIN_START_H * 60 + WIN_START_M
    end_min   = WIN_END_H   * 60 + WIN_END_M

    days = {}   # ny_date_str -> {'bits': [...], 'first': (ny,pt), 'last': (ny,pt)}
    n_ticks = 0
    n_bricks = 0

    for path in tick_files:
        with open(path, "r", errors="ignore") as fh:
            for line in fh:
                parsed = parse_tick_line(line, source_tz)
                if parsed is None:
                    continue
                dt_src, price = parsed
                n_ticks += 1

                for close_px, direction in builder.update(price):
                    n_bricks += 1
                    ny = dt_src.astimezone(NY_TZ)
                    pt = dt_src.astimezone(PT_TZ)

                    ny_min = ny.hour * 60 + ny.minute
                    if ny_min < start_min or ny_min > end_min:
                        continue   # outside the NY window: brick still built, just not emitted

                    raw = 1 if direction > 0 else 0          # up=green=1, down=red=0
                    bit = (1 - raw) if INVERT_BITS else raw

                    key = ny.strftime("%Y-%m-%d")
                    d = days.get(key)
                    if d is None:
                        d = {"bits": [], "first": (ny, pt), "last": (ny, pt)}
                        days[key] = d
                    d["bits"].append("1" if bit == 1 else "0")
                    d["last"] = (ny, pt)

    return days, n_ticks, n_bricks


def write_output(days, out_path, source_tz):
    with open(out_path, "w") as out:
        out.write(
            "# BUILT-IN RENKO research string | brick %d ticks (%.2f pts) x%d reversal | "
            "window NY %02d:%02d-%02d:%02d | invert=%s (%s) | src_tz=%s | generated %s\n" % (
                BRICK_TICKS, BRICK_TICKS * TICK_SIZE, REVERSAL_MULTIPLE,
                WIN_START_H, WIN_START_M, WIN_END_H, WIN_END_M,
                INVERT_BITS, "red=1 SHORT" if INVERT_BITS else "green=1 LONG",
                SOURCE_TZ_ID, datetime.now().strftime("%Y-%m-%d %H:%M"),
            )
        )
        for key in sorted(days.keys()):
            d = days[key]
            s = "".join(d["bits"])
            if not s:
                continue
            out.write("%s: %s\n" % (key, s))

    # Verification log to console (both zones, per day).
    print("[CFG] brick %d ticks (%.2f pts) x%d reversal | window NY %02d:%02d-%02d:%02d | "
          "invert=%s | src_tz=%s" % (
              BRICK_TICKS, BRICK_TICKS * TICK_SIZE, REVERSAL_MULTIPLE,
              WIN_START_H, WIN_START_M, WIN_END_H, WIN_END_M, INVERT_BITS, SOURCE_TZ_ID))
    for key in sorted(days.keys()):
        d = days[key]
        (fny, fpt), (lny, lpt) = d["first"], d["last"]
        print("[DAY] %s | %4d bits | first NY %s / PT %s | last NY %s / PT %s" % (
            key, len(d["bits"]),
            fny.strftime("%H:%M:%S"), fpt.strftime("%H:%M:%S"),
            lny.strftime("%H:%M:%S"), lpt.strftime("%H:%M:%S")))


# =============================================================================
#  Optional calibration: diff produced strings vs a recorded rawString file
#  --expect FILE  where FILE has lines "yyyy-MM-dd: 0110..." (your prod log)
# =============================================================================
def diff_against(days, expect_path):
    expected = {}
    with open(expect_path, "r", errors="ignore") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#") or ":" not in line:
                continue
            k, v = line.split(":", 1)
            expected[k.strip()] = v.strip()

    print("\n[VALIDATE] comparing produced vs expected (%s)" % expect_path)
    any_day = False
    for key in sorted(set(days) & set(expected)):
        any_day = True
        prod = "".join(days[key]["bits"])
        exp  = expected[key]
        n = min(len(prod), len(exp))
        match = sum(1 for i in range(n) if prod[i] == exp[i])
        first_div = next((i for i in range(n) if prod[i] != exp[i]), None)
        pct = (100.0 * match / n) if n else 0.0
        print("  %s | len prod=%d exp=%d | match=%.1f%% | first divergence idx=%s" % (
            key, len(prod), len(exp), pct,
            "none" if first_div is None else str(first_div)))
    if not any_day:
        print("  (no overlapping dates between produced output and expected file)")


# =============================================================================
#  Main
# =============================================================================
def main():
    ap = argparse.ArgumentParser(description="Built-in Renko tick -> per-NY-day bit-string slicer.")
    ap.add_argument("inputs", nargs="+", help="tick file(s), glob(s), or directory of NT tick exports")
    ap.add_argument("-o", "--out", default=OUTPUT_PATH, help="output string file")
    ap.add_argument("--src-tz", default=SOURCE_TZ_ID, help="time zone of the tick timestamps")
    ap.add_argument("--expect", default=None, help="recorded rawString file to validate against (calibration)")
    args = ap.parse_args()

    source_tz = ZoneInfo(args.src_tz)
    files = iter_tick_files(args.inputs)
    if not files:
        print("No tick files found for: %s" % args.inputs)
        sys.exit(1)
    print("[CFG] %d tick file(s):" % len(files))
    for f in files:
        print("      " + f)

    days, n_ticks, n_bricks = build_strings(files, source_tz)
    write_output(days, args.out, source_tz)
    print("[DONE] %d ticks -> %d bricks -> %d day-strings written to %s" % (
        n_ticks, n_bricks, len([d for d in days.values() if d['bits']]), args.out))

    if args.expect:
        diff_against(days, args.expect)


if __name__ == "__main__":
    main()
