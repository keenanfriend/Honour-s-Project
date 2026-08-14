
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Plotly.NET;
using Plotly.NET.CSharp;

using CoreChart = Plotly.NET.Chart;
using CSharpChart = Plotly.NET.CSharp.Chart;

namespace ChartApp
{
    internal class Program
    {
        List<Candle> candles = new();
        List<TrendLine> trendLines = new();

        List<Candle> maxima = new();
        List<Candle> minima = new();

        TrendLine? bestSupportLine = null;
        double bestSupportGradient = double.MaxValue;

        TrendLine? bestResistanceLine = null;
        double bestResistanceGradient = double.MinValue;

        double dataHighMax = double.MinValue;
        double dataLowMin = double.MaxValue;

        // Daily SMA data: maps date string (yyyy-MM-dd) to SMA50 value
        Dictionary<string, double> dailySma = new();

        public static void Main(string[] args)
        {
            new Program();
        }

        public Program()
        {
            ReadData();
            ReadDailySma();
            RunSlidingWindow();
        }

        List<(TrendLine support, TrendLine resistance, int tradeNum, string result)> tradeRecords = new();

        // =====================================================================
        // SMA Lookup
        // =====================================================================

        public void ReadDailySma()
        {
            string dataPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "btc_daily.csv");
            using StreamReader sr = new StreamReader(dataPath);
            sr.ReadLine(); // skip header

            while (!sr.EndOfStream)
            {
                string? line = sr.ReadLine();
                if (line is null) continue;

                var split = line.Split(',');
                // Datetime,Open,High,Low,Close,Volume,SMA50
                string date = split[0].Substring(0, 10); // extract yyyy-MM-dd
                string smaStr = split[6];

                if (!string.IsNullOrEmpty(smaStr) && double.TryParse(smaStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sma))
                {
                    dailySma[date] = sma;
                }
            }

            Console.WriteLine($"Loaded {dailySma.Count} daily SMA values.");
        }

        /// <summary>
        /// Returns the SMA50 value for the given candle's date, or null if not available.
        /// </summary>
        public double? GetSmaForCandle(Candle candle)
        {
            string date = candle.Date.Substring(0, 10);
            if (dailySma.TryGetValue(date, out double sma))
                return sma;
            return null;
        }

        /// <summary>
        /// Determines trade direction: true = long (price above SMA), false = short (price below SMA).
        /// Returns null if SMA is not available.
        /// </summary>
        public bool? GetTradeDirection(int candleIndex)
        {
            double? sma = GetSmaForCandle(candles[candleIndex]);
            if (sma is null) return null;
            return candles[candleIndex].Close > sma.Value;
        }

        // =====================================================================
        // Sliding Window
        // =====================================================================

        public void RunSlidingWindow()
        {
            int windowSize = 168;
            int lineLen = 80;
            int windowStart = 0;

            int trades = 0;
            int wins = 0;
            int losses = 0;
            int noResult = 0;
            double balance = 100.0;

            int longTrades = 0, longWins = 0;
            int shortTrades = 0, shortWins = 0;

            // Diagnostic counters
            int totalWindows = 0;
            int noResistanceFound = 0;
            int noSupportFound = 0;
            int failedGradientCheck = 0;
            int failedEntryBeyondWindow = 0;
            int failedEntryOutOfBounds = 0;
            int noSmaAvailable = 0;
            int tradeTaken = 0;

            while (windowStart + windowSize <= candles.Count)
            {
                int windowEnd = windowStart + windowSize;
                totalWindows++;

                // Determine trade direction from SMA at the window's last candle
                bool? direction = GetTradeDirection(windowEnd - 1);
                if (direction is null)
                {
                    noSmaAvailable++;
                    windowStart += 6;
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
                    // For shorts: support wants highest gradient, resistance wants lowest (flattest)
                    bestSupportGradient = double.MinValue;
                    bestResistanceGradient = double.MaxValue;
                }

                // Compute dynamic price bounds for this window
                dataHighMax = candles.Skip(windowStart).Take(windowSize).Max(c => c.High);
                dataLowMin = candles.Skip(windowStart).Take(windowSize).Min(c => c.Low);

                double pricePerc;
                int entryIndex;
                bool patternFound;

                if (isLong)
                {
                    patternFound = FindLongPattern(windowStart, lineLen, windowEnd, out pricePerc, out entryIndex);
                }
                else
                {
                    patternFound = FindShortPattern(windowStart, lineLen, windowEnd, out pricePerc, out entryIndex);
                }

                if (!patternFound)
                {
                    windowStart += 6;
                    continue;
                }

                // Cap the trade margin at 10%
                if (pricePerc > 10.0)
                    pricePerc = 10.0;

                if (entryIndex < windowEnd - 1)
                {
                    failedEntryBeyondWindow++;
                    windowStart += 6;
                    continue;
                }

                if (entryIndex >= candles.Count)
                {
                    failedEntryOutOfBounds++;
                    windowStart += 6;
                    continue;
                }

                // Trade is valid
                tradeTaken++;
                int result;
                int resolvedIndex;

                // Pattern length = distance from line start (X1) to entry point
                int patternLength;
                if (isLong)
                {
                    patternLength = entryIndex - (int)bestResistanceLine!.X1;
                    result = CalculateResultLong(pricePerc, entryIndex, patternLength);
                    resolvedIndex = FindResolutionIndexLong(pricePerc, entryIndex, patternLength);
                }
                else
                {
                    patternLength = entryIndex - (int)bestSupportLine!.X1;
                    result = CalculateResultShort(pricePerc, entryIndex, patternLength);
                    resolvedIndex = FindResolutionIndexShort(pricePerc, entryIndex, patternLength);
                }

                trades++;
                string dirStr = isLong ? "LONG" : "SHORT";
                string resultStr = result == 1 ? "TP hit" : result == -1 ? "SL hit" : "No hit";

                if (result == 1)
                {
                    wins++;
                    balance *= (1 + pricePerc / 100);
                    if (isLong) { longTrades++; longWins++; }
                    else { shortTrades++; shortWins++; }
                }
                else if (result == -1)
                {
                    losses++;
                    balance *= (1 - pricePerc / 100);
                    if (isLong) longTrades++;
                    else shortTrades++;
                }
                else
                {
                    noResult++;
                    if (isLong) longTrades++;
                    else shortTrades++;
                }

                Console.WriteLine($"Trade #{trades} [{dirStr}] @ candle {entryIndex} ({candles[entryIndex].Date}): Price%={pricePerc:F2}%, Result={resultStr}, Balance=${balance:F2}");

                tradeRecords.Add((bestSupportLine!, bestResistanceLine!, trades, resultStr));

                windowStart = resolvedIndex + 1;
            }

            Console.WriteLine($"\n--- Results ---");
            Console.WriteLine($"Total trades: {trades}");
            Console.WriteLine($"Wins (TP):    {wins}");
            Console.WriteLine($"Losses (SL):  {losses}");
            Console.WriteLine($"No result:    {noResult}");
            if (trades > 0)
                Console.WriteLine($"Win rate:     {(double)wins / trades * 100:F1}%");
            Console.WriteLine($"Final balance: ${balance:F2} (started at $100.00)");

            Console.WriteLine($"\n--- By Direction ---");
            Console.WriteLine($"Long trades:  {longTrades}, Wins: {longWins}, Win rate: {(longTrades > 0 ? (double)longWins / longTrades * 100 : 0):F1}%");
            Console.WriteLine($"Short trades: {shortTrades}, Wins: {shortWins}, Win rate: {(shortTrades > 0 ? (double)shortWins / shortTrades * 100 : 0):F1}%");

            Console.WriteLine($"\n--- Diagnostics ---");
            Console.WriteLine($"Total windows scanned:       {totalWindows}");
            Console.WriteLine($"No SMA available:            {noSmaAvailable} ({(double)noSmaAvailable / totalWindows * 100:F1}%)");
            Console.WriteLine($"No resistance found:         {noResistanceFound} ({(double)noResistanceFound / totalWindows * 100:F1}%)");
            Console.WriteLine($"No support found:            {noSupportFound} ({(double)noSupportFound / totalWindows * 100:F1}%)");
            Console.WriteLine($"Failed gradient check:       {failedGradientCheck} ({(double)failedGradientCheck / totalWindows * 100:F1}%)");
            Console.WriteLine($"Failed entry beyond window:  {failedEntryBeyondWindow} ({(double)failedEntryBeyondWindow / totalWindows * 100:F1}%)");
            Console.WriteLine($"Failed entry out of bounds:  {failedEntryOutOfBounds} ({(double)failedEntryOutOfBounds / totalWindows * 100:F1}%)");
            Console.WriteLine($"Trades taken:                {tradeTaken} ({(double)tradeTaken / totalWindows * 100:F1}%)");
        }

        // =====================================================================
        // LONG Pattern Finding (existing logic)
        // =====================================================================

        /// <summary>
        /// Finds a long pattern: resistance line above (downward slope) + support below.
        /// Breakout is upward through resistance.
        /// </summary>
        public bool FindLongPattern(int windowStart, int lineLen, int windowEnd, out double pricePerc, out int entryIndex)
        {
            pricePerc = 0;
            entryIndex = 0;

            FindLinesLong(windowStart, lineLen, windowEnd);

            if (bestResistanceLine is null)
                return false;

            bestResistanceLine = ExtendLineLong(bestResistanceLine);

            if (bestSupportLine is null)
                return false;

            // Pin the support line's X1/X2 to the resistance line's endpoints
            double m = (bestSupportLine.Y2 - bestSupportLine.Y1) / (bestSupportLine.X2 - bestSupportLine.X1);
            double c = bestSupportLine.Y1 - m * bestSupportLine.X1;
            double newX1 = bestResistanceLine.X1;
            double newX2 = bestResistanceLine.X2;
            bestSupportLine = new TrendLine(newX1, m * newX1 + c, newX2, m * newX2 + c);

            double resistanceGrad = (bestResistanceLine.Y2 - bestResistanceLine.Y1) / (bestResistanceLine.X2 - bestResistanceLine.X1);
            double supportGrad = (bestSupportLine.Y2 - bestSupportLine.Y1) / (bestSupportLine.X2 - bestSupportLine.X1);

            // Parallel or converging: resistance gradient <= support gradient
            if (resistanceGrad > supportGrad)
                return false;

            pricePerc = (bestResistanceLine.Y1 - bestSupportLine.Y1) / bestSupportLine.Y1 * 100;
            entryIndex = (int)bestResistanceLine.X2;
            return true;
        }

        public void FindLinesLong(int start, int lineLen, int end)
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

            double loStart = bestResistanceLine.X1 - 20;
            double loEnd = bestResistanceLine.X1 + 20;
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

        /// <summary>
        /// Extends resistance line forward until price touches or breaches it from below.
        /// </summary>
        public TrendLine ExtendLineLong(TrendLine line)
        {
            double m = (line.Y2 - line.Y1) / (line.X2 - line.X1);
            double c = line.Y1 - m * line.X1;

            int startIndex = (int)line.X2 + 1;

            for (int i = startIndex; i < candles.Count; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                if (y > candle.Low && y < candle.High)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);

                if (y < candle.Low)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);
            }

            double lastIndex = candles[^1].Index;
            return new TrendLine(line.X1, line.Y1, lastIndex, m * lastIndex + c);
        }

        // =====================================================================
        // SHORT Pattern Finding (mirrored logic)
        // =====================================================================

        /// <summary>
        /// Finds a short pattern: support line below (upward slope) + resistance above.
        /// Breakout is downward through support.
        /// </summary>
        public bool FindShortPattern(int windowStart, int lineLen, int windowEnd, out double pricePerc, out int entryIndex)
        {
            pricePerc = 0;
            entryIndex = 0;

            FindLinesShort(windowStart, lineLen, windowEnd);

            if (bestSupportLine is null)
                return false;

            bestSupportLine = ExtendLineShort(bestSupportLine);

            if (bestResistanceLine is null)
                return false;

            // Pin the resistance line's X1/X2 to the support line's endpoints
            double m = (bestResistanceLine.Y2 - bestResistanceLine.Y1) / (bestResistanceLine.X2 - bestResistanceLine.X1);
            double c = bestResistanceLine.Y1 - m * bestResistanceLine.X1;
            double newX1 = bestSupportLine.X1;
            double newX2 = bestSupportLine.X2;
            bestResistanceLine = new TrendLine(newX1, m * newX1 + c, newX2, m * newX2 + c);

            double supportGrad = (bestSupportLine.Y2 - bestSupportLine.Y1) / (bestSupportLine.X2 - bestSupportLine.X1);
            double resistanceGrad = (bestResistanceLine.Y2 - bestResistanceLine.Y1) / (bestResistanceLine.X2 - bestResistanceLine.X1);

            // Parallel or converging downward: support gradient >= resistance gradient
            if (supportGrad < resistanceGrad)
                return false;

            pricePerc = (bestResistanceLine.Y1 - bestSupportLine.Y1) / bestSupportLine.Y1 * 100;
            entryIndex = (int)bestSupportLine.X2;
            return true;
        }

        public void FindLinesShort(int start, int lineLen, int end)
        {
            // First find the support line (below price, upward/flat slope)
            for (int i = start; i < (end - lineLen); i++)
            {
                TrendLine? loCandidate = ProcessChartDataLoShort(i, lineLen, end);
                if (loCandidate is not null)
                {
                    double m = Math.Abs((loCandidate.Y2 - loCandidate.Y1) / (loCandidate.X2 - loCandidate.X1));
                    if (m > bestSupportGradient) // highest gradient wins for short support
                    {
                        bestSupportGradient = m;
                        bestSupportLine = loCandidate;
                    }
                }
            }

            if (bestSupportLine is null) return;

            // Then find resistance near the support anchor
            double hiStart = bestSupportLine.X1 - 20;
            double hiEnd = bestSupportLine.X1 + 20;
            if (hiStart < start) hiStart = start;
            if (hiEnd > end) hiEnd = end;

            for (int i = (int)hiStart; i < (int)hiEnd; i++)
            {
                TrendLine? hiCandidate = ProcessChartDataHiShort(i, lineLen, end);
                if (hiCandidate is not null)
                {
                    double m = Math.Abs((hiCandidate.Y2 - hiCandidate.Y1) / (hiCandidate.X2 - hiCandidate.X1));
                    if (m < bestResistanceGradient) // flattest resistance for short
                    {
                        bestResistanceGradient = m;
                        bestResistanceLine = hiCandidate;
                    }
                }
            }
        }

        /// <summary>
        /// For shorts: finds a support line with upward/flat slope (m >= 0) that stays below candle bodies.
        /// </summary>
        public TrendLine? ProcessChartDataLoShort(int start, int lineLen, int end)
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

                    (check, _) = CheckCandleLo(m, c, xa, j);

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

                    (_, touchCount) = CheckCandleLo(m, mid - m * j, xa, j);

                    if (touchCount > 2 && m >= 0) // upward or flat slope, >2 touches
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

        /// <summary>
        /// For shorts: finds a resistance line above price (any slope) with >1 touch.
        /// </summary>
        public TrendLine? ProcessChartDataHiShort(int start, int lineLen, int end)
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

                    (check, _) = CheckCandleHi(m, c, xa, j);

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
                        (_, touchCount) = CheckCandleHi(m, mid - m * j, xa, j);

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

        /// <summary>
        /// Extends support line forward until price touches or breaches it from above.
        /// </summary>
        public TrendLine ExtendLineShort(TrendLine line)
        {
            double m = (line.Y2 - line.Y1) / (line.X2 - line.X1);
            double c = line.Y1 - m * line.X1;

            int startIndex = (int)line.X2 + 1;

            for (int i = startIndex; i < candles.Count; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                // Stop if the line enters the candle's range (touches price)
                if (y > candle.Low && y < candle.High)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);

                // Stop if the line has been breached (price crossed above it)
                if (y > candle.High)
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);
            }

            double lastIndex = candles[^1].Index;
            return new TrendLine(line.X1, line.Y1, lastIndex, m * lastIndex + c);
        }

        // =====================================================================
        // Trade Result Calculation
        // =====================================================================

        /// <summary>Long: TP when price rises by pricePerc, SL when it falls by half. No time limit.</summary>
        public int CalculateResultLong(double pricePerc, int entryIndex, int patternLength)
        {
            double startPrice = candles[entryIndex].High;
            double profit = startPrice * (1 + pricePerc / 100);
            double loss   = startPrice * (1 - (pricePerc / 2) / 100);

            for (int i = entryIndex + 1; i < candles.Count; i++)
            {
                if (candles[i].High >= profit)
                    return 1;
                else if (candles[i].Low <= loss)
                    return -1;
            }
            return 0;
        }

        public int CalculateResultShort(double pricePerc, int entryIndex, int patternLength)
        {
            double startPrice = candles[entryIndex].Low;
            double profit = startPrice * (1 - pricePerc / 100);
            double loss   = startPrice * (1 + pricePerc / 100);

            for (int i = entryIndex + 1; i < candles.Count; i++)
            {
                if (candles[i].Low <= profit)
                    return 1;
                else if (candles[i].High >= loss)
                    return -1;
            }
            return 0;
        }

        public int FindResolutionIndexLong(double pricePerc, int entryIndex, int patternLength)
        {
            double startPrice = candles[entryIndex].High;
            double profit = startPrice * (1 + pricePerc / 100);
            double loss   = startPrice * (1 - pricePerc / 100);

            for (int i = entryIndex + 1; i < candles.Count; i++)
            {
                if (candles[i].High >= profit || candles[i].Low <= loss)
                    return i;
            }
            return candles.Count - 1;
        }

        public int FindResolutionIndexShort(double pricePerc, int entryIndex, int patternLength)
        {
            double startPrice = candles[entryIndex].Low;
            double profit = startPrice * (1 - pricePerc / 100);
            double loss   = startPrice * (1 + (pricePerc / 2) / 100);

            for (int i = entryIndex + 1; i < candles.Count; i++)
            {
                if (candles[i].Low <= profit || candles[i].High >= loss)
                    return i;
            }
            return candles.Count - 1;
        }



        public TrendLine? ProcessChartDataHi(int start, int lineLen, int end)
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

                    (check, _) = CheckCandleHi(m, c, xa, j);

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
                        (_, touchCount) = CheckCandleHi(m, mid - m * j, xa, j);

                        if (touchCount > 2 && m <= 0)
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

        public TrendLine? ProcessChartDataLo(int start, int lineLen, int end)
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

                    (check, _) = CheckCandleLo(m, c, xa, j);

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

                    (_, touchCount) = CheckCandleLo(m, mid - m * j, xa, j);

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
        // Candle Check Methods (shared by long and short)
        // =====================================================================

        public (int, int) CheckCandleHi(double m, double c, int start, int end)
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

        public (int, int) CheckCandleLo(double m, double c, int start, int end)
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


        public void ReadData()
        {
            string dataPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "btc_data.csv");
            StreamReader sr = new StreamReader(dataPath);
            sr.ReadLine();

            while (!sr.EndOfStream)
            {
                string? line = sr.ReadLine();
                if (line is null) continue;

                var split = line.Split(',');

                var date   = split[0];
                var open   = double.Parse(split[1], CultureInfo.InvariantCulture);
                var high   = double.Parse(split[2], CultureInfo.InvariantCulture);
                var low    = double.Parse(split[3], CultureInfo.InvariantCulture);
                var close  = double.Parse(split[4], CultureInfo.InvariantCulture);
                var volume = double.Parse(split[5], CultureInfo.InvariantCulture);

                var candle = new Candle(candles.Count, date, low, high, open, close, volume);
                candle.SetBodyHi(open, close);
                candle.SetBodyLo(open, close);
                candles.Add(candle);
            }
        }

        public GenericChart PopulateChart()
        {
            var xData = candles.Select(c => (double)c.Index).ToArray();
            var open  = candles.Select(c => c.Open).ToArray();
            var high  = candles.Select(c => c.High).ToArray();
            var low   = candles.Select(c => c.Low).ToArray();
            var close = candles.Select(c => c.Close).ToArray();

            return CSharpChart.Candlestick<double, double, string>(x: xData, open: open, high: high, low: low, close: close);
        }

        public void Display()
        {
            var priceChart = PopulateChart();
            var allCharts = new List<GenericChart> { priceChart };

            foreach (var (support, resistance, tradeNum, result) in tradeRecords)
            {
                var resistanceChart = CSharpChart.Line<double, double, string>(
                    x: new[] { resistance.X1, resistance.X2 },
                    y: new[] { resistance.Y1, resistance.Y2 },
                    Name: $"#{tradeNum} Resistance ({result})",
                    ShowMarkers: false,
                    ShowLegend: true,
                    LineColor: Plotly.NET.Color.fromARGB(255, 220, 0, 0),
                    LineWidth: 2.0
                );
                allCharts.Add(resistanceChart);

                var supportChart = CSharpChart.Line<double, double, string>(
                    x: new[] { support.X1, support.X2 },
                    y: new[] { support.Y1, support.Y2 },
                    Name: $"#{tradeNum} Support ({result})",
                    ShowMarkers: false,
                    ShowLegend: true,
                    LineColor: Plotly.NET.Color.fromARGB(255, 0, 200, 0),
                    LineWidth: 2.0
                );
                allCharts.Add(supportChart);
            }

            var finalChart = CoreChart.WithSize(Width: 1600, Height: 900).Invoke(CoreChart.Combine(allCharts));
            Console.WriteLine($"Graphing {candles.Count} candles with {tradeRecords.Count} trades...");

            string outputPath = Path.Combine(AppContext.BaseDirectory, "chart.html");
            Plotly.NET.GenericChartExtensions.SaveHtml(finalChart, outputPath);
            Console.WriteLine($"Chart saved to: {outputPath}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                UseShellExecute = true
            });
        }
    }
}
