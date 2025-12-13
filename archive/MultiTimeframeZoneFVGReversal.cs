#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Multi-timeframe Accumulation Zone + Fair Value Gap reversal strategy
    /// tuned for Nasdaq futures. Uses 15m structure, 5m FVG detection, and
    /// 1m entries with risk-based sizing, fixed SL/TP, breakeven, near-miss
    /// cancellation, cooldown, and trade flipping.
    /// </summary>
    public class MultiTimeframeZoneFVGReversal : Strategy
    {
        private class AccumulationZone
        {
            public int Id;
            public double High;
            public double Low;
            public bool Bullish;
            public DateTime Created;
            public DateTime Expiry;
            public DateTime? LastTrade;
        }

        private readonly List<AccumulationZone> zones = new List<AccumulationZone>();
        private int zoneCounter;
        private Order entryOrder;
        private string stagedSignal;
        private bool stagedLong;
        private int stagedZoneId = -1;
        private double stagedEntryPrice;
        private bool breakevenArmed;
        private bool nearMissArmed;
        private DateTime entryTime;
        private int activeTradeZoneId = -1;
        private string activeEntrySignal;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Multi-Timeframe Zone & FVG Reversal";
                Description = "15m accumulation zones + 5m FVGs with 1m execution for NQ.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsOverlay = true;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 50;
                OrderFillResolution = OrderFillResolution.Standard;
                SetOrderQuantity = SetOrderQuantity.Dynamic;

                AccountSize = 50000;
                RiskPerTradePercent = 1;
                StopLossPoints = 10;
                TakeProfitPoints = 60;
                BreakevenTriggerPoints = 30;
                MissBufferPoints = 2;
                MissedMoveCancelPoints = 30;
                ZoneCooldownMinutes = 10;
                FlipMinutes = 10;
                AverageRangePeriod = 20;
                BreakoutRangeMultiplier = 1.5;
                TightRangeMultiplier = 1.2;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);   // index 1
                AddDataSeries(BarsPeriodType.Minute, 15);  // index 2
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)
                zones.RemoveAll(z => Time[0] >= z.Expiry);

            switch (BarsInProgress)
            {
                case 2:
                    DetectAccumulationZones();
                    return;
                case 1:
                    DetectFvgSignals();
                    return;
                case 0:
                    ManageEntryLifecycle();
                    ManageActivePosition();
                    return;
                default:
                    return;
            }
        }

        private void DetectAccumulationZones()
        {
            if (CurrentBars[2] < AverageRangePeriod + 3)
                return;

            double avgRange = 0;
            for (int i = 1; i <= AverageRangePeriod; i++)
                avgRange += Highs[2][i] - Lows[2][i];
            avgRange /= AverageRangePeriod;

            double breakoutRange = Highs[2][0] - Lows[2][0];
            if (breakoutRange < BreakoutRangeMultiplier * avgRange)
                return;

            bool isTight = true;
            for (int i = 1; i <= 3; i++)
            {
                if (Highs[2][i] - Lows[2][i] >= TightRangeMultiplier * avgRange)
                {
                    isTight = false;
                    break;
                }
            }

            if (!isTight)
                return;

            double zoneHigh = Math.Max(Highs[2][1], Math.Max(Highs[2][2], Highs[2][3]));
            double zoneLow = Math.Min(Lows[2][1], Math.Min(Lows[2][2], Lows[2][3]));
            bool bullish = Closes[2][0] >= Opens[2][0];

            zones.Add(new AccumulationZone
            {
                Id = ++zoneCounter,
                High = zoneHigh,
                Low = zoneLow,
                Bullish = bullish,
                Created = Times[2][0],
                Expiry = Times[2][0].AddHours(1)
            });
        }

        private void DetectFvgSignals()
        {
            if (CurrentBars[1] < 3)
                return;

            AccumulationZone zone = GetActiveZone(Times[1][0]);
            if (zone == null)
                return;

            if (zone.LastTrade.HasValue && Times[1][0] - zone.LastTrade.Value < TimeSpan.FromMinutes(ZoneCooldownMinutes))
                return;

            bool bullishFvg = Lows[1][0] > Highs[1][2];
            bool bearishFvg = Highs[1][0] < Lows[1][2];

            if (bullishFvg && zone.Bullish)
            {
                double entryPrice = Highs[1][2];
                if (entryPrice >= zone.Low && entryPrice <= zone.High && Lows[1][0] >= zone.Low)
                    StageEntry(true, entryPrice, zone);
            }
            else if (bearishFvg && !zone.Bullish)
            {
                double entryPrice = Lows[1][2];
                if (entryPrice >= zone.Low && entryPrice <= zone.High && Highs[1][0] <= zone.High)
                    StageEntry(false, entryPrice, zone);
            }
        }

        private void StageEntry(bool isLong, double entryPrice, AccumulationZone zone)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (isLong && Position.MarketPosition == MarketPosition.Short && Times[1][0] - entryTime >= TimeSpan.FromMinutes(FlipMinutes))
                    ExitShort(0, "FlipExit", activeTradeZoneId >= 0 ? "ShortFVG" : string.Empty);
                else if (!isLong && Position.MarketPosition == MarketPosition.Long && Times[1][0] - entryTime >= TimeSpan.FromMinutes(FlipMinutes))
                    ExitLong(0, "FlipExit", activeTradeZoneId >= 0 ? "LongFVG" : string.Empty);
                else
                    return;
            }

            if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
                CancelOrder(entryOrder);

            stagedLong = isLong;
            stagedEntryPrice = entryPrice;
            stagedZoneId = zone.Id;
            stagedSignal = isLong ? "LongFVG" : "ShortFVG";
            nearMissArmed = false;
        }

        private void ManageEntryLifecycle()
        {
            if (stagedZoneId >= 0)
            {
                AccumulationZone zone = zones.Find(z => z.Id == stagedZoneId);
                if (zone == null || Time[0] >= zone.Expiry)
                {
                    if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
                        CancelOrder(entryOrder);
                    ResetStagedOrder();
                    return;
                }

                if (entryOrder == null && Position.MarketPosition == MarketPosition.Flat)
                {
                    int qty = CalculatePositionSize();
                    double stopPrice = stagedLong
                        ? stagedEntryPrice - StopLossPoints
                        : stagedEntryPrice + StopLossPoints;
                    double targetPrice = stagedLong
                        ? stagedEntryPrice + TakeProfitPoints
                        : stagedEntryPrice - TakeProfitPoints;

                    SetStopLoss(stagedSignal, CalculationMode.Price, stopPrice, false);
                    SetProfitTarget(stagedSignal, CalculationMode.Price, targetPrice);

                    if (stagedLong)
                        EnterLongLimit(qty, stagedEntryPrice, stagedSignal);
                    else
                        EnterShortLimit(qty, stagedEntryPrice, stagedSignal);
                }
            }

            if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
            {
                if (stagedLong)
                {
                    if (Low[0] > stagedEntryPrice && Low[0] <= stagedEntryPrice + MissBufferPoints)
                        nearMissArmed = true;
                    if (nearMissArmed && Close[0] >= stagedEntryPrice + MissedMoveCancelPoints)
                        CancelOrder(entryOrder);
                }
                else
                {
                    if (High[0] < stagedEntryPrice && High[0] >= stagedEntryPrice - MissBufferPoints)
                        nearMissArmed = true;
                    if (nearMissArmed && Close[0] <= stagedEntryPrice - MissedMoveCancelPoints)
                        CancelOrder(entryOrder);
                }
            }
        }

        private void ManageActivePosition()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                breakevenArmed = false;
                return;
            }

            if (Position.MarketPosition == MarketPosition.Long && !breakevenArmed && Close[0] >= Position.AveragePrice + BreakevenTriggerPoints)
            {
                breakevenArmed = true;
                SetStopLoss(activeEntrySignal ?? "LongFVG", CalculationMode.Price, Position.AveragePrice, false);
            }
            else if (Position.MarketPosition == MarketPosition.Short && !breakevenArmed && Close[0] <= Position.AveragePrice - BreakevenTriggerPoints)
            {
                breakevenArmed = true;
                SetStopLoss(activeEntrySignal ?? "ShortFVG", CalculationMode.Price, Position.AveragePrice, false);
            }
        }

        private AccumulationZone GetActiveZone(DateTime barTime)
        {
            for (int i = zones.Count - 1; i >= 0; i--)
            {
                if (barTime >= zones[i].Created && barTime <= zones[i].Expiry)
                    return zones[i];
            }

            return null;
        }

        private int CalculatePositionSize()
        {
            double dollarRisk = AccountSize * (RiskPerTradePercent / 100.0);
            double riskPerContract = StopLossPoints * Instrument.MasterInstrument.PointValue;
            int qty = (int)Math.Floor(dollarRisk / Math.Max(riskPerContract, TickSize));
            return Math.Max(1, qty);
        }

        private void ResetStagedOrder()
        {
            stagedSignal = null;
            stagedEntryPrice = 0;
            stagedZoneId = -1;
            stagedLong = false;
            nearMissArmed = false;
            entryOrder = null;
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name == stagedSignal)
                entryOrder = order;

            if (entryOrder != null && order == entryOrder && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
                ResetStagedOrder();
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (entryOrder == null || execution.Order != entryOrder)
                return;

            if (execution.Order.OrderState == OrderState.Filled || execution.Order.OrderState == OrderState.PartFilled)
            {
                activeTradeZoneId = stagedZoneId;
                AccumulationZone zone = zones.Find(z => z.Id == stagedZoneId);
                if (zone != null)
                    zone.LastTrade = time;

                entryTime = time;
                activeEntrySignal = execution.Order.Name;
                breakevenArmed = false;
                stagedZoneId = -1;
                stagedEntryPrice = 0;
                nearMissArmed = false;
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Account Size", Order = 0, GroupName = "Money Management")]
        public double AccountSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk % Per Trade", Order = 1, GroupName = "Money Management")]
        public double RiskPerTradePercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Loss Points", Order = 2, GroupName = "Trade Management")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Take Profit Points", Order = 3, GroupName = "Trade Management")]
        public double TakeProfitPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Breakeven Trigger Points", Order = 4, GroupName = "Trade Management")]
        public double BreakevenTriggerPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Miss Buffer Points", Order = 5, GroupName = "Filters")]
        public double MissBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Missed Move Cancel Points", Order = 6, GroupName = "Filters")]
        public double MissedMoveCancelPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Zone Cooldown (min)", Order = 7, GroupName = "Filters")]
        public int ZoneCooldownMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flip Min Open (min)", Order = 8, GroupName = "Filters")]
        public int FlipMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Avg Range Period", Order = 9, GroupName = "Structure Detection")]
        public int AverageRangePeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Breakout Range Multiplier", Order = 10, GroupName = "Structure Detection")]
        public double BreakoutRangeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tight Range Multiplier", Order = 11, GroupName = "Structure Detection")]
        public double TightRangeMultiplier { get; set; }
        #endregion
    }
}
