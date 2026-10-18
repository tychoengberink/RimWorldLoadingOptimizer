#if !RW17
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasPacking
    {
        public static int Lookups;
        public static int Fallbacks;
        public static int VerifyMismatches;
        private static readonly Stopwatch time = new Stopwatch();
        private static List<Texture2D> indexed;
        private static Dictionary<Texture2D, int> positions;

        public static void Install(Harmony harmony)
        {
            var pack = AccessTools.Method(typeof(StaticTextureAtlas), "CalcRectsForAtlasNew");
            var indexOf = AccessTools.Method(typeof(List<Texture2D>), nameof(List<Texture2D>.IndexOf), new[] { typeof(Texture2D) });
            if (!VersionCheck.Pattern("Atlas packing lookups", pack, code => VersionCheck.Calls(code, indexOf) == 1, "looks up each texture with one List.IndexOf call"))
            {
                return;
            }
            harmony.Patch(pack, transpiler: new HarmonyMethod(typeof(AtlasPacking), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var indexOf = AccessTools.Method(typeof(List<Texture2D>), nameof(List<Texture2D>.IndexOf), new[] { typeof(Texture2D) });
            return IlScan.Replace(instructions, indexOf, AccessTools.Method(typeof(AtlasPacking), nameof(IndexOf)));
        }

        public static int IndexOf(List<Texture2D> list, Texture2D texture)
        {
            time.Start();
            try
            {
                if (texture == null)
                {
                    Fallbacks++;
                    return list.IndexOf(texture);
                }
                if (!ReferenceEquals(list, indexed) || positions.Count > list.Count)
                {
                    indexed = list;
                    positions = new Dictionary<Texture2D, int>(list.Count, ReferenceComparer<Texture2D>.Instance);
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (!positions.ContainsKey(list[i]))
                        {
                            positions.Add(list[i], i);
                        }
                    }
                }
                Lookups++;
                var position = positions.TryGetValue(texture, out var found) ? found : -1;
                if (RLOMod.Verify && position != list.IndexOf(texture))
                {
                    VerifyMismatches++;
                    return list.IndexOf(texture);
                }
                return position;
            }
            finally
            {
                time.Stop();
            }
        }

        public static string Stats() => $"atlas packing lookups: {Lookups} by reference in {time.Elapsed.TotalSeconds:F2} s, {Fallbacks} left to the original" + (RLOMod.Verify ? $", {VerifyMismatches} verify mismatches" : "");
    }
}
#endif
