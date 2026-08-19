using System.Globalization;

namespace ChartApp
{
    public class FitnessEval
    {
        private readonly List<Candle> candles;
        private readonly Dictionary<string, double> dailySma;

        // Mutable state reset per evaluation
        private TrendLine? bestSupportLine = null;
        private TrendLine? bestResistanceLine = null;
        private double bestSupportGradient = double.MaxValue;
        private double bestResistanceGradient = double.MinValue;
        private double dataHighMax = double.MinValue;
        private double dataLowMin = double.MaxValue;

        // Minimum touches required for a valid trendline (adjust manually: 2 or 3)
        private const int MinTouchCount = 2;

        public FitnessEval(List<Candle> candles, Dictionary<string, double> dailySma)
        {
            this.candles = candles;
            this.dailySma = dailySma;
        }

        public double Evaluate(Chromosome c)
        {
            return RunSlidingWindow(c);
        }

        // =====================================================================
        // SMA Lookup
        // =====================================================================

        private double? GetSmaForCandle(Candle candle)
        {
            string date = candle.Date.Substring(0, 10);
            if (dailySma.TryGetValue(date, out double sma))
                return sma;
            return null;
        }

        private bool? GetTradeDirection(int candleIndex)
        {
            double? sma = GetSmaForCandle(candles[candleIndex]);
            if (sma is null) return null;
            return candles[candleIndex].Close > sma.Value;
        }

        // =====================================================================
        // Sliding Window
        // =====================================================================

        private double RunSlidingWindow(Chromosome c)
        {
            int windowSize = c.WindowSize;
            int lineLen = c.MinLineLength;
            int windowShift = c.WindowShift;
            double slRatio = c.SlRatio;
            double maxProfitPerc = c.MaxProfitPerc;
            int vicinity = c.Vicinity;
            int maxTradeLength = c.MaxTradeLength;
            int windowStart = 0;

            int trades = 0;
            int wins = 0;
            int losses = 0;
            int noResult = 0;
            double balance = 100.0;
            double peak = balance;
            double maxDrawdown = 0;

            while (windowStart + windowSize <= candles.Count)
            {
                int windowEnd = windowStart + windowSize;

                // Determine trade direction from SMA at the window's last candle
                bool? direction = GetTradeDirection(windowEnd - 1);
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
                bool patternFound;

                if (isLong)
                {
                    patternFound = FindLongPattern(windowStart, lineLen, windowEnd, vicinity, out pricePerc, out entryIndex);
                }
                else
                {
                    patternFound = FindShortPattern(windowStart, lineLen, windowEnd, vicinity, out pricePerc, out entryIndex);
                }

                if (!patternFound)
                {
                    windowStart += windowShift;
                    continue;
                }

                if (pricePerc > maxProfitPerc)
                    pricePerc = maxProfitPerc;

                if (entryIndex < windowEnd - 1)
                {
                    windowStart += windowShift;
                    continue;
                }

                if (entryIndex >= candles.Count)
                {
                    windowStart += windowShift;
                    continue;
                }

                // Evaluate trade
                int result;
                int resolvedIndex;
                int patternLength;

                if (isLong)
                {
                    patternLength = entryIndex - (int)bestResistanceLine!.X1;
                    (result, resolvedIndex) = EvaluateTradeLong(pricePerc, entryIndex, patternLength, slRatio, maxTradeLength);
                }
                else
                {
                    patternLength = entryIndex - (int)bestSupportLine!.X1;
                    (result, resolvedIndex) = EvaluateTradeShort(pricePerc, entryIndex, patternLength, slRatio, maxTradeLength);
                }

                trades++;

                if (result == 1)
                {
                    wins++;
                    balance *= (1 + pricePerc / 100);
                }
                else if (result == -1)
                {
                    losses++;
                    balance *= (1 - (pricePerc * slRatio) / 100);
                }
                else
                {
                    noResult++;
                }

                // Track drawdown
                if (balance > peak) peak = balance;
                double drawdown = (peak - balance) / peak;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                windowStart = resolvedIndex + 1;
            }

            // Compute fitness
            if (trades == 0) return 0;

            double winRate = (double)wins / trades;
            double drawdownPenalty = (maxDrawdown > 0) ? (1.0 / maxDrawdown) : 100.0;

            return balance * drawdownPenalty * winRate;
        }

        // =====================================================================
        // LONG Pattern Finding
        // =====================================================================

        private bool FindLongPattern(int windowStart, int lineLen, int windowEnd, int vicinity, out double pricePerc, out int entryIndex)
        {
            pricePerc = 0;
            entryIndex = 0;

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

        private bool FindShortPattern(int windowStart, int lineLen, int windowEnd, int vicinity, out double pricePerc, out int entryIndex)
        {
            pricePerc = 0;
            entryIndex = 0;

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
            double m = 0;
            int touchCount;
            double least_m = double.MinValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                touchCount = 0;
                double max = dataHighMax;
                double min = dataLowMin;
                double mid = 0;
                m = 1;
                int check = 1;
                int iterations = 20;

                for (int i = 0; i < iterations; i++)
                {
                    mid = (min + max) / 2;
                    m = (mid - ya) / (j - xa);
                    double c = mid - m * j;

                    (check, touchCount) = CheckCandleLo(m, c, xa, j);

                    if (check == 1)
                    {
                        min = mid;
                        continue;
                    }
                    if (check == -1)
                    {
                        max = mid;
                        continue;
                    }

                    if (touchCount > MinTouchCount && m >= 0)
                    {
                        double abs_m = Math.Abs(m);
                        if (abs_m > least_m)
                        {
                            least_m = abs_m;
                            bestfit = new TrendLine(xa, ya, j, mid);
                        }
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
            double m = 0;
            int touchCount;
            double least_m = double.MaxValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                touchCount = 0;
                double max = dataHighMax;
                double min = dataLowMin;
                double mid = 0;
                m = 1;
                int check = 1;
                int iterations = 20;

                for (int i = 0; i < iterations; i++)
                {
                    mid = (min + max) / 2;
                    m = (mid - ya) / (j - xa);
                    double c = mid - m * j;

                    (check, touchCount) = CheckCandleHi(m, c, xa, j);

                    if (check == 1)
                    {
                        max = mid;
                    }
                    else if (check == -1)
                    {
                        min = mid;
                    }
                    else
                    {
                        if (touchCount > 1)
                        {
                            double abs_m = Math.Abs(m);
                            if (abs_m < least_m)
                            {
                                least_m = abs_m;
                                bestfit = new TrendLine(xa, ya, j, mid);
                            }
                        }
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
            double m = 0;
            int touchCount;
            double least_m = double.MinValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                touchCount = 0;
                double max = dataHighMax;
                double min = dataLowMin;
                double mid = 0;
                m = 1;
                int check = 1;
                int iterations = 20;

                for (int i = 0; i < iterations; i++)
                {
                    mid = (min + max) / 2;
                    m = (mid - ya) / (j - xa);
                    double c = mid - m * j;

                    (check, touchCount) = CheckCandleHi(m, c, xa, j);

                    if (check == 1)
                    {
                        max = mid;
                    }
                    else if (check == -1)
                    {
                        min = mid;
                    }
                    else
                    {
                        if (touchCount > MinTouchCount && m <= 0)
                        {
                            double abs_m = Math.Abs(m);
                            if (abs_m > least_m)
                            {
                                least_m = abs_m;
                                bestfit = new TrendLine(xa, ya, j, mid);
                            }
                        }
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
            double m = 0;
            int touchCount;
            double least_m = double.MaxValue;

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                touchCount = 0;
                double max = dataHighMax;
                double min = dataLowMin;
                double mid = 0;
                m = 1;
                int check = 1;
                int iterations = 20;

                for (int i = 0; i < iterations; i++)
                {
                    mid = (min + max) / 2;
                    m = (mid - ya) / (j - xa);
                    double c = mid - m * j;

                    (check, touchCount) = CheckCandleLo(m, c, xa, j);

                    if (check == 1)
                    {
                        min = mid;
                        continue;
                    }
                    if (check == -1)
                    {
                        max = mid;
                        continue;
                    }

                    if (touchCount > 1)
                    {
                        double abs_m = Math.Abs(m);
                        if (abs_m < least_m)
                        {
                            least_m = abs_m;
                            bestfit = new TrendLine(xa, ya, j, mid);
                        }
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
