namespace ChartApp
{
    public class Chromosome
    {
        // ===== GA Parameters (evolved) =====
        public int WindowSize { get; }
        public int WindowShift { get; }
        public int MinLineLength { get; }
        public int SmaPeriod { get; }
        public double MaxGradientDiff { get; } // Max allowed abs difference in gradient between support & resistance

        // ===== Constants (fixed during GA runs) =====
        public const int Vicinity = 20;
        public const double SlRatio = 0.5;
        public const int MaxTradeLength = 500;
        public const double MaxProfitPerc = 10.0;

        public Chromosome(int windowSize, int windowShift, int minLineLength,
                          int smaPeriod, double maxGradientDiff)
        {
            WindowSize = windowSize;
            WindowShift = windowShift;
            MinLineLength = minLineLength;
            SmaPeriod = smaPeriod;
            MaxGradientDiff = maxGradientDiff;
        }
    }
}
