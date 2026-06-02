"""
pattern_analyzer.py
--------------------
Analyze a binary outcome string (1=win, 0=loss) to find:
  - Overall statistics
  - After a given pattern, what is the next outcome distribution?

Usage:
    from pattern_analyzer import analyze

    result = analyze("1101011010", patterns=["01", "011"])
    print(result)

Or run directly:
    python pattern_analyzer.py
"""

from typing import Optional


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
        sequence_length     : int
        overall_wins        : int
        overall_losses      : int
        overall_win_pct     : float  (0-100)
        overall_loss_pct    : float  (0-100)
        patterns            : dict keyed by pattern string, each containing:
            occurrences         : int   (how many times pattern appears)
            next_win_count      : int   (times next digit is 1)
            next_loss_count     : int   (times next digit is 0)
            next_win_pct        : float (0-100, or None if no next digit)
            next_loss_pct       : float (0-100, or None if no next digit)
            positions           : list[int]  (0-based index of each match start)
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

        # Slide through sequence looking for pattern matches
        for i in range(n - plen + 1):
            if seq[i:i + plen] == pat:
                occurrences += 1
                positions.append(i)
                # Check the digit immediately after the pattern
                next_idx = i + plen
                if next_idx < n:
                    if seq[next_idx] == '1':
                        next_win += 1
                    else:
                        next_loss += 1

        followable = next_win + next_loss  # matches that have a next digit

        result["patterns"][pat] = {
            "occurrences"    : occurrences,
            "next_win_count" : next_win,
            "next_loss_count": next_loss,
            "next_win_pct"   : round(next_win  / followable * 100, 2) if followable > 0 else None,
            "next_loss_pct"  : round(next_loss / followable * 100, 2) if followable > 0 else None,
            "positions"      : positions,
        }

    return result


def format_report(result: dict) -> str:
    """
    Format the analyze() result as a human-readable report string.
    """
    if "error" in result:
        return f"ERROR: {result['error']}"

    lines = []
    lines.append("=" * 55)
    lines.append("OUTCOME SEQUENCE ANALYSIS")
    lines.append("=" * 55)
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
            else:
                lines.append(f"    Next digit       : N/A (pattern only appears at end of sequence)")
            lines.append(f"    Positions        : {stats['positions']}")
        lines.append("")

    lines.append("=" * 55)
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
