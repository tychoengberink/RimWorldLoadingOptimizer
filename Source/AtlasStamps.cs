#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;
using Waylith.Assets;
using Texture2D = UnityEngine.Texture2D;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasStamps
    {
        private struct Stamp
        {
            public bool exists;
            public long length, ticks;
        }

        private static readonly int Workers = Math.Max(2, Math.Min(Environment.ProcessorCount, 16));

        private static MethodInfo stampMethod;
        private static FieldInfo queueField, masksField;
        private static Task<Dictionary<string, Stamp>> pending;
        private static Dictionary<string, Stamp> stamps;
        private static int state, bakes, files, used, packed, fallbacks, verified, mismatches;
        private static long resolveTicks, waitTicks, collectTicks;

        public static void Install(Harmony harmony)
        {
            var bake = AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases));
            stampMethod = AccessTools.Method(typeof(StaticTextureAtlas), "TryWriteSourceStamp", new[] { typeof(BinaryWriter), typeof(Texture2D) });
            if (!Fingerprint.Matches("Atlas source stamps",
                Fingerprint.Known(bake, "516529BC05D9B5EE", "45434BFD7AD5A8A9"),
                Fingerprint.Known(AccessTools.Method(typeof(StaticTextureAtlas), "TryGetCacheKey"), "31922490706299AC", "F74EA4FF5C50B4FA"),
                Fingerprint.Known(stampMethod, "4DED400458B2EBF0", "11386B8F876A15F3")))
            {
                return;
            }
            queueField = AccessTools.Field(typeof(GlobalTextureAtlasManager), "buildQueue");
            masksField = AccessTools.Field(typeof(GlobalTextureAtlasManager), "buildQueueMasks");
            if (queueField == null || !queueField.IsStatic || queueField.FieldType != typeof(Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>)
                || masksField == null || !masksField.IsStatic || masksField.FieldType != typeof(Dictionary<Texture2D, Texture2D>))
            {
                Log.Message("[RLO] atlas source stamps: the game's atlas build queue was not found. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            harmony.Patch(bake, prefix: new HarmonyMethod(typeof(AtlasStamps), nameof(BakePrefix)), finalizer: new HarmonyMethod(typeof(AtlasStamps), nameof(BakeFinalizer)));
            harmony.Patch(stampMethod, prefix: new HarmonyMethod(typeof(AtlasStamps), nameof(StampPrefix)));
            MainMenu.UnpatchAfterStartup(bake);
            MainMenu.UnpatchAfterStartup(stampMethod);
            LeanFixes.Reports.Add(Stats);
        }

        public static void BakePrefix()
        {
            var queue = (Dictionary<TextureAtlasGroupKey, (List<Texture2D>, HashSet<Texture2D>)>)queueField.GetValue(null);
            var queueMasks = (Dictionary<Texture2D, Texture2D>)masksField.GetValue(null);
            if (pending != null || stamps != null || queue == null || queueMasks == null)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in queue.Values)
            {
                foreach (var texture in entry.Item1)
                {
                    Add(paths, texture);
                }
            }
            foreach (var mask in queueMasks.Values)
            {
                Add(paths, mask);
            }
            var list = paths.ToArray();
            collectTicks += sw.ElapsedTicks;
            bakes++;
            files += list.Length;
            pending = Task.Run(() => Resolve(list));
        }

        public static Exception BakeFinalizer(Exception __exception)
        {
            pending = null;
            stamps = null;
            return __exception;
        }

        private static void Add(HashSet<string> paths, Texture2D texture)
        {
            var path = texture?.CoreTexture?.SourcePath;
            if (path != null)
            {
                paths.Add(path);
            }
        }

        private static Dictionary<string, Stamp> Resolve(string[] paths)
        {
            var sw = Stopwatch.StartNew();
            var found = new Stamp[paths.Length];
            var ok = new bool[paths.Length];
            Parallel.For(0, paths.Length, new ParallelOptions { MaxDegreeOfParallelism = Workers }, i =>
            {
                try
                {
                    found[i] = Read(paths[i]);
                    ok[i] = true;
                }
                catch (Exception)
                {
                }
            });
            var map = new Dictionary<string, Stamp>(paths.Length, StringComparer.Ordinal);
            for (var i = 0; i < paths.Length; i++)
            {
                if (ok[i])
                {
                    map[paths[i]] = found[i];
                }
            }
            resolveTicks = sw.ElapsedTicks;
            return map;
        }

        private static Stamp Read(string path)
        {
            var file = new FileInfo(path);
            var stamp = new Stamp { exists = file.Exists };
            if (stamp.exists)
            {
                stamp.length = file.Length;
                stamp.ticks = file.LastWriteTimeUtc.Ticks;
            }
            return stamp;
        }

        private static Dictionary<string, Stamp> Ready()
        {
            if (pending != null)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    stamps = pending.Result;
                }
                catch (Exception e)
                {
                    stamps = null;
                    Log.Warning("[RLO] atlas source stamps: reading the file stamps failed, the game reads them itself: " + e.GetBaseException().Message);
                }
                waitTicks += sw.ElapsedTicks;
                pending = null;
            }
            return stamps;
        }

        internal static bool TryGet(string path, out bool exists, out long length, out long ticks)
        {
            var map = path == null ? null : Ready();
            if (map == null || !map.TryGetValue(path, out var stamp))
            {
                exists = false;
                length = ticks = 0;
                return false;
            }
            exists = stamp.exists;
            length = stamp.length;
            ticks = stamp.ticks;
            return true;
        }

        public static bool StampPrefix(BinaryWriter writer, Texture2D texture, ref bool __result)
        {
            if (state == 0)
            {
                state = Fingerprint.OnlyOurs(stampMethod) ? 1 : 2;
            }
            var map = state == 1 ? Ready() : null;
            if (map == null)
            {
                return true;
            }
            var path = texture?.CoreTexture?.SourcePath;
            if (path == null)
            {
                return true;
            }
            if (!map.TryGetValue(path, out var stamp))
            {
                fallbacks++;
                return true;
            }
            writer.Write(path);
            if (ContentPack.TryGetTextureContentHash(path, out var hash))
            {
                writer.Write(hash);
                packed++;
                __result = true;
                return false;
            }
            used++;
            if (RLOMod.Verify)
            {
                Check(path, stamp);
            }
            if (!stamp.exists)
            {
                __result = false;
                return false;
            }
            writer.Write(stamp.length);
            writer.Write(stamp.ticks);
            __result = true;
            return false;
        }

        private static void Check(string path, Stamp stamp)
        {
            verified++;
            Stamp fresh;
            try
            {
                fresh = Read(path);
            }
            catch (Exception e)
            {
                fresh = new Stamp { exists = !stamp.exists };
                Log.Warning($"[RLO] atlas source stamps: verify {path}: fresh FileInfo failed: {e.Message}");
            }
            if (fresh.exists != stamp.exists || fresh.length != stamp.length || fresh.ticks != stamp.ticks)
            {
                if (++mismatches <= 10)
                {
                    Log.Warning($"[RLO] atlas source stamps: verify mismatch for {path}: read ahead {stamp.exists}/{stamp.length}/{stamp.ticks}, fresh {fresh.exists}/{fresh.length}/{fresh.ticks}");
                }
            }
        }

        private static string Stats()
        {
            if (bakes == 0)
            {
                return "atlas source stamps: atlas baking not reached";
            }
            if (state == 2)
            {
                return "atlas source stamps: left to vanilla (another mod patches the stamp method)";
            }
            return $"atlas source stamps: {files} source files stamped on {Workers} threads in {LeanFixes.Seconds(resolveTicks):F2} s (collect {LeanFixes.Seconds(collectTicks) * 1000:F0} ms, main thread waited {LeanFixes.Seconds(waitTicks) * 1000:F0} ms); "
                + $"{used} stamps from them, {packed} from content packs, {fallbacks} left to vanilla"
                + (RLOMod.Verify ? $", verify {verified} checked, {mismatches} mismatches" : "");
        }
    }
}
#endif
