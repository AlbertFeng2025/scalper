# -*- coding: utf-8 -*-
"""
trade_filter.py
---------------
Distill a raw market string through three pattern filters to find
the best trading signal.

Pipeline:
    Raw String → Filter 1 → Filter 2 → Filter 3 → Trade (Live)

Usage:
    result = run(market_string="010011000001110", f1="01", f2="01", f3="01")
    print(result["report"])

Or run directly:
    python trade_filter.py
"""

def apply_filter(input_str: str, pattern: str) -> str:
    """
    Scan input_str for pattern, collect the digit immediately after
    each match → that becomes the output string for the next layer.
    """
    out = []
    plen = len(pattern)
    n = len(input_str)
    for i in range(n - plen + 1):
        if input_str[i:i + plen] == pattern:
            next_idx = i + plen
            if next_idx < n:
                out.append(input_str[next_idx])
    return "".join(out)


def stats(s: str) -> dict:
    """
    Compute stats for a binary string:
        length, win count, lose count, win%, lose%,
        max consecutive wins, max consecutive losses,
        win streaks list, loss streaks list.
    """
    n = len(s)
    if n == 0:
        return {
            "length"         : 0,
            "win_count"      : 0,
            "lose_count"     : 0,
            "win_pct"        : 0.0,
            "lose_pct"       : 0.0,
            "max_consec_win" : 0,
            "max_consec_lose": 0,
            "win_streaks"    : [],
            "lose_streaks"   : [],
        }

    wins  = s.count('1')
    loses = s.count('0')

    # Streak tracking
    win_streaks, lose_streaks = [], []
    cur_win = cur_lose = 0
    for ch in s:
        if ch == '1':
            cur_win += 1
            if cur_lose > 0:
                lose_streaks.append(cur_lose)
                cur_lose = 0
        else:
            cur_lose += 1
            if cur_win > 0:
                win_streaks.append(cur_win)
                cur_win = 0
    if cur_win  > 0: win_streaks.append(cur_win)
    if cur_lose > 0: lose_streaks.append(cur_lose)

    return {
        "length"         : n,
        "win_count"      : wins,
        "lose_count"     : loses,
        "win_pct"        : round(wins  / n * 100, 2) if n > 0 else 0.0,
        "lose_pct"       : round(loses / n * 100, 2) if n > 0 else 0.0,
        "max_consec_win" : max(win_streaks)  if win_streaks  else 0,
        "max_consec_lose": max(lose_streaks) if lose_streaks else 0,
        "win_streaks"    : win_streaks,
        "lose_streaks"   : lose_streaks,
    }


def run(market_string: str, f1: str = "01", f2: str = "01", f3: str = "01") -> dict:
    """
    Run the full pipeline:
        Raw → Filter 1 → Filter 2 → Filter 3 → Trade

    Parameters
    ----------
    market_string : str   Binary string e.g. "010011000001110"
    f1            : str   Filter 1 pattern (default "01")
    f2            : str   Filter 2 pattern (default "01")
    f3            : str   Filter 3 pattern (default "01")

    Returns
    -------
    dict with keys mapping the stats for each layer.
    """
    raw    = market_string.strip()
    f1_str = apply_filter(raw, f1)
    f2_str = apply_filter(f1_str, f2)
    f3_str = apply_filter(f2_str, f3)

    raw_st = stats(raw)
    f1_st  = stats(f1_str)
    f2_st  = stats(f2_str)
    f3_st  = stats(f3_str)

    report = _format_report(raw, f1, f2, f3, f1_str, f2_str, f3_str, raw_st, f1_st, f2_st, f3_st)

    return {
        "raw_string"    : raw,
        "f1_pattern"    : f1,
        "f2_pattern"    : f2,
        "f3_pattern"    : f3,
        "raw_stats"     : raw_st,
        "filter1_string": f1_str,
        "filter1_stats" : f1_st,
        "filter2_string": f2_str,
        "filter2_stats" : f2_st,
        "filter3_string": f3_str,
        "filter3_stats" : f3_st,
        "report"        : report,
    }


def _format_report(raw, f1, f2, f3, f1_str, f2_str, f3_str, raw_st, f1_st, f2_st, f3_st) -> str:
    W = 66
    lines = []
    lines.append("=" * W)
    lines.append("  TRADE FILTER  :  Raw → Filter 1 → Filter 2 → Filter 3 → Trade")
    lines.append("=" * W)

    # Raw
    lines.append(f"  RAW STRING")
    lines.append(f"    String          : {raw[:70]}{'...' if len(raw)>70 else ''}")
    lines.append(f"    Length          : {raw_st['length']}")
    lines.append(f"    Win  (1)        : {raw_st['win_count']}  ({raw_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {raw_st['lose_count']}  ({raw_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {raw_st['max_consec_win']}   streaks={raw_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {raw_st['max_consec_lose']}   streaks={raw_st['lose_streaks']}")
    lines.append("")

    # Filter 1
    lines.append(f"  FILTER 1  (pattern: '{f1}')")
    lines.append(f"    String          : {f1_str if f1_str else '(empty)'}")
    lines.append(f"    Length          : {f1_st['length']}")
    lines.append(f"    Win  (1)        : {f1_st['win_count']}  ({f1_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {f1_st['lose_count']}  ({f1_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {f1_st['max_consec_win']}   streaks={f1_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {f1_st['max_consec_lose']}   streaks={f1_st['lose_streaks']}")
    lines.append("")

    # Filter 2
    lines.append(f"  FILTER 2  (pattern: '{f2}')")
    lines.append(f"    String          : {f2_str if f2_str else '(empty)'}")
    lines.append(f"    Length          : {f2_st['length']}")
    lines.append(f"    Win  (1)        : {f2_st['win_count']}  ({f2_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {f2_st['lose_count']}  ({f2_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {f2_st['max_consec_win']}   streaks={f2_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {f2_st['max_consec_lose']}   streaks={f2_st['lose_streaks']}")
    lines.append("")

    # Filter 3
    lines.append(f"  FILTER 3 → TRADE (LIVE)  (pattern: '{f3}')")
    lines.append(f"    String          : {f3_str if f3_str else '(empty)'}")
    lines.append(f"    Length          : {f3_st['length']}")
    lines.append(f"    Win  (1)        : {f3_st['win_count']}  ({f3_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {f3_st['lose_count']}  ({f3_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {f3_st['max_consec_win']}   streaks={f3_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {f3_st['max_consec_lose']}   streaks={f3_st['lose_streaks']}")
    lines.append("")
    lines.append("=" * W)

    return "\n".join(lines)


# ── Run directly ──────────────────────────────────────────────────────────────
if __name__ == "__main__":

    # Example  — big string from earlier session
    print(run(
        market_string = "1111000100100001111000100110011000000010110010010100001010000000001001100000110011100111110000011110010010010110000001001100000010111000011000110000101000000110111100100010110110111100011100000000010000010010011100011010000100010110111001101001111111001000100101010000",
        f1 = "01",
        f2 = "01",
        f3 = "01"
    )["report"])
