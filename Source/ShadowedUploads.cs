#if RW17
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Linq;
using HarmonyLib;
using Verse;
using CoreTexture = Waylith.Rendering.Texture2D;
using Texture2D = UnityEngine.Texture2D;

namespace RimWorldLoadingOptimizer
{
    public static class ShadowedUploads
    {
        private static bool installed;
        private static readonly Dictionary<CoreTexture, long> destroyed = new Dictionary<CoreTexture, long>(ReferenceComparer<CoreTexture>.Instance);
        private static readonly Dictionary<CoreTexture, long> kept = new Dictionary<CoreTexture, long>(ReferenceComparer<CoreTexture>.Instance);
        private static HashSet<CoreTexture> winners;
        private static int skippedDestroyed, skippedKept, refused, mismatches, checkedKept;
        private static long skippedBytes;
        private static readonly List<CoreTexture> skippedKeptCores = new List<CoreTexture>();

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.LeavesSourceEvicted), new[] { typeof(CoreTexture) });
            var bake = AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases));
            var endDeferred = AccessTools.Method(typeof(CoreTexture), nameof(CoreTexture.EndDeferredLoading), new[] { typeof(Func<CoreTexture, bool>) });
            if (target == null || bake == null || endDeferred == null)
            {
                VersionCheck.Skip("Shadowed textures not uploaded", "a method it needs no longer exists");
                return;
            }
            if (!VersionCheck.Pattern("Shadowed textures not uploaded", bake,
                    code => VersionCheck.Calls(code, endDeferred) == 1 && code.Count(i => i.opcode == OpCodes.Ldftn && Equals(i.operand, target)) == 1,
                    "hands LeavesSourceEvicted to Texture2D.EndDeferredLoading to choose the textures left to load at first use"))
            {
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ShadowedUploads), nameof(Postfix)));
            installed = true;
            LeanFixes.Reports.Add(Stats);
            if (RLOMod.Verify)
            {
                MainMenu.Then(VerifyKept);
            }
        }

        public static void Note(Texture2D texture, bool wasDestroyed, long bytes)
        {
            var core = installed ? texture?.CoreTexture : null;
            if (core != null)
            {
                (wasDestroyed ? destroyed : kept)[core] = bytes;
            }
        }

        public static void Postfix(CoreTexture texture, ref bool __result)
        {
            if (__result || texture == null)
            {
                return;
            }
            var isDestroyed = destroyed.TryGetValue(texture, out var bytes);
            if (!isDestroyed && !kept.TryGetValue(texture, out bytes))
            {
                return;
            }
            if ((winners ??= Winners()).Contains(texture))
            {
                refused++;
                if (RLOMod.Verify)
                {
                    mismatches++;
                }
                return;
            }
            __result = true;
            skippedBytes += bytes;
            if (isDestroyed)
            {
                skippedDestroyed++;
                return;
            }
            skippedKept++;
            if (RLOMod.Verify)
            {
                skippedKeptCores.Add(texture);
            }
        }

        private static HashSet<CoreTexture> Winners()
        {
            var seen = new HashSet<string>();
            var set = new HashSet<CoreTexture>(ReferenceComparer<CoreTexture>.Instance);
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                foreach (var entry in mods[i].GetContentHolder<Texture2D>().contentList)
                {
                    if (seen.Add(entry.Key) && entry.Value?.CoreTexture != null)
                    {
                        set.Add(entry.Value.CoreTexture);
                    }
                }
            }
            return set;
        }

        private static void VerifyKept()
        {
            foreach (var core in skippedKeptCores)
            {
                checkedKept++;
                if (!core.EnsureResident())
                {
                    mismatches++;
                }
            }
            Log.Message($"[RLO] shadowed textures not uploaded, verify at the main menu: {checkedKept} kept textures loaded back on demand, {mismatches} verify mismatches");
        }

        public static string Stats() =>
            $"shadowed textures not uploaded at load end: {skippedDestroyed} released and {skippedKept} kept overridden textures (~{skippedBytes >> 20} MB), {refused} still in use and uploaded as usual"
            + (RLOMod.Verify ? $", {mismatches} verify mismatches so far" : "");
    }
}
#endif
