using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class RLOLog
    {
        private const string Prefix = "[RLO] ";

        public static void Message(string text) => Log.Message(Prefix + text);

        public static void Warning(string text) => Log.Warning(Prefix + text);

        public static void Error(string text) => Log.Error(Prefix + text);
    }
}
