#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

/*
    ORB_FVG Strategy — NQ Futures
    -------------------------------------------------------
    - Marks first 5m and 15m candle range from NY open (9:30 AM ET)
    - Waits for 3 consecutive same-color 1m candles with a Fair Value Gap
      to break outside the range, closing beyond it on the 3rd candle
    - Enters 2 contracts: Contract 1 exits at 1:1 (moves remaining to BE),
      Contract 2 exits at 1:2
    - Stop loss: below/above body of the FVG candle (middle of the 3)
    - Max 1 long + 1 short per day. Cutoff: 11:00 AM ET
*/

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ORB_FVG : Strategy
    {
        // Daily range state
        private double range5mHigh, range5mLow;
        private double range15mHigh, range15mLow;
        private bool range5mSet, range15mSet;
        private bool bullishTaken, bearishTaken;

        // Active trade state
        private bool inTrade;
        private int  tradeDir;          // 1 = long, -1 = short
        private double entryPrice;
        private double slPrice;
        private double tp1Price;
        private double tp2Price;
        private bool contract1Exited;

        private const int SERIES_5M  = 1;
        private const int SERIES_15M = 2;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "ORB_FVG";
                Description                  = "Opening Range Breakout with FVG — NQ";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 3;
                TraceOrders                  = true;

                SlBufferTicks = 2;
                Contracts     = 2;
            }
            else if (State == State.Configure)
            {
                // Add 5m and 15m data series alongside the primary 1m series
                AddDataSeries(BarsPeriodType.Minute, 5);
                AddDataSeries(BarsPeriodType.Minute, 15);
            }
        }

        protected override void OnBarUpdate()
        {
            // ── Reset daily state at session open on 1m series ──
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
                ResetDay();

            // ── Capture 5m opening range ──
            if (BarsInProgress == SERIES_5M)
            {
                if (!range5mSet && Bars.IsFirstBarOfSession)
                {
                    range5mHigh = High[0];
                    range5mLow  = Low[0];
                    range5mSet  = true;

                    // Draw 5m range zone (blue)
                    Draw.HorizontalLine(this, "5mHigh", range5mHigh, Brushes.SteelBlue);
                    Draw.HorizontalLine(this, "5mLow",  range5mLow,  Brushes.SteelBlue);
                }
                return;
            }

            // ── Capture 15m opening range ──
            if (BarsInProgress == SERIES_15M)
            {
                if (!range15mSet && Bars.IsFirstBarOfSession)
                {
                    range15mHigh = High[0];
                    range15mLow  = Low[0];
                    range15mSet  = true;

                    // Draw 15m range zone (wheat/tan)
                    Draw.HorizontalLine(this, "15mHigh", range15mHigh, Brushes.Goldenrod);
                    Draw.HorizontalLine(this, "15mLow",  range15mLow,  Brushes.Goldenrod);
                }
                return;
            }

            // ── 1m bar logic ──
            if (BarsInProgress != 0) return;
            if (!range5mSet)         return;  // wait for 5m range to be set
            if (CurrentBar < 3)      return;  // need 3 bars for FVG check
            if (IsAfterCutoff())     return;  // 11:00 AM cutoff

            // Manage any open trade first
            if (inTrade)
            {
                ManageTrade();
                return; // Don't look for new entries while in a trade
            }

            if (!bullishTaken) CheckBullishSetup();
            if (!bearishTaken) CheckBearishSetup();
        }

        // ─────────────────────────────────────────────────────────
        //  ENTRY LOGIC
        // ─────────────────────────────────────────────────────────

        private void CheckBullishSetup()
        {
            // 1) Three consecutive bullish 1m candles
            if (!(Close[0] > Open[0] && Close[1] > Open[1] && Close[2] > Open[2]))
                return;

            // 2) Bullish FVG: gap between bar[2] high and bar[0] low
            //    bar[0].Low must be above bar[2].High (unfilled gap between them)
            if (Low[0] <= High[2]) return;

            // 3) Third candle must CLOSE above the 5m range high
            if (Close[0] <= range5mHigh) return;

            // 4) SL = below the body of the FVG candle (bar[1]), minus buffer
            double slLevel = Math.Min(Open[1], Close[1]) - (SlBufferTicks * TickSize);
            double risk    = Close[0] - slLevel;
            if (risk <= 0) return;

            double tp1 = Close[0] + risk;
            double tp2 = Close[0] + risk * 2.0;

            // Confluence: 5m high and 15m high align within 2 ticks
            bool confluence = range15mSet &&
                              Math.Abs(range15mHigh - range5mHigh) <= 2 * TickSize;

            EnterLong(Contracts, "Long");
            SetTradeState(1, Close[0], slLevel, tp1, tp2);
            bullishTaken = true;

            Brush arrowColor = confluence ? Brushes.Gold : Brushes.LimeGreen;
            Draw.ArrowUp(this, "B_" + CurrentBar, false, 0,
                Low[0] - 3 * TickSize, arrowColor);
        }

        private void CheckBearishSetup()
        {
            // 1) Three consecutive bearish 1m candles
            if (!(Close[0] < Open[0] && Close[1] < Open[1] && Close[2] < Open[2]))
                return;

            // 2) Bearish FVG: gap between bar[2] low and bar[0] high
            //    bar[0].High must be below bar[2].Low
            if (High[0] >= Low[2]) return;

            // 3) Third candle must CLOSE below the 5m range low
            if (Close[0] >= range5mLow) return;

            // 4) SL = above the body of the FVG candle (bar[1]), plus buffer
            double slLevel = Math.Max(Open[1], Close[1]) + (SlBufferTicks * TickSize);
            double risk    = slLevel - Close[0];
            if (risk <= 0) return;

            double tp1 = Close[0] - risk;
            double tp2 = Close[0] - risk * 2.0;

            bool confluence = range15mSet &&
                              Math.Abs(range15mLow - range5mLow) <= 2 * TickSize;

            EnterShort(Contracts, "Short");
            SetTradeState(-1, Close[0], slLevel, tp1, tp2);
            bearishTaken = true;

            Brush arrowColor = confluence ? Brushes.Gold : Brushes.Red;
            Draw.ArrowDown(this, "S_" + CurrentBar, false, 0,
                High[0] + 3 * TickSize, arrowColor);
        }

        // ─────────────────────────────────────────────────────────
        //  TRADE MANAGEMENT  (checked every 1m bar close)
        // ─────────────────────────────────────────────────────────

        private void ManageTrade()
        {
            if (tradeDir == 1) // ── Long ──
            {
                // Stop loss hit
                if (Low[0] <= slPrice)
                {
                    ExitLong(0, "SL", "Long");
                    inTrade = false;
                    return;
                }

                // TP1 — exit 1 contract, move to BE
                if (!contract1Exited && High[0] >= tp1Price)
                {
                    ExitLong(1, "TP1", "Long");
                    slPrice          = entryPrice; // break even
                    contract1Exited  = true;
                }

                // TP2 — exit remaining contract
                if (contract1Exited && High[0] >= tp2Price)
                {
                    ExitLong(1, "TP2", "Long");
                    inTrade = false;
                }
            }
            else // ── Short ──
            {
                // Stop loss hit
                if (High[0] >= slPrice)
                {
                    ExitShort(0, "SL", "Short");
                    inTrade = false;
                    return;
                }

                // TP1 — exit 1 contract, move to BE
                if (!contract1Exited && Low[0] <= tp1Price)
                {
                    ExitShort(1, "TP1", "Short");
                    slPrice         = entryPrice;
                    contract1Exited = true;
                }

                // TP2 — exit remaining contract
                if (contract1Exited && Low[0] <= tp2Price)
                {
                    ExitShort(1, "TP2", "Short");
                    inTrade = false;
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────

        private void SetTradeState(int dir, double entry, double sl, double tp1, double tp2)
        {
            tradeDir        = dir;
            entryPrice      = entry;
            slPrice         = sl;
            tp1Price        = tp1;
            tp2Price        = tp2;
            inTrade         = true;
            contract1Exited = false;
        }

        private void ResetDay()
        {
            range5mSet   = false;
            range15mSet  = false;
            bullishTaken = false;
            bearishTaken = false;
            inTrade      = false;
            tradeDir     = 0;
        }

        private bool IsAfterCutoff()
        {
            // Cutoff at 11:00 AM ET
            return ToTime(Time[0]) >= 110000;
        }

        // ─────────────────────────────────────────────────────────
        //  PARAMETERS
        // ─────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "SL Buffer (Ticks)", Order = 1, GroupName = "Strategy Parameters")]
        public int SlBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Contracts", Order = 2, GroupName = "Strategy Parameters")]
        public int Contracts { get; set; }
    }
}
