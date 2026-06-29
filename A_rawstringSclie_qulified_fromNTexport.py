# -*- coding: utf-8 -*-
"""
build_historical_recorder_with_qualified.py   (v2 - causal fix)
---------------------------------------------------------------
Replicates the NinjaScript 'LONG_SHORT_rawString_recorder' slice logic, and
(optionally) builds qualified subsequences based on per-slice metadata.

CRITICAL FIX (the look-ahead leak):
A slice's volume and build-time are only known AFTER it resolves - at which
point its own bit is already determined. Selecting a bit using its OWN slice's
volume/speed is LOOK-AHEAD. Correct causal rule: a qualifying slice is a
CONDITION; the tradeable outcome is the NEXT slice's bit.

  RAW             : every resolved slice's bit (baseline).
  VOLUME-QUALIFIED: bit AFTER each slice whose volume is in the vol range.
  SPEED-QUALIFIED : bit AFTER each slice whose build-time is in the spd range.
  DUAL-QUALIFIED  : bit AFTER each slice that passed BOTH.

NT format: yyyyMMdd HHmmss fffffff;last;bid;ask;volume  (bid=2, ask=3, vol=4)
BUILD_QUALIFIED=False reproduces the OLD raw-only output exactly.
"""
import os, time
from collections import deque
from datetime import datetime


def generate_raw_string_from_history(source_file, result_file, direction,
        profit_take, stop_loss, check_interval_seconds,
        build_qualified=False, lookback=10,
        vol_lower_bound=1.5, vol_upper_bound=100.0,
        spd_lower_bound=1.5, spd_upper_bound=100.0):

    direction = direction.upper()
    if direction not in ["LONG", "SHORT"]:
        raise ValueError("direction must be 'LONG' or 'SHORT'.")

    binary_chars = []
    vol_qualified_chars = []; spd_qualified_chars = []; dual_qualified_chars = []
    vol_take_next = False; spd_take_next = False; dual_take_next = False

    in_slice = False
    slice_entry_price = 0.0; slice_stop_price = 0.0; slice_target_price = 0.0
    last_check_time = -999999.0
    slice_start_time = 0.0; slice_volume_accum = 0.0
    recent_volumes = deque(maxlen=lookback); recent_buildtimes = deque(maxlen=lookback)
    cached_date_str = ""; base_day_seconds = 0.0

    print("=" * 65)
    print("RECORDER LOGIC" + (" + QUALIFIED (causal)" if build_qualified else ""))
    print(f"   Direction {direction} | Target {profit_take} | Stop {stop_loss} | Throttle {check_interval_seconds}s")
    if build_qualified:
        print(f"   Qualified ON (lookback={lookback}); records NEXT bit after a qualifying slice")
        print(f"      Vol [{vol_lower_bound}x,{vol_upper_bound}x]avg  Spd [{spd_lower_bound}x,{spd_upper_bound}x]faster")
    print(f"   Processing {source_file}")
    print("=" * 65)
    start_perf_time = time.time()

    with open(source_file, 'r') as f:
        for line in f:
            line = line.strip()
            if not line or ";" not in line or line.startswith('#'):
                continue
            parts = line.split(';')
            try:
                ts_str = parts[0]; bid = float(parts[2]); ask = float(parts[3])
                vol = float(parts[4]) if len(parts) > 4 else 0.0
            except (IndexError, ValueError):
                continue
            if bid <= 0 or ask <= 0:
                continue

            date_part = ts_str[0:8]
            if date_part != cached_date_str:
                cached_date_str = date_part
                base_day_seconds = datetime.strptime(date_part, "%Y%m%d").timestamp()
            hours = int(ts_str[9:11]); minutes = int(ts_str[11:13])
            seconds = int(ts_str[13:15]); subseconds = int(ts_str[16:]) / 10000000.0
            current_time_secs = base_day_seconds + (hours*3600 + minutes*60 + seconds + subseconds)

            if in_slice:
                if build_qualified:
                    slice_volume_accum += vol
                if direction == "LONG":
                    stop_hit = (bid <= slice_stop_price); target_hit = (ask >= slice_target_price)
                else:
                    stop_hit = (ask >= slice_stop_price); target_hit = (bid <= slice_target_price)

                if stop_hit or target_hit:
                    bit = "0" if stop_hit else "1"
                    binary_chars.append(bit)
                    if build_qualified:
                        # FIRST: a prior qualifying slice means THIS bit is its outcome
                        if vol_take_next:  vol_qualified_chars.append(bit);  vol_take_next = False
                        if spd_take_next:  spd_qualified_chars.append(bit);  spd_take_next = False
                        if dual_take_next: dual_qualified_chars.append(bit); dual_take_next = False
                        # THEN: does THIS slice qualify? sets flag for the NEXT bit
                        build_time = current_time_secs - slice_start_time
                        if len(recent_volumes) == lookback:
                            avg_vol = sum(recent_volumes) / lookback
                            avg_bt = sum(recent_buildtimes) / lookback
                            vol_ok = False; spd_ok = False
                            if avg_vol > 0 and vol_lower_bound*avg_vol <= slice_volume_accum <= vol_upper_bound*avg_vol:
                                vol_ok = True
                            if avg_bt > 0:
                                min_t = avg_bt/spd_upper_bound if spd_upper_bound > 0 else 0
                                max_t = avg_bt/spd_lower_bound if spd_lower_bound > 0 else float('inf')
                                if min_t <= build_time <= max_t:
                                    spd_ok = True
                            if vol_ok: vol_take_next = True
                            if spd_ok: spd_take_next = True
                            if vol_ok and spd_ok: dual_take_next = True
                        recent_volumes.append(slice_volume_accum)
                        recent_buildtimes.append(build_time)
                    in_slice = False
                    slice_entry_price = 0.0; slice_stop_price = 0.0; slice_target_price = 0.0
                continue

            if (current_time_secs - last_check_time) < check_interval_seconds:
                continue
            last_check_time = current_time_secs
            in_slice = True
            if build_qualified:
                slice_start_time = current_time_secs; slice_volume_accum = vol
            if direction == "LONG":
                slice_entry_price = bid
                slice_stop_price = bid - stop_loss; slice_target_price = bid + profit_take
            else:
                slice_entry_price = ask
                slice_stop_price = ask + stop_loss; slice_target_price = ask - profit_take

    raw_string = "".join(binary_chars)
    with open(result_file, 'w') as out_f:
        out_f.write(raw_string)
    if build_qualified:
        vol_s = "".join(vol_qualified_chars); spd_s = "".join(spd_qualified_chars); dual_s = "".join(dual_qualified_chars)
        base, ext = os.path.splitext(result_file)
        for suff, s in [("_VOLqualified", vol_s), ("_SPEEDqualified", spd_s), ("_DUALqualified", dual_s)]:
            with open(base + suff + ext, 'w') as wf:
                wf.write(s)

    et = round(time.time() - start_perf_time, 2)
    def p1(s): return (s.count('1')/len(s)*100) if s else 0.0
    print(f"DONE in {et}s")
    print(f"   RAW : len {len(raw_string):,} | P(1)={p1(raw_string):.1f}% | {result_file}")
    print(f"   head {raw_string[:80]}")
    if build_qualified:
        print("-" * 65)
        print("   (qualified = the NEXT bit after a qualifying slice; causal)")
        print(f"   VOL  : {len(vol_s):,} ({len(vol_s)/max(len(raw_string),1)*100:.0f}%) P(1)={p1(vol_s):.1f}%")
        print(f"   SPEED: {len(spd_s):,} ({len(spd_s)/max(len(raw_string),1)*100:.0f}%) P(1)={p1(spd_s):.1f}%")
        print(f"   DUAL : {len(dual_s):,} ({len(dual_s)/max(len(raw_string),1)*100:.0f}%) P(1)={p1(dual_s):.1f}%")
        print(f"   COMPARE vs RAW P(1)={p1(raw_string):.1f}%")
    print("=" * 65)


if __name__ == "__main__":
    PARAM_DIRECTION = "LONG"; PARAM_STOP_LOSS = 40; PARAM_PROFIT_TAKE = 20; PARAM_THROTTLE = 1
    BUILD_QUALIFIED = True; LOOKBACK = 10
    VOL_LOWER_BOUND = 1.5; VOL_UPPER_BOUND = 100.0
    SPD_LOWER_BOUND = 1.5; SPD_UPPER_BOUND = 100.0
    FILE_SOURCE = r"C:\Users\chpfe\Downloads\6-22MNQ 09-26.Last2.txt"
    FILE_RESULT = r"C:\Users\chpfe\Downloads\qualifiedto23morningrawString_LONG_20stop_20profit.txt"
    generate_raw_string_from_history(FILE_SOURCE, FILE_RESULT, PARAM_DIRECTION,
        PARAM_PROFIT_TAKE, PARAM_STOP_LOSS, PARAM_THROTTLE,
        build_qualified=BUILD_QUALIFIED, lookback=LOOKBACK,
        vol_lower_bound=VOL_LOWER_BOUND, vol_upper_bound=VOL_UPPER_BOUND,
        spd_lower_bound=SPD_LOWER_BOUND, spd_upper_bound=SPD_UPPER_BOUND)
