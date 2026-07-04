# -*- coding: utf-8 -*-
"""
build_historical_recorder_limit.py
----------------------------
Parses high-resolution historical tick data files and precisely replicates 
the logic of the NinjaScript 'LONG_SHORT_rawString_recorder' strategy.
Updated to use Limit Orders with Timeouts instead of Market Orders.
"""

import os
import time
from datetime import datetime

def generate_raw_string_from_history(
        source_file: str, 
        result_file: str, 
        direction: str, 
        profit_take: float, 
        stop_loss: float,
        check_interval_seconds: int,
        limit_offset_points: float = 5.0,
        limit_timeout_seconds: float = 5.0
):
    direction = direction.upper()
    if direction not in ["LONG", "SHORT"]:
        raise ValueError("Error: direction parameter must be strictly 'LONG' or 'SHORT'.")

    binary_chars = []
    
    # State Machine Variables
    state = "IDLE"  # Can be "IDLE", "PENDING", or "ACTIVE"
    slice_start_time = 0.0
    slice_limit_price = 0.0
    slice_entry_price = 0.0
    slice_stop_price = 0.0
    slice_target_price = 0.0
    last_check_time = -999999.0  
    
    # High-speed date tracking cache variables
    cached_date_str = ""
    base_day_seconds = 0.0

    print("=" * 70)
    print(f"🎬 MATCHING NINJASCRIPT RECORDER LOGIC (LIMIT ORDERS)")
    print(f"   Direction      : {direction}")
    print(f"   Limit Offset   : {limit_offset_points} points")
    print(f"   Limit Timeout  : {limit_timeout_seconds} second(s)")
    print(f"   Profit Target  : {profit_take} points")
    print(f"   Stop Loss      : {stop_loss} points")
    print(f"   Time Throttle  : {check_interval_seconds} second(s)")
    print(f"   Processing     : {source_file}")
    print("=" * 70)

    start_perf_time = time.time()

    with open(source_file, 'r') as f:
        for line in f:
            line = line.strip()
            if not line or ";" not in line or line.startswith('#'):
                continue
                
            parts = line.split(';')
            try:
                ts_str = parts[0]
                bid = float(parts[2])
                ask = float(parts[3])
            except (IndexError, ValueError):
                continue
                
            if bid <= 0 or ask <= 0:
                continue

            # -----------------------------------------------------------------
            # ULTRA-FAST HISTORICAL TIMESTAMP PARSING
            # -----------------------------------------------------------------
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

            # -----------------------------------------------------------------
            # STATE 3: ACTIVE SLICE (Looking for Target or Stop)
            # -----------------------------------------------------------------
            if state == "ACTIVE":
                stop_hit = False
                target_hit = False
                
                if direction == "LONG":
                    stop_hit = (bid <= slice_stop_price)
                    target_hit = (ask >= slice_target_price)
                else: # SHORT
                    stop_hit = (ask >= slice_stop_price)
                    target_hit = (bid <= slice_target_price)
                    
                # Whichever tick triggers a hit first takes over.
                if stop_hit or target_hit:
                    # If a massive tick hits BOTH simultaneously, Stop takes precedence (0)
                    bit = "0" if stop_hit else "1"
                    binary_chars.append(bit)
                    
                    # Reset state
                    state = "IDLE"
                    last_check_time = current_time_secs
                    continue
                    
                continue # Stay in active state if neither hit

            # -----------------------------------------------------------------
            # STATE 2: PENDING SLICE (Waiting for Limit Order to Fill)
            # -----------------------------------------------------------------
            elif state == "PENDING":
                # Check if we have waited too long (Timeout)
                if (current_time_secs - slice_start_time) > limit_timeout_seconds:
                    state = "IDLE" # Void the slice
                    last_check_time = current_time_secs # Reset throttle
                    continue
                
                # Check if the market reached our limit price
                is_filled = False
                if direction == "LONG" and ask <= slice_limit_price:
                    is_filled = True
                elif direction == "SHORT" and bid >= slice_limit_price:
                    is_filled = True
                    
                if is_filled:
                    state = "ACTIVE"
                    slice_entry_price = slice_limit_price
                    
                    if direction == "LONG":
                        slice_stop_price = slice_entry_price - stop_loss
                        slice_target_price = slice_entry_price + profit_take
                    else: # SHORT
                        slice_stop_price = slice_entry_price + stop_loss
                        slice_target_price = slice_entry_price - profit_take
                        
                continue # Stay in pending state if not filled and not timed out

            # -----------------------------------------------------------------
            # STATE 1: IDLE (Looking to place a new Limit Order)
            # -----------------------------------------------------------------
            elif state == "IDLE":
                if (current_time_secs - last_check_time) >= check_interval_seconds:
                    state = "PENDING"
                    slice_start_time = current_time_secs
                    
                    if direction == "LONG":
                        # Buy limit is placed below the current bid
                        slice_limit_price = bid - limit_offset_points
                    else: # SHORT
                        # Sell limit is placed above the current ask
                        slice_limit_price = ask + limit_offset_points

    # Compile and output results
    final_string = "".join(binary_chars)
    with open(result_file, 'w') as out_f:
        out_f.write(final_string)
        
    execution_time = round(time.time() - start_perf_time, 2)

    print(f"✨ PROCESS COMPLETED IN {execution_time} SECONDS")
    print(f"   Saved Output : {result_file}")
    print(f"   String Length: {len(final_string):,} bits generated")
    print(f"   String Head  : {final_string[:100]}")
    print("=" * 70)


# ── PARAMETER SELECTION BLOCK ───────────────────────────────────────────────
if __name__ == "__main__":

    # Configure your historical test parameters here:
    PARAM_DIRECTION   = "SHORT"    # Use "LONG" or "SHORT"
    PARAM_STOP_LOSS   = 40.0       # Stop Loss in index points
    PARAM_PROFIT_TAKE = 20.0       # Profit Target in index points
    PARAM_THROTTLE    = 1          # Minimum seconds before looking for a new setup
    
    # New Limit Order Parameters
    PARAM_LIMIT_OFFSET  = 5.0      # How many points away to place the limit order (5 pts = 20 ticks)
    PARAM_LIMIT_TIMEOUT = 5.0      # How many seconds to wait for the limit fill before voiding

    FILE_SOURCE       = r"C:\Users\chpfe\Downloads\4-20MNQ 06-26.Last.txt"
    FILE_RESULT       = r"C:\Users\chpfe\Downloads\4-20MNQ 06-26SHORT40_20_Limit.txt"

    # Run Generation
    generate_raw_string_from_history(
        source_file            = FILE_SOURCE,
        result_file            = FILE_RESULT,
        direction              = PARAM_DIRECTION,
        profit_take            = PARAM_PROFIT_TAKE,
        stop_loss              = PARAM_STOP_LOSS,
        check_interval_seconds = PARAM_THROTTLE,
        limit_offset_points    = PARAM_LIMIT_OFFSET,
        limit_timeout_seconds  = PARAM_LIMIT_TIMEOUT
    )
