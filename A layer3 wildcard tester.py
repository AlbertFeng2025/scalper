# A_layer3_wildcard_tester
#
# PURE MATH. String of 0/1 only.
#
# Standard Layer-3 filter pipeline (F1 -> F2 -> F3 -> chase target). ANY filter
# pattern may contain the flexible wildcard '*' meaning "a run of >=1 of a
# specified bit" (default the gap bit is '0'). Wildcards may appear in any
# layer, and the matcher allows the gap to overlay/absorb any run length.
#
#   "1*11" with wild_bit='0'  ->  1, then one-or-more 0s, then 1, then 1
#                                 (tail-anchored; matches ...10011, ...100011, ...1011)
#   "011"  (no '*')           ->  plain fixed pattern
#
# PIPELINE (same as the NinjaTrader Layer-3 strategy):
#   for each bit:
#     1. append bit to rawString
#     2. if waiting-for-F1-outcome: append bit to filter1Outcome (f1)
#          - if also waiting-for-F2-outcome: append bit to filter2Outcome (f2),
#            then isArmed = (f2 tail matches F3)
#          - if f1 tail matches F2 -> start waiting-for-F2-outcome
#     3. if rawString tail matches F1 -> start waiting-for-F1-outcome
#     4. if isArmed AND waiting-F2 AND waiting-F1 -> next bit is a MONEY trade
#   The money-trade bit is the outcome. target=1 = we chase 1.
#
# This matches the existing scalper_*_Layer3 C# pipeline exactly, with the
# single addition that each filter pattern may use '*'.
#
# DEFAULT COMBO shown: F1="011", F2="1*11", F3="1"
#   (set F3 to whatever you want to test; analysis suggested F3 often adds
#    little on top of 011/1*11, but this lets you check any F3.)
#
# OUTPUT: step-by-step P(1) at RAW -> LAYER1 -> LAYER2 -> LAYER3(trade),
#   plus fire count, fire rate, max 0-in-a-row, max 1-in-a-row, and the strings.

import re


def make_matcher(pattern, wild_bit='0'):
    """Return f(s) -> bool: does s END WITH pattern? '*' = one-or-more wild_bit.
       The '+' (one-or-more) lets the gap overlay/absorb any run length."""
    if '*' not in pattern:
        return lambda s: s.endswith(pattern)
    rx = ""
    for ch in pattern:
        rx += (wild_bit + "+") if ch == '*' else ch
    cre = re.compile(rx + "$")
    return lambda s: cre.search(s) is not None


def A_layer3_wildcard_tester(
    string,
    target=1,
    F1="011",
    F2="1*11",
    F3="1",
    wild_bit='0',
):
    target_bit = str(target)
    m1 = make_matcher(F1, wild_bit)
    m2 = make_matcher(F2, wild_bit)
    m3 = make_matcher(F3, wild_bit)

    rawString = ""
    f1 = ""              # filter1Outcome
    f2 = ""              # filter2Outcome
    waitF1 = False
    waitF2 = False
    isArmed = False
    pending = False
    trades = []

    for b in string:
        if pending:
            trades.append(b)
            pending = False
        rawString += b

        if waitF1:
            waitF1 = False
            consumeF2 = waitF2
            f1 += b                       # layer-1 outcome grows
            if consumeF2:
                waitF2 = False
                f2 += b                   # layer-2 outcome grows
                isArmed = m3(f2)          # F3 checked on filter2Outcome
            if m2(f1):                    # F2 checked on filter1Outcome
                waitF2 = True

        if m1(rawString):                 # F1 checked on rawString
            waitF1 = True

        if isArmed and waitF2 and waitF1:
            pending = True

    n = len(string)

    # ---- per-layer percentages (step-by-step filtration) ----
    raw_hits = string.count(target_bit)
    raw_pct = (raw_hits / n * 100) if n else 0.0
    f1_hits = f1.count(target_bit)
    f1_pct = (f1_hits / len(f1) * 100) if f1 else 0.0
    f2_hits = f2.count(target_bit)
    f2_pct = (f2_hits / len(f2) * 100) if f2 else 0.0

    fire_count = len(trades)
    hits = sum(1 for t in trades if t == target_bit)
    fired_pct = (hits / fire_count * 100) if fire_count else 0.0

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
        "raw_pct": round(raw_pct, 1), "raw_count": f"{raw_hits}/{n}",
        "layer1_pct": round(f1_pct, 1), "layer1_count": f"{f1_hits}/{len(f1)}", "layer1_string": f1,
        "layer2_pct": round(f2_pct, 1), "layer2_count": f"{f2_hits}/{len(f2)}", "layer2_string": f2,
        "fire_count": fire_count,
        "fire_rate_pct": round(fire_rate, 1),
        "bits_per_fire": round(bits_per_fire, 1),
        "fired_pct": round(fired_pct, 1), "fired_count": f"{hits}/{fire_count}",
        "max_nontarget_in_a_row": max_nontarget,
        "max_target_in_a_row": max_target,
        "trades_string": trades_str,
    }


def print_report(result, F1="011", F2="1*11", F3="1", wild_bit='0', book=None):
    print("=" * 64)
    print("A_layer3_wildcard_tester")
    print("=" * 64)
    if book:
        print(f"  (book = {book})")
    print(f"  F1 = {F1}   F2 = {F2}   F3 = {F3}   (* = one-or-more '{wild_bit}')")
    print("-" * 64)
    print(f"  string length            : {result['string_length']}")
    print(f"  STEP-BY-STEP FILTRATION (P(1) should climb each layer):")
    print(f"    RAW            : {result['raw_pct']}%  ({result['raw_count']})")
    print(f"    LAYER1 outcome : {result['layer1_pct']}%  ({result['layer1_count']})   after F1={F1}")
    print(f"    LAYER2 outcome : {result['layer2_pct']}%  ({result['layer2_count']})   after F2={F2}")
    print(f"    LAYER3 (trade) : {result['fired_pct']}%  ({result['fired_count']})   after F3={F3}   <- THE ANSWER")
    print(f"  fire rate                : {result['fire_rate_pct']}% of bits  "
          f"(~1 per {result['bits_per_fire']} bits)")
    print(f"  max 0-in-a-row (losing)  : {result['max_nontarget_in_a_row']}")
    print(f"  max 1-in-a-row (winning) : {result['max_target_in_a_row']}")
    print(f"  --- strings ---")
    print(f"  layer1 outcome string    : {result['layer1_string']}")
    print(f"  layer2 outcome string    : {result['layer2_string']}")
    print(f"  trade string             : {result['trades_string']}")
    print("=" * 64)


# ---------------------------------------------------------------------------
# EXAMPLE — paste a logged string into `s`, label the book, set F1/F2/F3, run.
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
    F3 = "1"
    print_report(A_layer3_wildcard_tester(s, target=1, F1=F1, F2=F2, F3=F3),
                 F1, F2, F3, '0', book)
