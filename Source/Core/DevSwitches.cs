using System;
using System.Globalization;
using System.Linq;

namespace RimWorldLoadingOptimizer
{
    public static class DevSwitches
    {
        public static readonly bool AllOff = On("LEANMEMORY_ALLOFF");
        public static readonly bool Verify = On("LEANMEMORY_VERIFY");
        public static readonly bool SelfCheck = On("LEANMEMORY_SELFCHECK");
        public static readonly bool IgnoreFingerprints = On("LEANMEMORY_IGNORE_FINGERPRINTS");
        public static readonly bool Profile = On("LEANMEMORY_PROFILE");
        public static readonly bool HeapStats = On("LEANMEMORY_HEAPSTATS");
        public static readonly bool ObjectStats = On("LEANMEMORY_OBJECTSTATS");
        public static readonly bool StringStats = On("LEANMEMORY_STRINGSTATS");
        public static readonly bool XmlStats = On("LEANMEMORY_XMLSTATS");
        public static readonly bool NoAtlasCache = On("LEANMEMORY_NOATLASCACHE");
        public static readonly bool NoAtlasFlush = On("LEANMEMORY_NOATLASFLUSH");
        public static readonly bool NoAtlasPacking = On("LEANMEMORY_NOATLASPACKING");
        public static readonly bool NoMeshPool = On("LEANMEMORY_NOMESHPOOL");
        public static readonly bool NoBoost = On("LEANMEMORY_NOBOOST");
        public static readonly bool NoPrefetch = On("LEANMEMORY_NOPREFETCH");
        public static readonly bool GraphicsProfile = On("RLO_GRAPHICS_PROFILE");
        public static readonly bool ThreadTimeline = On("RLO_THREAD_TIMELINE");
        public static readonly bool FilterDigest = On("RLO_FILTER_DIGEST");
        public static readonly bool NoFilterLoops = On("RLO_NO_FILTER_LOOPS");
        public static readonly bool ConstructorTiming = On("CTORS2_TIMING");
        public static readonly bool LateModSupportVerify = On("CTORS2_VERIFY");
        public static readonly string[] LateModSupportOff = List("CTORS2_OFF").Select(s => s.ToLowerInvariant()).ToArray();
        public static readonly string[] ModConstructorSpeedupsOff = List("PREXML2_OFF");
        public static readonly float StartupFrameSeconds =
            float.TryParse(Environment.GetEnvironmentVariable("MAINPHASES_FRAME"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0.1f ? f : 0.25f;

        private static bool On(string name) => Environment.GetEnvironmentVariable(name) == "1";

        private static string[] List(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Split(',').Select(s => s.Trim()).ToArray();
    }
}
