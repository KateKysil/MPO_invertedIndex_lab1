using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ParallelInvertedIndex
{
    // Реалізація А
    public struct CounterUnpadded
    {
        public long Value;
    }

    // Реалізація B
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct CounterPadded
    {
        [FieldOffset(0)]
        public long Value;
    }

    public static class FalseSharingBench
    {
        private const int OperationsPerThread = 10_000_000;

        public static void Run()
        {
            Console.WriteLine("\nДослідження хибного розділення:");
            int[] threadCounts = { 1, 2, 4, 8 };

            Console.WriteLine("Threads\tUnpadded (A) ms\tPadded (B) ms\tSpeedup / Ratio");

            foreach (int P in threadCounts)
            {
                double timeA = MeasureA(P);
                double timeB = MeasureB(P);
                Console.WriteLine($"{P}\t{timeA,12:F2}\t{timeB,12:F2}\t{(timeA / timeB),12:F2}x");
            }
        }

        private static double MeasureA(int P)
        {
            var counters = new CounterUnpadded[P];

            // Warmup
            RunThreadA(counters, P);

            var sw = Stopwatch.StartNew();
            RunThreadA(counters, P);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static void RunThreadA(CounterUnpadded[] counters, int P)
        {
            Parallel.For(0, P, new ParallelOptions { MaxDegreeOfParallelism = P }, threadIdx =>
            {
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    counters[threadIdx].Value++;
                }
            });
        }

        private static double MeasureB(int P)
        {
            var counters = new CounterPadded[P];

            // Warmup
            RunThreadB(counters, P);

            var sw = Stopwatch.StartNew();
            RunThreadB(counters, P);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static void RunThreadB(CounterPadded[] counters, int P)
        {
            Parallel.For(0, P, new ParallelOptions { MaxDegreeOfParallelism = P }, threadIdx =>
            {
                for (int i = 0; i < OperationsPerThread; i++)
                {
                    counters[threadIdx].Value++;
                }
            });
        }
    }
}