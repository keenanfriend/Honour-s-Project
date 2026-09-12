using System;
using System.Collections.Generic;
using System.Linq;

namespace ChartApp
{
    /// <summary>
    /// Precomputed daily close prices with an O(1) rolling-average lookup.
    /// Built once and shared, read-only, across every chromosome evaluation --
    /// the closes themselves don't depend on any gene, only the period
    /// averaged over does, so the sort and prefix sums are done exactly once
    /// per run rather than once per evaluation.
    /// </summary>
    public class DailyCloseSeries
    {
        private readonly Dictionary<string, int> dateIndex;
        private readonly double[] prefixSums; // prefixSums[i] = sum of the first i closes, chronologically

        public DailyCloseSeries(Dictionary<string, double> dailyCloses)
        {
            var sortedDates = dailyCloses.Keys.OrderBy(d => d, StringComparer.Ordinal).ToArray();

            dateIndex = new Dictionary<string, int>();
            prefixSums = new double[sortedDates.Length + 1];

            for (int i = 0; i < sortedDates.Length; i++)
            {
                dateIndex[sortedDates[i]] = i;
                prefixSums[i + 1] = prefixSums[i] + dailyCloses[sortedDates[i]];
            }
        }

        /// <summary>
        /// The trailing <paramref name="period"/>-day simple moving average
        /// ending on and including <paramref name="date"/>, or null if the
        /// date is unknown or there isn't yet enough history for the
        /// requested period (in which case the caller should skip ahead,
        /// exactly as a missing value did before).
        /// </summary>
        public double? Sma(string date, int period)
        {
            if (!dateIndex.TryGetValue(date, out int idx))
                return null;

            int start = idx - period + 1;
            if (start < 0)
                return null;

            return (prefixSums[idx + 1] - prefixSums[start]) / period;
        }
    }
}
