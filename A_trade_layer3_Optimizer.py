# -*- coding: utf-8 -*-
"""
pipeline_optimizer_v2.py
------------------------
Brute-force optimizer to find the best F1, F2, and F3 patterns.
Includes layer-by-layer distillation metrics (Raw -> F1 -> F2 -> Trade).
"""
import itertools

def simulate_pipeline(market_string: str, f1_pat: str, f2_pat: str, f3_pat: str) -> dict:
    """
    Simulates the exact tick-by-tick state machine from the C# NinjaScript.
    Returns a dictionary containing the stats for every layer in the pipeline.
    """
    raw_string = ""
    f1_outcome = ""
    f2_outcome = ""
    
    is_armed = False
    wait_f1 = False
    wait_f2 = False
    next_is_money = False
    
    trades = []
    
    for bit in market_string:
        # If the pipeline was fully armed on the PREVIOUS slice, THIS slice is the real trade
        if next_is_money:
            trades.append(int(bit))
            
        # Step 1: Append to rawString
        raw_string += bit
        
        # Step 2: Process pending F1 collection
        if wait_f1:
            wait_f1 = False
            consume_f2 = wait_f2  # Snapshot waitF2
            
            f1_outcome += bit
            
            # If waitF2 was already set, this same bit feeds F2
            if consume_f2:
                wait_f2 = False
                f2_outcome += bit
                # Check F3 match to arm the system
                is_armed = f2_outcome.endswith(f3_pat)
                
            # Check F2 match to prime the next F1 digit
            if f1_outcome.endswith(f2_pat):
                wait_f2 = True
                
        # Step 3: Check F1 match to prime the next raw digit
        if raw_string.endswith(f1_pat):
            wait_f1 = True
            
        # Step 4: Determine if NEXT slice is a money trade
        next_is_money = is_armed and wait_f2 and wait_f1
        
    # Helper function to calculate win percentage safely
    def calc_pct(s):
        if not s: return 0.0
        return (s.count('1') / len(s)) * 100

    # Package all metrics into a dictionary to return to the optimizer
    return {
        "raw_len": len(raw_string),
        "raw_pct": calc_pct(raw_string),
        "f1_len": len(f1_outcome),
        "f1_pct": calc_pct(f1_outcome),
        "f2_len": len(f2_outcome),
        "f2_pct": calc_pct(f2_outcome),
        "trade_len": len(trades),
        "trade_wins": sum(trades),
        "trade_pct": (sum(trades) / len(trades) * 100) if trades else 0.0
    }

def generate_patterns(length: int) -> list:
    """Generates all possible binary combinations of a given length."""
    return [''.join(p) for p in itertools.product('01', repeat=length)]

def run_optimization(market_string: str, f1_len: int, f2_len: int, f3_len: int, min_trades: int = 5):
    """
    Tests all combinations of F1, F2, and F3 lengths.
    Prints the top results sorted by Win Rate, showing the layer-by-layer breakdown.
    """
    print(f"Generating patterns (F1 len: {f1_len}, F2 len: {f2_len}, F3 len: {f3_len})...")
    
    f1_patterns = generate_patterns(f1_len)
    f2_patterns = generate_patterns(f2_len)
    f3_patterns = generate_patterns(f3_len)
    
    results = []
    total_combos = len(f1_patterns) * len(f2_patterns) * len(f3_patterns)
    print(f"Testing {total_combos} combinations against {len(market_string)} market slices...\n")
    
    for f1 in f1_patterns:
        for f2 in f2_patterns:
            for f3 in f3_patterns:
                # Get the full stats dictionary from the simulation
                stats = simulate_pipeline(market_string, f1, f2, f3)
                
                # Only save combinations that meet our minimum trade threshold
                if stats["trade_len"] >= min_trades:
                    results.append({
                        "combo": f"F1: '{f1}' | F2: '{f2}' | F3: '{f3}'",
                        "stats": stats
                    })
                    
    # Sort primarily by Final Trade Win Rate, secondarily by Total Trades
    results.sort(key=lambda x: (x["stats"]["trade_pct"], x["stats"]["trade_len"]), reverse=True)
    
    print("=" * 80)
    print(f"{'TOP FILTER COMBINATIONS (Showing Distillation Progress)':^80}")
    print("=" * 80)
    
    if not results:
        print(f"No combinations produced at least {min_trades} trades.")
        return
        
    for i, res in enumerate(results[:10]):
        st = res["stats"]
        print(f"{i+1:2}. {res['combo']}")
        print(f"    Raw String : {st['raw_len']:4} slices  |  Win Rate: {st['raw_pct']:6.2f}%")
        print(f"    F1 Outcome : {st['f1_len']:4} slices  |  Win Rate: {st['f1_pct']:6.2f}%")
        print(f"    F2 Outcome : {st['f2_len']:4} slices  |  Win Rate: {st['f2_pct']:6.2f}%")
        print(f"    Live Trades: {st['trade_len']:4} trades  |  Win Rate: {st['trade_pct']:6.2f}%  ({st['trade_wins']}W - {st['trade_len'] - st['trade_wins']}L)")
        print("-" * 80)


if __name__ == "__main__":
    # 1. Paste your historical raw string here (No trailing comma)
    test_market_string = (
        "001100101101010010001100000011100101110110000001000001101011111111010111010111110110010000111111110111001101001000100011010101101111001011011000101000000011001010000100011111011010110100011100100100001101111101011011000110100011000010111100011000110111001001101010111001011011110001011110110111101001111111000111101000100100101110011001101001110111000100001100000100010101110011101111110011011101110001011101110101000111010110011101110110000101000010000110100000010011111100000000101110011010111100111110010111010000110001011011010110011001101110100011111011100100011110100101010101000100111000010111110001011100101000011110010011001101101111000"
        "101000110110101010110111000100110110010001101010011011101011110100101101110110000111011010001111110110010111100010010110110101011011110110100000001010100101111"
    )
    
    # 2. Set the lengths of the patterns you want to test
    LENGTH_F1 = 2
    LENGTH_F2 = 1
    LENGTH_F3 = 1
    
    # 3. Minimum trades required to be considered a valid combo
    MIN_TRADES = 3
    
    run_optimization(test_market_string, LENGTH_F1, LENGTH_F2, LENGTH_F3, min_trades=MIN_TRADES)
