namespace ChartApp
{
    public class EvalResult
    {
        public double Fitness { get; }
        public int PatternsFound { get; }
        public double TotalReturnPerc { get; }

        public EvalResult(double fitness, int patternsFound, double totalReturnPerc)
        {
            Fitness = fitness;
            PatternsFound = patternsFound;
            TotalReturnPerc = totalReturnPerc;
        }
    }
}
