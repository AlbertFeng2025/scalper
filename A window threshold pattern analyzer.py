# A_window_threshold_pattern_analyzer
#
# WHAT IT DOES (pure math, no win/lose/up/down language):
#   Scans a binary string left to right and answers ONE question:
#     "After a defined condition, what is the percentage of `target` on the
#      very next bit?"
#
#   The condition has two stages:
#     STAGE 1 (ARM):  the last `window_length` bits contain at least
#                     `zero_threshold` fraction of 0's.
#     STAGE 2 (FIRE): once armed, wait until `pattern` appears as the tail
#                     of the string; the bit immediately AFTER that pattern
#                     is the "fired bit" we measure.
#
#   RULES (locked with the user):
#     - Stay armed after the threshold is met, even if the window later drops
#       below the threshold before the pattern arrives. (Market down -> struggle
#       -> recover: the recovery bits pull the window below threshold, that's ok.)
#     - ONE fire per arming. After a fire, DISARM. The window must reach the
#       threshold again before the next fire. (No continuous trading during a
#       long one-directional run.)
#     - target defaults to 1 (we always look for a HIGH target percentage).
#
#   NOTE ON DIRECTION (social reminder only, not part of the math):
#     The string is just 1's and 0's. The book it came from (LONG or SHORT)
#     only decides which real order direction a strategy would place if it
#     chased `target`. Analysis stays pure-math: we report the target percentage.
#
# PARAMETERS:
#   string          : the binary string of '0'/'1'
#   target          : the bit we measure the percentage of   (default 1)
#   window_length   : rolling window size                    (default 20)
#   zero_threshold  : min fraction of 0's in window to arm   (default 0.65)
#   pattern         : follow-up pattern that triggers a fire (default "011")


def A_window_threshold_pattern_analyzer(
    string,
    target=1,
    window_length=20,
    zero_threshold=0.65,
    pattern="011",
):
    target = str(target)
    n = len(string)

    fired_bits = []          # the bit after each fired pattern
    fire_positions = []      # index of each fired bit (for optional inspection)

    armed = False
    pos = 0
    while pos < n:
        # STAGE 1 — arm if the trailing window has enough 0's.
        if not armed and pos >= window_length:
            window = string[pos - window_length:pos]
            if window.count('0') / window_length >= zero_threshold:
                armed = True

        # STAGE 2 — if armed and the tail matches `pattern`, the bit at `pos`
        # is the fired bit. Record it, then DISARM (one fire per arming).
        if armed and pos >= len(pattern) and string[pos - len(pattern):pos] == pattern:
            fired_bits.append(string[pos])
            fire_positions.append(pos)
            armed = False

        pos += 1

    # ---- metrics ----
    fire_count = len(fired_bits)
    target_hits = sum(1 for b in fired_bits if b == target)
    fired_target_pct = (target_hits / fire_count * 100) if fire_count else 0.0

    # fire rate: how OFTEN the condition fired, relative to string length.
    # Shown two ways: as a percent of bits, and as "one fire per N bits".
    fire_rate_pct = (fire_count / n * 100) if n else 0.0
    bits_per_fire = (n / fire_count) if fire_count else 0.0

    # original string target percentage (baseline)
    orig_target = string.count(target)
    orig_target_pct = (orig_target / n * 100) if n else 0.0

    # max run of NON-target (the "0 in a row" relative to target) among fired bits
    # and max run of target among fired bits.
    def max_run(seq, ch):
        m = c = 0
        for x in seq:
            if x == ch:
                c += 1
                m = max(m, c)
            else:
                c = 0
        return m

    non_target = '0' if target == '1' else '1'
    max_nontarget_row = max_run(fired_bits, non_target)
    max_target_row = max_run(fired_bits, target)

    return {
        "string_length": n,
        "orig_target_pct": round(orig_target_pct, 1),
        "orig_target_count": f"{orig_target}/{n}",
        "fire_count": fire_count,
        "fire_rate_pct": round(fire_rate_pct, 1),
        "bits_per_fire": round(bits_per_fire, 1),
        "fired_target_pct": round(fired_target_pct, 1),
        "fired_target_count": f"{target_hits}/{fire_count}",
        "max_nontarget_in_a_row": max_nontarget_row,   # losing streak (for breaker)
        "max_target_in_a_row": max_target_row,          # winning streak (for sizing)
        "fired_bits": "".join(fired_bits),
        "fire_positions": fire_positions,
    }


def print_report(result, params=None):
    print("=" * 56)
    print("A_window_threshold_pattern_analyzer")
    print("=" * 56)
    if params:
        print(f"  target          = {params.get('target', 1)}")
        print(f"  window_length   = {params.get('window_length', 20)}")
        print(f"  zero_threshold  = {params.get('zero_threshold', 0.65)} "
              f"({int(params.get('zero_threshold', 0.65) * 100)}% zeros to arm)")
        print(f"  pattern         = {params.get('pattern', '011')}")
        print("-" * 56)
    print(f"  string length              : {result['string_length']}")
    print(f"  original target percentage : {result['orig_target_pct']}%  "
          f"({result['orig_target_count']})   <- baseline")
    print(f"  fire count                 : {result['fire_count']}")
    print(f"  fire rate                  : {result['fire_rate_pct']}% of bits  "
          f"(~1 fire per {result['bits_per_fire']} bits)   <- how OFTEN")
    print(f"  FIRED target percentage    : {result['fired_target_pct']}%  "
          f"({result['fired_target_count']})   <- THE ANSWER")
    print(f"  max non-target in a row    : {result['max_nontarget_in_a_row']}  "
          f"(losing streak; for breaker)")
    print(f"  max target in a row        : {result['max_target_in_a_row']}  "
          f"(winning streak; for sizing)")
    print(f"  fired bits                 : {result['fired_bits']}")
    print("=" * 56)


# ---------------------------------------------------------------------------
# EXAMPLE — paste your logged string into `s` and run.
#
# IMPORTANT: always note which BOOK the string came from, so you remember
# what a real trade would do (LONG book target=1 -> long order;
# SHORT book target=1 -> short order). This does NOT change the math —
# we always look for a HIGH target percentage — it is only a reminder.
#
# The sample string below is a LONG book (1 = price up), 20/20, MNQ,
# recorded over a session where the real market went DOWN.
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    book = "LONG"   # <- label the book this string came from (LONG or SHORT)
    s = (
        "1000000110010011101011111111111010100000000001101111010100110000"
        "0110100101010100011011000001111011010000000001100010000100100010"
        "0100100010010101101010010001111111111010011010000110001011110111"
        "0000110011011101110111011100001111010110100110000110010110010000"
        "0101010100111100000111001010101110010100001111111000101010000101"
        "0000100011"
    )

    # ---- set your parameters here ----
    params = dict(
        target=1,
        window_length=20,
        zero_threshold=0.65,
        pattern="011",
    )

    result = A_window_threshold_pattern_analyzer(s, **params)
    print(f"\n(book = {book})")
    print_report(result, params)
