# -*- coding: utf-8 -*-
"""
pipeline_optimizer_v4.py
------------------------
Brute-force optimizer to find the best F1, F2, and F3 patterns.
NOW: patterns are generated UP TO the specified length (not fixed length).
Example: MAX_F1=2 tests patterns of length 1 AND 2.
         MAX_F1=3 tests patterns of length 1, 2, AND 3.
"""
import itertools

def simulate_pipeline(market_string: str, f1_pat: str, f2_pat: str, f3_pat: str) -> dict:
    raw_string = ""
    f1_outcome = ""
    f2_outcome = ""
    is_armed   = False
    wait_f1    = False
    wait_f2    = False
    next_is_money = False
    trades = []

    for bit in market_string:
        if next_is_money:
            trades.append(int(bit))

        raw_string += bit

        if wait_f1:
            wait_f1    = False
            consume_f2 = wait_f2
            f1_outcome += bit
            if consume_f2:
                wait_f2    = False
                f2_outcome += bit
                is_armed   = f2_outcome.endswith(f3_pat)
            if f1_outcome.endswith(f2_pat):
                wait_f2 = True

        if raw_string.endswith(f1_pat):
            wait_f1 = True

        next_is_money = is_armed and wait_f2 and wait_f1

    def calc_pct(s):
        if not s: return 0.0
        if isinstance(s, list):
            return (sum(s) / len(s)) * 100
        return (s.count('1') / len(s)) * 100

    def calc_max_loss_streak(sequence):
        if not sequence: return 0
        seq_str = "".join(map(str, sequence)) if isinstance(sequence, list) else sequence
        streaks = seq_str.split('1')
        return max(len(streak) for streak in streaks)

    return {
        "raw_len"        : len(raw_string),
        "raw_pct"        : calc_pct(raw_string),
        "raw_max_losses" : calc_max_loss_streak(raw_string),
        "f1_len"         : len(f1_outcome),
        "f1_pct"         : calc_pct(f1_outcome),
        "f1_max_losses"  : calc_max_loss_streak(f1_outcome),
        "f2_len"         : len(f2_outcome),
        "f2_pct"         : calc_pct(f2_outcome),
        "f2_max_losses"  : calc_max_loss_streak(f2_outcome),
        "trade_len"      : len(trades),
        "trade_wins"     : sum(trades),
        "trade_pct"      : (sum(trades) / len(trades) * 100) if trades else 0.0,
        "trade_max_losses": calc_max_loss_streak(trades)
    }

def generate_patterns_upto(max_length: int) -> list:
    """
    Generates all binary patterns of length 1 UP TO max_length.
    e.g. max_length=2 → ["0","1","00","01","10","11"]
         max_length=3 → adds "000","001","010","011","100","101","110","111"
    """
    patterns = []
    for length in range(1, max_length + 1):
        for p in itertools.product('01', repeat=length):
            patterns.append(''.join(p))
    return patterns

def run_optimization(market_string: str,
                     max_f1: int, max_f2: int, max_f3: int,
                     min_trades: int = 5,
                     top_n: int = 10,
                     sort_by: str = "win_rate"):
    """
    Tests all combinations of F1, F2, F3 patterns up to their specified max length.

    sort_by options:
        "win_rate"  — sort by final trade win rate (default)
        "max_loss"  — sort by lowest max consecutive loss streak
        "trades"    — sort by most trades
    """
    f1_patterns = generate_patterns_upto(max_f1)
    f2_patterns = generate_patterns_upto(max_f2)
    f3_patterns = generate_patterns_upto(max_f3)

    total_combos = len(f1_patterns) * len(f2_patterns) * len(f3_patterns)

    print("=" * 95)
    print(f"  pipeline_optimizer_v4  —  UP TO length mode")
    print(f"  F1 max={max_f1} ({len(f1_patterns)} patterns) | "
          f"F2 max={max_f2} ({len(f2_patterns)} patterns) | "
          f"F3 max={max_f3} ({len(f3_patterns)} patterns)")
    print(f"  Total combos: {total_combos:,}  |  Market string length: {len(market_string)}")
    print(f"  Min trades: {min_trades}  |  Sort by: {sort_by}  |  Show top: {top_n}")
    print("=" * 95)

    results = []
    for f1 in f1_patterns:
        for f2 in f2_patterns:
            for f3 in f3_patterns:
                stats = simulate_pipeline(market_string, f1, f2, f3)
                if stats["trade_len"] >= min_trades:
                    results.append({
                        "f1": f1, "f2": f2, "f3": f3,
                        "stats": stats
                    })

    if not results:
        print(f"No combinations produced at least {min_trades} trades.")
        return

    # Sort options
    if sort_by == "win_rate":
        results.sort(key=lambda x: (x["stats"]["trade_pct"],
                                     x["stats"]["trade_len"]), reverse=True)
    elif sort_by == "max_loss":
        results.sort(key=lambda x: (x["stats"]["trade_max_losses"],
                                    -x["stats"]["trade_len"]))
    elif sort_by == "trades":
        results.sort(key=lambda x: x["stats"]["trade_len"], reverse=True)

    print(f"\n  {len(results):,} combos passed min_trades={min_trades} filter.\n")

    for i, res in enumerate(results[:top_n]):
        st  = res["stats"]
        tag = f"F1:'{res['f1']}' F2:'{res['f2']}' F3:'{res['f3']}'"
        print(f"{i+1:2}. {tag}")
        print(f"    Raw String : {st['raw_len']:5} slices | Win%: {st['raw_pct']:6.2f}% | MaxLoss: {st['raw_max_losses']}")
        print(f"    F1 Outcome : {st['f1_len']:5} slices | Win%: {st['f1_pct']:6.2f}% | MaxLoss: {st['f1_max_losses']}")
        print(f"    F2 Outcome : {st['f2_len']:5} slices | Win%: {st['f2_pct']:6.2f}% | MaxLoss: {st['f2_max_losses']}")
        print(f"    Live Trades: {st['trade_len']:5} trades | Win%: {st['trade_pct']:6.2f}% | MaxLoss: {st['trade_max_losses']} "
              f"({st['trade_wins']}W - {st['trade_len'] - st['trade_wins']}L)")
        print("-" * 95)

    # Summary table
    print(f"\n  QUICK SUMMARY TABLE (top {min(top_n, len(results))})")
    print(f"  {'#':>2}  {'F1':>5} {'F2':>5} {'F3':>5} | {'Trades':>7} {'Win%':>7} {'MaxLoss':>8}")
    print(f"  {'-'*60}")
    for i, res in enumerate(results[:top_n]):
        st = res["stats"]
        print(f"  {i+1:2}  {res['f1']:>5} {res['f2']:>5} {res['f3']:>5} | "
              f"{st['trade_len']:>7} {st['trade_pct']:>7.2f}% {st['trade_max_losses']:>8}")


# ── Run directly ──────────────────────────────────────────────────────────────
if __name__ == "__main__":

    test_market_string = "010100100011000011111110011110001110100000001110010011100010100101011011011111101100010110000011111101101110100001000110001011111001010110111010010101010100101110"

    # Max length per filter — UP TO this length will be tested
    # e.g. MAX_F1=2 tests: "0","1","00","01","10","11" (6 patterns)
    #      MAX_F1=3 tests: above + "000".."111"        (14 patterns)
    MAX_F1 = 2
    MAX_F2 = 3
    MAX_F3 = 2

    # Minimum trades for statistical significance
    MIN_TRADES = 3

    # Sort options: "win_rate" | "max_loss" | "trades"
    SORT_BY = "win_rate"

    # How many top results to display
    TOP_N = 10

    run_optimization(test_market_string, MAX_F1, MAX_F2, MAX_F3,
                     min_trades=MIN_TRADES, top_n=TOP_N, sort_by=SORT_BY)
