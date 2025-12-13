# Multi-Timeframe Zone & FVG Reversal Strategy

## Overview

This NinjaScript strategy implements a multi-timeframe trading approach that combines:

1. **Market Structure Analysis** (15-Minute Timeframe)
2. **Entry Signal Detection** (5-Minute Timeframe)
3. **Order Execution** (1-Minute Timeframe)
4. **Advanced Trade Management and Filters**

## Strategy Logic

### 1. Market Structure Analysis (15-Minute Timeframe)

The strategy identifies "Accumulation Zones" to establish a directional bias.

- **Trigger**: Volatile Breakout Candle (Range > 1.5x Average Range)
- **Confirmation**: The 3 candles immediately preceding the breakout must be "tight" (Range < 1.2x Average Range)
- **Zone Validity**: Each zone is valid for 1 hour from creation

### 2. Entry Signal (5-Minute Timeframe)

The strategy detects Fair Value Gaps (FVG) that form inside identified Accumulation Zones.

- **Bullish FVG**: A 3-candle sequence where Candle 3's Low is above Candle 1's High (gap up)
- **Bearish FVG**: A 3-candle sequence where Candle 3's High is below Candle 1's Low (gap down)

### 3. Order Execution (1-Minute Timeframe)

- **Order Type**: Limit Order
- **Long Entry**: Buys at the Top of the Gap (Pattern High)
- **Short Entry**: Sells at the Bottom of the Gap (Pattern Low)
- **Position Sizing**: Quantity calculated based on risk percentage relative to Stop Loss distance

### 4. Trade Management

- **Stop Loss (SL)**: Fixed at 10 points from entry
- **Take Profit (TP)**: Fixed at 60 points from entry
- **Breakeven Logic**: If price moves 30 points in profit, Stop Loss moves to Entry Price

### 5. Advanced Filters

- **Near Miss / Invalidation**: If price comes within 2 points of entry but doesn't fill, and then moves 30 points away, the order is cancelled
- **Zone Cooldown**: No second trade from the same zone within 10 minutes
- **Trade Flipping**: If an opposing signal occurs while in a trade (open > 10 mins), force-close and flip into the new direction

## Installation

1. Copy `MultiTimeframeZoneFVGReversal.cs` to your NinjaTrader 8 strategies folder:
   - Default location: `Documents\NinjaTrader 8\bin\Custom\Strategies\`

2. Open NinjaTrader 8 and compile the strategy:
   - Go to **Control Center > New > NinjaScript Editor**
   - Press **F5** to compile

3. Apply the strategy to a chart:
   - Right-click on the chart
   - Select **Strategies > MultiTimeframeZoneFVGReversal**

## Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| Risk Percentage | 1.0% | Account risk per trade |
| Stop Loss Points | 10 | Distance from entry for stop loss |
| Take Profit Points | 60 | Distance from entry for take profit |
| Breakeven Trigger Points | 30 | Points in profit to move SL to breakeven |
| Volatile Breakout Multiplier | 1.5 | Multiplier for identifying volatile candles |
| Tight Range Multiplier | 1.2 | Multiplier for identifying tight candles |
| Zone Validity Minutes | 60 | How long an accumulation zone remains valid |
| Zone Cooldown Minutes | 10 | Minimum time between trades from same zone |
| Near Miss Threshold Points | 2 | Distance to trigger near-miss detection |
| Near Miss Cancel Distance Points | 30 | Distance price must move to cancel near-miss order |
| Trade Flip Minimum Minutes | 10 | Minimum trade duration before flip is allowed |
| Average Range Period | 14 | Lookback period for average range calculation |

## Requirements

- NinjaTrader 8
- 1-minute, 5-minute, and 15-minute data for the instrument

## Disclaimer

This strategy is provided for educational purposes only. Trading involves significant risk of loss. Always test thoroughly in simulation before using with real capital.
