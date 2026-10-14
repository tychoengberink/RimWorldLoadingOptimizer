using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class TextureLookup
    {
        private static readonly MethodInfo Original = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get));

        private struct Winner
        {
            public int Mod;
            public Texture2D Texture;
        }

        private static Dictionary<string, Winner> winners;
        private static readonly HashSet<string> misses = new HashSet<string>();
        private static long signature = -1;
        private static int checkedFrame = -1;

        public static int Hits;
        public static int CachedMisses;
        public static int Builds;
        public static int Mismatches;
        public static int PatchedMethods;

        public static bool Installed { get; private set; }

        public static void Install(Harmony harmony)
        {
#if RW17
            if (!Fingerprint.Matches("Texture lookup index", Fingerprint.Known(Original, "D4D313BA56F77956", "B802294668DE64B1")))
#else
            if (!Fingerprint.Matches("Texture lookup index", Fingerprint.Known(Original, "D4D313BA56F77956")))
#endif
            {
                return;
            }
            Installed = true;
            var transpiler = new HarmonyMethod(typeof(TextureLookup), nameof(Transpiler)) { priority = Priority.Last };
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ClearDestroy)),
                postfix: new HarmonyMethod(typeof(TextureLookup), nameof(Reset)));
            harmony.Patch(AccessTools.Method(typeof(PlayDataLoader), nameof(PlayDataLoader.ClearAllPlayData)),
                postfix: new HarmonyMethod(typeof(TextureLookup), nameof(Reset)));
            var namedTargets = (RLOMod.Settings.Supports("childrenwearanything") ? new[] { "ChildClothingUnrestricted.TextureRouting.TexturePathResolver" } : new string[0])
                .Select(AccessTools.TypeByName)
                .Where(t => t != null);
            foreach (var type in GenTypes.AllSubclasses(typeof(Graphic)).Concat(new[] { typeof(Graphic) }).Concat(namedTargets))
            {
                if (!AssemblySafety.CanScan(type) || type.ContainsGenericParameters)
                {
                    continue;
                }
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || !IlScan.CallsAny(method, Original))
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, transpiler: transpiler);
                        PatchedMethods++;
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"[RLO] could not accelerate texture lookups in {type.FullName}.{method.Name}: {e.Message}");
                    }
                }
            }
        }

        public static void Reset()
        {
            winners = null;
            misses.Clear();
            signature = -1;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, Original, AccessTools.Method(typeof(TextureLookup), nameof(Get)));

        public static Texture2D Get(string itemPath, bool reportFailure)
        {
            if (itemPath == null || Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return ContentFinder<Texture2D>.Get(itemPath, reportFailure);
            }
            Refresh(false);
            if (winners.TryGetValue(itemPath, out var winner) && StillValid(itemPath, winner))
            {
                Hits++;
                if (RLOMod.Verify)
                {
                    Check(itemPath, winner.Texture);
                }
                return winner.Texture;
            }
            if (misses.Contains(itemPath))
            {
                Refresh(true);
                if (misses.Contains(itemPath))
                {
                    if (reportFailure)
                    {
                        return ContentFinder<Texture2D>.Get(itemPath, true);
                    }
                    CachedMisses++;
                    if (RLOMod.Verify)
                    {
                        Check(itemPath, null);
                    }
                    return null;
                }
            }
            var resolved = ContentFinder<Texture2D>.Get(itemPath, reportFailure);
            if (resolved != null)
            {
                winners[itemPath] = new Winner { Mod = OwnerOf(itemPath, resolved), Texture = resolved };
            }
            else
            {
                misses.Add(itemPath);
            }
            return resolved;
        }

        private static bool StillValid(string itemPath, Winner winner)
        {
            if (winner.Texture == null)
            {
                return false;
            }
            if (winner.Mod < 0)
            {
                return true;
            }
            var mods = LoadedModManager.RunningModsListForReading;
            return winner.Mod < mods.Count
                   && mods[winner.Mod].GetContentHolder<Texture2D>().contentList.TryGetValue(itemPath, out var current)
                   && ReferenceEquals(current, winner.Texture);
        }

        private static int OwnerOf(string itemPath, Texture2D texture)
        {
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                if (mods[i].GetContentHolder<Texture2D>().contentList.TryGetValue(itemPath, out var t) && ReferenceEquals(t, texture))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void Refresh(bool force)
        {
            var frame = Time.frameCount;
            if (!force && winners != null && frame == checkedFrame)
            {
                return;
            }
            checkedFrame = frame;
            var mods = LoadedModManager.RunningModsListForReading;
            long current = mods.Count;
            for (var i = 0; i < mods.Count; i++)
            {
                var holder = mods[i].GetContentHolder<Texture2D>();
                current = current * 31 + RuntimeHelpers.GetHashCode(holder);
                current = current * 31 + holder.contentList.Count;
            }
            if (winners != null && current == signature)
            {
                return;
            }
            signature = current;
            winners = new Dictionary<string, Winner>();
            misses.Clear();
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                foreach (var entry in mods[i].GetContentHolder<Texture2D>().contentList)
                {
                    if (entry.Value != null && !winners.ContainsKey(entry.Key))
                    {
                        winners[entry.Key] = new Winner { Mod = i, Texture = entry.Value };
                    }
                }
            }
            Builds++;
        }

        private static void Check(string itemPath, Texture2D found)
        {
            var expected = ContentFinder<Texture2D>.Get(itemPath, false);
            if (expected != found && Mismatches++ < 10)
            {
                Log.Warning($"[RLO] texture lookup mismatch for {itemPath}");
            }
        }
    }
}
