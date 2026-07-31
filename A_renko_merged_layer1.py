# ==============================================================================
# A_renko_merged_layer1.py
# ==============================================================================
# Background: 
# This script tracks Renko bar reversals by monitoring a raw string of binary 
# states (e.g., 1=Green, 0=Red). It simultaneously tracks a "long" string and 
# its inverse "short" string. 
#
# When a specific pattern (F1, default "10") is found in EITHER string, 
# it captures the very next bit from that respective string and appends it 
# to a merged outcome string, perfectly capturing choppy sideways market data.
# ==============================================================================

def analyze_renko_layer1(renkobar_rawString, F1="10"):
    # ---------------------------------------------------------
    # STEP 1: Build the Long and Short raw strings
    # ---------------------------------------------------------
    long_rawString = renkobar_rawString
    
    # Flip '1's to '0's and '0's to '1's for the short string
    short_rawString = "".join(['1' if bit == '0' else '0' for bit in long_rawString])
    
    # ---------------------------------------------------------
    # STEP 2: Build the three outcome strings
    # ---------------------------------------------------------
    F1_longOutcome = ""
    F1_shortOutcome = ""
    F1_mergedOutcome = ""
    
    trigger_len = len(F1)
    
    # We start looping at the index equal to our trigger length.
    # For F1="10" (length 2), we start at index 2, looking back at index 0 and 1.
    for i in range(trigger_len, len(long_rawString)):
        
        # 1. Look at the trailing pattern of length F1 immediately before index 'i'
        pattern_L = long_rawString[i - trigger_len : i]
        pattern_S = short_rawString[i - trigger_len : i]
        
        # 2. Identify the target bit we will capture if the trigger is met
        next_bit_L = long_rawString[i]
        next_bit_S = short_rawString[i]
        
        # 3. Check for Trigger in Long String
        if pattern_L == F1:
            F1_longOutcome += next_bit_L       # Add to isolated Long outcome
            F1_mergedOutcome += next_bit_L     # Add to merged outcome
            
        # 4. Check for Trigger in Short String
        if pattern_S == F1:
            F1_shortOutcome += next_bit_S      # Add to isolated Short outcome
            F1_mergedOutcome += next_bit_S     # Add to merged outcome

    # ---------------------------------------------------------
    # STEP 3: Calculate and Print Statistics
    # ---------------------------------------------------------
    
    # Helper function to easily calculate stats for any outcome string
    def calculate_stats(outcome_str):
        if not outcome_str:
            return 0.0, 0, 0
        
        total_bits = len(outcome_str)
        ones_pct = (outcome_str.count('1') / total_bits) * 100
        
        # Split the string by '1' to get clusters of '0's, then find the longest cluster
        max_zeros = max((len(zero_seq) for zero_seq in outcome_str.split('1')), default=0)
        
        return ones_pct, max_zeros, total_bits

    # Generate stats for all three strings
    long_pct, long_max_0, long_len = calculate_stats(F1_longOutcome)
    short_pct, short_max_0, short_len = calculate_stats(F1_shortOutcome)
    merged_pct, merged_max_0, merged_len = calculate_stats(F1_mergedOutcome)

    # --- Print Results to Console ---
    print("=" * 60)
    print(" RAW STRINGS")
    print("=" * 60)
    print(f"Long Raw String  (L_raw): {long_rawString}")
    print(f"Short Raw String (S_raw): {short_rawString}")
    print(f"Trigger Pattern  (F1):    '{F1}'")
    print("\n" + "=" * 60)
    print(" OUTCOME STRINGS")
    print("=" * 60)
    print(f"(1) F1_longOutcome:   {F1_longOutcome}")
    print(f"(2) F1_shortOutcome:  {F1_shortOutcome}")
    print(f"(3) F1_mergedOutcome: {F1_mergedOutcome}")
    
    print("\n" + "=" * 60)
    print(" STATISTICS")
    print("=" * 60)
    
    print("--- F1_longOutcome Stats ---")
    print(f"Total Bits Captured:     {long_len}")
    print(f"Percentage of 1's:       {long_pct:.2f}%")
    print(f"Max Consecutive 0's:     {long_max_0}")
    
    print("\n--- F1_shortOutcome Stats ---")
    print(f"Total Bits Captured:     {short_len}")
    print(f"Percentage of 1's:       {short_pct:.2f}%")
    print(f"Max Consecutive 0's:     {short_max_0}")
    
    print("\n--- F1_mergedOutcome Stats ---")
    print(f"Total Bits Captured:     {merged_len}")
    print(f"Percentage of 1's:       {merged_pct:.2f}%")
    print(f"Max Consecutive 0's:     {merged_max_0}")
    print("=" * 60)


# ==============================================================================
# RUN THE SCRIPT
# ==============================================================================
if __name__ == "__main__":
    # User-defined inputs
    input_string = "111110011001000100011110111100001010111101011100011"
    filter_pattern = "10"
    
    # Execute function
    analyze_renko_layer1(renkobar_rawString=input_string, F1=filter_pattern)
