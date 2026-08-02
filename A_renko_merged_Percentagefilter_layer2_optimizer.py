# ==============================================================================
# Renko_Layer2_Optimizer.py
# ==============================================================================
# This script automatically tests thousands of parameter combinations to find 
# the settings that yield the highest percentage of 1's in F2_mergedOutcome.
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
                i = capture_end - num_search
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
    # SEARCH GRID CONFIGURATION
    # Adjust these ranges to search wider or narrower possibilities
    # ------------------------------------------------------------------
    SEARCH_LENGTHS = range(3, 16)       # Tests num_search from 3 to 15
    CAPTURE_LENGTHS = range(1, 8)       # Tests num_capture from 1 to 7
    OVERLAY_OPTIONS = [True, False]
    
    # Minimum amount of bits required in the final string to be considered valid
    MIN_CAPTURE_LENGTH = 30 
    # ------------------------------------------------------------------

    results = []

    print("Running optimizer, testing parameter combinations...")
    
    for n_search in SEARCH_LENGTHS:
        for n_cap in CAPTURE_LENGTHS:
            for overlay in OVERLAY_OPTIONS:
                # Test different percentage brackets (e.g. 0-20, 10-30, 80-100, etc.)
                for p_lower in range(0, 100, 10):
                    for p_upper in range(p_lower + 10, 101, 10):
                        
                        pct, length = get_f2_merged_stats(
                            RAW_STRING, PATTERN_F1, n_search, p_lower, p_upper, n_cap, overlay
                        )
                        
                        if length >= MIN_CAPTURE_LENGTH:
                            results.append({
                                'pct': pct,
                                'len': length,
                                'n_search': n_search,
                                'p_lower': p_lower,
                                'p_upper': p_upper,
                                'n_cap': n_cap,
                                'overlay': overlay
                            })

    # Sort the results by highest percentage first, then by highest length
    results.sort(key=lambda x: (x['pct'], x['len']), reverse=True)

    print("\n" + "=" * 70)
    print(" TOP 10 BEST PARAMETER COMBINATIONS")
    print(f" (Filtering out results with less than {MIN_CAPTURE_LENGTH} total captured bits)")
    print("=" * 70)
    
    if not results:
        print("No combinations met the minimum capture length requirement.")
    else:
        for i, res in enumerate(results[:10]):
            print(f"Rank {i+1}: {res['pct']:.2f}% 1's (Total bits: {res['len']})")
            print(f"  -> num_search: {res['n_search']}, pct_bounds: [{res['p_lower']}-{res['p_upper']}], num_cap: {res['n_cap']}, overlay: {res['overlay']}\n")
