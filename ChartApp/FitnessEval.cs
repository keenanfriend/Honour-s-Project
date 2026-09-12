using System.Globalization;

namespace ChartApp
{
    public class FitnessEval
    {
        private readonly List<Candle> candles;
        private readonly DailyCloseSeries dailyCloseSeries;

        // Mutable state reset per evaluation
        private TrendLine? bestSupportLine = null;
        private TrendLine? bestResistanceLine = null;
        private double bestSupportGradient = double.MaxValue;
        private double bestResistanceGradient = double.MinValue;
        private double dataHighMax = double.MinValue;
        private double dataLowMin = double.MaxValue;

        // Minimum touches required for a valid trendline (adjust manually: 2 or 3)
        private const int MinTouchCount = 2;

        // When true, every detected pattern's realised gradient difference is
        // recorded here alongside its trade outcome, to see what the natural
        // distribution looks like -- see D-0024.
        public bool LogPatterns { get; set; } = false;
        public List<(double GradientDiff, double ReturnPerc, int Result)> PatternLog { get; } = new();

        public FitnessEval(List<Candle> candles, DailyCloseSeries dailyCloseSeries)
        {
            this.candles = candles;
            this.dailyCloseSeries = dailyCloseSeries;
        }

        public EvalResult Evaluate(Chromosome c)
        {
            return RunSlidingWindow(c);
        }

        // =====================================================================
        // SMA Lookup
        // =====================================================================

        private bool? GetTradeDirection(int candleIndex, int smaPeriod)
        {
            string date = candles[candleIndex].Date.Substring(0, 10);
            double? sma = dailyCloseSeries.Sma(date, smaPeriod);
            if (sma is null) return null;
            return candles[candleIndex].Close > sma.Value;
        }

        // =====================================================================
        // Sliding Window
        // =====================================================================

        private EvalResult RunSlidingWindow(Chromosome c)
        {
            int windowSize = c.WindowSize;
            int lineLen = c.MinLineLength;
            int windowShift = c.WindowShift;
            double minGradientDiff = c.MinGradientDiff;
            int smaPeriod = c.SmaPeriod;
            int vicinity = Chromosome.Vicinity;
            int windowStart = 0;

            int patternsFound = 0;
            double totalReturnPerc = 0;

            while (windowStart + windowSize <= candles.Count)
            {
                int windowEnd = windowStart + windowSize;

                // Determine trade direction from this chromosome's own SmaPeriod-day
                // rolling average at the window's last candle
                bool? direction = GetTradeDirection(windowEnd - 1, smaPeriod);
                if (direction is null)
                {
                    windowStart += windowShift;
                    continue;
                }

                bool isLong = direction.Value;

                // Reset state for this window
                bestSupportLine = null;
                bestResistanceLine = null;

                if (isLong)
                {
                    bestSupportGradient = double.MaxValue;
                    bestResistanceGradient = double.MinValue;
                }
                else
                {
                    bestSupportGradient = double.MinValue;
                    bestResistanceGradient = double.MaxValue;
                }

                // Compute dynamic price bounds for this window
                dataHighMax = double.MinValue;
                dataLowMin = double.MaxValue;
                for (int i = windowStart; i < windowEnd; i++)
                {
                    if (candles[i].High > dataHighMax) dataHighMax = candles[i].High;
                    if (candles[i].Low < dataLowMin) dataLowMin = candles[i].Low;
                }

                double pricePerc;
                int entryIndex;
                double gradientDiff;
                bool patternFound;

                if (isLong)
                {
                    patternFound = FindLongPattern(windowStart, lineLen, windowEnd, vicinity, minGradientDiff, out pricePerc, out entryIndex, out gradientDiff);
                }
                else
                {
                    patternFound = FindShortPattern(windowStart, lineLen, windowEnd, vicinity, minGradientDiff, out pricePerc, out entryIndex, out gradientDiff);
                }

                if (!patternFound)
                {
                    windowStart += windowShift;
                    continue;
                }

                // A pattern was found: simulate the trade it implies instead of
                // just summing the funnel width directly. Everything here stays
                // a percentage of the entry price -- no position sizing or
                // account-equity tracking, just win/loss/timeout against the
                // pattern's own detected target and stop distance.
                int patternLength = entryIndex - windowStart;
                (int result, int resolvedIndex) = isLong
                    ? EvaluateTradeLong(pricePerc, entryIndex, patternLength, Chromosome.SlRatio, Chromosome.MaxTradeLength)
                    : EvaluateTradeShort(pricePerc, entryIndex, patternLength, Chromosome.SlRatio, Chromosome.MaxTradeLength);

                double tradeReturnPerc = result switch
                {
                    1 => pricePerc,                           // target hit: captured the full detected funnel width
                    -1 => -(pricePerc * Chromosome.SlRatio),  // stop hit: lost the scaled-down risk distance
                    _ => 0.0                                  // neither hit within MaxTradeLength candles
                };

                patternsFound++;
                totalReturnPerc += tradeReturnPerc;

                if (LogPatterns)
                    PatternLog.Add((gradientDiff, tradeReturnPerc, result));

                // Advance past the resolved trade instead of a fixed shift, so an
                // overlapping window can no longer count the same trade twice.
                windowStart = resolvedIndex + 1;
            }

            // Fitness = total rate of return percentage, from simulated trades
            double fitness = totalReturnPerc;

            return new EvalResult(fitness, patternsFound, totalReturnPerc);
        }

        // =====================================================================
        // LONG Pattern Finding
        // =====================================================================

        private bool FindLongPattern(int windowStart, int lineLen, int windowEnd, int vicinity, double minGradientDiff, out double pricePerc, out int entryIndex, out double gradientDiff)
        {
            pricePerc = 0;
            entryIndex = 0;
            gradientDiff = 0;

            FindLinesLong(windowStart, lineLen, windowEnd, vicinity);

            if (bestResistanceLine is null)
                return false;

            bestResistanceLine = ExtendLineLong(bestResistanceLine);

            if (bestSupportLine is null)
                return false;

            double m = (bestSupportLine.Y2 - bestSupportLine.Y1) / (bestSupportLine.X2 - bestSupportLine.X1);
            double c = bestSupportLine.Y1 - m * bestSupportLine.X1;
            double newX1 = bestResistanceLine.X1;
            double newX2 = bestResistanceLine.X2;
            bestSupportLine = new TrendLine(newX1, m * newX1 + c, newX2, m * newX2 + c);

            double resistanceGrad = (bestResistanceLine.Y2 - bestResistanceLine.Y1) / (bestResistanceLine.X2 - bestResistanceLine.X1);
            double supportGrad = (bestSupportLine.Y2 - bestSupportLine.Y1) / (bestSupportLine.X2 - bestSupportLine.X1);

            if (resistanceGrad > supportGrad)
                return false;

            // Gradient difference filter: reject if lines diverge less than
            // required -- a near-parallel channel rather than a genuine wedge
            // (D-0026; formerly a ceiling here, see D-0004/D-0023). The
            // directional check above (resistanceGrad > supportGrad) still
            // separately guarantees the lines converge or run parallel, never
            // diverge outward -- untouched by this change.
            gradientDiff = Math.Abs(resistanceGrad - supportGrad);
            if (gradientDiff < minGradientDiff)
                return false;

            pricePerc = (bestResistanceLine.Y1 - bestSupportLine.Y1) / bestSupportLine.Y1 * 100;
            entryIndex = (int)bestResistanceLine.X2;
            return true;
        }

        private void FindLinesLong(int start, int lineLen, int end, int vicinity)
        {
            for (int i = start; i < (end - lineLen); i++)
            {
                TrendLine? hiCandidate = ProcessChartDataHi(i, lineLen, end);
                if (hiCandidate is not null)
                {
                    double m = Math.Abs((hiCandidate.Y2 - hiCandidate.Y1) / (hiCandidate.X2 - hiCandidate.X1));
                    if (m > bestResistanceGradient)
                    {
                        bestResistanceGradient = m;
                        bestResistanceLine = hiCandidate;
                    }
                }
            }

            if (bestResistanceLine is null) return;

            double loStart = bestResistanceLine.X1 - vicinity;
            double loEnd = bestResistanceLine.X1 + vicinity;
            if (loStart < start) loStart = start;
            if (loEnd > end) loEnd = end;

            for (int i = (int)loStart; i < (int)loEnd; i++)
            {
                TrendLine? loCandidate = ProcessChartDataLo(i, lineLen, end);
                if (loCandidate is not null)
                {
                    double m = Math.Abs((loCandidate.Y2 - loCandidate.Y1) / (loCandidate.X2 - loCandidate.X1));
                    if (m < bestSupportGradient)
                    {
                        bestSupportGradient = m;
                        bestSupportLine = loCandidate;
                    }
                }
            }
        }

        private TrendLine ExtendLineLong(TrendLine line)
        {
            double m = (line.Y2 - line.Y1) / (line.X2 - line.X1);
            double c = line.Y1 - m * line.X1;

            int startIndex = (int)line.X2 + 1;
            int maxExtension = (int)(line.X2 - line.X1) * 2;
            int endIndex = Math.Min(startIndex + maxExtension, candles.Count);

            for (int i = startIndex; i < endIndex; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                if (y > candle.Low && y < candle.High)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);

                if (y < candle.Low)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);
            }

            double lastIndex = (endIndex < candles.Count) ? candles[endIndex - 1].Index : candles[^1].Index;
            return new TrendLine(line.X1, line.Y1, lastIndex, m * lastIndex + c);
        }

        // =====================================================================
        // SHORT Pattern Finding
        // =====================================================================

        private bool FindShortPattern(int windowStart, int lineLen, int windowEnd, int vicinity, double minGradientDiff, out double pricePerc, out int entryIndex, out double gradientDiff)
        {
            pricePerc = 0;
            entryIndex = 0;
            gradientDiff = 0;

            FindLinesShort(windowStart, lineLen, windowEnd, vicinity);

            if (bestSupportLine is null)
                return false;

            bestSupportLine = ExtendLineShort(bestSupportLine);

            if (bestResistanceLine is null)
                return false;

            double m = (bestResistanceLine.Y2 - bestResistanceLine.Y1) / (bestResistanceLine.X2 - bestResistanceLine.X1);
            double c = bestResistanceLine.Y1 - m * bestResistanceLine.X1;
            double newX1 = bestSupportLine.X1;
            double newX2 = bestSupportLine.X2;
            bestResistanceLine = new TrendLine(newX1, m * newX1 + c, newX2, m * newX2 + c);

            double supportGrad = (bestSupportLine.Y2 - bestSupportLine.Y1) / (bestSupportLine.X2 - bestSupportLine.X1);
            double resistanceGrad = (bestResistanceLine.Y2 - bestResistanceLine.Y1) / (bestResistanceLine.X2 - bestResistanceLine.X1);

            if (supportGrad < resistanceGrad)
                return false;

            // Gradient difference filter: reject if lines diverge less than
            // required -- a near-parallel channel rather than a genuine wedge
            // (D-0026; formerly a ceiling here, see D-0004/D-0023). The
            // directional check above still separately guarantees the lines
            // converge or run parallel, never diverge outward.
            gradientDiff = Math.Abs(supportGrad - resistanceGrad);
            if (gradientDiff < minGradientDiff)
                return false;

            pricePerc = (bestResistanceLine.Y1 - bestSupportLine.Y1) / bestSupportLine.Y1 * 100;
            entryIndex = (int)bestSupportLine.X2;
            return true;
        }

        private void FindLinesShort(int start, int lineLen, int end, int vicinity)
        {
            for (int i = start; i < (end - lineLen); i++)
            {
                TrendLine? loCandidate = ProcessChartDataLoShort(i, lineLen, end);
                if (loCandidate is not null)
                {
                    double m = Math.Abs((loCandidate.Y2 - loCandidate.Y1) / (loCandidate.X2 - loCandidate.X1));
                    if (m > bestSupportGradient)
                    {
                        bestSupportGradient = m;
                        bestSupportLine = loCandidate;
                    }
                }
            }

            if (bestSupportLine is null) return;

            double hiStart = bestSupportLine.X1 - vicinity;
            double hiEnd = bestSupportLine.X1 + vicinity;
            if (hiStart < start) hiStart = start;
            if (hiEnd > end) hiEnd = end;

            for (int i = (int)hiStart; i < (int)hiEnd; i++)
            {
                TrendLine? hiCandidate = ProcessChartDataHiShort(i, lineLen, end);
                if (hiCandidate is not null)
                {
                    double m = Math.Abs((hiCandidate.Y2 - hiCandidate.Y1) / (hiCandidate.X2 - hiCandidate.X1));
                    if (m < bestResistanceGradient)
                    {
                        bestResistanceGradient = m;
                        bestResistanceLine = hiCandidate;
                    }
                }
            }
        }

        private TrendLine? ProcessChartDataLoShort(int start, int lineLen, int end)
        {
            int xa = start;
            int xb = start + lineLen;
            double ya = candles[start].Low;
            double least_m = double.MinValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                // The steepest positive gradient possible for this endpoint
                // is constrained by the highest point reachable at j.
                // If that max gradient's abs can't beat our current best (we want largest), skip.
                double maxGradient = (dataHighMax - ya) / (j - xa);
                if (Math.Abs(maxGradient) <= least_m)
                    continue;

                // Find the constraining gradient: the line must sit below all BodyLo values.
                // The "highest" valid support line is the one constrained by the lowest BodyLo,
                // which forces the most negative (or least positive) gradient.
                double bestGradientForJ = double.PositiveInfinity;
                for (int i = xa + 1; i < j; i++)
                {
                    double requiredM = (candles[i].BodyLo - ya) / (i - xa);
                    if (requiredM < bestGradientForJ)
                        bestGradientForJ = requiredM;
                }

                double m = bestGradientForJ;
                if (m == double.PositiveInfinity)
                    continue;

                // We want m >= 0 (support slopes up or flat for short pattern)
                if (m < 0)
                    continue;

                double c = ya - m * xa;
                double yEnd = m * j + c;

                // Validate: count touches (wick touches between Low and BodyLo)
                int touchCount = 0;
                bool valid = true;
                for (int i = xa; i < j; i++)
                {
                    Candle candle = candles[i];
                    double y = m * candle.Index + c;

                    if (y > candle.BodyLo)
                    {
                        valid = false;
                        break;
                    }
                    if (y <= candle.BodyLo && y >= candle.Low)
                        touchCount++;
                }

                if (!valid)
                    continue;

                if (touchCount > MinTouchCount)
                {
                    double abs_m = Math.Abs(m);
                    if (abs_m > least_m)
                    {
                        least_m = abs_m;
                        bestfit = new TrendLine(xa, ya, j, yEnd);
                    }
                }
            }

            return bestfit;
        }

        private TrendLine? ProcessChartDataHiShort(int start, int lineLen, int end)
        {
            int xa = start;
            int xb = start + lineLen;
            double ya = candles[start].High;
            double least_m = double.MaxValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                // Find the constraining gradient: the line must sit above all BodyHi values.
                // The "lowest" valid resistance is constrained by the highest BodyHi,
                // which forces the least negative (or most positive) gradient.
                double bestGradientForJ = double.NegativeInfinity;
                for (int i = xa + 1; i < j; i++)
                {
                    double requiredM = (candles[i].BodyHi - ya) / (i - xa);
                    if (requiredM > bestGradientForJ)
                        bestGradientForJ = requiredM;
                }

                double m = bestGradientForJ;
                if (m == double.NegativeInfinity)
                    continue;

                // Early skip: if this gradient's abs is already worse than our best, skip
                double abs_m = Math.Abs(m);
                if (abs_m >= least_m)
                    continue;

                double c = ya - m * xa;
                double yEnd = m * j + c;

                // Validate: count touches (wick touches between BodyHi and High)
                int touchCount = 0;
                bool valid = true;
                for (int i = xa; i < j; i++)
                {
                    Candle candle = candles[i];
                    double y = m * candle.Index + c;

                    if (y < candle.BodyHi)
                    {
                        valid = false;
                        break;
                    }
                    if (y >= candle.BodyHi && y <= candle.High)
                        touchCount++;
                }

                if (!valid)
                    continue;

                if (touchCount > 1)
                {
                    if (abs_m < least_m)
                    {
                        least_m = abs_m;
                        bestfit = new TrendLine(xa, ya, j, yEnd);
                    }
                }
            }

            return bestfit;
        }

        private TrendLine ExtendLineShort(TrendLine line)
        {
            double m = (line.Y2 - line.Y1) / (line.X2 - line.X1);
            double c = line.Y1 - m * line.X1;

            int startIndex = (int)line.X2 + 1;
            int maxExtension = (int)(line.X2 - line.X1) * 2;
            int endIndex = Math.Min(startIndex + maxExtension, candles.Count);

            for (int i = startIndex; i < endIndex; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                if (y > candle.Low && y < candle.High)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);

                if (y > candle.High)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);
            }

            double lastIndex = (endIndex < candles.Count) ? candles[endIndex - 1].Index : candles[^1].Index;
            return new TrendLine(line.X1, line.Y1, lastIndex, m * lastIndex + c);
        }

        // =====================================================================
        // Trade Evaluation
        // =====================================================================

        private (int result, int resolvedIndex) EvaluateTradeLong(double pricePerc, int entryIndex, int patternLength, double slRatio, int maxTradeLength)
        {
            double startPrice = candles[entryIndex].High;
            double profit = startPrice * (1 + pricePerc / 100);
            double loss = startPrice * (1 - (pricePerc * slRatio) / 100);

            int limit = Math.Min(entryIndex + 1 + maxTradeLength, candles.Count);

            for (int i = entryIndex + 1; i < limit; i++)
            {
                if (candles[i].High >= profit)
                    return (1, i);
                else if (candles[i].Low <= loss)
                    return (-1, i);
            }
            return (0, limit - 1);
        }

        private (int result, int resolvedIndex) EvaluateTradeShort(double pricePerc, int entryIndex, int patternLength, double slRatio, int maxTradeLength)
        {
            double startPrice = candles[entryIndex].Low;
            double profit = startPrice * (1 - pricePerc / 100);
            double loss = startPrice * (1 + (pricePerc * slRatio) / 100);

            int limit = Math.Min(entryIndex + 1 + maxTradeLength, candles.Count);

            for (int i = entryIndex + 1; i < limit; i++)
            {
                if (candles[i].Low <= profit)
                    return (1, i);
                else if (candles[i].High >= loss)
                    return (-1, i);
            }
            return (0, limit - 1);
        }

        // =====================================================================
        // Trendline Fitting
        // =====================================================================

        private TrendLine? ProcessChartDataHi(int start, int lineLen, int end)
        {
            int xa = start;
            int xb = start + lineLen;
            double ya = candles[start].High;
            double least_m = double.MinValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                // The lowest (most negative) gradient possible for this endpoint
                // is constrained by the highest BodyHi between xa and j.
                // If that minimum gradient's abs can't beat our current best, skip.
                double minGradient = (dataLowMin - ya) / (j - xa);
                if (Math.Abs(minGradient) <= least_m)
                    continue;

                // Find the constraining gradient: the line must sit above all BodyHi values.
                // The "lowest" valid line is determined by the candle that forces the
                // steepest (least negative) gradient.
                double bestGradientForJ = double.NegativeInfinity;
                for (int i = xa + 1; i < j; i++)
                {
                    // Gradient required to just touch candle i's BodyHi from anchor (xa, ya)
                    double requiredM = (candles[i].BodyHi - ya) / (i - xa);
                    if (requiredM > bestGradientForJ)
                        bestGradientForJ = requiredM;
                }

                // We want m <= 0 (resistance slopes down or flat)
                double m = bestGradientForJ;
                if (m > 0)
                    continue;

                double c = ya - m * xa;
                double yEnd = m * j + c;

                // Validate: count touches (wick touches between BodyHi and High)
                int touchCount = 0;
                bool valid = true;
                for (int i = xa; i < j; i++)
                {
                    Candle candle = candles[i];
                    double y = m * candle.Index + c;

                    if (y < candle.BodyHi)
                    {
                        valid = false;
                        break;
                    }
                    if (y >= candle.BodyHi && y <= candle.High)
                        touchCount++;
                }

                if (!valid)
                    continue;

                if (touchCount > MinTouchCount)
                {
                    double abs_m = Math.Abs(m);
                    if (abs_m > least_m)
                    {
                        least_m = abs_m;
                        bestfit = new TrendLine(xa, ya, j, yEnd);
                    }
                }
            }

            return bestfit;
        }

        private TrendLine? ProcessChartDataLo(int start, int lineLen, int end)
        {
            int xa = start;
            int xb = start + lineLen;
            double ya = candles[start].Low;
            double least_m = double.MaxValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                // Find the constraining gradient: the line must sit below all BodyLo values.
                // The "highest" valid line is determined by the candle that forces the
                // least positive (most negative) gradient — i.e. the lowest BodyLo pulls the line down.
                double bestGradientForJ = double.PositiveInfinity;
                for (int i = xa + 1; i < j; i++)
                {
                    // Gradient required so line just touches candle i's BodyLo from anchor (xa, ya)
                    double requiredM = (candles[i].BodyLo - ya) / (i - xa);
                    if (requiredM < bestGradientForJ)
                        bestGradientForJ = requiredM;
                }

                double m = bestGradientForJ;
                if (m == double.PositiveInfinity)
                    continue;

                // Early skip: if this gradient's abs is already worse than our best, skip
                double abs_m = Math.Abs(m);
                if (abs_m >= least_m)
                    continue;

                double c = ya - m * xa;
                double yEnd = m * j + c;

                // Validate: count touches (wick touches between Low and BodyLo)
                int touchCount = 0;
                bool valid = true;
                for (int i = xa; i < j; i++)
                {
                    Candle candle = candles[i];
                    double y = m * candle.Index + c;

                    if (y > candle.BodyLo)
                    {
                        valid = false;
                        break;
                    }
                    if (y <= candle.BodyLo && y >= candle.Low)
                        touchCount++;
                }

                if (!valid)
                    continue;

                if (touchCount > 1)
                {
                    if (abs_m < least_m)
                    {
                        least_m = abs_m;
                        bestfit = new TrendLine(xa, ya, j, yEnd);
                    }
                }
            }

            return bestfit;
        }

        // =====================================================================
        // Candle Check Methods
        // =====================================================================

        private (int, int) CheckCandleHi(double m, double c, int start, int end)
        {
            int result = 1;
            int touchCount = 0;

            for (int i = start; i < end; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                if (y < candle.BodyHi)
                {
                    result = -1;
                    break;
                }
                else
                {
                    if (Math.Round(y) == Math.Round(candle.BodyHi))
                        result = 0;

                    if (y >= candle.BodyHi && y <= candle.High)
                        touchCount++;
                }
            }

            return (result, touchCount);
        }

        private (int, int) CheckCandleLo(double m, double c, int start, int end)
        {
            int result = 1;
            int touchCount = 0;

            for (int i = start; i < end; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                if (y > candle.BodyLo)
                {
                    result = -1;
                    break;
                }
                else
                {
                    if (Math.Round(y) == Math.Round(candle.BodyLo))
                        result = 0;

                    if (y <= candle.BodyLo && y >= candle.Low)
                        touchCount++;
                }
            }

            return (result, touchCount);
        }
    }
}
