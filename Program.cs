
using System;
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

        public static void Main(string[] args)
        {
            new Program();
        }

        public Program()
        {
            ReadData();
            FindLines(0, 80);

            if (bestSupportLine is not null)
                bestSupportLine = ExtendLine(bestSupportLine);

            if (bestResistanceLine is not null)
                bestResistanceLine = ExtendLine(bestResistanceLine);

            Display();
        }

        public void Display()
        {
            var priceChart = PopulateChart();
            var allCharts = new List<GenericChart> { priceChart };

            if (bestSupportLine is not null)
            {
                var supportChart = CSharpChart.Line<double, double, string>(
                    x: new[] { bestSupportLine.X1, bestSupportLine.X2 },
                    y: new[] { bestSupportLine.Y1, bestSupportLine.Y2 },
                    Name: "Support",
                    ShowMarkers: false,
                    ShowLegend: true,
                    LineColor: Plotly.NET.Color.fromARGB(255, 0, 200, 0),
                    LineWidth: 2.0
                );
                allCharts.Add(supportChart);
                Console.WriteLine($"Best support line:    ({bestSupportLine.X1}, {bestSupportLine.Y1:F2}) -> ({bestSupportLine.X2}, {bestSupportLine.Y2:F2}), gradient={bestSupportGradient:F4}");
            }

            if (bestResistanceLine is not null)
            {
                var resistanceChart = CSharpChart.Line<double, double, string>(
                    x: new[] { bestResistanceLine.X1, bestResistanceLine.X2 },
                    y: new[] { bestResistanceLine.Y1, bestResistanceLine.Y2 },
                    Name: "Resistance",
                    ShowMarkers: false,
                    ShowLegend: true,
                    LineColor: Plotly.NET.Color.fromARGB(255, 220, 0, 0),
                    LineWidth: 2.0
                );
                allCharts.Add(resistanceChart);
                Console.WriteLine($"Best resistance line: ({bestResistanceLine.X1}, {bestResistanceLine.Y1:F2}) -> ({bestResistanceLine.X2}, {bestResistanceLine.Y2:F2}), gradient={bestResistanceGradient:F4}");
            }

            var finalChart = CoreChart.WithSize(Width: 1600, Height: 900).Invoke(CoreChart.Combine(allCharts));
            Console.WriteLine($"Graphing {candles.Count} candles...");

            string outputPath = Path.Combine(AppContext.BaseDirectory, "chart.html");
            Plotly.NET.GenericChartExtensions.SaveHtml(finalChart, outputPath);
            Console.WriteLine($"Chart saved to: {outputPath}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                UseShellExecute = true
            });
        }

        public void ReadData()
        {
            StreamReader sr = new StreamReader("btc_data.txt");
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

                var candle = new Candle(candles.Count + 1, date, low, high, open, close, volume);
                candle.SetBodyHi(open, close);
                candle.SetBodyLo(open, close);
                candles.Add(candle);
            }
        }

        /*public void Filter()
        {
            double lo = 100000;
            double hi = 0;
            Stack<Candle> candleStack = new Stack<Candle>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (candles[i].Low < lo)
                    lo = candles[i].Low;

                if (candles[i].High > hi)
                {
                    if (candleStack.Count > 0)
                    {
                        var lastCandle = candleStack.Peek();
                        if (lastCandle.High < candles[i].High)
                        {
                            maxima.Add(candles[i]);
                            candleStack.Pop();
                            candleStack.Push(candles[i]);
                        }
                    }
                    else
                    {
                        candleStack.Push(candles[i]);
                    }
                }
            }
        }*/

        public GenericChart PopulateChart()
        {
            var xData = candles.Select(c => (double)c.Index).ToArray();
            var open  = candles.Select(c => c.Open).ToArray();
            var high  = candles.Select(c => c.High).ToArray();
            var low   = candles.Select(c => c.Low).ToArray();
            var close = candles.Select(c => c.Close).ToArray();

            return CSharpChart.Candlestick<double, double, string>(x: xData, open: open, high: high, low: low, close: close);
        }

        // Returns the lowest-gradient valid resistance line anchored at `start`, or null if none found.
        public TrendLine? ProcessChartDataHi(int start, int lineLen, int end)
        {
            int xa = start;
            int xb = start + lineLen;
            double ya = candles[start].High;
            double m = 0;
            int touchCount;
            double least_m = double.MinValue; // tracking highest gradient

            TrendLine? bestfit = null;

            for (int j = xb; j < end; j++)
            {
                touchCount = 0;
                double max = 75000;
                double min = 65000;
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
                        // check == 0: line is valid, do a final pass to get the true touch count
                        (_, touchCount) = CheckCandleHi(m, mid - m * j, xa, j);

                        if (touchCount > 2 && m <= 0)
                        {
                            double abs_m = Math.Abs(m);
                            if (abs_m > least_m) // highest gradient wins
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

        // Returns the lowest-gradient valid support line anchored at `start`, or null if none found.
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
                double max = 70000;
                double min = 50000;
                double mid = 0;
                m = 1;
                int check = 1;
                int iterations = 20;

                for (int i = 0; i < iterations; i++)
                {
                    mid = (min + max) / 2;
                    m = (mid - ya) / (j - xa); // gradient of the line
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

                    // check == 0: line is valid, do a final pass to get the true touch count
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

        // Extends a line forward candle-by-candle until it enters a candle's full range (Low < y < High).
        public TrendLine ExtendLine(TrendLine line)
        {
            double m = (line.Y2 - line.Y1) / (line.X2 - line.X1);
            double c = line.Y1 - m * line.X1;

            int startIndex = (int)line.X2 + 1;

            for (int i = startIndex; i < candles.Count; i++)
            {
                Candle candle = candles[i];
                double y = m * candle.Index + c;

                if (y > candle.Low && y < candle.High)
                {
                    return new TrendLine(line.X1, line.Y1, candle.Index, y);
                }
            }

            // No candle hit — extend to the last candle
            double lastIndex = candles[^1].Index;
            return new TrendLine(line.X1, line.Y1, lastIndex, m * lastIndex + c);
        }

        public void FindLines(int start, int lineLen)
        {
            int end = 145;

            for (int i = start; i < (end - lineLen); i++)
            {
                TrendLine? hiCandidate = ProcessChartDataHi(i, lineLen, end);
                if (hiCandidate is not null)
                {
                    double m = Math.Abs((hiCandidate.Y2 - hiCandidate.Y1) / (hiCandidate.X2 - hiCandidate.X1));
                    if (m > bestResistanceGradient) // highest gradient wins
                    {
                        bestResistanceGradient = m;
                        bestResistanceLine = hiCandidate;
                    }
                }
            }

            double loStart = 0;
            double loEnd = end;

            if (bestResistanceLine is not null)
            {
                loStart = bestResistanceLine.X1 - 10;
                loEnd = bestResistanceLine.X1 + 10;

                if (loStart < 0) loStart = 0;
                if (loEnd > end) loEnd = end;
            }

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
    }
}
