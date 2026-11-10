#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Verse;
using Waylith.Graphics.Textures;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasPagePrefetch
    {
        public const string Feature = "Atlas page read-ahead";

        private const int Window = 4;
        private const int Workers = 3;

        private static string KeyFile =>
            Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "atlas-page-keys.txt");

        private static readonly List<string> Used = new List<string>();
        private static Slot[] slots;
        private static Dictionary<string, int> index;
        private static SemaphoreSlim window;
        private static int next = -1,
            consumed,
            served,
            fallbacks,
            wasted;
        private static long waitTicks;
        private static string previous;

        [ThreadStatic]
        private static bool reading;

        private delegate bool OpenKeyed(string key, out ITextureCacheSource source);
        private static OpenKeyed Open;

        private sealed class Slot
        {
            public readonly string Key;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            public ITextureCacheSource Source;
            public bool Taken;

            public Slot(string key)
            {
                Key = key;
            }
        }

        public static void Install(Harmony harmony)
        {
            var store = AccessTools.TypeByName("Waylith.Rendering.TextureCacheStore");
            var open =
                store == null
                    ? null
                    : AccessTools.Method(
                        store,
                        "TryOpenKeyed",
                        new[] { typeof(string), typeof(ITextureCacheSource).MakeByRefType() }
                    );
            var bake = AccessTools.Method(
                typeof(GlobalTextureAtlasManager),
                nameof(GlobalTextureAtlasManager.BakeStaticAtlases)
            );
            if (open == null || bake == null || open.ReturnType != typeof(bool))
            {
                VersionCheck.Skip(Feature, "a method it needs no longer exists");
                return;
            }
            Open = (OpenKeyed)Delegate.CreateDelegate(typeof(OpenKeyed), open);
            harmony.Patch(
                open,
                prefix: new HarmonyMethod(typeof(AtlasPagePrefetch), nameof(OpenPrefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(AtlasPagePrefetch), nameof(OpenPostfix))
            );
            harmony.Patch(
                bake,
                prefix: new HarmonyMethod(typeof(AtlasPagePrefetch), nameof(BakePrefix)) { priority = Priority.Low },
                finalizer: new HarmonyMethod(typeof(AtlasPagePrefetch), nameof(BakeFinalizer))
                {
                    priority = Priority.High,
                }
            );
            MainMenu.UnpatchAfterStartup(open);
            MainMenu.UnpatchAfterStartup(bake);
            StartupPhaseFixes.Reports.Add(Stats);
        }

        public static void BakePrefix()
        {
            if (slots != null)
            {
                return;
            }
            string[] keys;
            try
            {
                previous = File.Exists(KeyFile) ? File.ReadAllText(KeyFile) : "";
                keys = previous.Split('\n').Where(k => k.Length > 0).Distinct().ToArray();
            }
            catch (Exception e)
            {
                RLOLog.Warning(Feature + ": could not read the page list: " + e.Message);
                keys = Array.Empty<string>();
            }
            slots = keys.Select(k => new Slot(k)).ToArray();
            index = new Dictionary<string, int>();
            for (var i = 0; i < slots.Length; i++)
            {
                index[slots[i].Key] = i;
            }
            if (slots.Length == 0)
            {
                return;
            }
            window = new SemaphoreSlim(Window);
            for (var i = 0; i < Math.Min(Workers, slots.Length); i++)
            {
                new Thread(Work) { IsBackground = true, Name = "RLO atlas page read-ahead" }.Start();
            }
        }

        private static void Work()
        {
            reading = true;
            var all = slots;
            var gate = window;
            while (true)
            {
                gate.Wait();
                var i = Interlocked.Increment(ref next);
                if (i >= all.Length)
                {
                    gate.Release();
                    return;
                }
                var slot = all[i];
                try
                {
                    if (!Volatile.Read(ref slot.Taken))
                    {
                        Open(slot.Key, out slot.Source);
                    }
                }
                catch
                {
                    slot.Source = null;
                }
                slot.Done.Set();
            }
        }

        public static bool OpenPrefix(string key, ref ITextureCacheSource source, ref bool __result)
        {
            if (reading || slots == null || key == null || !index.TryGetValue(key, out var i))
            {
                return true;
            }
            var slot = slots[i];
            lock (slots)
            {
                if (slot.Taken)
                {
                    return true;
                }
                slot.Taken = true;
                for (var j = consumed; j < i; j++)
                {
                    Abandon(slots[j]);
                }
                consumed = Math.Max(consumed, i + 1);
            }
            var watch = Stopwatch.StartNew();
            slot.Done.Wait();
            Interlocked.Add(ref waitTicks, watch.ElapsedTicks);
            var ready = slot.Source;
            slot.Source = null;
            window.Release();
            if (ready == null)
            {
                fallbacks++;
                return true;
            }
            served++;
            source = ready;
            __result = true;
            lock (Used)
            {
                Used.Add(key);
            }
            return false;
        }

        private static void Abandon(Slot slot)
        {
            if (slot.Taken)
            {
                return;
            }
            slot.Taken = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                slot.Done.Wait();
                if (slot.Source != null)
                {
                    slot.Source.Dispose();
                    slot.Source = null;
                    Interlocked.Increment(ref wasted);
                }
                window.Release();
            });
        }

        public static void OpenPostfix(string key, bool __result, bool __runOriginal)
        {
            if (reading || !__runOriginal || !__result || key == null)
            {
                return;
            }
            lock (Used)
            {
                Used.Add(key);
            }
        }

        public static Exception BakeFinalizer(Exception __exception)
        {
            var all = slots;
            if (all != null)
            {
                lock (all)
                {
                    for (var j = consumed; j < all.Length; j++)
                    {
                        Abandon(all[j]);
                    }
                    consumed = all.Length;
                }
            }
            if (__exception == null)
            {
                Save();
            }
            return __exception;
        }

        private static void Save()
        {
            string text;
            lock (Used)
            {
                text = string.Concat(Used.Select(k => k + "\n"));
            }
            if (text == previous)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KeyFile));
                File.WriteAllText(KeyFile, text);
            }
            catch (Exception e)
            {
                RLOLog.Warning(Feature + ": could not save the page list: " + e.Message);
            }
        }

        private static string Stats()
        {
            if (slots == null)
            {
                return Feature + ": atlas baking not reached";
            }
            return $"{Feature}: {served} of {slots.Length} listed pages read ahead on worker threads, {fallbacks} read on the main thread after all, {wasted} read ahead but not used; the main thread waited {waitTicks * 1000.0 / Stopwatch.Frequency:N0} ms";
        }
    }
}
#endif
