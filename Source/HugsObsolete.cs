using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class HugsObsolete
    {
        private static readonly Dictionary<string, string[]> known = new Dictionary<string, string[]>
        {
            ["EnumerateObsoleteMethodPatchOwners"] = new[] { "A73FC1AFB724BD5B", "FE32CBB1003C880B" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_0"] = new[] { "87A7B164DD684927", "03B0E3D210A7FEDF" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_1"] = new[] { "3E9E4564BC2DBFB9", "155BE7DAC14ABC68" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_2"] = new[] { "701F49099DED54B8", "66688DF7E3A25C52" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_3"] = new[] { "8D2CA4009E4E46A4", "C6B8C9CE8CFBCCBD" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_4"] = new[] { "D4CA2D2612221583", "28A1906ED02E3958" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_5"] = new[] { "2F82244B887645FF", "E398EF44CBEFD1ED" },
            ["<EnumerateObsoleteMethodPatchOwners>b__9_6"] = new[] { "E065E7CAB477454B", "60224AAC2F770420" },
        };

        private static Func<Patches, bool> hasActivePatches;
        private static MethodInfo original;
        private static bool installed;
        private static bool bypass;
        private static int calls;
        private static int patched;
        private static int candidates;
        private static int mismatches;
        private static long ticks;

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("HugsLib.Utils.HarmonyUtility");
            if (type == null)
            {
                return;
            }
            original = AccessTools.Method(type, "EnumerateObsoleteMethodPatchOwners");
            if (original == null || !Fingerprint.Matches("hugslib", LeanFixes.WithLambdas(original, known).ToArray()))
            {
                return;
            }
            hasActivePatches = AccessTools.MethodDelegate<Func<Patches, bool>>(AccessTools.Method(type, "HasActivePatches"));
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(HugsObsolete), nameof(Prefix)));
            var log = AccessTools.Method(type, "LogObsoleteMethodPatchErrors");
            harmony.Patch(log, prefix: new HarmonyMethod(typeof(HugsObsolete), nameof(Start)), postfix: new HarmonyMethod(typeof(HugsObsolete), nameof(Stop)));
            installed = true;
        }

        public static void Start(out long __state) => __state = Stopwatch.GetTimestamp();

        public static void Stop(long __state) => ticks += Stopwatch.GetTimestamp() - __state;

        public static bool Prefix(ref IEnumerable<(string owner, IEnumerable<MethodBase> methods)> __result)
        {
            if (bypass)
            {
                return true;
            }
            calls++;
            __result = Fast();
            if (RLOMod.Verify)
            {
                var fast = Materialize(__result);
                bypass = true;
                List<(string owner, List<MethodBase> methods)> slow;
                try
                {
                    slow = Materialize((IEnumerable<(string owner, IEnumerable<MethodBase> methods)>)original.Invoke(null, null));
                }
                finally
                {
                    bypass = false;
                }
                var same = fast.Count == slow.Count && fast.Zip(slow, (a, b) => a.owner == b.owner && a.methods.SequenceEqual(b.methods)).All(x => x);
                if (!same)
                {
                    mismatches++;
                    Log.Warning($"[RLO] hugslib verify mismatch: fast {fast.Count} owners, original {slow.Count} owners");
                }
                __result = fast.Select(p => (p.owner, (IEnumerable<MethodBase>)p.methods)).ToList();
            }
            return false;
        }

        private static List<(string owner, List<MethodBase> methods)> Materialize(IEnumerable<(string owner, IEnumerable<MethodBase> methods)> source) =>
            source.Select(p => (p.owner, p.methods.ToList())).ToList();

        private static bool MayBeObsolete(MethodBase method)
        {
            patched++;
            try
            {
                if (!method.HasAttribute<ObsoleteAttribute>())
                {
                    return false;
                }
            }
            catch (Exception)
            {
            }
            candidates++;
            return true;
        }

        private static IEnumerable<(string owner, IEnumerable<MethodBase> methods)> Fast()
        {
            return from method in Harmony.GetAllPatchedMethods()
                   where MayBeObsolete(method)
                   select (method, info: Harmony.GetPatchInfo(method)) into pair
                   where hasActivePatches(pair.info) && pair.method.HasAttribute<ObsoleteAttribute>()
                   from owner in pair.info.Owners
                   select (owner, pair.method) into pair
                   group pair.method by pair.owner into grp
                   select (grp.Key, (IEnumerable<MethodBase>)grp);
        }

        public static string Stats() => !installed ? "hugslib: not active" : $"hugslib: obsolete patch check {LeanFixes.Seconds(ticks):F2} s, calls {calls}, patched methods {patched}, obsolete candidates {candidates}" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
    }
}
