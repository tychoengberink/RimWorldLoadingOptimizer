using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CwaTextures
    {
        private static HashSet<string> bundleTails;
        private static int bundleSignature = -1;
        private static int installed, calls, bundleChecks, mismatches;
        private static long holderTicks, resourceTicks, bundleTicks;

        private const string PatcherType = "ChildClothingUnrestricted.Core.ApparelDefPatcher";
        private static Dictionary<string, Texture2D> index;
        private static int ctorDepth;
        private static long indexTicks;
        private static int indexEntries;

        public static void InstallEarly(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RuntimeHelpers), nameof(RuntimeHelpers.RunClassConstructor), new[] { typeof(RuntimeTypeHandle) }),
                prefix: new HarmonyMethod(typeof(CwaTextures), nameof(CtorStart)),
                finalizer: new HarmonyMethod(typeof(CwaTextures), nameof(CtorEnd)));
        }

        public static void Install(Harmony harmony)
        {
            var type = ModTypes.Find("ChildClothingUnrestricted.TextureRouting.TexturePathResolver");
            var method = type == null ? null : AccessTools.Method(type, "TextureExists", new[] { typeof(string) });
            if (method == null || method.ReturnType != typeof(bool) || !Fingerprint.Matches("Children Wear Anything texture checks", Fingerprint.Known(method, "A79403565690FCB6")))
            {
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(CwaTextures), nameof(Prefix)));
            installed = 1;
        }

        public static void CtorStart(RuntimeTypeHandle type)
        {
            if (installed == 0 || !UnityData.IsInMainThread)
            {
                return;
            }
            if (ctorDepth > 0)
            {
                ctorDepth++;
                return;
            }
            if (Type.GetTypeFromHandle(type)?.FullName == PatcherType)
            {
                ctorDepth = 1;
            }
        }

        public static void CtorEnd(Exception __exception)
        {
            if (ctorDepth > 0 && UnityData.IsInMainThread && --ctorDepth == 0)
            {
                index = null;
            }
        }

        public static bool Prefix(string pathWithoutOrientation, ref bool __result)
        {
            if (pathWithoutOrientation == null || !UnityData.IsInMainThread)
            {
                return true;
            }
            calls++;
            try
            {
                __result = Find(pathWithoutOrientation + "_south") != null || Find(pathWithoutOrientation) != null;
            }
            catch
            {
                __result = false;
            }
            if (RLOMod.Verify)
            {
                bool expected;
                try
                {
                    expected = ContentFinder<Texture2D>.Get(pathWithoutOrientation + "_south", false) != null || ContentFinder<Texture2D>.Get(pathWithoutOrientation, false) != null;
                }
                catch
                {
                    expected = false;
                }
                if (expected != __result && mismatches++ < 10)
                {
                    Log.Warning($"[RLO] cwa verify mismatch for {pathWithoutOrientation}: fast {__result}, original {expected}");
                }
            }
            return false;
        }

        private static void BuildIndex()
        {
            var t0 = Stopwatch.GetTimestamp();
            index = new Dictionary<string, Texture2D>();
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                foreach (var entry in mods[i].GetContentHolder<Texture2D>().contentList)
                {
                    if ((object)entry.Value != null && !index.ContainsKey(entry.Key))
                    {
                        index[entry.Key] = entry.Value;
                    }
                }
            }
            indexEntries = index.Count;
            indexTicks += Stopwatch.GetTimestamp() - t0;
        }

        public static Texture2D Find(string itemPath)
        {
            var t0 = Stopwatch.GetTimestamp();
            if (ctorDepth > 0 && index == null)
            {
                BuildIndex();
                t0 = Stopwatch.GetTimestamp();
            }
            Texture2D found = null;
            if (ctorDepth > 0)
            {
                index.TryGetValue(itemPath, out found);
            }
            else
            {
                var mods = LoadedModManager.RunningModsListForReading;
                for (var i = mods.Count - 1; i >= 0 && (object)found == null; i--)
                {
                    found = mods[i].GetContentHolder<Texture2D>().Get(itemPath);
                }
            }
            var t1 = Stopwatch.GetTimestamp();
            holderTicks += t1 - t0;
            if ((object)found != null)
            {
                return found;
            }
            found = Resources.Load<Texture2D>(GenFilePaths.ContentPath<Texture2D>() + itemPath);
            var t2 = Stopwatch.GetTimestamp();
            resourceTicks += t2 - t1;
            if ((object)found != null)
            {
                return found;
            }
            if (BundleMayContain(itemPath))
            {
                bundleChecks++;
                found = ContentFinder<Texture2D>.TryFindAssetInModBundles(itemPath);
            }
            bundleTicks += Stopwatch.GetTimestamp() - t2;
            return found;
        }

        private static bool BundleMayContain(string itemPath)
        {
            if (itemPath.Length == 0 || itemPath[0] == '/' || itemPath.IndexOf('\\') >= 0 || itemPath.IndexOf(':') >= 0)
            {
                return true;
            }
            var mods = LoadedModManager.RunningModsListForReading;
            var signature = mods.Count;
            for (var i = 0; i < mods.Count; i++)
            {
                var bundles = mods[i].assetBundles.loadedAssetBundles;
                for (var j = 0; j < bundles.Count; j++)
                {
                    signature = signature * 31 + (bundles[j] == null ? 0 : bundles[j].GetInstanceID());
                }
            }
            if (bundleTails == null || signature != bundleSignature)
            {
                bundleSignature = signature;
                bundleTails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var marker = GenFilePaths.ContentPath<Texture2D>().ToLowerInvariant();
                foreach (var mod in mods)
                {
                    foreach (var bundle in mod.assetBundles.loadedAssetBundles)
                    {
                        if (bundle == null)
                        {
                            continue;
                        }
                        foreach (var name in bundle.GetAllAssetNames())
                        {
                            var lower = name.ToLowerInvariant();
                            var at = lower.IndexOf(marker, StringComparison.Ordinal);
                            while (at >= 0)
                            {
                                var tail = lower.Substring(at + marker.Length);
                                bundleTails.Add(tail);
                                var ext = Path.GetExtension(tail);
                                if (ext.Length > 0)
                                {
                                    bundleTails.Add(tail.Substring(0, tail.Length - ext.Length));
                                }
                                at = lower.IndexOf(marker, at + 1, StringComparison.Ordinal);
                            }
                        }
                    }
                }
            }
            return bundleTails.Contains(itemPath);
        }

        public static string Stats()
        {
            if (installed == 0)
            {
                return "cwa: off";
            }
            double ms(long t) => t * 1000.0 / Stopwatch.Frequency;
            return $"cwa: {calls} TextureExists calls; index {ms(indexTicks):F0} ms ({indexEntries} paths); holders {ms(holderTicks):F0} ms, Resources.Load {ms(resourceTicks):F0} ms, bundles {ms(bundleTicks):F0} ms ({bundleChecks} full bundle searches, {bundleTails?.Count ?? 0} bundle texture paths); {mismatches} verify mismatches";
        }
    }
}
