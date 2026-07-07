def A_layer1_tester_with_full_output(string, F1):
    def make_matcher(pattern):
        return lambda s: s.endswith(pattern)
    
    m1 = make_matcher(F1)
    
    rawString = ""
    pending = False
    
    trade_positions = []
    outcome_string = ""
    
    for i, b in enumerate(string):
        # 1. Capture the trade result if the trigger was hit in the previous step
        if pending:
            outcome_string += b
            trade_positions.append({"index": i, "result": b})
            pending = False # Reset the pending trigger
            
        rawString += b
        
        # 2. Check for trigger (F1) directly
        if m1(rawString):
            pending = True
            
    return trade_positions, outcome_string

# --- Test ---
s = "0110110111100011111111110100101111111001111101110001110011010011011010101111111011000111101101111010111111111011111101111101111100110011011011100011111101110011111011110111100101110010011100001101110101011011100111101111111110111111011101111011111110010110111001111101101110101001101010111111100110111100111101110000011001001011101111000111110111011010001110101011111011011110111101011111010010011111110101110"

# F2 is entirely removed
positions, outcomes = A_layer1_tester_with_full_output(s, F1="001100")

print(f"Percentage of 1's in input string: {s.count('1') / len(s) * 100:.2f}%")
print(f"Outcome string: {outcomes}")
print(f"Total trades captured: {len(positions)}")
print(f"Percentage of 1's in outcomes: {outcomes.count('1') / len(outcomes) * 100:.2f}%")
print(f"Trade positions: {positions}")
