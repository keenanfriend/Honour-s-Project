using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ChartApp
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            // Load candle data
            List<Candle> candles = LoadCandles();
            Console.WriteLine($"Loaded {candles.Count} candles.");

            // Load SMA data
            Dictionary<string, double> dailySma = LoadDailySma();
            Console.WriteLine($"Loaded {dailySma.Count} daily SMA values.");

            // Create evaluator factory (each thread gets its own instance for thread safety)
            var testCandles = candles.GetRange(0, Math.Min(5000, candles.Count));
            Func<FitnessEval> evaluatorFactory = () => new FitnessEval(testCandles, dailySma);

            // Run genetic algorithm
            var ga = new GeneticAlgorithm(evaluatorFactory)
            {
                PopulationSize = 50,
                Generations = 100,
                TournamentSize = 3,
                CrossoverRate = 0.80,
                MutationRate = 0.15,
                MaxParallelism = 10
            };

            Console.WriteLine("Starting GA...");
            Console.WriteLine($"Population: {ga.PopulationSize} | Generations: {ga.Generations}");
            Console.WriteLine();

            var (best, bestResult) = ga.Run();

            Console.WriteLine();
            Console.WriteLine("=== GA Complete ===");
            Console.WriteLine($"Best Fitness:      {bestResult.Fitness:F4}%");
            Console.WriteLine($"Patterns Found:    {bestResult.PatternsFound}");
            Console.WriteLine($"Total Return %:    {bestResult.TotalReturnPerc:F4}");
            Console.WriteLine();
            Console.WriteLine("--- Best Parameters ---");
            Console.WriteLine($"WindowSize:        {best.WindowSize}");
            Console.WriteLine($"WindowShift:       {best.WindowShift}");
            Console.WriteLine($"MinLineLength:     {best.MinLineLength}");
            Console.WriteLine($"SmaPeriod:         {best.SmaPeriod}");
            Console.WriteLine($"MaxGradientDiff:   {best.MaxGradientDiff:F6}");
            Console.WriteLine();
            Console.WriteLine("Results written to /results/ folder.");
        }

        private static List<Candle> LoadCandles()
        {
            List<Candle> candles = new();
            string dataPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "btc_data.csv");
            using StreamReader sr = new StreamReader(dataPath);
            sr.ReadLine(); // skip header

            while (!sr.EndOfStream)
            {
                string? line = sr.ReadLine();
                if (line is null) continue;

                var split = line.Split(',');

                var date = split[0];
                var open = double.Parse(split[1], CultureInfo.InvariantCulture);
                var high = double.Parse(split[2], CultureInfo.InvariantCulture);
                var low = double.Parse(split[3], CultureInfo.InvariantCulture);
                var close = double.Parse(split[4], CultureInfo.InvariantCulture);
                var volume = double.Parse(split[5], CultureInfo.InvariantCulture);

                var candle = new Candle(candles.Count, date, low, high, open, close, volume);
                candle.SetBodyHi(open, close);
                candle.SetBodyLo(open, close);
                candles.Add(candle);
            }

            return candles;
        }

        private static Dictionary<string, double> LoadDailySma()
        {
            Dictionary<string, double> dailySma = new();
            string dataPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "btc_daily.csv");
            using StreamReader sr = new StreamReader(dataPath);
            sr.ReadLine(); // skip header

            while (!sr.EndOfStream)
            {
                string? line = sr.ReadLine();
                if (line is null) continue;

                var split = line.Split(',');
                string date = split[0].Substring(0, 10);
                string smaStr = split[6];

                if (!string.IsNullOrEmpty(smaStr) && double.TryParse(smaStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sma))
                {
                    dailySma[date] = sma;
                }
            }

            return dailySma;
        }
    }
}
