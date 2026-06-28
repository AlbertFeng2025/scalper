# A_layer2_wildcard_tester
#
# PURE MATH. String of 0/1 only.
#
# Standard Layer-2 filter pipeline (F1 then F2 -> chase target), but a filter
# pattern may contain ONE flexible wildcard '*' meaning "a run of >=1 of a
# specified bit" (default the gap bit is '0').
#
#   "1*11" with wild_bit='0'  ->  1, then one-or-more 0s, then 1, then 1
#                                 (tail-anchored; matches ...10011, ...100011, ...1011)
#   "011"  (no '*')           ->  plain fixed pattern
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
    """Return f(s) -> bool: does s END WITH pattern? '*' = one-or-more wild_bit."""
    if '*' not in pattern:
        return lambda s: s.endswith(pattern)
    rx = ""
    for ch in pattern:
        rx += (wild_bit + "+") if ch == '*' else ch
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
            f1 += b
            isArmed = m2(f1)
        if m1(rawString):
            waitF1 = True
        if isArmed and m1(rawString):
            pending = True

    n = len(string)
    fire_count = len(trades)
    hits = sum(1 for t in trades if t == target_bit)
    fired_pct = (hits / fire_count * 100) if fire_count else 0.0
    base_pct = (string.count(target_bit) / n * 100) if n else 0.0

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
    print(f"  baseline P(1)            : {result['baseline_pct']}%   <- whole-string")
    print(f"  fire count (trades)      : {result['fire_count']}")
    print(f"  fire rate                : {result['fire_rate_pct']}% of bits  "
          f"(~1 per {result['bits_per_fire']} bits)")
    print(f"  FIRED P(1)               : {result['fired_pct']}%  "
          f"({result['fired_count']})   <- THE ANSWER")
    print(f"  max 0-in-a-row (losing)  : {result['max_nontarget_in_a_row']}")
    print(f"  max 1-in-a-row (winning) : {result['max_target_in_a_row']}")
    print(f"  trade string             : {result['trades_string']}")
    print("=" * 60)


# ---------------------------------------------------------------------------
# EXAMPLE — paste a logged string into `s`, label the book, run.
# Default combo F1="011", F2="1*11". Change F1/F2 to test other combos.
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    book = "SHORT"
    s = (
        "110011111111101100011111111000100101011110111010010101001111110010"
        "100001011010100001010111111100100100001010010111111111010001001011"
        "010001110100011101011111010010000100101011000010110101010010000000"
        "101101111010000010010100000010110110001111111111011101110011110111"
        "011111010110101110111110000100100000110101101110010010011010110111"
        "101111000010101000011100001001101110011110011110110101111011100001"
        "111101101100001110111101110111110101010011111110100110011000011100"
        "000011101111000001111010110100101011101111100111010101011111001100"
        "101010111011100111100111110110011111101010010100001101011011110110"
        "000110100100011110100011111111111010101110101110111011110001011010"
        "010001101100011111111011111001001010000100011101001010011110001010"
        "000111111111011111101111010011110110001111010001"
    )

    F1 = "011"
    F2 = "1*11"
    print_report(A_layer2_wildcard_tester(s, target=1, F1=F1, F2=F2), F1, F2, '0', book)
