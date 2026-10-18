#if !RW17
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasFlush
    {
        public static int Flushes;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.Bake)), postfix: new HarmonyMethod(typeof(AtlasFlush), nameof(Postfix)));
        }

        public static void Postfix()
        {
            GL.Flush();
            Flushes++;
        }

        public static string Stats() => $"atlas flush: graphics commands sent after {Flushes} atlas bakes";
    }
}
#endif
