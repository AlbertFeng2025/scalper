# A_layer2_wildcard_tester
#
# PURE MATH. String of 0/1 only.
#
# Standard Layer-2 filter pipeline (F1 then F2 -> chase target). A filter
# pattern may contain TWO kinds of flexible wildcard (mix them freely):
#   '*'  = one-or-more 0s   (a gap / run of zeros)
#   '?'  = one-or-more 1s   (a run of ones)
#
#   "1*11" -> 1, one-or-more 0s, 1, 1      (tail: ...10011, ...100011, ...1011)
#   "0?0"  -> 0, one-or-more 1s, 0         (tail: ...010, ...0110, ...01110)
#   "011"  -> plain fixed pattern (no wildcard)
#
# PIPELINE (same as the NinjaTrader Layer-2 strategy):
#   for each bit:
#     1. append bit to rawString
#     2. if waiting: append bit to filter1Outcome; isArmed = (f1 tail matches F2)
#     3. if rawString tail matches F1 -> start waiting (next bit feeds f1)
#     4. if isArmed AND rawString tail matches F1 -> next bit is a MONEY trade
#   The money-trade bit is recorded as the outcome. target=1 = we chase 1.
#
# DEFAULT COMBO: F1="011", F2="1*11"  (the universal combo found in analysis:
#   lifts P(1) above baseline on BOTH books, out-of-sample, with short 0-runs).
#
# OUTPUT: baseline P(1), fire count, fired P(1) (the answer), max 0-in-a-row
#         (losing streak), max 1-in-a-row (winning streak), and the trade string.

import re


def make_matcher(pattern, wild_bit='0'):
    """Return f(s) -> bool: does s END WITH pattern (tail-anchored)?

    TWO wildcards are supported and may be mixed freely in one pattern:
       '*'  = one-or-more 0s   (a gap / run of zeros)
       '?'  = one-or-more 1s   (a run of ones)
    Everything else ('0' or '1') is a single fixed bit.

    Examples (all tail-anchored — must match at the END of the string):
       "1*11" -> 1, some 0s, 1, 1          (10011, 100011, 1011, ...)
       "0?0"  -> 0, some 1s, 0             (010, 0110, 01110, ...)   island of 1s
       "1*1?1"-> 1, some 0s, 1, some 1s, 1 (mixed both wildcards)
    Read left-to-right as you type it; the matcher checks the last char (anchor)
    first and only looks further back if the anchor matches (so it's cheap).
    """
    if '*' not in pattern and '?' not in pattern:
        return lambda s: s.endswith(pattern)
    rx = ""
    for ch in pattern:
        if ch == '*':
            rx += wild_bit + "+"      # one-or-more 0s (default wild_bit)
        elif ch == '?':
            rx += "1+"                # one-or-more 1s
        else:
            rx += ch
    cre = re.compile(rx + "$")
    return lambda s: cre.search(s) is not None


def A_layer2_wildcard_tester(
    string,
    target=1,
    F1="011",
    F2="1*11",
    wild_bit='0',
):
    target_bit = str(target)
    m1 = make_matcher(F1, wild_bit)
    m2 = make_matcher(F2, wild_bit)

    rawString = ""
    f1 = ""
    isArmed = False
    waitF1 = False
    pending = False
    trades = []          # money-trade outcome bits

    for b in string:
        if pending:
            trades.append(b)
            pending = False
        rawString += b
        if waitF1:
            waitF1 = False
            f1 += b          # layer-1 outcome string grows here
            isArmed = m2(f1)
        if m1(rawString):
            waitF1 = True
        if isArmed and m1(rawString):
            pending = True

    n = len(string)

    # ---- per-layer percentages (step-by-step filtration) ----
    raw_hits = string.count(target_bit)
    raw_pct = (raw_hits / n * 100) if n else 0.0
    f1_hits = f1.count(target_bit)
    f1_pct = (f1_hits / len(f1) * 100) if f1 else 0.0

    fire_count = len(trades)
    hits = sum(1 for t in trades if t == target_bit)
    fired_pct = (hits / fire_count * 100) if fire_count else 0.0
    base_pct = raw_pct

    def max_run(seq, ch):
        m = c = 0
        for x in seq:
            if x == ch:
                c += 1
                m = max(m, c)
            else:
                c = 0
        return m

    non_target = '0' if target_bit == '1' else '1'
    trades_str = "".join(trades)
    max_nontarget = max_run(trades_str, non_target)
    max_target = max_run(trades_str, target_bit)
    fire_rate = (fire_count / n * 100) if n else 0.0
    bits_per_fire = (n / fire_count) if fire_count else 0.0

    return {
        "string_length": n,
        "baseline_pct": round(base_pct, 1),
        "raw_pct": round(raw_pct, 1),
        "raw_count": f"{raw_hits}/{n}",
        "layer1_pct": round(f1_pct, 1),
        "layer1_count": f"{f1_hits}/{len(f1)}",
        "layer1_string": f1,
        "fire_count": fire_count,
        "fire_rate_pct": round(fire_rate, 1),
        "bits_per_fire": round(bits_per_fire, 1),
        "fired_pct": round(fired_pct, 1),
        "fired_count": f"{hits}/{fire_count}",
        "max_nontarget_in_a_row": max_nontarget,
        "max_target_in_a_row": max_target,
        "trades_string": trades_str,
    }


def print_report(result, F1="011", F2="1*11", wild_bit='0', book=None):
    print("=" * 60)
    print("A_layer2_wildcard_tester")
    print("=" * 60)
    if book:
        print(f"  (book = {book})")
    print(f"  F1 = {F1}   F2 = {F2}   (* = one-or-more '{wild_bit}')")
    print("-" * 60)
    print(f"  string length            : {result['string_length']}")
    print(f"  STEP-BY-STEP FILTRATION (P(1) should climb each layer):")
    print(f"    RAW            : {result['raw_pct']}%  ({result['raw_count']})")
    print(f"    LAYER1 outcome : {result['layer1_pct']}%  ({result['layer1_count']})   "
          f"after F1={F1}")
    print(f"    LAYER2 (trade) : {result['fired_pct']}%  ({result['fired_count']})   "
          f"after F2={F2}   <- THE ANSWER")
    print(f"  fire rate                : {result['fire_rate_pct']}% of bits  "
          f"(~1 per {result['bits_per_fire']} bits)")
    print(f"  max 0-in-a-row (losing)  : {result['max_nontarget_in_a_row']}")
    print(f"  max 1-in-a-row (winning) : {result['max_target_in_a_row']}")
    print(f"  --- strings ---")
    print(f"  layer1 outcome string    : {result['layer1_string']}")
    print(f"  trade string             : {result['trades_string']}")
    print("=" * 60)


# ---------------------------------------------------------------------------
# EXAMPLE — paste a logged string into `s`, label the book, run.
# Default combo F1="011", F2="1*11". Change F1/F2 to test other combos.
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    book = "SHORT"
    s = "101110000111101111101111101111111101011110"
    F1 = "101"
    F2 = "00"
    print_report(A_layer2_wildcard_tester(s, target=1, F1=F1, F2=F2), F1, F2, '0', book)
