class Chromosome
{
    private double windowSize;
    private double windowShift;
    private double minLineLength;
    private double vicinity;
    private double slRatio;
    private double sma;
    private double tradeLength;
    private double maxProfitPerc;
    private double minTouchCount;

    public Chromosome(double windowSize, double windowShift, double minLineLength, 
                      double vicinity, double slRatio, double sma, double tradeLength, double maxProfitPerc, double minTouchCount) {
        
        this.windowSize = windowSize;
        this.windowShift = windowShift;
        this.minLineLength = minLineLength;
        this.vicinity = vicinity;
        this.slRatio = slRatio;
        this.sma = sma;
        this.tradeLength = tradeLength;
        this.maxProfitPerc = maxProfitPerc;
        this.minTouchCount = minTouchCount;
    }


}