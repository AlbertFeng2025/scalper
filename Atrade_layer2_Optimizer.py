# -*- coding: utf-8 -*-
"""
layer2_optimizer.py
-------------------
Brute-force optimizer for the lookforward pattern distillation pipeline.
Finds the best F1 and F2 pattern combinations based on win rate and streak reduction.

Pipeline:
    Raw String → Filter 1 → Filter 2 (Live Trades)
"""
import itertools


def apply_filter(input_str: str, pattern: str) -> str:
    """
    Scan input_str for pattern, collect the digit immediately after
    each match → that becomes the output string for the next layer.
    """
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
    """
    Compute comprehensive stats for a binary string, including metrics
    for consecutive win/loss analysis.
    """
    n = len(s)
    if n == 0:
        return {
            "length": 0,
            "win_count": 0,
            "lose_count": 0,
            "win_pct": 0.0,
            "lose_pct": 0.0,
            "max_consec_win": 0,
            "max_consec_lose": 0,
            "win_streaks": [],
            "lose_streaks": [],
        }

    wins = s.count("1")
    loses = s.count("0")

    win_streaks, lose_streaks = [], []
    cur_win = cur_lose = 0
    for ch in s:
        if ch == "1":
            cur_win += 1
            if cur_lose > 0:
                lose_streaks.append(cur_lose)
                cur_lose = 0
        else:
            cur_lose += 1
            if cur_win > 0:
                win_streaks.append(cur_win)
                cur_win = 0
    if cur_win > 0:
        win_streaks.append(cur_win)
    if cur_lose > 0:
        lose_streaks.append(cur_lose)

    return {
        "length": n,
        "win_count": wins,
        "lose_count": loses,
        "win_pct": round(wins / n * 100, 2),
        "lose_pct": round(loses / n * 100, 2),
        "max_consec_win": max(win_streaks) if win_streaks else 0,
        "max_consec_lose": max(lose_streaks) if lose_streaks else 0,
        "win_streaks": win_streaks,
        "lose_streaks": lose_streaks,
    }


def generate_patterns(length: int) -> list:
    """Generates all possible binary combinations of a given length."""
    return ["".join(p) for p in itertools.product("01", repeat=length)]


def run_optimization(
    market_string: str, f1_len: int, f2_len: int, min_trades: int = 5
):
    """
    Tests all combinations of F1 and F2 pattern structures.
    Ranks combinations primarily by Live Trade Win Rate, and secondarily by trade volume.
    """
    raw = market_string.strip()
    raw_st = stats(raw)

    print(f"Generating search space (F1 Length: {f1_len} | F2 Length: {f2_len})...")
    f1_patterns = generate_patterns(f1_len)
    f2_patterns = generate_patterns(f2_len)

    results = []
    total_combos = len(f1_patterns) * len(f2_patterns)
    print(
        f"Testing {total_combos} combinations against {len(raw)} market slices...\n"
    )

    for f1 in f1_patterns:
        for f2 in f2_patterns:
            # Execute lookforward pipeline distillation
            f1_str = apply_filter(raw, f1)
            f2_str = apply_filter(f1_str, f2)

            f1_st = stats(f1_str)
            f2_st = stats(f2_str)

            # Enforce statistical significance gate
            if f2_st["length"] >= min_trades:
                results.append(
                    {
                        "f1_pattern": f1,
                        "f2_pattern": f2,
                        "f1_stats": f1_st,
                        "trade_stats": f2_st,
                    }
                )

    # Sort: High Win Rate -> High Trade Volume -> Low Max Loss Streak
    results.sort(
        key=lambda x: (
            x["trade_stats"]["win_pct"],
            x["trade_stats"]["length"],
            -x["trade_stats"]["max_consec_lose"],
        ),
        reverse=True,
    )

    # Print out results panel
    W = 95
    print("=" * W)
    print(f"{'TOP PIPELINE CONFIGURATIONS (Lookforward Distillation)':^95}")
    print("=" * W)
    print(
        f"Baseline Market String Status: {raw_st['length']} slices | Win Rate: {raw_st['win_pct']}% | Max Drawdown: {raw_st['max_consec_lose']} in a row"
    )
    print("-" * W)

    if not results:
        print(
            f"No structural matches managed to clear the minimum threshold of {min_trades} trades."
        )
        return

    for i, res in enumerate(results[:10]):
        f1_st = res["f1_stats"]
        t_st = res["trade_stats"]

        print(f"{i+1:2}. Pattern Combo -> [ F1: '{res['f1_pattern']}' ] → [ F2: '{res['f2_pattern']}' ]")
        print(
            f"    Layer 1 (F1 Output): {f1_st['length']:4} slices | Win Rate: {f1_st['win_pct']:6.2f}% | Max Loss Streak: {f1_st['max_consec_lose']} in a row"
        )
        print(
            f"    LIVE TRADES (Trade): {t_st['length']:4} trades | Win Rate: {t_st['win_pct']:6.2f}% | Max Loss Streak: {t_st['max_consec_lose']} IN A ROW ({t_st['win_count']}W - {t_st['lose_count']}L)"
        )
        print("-" * W)


if __name__ == "__main__":
    # Historical sequence sample for mapping execution verification
    historical_data = "011001010000011101110010000000000010000100100100000011011010101110000101110010010000100000101000110010010001000100000010100101100011000001010010011000110000001"

    # Configure length setups to test
    LENGTH_F1 = 2
    LENGTH_F2 = 2
    MIN_TRADES = 2

    run_optimization(historical_data, LENGTH_F1, LENGTH_F2, min_trades=MIN_TRADES)
