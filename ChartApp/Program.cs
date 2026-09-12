using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ChartApp
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--gradient-diagnostic")
            {
                RunGradientDiffDiagnostic();
                return;
            }

            // Load candle data
            List<Candle> candles = LoadCandles();
            Console.WriteLine($"Loaded {candles.Count} candles.");

            // Load daily close prices and precompute the rolling-average lookup once.
            // Each chromosome reads its own SmaPeriod-day average from this same
            // shared series -- the closes don't depend on any gene, only the
            // period averaged over does, so this is built exactly once per run.
            Dictionary<string, double> dailyCloses = LoadDailyCloses();
            Console.WriteLine($"Loaded {dailyCloses.Count} daily close values.");
            var dailyCloseSeries = new DailyCloseSeries(dailyCloses);

            // Create evaluator factory (each thread gets its own instance for thread safety)
            var testCandles = candles.GetRange(0, Math.Min(5000, candles.Count));
            Func<FitnessEval> evaluatorFactory = () => new FitnessEval(testCandles, dailyCloseSeries);

            // Run genetic algorithm. MinGradientDiff (formerly MaxGradientDiff,
            // a ceiling) is now the fifth evolved gene -- a required minimum
            // gradient difference, evolved like the other four. See D-0026.
            var ga = new GeneticAlgorithm(evaluatorFactory, seed: 42)
            {
                PopulationSize = 50,
                Generations = 100,
                TournamentSize = 3,
                CrossoverRate = 0.50,
                MutationRate = 0.15,
                MaxParallelism = 10,
                ElitismCount = 2
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
            Console.WriteLine($"MinGradientDiff:   {best.MinGradientDiff:F6}");
            Console.WriteLine();
            Console.WriteLine("Results written to /results/ folder.");
        }

        // Evaluates one fixed, representative chromosome with no gradient-diff
        // floor (minGradientDiff: 0.0 can never reject anything, since
        // gradientDiff is never negative) and records every detected pattern's
        // realised gradient difference alongside its trade outcome -- answers
        // "what does the natural distribution look like, and does a bigger
        // difference (a sharper wedge) actually trade better?" See D-0024;
        // D-0026 later turned the answer into an evolved floor gene on
        // Chromosome itself (MinGradientDiff, replacing the old
        // MaxGradientDiff ceiling) -- this diagnostic still evaluates outside
        // the GA/evolution path, so it keeps its own independent floor value.
        private static void RunGradientDiffDiagnostic()
        {
            List<Candle> candles = LoadCandles();
            Console.WriteLine($"Loaded {candles.Count} candles.");
            Dictionary<string, double> dailyCloses = LoadDailyCloses();
            var dailyCloseSeries = new DailyCloseSeries(dailyCloses);
            var testCandles = candles.GetRange(0, Math.Min(5000, candles.Count));

            // Representative chromosome: the best individual found in the
            // pre-D-0026 uncapped ablation run (E-0008). minGradientDiff: 0.0
            // means no floor is enforced here -- every pattern the geometry
            // finds is recorded, regardless of its gradient difference.
            var chromosome = new Chromosome(windowSize: 168, windowShift: 15, minLineLength: 28, smaPeriod: 9, minGradientDiff: 0.0);

            var evaluator = new FitnessEval(testCandles, dailyCloseSeries) { LogPatterns = true };
            var result = evaluator.Evaluate(chromosome);

            Console.WriteLine();
            Console.WriteLine("=== Gradient-Diff Diagnostic ===");
            Console.WriteLine($"Chromosome: WS={chromosome.WindowSize} WSh={chromosome.WindowShift} MLL={chromosome.MinLineLength} SMA={chromosome.SmaPeriod} MinGD=unconstrained (0.0)");
            Console.WriteLine($"Fitness: {result.Fitness:F4}% | Patterns: {result.PatternsFound}");
            Console.WriteLine();
            Console.WriteLine("GradientDiff | ReturnPerc | Result");
            foreach (var (gradientDiff, returnPerc, tradeResult) in evaluator.PatternLog)
            {
                string label = tradeResult == 1 ? "WIN" : tradeResult == -1 ? "LOSS" : "TIMEOUT";
                Console.WriteLine($"{gradientDiff,12:F4} | {returnPerc,10:F4} | {label}");
            }

            var wins = evaluator.PatternLog.Where(p => p.Result == 1).Select(p => p.GradientDiff).ToList();
            var losses = evaluator.PatternLog.Where(p => p.Result == -1).Select(p => p.GradientDiff).ToList();
            var timeouts = evaluator.PatternLog.Where(p => p.Result != 1 && p.Result != -1).Select(p => p.GradientDiff).ToList();

            Console.WriteLine();
            Console.WriteLine($"Mean gradientDiff, wins:     {(wins.Count > 0 ? wins.Average() : double.NaN):F4} (n={wins.Count})");
            Console.WriteLine($"Mean gradientDiff, losses:   {(losses.Count > 0 ? losses.Average() : double.NaN):F4} (n={losses.Count})");
            Console.WriteLine($"Mean gradientDiff, timeouts: {(timeouts.Count > 0 ? timeouts.Average() : double.NaN):F4} (n={timeouts.Count})");
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

        // Reads the daily Close column (index 4) of btc_daily.csv. The file also
        // carries an SMA50 column (index 6) that this program no longer reads --
        // its provenance was never established (see Q-0020 in the project brain)
        // and it is superseded by DailyCloseSeries, which computes each
        // chromosome's own SmaPeriod-day average from these closes directly.
        private static Dictionary<string, double> LoadDailyCloses()
        {
            Dictionary<string, double> dailyCloses = new();
            string dataPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "btc_daily.csv");
            using StreamReader sr = new StreamReader(dataPath);
            sr.ReadLine(); // skip header

            while (!sr.EndOfStream)
            {
                string? line = sr.ReadLine();
                if (line is null) continue;

                var split = line.Split(',');
                string date = split[0].Substring(0, 10);
                string closeStr = split[4];

                if (!string.IsNullOrEmpty(closeStr) && double.TryParse(closeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double close))
                {
                    dailyCloses[date] = close;
                }
            }

            return dailyCloses;
        }
    }
}
