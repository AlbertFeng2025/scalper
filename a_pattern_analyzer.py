"""
pattern_analyzer.py
--------------------
Analyze a binary outcome string (1=win, 0=loss) to find:
  - Overall statistics
  - After a given pattern, what is the next outcome distribution?
  - After next=WIN or next=LOSS, how many consecutive same digits follow?

Usage:
    from pattern_analyzer import analyze

    result = analyze("1101011010", patterns=["01", "011"])
    print(format_report(result))

Or run directly:
    python pattern_analyzer.py
"""

from collections import Counter


def _count_consecutive(seq: str, start: int) -> int:
    """
    Count how many consecutive identical characters begin at seq[start].
    e.g. seq="11100", start=0 → 3
         seq="11100", start=3 → 2
    Returns 0 if start is out of bounds.
    """
    if start >= len(seq):
        return 0
    ch = seq[start]
    count = 0
    i = start
    while i < len(seq) and seq[i] == ch:
        count += 1
        i += 1
    return count


def analyze(sequence: str, patterns: list[str]) -> dict:
    """
    Analyze a binary outcome string against one or more patterns.

    Parameters
    ----------
    sequence : str
        Binary string of '0' and '1' characters.
        e.g. "1101011010"
    patterns : list[str]
        List of binary pattern strings to search for.
        e.g. ["01", "011"]

    Returns
    -------
    dict with keys:
        sequence_length         : int
        overall_wins            : int
        overall_losses          : int
        overall_win_pct         : float  (0-100)
        overall_loss_pct        : float  (0-100)
        patterns                : dict keyed by pattern string, each containing:
            occurrences             : int
            next_win_count          : int
            next_loss_count         : int
            next_win_pct            : float | None
            next_loss_pct           : float | None
            positions               : list[int]
            consec_after_win        : dict  {run_length: count}  (distribution of
                                            consecutive wins when next digit = 1)
            consec_after_win_avg    : float | None
            consec_after_loss       : dict  {run_length: count}  (distribution of
                                            consecutive losses when next digit = 0)
            consec_after_loss_avg   : float | None
    """

    # ---- Validate input ----
    seq = sequence.strip()
    if not seq:
        return {"error": "Empty sequence"}
    invalid = [c for c in seq if c not in ('0', '1')]
    if invalid:
        return {"error": f"Invalid characters in sequence: {set(invalid)}"}
    if not patterns:
        return {"error": "No patterns provided"}

    n = len(seq)
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

        consec_win_runs  = []   # run lengths collected when next digit = 1
        consec_loss_runs = []   # run lengths collected when next digit = 0

        # Slide through sequence looking for pattern matches
        for i in range(n - plen + 1):
            if seq[i:i + plen] == pat:
                occurrences += 1
                positions.append(i)

                next_idx = i + plen
                if next_idx < n:
                    run_len = _count_consecutive(seq, next_idx)
                    if seq[next_idx] == '1':
                        next_win += 1
                        consec_win_runs.append(run_len)
                    else:
                        next_loss += 1
                        consec_loss_runs.append(run_len)

        followable = next_win + next_loss

        # Build distributions (sorted by run length for readability)
        def _distribution(runs: list) -> dict:
            return dict(sorted(Counter(runs).items()))

        def _average(runs: list) -> float | None:
            return round(sum(runs) / len(runs), 2) if runs else None

        result["patterns"][pat] = {
            "occurrences"          : occurrences,
            "next_win_count"       : next_win,
            "next_loss_count"      : next_loss,
            "next_win_pct"         : round(next_win  / followable * 100, 2) if followable > 0 else None,
            "next_loss_pct"        : round(next_loss / followable * 100, 2) if followable > 0 else None,
            "positions"            : positions,
            "consec_after_win"     : _distribution(consec_win_runs),
            "consec_after_win_avg" : _average(consec_win_runs),
            "consec_after_loss"    : _distribution(consec_loss_runs),
            "consec_after_loss_avg": _average(consec_loss_runs),
        }

    return result


def format_report(result: dict) -> str:
    """
    Format the analyze() result as a human-readable report string.
    """
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
            lines.append(f"    Occurrences         : {stats['occurrences']}")

            if stats["next_win_pct"] is not None:
                lines.append(f"    Next = WIN  (1)     : {stats['next_win_count']}  ({stats['next_win_pct']}%)")
                # Consecutive wins distribution
                if stats["consec_after_win"]:
                    lines.append(f"      Consec WIN runs (avg {stats['consec_after_win_avg']}):")
                    for run_len, count in stats["consec_after_win"].items():
                        bar = "█" * count
                        lines.append(f"        {run_len:>2} consecutive : {count:>3}x  {bar}")
                else:
                    lines.append(f"      Consec WIN runs : N/A")

                lines.append(f"    Next = LOSS (0)     : {stats['next_loss_count']}  ({stats['next_loss_pct']}%)")
                # Consecutive losses distribution
                if stats["consec_after_loss"]:
                    lines.append(f"      Consec LOSS runs (avg {stats['consec_after_loss_avg']}):")
                    for run_len, count in stats["consec_after_loss"].items():
                        bar = "█" * count
                        lines.append(f"        {run_len:>2} consecutive : {count:>3}x  {bar}")
                else:
                    lines.append(f"      Consec LOSS runs : N/A")
            else:
                lines.append(f"    Next digit          : N/A (pattern only appears at end of sequence)")

            lines.append(f"    Positions           : {stats['positions']}")
        lines.append("")

    lines.append("=" * 60)
    return "\n".join(lines)


def analyze_and_print(sequence: str, patterns: list[str]) -> dict:
    """Convenience: analyze + print report, return raw result."""
    result = analyze(sequence, patterns)
    print(format_report(result))
    return result


# ---- Demo / self-test when run directly ----
if __name__ == "__main__":

    # Example 1 — basic usage
    seq1 = "1101011010011101"
    print(f"Sequence: {seq1}\n")
    analyze_and_print(seq1, ["01", "011", "0", "1"])

    print()

    # Example 2 — longer sequence
    seq2 = "11010110100111011101101011010011"
    print(f"Sequence: {seq2}\n")
    analyze_and_print(seq2, ["01", "011", "0111"])

    print()

    # Example 3 — edge cases
    seq3 = "0000011111"
    print(f"Sequence: {seq3}\n")
    analyze_and_print(seq3, ["00", "11", "0011"])
