using System.Threading;

namespace RimWorldLoadingOptimizer
{
    public sealed class MismatchLog
    {
        private const int Logged = 10;

        private readonly string feature;
        private int count;

        public MismatchLog(string feature)
        {
            this.feature = feature;
        }

        public int Count => Volatile.Read(ref count);

        public void Report(string what)
        {
            if (Interlocked.Increment(ref count) <= Logged)
            {
                RLOLog.Warning($"{feature} mismatch: {what}");
            }
        }

        public override string ToString() => $"{Count} mismatches";
    }
}
