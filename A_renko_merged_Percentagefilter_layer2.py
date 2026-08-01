# ==============================================================================
# A_renko_merged_Percentagefilter_layer2.py
# ==============================================================================
# Background: 
# This script extends the Layer 1 Renko bar reversal logic using a percentage-based
# filter for Layer 2.
# 
# Step 1: Generates Layer 1 outcomes (long, short, and merged) using pattern F1.
# Step 2: Evaluates a sliding chunk (NumberBit_to_search) of the Layer 1 string.
#         If the percentage of '1's falls within [lowerbound, upperbound], it 
#         captures the subsequent 'NumberBit_to_capture' bits and skips forward 
#         (no overlay).
# Step 3: Calculates and prints the statistics for BOTH Layer 1 and Layer 2.
# ==============================================================================

def analyze_renko_percentage_layer2(renkobar_rawString, F1, num_search, pct_lower, pct_upper, num_capture):
    # ---------------------------------------------------------
    # STEP 1: Build Layer 1 Strings (Long, Short, Merged)
    # ---------------------------------------------------------
    long_rawString = renkobar_rawString
    short_rawString = "".join(['1' if bit == '0' else '0' for bit in long_rawString])
    
    F1_longOutcome = ""
    F1_shortOutcome = ""
    F1_mergedOutcome = ""
    
    f1_len = len(F1)
    
    for i in range(f1_len, len(long_rawString)):
        pattern_L = long_rawString[i - f1_len : i]
        pattern_S = short_rawString[i - f1_len : i]
        
        next_bit_L = long_rawString[i]
        next_bit_S = short_rawString[i]
        
        if pattern_L == F1:
            F1_longOutcome += next_bit_L
            F1_mergedOutcome += next_bit_L
            
        if pattern_S == F1:
            F1_shortOutcome += next_bit_S
            F1_mergedOutcome += next_bit_S

    # ---------------------------------------------------------
    # STEP 2: Build Layer 2 Strings using Percentage Filter
    # ---------------------------------------------------------
    def percentage_filter(source_string, search_len, lower_bnd, upper_bnd, capture_len):
        outcome = ""
        i = 0
        
        # Slide through the string until there's not enough room for a full search chunk
        while i <= len(source_string) - search_len:
            # 1. Isolate the search chunk
            chunk = source_string[i : i + search_len]
            
            # 2. Calculate the percentage of 1's
            ones_count = chunk.count('1')
            pct = (ones_count / search_len) * 100
            
            # 3. Check if percentage is within bounds
            if lower_bnd <= pct <= upper_bnd:
                # Define start and end indices for the capture
                capture_start = i + search_len
                capture_end = capture_start + capture_len
                
                # Capture the bits (Python safely truncates if capture_end exceeds string length)
                captured_bits = source_string[capture_start:capture_end]
                outcome += captured_bits
                
                # Move the pointer to the end of the captured chunk (NO OVERLAY)
                i = capture_end
            else:
                # Condition not met, slide the search window forward by 1 bit
                i += 1
                
        return outcome

    # Apply the percentage filter to the respective F1 outcomes
    F2_longOutcome = percentage_filter(F1_longOutcome, num_search, pct_lower, pct_upper, num_capture)
    F2_shortOutcome = percentage_filter(F1_shortOutcome, num_search, pct_lower, pct_upper, num_capture)
    F2_mergedOutcome = percentage_filter(F1_mergedOutcome, num_search, pct_lower, pct_upper, num_capture)

    # ---------------------------------------------------------
    # STEP 3: Calculate and Print Statistics
    # ---------------------------------------------------------
    def calculate_stats(outcome_str):
        if not outcome_str:
            return 0.0, 0, 0
        
        total_bits = len(outcome_str)
        ones_pct = (outcome_str.count('1') / total_bits) * 100
        max_zeros = max((len(zero_seq) for zero_seq in outcome_str.split('1')), default=0)
        
        return ones_pct, max_zeros, total_bits

    # Generate stats for Layer 1 strings
    f1_long_pct, f1_long_max_0, f1_long_len = calculate_stats(F1_longOutcome)
    f1_short_pct, f1_short_max_0, f1_short_len = calculate_stats(F1_shortOutcome)
    f1_merged_pct, f1_merged_max_0, f1_merged_len = calculate_stats(F1_mergedOutcome)

    # Generate stats for Layer 2 strings
    f2_long_pct, f2_long_max_0, f2_long_len = calculate_stats(F2_longOutcome)
    f2_short_pct, f2_short_max_0, f2_short_len = calculate_stats(F2_shortOutcome)
    f2_merged_pct, f2_merged_max_0, f2_merged_len = calculate_stats(F2_mergedOutcome)

    # --- Print Results to Console ---
    print("=" * 70)
    print(" RAW STRINGS & CONFIGURATION")
    print("=" * 70)
    print(f"Raw Input String:          {long_rawString}")
    print(f"Layer 1 Filter (F1):       '{F1}'")
    print(f"Layer 2 NumberBit_to_search: {num_search}")
    print(f"Layer 2 Percentage Bounds:   {pct_lower}% to {pct_upper}%")
    print(f"Layer 2 NumberBit_to_capture:{num_capture}")
    
    print("\n" + "=" * 70)
    print(" LAYER 1 RESULTS & STATISTICS")
    print("=" * 70)
    print(f"F1_longOutcome:   {F1_longOutcome}")
    print(f"  -> Total: {f1_long_len} bits | 1's: {f1_long_pct:.2f}% | Max 0's: {f1_long_max_0}")
    
    print(f"\nF1_shortOutcome:  {F1_shortOutcome}")
    print(f"  -> Total: {f1_short_len} bits | 1's: {f1_short_pct:.2f}% | Max 0's: {f1_short_max_0}")
    
    print(f"\nF1_mergedOutcome: {F1_mergedOutcome}")
    print(f"  -> Total: {f1_merged_len} bits | 1's: {f1_merged_pct:.2f}% | Max 0's: {f1_merged_max_0}")

    print("\n" + "=" * 70)
    print(" LAYER 2 RESULTS & STATISTICS (Filtered by Percentage)")
    print("=" * 70)
    print(f"(1) F2_longOutcome:   {F2_longOutcome if F2_longOutcome else '[Empty - Condition not met]'}")
    print(f"  -> Total: {f2_long_len} bits | 1's: {f2_long_pct:.2f}% | Max 0's: {f2_long_max_0}")
    
    print(f"\n(2) F2_shortOutcome:  {F2_shortOutcome if F2_shortOutcome else '[Empty - Condition not met]'}")
    print(f"  -> Total: {f2_short_len} bits | 1's: {f2_short_pct:.2f}% | Max 0's: {f2_short_max_0}")
    
    print(f"\n(3) F2_mergedOutcome: {F2_mergedOutcome if F2_mergedOutcome else '[Empty - Condition not met]'}")
    print(f"  -> Total: {f2_merged_len} bits | 1's: {f2_merged_pct:.2f}% | Max 0's: {f2_merged_max_0}")
    print("=" * 70)


# ==============================================================================
# USER CONFIGURATION PANEL (RUN SCRIPT HERE)
# ==============================================================================
if __name__ == "__main__":
    # 1. Define your starting raw string
    RAW_STRING = "111110011001000100011110111100001010111101011100011"
    
    # 2. Define your Layer 1 trigger
    PATTERN_F1 = "10"
    
    # 3. Define your Layer 2 Percentage Filter parameters
    NUM_SEARCH = 10
    PCT_LOWER  = 10
    PCT_UPPER  = 20
    NUM_CAPTURE = 5
    
    # Execute function
    analyze_renko_percentage_layer2(
        renkobar_rawString = RAW_STRING, 
        F1 = PATTERN_F1, 
        num_search = NUM_SEARCH,
        pct_lower = PCT_LOWER,
        pct_upper = PCT_UPPER,
        num_capture = NUM_CAPTURE
    )
