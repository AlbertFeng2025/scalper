User Guide: LongScalper (Fixed Limit Entry)
This guide explains how the LongScalper automated strategy operates on NinjaTrader 8. This strategy is designed to enter "Long" positions by placing a limit order at a strategic distance from the market price, managed by real-time volatility.
LongScalper_FixedLimitEntry is a long-only scalping strategy that places a limit buy order and manages the full trade with profit target, stop loss, and trailing stop.
Main Purpose
•	Enter long at a favorable price during pullback recoveries.
•	Automatically attach profit target and stop loss brackets upon fill.
•	Trail the stop loss to lock in profits.
•	Provide strong defensive protection against orphaned orders.

________________________________________
🚀 How the Strategy Works
The LongScalper doesn't just "buy at the market." It waits for a specific price point based on how the market is currently moving.
1.	Volatility Tracking: The bot constantly calculates the Average Bar Size (the height of recent candles). This tells the bot if the market is "fast" or "slow."
2.	The "Dip" Entry: When the strategy starts, it places a Limit Order slightly below the current price. It is essentially betting that the price will "dip" for a split second to fill your order before moving higher.
3.	Active Management: Once you are in a trade, the bot immediately places a Profit Target and a Trailing Stop Loss to protect your capital.
________________________________________
⚙️ Parameter Definitions
1. Entry Settings (Getting In)
These parameters determine where and when your buy order is placed.
•	Limit Mode:
o	OffsetFromLastPrice: The bot follows the current price and places an order $X$ amount below it.
o	FixedPrice: You specify one exact price (e.g., 18500.25) and the bot stays there.
•	Entry Offset Multiplier:
o	Usage: Only used in OffsetFromLastPrice mode.
o	Logic: It takes the Average Bar Size and multiplies it by this number.
o	Example: If the average candle is 10 points and your multiplier is 0.2, the bot will place your buy order 2 points below the current price.
•	Order Life (Seconds):
o	How long the bot waits for the "dip" to hit your price. If the market moves away and you aren't filled within this time, the order is cancelled and replaced at a new, better price.
2. Exit Settings (Getting Out)
These parameters manage your money once the trade is active.
•	Profit Target Points:
o	The fixed amount of profit you want to take. If set to 15, the bot will exit as soon as the trade is up 15 points.
•	Stop Loss Mode:
o	OffsetFromFilledPrice: Highly recommended. It sets your stop loss based on recent volatility.
o	FixedPoints: A static stop (e.g., always 20 points).
•	Stop Offset Multiplier:
o	How much "breathing room" the trade has. A value of 2.0 means your stop loss is twice the size of an average candle.
•	Trail Offset Multiplier:
o	Controls the Trailing Stop. As the price moves up, the stop loss "walks" up behind it. A smaller number (like 0.5) follows the price very tightly; a larger number (like 1.5) gives the trade more room to swing.
3. Strategy Logic (The "Brain")
•	Bar Size Average Period:
o	How many previous candles the bot looks at to determine "average volatility." Default is 10. Increase this for a steadier, slower-reacting bot.
•	Monitor Interval (Seconds):
o	How often the bot "checks" the market to move your trailing stop. Usually set to 2 or 3 seconds.
________________________________________
🛡️ Safety & Account Protection
This strategy includes a Full-Sync Safety Module. This is crucial for non-programmers to understand:
1.	Ghost Order Prevention: Every few seconds, the bot compares your NinjaTrader Account to its Internal Logic.
2.	Auto-Cleanup: If you manually close a position on your phone or in another chart, the bot will detect that you are "Flat." It will immediately cancel any "orphaned" Limit Orders or Stop Losses that were left behind.
3.	Fail-Safe: If the bot detects a mismatch it cannot resolve, it will sound an alert and shut itself down to prevent "accidental trading."
________________________________________
💡 Pro-Tips for Tuning
•	Volatile Markets (Nasdaq/MNQ): Use a larger Stop Offset Multiplier (e.g., 2.5) to avoid being stopped out by "noise."
•	Quiet Markets (S&P 500/MES): Use a smaller Entry Offset Multiplier (e.g., 0.05) because the price doesn't dip as far, and you want to make sure you get filled.
•	Scalping: If you want quick "in-and-out" trades, set your Profit Target low and your Trail Offset very tight (0.4).
________________________________________
How to Start
1.	Right-click your NinjaTrader Chart.
2.	Select Strategies.
3.	Choose LongScalper_FixedLimitEntry.
4.	Adjust the parameters in the right-hand panel using the guide above.
5.	Check the Enabled box and click OK.

