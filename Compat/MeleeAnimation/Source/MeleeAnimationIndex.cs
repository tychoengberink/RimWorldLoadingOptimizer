using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AM.Retexture;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer.MeleeAnimation
{
    [StaticConstructorOnStartup]
    public static class MeleeAnimationIndex
    {
        private const string TypeName = "AM.Retexture.RetextureUtility";
        private static readonly bool verify = Environment.GetEnvironmentVariable("LEANMEMORY_VERIFY") == "1";
        private static readonly List<(ModContentPack, Texture2D, string)> none = new List<(ModContentPack, Texture2D, string)>();
        private static Func<IEnumerable<(ModContentPack, Texture2D, string)>> original;
        private static Dictionary<string, List<(ModContentPack, Texture2D, string)>> index;
        private static long signature;
        private static bool bypass;
        [ThreadStatic] private static string path;
        private static int lookups, builds, entries, callChecks, reportChecks, mismatches;
        private static double buildMs;

        static MeleeAnimationIndex()
        {
            if (RLOMod.Settings?.compatFixes == false || AccessTools.TypeByName(TypeName) == null)
            {
                return;
            }
            try
            {
                Install();
            }
            catch (Exception e)
            {
                Log.Warning("[RLO] Melee Animation: not installed: " + e);
            }
        }

        private static void Install()
        {
            var type = typeof(RetextureUtility);
            original = AccessTools.MethodDelegate<Func<IEnumerable<(ModContentPack, Texture2D, string)>>>(AccessTools.Method(type, "GetAllModTextures"));
            var harmony = new Harmony("aruclya.rimworldloadingoptimizer.meleeanimation");
            harmony.Patch(AccessTools.Method(type, nameof(RetextureUtility.GetTextureReport)), transpiler: new HarmonyMethod(typeof(MeleeAnimationIndex), nameof(Transpiler)));
            harmony.Patch(AccessTools.Method(type, nameof(RetextureUtility.PreCacheAllTextureReports)), postfix: new HarmonyMethod(typeof(MeleeAnimationIndex), nameof(PreCachePostfix)));
            Log.Message("[RLO] Melee Animation: retexture scan index installed" + (verify ? " (verify mode)" : ""));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var resolve = AccessTools.Method(typeof(RetextureUtility), "ResolveTexturePath");
            var all = AccessTools.Method(typeof(RetextureUtility), "GetAllModTextures");
            var list = instructions.ToList();
            var resolveAt = list.FindAll(i => i.Calls(resolve));
            var allAt = list.FindAll(i => i.Calls(all));
            if (resolveAt.Count != 1 || allAt.Count != 1)
            {
                Log.Warning($"[RLO] Melee Animation: unexpected GetTextureReport IL ({resolveAt.Count} resolve, {allAt.Count} scan calls); left unchanged");
                return list;
            }
            allAt[0].operand = AccessTools.Method(typeof(MeleeAnimationIndex), nameof(Lookup));
            list.Insert(list.IndexOf(resolveAt[0]) + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MeleeAnimationIndex), nameof(Remember))));
            return list;
        }

        public static string Remember(string texturePath)
        {
            path = texturePath;
            return texturePath;
        }

        public static IEnumerable<(ModContentPack, Texture2D, string)> Lookup()
        {
            if (bypass)
            {
                return original();
            }
            lookups++;
            var current = Signature();
            if (index == null || current != signature)
            {
                Build(current);
            }
            var key = path;
            if (key == null || !index.TryGetValue(key, out var found))
            {
                found = none;
            }
            if (verify)
            {
                CheckCall(key, found);
            }
            return found;
        }

        private static long Signature()
        {
            var mods = LoadedModManager.RunningModsListForReading;
            long current = mods.Count;
            foreach (var mod in mods)
            {
                current = current * 31 + (mod.GetContentHolder<Texture2D>()?.contentList?.Count ?? -1);
                current = current * 31 + (mod.assetBundles?.loadedAssetBundles?.Count ?? -1);
            }
            return current;
        }

        private static void Build(long current)
        {
            var watch = Stopwatch.StartNew();
            var built = new Dictionary<string, List<(ModContentPack, Texture2D, string)>>();
            entries = 0;
            foreach (var entry in original())
            {
                if (!built.TryGetValue(entry.Item3, out var list))
                {
                    list = new List<(ModContentPack, Texture2D, string)>(1);
                    built[entry.Item3] = list;
                }
                list.Add(entry);
                entries++;
            }
            index = built;
            signature = current;
            builds++;
            buildMs += watch.Elapsed.TotalMilliseconds;
        }

        private static void CheckCall(string key, List<(ModContentPack, Texture2D, string)> found)
        {
            callChecks++;
            var expected = original().Where(e => e.Item3 == key).ToList();
            var same = expected.Count == found.Count;
            for (var i = 0; same && i < expected.Count; i++)
            {
                same = ReferenceEquals(expected[i].Item1, found[i].Item1) && ReferenceEquals(expected[i].Item2, found[i].Item2) && expected[i].Item3 == found[i].Item3;
            }
            if (!same)
            {
                Mismatch($"scan for '{key}': original {expected.Count} entries, index {found.Count}");
            }
        }

        private static void PreCachePostfix()
        {
            if (verify)
            {
                VerifyReports();
            }
            Report("after precache");
            LongEventHandler.ExecuteWhenFinished(() => Report("after Melee Animation load event"));
        }

        private static void VerifyReports()
        {
            foreach (var weapon in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => RetextureUtility.IsMeleeWeapon(d)))
            {
                foreach (var full in new[] { false, true })
                {
                    var fast = RetextureUtility.GetTextureReport(weapon, 0, full, false, false);
                    bypass = true;
                    ActiveTextureReport slow;
                    try
                    {
                        slow = RetextureUtility.GetTextureReport(weapon, 0, full, false, false);
                    }
                    finally
                    {
                        bypass = false;
                    }
                    reportChecks++;
                    if (!SameReport(fast, slow))
                    {
                        Mismatch($"report for {weapon.defName} full={full}: original '{Describe(slow)}', index '{Describe(fast)}'");
                    }
                }
            }
        }

        private static bool SameReport(ActiveTextureReport a, ActiveTextureReport b)
        {
            if (a.TexturePath != b.TexturePath || a.ErrorMessage != b.ErrorMessage || !ReferenceEquals(a.Weapon, b.Weapon)
                || !ReferenceEquals(a.ActiveTexture, b.ActiveTexture) || !ReferenceEquals(a.ActiveRetextureMod, b.ActiveRetextureMod))
            {
                return false;
            }
            if (a.AllRetextures == null || b.AllRetextures == null)
            {
                return a.AllRetextures == b.AllRetextures;
            }
            if (a.AllRetextures.Count != b.AllRetextures.Count)
            {
                return false;
            }
            for (var i = 0; i < a.AllRetextures.Count; i++)
            {
                if (!ReferenceEquals(a.AllRetextures[i].mod, b.AllRetextures[i].mod) || !ReferenceEquals(a.AllRetextures[i].texture, b.AllRetextures[i].texture))
                {
                    return false;
                }
            }
            return true;
        }

        private static string Describe(ActiveTextureReport r)
        {
            return $"{r.TexturePath} -> {r.ActiveRetextureMod?.PackageId} [{(r.AllRetextures == null ? "null" : string.Join(",", r.AllRetextures.Select(p => p.mod?.PackageId)))}] {r.ErrorMessage}";
        }

        private static void Mismatch(string message)
        {
            mismatches++;
            if (mismatches <= 20)
            {
                Log.Warning("[RLO] Melee Animation: verify mismatch: " + message);
            }
        }

        private static void Report(string when)
        {
            var verifyText = verify ? $"; verify: {callChecks} scans and {reportChecks} reports compared, {mismatches} mismatches" : "";
            Log.Message($"[RLO] Melee Animation: {when}: {lookups} indexed scans, {builds} index builds ({entries} textures, {index?.Count ?? 0} paths, {buildMs:F0} ms){verifyText}");
        }
    }
}
