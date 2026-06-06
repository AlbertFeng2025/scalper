# -*- coding: utf-8 -*-
"""
trade_filter.py
---------------
Distill a raw market string through two pattern filters to find
the best trading signal.

Pipeline:
    Raw String → Filter 1 → Filter 2 → Trade (Live)

Usage:
    result = run(market_string="010011000001110", f1="01", f2="01")
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
            "length"       : 0,
            "win_count"    : 0,
            "lose_count"   : 0,
            "win_pct"      : 0.0,
            "lose_pct"     : 0.0,
            "max_consec_win" : 0,
            "max_consec_lose": 0,
            "win_streaks"  : [],
            "lose_streaks" : [],
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
        "win_pct"        : round(wins  / n * 100, 2),
        "lose_pct"       : round(loses / n * 100, 2),
        "max_consec_win" : max(win_streaks)  if win_streaks  else 0,
        "max_consec_lose": max(lose_streaks) if lose_streaks else 0,
        "win_streaks"    : win_streaks,
        "lose_streaks"   : lose_streaks,
    }


def run(market_string: str, f1: str = "01", f2: str = "01") -> dict:
    """
    Run the full pipeline:
        Raw → Filter 1 → Filter 2 → Trade

    Parameters
    ----------
    market_string : str   Binary string e.g. "010011000001110"
    f1            : str   Filter 1 pattern (default "01")
    f2            : str   Filter 2 pattern (default "01")

    Returns
    -------
    dict with keys:
        raw_string      : str
        f1_pattern      : str
        f2_pattern      : str
        raw_stats       : dict
        filter1_string  : str
        filter1_stats   : dict
        filter2_string  : str  (= Live / Trade string)
        filter2_stats   : dict
        report          : str  (human-readable summary)
    """
    raw    = market_string.strip()
    f1_str = apply_filter(raw, f1)
    f2_str = apply_filter(f1_str, f2)

    raw_st = stats(raw)
    f1_st  = stats(f1_str)
    f2_st  = stats(f2_str)

    report = _format_report(raw, f1, f2, f1_str, f2_str, raw_st, f1_st, f2_st)

    return {
        "raw_string"    : raw,
        "f1_pattern"    : f1,
        "f2_pattern"    : f2,
        "raw_stats"     : raw_st,
        "filter1_string": f1_str,
        "filter1_stats" : f1_st,
        "filter2_string": f2_str,
        "filter2_stats" : f2_st,
        "report"        : report,
    }


def _format_report(raw, f1, f2, f1_str, f2_str, raw_st, f1_st, f2_st) -> str:
    W = 62
    lines = []
    lines.append("=" * W)
    lines.append("  TRADE FILTER  :  Raw → Filter 1 → Filter 2 → Trade")
    lines.append("=" * W)

    def section(label, pattern, string, st):
        lines.append(f"  {label}  (pattern: '{pattern}')")
        lines.append(f"    String          : {string if string else '(empty)'}")
        lines.append(f"    Length          : {st['length']}")
        lines.append(f"    Win  (1)        : {st['win_count']}  ({st['win_pct']}%)")
        lines.append(f"    Lose (0)        : {st['lose_count']}  ({st['lose_pct']}%)")
        lines.append(f"    Max consec Win  : {st['max_consec_win']}   streaks={st['win_streaks']}")
        lines.append(f"    Max consec Lose : {st['max_consec_lose']}   streaks={st['lose_streaks']}")

    # Raw
    lines.append(f"  RAW STRING")
    lines.append(f"    String          : {raw[:70]}{'...' if len(raw)>70 else ''}")
    lines.append(f"    Length          : {raw_st['length']}")
    lines.append(f"    Win  (1)        : {raw_st['win_count']}  ({raw_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {raw_st['lose_count']}  ({raw_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {raw_st['max_consec_win']}   streaks={raw_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {raw_st['max_consec_lose']}   streaks={raw_st['lose_streaks']}")
    lines.append("")

    lines.append(f"  FILTER 1  (pattern: '{f1}')")
    lines.append(f"    String          : {f1_str if f1_str else '(empty)'}")
    lines.append(f"    Length          : {f1_st['length']}")
    lines.append(f"    Win  (1)        : {f1_st['win_count']}  ({f1_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {f1_st['lose_count']}  ({f1_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {f1_st['max_consec_win']}   streaks={f1_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {f1_st['max_consec_lose']}   streaks={f1_st['lose_streaks']}")
    lines.append("")

    lines.append(f"  FILTER 2 → TRADE (LIVE)  (pattern: '{f2}')")
    lines.append(f"    String          : {f2_str if f2_str else '(empty)'}")
    lines.append(f"    Length          : {f2_st['length']}")
    lines.append(f"    Win  (1)        : {f2_st['win_count']}  ({f2_st['win_pct']}%)")
    lines.append(f"    Lose (0)        : {f2_st['lose_count']}  ({f2_st['lose_pct']}%)")
    lines.append(f"    Max consec Win  : {f2_st['max_consec_win']}   streaks={f2_st['win_streaks']}")
    lines.append(f"    Max consec Lose : {f2_st['max_consec_lose']}   streaks={f2_st['lose_streaks']}")
    lines.append("")
    lines.append("=" * W)

    return "\n".join(lines)


# ── Run directly ──────────────────────────────────────────────────────────────
if __name__ == "__main__":

    # Example 1 — from our discussion
    print(run(
        market_string = "010011000001110",
        f1 = "01",
        f2 = "01",
    )["report"])

    print()

    # Example 2 — longer string
    print(run(
        market_string = "011011011101110101110110",
        f1 = "01",
        f2 = "01",
    )["report"])

    print()

    # Example 3 — big string from earlier session
    print(run(
        market_string = "1111000100100001111000100110011000000010110010010100001010000000001001100000110011100111110000011110010010010110000001001100000010111000011000110000101000000110111100100010110110111100011100000000010000010010011100011010000100010110111001101001111111001000100101010000",
        f1 = "01",
        f2 = "01",
    )["report"])
