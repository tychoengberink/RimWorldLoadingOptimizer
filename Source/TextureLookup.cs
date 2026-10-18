using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static ModContentHolder<Texture2D>[] holders;
        private static int[] counts;
        private static int checkedFrame = -1;

        public static int Hits;
        public static int CachedMisses;
        public static int SkippedScans;
        public static int Builds;
        public static int Mismatches;
        public static int PatchedMethods;

        public static bool Installed { get; private set; }

        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            if (Original == null)
            {
                VersionCheck.Skip("Texture lookup index", "ContentFinder.Get no longer exists");
                return;
            }
#if RW17
            check = new SelfCheck("Texture lookup index", VersionCheck.Known(Fingerprint.Known(Original, "D4D313BA56F77956", "B802294668DE64B1")));
#else
            check = new SelfCheck("Texture lookup index", VersionCheck.Known(Fingerprint.Known(Original, "D4D313BA56F77956")));
#endif
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
            holders = null;
            counts = null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, Original, AccessTools.Method(typeof(TextureLookup), nameof(Get)));

        public static Texture2D Get(string itemPath, bool reportFailure)
        {
            if (itemPath == null || check.IsOff || Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return ContentFinder<Texture2D>.Get(itemPath, reportFailure);
            }
            Refresh(false);
            if (winners.TryGetValue(itemPath, out var winner) && StillValid(itemPath, winner))
            {
                Hits++;
                return Checked(itemPath, winner.Texture);
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
                    return Checked(itemPath, null);
                }
            }
            // While the holders are the ones the winners were built from, winners holds every non-null holder entry, so a path
            // it lacks is in no holder: the holder loop of the lookup and the owner search would both come back empty.
            if (FastContent.Installed && !winners.ContainsKey(itemPath) && Unchanged())
            {
                SkippedScans++;
                var outside = FastContent.GetOutsideHolders(itemPath, reportFailure);
                if (outside != null)
                {
                    winners[itemPath] = new Winner { Mod = -1, Texture = outside };
                }
                else
                {
                    misses.Add(itemPath);
                }
                return Checked(itemPath, outside);
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
            if (winners != null && Unchanged())
            {
                return;
            }
            var mods = LoadedModManager.RunningModsListForReading;
            holders = new ModContentHolder<Texture2D>[mods.Count];
            counts = new int[mods.Count];
            for (var i = 0; i < mods.Count; i++)
            {
                holders[i] = mods[i].GetContentHolder<Texture2D>();
                counts[i] = holders[i].contentList.Count;
            }
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

        // Whether every mod still has the holder and entry count the winners were built from (the old signature, without collisions).
        private static bool Unchanged()
        {
            var mods = LoadedModManager.RunningModsListForReading;
            if (holders == null || holders.Length != mods.Count)
            {
                return false;
            }
            for (var i = 0; i < mods.Count; i++)
            {
                var holder = mods[i].GetContentHolder<Texture2D>();
                if (!ReferenceEquals(holder, holders[i]) || holder.contentList.Count != counts[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static Texture2D Checked(string itemPath, Texture2D found)
        {
            if (!RLOMod.Verify && !check.IsChecking)
            {
                return found;
            }
            var expected = ContentFinder<Texture2D>.Get(itemPath, false);
            var same = expected == found;
            if (!same && Mismatches++ < 10)
            {
                Log.Warning($"[RLO] texture lookup mismatch for {itemPath}");
            }
            check.Report(same, $"texture {itemPath}");
            return same ? found : expected;
        }
    }
}
