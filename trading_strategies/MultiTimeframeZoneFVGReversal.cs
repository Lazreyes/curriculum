#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Multi-Timeframe Zone & FVG Reversal Strategy
    /// 
    /// This strategy implements:
    /// 1. Market Structure Analysis (15-Min) - Accumulation Zone detection
    /// 2. Entry Signal (5-Min) - Fair Value Gap (FVG) detection inside zones
    /// 3. Order Execution (1-Min) - Limit orders with risk-based position sizing
    /// 4. Trade Management - SL/TP and breakeven logic
    /// 5. Advanced Filters - Near miss, zone cooldown, trade flipping
    /// </summary>
    public class MultiTimeframeZoneFVGReversal : Strategy
    {
        #region Private Variables
        
        // Data series for multi-timeframe analysis
        private int _5MinBarsIndex;
        private int _15MinBarsIndex;
        
        // Accumulation Zone tracking
        private class AccumulationZone
        {
            public double HighPrice { get; set; }
            public double LowPrice { get; set; }
            public DateTime CreationTime { get; set; }
            public DateTime ExpirationTime { get; set; }
            public bool IsBullish { get; set; }
            public DateTime LastTradeTime { get; set; }
            
            public bool IsValid(DateTime currentTime)
            {
                return currentTime <= ExpirationTime;
            }
            
            public bool IsInCooldown(DateTime currentTime, int cooldownMinutes)
            {
                if (LastTradeTime == DateTime.MinValue)
                    return false;
                return (currentTime - LastTradeTime).TotalMinutes < cooldownMinutes;
            }
        }
        
        private List<AccumulationZone> _accumulationZones = new List<AccumulationZone>();
        
        // FVG tracking
        private class FairValueGap
        {
            public double PatternHigh { get; set; }
            public double PatternLow { get; set; }
            public bool IsBullish { get; set; }
            public DateTime CreationTime { get; set; }
            public AccumulationZone ParentZone { get; set; }
        }
        
        private FairValueGap _pendingFVG = null;
        
        // Order and position tracking
        private Order _entryOrder = null;
        private double _entryPrice = 0;
        private DateTime _entryTime = DateTime.MinValue;
        private bool _breakevenApplied = false;
        
        // Near miss tracking
        private double _pendingOrderPrice = 0;
        private double _nearMissStartPrice = 0;
        private bool _nearMissTriggered = false;
        
        #endregion
        
        #region Strategy Parameters
        
        [NinjaScriptProperty]
        [Range(0.1, 100)]
        [Display(Name = "Risk Percentage", Order = 1, GroupName = "Position Sizing")]
        public double RiskPercentage { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Loss Points", Order = 2, GroupName = "Trade Management")]
        public double StopLossPoints { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Take Profit Points", Order = 3, GroupName = "Trade Management")]
        public double TakeProfitPoints { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Breakeven Trigger Points", Order = 4, GroupName = "Trade Management")]
        public double BreakevenTriggerPoints { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.1, 10)]
        [Display(Name = "Volatile Breakout Multiplier", Order = 5, GroupName = "Zone Detection")]
        public double VolatileBreakoutMultiplier { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.1, 5)]
        [Display(Name = "Tight Range Multiplier", Order = 6, GroupName = "Zone Detection")]
        public double TightRangeMultiplier { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "Zone Validity Minutes", Order = 7, GroupName = "Zone Detection")]
        public int ZoneValidityMinutes { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Zone Cooldown Minutes", Order = 8, GroupName = "Advanced Filters")]
        public int ZoneCooldownMinutes { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.1, 50)]
        [Display(Name = "Near Miss Threshold Points", Order = 9, GroupName = "Advanced Filters")]
        public double NearMissThresholdPoints { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Near Miss Cancel Distance Points", Order = 10, GroupName = "Advanced Filters")]
        public double NearMissCancelDistancePoints { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Trade Flip Minimum Minutes", Order = 11, GroupName = "Advanced Filters")]
        public int TradeFlipMinimumMinutes { get; set; }
        
        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Average Range Period", Order = 12, GroupName = "Zone Detection")]
        public int AverageRangePeriod { get; set; }
        
        #endregion
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Multi-Timeframe Zone & FVG Reversal Strategy";
                Name = "MultiTimeframeZoneFVGReversal";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;
                
                // Default parameter values
                RiskPercentage = 1.0;
                StopLossPoints = 10;
                TakeProfitPoints = 60;
                BreakevenTriggerPoints = 30;
                VolatileBreakoutMultiplier = 1.5;
                TightRangeMultiplier = 1.2;
                ZoneValidityMinutes = 60;
                ZoneCooldownMinutes = 10;
                NearMissThresholdPoints = 2;
                NearMissCancelDistancePoints = 30;
                TradeFlipMinimumMinutes = 10;
                AverageRangePeriod = 14;
            }
            else if (State == State.Configure)
            {
                // Add 5-minute and 15-minute data series for multi-timeframe analysis
                AddDataSeries(Data.BarsPeriodType.Minute, 5);
                AddDataSeries(Data.BarsPeriodType.Minute, 15);
            }
            else if (State == State.DataLoaded)
            {
                _5MinBarsIndex = 1;
                _15MinBarsIndex = 2;
            }
        }
        
        protected override void OnBarUpdate()
        {
            // Ensure we have enough bars on all timeframes
            if (CurrentBars[0] < AverageRangePeriod || 
                CurrentBars[_5MinBarsIndex] < AverageRangePeriod || 
                CurrentBars[_15MinBarsIndex] < AverageRangePeriod)
                return;
            
            // Process based on which data series triggered the update
            if (BarsInProgress == _15MinBarsIndex)
            {
                // 15-Minute: Market Structure Analysis - Accumulation Zone Detection
                DetectAccumulationZones();
            }
            else if (BarsInProgress == _5MinBarsIndex)
            {
                // 5-Minute: Entry Signal - FVG Detection inside Accumulation Zones
                DetectFairValueGaps();
            }
            else if (BarsInProgress == 0)
            {
                // 1-Minute: Order Execution and Trade Management
                ManageActivePositions();
                CheckForNearMiss();
                CheckForTradeFlip();
                ExecutePendingFVGSignals();
            }
        }
        
        #region Market Structure Analysis (15-Minute)
        
        /// <summary>
        /// Detects Accumulation Zones on the 15-minute timeframe.
        /// Trigger: Volatile Breakout Candle (Range > 1.5x Average Range)
        /// Confirmation: 3 preceding candles must be "tight" (Range < 1.2x Average Range)
        /// </summary>
        private void DetectAccumulationZones()
        {
            // Calculate average range for 15-minute bars
            double averageRange = CalculateAverageRange(_15MinBarsIndex);
            
            // Get current bar range
            double currentRange = Highs[_15MinBarsIndex][0] - Lows[_15MinBarsIndex][0];
            
            // Check for Volatile Breakout Candle
            if (currentRange > VolatileBreakoutMultiplier * averageRange)
            {
                // Check if preceding 3 candles are "tight"
                bool precedingCandlesTight = true;
                double tightThreshold = TightRangeMultiplier * averageRange;
                
                for (int i = 1; i <= 3; i++)
                {
                    if (CurrentBars[_15MinBarsIndex] < i)
                    {
                        precedingCandlesTight = false;
                        break;
                    }
                    
                    double range = Highs[_15MinBarsIndex][i] - Lows[_15MinBarsIndex][i];
                    if (range >= tightThreshold)
                    {
                        precedingCandlesTight = false;
                        break;
                    }
                }
                
                if (precedingCandlesTight)
                {
                    // Determine zone boundaries from the 3 tight candles
                    double zoneHigh = Math.Max(Math.Max(Highs[_15MinBarsIndex][1], Highs[_15MinBarsIndex][2]), Highs[_15MinBarsIndex][3]);
                    double zoneLow = Math.Min(Math.Min(Lows[_15MinBarsIndex][1], Lows[_15MinBarsIndex][2]), Lows[_15MinBarsIndex][3]);
                    
                    // Determine directional bias based on breakout direction
                    bool isBullish = Closes[_15MinBarsIndex][0] > Opens[_15MinBarsIndex][0];
                    
                    // Create new Accumulation Zone valid for 1 hour
                    AccumulationZone newZone = new AccumulationZone
                    {
                        HighPrice = zoneHigh,
                        LowPrice = zoneLow,
                        CreationTime = Times[_15MinBarsIndex][0],
                        ExpirationTime = Times[_15MinBarsIndex][0].AddMinutes(ZoneValidityMinutes),
                        IsBullish = isBullish,
                        LastTradeTime = DateTime.MinValue
                    };
                    
                    _accumulationZones.Add(newZone);
                    
                    Print($"[{Times[_15MinBarsIndex][0]}] New Accumulation Zone detected: " +
                          $"High={zoneHigh:F2}, Low={zoneLow:F2}, Bullish={isBullish}");
                }
            }
            
            // Clean up expired zones
            CleanupExpiredZones();
        }
        
        private double CalculateAverageRange(int barsIndex)
        {
            double sum = 0;
            for (int i = 1; i <= AverageRangePeriod; i++)
            {
                if (CurrentBars[barsIndex] >= i)
                    sum += Highs[barsIndex][i] - Lows[barsIndex][i];
            }
            return sum / AverageRangePeriod;
        }
        
        private void CleanupExpiredZones()
        {
            _accumulationZones.RemoveAll(z => !z.IsValid(Times[_15MinBarsIndex][0]));
        }
        
        #endregion
        
        #region Entry Signal Detection (5-Minute)
        
        /// <summary>
        /// Detects Fair Value Gaps (FVG) on the 5-minute timeframe.
        /// Bullish FVG: Candle 3's Low > Candle 1's High (gap up)
        /// Bearish FVG: Candle 3's High < Candle 1's Low (gap down)
        /// </summary>
        private void DetectFairValueGaps()
        {
            if (CurrentBars[_5MinBarsIndex] < 3)
                return;
            
            // Get the current price to check if we're inside an accumulation zone
            double currentPrice = Closes[_5MinBarsIndex][0];
            
            // Find valid accumulation zone containing current price
            AccumulationZone activeZone = FindActiveZone(currentPrice);
            
            if (activeZone == null)
                return; // No valid zone, skip FVG detection
            
            // Check zone cooldown
            if (activeZone.IsInCooldown(Times[_5MinBarsIndex][0], ZoneCooldownMinutes))
                return;
            
            // FVG Detection using 3-candle pattern
            // Candle indices: [2] = oldest, [1] = middle, [0] = newest
            double candle1High = Highs[_5MinBarsIndex][2];
            double candle1Low = Lows[_5MinBarsIndex][2];
            double candle3High = Highs[_5MinBarsIndex][0];
            double candle3Low = Lows[_5MinBarsIndex][0];
            
            // Bullish FVG: Gap up - Candle 3's Low above Candle 1's High
            if (candle3Low > candle1High && activeZone.IsBullish)
            {
                _pendingFVG = new FairValueGap
                {
                    PatternHigh = candle3Low,    // Top of the gap
                    PatternLow = candle1High,    // Bottom of the gap
                    IsBullish = true,
                    CreationTime = Times[_5MinBarsIndex][0],
                    ParentZone = activeZone
                };
                
                Print($"[{Times[_5MinBarsIndex][0]}] Bullish FVG detected: " +
                      $"Entry at Top of Gap={_pendingFVG.PatternHigh:F2}");
            }
            // Bearish FVG: Gap down - Candle 3's High below Candle 1's Low
            else if (candle3High < candle1Low && !activeZone.IsBullish)
            {
                _pendingFVG = new FairValueGap
                {
                    PatternHigh = candle1Low,    // Top of the gap
                    PatternLow = candle3High,    // Bottom of the gap
                    IsBullish = false,
                    CreationTime = Times[_5MinBarsIndex][0],
                    ParentZone = activeZone
                };
                
                Print($"[{Times[_5MinBarsIndex][0]}] Bearish FVG detected: " +
                      $"Entry at Bottom of Gap={_pendingFVG.PatternLow:F2}");
            }
        }
        
        private AccumulationZone FindActiveZone(double price)
        {
            DateTime currentTime = Times[_5MinBarsIndex][0];
            
            foreach (var zone in _accumulationZones)
            {
                if (zone.IsValid(currentTime) && 
                    price >= zone.LowPrice && 
                    price <= zone.HighPrice)
                {
                    return zone;
                }
            }
            
            return null;
        }
        
        #endregion
        
        #region Order Execution (1-Minute)
        
        /// <summary>
        /// Executes pending FVG signals using limit orders on the 1-minute timeframe.
        /// Long Entry: Limit at Top of Gap (Pattern High)
        /// Short Entry: Limit at Bottom of Gap (Pattern Low)
        /// </summary>
        private void ExecutePendingFVGSignals()
        {
            if (_pendingFVG == null || Position.MarketPosition != MarketPosition.Flat)
                return;
            
            // Cancel any existing pending orders
            if (_entryOrder != null)
            {
                CancelOrder(_entryOrder);
                _entryOrder = null;
            }
            
            // Calculate position size based on risk
            int quantity = CalculatePositionSize();
            
            if (quantity <= 0)
            {
                Print("Position size calculation resulted in 0 or negative quantity. Skipping trade.");
                return;
            }
            
            if (_pendingFVG.IsBullish)
            {
                // Long Entry: Buy limit at top of gap
                double entryPrice = _pendingFVG.PatternHigh;
                _pendingOrderPrice = entryPrice;
                _entryOrder = EnterLongLimit(0, true, quantity, entryPrice, "Long FVG Entry");
                
                Print($"[{Time[0]}] Submitting Long Limit Order: Price={entryPrice:F2}, Qty={quantity}");
            }
            else
            {
                // Short Entry: Sell limit at bottom of gap
                double entryPrice = _pendingFVG.PatternLow;
                _pendingOrderPrice = entryPrice;
                _entryOrder = EnterShortLimit(0, true, quantity, entryPrice, "Short FVG Entry");
                
                Print($"[{Time[0]}] Submitting Short Limit Order: Price={entryPrice:F2}, Qty={quantity}");
            }
            
            // Mark zone as used
            _pendingFVG.ParentZone.LastTradeTime = Time[0];
            
            // Reset near miss tracking
            _nearMissTriggered = false;
            _nearMissStartPrice = 0;
        }
        
        /// <summary>
        /// Calculates position size based on risk percentage and stop loss distance.
        /// </summary>
        private int CalculatePositionSize()
        {
            double accountSize = Account.Get(AccountItem.CashValue, Currency.UsDollar);
            double riskAmount = accountSize * (RiskPercentage / 100.0);
            double stopLossValue = StopLossPoints * Instrument.MasterInstrument.PointValue;
            
            if (stopLossValue <= 0)
                return 1;
            
            int quantity = (int)Math.Floor(riskAmount / stopLossValue);
            return Math.Max(1, quantity);
        }
        
        #endregion
        
        #region Trade Management
        
        /// <summary>
        /// Manages active positions including:
        /// - Stop Loss (10 points from entry)
        /// - Take Profit (60 points from entry)
        /// - Breakeven Logic (Move SL to entry when 30 points in profit)
        /// </summary>
        private void ManageActivePositions()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                _breakevenApplied = false;
                return;
            }
            
            double currentPrice = Close[0];
            double profitPoints = 0;
            
            if (Position.MarketPosition == MarketPosition.Long)
            {
                profitPoints = (currentPrice - Position.AveragePrice) / TickSize * TickSize;
                
                // Check for breakeven trigger
                if (!_breakevenApplied && profitPoints >= BreakevenTriggerPoints)
                {
                    // Move stop loss to entry price (breakeven)
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    _breakevenApplied = true;
                    Print($"[{Time[0]}] Breakeven applied for Long position at {Position.AveragePrice:F2}");
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                profitPoints = (Position.AveragePrice - currentPrice) / TickSize * TickSize;
                
                // Check for breakeven trigger
                if (!_breakevenApplied && profitPoints >= BreakevenTriggerPoints)
                {
                    // Move stop loss to entry price (breakeven)
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    _breakevenApplied = true;
                    Print($"[{Time[0]}] Breakeven applied for Short position at {Position.AveragePrice:F2}");
                }
            }
        }
        
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, 
            int quantity, int filled, double averageFillPrice, OrderState orderState, 
            DateTime time, ErrorCode error, string comment)
        {
            if (order.Name == "Long FVG Entry" || order.Name == "Short FVG Entry")
            {
                _entryOrder = order;
                
                if (orderState == OrderState.Filled)
                {
                    _entryPrice = averageFillPrice;
                    _entryTime = time;
                    _pendingFVG = null;
                    
                    // Set initial Stop Loss and Take Profit
                    if (order.Name == "Long FVG Entry")
                    {
                        SetStopLoss(CalculationMode.Price, _entryPrice - StopLossPoints);
                        SetProfitTarget(CalculationMode.Price, _entryPrice + TakeProfitPoints);
                    }
                    else
                    {
                        SetStopLoss(CalculationMode.Price, _entryPrice + StopLossPoints);
                        SetProfitTarget(CalculationMode.Price, _entryPrice - TakeProfitPoints);
                    }
                    
                    Print($"[{time}] Order filled at {averageFillPrice:F2}. SL and TP set.");
                }
                else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    _entryOrder = null;
                    _pendingOrderPrice = 0;
                }
            }
        }
        
        #endregion
        
        #region Advanced Filters
        
        /// <summary>
        /// Near Miss / Invalidation Logic:
        /// If price comes within 2 points of entry but doesn't fill,
        /// and then moves 30 points away, cancel the order.
        /// </summary>
        private void CheckForNearMiss()
        {
            if (_entryOrder == null || _entryOrder.OrderState != OrderState.Working)
                return;
            
            double currentPrice = Close[0];
            double distanceFromEntry = Math.Abs(currentPrice - _pendingOrderPrice);
            
            // Check if price came near the entry
            if (!_nearMissTriggered && distanceFromEntry <= NearMissThresholdPoints)
            {
                _nearMissTriggered = true;
                _nearMissStartPrice = currentPrice;
                Print($"[{Time[0]}] Near miss detected at price {currentPrice:F2}");
            }
            
            // Check if price has moved away after near miss
            if (_nearMissTriggered)
            {
                double moveAwayDistance = Math.Abs(currentPrice - _nearMissStartPrice);
                
                if (moveAwayDistance >= NearMissCancelDistancePoints)
                {
                    CancelOrder(_entryOrder);
                    _entryOrder = null;
                    _pendingFVG = null;
                    _nearMissTriggered = false;
                    _nearMissStartPrice = 0;
                    
                    Print($"[{Time[0]}] Order cancelled due to near miss invalidation. " +
                          $"Price moved {moveAwayDistance:F2} points away.");
                }
            }
        }
        
        /// <summary>
        /// Trade Flipping Logic:
        /// If a valid opposing signal occurs while in a trade (and trade open > 10 mins),
        /// force-close current trade and flip into new direction.
        /// </summary>
        private void CheckForTradeFlip()
        {
            if (Position.MarketPosition == MarketPosition.Flat || _pendingFVG == null)
                return;
            
            // Check if current trade has been open long enough
            TimeSpan tradeOpenDuration = Time[0] - _entryTime;
            if (tradeOpenDuration.TotalMinutes < TradeFlipMinimumMinutes)
                return;
            
            // Check for opposing signal
            bool isOpposingSignal = false;
            
            if (Position.MarketPosition == MarketPosition.Long && !_pendingFVG.IsBullish)
            {
                isOpposingSignal = true;
            }
            else if (Position.MarketPosition == MarketPosition.Short && _pendingFVG.IsBullish)
            {
                isOpposingSignal = true;
            }
            
            if (isOpposingSignal)
            {
                Print($"[{Time[0]}] Trade flip triggered. Closing current {Position.MarketPosition} position.");
                
                // Close current position
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    ExitLong("Trade Flip Exit", "Long FVG Entry");
                }
                else
                {
                    ExitShort("Trade Flip Exit", "Short FVG Entry");
                }
                
                // The pending FVG will be executed on the next bar update
                // when position becomes flat
            }
        }
        
        #endregion
    }
}
