using System.Globalization;

namespace ChartApp
{
    public static class ResultLogger
    {
        private static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "results");

        public static void Log(Chromosome c, EvalResult result, int passNumber)
        {
            Directory.CreateDirectory(OutputDir);

            string filename = $"pass_{passNumber:D4}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string filepath = Path.Combine(OutputDir, filename);

            using var sw = new StreamWriter(filepath);

            sw.WriteLine("=== GA Evaluation Pass ===");
            sw.WriteLine($"Pass Number:       {passNumber}");
            sw.WriteLine($"Timestamp:         {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sw.WriteLine();

            sw.WriteLine("--- GA Parameters ---");
            sw.WriteLine($"WindowSize:        {c.WindowSize}");
            sw.WriteLine($"WindowShift:       {c.WindowShift}");
            sw.WriteLine($"MinLineLength:     {c.MinLineLength}");
            sw.WriteLine($"SmaPeriod:         {c.SmaPeriod}");
            sw.WriteLine($"MaxGradientDiff:   {c.MaxGradientDiff.ToString("F6", CultureInfo.InvariantCulture)}");
            sw.WriteLine();

            sw.WriteLine("--- Constants ---");
            sw.WriteLine($"Vicinity:          {Chromosome.Vicinity}");
            sw.WriteLine($"MinTouchCount:     2");
            sw.WriteLine();

            sw.WriteLine("--- Results ---");
            sw.WriteLine($"Fitness (Return%): {result.Fitness.ToString("F4", CultureInfo.InvariantCulture)}");
            sw.WriteLine($"Patterns Found:    {result.PatternsFound}");
            sw.WriteLine($"Total Return %:    {result.TotalReturnPerc.ToString("F4", CultureInfo.InvariantCulture)}");
        }
    }
}
