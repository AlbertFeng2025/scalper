"""
pattern_analyzer.py
--------------------
Analyze a binary outcome string (1=win, 0=loss) to find:
  - Overall statistics
  - After a given pattern, what is the next outcome distribution?
  - Consecutive win/loss streaks across all pattern occurrences (in order)

Usage:
    from pattern_analyzer import analyze

    result = analyze("1101011010", patterns=["01", "011"])
    print(format_report(result))

Or run directly:
    python pattern_analyzer.py
"""


def _compute_streaks(next_digits: list[str]) -> dict:
    """
    Given an ordered list of next digits after each pattern match,
    compute consecutive win and loss streaks.

    e.g. ['1','1','0','1','0','0','1']
    -> win_streaks  = [2, 1, 1]   max_consec_win  = 2
    -> loss_streaks = [1, 2]      max_consec_loss = 2
    """
    win_streaks  = []
    loss_streaks = []

    cur_win  = 0
    cur_loss = 0

    for d in next_digits:
        if d == '1':
            cur_win += 1
            if cur_loss > 0:
                loss_streaks.append(cur_loss)
                cur_loss = 0
        else:
            cur_loss += 1
            if cur_win > 0:
                win_streaks.append(cur_win)
                cur_win = 0

    # flush whatever streak is still open at the end
    if cur_win  > 0: win_streaks.append(cur_win)
    if cur_loss > 0: loss_streaks.append(cur_loss)

    return {
        "win_streaks"     : win_streaks,
        "max_consec_win"  : max(win_streaks)  if win_streaks  else 0,
        "loss_streaks"    : loss_streaks,
        "max_consec_loss" : max(loss_streaks) if loss_streaks else 0,
    }


def analyze(sequence: str, patterns: list[str]) -> dict:
    """
    Analyze a binary outcome string against one or more patterns.

    Parameters
    ----------
    sequence : str
        Binary string of '0' and '1' characters.
    patterns : list[str]
        List of binary pattern strings to search for.

    Returns
    -------
    dict with keys:
        sequence_length     : int
        overall_wins        : int
        overall_losses      : int
        overall_win_pct     : float
        overall_loss_pct    : float
        patterns            : dict keyed by pattern string, each containing:
            occurrences         : int
            next_win_count      : int
            next_loss_count     : int
            next_win_pct        : float | None
            next_loss_pct       : float | None
            positions           : list[int]
            win_streaks         : list[int]   e.g. [2, 1, 1]
            max_consec_win      : int
            loss_streaks        : list[int]   e.g. [1, 2]
            max_consec_loss     : int
    """

    seq = sequence.strip()
    if not seq:
        return {"error": "Empty sequence"}
    invalid = [c for c in seq if c not in ('0', '1')]
    if invalid:
        return {"error": f"Invalid characters in sequence: {set(invalid)}"}
    if not patterns:
        return {"error": "No patterns provided"}

    n      = len(seq)
    wins   = seq.count('1')
    losses = seq.count('0')

    result = {
        "sequence_length" : n,
        "overall_wins"    : wins,
        "overall_losses"  : losses,
        "overall_win_pct" : round(wins   / n * 100, 2) if n > 0 else 0.0,
        "overall_loss_pct": round(losses / n * 100, 2) if n > 0 else 0.0,
        "patterns"        : {}
    }

    for pat in patterns:
        pat = pat.strip()
        if not pat or any(c not in ('0', '1') for c in pat):
            result["patterns"][pat] = {"error": f"Invalid pattern: '{pat}'"}
            continue

        plen        = len(pat)
        occurrences = 0
        next_win    = 0
        next_loss   = 0
        positions   = []
        next_digits = []   # ordered list of next digits for streak tracking

        for i in range(n - plen + 1):
            if seq[i:i + plen] == pat:
                occurrences += 1
                positions.append(i)
                next_idx = i + plen
                if next_idx < n:
                    d = seq[next_idx]
                    next_digits.append(d)
                    if d == '1':
                        next_win += 1
                    else:
                        next_loss += 1

        followable = next_win + next_loss
        streaks    = _compute_streaks(next_digits)

        result["patterns"][pat] = {
            "occurrences"    : occurrences,
            "next_win_count" : next_win,
            "next_loss_count": next_loss,
            "next_win_pct"   : round(next_win  / followable * 100, 2) if followable > 0 else None,
            "next_loss_pct"  : round(next_loss / followable * 100, 2) if followable > 0 else None,
            "positions"      : positions,
            **streaks,
        }

    return result


def format_report(result: dict) -> str:
    if "error" in result:
        return f"ERROR: {result['error']}"

    lines = []
    lines.append("=" * 60)
    lines.append("OUTCOME SEQUENCE ANALYSIS")
    lines.append("=" * 60)
    lines.append(f"  Sequence length : {result['sequence_length']}")
    lines.append(f"  Overall wins    : {result['overall_wins']}  ({result['overall_win_pct']}%)")
    lines.append(f"  Overall losses  : {result['overall_losses']}  ({result['overall_loss_pct']}%)")
    lines.append("")

    for pat, stats in result["patterns"].items():
        lines.append(f"  Pattern  : '{pat}'")
        if "error" in stats:
            lines.append(f"    ERROR: {stats['error']}")
        else:
            lines.append(f"    Occurrences      : {stats['occurrences']}")
            if stats["next_win_pct"] is not None:
                lines.append(f"    Next = WIN  (1)  : {stats['next_win_count']}  ({stats['next_win_pct']}%)")
                lines.append(f"    Next = LOSS (0)  : {stats['next_loss_count']}  ({stats['next_loss_pct']}%)")
                lines.append(f"    Win  streaks     : {stats['win_streaks']}  → max = {stats['max_consec_win']}")
                lines.append(f"    Loss streaks     : {stats['loss_streaks']}  → max = {stats['max_consec_loss']}")
            else:
                lines.append(f"    Next digit       : N/A (pattern only appears at end of sequence)")
            lines.append(f"    Positions        : {stats['positions']}")
        lines.append("")

    lines.append("=" * 60)
    return "\n".join(lines)


def analyze_and_print(sequence: str, patterns: list[str]) -> dict:
    result = analyze(sequence, patterns)
    print(format_report(result))
    return result


if __name__ == "__main__":

    # Verify the example from the conversation:
    # next digits: 1,1,0,1,0,0,1 → win streaks (2,1,1) max=2 | loss streaks (1,2) max=2
     
    # The big sequence from earlier
    seq3 = "1111000100100001111000100110011000000010110010010100001010000000001001100000110011100111110000011110010010010110000001001100000010111000011000110000101000000110111100100010110110111100011100000000010000010010011100011010000100010110111001101001111111001000100101010000"
    print(f"Sequence: {seq3[:40]}...")
    analyze_and_print(seq3, ["000111", "01","011","0111"])
