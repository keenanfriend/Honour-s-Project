namespace ChartApp
{
    public class Chromosome
    {
        public int WindowSize { get; }
        public int WindowShift { get; }
        public int MinLineLength { get; }
        public int Vicinity { get; }
        public double SlRatio { get; }
        public int SmaPeriod { get; }
        public int MaxTradeLength { get; }
        public double MaxProfitPerc { get; }

        public Chromosome(int windowSize, int windowShift, int minLineLength,
                          int vicinity, double slRatio, int smaPeriod,
                          int maxTradeLength, double maxProfitPerc)
        {
            WindowSize = windowSize;
            WindowShift = windowShift;
            MinLineLength = minLineLength;
            Vicinity = vicinity;
            SlRatio = slRatio;
            SmaPeriod = smaPeriod;
            MaxTradeLength = maxTradeLength;
            MaxProfitPerc = maxProfitPerc;
        }
    }
}
