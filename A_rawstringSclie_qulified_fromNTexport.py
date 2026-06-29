# -*- coding: utf-8 -*-
"""
build_historical_recorder_with_qualified.py
--------------------------------------------
Parses high-resolution historical tick data and replicates the NinjaScript
'LONG_SHORT_rawString_recorder' slice logic, AND (optionally) builds THREE
"qualified" subsequences of the raw string based on per-slice metadata:

  RAW string             : every resolved slice's bit (the baseline; identical
                           to the original build_historical_recorder output).
  VOLUME-QUALIFIED       : only bits whose slice traded within the volume range
                           (e.g., between 1.5x and 100x trailing-avg volume).
  SPEED-QUALIFIED        : only bits whose slice resolved within the speed range
                           (e.g., between 1.5x and 100x faster than recent avg).
  DUAL-QUALIFIED         : only bits that passed BOTH the volume and speed tests.

WHY: the bare 1/0 throws away HOW a bit formed. A '1' that ripped 20pts in
0.3s on heavy volume is a different event than a '1' that drifted there in 8s
on thin volume. The qualified strings keep only the "high-conviction" bits, so
we can test whether filtering on conviction lifts P(1) vs the raw string.

NT tick replay format (confirmed against NinjaTrader docs):
    yyyyMMdd HHmmss fffffff;last;bid;ask;volume
    index:   0(ts)          1   2   3   4
So bid = parts[2], ask = parts[3], volume = parts[4].

USAGE: set BUILD_QUALIFIED=False to reproduce the OLD raw-only output exactly,
or True to also emit the qualified strings. Try ONE day first.
"""

import os
import time
from collections import deque
from datetime import datetime


def generate_raw_string_from_history(
    source_file: str,
    result_file: str,
    direction: str,
    profit_take: float,
    stop_loss: float,
    check_interval_seconds: int,
    build_qualified: bool = False,
    lookback: int = 10,
    vol_lower_bound: float = 1.5,
    vol_upper_bound: float = 100.0,
    spd_lower_bound: float = 1.5,
    spd_upper_bound: float = 100.0,
):
    direction = direction.upper()
    if direction not in ["LONG", "SHORT"]:
        raise ValueError("Error: direction parameter must be strictly 'LONG' or 'SHORT'.")

    binary_chars = []          # RAW string
    vol_qualified_chars = []   # VOLUME-qualified subsequence
    spd_qualified_chars = []   # SPEED-qualified subsequence
    dual_qualified_chars = []  # BOTH Volume AND Speed qualified subsequence

    # State Machine Variables (Matching C# Defaults)
    in_slice = False
    slice_entry_price = 0.0
    slice_stop_price = 0.0
    slice_target_price = 0.0
    last_check_time = -999999.0   # Simulates DateTime.MinValue

    # --- per-slice metadata accumulators (only used if build_qualified) ---
    slice_start_time = 0.0        # build-time clock start (when slice opened)
    slice_volume_accum = 0.0      # volume traded during the open slice

    # trailing windows of the last `lookback` slices' metadata (all slices)
    recent_volumes = deque(maxlen=lookback)
    recent_buildtimes = deque(maxlen=lookback)

    # High-speed date tracking cache variables
    cached_date_str = ""
    base_day_seconds = 0.0

    print("=" * 65)
    print("MATCHING NINJASCRIPT RECORDER LOGIC" + (" + QUALIFIED" if build_qualified else ""))
    print(f"   Direction      : {direction}")
    print(f"   Profit Target  : {profit_take} points")
    print(f"   Stop Loss      : {stop_loss} points")
    print(f"   Time Throttle  : {check_interval_seconds} second(s)")
    if build_qualified:
        print(f"   Qualified      : ON  (lookback={lookback})")
        print(f"      Vol bounds  : [{vol_lower_bound}x, {vol_upper_bound}x] avg")
        print(f"      Spd bounds  : [{spd_lower_bound}x, {spd_upper_bound}x] avg")
    else:
        print(f"   Qualified      : OFF (raw string only)")
    print(f"   Processing     : {source_file}")
    print("=" * 65)

    start_perf_time = time.time()

    with open(source_file, 'r') as f:
        for line in f:
            line = line.strip()
            if not line or ";" not in line or line.startswith('#'):
                continue

            parts = line.split(';')
            try:
                ts_str = parts[0]            # "20260609 070002 8520000"
                bid = float(parts[2])
                ask = float(parts[3])
                vol = float(parts[4]) if len(parts) > 4 else 0.0
            except (IndexError, ValueError):
                continue

            if bid <= 0 or ask <= 0:
                continue

            # ---- fast timestamp parse (date cached; sub-second by slicing) ----
            date_part = ts_str[0:8]
            if date_part != cached_date_str:
                cached_date_str = date_part
                dt_base = datetime.strptime(date_part, "%Y%m%d")
                base_day_seconds = dt_base.timestamp()

            hours = int(ts_str[9:11])
            minutes = int(ts_str[11:13])
            seconds = int(ts_str[13:15])
            subseconds = int(ts_str[16:]) / 10000000.0
            current_time_secs = base_day_seconds + (hours * 3600 + minutes * 60 + seconds + subseconds)

            # ---- STATE MACHINE (replicating OnBarUpdate) ----
            if in_slice:
                # accumulate volume traded during the open slice
                if build_qualified:
                    slice_volume_accum += vol

                stop_hit = False
                target_hit = False
                if direction == "LONG":
                    stop_hit = (bid <= slice_stop_price)
                    target_hit = (ask >= slice_target_price)
                else:  # SHORT
                    stop_hit = (ask >= slice_stop_price)
                    target_hit = (bid <= slice_target_price)

                if stop_hit or target_hit:
                    # both-hit precedence: record stop (0), matching C#
                    bit = "0" if stop_hit else "1"
                    binary_chars.append(bit)

                    if build_qualified:
                        build_time = current_time_secs - slice_start_time
                        
                        # decide qualification using the trailing averages
                        if len(recent_volumes) == lookback:
                            avg_vol = sum(recent_volumes) / lookback
                            avg_bt = sum(recent_buildtimes) / lookback
                            
                            vol_ok = False
                            spd_ok = False

                            # VOLUME CHECK: bounded by lower and upper multipliers
                            if avg_vol > 0:
                                min_vol = vol_lower_bound * avg_vol
                                max_vol = vol_upper_bound * avg_vol
                                if min_vol <= slice_volume_accum <= max_vol:
                                    vol_ok = True
                                    vol_qualified_chars.append(bit)

                            # SPEED CHECK: bounded by lower and upper multipliers
                            # (A higher multiplier means less time. e.g., 5x faster = avg_bt / 5)
                            if avg_bt > 0:
                                min_time = avg_bt / spd_upper_bound if spd_upper_bound > 0 else 0
                                max_time = avg_bt / spd_lower_bound if spd_lower_bound > 0 else float('inf')
                                
                                if min_time <= build_time <= max_time:
                                    spd_ok = True
                                    spd_qualified_chars.append(bit)
                            
                            # DUAL CHECK: passed both volume and speed constraints
                            if vol_ok and spd_ok:
                                dual_qualified_chars.append(bit)

                        # update trailing windows with THIS slice (all slices)
                        recent_volumes.append(slice_volume_accum)
                        recent_buildtimes.append(build_time)

                    # reset slice state
                    in_slice = False
                    slice_entry_price = 0.0
                    slice_stop_price = 0.0
                    slice_target_price = 0.0

                continue  # replicates C# 'return;' inside if(inSlice)

            # ---- throttle new slice starts ----
            if (current_time_secs - last_check_time) < check_interval_seconds:
                continue

            # ---- start next slice ----
            last_check_time = current_time_secs
            in_slice = True
            if build_qualified:
                slice_start_time = current_time_secs
                slice_volume_accum = vol   # count the starting tick's volume too

            if direction == "LONG":
                slice_entry_price = bid
                slice_stop_price = slice_entry_price - stop_loss
                slice_target_price = slice_entry_price + profit_take
            else:  # SHORT
                slice_entry_price = ask
                slice_stop_price = slice_entry_price + stop_loss
                slice_target_price = slice_entry_price - profit_take

    # ---- compile and write ----
    raw_string = "".join(binary_chars)
    with open(result_file, 'w') as out_f:
        out_f.write(raw_string)

    if build_qualified:
        vol_string = "".join(vol_qualified_chars)
        spd_string = "".join(spd_qualified_chars)
        dual_string = "".join(dual_qualified_chars)
        
        base, ext = os.path.splitext(result_file)
        vol_path = base + "_VOLqualified" + ext
        spd_path = base + "_SPEEDqualified" + ext
        dual_path = base + "_DUALqualified" + ext
        
        with open(vol_path, 'w') as vf:
            vf.write(vol_string)
        with open(spd_path, 'w') as sf:
            sf.write(spd_string)
        with open(dual_path, 'w') as df:
            df.write(dual_string)

    execution_time = round(time.time() - start_perf_time, 2)

    print(f"PROCESS COMPLETED IN {execution_time} SECONDS")
    print(f"   Saved RAW    : {result_file}")
    print(f"   RAW length   : {len(raw_string):,} bits | P(1)="
          f"{(raw_string.count('1')/len(raw_string)*100 if raw_string else 0):.1f}%")
    print(f"   RAW head     : {raw_string[:80]}")
    if build_qualified:
        def p1(s): return (s.count('1') / len(s) * 100) if s else 0.0
        print("-" * 65)
        print(f"   VOL-qualified: {vol_path}")
        print(f"     length {len(vol_string):,} ({len(vol_string)/max(len(raw_string),1)*100:.0f}% of raw) "
              f"| P(1)={p1(vol_string):.1f}%")
        print(f"     head {vol_string[:80]}")
        print(f"   SPEED-qualified: {spd_path}")
        print(f"     length {len(spd_string):,} ({len(spd_string)/max(len(raw_string),1)*100:.0f}% of raw) "
              f"| P(1)={p1(spd_string):.1f}%")
        print(f"     head {spd_string[:80]}")
        print(f"   DUAL-qualified : {dual_path}")
        print(f"     length {len(dual_string):,} ({len(dual_string)/max(len(raw_string),1)*100:.0f}% of raw) "
              f"| P(1)={p1(dual_string):.1f}%")
        print(f"     head {dual_string[:80]}")
        print("-" * 65)
        print("   COMPARE: do the qualified strings beat RAW P(1)?")
    print("=" * 65)


# ── PARAMETER SELECTION BLOCK ───────────────────────────────────────────────
if __name__ == "__main__":

    PARAM_DIRECTION   = "LONG"     # "LONG" or "SHORT"
    PARAM_STOP_LOSS   = 20.0
    PARAM_PROFIT_TAKE = 20.0
    PARAM_THROTTLE    = 1          # match CheckIntervalSeconds

    # --- the new switch: False = old raw-only; True = also build qualified ---
    BUILD_QUALIFIED   = True
    LOOKBACK          = 10
    
    # Range Bounds Example: 
    # Between 1.5x and 100.0x the average volume/speed
    VOL_LOWER_BOUND   = 0.1
    VOL_UPPER_BOUND   = 0.5  
    
    SPD_LOWER_BOUND   = 0.1   
    SPD_UPPER_BOUND   = 0.5  
    
    FILE_SOURCE       = r"C:\Users\chpfe\Downloads\6-22MNQ 09-26.Last2.txt"
    FILE_RESULT       = r"C:\Users\chpfe\Downloads\qualifiedto23morningrawString_LONG_20stop_20profitmix.txt"

    generate_raw_string_from_history(
        source_file            = FILE_SOURCE,
        result_file            = FILE_RESULT,
        direction              = PARAM_DIRECTION,
        profit_take            = PARAM_PROFIT_TAKE,
        stop_loss              = PARAM_STOP_LOSS,
        check_interval_seconds = PARAM_THROTTLE,
        build_qualified        = BUILD_QUALIFIED,
        lookback               = LOOKBACK,
        vol_lower_bound        = VOL_LOWER_BOUND,
        vol_upper_bound        = VOL_UPPER_BOUND,
        spd_lower_bound        = SPD_LOWER_BOUND,
        spd_upper_bound        = SPD_UPPER_BOUND,
    )
