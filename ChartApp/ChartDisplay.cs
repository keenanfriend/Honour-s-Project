using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Plotly.NET;
using Plotly.NET.CSharp;

using CoreChart = Plotly.NET.Chart;
using CSharpChart = Plotly.NET.CSharp.Chart;

namespace ChartApp
{
    public class ChartDisplay
    {
        private readonly List<Candle> candles;
        private readonly List<(TrendLine support, TrendLine resistance, int tradeNum, string result)> tradeRecords;

        public ChartDisplay(List<Candle> candles, List<(TrendLine support, TrendLine resistance, int tradeNum, string result)> tradeRecords)
        {
            this.candles = candles;
            this.tradeRecords = tradeRecords;
        }

        public void Show()
        {
            var priceChart = BuildCandlestickChart();
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

        private GenericChart BuildCandlestickChart()
        {
            var xData = candles.Select(c => (double)c.Index).ToArray();
            var open = candles.Select(c => c.Open).ToArray();
            var high = candles.Select(c => c.High).ToArray();
            var low = candles.Select(c => c.Low).ToArray();
            var close = candles.Select(c => c.Close).ToArray();

            return CSharpChart.Candlestick<double, double, string>(x: xData, open: open, high: high, low: low, close: close);
        }
    }
}
