# -*- coding: utf-8 -*-
"""
run_water_level.py
==================
Helper to run water_level_sim across many raw-string files at once,
in both overlap and decorrelated modes, and print a comparison.

Usage:
    python run_water_level.py

Edit the CONFIG block below to change filter / limit_size / stop / profit,
and the FILES list to point at your raw-string .txt files.
"""

from water_level_sim import simulate, net_legs_per_trade, load_raw_file
import os

# ---------- CONFIG : edit these ----------
FILTER      = "111"
LIMIT_SIZE  = 0
STOP        = 1
PROFIT      = 2
COST_LEGS   = 0.1          # ~2pt cost expressed in 20pt-leg units
# Folder holding your raw-string .txt files:
RAW_DIR     = "/mnt/user-data/uploads"
# Match files containing this substring (e.g. "LONG_20stop_20profit"):
FILE_MATCH  = "20stop_20profit"
# -----------------------------------------


def gather_files(folder, match):
    out = []
    if os.path.isdir(folder):
        for fn in sorted(os.listdir(folder)):
            if match in fn and fn.endswith(".txt"):
                out.append(os.path.join(folder, fn))
    return out


def run_all(files, filter, limit_size, stop, profit, cost_legs):
    print(f"filter={filter}  limit_size={limit_size}  stop:profit={stop}:{profit}")
    be = stop / (stop + profit) * 100
    print(f"breakeven win% = {be:.1f}%   cost = {cost_legs} leg/trade")
    print("=" * 78)
    header = f"{'file':<34}{'mode':<14}{'trades':>7}{'win%':>7}{'mcl':>5}{'net':>8}"
    print(header)
    print("-" * 78)

    pooled = {"overlap": [0, 0], "decorrelated": [0, 0]}
    for path in files:
        raw = load_raw_file(path)
        name = os.path.basename(path)[:32]
        for mode in ("overlap", "decorrelated"):
            r = simulate(raw, filter, limit_size, stop, profit, mode=mode)
            net = net_legs_per_trade(r["win_pct"], stop, profit, cost_legs)
            pooled[mode][0] += r["entries"]
            pooled[mode][1] += r["wins"]
            print(f"{name:<34}{mode:<14}{r['entries']:>7}{r['win_pct']:>6.0f}%"
                  f"{r['max_consec_loss']:>5}{net:>+8.3f}")
        print()

    print("=" * 78)
    print("POOLED across all files:")
    for mode in ("overlap", "decorrelated"):
        n, w = pooled[mode]
        wr = w / n * 100 if n else 0
        net = net_legs_per_trade(wr, stop, profit, cost_legs)
        print(f"  {mode:<14}: {n:>5} trades, {wr:>5.1f}% win  "
              f"(breakeven {be:.1f}%)  net {net:+.3f} legs/trade")


if __name__ == "__main__":
    files = gather_files(RAW_DIR, FILE_MATCH)
    if not files:
        print(f"No files matching '{FILE_MATCH}' in {RAW_DIR}")
    else:
        run_all(files, FILTER, LIMIT_SIZE, STOP, PROFIT, COST_LEGS)
