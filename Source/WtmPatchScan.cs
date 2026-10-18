using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class WtmPatchScan
    {
        private static Type modCore;
        private static MethodInfo tryResolvePatches;
        private static Dictionary<string, ModContentPack> probablyAddedBy;
        private static bool precomputed, skipping, consumed, lateInstalled;
        private static bool? verified;
        private static int mismatches = -1, entries;
        private static long precomputeTicks, verifyTicks;
        private static SelfCheck check;

        public static void InstallEarly(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ClearCachedPatches)),
                prefix: new HarmonyMethod(typeof(WtmPatchScan), nameof(BeforeClearCachedPatches)));
        }

        public static void InstallLate(Harmony harmony)
        {
            if (!Verified())
            {
                return;
            }
            harmony.Patch(tryResolvePatches, prefix: new HarmonyMethod(typeof(WtmPatchScan), nameof(TryResolvePatchesPrefix)));
            harmony.Patch(AccessTools.Method(modCore, "DefWriter"),
                prefix: new HarmonyMethod(typeof(WtmPatchScan), nameof(DefWriterPrefix)),
                finalizer: new HarmonyMethod(typeof(WtmPatchScan), nameof(DefWriterFinalizer)));
            lateInstalled = true;
        }

        private static bool Resolve()
        {
            if (modCore != null)
            {
                return true;
            }
            modCore = AccessTools.TypeByName("WhatsThatMod.ModCore");
            if (modCore == null)
            {
                return false;
            }
            tryResolvePatches = AccessTools.Method(modCore, "TryResolvePatches");
            probablyAddedBy = AccessTools.Field(modCore, "probablyAddedBy")?.GetValue(null) as Dictionary<string, ModContentPack>;
            if (tryResolvePatches == null || probablyAddedBy == null || AccessTools.Method(modCore, "DefWriter") == null)
            {
                VersionCheck.Skip("What's That Mod patch scan", "ModCore.TryResolvePatches, DefWriter or probablyAddedBy no longer exists");
                modCore = null;
                return false;
            }
            return true;
        }

        private static bool Verified()
        {
            if (verified == null)
            {
                verified = Resolve();
                if (verified.Value)
                {
                    check = new SelfCheck("What's That Mod patch scan", VersionCheck.Known(
                        Fingerprint.Known(AccessTools.Method(modCore, "DefWriter"), "4E13A7480F7666F5"),
                        Fingerprint.Known(tryResolvePatches, "5647A9F09195401A")), 1);
                }
            }
            return verified.Value;
        }

        private static bool DetectPatched()
        {
            var instance = AccessTools.Property(modCore, "Instance")?.GetValue(null) as Mod;
            var settingsType = AccessTools.TypeByName("WhatsThatMod.WTM_ModSettings");
            if (instance == null || settingsType == null)
            {
                return false;
            }
            var settings = AccessTools.Method(typeof(Mod), nameof(Mod.GetSettings)).MakeGenericMethod(settingsType).Invoke(instance, null);
            return Traverse.Create(settings).Field("DetectPatched").GetValue<bool>();
        }

        private static void RunOriginalLoop()
        {
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                try
                {
                    tryResolvePatches.Invoke(null, new object[] { mod });
                }
                catch (TargetInvocationException e)
                {
                    Log.Error($"[RLO] What's That Mod patch scan failed for {mod.PackageIdPlayerFacing}: {e.InnerException ?? e}");
                }
            }
        }

        [Hook]
        public static void BeforeClearCachedPatches()
        {
            if (precomputed || consumed)
            {
                return;
            }
            try
            {
                if (!Verified() || !DetectPatched())
                {
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                RunOriginalLoop();
                precomputeTicks = Stopwatch.GetTimestamp() - start;
                entries = probablyAddedBy.Count;
                precomputed = true;
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] What's That Mod precompute skipped: {e}");
            }
        }

        public static void DefWriterPrefix()
        {
            if (!precomputed || consumed)
            {
                return;
            }
            consumed = true;
            if (RLOMod.Verify || check.IsChecking)
            {
                var fast = new Dictionary<string, ModContentPack>(probablyAddedBy);
                probablyAddedBy.Clear();
                var start = Stopwatch.GetTimestamp();
                RunOriginalLoop();
                verifyTicks = Stopwatch.GetTimestamp() - start;
                mismatches = fast.Count != probablyAddedBy.Count ? Math.Abs(fast.Count - probablyAddedBy.Count) : 0;
                foreach (var pair in probablyAddedBy)
                {
                    if (!fast.TryGetValue(pair.Key, out var mod) || mod != pair.Value)
                    {
                        if (++mismatches <= 10)
                        {
                            Log.Warning($"[RLO] What's That Mod verify mismatch for '{pair.Key}': fast {mod?.PackageId ?? "none"}, original {pair.Value?.PackageId}");
                        }
                    }
                }
                check.Report(mismatches == 0, $"{mismatches} of {probablyAddedBy.Count} defNames");
                if (mismatches != 0)
                {
                    probablyAddedBy.Clear();
                    return;
                }
            }
            skipping = true;
        }

        public static bool TryResolvePatchesPrefix() => !skipping;

        public static void DefWriterFinalizer()
        {
            skipping = false;
        }

        public static string Stats()
        {
            var msPerTick = 1000.0 / Stopwatch.Frequency;
            return $"What's That Mod: precomputed {precomputed} ({entries} defNames in {precomputeTicks * msPerTick:F0} ms at ClearCachedPatches), late patches {lateInstalled}, used {consumed}{(mismatches >= 0 ? $", original rescan {verifyTicks * msPerTick:F0} ms, {mismatches} verify mismatches" : "")}";
        }
    }
}
