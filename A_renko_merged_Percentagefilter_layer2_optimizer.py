# ==============================================================================
# Renko_Layer2_Cluster_Optimizer.py (with Overlay toggle)
# ==============================================================================
# This script maps all parameter combinations and then scans for "Clusters" - 
# areas where a parameter AND its immediate neighbors all perform well.
# It now tests both Overlay = True and Overlay = False and prints the result.
# ==============================================================================

def get_f2_merged_stats(long_rawString, F1, num_search, pct_lower, pct_upper, num_capture, allow_overlay):
    if num_capture <= 0:
        return 0.0, 0

    # 1. Build Layer 1 Merged String
    short_rawString = "".join(['1' if bit == '0' else '0' for bit in long_rawString])
    F1_mergedOutcome = ""
    f1_len = len(F1)
    
    for i in range(f1_len, len(long_rawString)):
        pattern_L = long_rawString[i - f1_len : i]
        pattern_S = short_rawString[i - f1_len : i]
        
        if pattern_L == F1:
            F1_mergedOutcome += long_rawString[i]
        if pattern_S == F1:
            F1_mergedOutcome += short_rawString[i]

    # 2. Build Layer 2 Merged String
    outcome = ""
    i = 0
    while i <= len(F1_mergedOutcome) - num_search:
        chunk = F1_mergedOutcome[i : i + num_search]
        pct = (chunk.count('1') / num_search) * 100
        
        if pct_lower <= pct <= pct_upper:
            capture_start = i + num_search
            capture_end = capture_start + num_capture
            outcome += F1_mergedOutcome[capture_start:capture_end]
            
            if allow_overlay:
                i = max(i + 1, capture_end - num_search)
            else:
                i = capture_end
        else:
            i += 1
            
    # 3. Calculate Stats
    total_bits = len(outcome)
    if total_bits == 0:
        return 0.0, 0
        
    ones_pct = (outcome.count('1') / total_bits) * 100
    return ones_pct, total_bits

if __name__ == "__main__":
    RAW_STRING = "0011110101111101110101101001101000110110011100000010000011001110011100111100111111111110000001000101111111111101001100011000101100001101000000010111011100010000001101000000001110011111000001111011101000100000011011111010000001110000100001011111100011011001110011111000000110000111100000101000101111111110101001100000111000100000001110010000001111111100100000000011010111111111011111111010010001111111011101100000000111111011111111011001101100101111101110111000111011100011111010001110011111010000001111101110011110111111000110101100111100011100001111001000001111100011111110000110000111001111000100111001101001111111111111111111111110000011101101111000101111011110001100010000001100011101110011100001101111101111111110001110101001101000000011000011000000000000011111000000101111111101100000111111100000000010000111110001111110010000101110000000111100010111011011110011000011101111011100"
    PATTERN_F1 = "10"
    
    # ------------------------------------------------------------------
    # ROBUSTNESS CRITERIA
    # ------------------------------------------------------------------
    MIN_CAPTURE_LENGTH = 30 
    MIN_NEIGHBOR_WIN_RATE = 35.0 
    # ------------------------------------------------------------------

    # 1. Run Grid Search and store everything in a dictionary
    results_map = {}
    print("Step 1: Running grid search (Testing both Overlay True and False)...")
    
    for overlay in [True, False]:  # Now testing both!
        for n_search in range(3, 16):
            for n_cap in range(1, 8):
                for p_lower in range(0, 100, 10):
                    for p_upper in range(p_lower + 10, 101, 10):
                        pct, length = get_f2_merged_stats(
                            RAW_STRING, PATTERN_F1, n_search, p_lower, p_upper, n_cap, overlay
                        )
                        
                        if length >= MIN_CAPTURE_LENGTH:
                            # We now store overlay in the dictionary key so we can track it
                            results_map[(n_search, n_cap, p_lower, p_upper, overlay)] = (pct, length)

    # 2. Analyze Neighborhoods (Clusters)
    print("Step 2: Finding robust parameter clusters...")
    clusters = []
    
    for params, (center_pct, center_len) in results_map.items():
        S, C, L, U, overlay = params
        
        # Look at the 3x3 grid around our current parameter set
        # We keep the overlay, lower, and upper bounds the same, but shift search and capture lengths
        neighbors = []
        for dS in [-1, 0, 1]:
            for dC in [-1, 0, 1]:
                neighbor_key = (S + dS, C + dC, L, U, overlay)
                if neighbor_key in results_map:
                    neighbors.append(results_map[neighbor_key])
        
        # A full 3x3 grid has 9 points. We want at least 6 valid neighbors 
        if len(neighbors) >= 6:
            avg_pct = sum(n[0] for n in neighbors) / len(neighbors)
            min_pct = min(n[0] for n in neighbors)
            total_cluster_trades = sum(n[1] for n in neighbors)
            
            # Filter: Does the WORST neighbor still survive our break-even threshold?
            if min_pct >= MIN_NEIGHBOR_WIN_RATE:
                clusters.append({
                    'params': params,
                    'center_pct': center_pct,
                    'avg_pct': avg_pct,
                    'min_pct': min_pct,
                    'total_trades': total_cluster_trades,
                    'neighbor_count': len(neighbors)
                })

    # Sort the clusters by the highest AVERAGE percentage across the neighborhood
    clusters.sort(key=lambda x: x['avg_pct'], reverse=True)

    # 3. Print Results
    print("\n" + "=" * 80)
    print(" TOP ROBUST PARAMETER CLUSTERS")
    print(f" (Worst neighbor must stay above {MIN_NEIGHBOR_WIN_RATE}% win rate)")
    print("=" * 80)
    
    if not clusters:
        print("No clusters met the strict robustness and break-even requirements.")
        print("Try lowering the MIN_NEIGHBOR_WIN_RATE or providing a longer RAW_STRING.")
    else:
        for i, cl in enumerate(clusters[:10]):
            S, C, L, U, overlay = cl['params']
            print(f"Rank {i+1}: Center [Search:{S}, Cap:{C}, Bounds:{L}-{U}, Overlay: {overlay}]")
            print(f"  -> Cluster Average Win Rate: {cl['avg_pct']:.2f}% (over {cl['neighbor_count']} neighbors)")
            print(f"  -> Center Point Win Rate:    {cl['center_pct']:.2f}%")
            print(f"  -> WORST Neighbor Win Rate:  {cl['min_pct']:.2f}%  <-- Safe!")
            print(f"  -> Total bits captured across cluster: {cl['total_trades']}\n")
