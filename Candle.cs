    class Candle
    {
        public int Index { get; set; }
        public string Date { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }
        public double BodyHi { get; set; }
        public double BodyLo { get; set; }

        public Candle(int index, string date, double low, double high, double open, double close, double volume)
        {
            Index = index;
            Date = date;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
            BodyHi = 0;
            BodyLo = 0;
        }

        public void SetBodyHi(double open, double close)
        {
            BodyHi = Math.Max(close, open);
        }

        public void SetBodyLo(double open, double close)
        {
            BodyLo = Math.Min(close, open);
        }
    }