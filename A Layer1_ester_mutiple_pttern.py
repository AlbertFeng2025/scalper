# -*- coding: utf-8 -*-
"""
trade_filter_direct.py
----------------------
Single-layer pipeline:   Raw String → Filter → Trade (Live)

One filter layer is applied directly to the raw market string, and its
output IS the trade outcome — there is no second layer.

The layer accepts one OR MORE patterns, supplied as a comma-delimited
string ("10,01" or '"10","01"') or as a Python list (["10", "01"]).

Overlap: full. Every position is a candidate match, matched digits are
never consumed, so an outcome digit can also be part of a later pattern.

Usage (single string):
    r = run("010011000001110", f="10,01")
    print(r["trade"])            # the direct trade string
    print(r["report"])

Usage (CSV of binary rows, column "raw"), day-by-day list:
    python trade_filter_direct.py temptest.csv "10,01"
    python trade_filter_direct.py temptest.csv "000,110"   2 1   # reward risk
"""

import sys, csv


def parse_patterns(spec) -> list:
    """Normalise 'spec' into a clean list of pattern strings."""
    if spec is None:
        return []
    items = spec if isinstance(spec, (list, tuple)) else str(spec).split(",")
    out = []
    for item in items:
        p = str(item).strip(" \t\r\n\"'")
        if p:
            out.append(p)
    return out


def apply_filter(input_str: str, patterns) -> str:
    """
    Scan input_str left to right. At each position, test every pattern;
    for each match, collect the digit immediately after it. The
    concatenated digits are the output (= the trade string here).
    """
    patterns = parse_patterns(patterns)
    out = []
    n = len(input_str)
    for i in range(n):
        for p in patterns:
            plen = len(p)
            if plen and i + plen < n and input_str[i:i + plen] == p:
                out.append(input_str[i + plen])
    return "".join(out)


def max_zero_run(s: str) -> int:
    """Longest run of consecutive '0' (max losing streak)."""
    best = cur = 0
    for c in s:
        if c == '0':
            cur += 1
            best = max(best, cur)
        else:
            cur = 0
    return best


def stats(s: str, reward: float = 2, risk: float = 1) -> dict:
    """Trade stats for a binary string, incl. R at a reward:risk payoff."""
    n = len(s)
    w = s.count('1')
    l = n - w
    return {
        "trades"       : n,
        "wins"         : w,
        "losses"       : l,
        "win_pct"      : round(w / n * 100, 2) if n else 0.0,
        "max_loss_run" : max_zero_run(s),
        "R"            : reward * w - risk * l,   # e.g. 2:1 -> breakeven 33.3%
    }


def run(market_string: str, f="10,01", reward: float = 2, risk: float = 1) -> dict:
    """
    Single layer, direct to trade.

    Returns dict: raw, patterns, trade, stats, report
    """
    raw     = market_string.strip()
    pats    = parse_patterns(f)
    trade   = apply_filter(raw, pats)
    st      = stats(trade, reward, risk)
    report  = _report(raw, pats, trade, st, reward, risk)
    return {"raw": raw, "patterns": pats, "trade": trade, "stats": st, "report": report}


def _report(raw, pats, trade, st, reward, risk) -> str:
    W = 60
    L = []
    L.append("=" * W)
    L.append("  DIRECT TRADE FILTER  :  Raw → Filter → Trade")
    L.append("=" * W)
    L.append(f"  Patterns        : {', '.join(repr(p) for p in pats) if pats else '(none)'}")
    L.append(f"  Payoff          : {reward:g} : {risk:g}   (breakeven "
             f"{risk/(reward+risk)*100:.2f}% win)")
    L.append(f"  Raw length      : {len(raw)}")
    L.append("-" * W)
    L.append(f"  TRADE string    : {trade if trade else '(empty)'}")
    L.append(f"  Trades          : {st['trades']}")
    L.append(f"  Win  (1)        : {st['wins']}  ({st['win_pct']}%)")
    L.append(f"  Lose (0)        : {st['losses']}")
    L.append(f"  Max loss run    : {st['max_loss_run']}")
    L.append(f"  R ({reward:g}:{risk:g})        : {st['R']:+g}")
    L.append("=" * W)
    return "\n".join(L)


def batch_csv(path, f="10,01", reward: float = 2, risk: float = 1, column="raw"):
    """List day-by-day (one CSV row = one day) for a single-layer filter."""
    rows = []
    with open(path, newline="") as fh:
        for r in csv.DictReader(fh):
            raw = (r.get(column) or "").strip()
            if raw:
                rows.append(raw)

    pats = parse_patterns(f)
    print(f"Filter: {', '.join(repr(p) for p in pats)}   "
          f"payoff {reward:g}:{risk:g} (breakeven {risk/(reward+risk)*100:.2f}%)   "
          f"({len(rows)} days)\n")
    print(f"{'Day':>3} | {'Trade':<14} | {'Trd':>3} {'W':>2} {'L':>2} "
          f"{'Win%':>5} {'MaxLossRun':>10} {'R':>6}")
    print("-" * 62)

    tT = tW = tL = 0
    worst = 0
    R = 0
    for i, raw in enumerate(rows, 1):
        trade = apply_filter(raw, pats)
        st = stats(trade, reward, risk)
        tT += st['trades']; tW += st['wins']; tL += st['losses']
        worst = max(worst, st['max_loss_run']); R += st['R']
        print(f"{i:>3} | {(trade if trade else '(none)'):<14} | "
              f"{st['trades']:>3} {st['wins']:>2} {st['losses']:>2} "
              f"{st['win_pct']:>5.0f} {st['max_loss_run']:>10} {st['R']:>+6g}")
    print("-" * 62)
    twr = tW / tT * 100 if tT else 0
    print(f"{'TOT':>3} | {'':<14} | {tT:>3} {tW:>2} {tL:>2} "
          f"{twr:>5.0f} {worst:>10} {R:>+6g}")


if __name__ == "__main__":
    if len(sys.argv) >= 3:                      # CSV batch mode
        path = sys.argv[1]
        pat  = sys.argv[2]
        rw   = float(sys.argv[3]) if len(sys.argv) > 3 else 2
        rk   = float(sys.argv[4]) if len(sys.argv) > 4 else 1
        batch_csv(path, pat, rw, rk)
    else:                                        # quick demo
        print(run("010011000001110", f="10,01")["report"])
