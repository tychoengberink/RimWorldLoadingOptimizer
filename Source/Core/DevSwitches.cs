using System;
using System.Globalization;
using System.Linq;

namespace RimWorldLoadingOptimizer
{
    public static class DevSwitches
    {
        public static readonly bool AllOff = On("RLO_ALL_OFF");
        public static readonly bool Verify = On("RLO_VERIFY");
        public static readonly bool SelfCheck = On("RLO_SELF_CHECK");
        public static readonly bool IgnoreFingerprints = On("RLO_IGNORE_FINGERPRINTS");
        public static readonly bool Profile = On("RLO_PROFILE");
        public static readonly bool HeapStats = On("RLO_HEAP_STATS");
        public static readonly bool ObjectStats = On("RLO_OBJECT_STATS");
        public static readonly bool StringStats = On("RLO_STRING_STATS");
        public static readonly bool XmlStats = On("RLO_XML_STATS");
        public static readonly bool NoAtlasCache = On("RLO_NO_ATLAS_CACHE");
        public static readonly bool NoAtlasFlush = On("RLO_NO_ATLAS_FLUSH");
        public static readonly bool NoAtlasPacking = On("RLO_NO_ATLAS_PACKING");
        public static readonly bool NoMeshPool = On("RLO_NO_MESH_POOL");
        public static readonly bool NoBoost = On("RLO_NO_BOOST");
        public static readonly bool NoPrefetch = On("RLO_NO_PREFETCH");
        public static readonly bool GraphicsProfile = On("RLO_GRAPHICS_PROFILE");
        public static readonly bool ThreadTimeline = On("RLO_THREAD_TIMELINE");
        public static readonly bool FilterDigest = On("RLO_FILTER_DIGEST");
        public static readonly bool NoFilterLoops = On("RLO_NO_FILTER_LOOPS");
        public static readonly bool ConstructorTiming = On("RLO_CONSTRUCTOR_TIMING");
        public static readonly bool LateModSupportVerify = On("RLO_LATE_SUPPORT_VERIFY");
        public static readonly string[] LateModSupportOff = List("RLO_LATE_SUPPORT_OFF").Select(s => s.ToLowerInvariant()).ToArray();
        public static readonly string[] ModConstructorSpeedupsOff = List("RLO_CONSTRUCTOR_FIXES_OFF");
        public static readonly float StartupFrameSeconds =
            float.TryParse(Environment.GetEnvironmentVariable("RLO_STARTUP_FRAME"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0.1f ? f : 0.25f;

        private static bool On(string name) => Environment.GetEnvironmentVariable(name) == "1";

        private static string[] List(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Split(',').Select(s => s.Trim()).ToArray();
    }
}
