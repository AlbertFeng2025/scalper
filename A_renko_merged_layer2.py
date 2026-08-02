# ==============================================================================
# A_renko_merged_layer2.py
# ==============================================================================
# Background: 
# This script extends the Layer 1 Renko bar reversal logic. 
# 
# Step 1: Generates the Layer 1 outcomes (long, short, and merged) using pattern F1.
# Step 2: Applies a second filter, pattern F2, directly to each of the three Layer 1
#         outcome strings to extract the next bit, creating Layer 2 strings.
# Step 3: Calculates and prints the statistics for the Layer 2 strings.
#
# INPUT CONVENTION (important):
#   The input string is GREEN=1 / RED=0  (i.e. the LONG-book encoding, up-brick=1).
#   -> long_rawString  = input as-is        (green=1 = LONG book, 1 = long win)
#   -> short_rawString = bitwise flip        (green=0 = SHORT book, 1 = short win)
#   NOTE: the saved renko_strings.json is SHORT-encoded (green=0). To use those
#         here, FLIP them first, or the long/short labels will be reversed.
# ==============================================================================

def analyze_renko_layer2(renkobar_rawString, F1="10", F2="100"):
    # ---------------------------------------------------------
    # STEP 1: Build Layer 1 Strings (Long, Short, Merged)
    # ---------------------------------------------------------
    long_rawString  = renkobar_rawString                                            # green=1/red=0 -> LONG book (1 = long win)
    short_rawString = "".join(['1' if bit == '0' else '0' for bit in long_rawString])  # flip -> SHORT book (1 = short win)
    
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
    # STEP 2: Build Layer 2 Strings using F2
    # ---------------------------------------------------------
    # Helper function to extract bits based on a pattern from a given string
    def filter_by_pattern(source_string, pattern):
        outcome = ""
        p_len = len(pattern)
        for i in range(p_len, len(source_string)):
            if source_string[i - p_len : i] == pattern:
                outcome += source_string[i]
        return outcome

    # Apply F2 to the respective F1 outcomes
    F2_longOutcome = filter_by_pattern(F1_longOutcome, F2)
    F2_shortOutcome = filter_by_pattern(F1_shortOutcome, F2)
    F2_mergedOutcome = filter_by_pattern(F1_mergedOutcome, F2)

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

    # Generate stats for all three Layer 2 strings
    f2_long_pct, f2_long_max_0, f2_long_len = calculate_stats(F2_longOutcome)
    f2_short_pct, f2_short_max_0, f2_short_len = calculate_stats(F2_shortOutcome)
    f2_merged_pct, f2_merged_max_0, f2_merged_len = calculate_stats(F2_mergedOutcome)

    # --- Print Results to Console ---
    print("=" * 60)
    print(" RAW STRINGS & SETTINGS")
    print("=" * 60)
    print(f"Raw Input String: {long_rawString}")
    print(f"Layer 1 Filter (F1): '{F1}'")
    print(f"Layer 2 Filter (F2): '{F2}'")
    
    print("\n" + "=" * 60)
    print(" LAYER 1 OUTCOMES (For Reference)")
    print("=" * 60)
    print(f"F1_longOutcome:   {F1_longOutcome}")
    print(f"F1_shortOutcome:  {F1_shortOutcome}")
    print(f"F1_mergedOutcome: {F1_mergedOutcome}")

    print("\n" + "=" * 60)
    print(" LAYER 2 OUTCOMES (Filtered by F2)")
    print("=" * 60)
    print(f"(1) F2_longOutcome:   {F2_longOutcome if F2_longOutcome else '[Empty - Pattern not found]'}")
    print(f"(2) F2_shortOutcome:  {F2_shortOutcome if F2_shortOutcome else '[Empty - Pattern not found]'}")
    print(f"(3) F2_mergedOutcome: {F2_mergedOutcome if F2_mergedOutcome else '[Empty - Pattern not found]'}")
    
    print("\n" + "=" * 60)
    print(" LAYER 2 STATISTICS")
    print("=" * 60)
    
    print("--- F2_longOutcome Stats ---")
    print(f"Total Bits Captured:     {f2_long_len}")
    print(f"Percentage of 1's:       {f2_long_pct:.2f}%")
    print(f"Max Consecutive 0's:     {f2_long_max_0}")
    
    print("\n--- F2_shortOutcome Stats ---")
    print(f"Total Bits Captured:     {f2_short_len}")
    print(f"Percentage of 1's:       {f2_short_pct:.2f}%")
    print(f"Max Consecutive 0's:     {f2_short_max_0}")
    
    print("\n--- F2_mergedOutcome Stats ---")
    print(f"Total Bits Captured:     {f2_merged_len}")
    print(f"Percentage of 1's:       {f2_merged_pct:.2f}%")
    print(f"Max Consecutive 0's:     {f2_merged_max_0}")
    print("=" * 60)


# ==============================================================================
# RUN THE SCRIPT
# ==============================================================================
if __name__ == "__main__":
    # User-defined input (GREEN=1 / RED=0 = LONG-book encoding). Feed ONE day at a time.
    renko_green1_red0_string = "111110011001000100011110111100001010111101011100011"
    
    # Execute function with F1 and F2 parameters
    analyze_renko_layer2(renkobar_rawString=renko_green1_red0_string, F1="10", F2="100")
