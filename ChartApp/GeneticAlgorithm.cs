using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChartApp
{
    public class GeneticAlgorithm
    {
        private readonly Func<FitnessEval> evaluatorFactory;
        private readonly Random rng;

        // GA hyperparameters
        public int PopulationSize { get; set; } = 100;
        public int Generations { get; set; } = 100;
        public int TournamentSize { get; set; } = 3;
        public double CrossoverRate { get; set; } = 0.5; // per-gene swap probability
        public double MutationRate { get; set; } = 0.15; // probability of mutating each gene
        public int MaxParallelism { get; set; } = 10;
        public int ElitismCount { get; set; } = 2; // top individuals carried forward unchanged each generation

        // Gene bounds: [min, max] for each GA parameter
        // Order: WindowSize, WindowShift, MinLineLength, SmaPeriod, MinGradientDiff
        // MinGradientDiff replaces the old MaxGradientDiff ceiling as of
        // D-0026: E-0008/E-0009 found evolving an upper bound on how much the
        // support/resistance gradients may differ was actively harmful (best
        // fitness rose when the cap was removed, and the best trade in
        // E-0009's sample needed a bigger difference than the gene's own
        // ceiling allowed). This gene now evolves a *minimum* required
        // difference instead -- rejecting near-parallel, weak-wedge patterns
        // -- reusing the same bounds/sigma infrastructure the ceiling used.
        // The "must not diverge" directional check in FitnessEval is separate
        // and untouched by this change.
        private readonly (int min, int max) windowSizeBounds = (100, 500);
        private readonly (int min, int max) windowShiftBounds = (1, 50);
        private readonly (int min, int max) minLineLengthBounds = (10, 200);
        private readonly (int min, int max) smaPeriodBounds = (5, 200);
        private readonly (double min, double max) minGradientDiffBounds = (0.1, 50.0);

        // Gaussian mutation standard deviations (per gene)
        private readonly double windowSizeSigma = 10.0;
        private readonly double windowShiftSigma = 5.0;
        private readonly double minLineLengthSigma = 10.0;
        private readonly double smaPeriodSigma = 15.0;
        private readonly double minGradientDiffSigma = 3.0;

        public GeneticAlgorithm(Func<FitnessEval> evaluatorFactory, int? seed = null)
        {
            this.evaluatorFactory = evaluatorFactory;
            this.rng = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public int EarlyStopGenerations { get; set; } = 10; // Stop if no improvement for this many generations

        public (Chromosome best, EvalResult bestResult) Run()
        {
            // Initialise population
            var population = InitialisePopulation();
            Console.WriteLine("Evaluating initial population...");
            var results = EvaluatePopulation(population, 0);

            Chromosome bestChromosome = population[0];
            EvalResult bestResult = results[0];
            int stagnantGenerations = 0;

            for (int gen = 0; gen < Generations; gen++)
            {
                double previousBest = bestResult.Fitness;

                // Find best in current generation
                for (int i = 0; i < population.Count; i++)
                {
                    if (results[i].Fitness > bestResult.Fitness)
                    {
                        bestResult = results[i];
                        bestChromosome = population[i];
                    }
                }

                // Check for improvement
                if (bestResult.Fitness > previousBest)
                    stagnantGenerations = 0;
                else
                    stagnantGenerations++;

                var diversity = CalculateDiversity(population);

                Console.WriteLine($"Gen {gen + 1}/{Generations} | Best Fitness: {bestResult.Fitness:F4}% | Patterns: {bestResult.PatternsFound} | WS: {bestChromosome.WindowSize} MLL: {bestChromosome.MinLineLength} MinGD: {bestChromosome.MinGradientDiff:F2} | Stagnant: {stagnantGenerations} | Population Diversity: {diversity}");

                // Early stopping
                if (stagnantGenerations >= EarlyStopGenerations)
                {
                    Console.WriteLine($"Early stopping: no improvement for {EarlyStopGenerations} generations.");
                    break;
                }

                // Log every individual this generation
                for (int i = 0; i < population.Count; i++)
                {
                    int passNumber = gen * PopulationSize + i + 1;
                    ResultLogger.Log(population[i], results[i], passNumber);
                }

                // Elitism: carry the top ElitismCount individuals forward
                // unchanged, so the best solutions found cannot be lost between
                // generations. Chromosome is immutable, so reusing the same
                // instances is safe. Their fitness is already known -- no need
                // to re-evaluate them.
                var eliteIndices = Enumerable.Range(0, population.Count)
                    .OrderByDescending(i => results[i].Fitness)
                    .Take(ElitismCount)
                    .ToList();

                var nextGen = new List<Chromosome>();
                var eliteResults = new List<EvalResult>();
                foreach (int idx in eliteIndices)
                {
                    nextGen.Add(population[idx]);
                    eliteResults.Add(results[idx]);
                }

                // Fill the remaining slots via tournament selection + crossover + mutation
                var offspring = new List<Chromosome>();
                while (nextGen.Count + offspring.Count < PopulationSize)
                {
                    Chromosome parent1 = TournamentSelect(population, results);
                    Chromosome parent2 = TournamentSelect(population, results);

                    Chromosome child = UniformCrossover(parent1, parent2);
                    child = GaussianMutate(child);

                    offspring.Add(child);
                }

                var offspringResults = EvaluatePopulation(offspring, gen + 1);

                population = nextGen.Concat(offspring).ToList();
                results = eliteResults.Concat(offspringResults).ToList();
            }

            // Final check
            for (int i = 0; i < population.Count; i++)
            {
                if (results[i].Fitness > bestResult.Fitness)
                {
                    bestResult = results[i];
                    bestChromosome = population[i];
                }
            }

            return (bestChromosome, bestResult);
        }

        //Calculate diversity of the population
        public double CalculateDiversity(List<Chromosome> population)
        {
            if (population == null || population.Count == 0)
                return 0.0;

            // Calculate mean chromosome
            double meanWindowSize = population.Average(c => c.WindowSize);
            double meanWindowShift = population.Average(c => c.WindowShift);
            double meanMinLineLength = population.Average(c => c.MinLineLength);
            double meanSmaPeriod = population.Average(c => c.SmaPeriod);
            double meanMinGradientDiff = population.Average(c => c.MinGradientDiff);

            // Calculate variance for each gene
            double varianceWindowSize = population.Average(c => Math.Pow(c.WindowSize - meanWindowSize, 2));
            double varianceWindowShift = population.Average(c => Math.Pow(c.WindowShift - meanWindowShift, 2));
            double varianceMinLineLength = population.Average(c => Math.Pow(c.MinLineLength - meanMinLineLength, 2));
            double varianceSmaPeriod = population.Average(c => Math.Pow(c.SmaPeriod - meanSmaPeriod, 2));
            double varianceMinGradientDiff = population.Average(c => Math.Pow(c.MinGradientDiff - meanMinGradientDiff, 2));

            // Standard deviation
            double stdWindowSize = Math.Sqrt(varianceWindowSize);
            double stdWindowShift = Math.Sqrt(varianceWindowShift);
            double stdMinLineLength = Math.Sqrt(varianceMinLineLength);
            double stdSmaPeriod = Math.Sqrt(varianceSmaPeriod);
            double stdMinGradientDiff = Math.Sqrt(varianceMinGradientDiff);

            // Normalise standard deviations to [0, 1] range using gene bounds
            double normStdWindowSize = stdWindowSize / (windowSizeBounds.max - windowSizeBounds.min);
            double normStdWindowShift = stdWindowShift / (windowShiftBounds.max - windowShiftBounds.min);
            double normStdMinLineLength = stdMinLineLength / (minLineLengthBounds.max - minLineLengthBounds.min);
            double normStdSmaPeriod = stdSmaPeriod / (smaPeriodBounds.max - smaPeriodBounds.min);
            double normStdMinGradientDiff = stdMinGradientDiff / (minGradientDiffBounds.max - minGradientDiffBounds.min);

            // Average normalised standard deviation as diversity measure, over
            // all five genes -- MinGradientDiff evolves like the other four
            // now that D-0026 removed the fixed-value ablation machinery.
            double diversity = (normStdWindowSize + normStdWindowShift + normStdMinLineLength +
                                normStdSmaPeriod + normStdMinGradientDiff) / 5.0;

            return diversity;

        }




        private List<Chromosome> InitialisePopulation()
        {
            var pop = new List<Chromosome>(PopulationSize);
            for (int i = 0; i < PopulationSize; i++)
            {
                int ws = rng.Next(windowSizeBounds.min, windowSizeBounds.max + 1);
                int wsh = rng.Next(windowShiftBounds.min, windowShiftBounds.max + 1);
                int mll = rng.Next(minLineLengthBounds.min, minLineLengthBounds.max + 1);
                int sma = rng.Next(smaPeriodBounds.min, smaPeriodBounds.max + 1);
                double mingd = minGradientDiffBounds.min + rng.NextDouble() * (minGradientDiffBounds.max - minGradientDiffBounds.min);

                pop.Add(new Chromosome(ws, wsh, mll, sma, mingd));
            }
            return pop;
        }


        public int EvalTimeoutSeconds { get; set; } = 60;

        private List<EvalResult> EvaluatePopulation(List<Chromosome> population, int generation = -1)
        {
            var results = new EvalResult[population.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism };
            int completed = 0;
            int timedOut = 0;

            Parallel.For(0, population.Count, options, i =>
            {
                // Each thread gets its own evaluator instance (mutable state is per-instance)
                var localEvaluator = evaluatorFactory();

                EvalResult? result = null;
                var cts = new System.Threading.CancellationTokenSource();
                var task = Task.Run(() => localEvaluator.Evaluate(population[i]), cts.Token);

                if (task.Wait(TimeSpan.FromSeconds(EvalTimeoutSeconds)))
                {
                    result = task.Result;
                }
                else
                {
                    // Timed out — assign zero fitness
                    cts.Cancel();
                    result = new EvalResult(0, 0, 0);
                    System.Threading.Interlocked.Increment(ref timedOut);
                }

                results[i] = result;

                int done = System.Threading.Interlocked.Increment(ref completed);
                if (generation >= 0)
                {
                    Console.Write($"\r  Evaluating Gen {generation + 1}: {done}/{population.Count} complete ({timedOut} timed out)");
                }
            });

            if (generation >= 0)
                Console.WriteLine();

            return results.ToList();
        }

        // =================================================================
        // Tournament Selection
        // =================================================================

        private Chromosome TournamentSelect(List<Chromosome> population, List<EvalResult> results)
        {
            Chromosome best = population[0];
            double bestFitness = double.MinValue;

            for (int i = 0; i < TournamentSize; i++)
            {
                int idx = rng.Next(population.Count);
                if (results[idx].Fitness > bestFitness)
                {
                    bestFitness = results[idx].Fitness;
                    best = population[idx];
                }
            }

            return best;
        }

        // =================================================================
        // Uniform Crossover (per-gene swap probability = CrossoverRate)
        // =================================================================

        private Chromosome UniformCrossover(Chromosome p1, Chromosome p2)
        {
            int ws = rng.NextDouble() < CrossoverRate ? p2.WindowSize : p1.WindowSize;
            int wsh = rng.NextDouble() < CrossoverRate ? p2.WindowShift : p1.WindowShift;
            int mll = rng.NextDouble() < CrossoverRate ? p2.MinLineLength : p1.MinLineLength;
            int sma = rng.NextDouble() < CrossoverRate ? p2.SmaPeriod : p1.SmaPeriod;
            double mingd = rng.NextDouble() < CrossoverRate ? p2.MinGradientDiff : p1.MinGradientDiff;

            return new Chromosome(ws, wsh, mll, sma, mingd);
        }

        // =================================================================
        // Gaussian Mutation
        // =================================================================

        private Chromosome GaussianMutate(Chromosome c)
        {
            int ws = c.WindowSize;
            int wsh = c.WindowShift;
            int mll = c.MinLineLength;
            int sma = c.SmaPeriod;
            double mingd = c.MinGradientDiff;

            if (rng.NextDouble() < MutationRate)
                ws = Clamp((int)(ws + GaussianSample() * windowSizeSigma), windowSizeBounds.min, windowSizeBounds.max);

            if (rng.NextDouble() < MutationRate)
                wsh = Clamp((int)(wsh + GaussianSample() * windowShiftSigma), windowShiftBounds.min, windowShiftBounds.max);

            if (rng.NextDouble() < MutationRate)
                mll = Clamp((int)(mll + GaussianSample() * minLineLengthSigma), minLineLengthBounds.min, minLineLengthBounds.max);

            if (rng.NextDouble() < MutationRate)
                sma = Clamp((int)(sma + GaussianSample() * smaPeriodSigma), smaPeriodBounds.min, smaPeriodBounds.max);

            if (rng.NextDouble() < MutationRate)
                mingd = ClampDouble(mingd + GaussianSample() * minGradientDiffSigma, minGradientDiffBounds.min, minGradientDiffBounds.max);

            return new Chromosome(ws, wsh, mll, sma, mingd);
        }

        // =================================================================
        // Helpers
        // =================================================================

        private double GaussianSample()
        {
            // Box-Muller transform
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double ClampDouble(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
