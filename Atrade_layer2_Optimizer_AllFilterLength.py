# -*- coding: utf-8 -*-
"""
layer2_optimizer_v2.py
----------------------
Brute-force optimizer for the lookforward pattern distillation pipeline.
Finds the best F1 and F2 pattern combinations based on win rate and streak reduction.

Pipeline:
    Raw String → Filter 1 → Filter 2 (Live Trades)

CHANGE from v1: patterns are now generated UP TO the specified length,
not fixed length only.
Example: MAX_F1=2 tests lengths 1 AND 2 → "0","1","00","01","10","11"
         MAX_F1=3 tests lengths 1, 2, AND 3 → 14 patterns total
"""
import itertools


def apply_filter(input_str: str, pattern: str) -> str:
    out = []
    plen = len(pattern)
    n = len(input_str)
    for i in range(n - plen + 1):
        if input_str[i : i + plen] == pattern:
            next_idx = i + plen
            if next_idx < n:
                out.append(input_str[next_idx])
    return "".join(out)


def stats(s: str) -> dict:
    n = len(s)
    if n == 0:
        return {
            "length": 0, "win_count": 0, "lose_count": 0,
            "win_pct": 0.0, "lose_pct": 0.0,
            "max_consec_win": 0, "max_consec_lose": 0,
            "win_streaks": [], "lose_streaks": [],
        }
    wins = s.count("1")
    loses = s.count("0")
    win_streaks, lose_streaks = [], []
    cur_win = cur_lose = 0
    for ch in s:
        if ch == "1":
            cur_win += 1
            if cur_lose > 0: lose_streaks.append(cur_lose); cur_lose = 0
        else:
            cur_lose += 1
            if cur_win > 0: win_streaks.append(cur_win); cur_win = 0
    if cur_win  > 0: win_streaks.append(cur_win)
    if cur_lose > 0: lose_streaks.append(cur_lose)
    return {
        "length": n, "win_count": wins, "lose_count": loses,
        "win_pct": round(wins / n * 100, 2),
        "lose_pct": round(loses / n * 100, 2),
        "max_consec_win":  max(win_streaks)  if win_streaks  else 0,
        "max_consec_lose": max(lose_streaks) if lose_streaks else 0,
        "win_streaks": win_streaks, "lose_streaks": lose_streaks,
    }


def generate_patterns_upto(max_length: int) -> list:
    """
    Generates all binary patterns of length 1 UP TO max_length.
    max_length=1 →  2 patterns: "0","1"
    max_length=2 →  6 patterns: "0","1","00","01","10","11"
    max_length=3 → 14 patterns: above + "000".."111"
    max_length=4 → 30 patterns
    """
    patterns = []
    for length in range(1, max_length + 1):
        for p in itertools.product("01", repeat=length):
            patterns.append("".join(p))
    return patterns


def run_optimization(market_string: str,
                     max_f1: int, max_f2: int,
                     min_trades: int = 5,
                     top_n: int = 10,
                     sort_by: str = "win_rate"):
    """
    Tests all combinations of F1 and F2 patterns up to their specified max length.

    sort_by options:
        "win_rate"  — highest final trade win rate (default)
        "max_loss"  — lowest max consecutive loss streak
        "trades"    — most trades
    """
    raw = market_string.strip()
    raw_st = stats(raw)

    f1_patterns = generate_patterns_upto(max_f1)
    f2_patterns = generate_patterns_upto(max_f2)
    total_combos = len(f1_patterns) * len(f2_patterns)

    W = 95
    print("=" * W)
    print(f"  layer2_optimizer_v2  —  UP TO length mode")
    print(f"  F1 max={max_f1} ({len(f1_patterns)} patterns) | "
          f"F2 max={max_f2} ({len(f2_patterns)} patterns)")
    print(f"  Total combos: {total_combos:,}  |  Market string: {len(raw)} slices  |  "
          f"Min trades: {min_trades}  |  Sort by: {sort_by}")
    print(f"  Raw baseline: Win%={raw_st['win_pct']}%  "
          f"MaxLoss={raw_st['max_consec_lose']}  "
          f"MaxWin={raw_st['max_consec_win']}")
    print("=" * W)

    results = []
    for f1 in f1_patterns:
        for f2 in f2_patterns:
            f1_str = apply_filter(raw, f1)
            f2_str = apply_filter(f1_str, f2)
            f1_st  = stats(f1_str)
            f2_st  = stats(f2_str)

            if f2_st["length"] >= min_trades:
                results.append({
                    "f1": f1, "f2": f2,
                    "f1_st": f1_st,
                    "f2_st": f2_st,
                })

    if not results:
        print(f"  No combinations produced at least {min_trades} trades.")
        return

    # Sort options
    if sort_by == "win_rate":
        results.sort(key=lambda x: (
            x["f2_st"]["win_pct"],
            x["f2_st"]["length"],
            -x["f2_st"]["max_consec_lose"]), reverse=True)
    elif sort_by == "max_loss":
        results.sort(key=lambda x: (
            x["f2_st"]["max_consec_lose"],
            -x["f2_st"]["length"]))
    elif sort_by == "trades":
        results.sort(key=lambda x: x["f2_st"]["length"], reverse=True)

    print(f"\n  {len(results):,} combos passed min_trades={min_trades} filter.\n")

    for i, res in enumerate(results[:top_n]):
        f1_st = res["f1_st"]
        f2_st = res["f2_st"]
        print(f"{i+1:2}. F1:'{res['f1']}'  →  F2:'{res['f2']}'")
        print(f"    F1 Output  : {f1_st['length']:5} slices | "
              f"Win%: {f1_st['win_pct']:6.2f}% | "
              f"MaxLoss: {f1_st['max_consec_lose']}  "
              f"MaxWin: {f1_st['max_consec_win']}")
        print(f"    Live Trades: {f2_st['length']:5} trades | "
              f"Win%: {f2_st['win_pct']:6.2f}% | "
              f"MaxLoss: {f2_st['max_consec_lose']}  "
              f"({f2_st['win_count']}W - {f2_st['lose_count']}L)")
        print("-" * W)

    # Quick summary table
    print(f"\n  QUICK SUMMARY TABLE (top {min(top_n, len(results))})")
    print(f"  {'#':>2}  {'F1':>6} {'F2':>6} | {'Trades':>7} {'Win%':>7} {'MaxLoss':>8} {'MaxWin':>7}")
    print(f"  {'-'*55}")
    for i, res in enumerate(results[:top_n]):
        st = res["f2_st"]
        print(f"  {i+1:2}  {res['f1']:>6} {res['f2']:>6} | "
              f"{st['length']:>7} {st['win_pct']:>7.2f}% "
              f"{st['max_consec_lose']:>8} {st['max_consec_win']:>7}")


# ── Run directly ──────────────────────────────────────────────────────────────
if __name__ == "__main__":

    historical_data = "011001010000011101110010000000000010000100100100000011011010101110000101110010010000100000101000110010010001000100000010100101100011000001010010011000110000001"

    # Max length per filter — UP TO this length will be tested
    MAX_F1 = 2   # tests lengths 1 and 2  →  6 patterns
    MAX_F2 = 3   # tests lengths 1, 2, 3  → 14 patterns

    MIN_TRADES = 2

    # Sort options: "win_rate" | "max_loss" | "trades"
    SORT_BY = "win_rate"

    TOP_N = 10

    run_optimization(historical_data, MAX_F1, MAX_F2,
                     min_trades=MIN_TRADES, top_n=TOP_N, sort_by=SORT_BY)
