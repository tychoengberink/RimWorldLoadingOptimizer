using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class DdsPrefetch
    {
        private sealed class Entry
        {
            public int Index;
            public IntPtr Data;
            public int Length;
        }

        private const long Budget = 768L << 20;
        private const long WarmWindow = 512L << 20;
        private static readonly int Workers = Math.Max(4, Math.Min(8, Environment.ProcessorCount / 2));
        private const int Lag = 400;

        private static string[] files;
        private static long[] before;
        private static Dictionary<ModContentPack, int> firstOfMod;
        private static bool buffers;
        private static readonly object windowGate = new object();
        private static readonly ConcurrentDictionary<string, Entry> ready = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private static volatile Dictionary<string, int> indexOf;
        private static readonly object budgetGate = new object();
        private static long inFlight;
        private static int next = -1;
        private static int consumed = -1;
        private static volatile bool stopped;
        private static int sweep;

        public static int Hits;
        public static int Misses;
        public static int Discarded;
        public static long BytesRead;
        public static long PeakInFlight;
        public static int Warmed;

        public static void Install(Harmony harmony)
        {
            var mods = LoadedModManager.RunningModsListForReading.ToList();
            var loadDds = AccessTools.Method(AccessTools.TypeByName("GraphicSetter.DDSLoader"), "LoadDDS");
            var parameters = loadDds?.GetParameters();
            buffers = loadDds != null && loadDds.ReturnType == typeof(Texture2D) && parameters.Length == 3
                && parameters[0].ParameterType == typeof(string) && parameters[0].Name == "path"
                && parameters[1].ParameterType == typeof(bool).MakeByRefType() && parameters[1].Name == "hasMipMaps"
                && Fingerprint.Matches("Graphics Settings+ texture buffers (falls back to read-ahead)", Fingerprint.Known(loadDds, "538A89F2DA2F1CDD"));
            if (buffers)
            {
                harmony.Patch(loadDds, prefix: new HarmonyMethod(typeof(DdsPrefetch), nameof(LoadDdsPrefix)));
            }
            var vanillaDds = AccessTools.Method(AccessTools.TypeByName("Verse.ModDdsLoader"), "TryLoadDds", new[] { typeof(RimWorld.IO.VirtualFile) });
            if (vanillaDds != null)
            {
                harmony.Patch(vanillaDds, prefix: new HarmonyMethod(typeof(DdsPrefetch), nameof(VanillaDdsPrefix)));
            }
            var reloadContent = AccessTools.Method(typeof(ModContentPack), "ReloadContentInt");
            if (reloadContent != null)
            {
                harmony.Patch(reloadContent, prefix: new HarmonyMethod(typeof(DdsPrefetch), nameof(ReloadContentPrefix)));
            }
            new Thread(() => Start(mods)) { IsBackground = true, Name = "RLO DDS scan" }.Start();
        }

        private static void Start(List<ModContentPack> mods)
        {
            var list = new List<string>();
            var sizes = new List<long>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var first = new Dictionary<ModContentPack, int>();
            foreach (var mod in mods)
            {
                first[mod] = list.Count;
                foreach (var folder in mod.foldersToLoadDescendingOrder)
                {
                    try
                    {
                        var dir = new DirectoryInfo(Path.Combine(folder, "Textures/"));
                        if (!dir.Exists)
                        {
                            continue;
                        }
                        foreach (var file in dir.GetFiles("*.dds", SearchOption.AllDirectories))
                        {
                            var path = file.FullName;
                            if (!index.ContainsKey(path))
                            {
                                index[path] = list.Count;
                                list.Add(path);
                                sizes.Add(file.Length);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            var offsets = new long[sizes.Count + 1];
            for (var i = 0; i < sizes.Count; i++)
            {
                offsets[i + 1] = offsets[i] + sizes[i];
            }
            files = list.ToArray();
            before = offsets;
            firstOfMod = first;
            indexOf = index;
            for (var i = 0; i < Workers; i++)
            {
                new Thread(Work) { IsBackground = true, Name = "RLO DDS prefetch" }.Start();
            }
        }

        private static void Work()
        {
            var scratch = new byte[1 << 20];
            while (!stopped)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= files.Length)
                {
                    return;
                }
                if (i <= Volatile.Read(ref consumed))
                {
                    continue;
                }
                try
                {
                    if (buffers)
                    {
                        Read(i, scratch);
                    }
                    else
                    {
                        Warm(i, scratch);
                    }
                }
                catch
                {
                }
            }
        }

        private static void Read(int index, byte[] scratch)
        {
            var path = files[index];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                var length = stream.Length;
                if (length <= 148 || length > int.MaxValue)
                {
                    return;
                }
                lock (budgetGate)
                {
                    while (inFlight + length > Budget && inFlight > 0 && !stopped)
                    {
                        Monitor.Wait(budgetGate, 50);
                        if (index <= Volatile.Read(ref consumed))
                        {
                            return;
                        }
                    }
                    if (stopped)
                    {
                        return;
                    }
                    inFlight += length;
                    if (inFlight > PeakInFlight)
                    {
                        PeakInFlight = inFlight;
                    }
                }
                var data = Marshal.AllocHGlobal((IntPtr)length);
                var offset = 0;
                try
                {
                    while (offset < length)
                    {
                        var n = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, length - offset));
                        if (n <= 0)
                        {
                            throw new EndOfStreamException();
                        }
                        Marshal.Copy(scratch, 0, data + offset, n);
                        offset += n;
                    }
                }
                catch
                {
                    Free(data, length);
                    throw;
                }
                Interlocked.Add(ref BytesRead, length);
                var entry = new Entry { Index = index, Data = data, Length = (int)length };
                if (stopped || index <= Volatile.Read(ref consumed) - Lag || !ready.TryAdd(path, entry))
                {
                    Free(data, length);
                }
                else if (stopped && ready.TryRemove(path, out var late))
                {
                    Free(late.Data, late.Length);
                }
            }
        }

        private static void Warm(int index, byte[] scratch)
        {
            lock (windowGate)
            {
                while (!stopped && before[index] - before[Math.Max(Volatile.Read(ref consumed), 0)] > WarmWindow)
                {
                    Monitor.Wait(windowGate, 100);
                }
            }
            if (stopped || index <= Volatile.Read(ref consumed))
            {
                return;
            }
            using (var stream = new FileStream(files[index], FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                long total = 0;
                int n;
                while ((n = stream.Read(scratch, 0, scratch.Length)) > 0)
                {
                    total += n;
                }
                Interlocked.Add(ref BytesRead, total);
                Interlocked.Increment(ref Warmed);
            }
        }

        private static void Free(IntPtr data, long length)
        {
            Marshal.FreeHGlobal(data);
            lock (budgetGate)
            {
                inFlight -= length;
                Monitor.PulseAll(budgetGate);
            }
        }

        private static void Advance(int index)
        {
            if (index <= consumed)
            {
                return;
            }
            consumed = index;
            if (!buffers)
            {
                lock (windowGate)
                {
                    Monitor.PulseAll(windowGate);
                }
                return;
            }
            if (index - Lag < 0 || ++sweep % 256 != 0)
            {
                return;
            }
            foreach (var pair in ready)
            {
                if (pair.Value.Index < index - Lag && ready.TryRemove(pair.Key, out var stale))
                {
                    Discarded++;
                    Free(stale.Data, stale.Length);
                }
            }
        }

        public static void Stop()
        {
            stopped = true;
            lock (windowGate)
            {
                Monitor.PulseAll(windowGate);
            }
            foreach (var pair in ready)
            {
                if (ready.TryRemove(pair.Key, out var entry))
                {
                    Discarded++;
                    Free(entry.Data, entry.Length);
                }
            }
        }

        public static void VanillaDdsPrefix(RimWorld.IO.VirtualFile file)
        {
            var map = indexOf;
            if (stopped || buffers || map == null || file == null || !UnityData.IsInMainThread)
            {
                return;
            }
            try
            {
                if (map.TryGetValue(file.FullPath, out var index))
                {
                    Advance(index);
                }
            }
            catch
            {
            }
        }

        public static void ReloadContentPrefix(ModContentPack __instance)
        {
            var first = firstOfMod;
            if (stopped || buffers || first == null || __instance == null || !UnityData.IsInMainThread || !first.TryGetValue(__instance, out var index))
            {
                return;
            }
            Advance(index - 1);
        }

        public static unsafe bool LoadDdsPrefix(string path, ref bool hasMipMaps, ref Texture2D __result)
        {
            var map = indexOf;
            if (stopped || !buffers || map == null || path == null || !UnityData.IsInMainThread || !map.TryGetValue(path, out var index))
            {
                return true;
            }
            Advance(index);
            if (!ready.TryRemove(path, out var entry))
            {
                Misses++;
                return true;
            }
            try
            {
                var bytes = (byte*)entry.Data;
                if (entry.Length < 128 || bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S' || bytes[3] != ' ' || *(uint*)(bytes + 4) != 124)
                {
                    return true;
                }
                var flags = *(uint*)(bytes + 8);
                var height = *(uint*)(bytes + 12);
                var width = *(uint*)(bytes + 16);
                var mips = (flags & 0x20000) == 0 ? 1u : *(uint*)(bytes + 28);
                var pixelFlags = *(uint*)(bytes + 80);
                if ((pixelFlags & 4) == 0 || (width & 3) != 0 || (height & 3) != 0)
                {
                    return true;
                }
                var fourCc = *(uint*)(bytes + 84);
                TextureFormat format;
                if (fourCc == 0x31545844)
                {
                    format = TextureFormat.DXT1;
                }
                else if (fourCc == 0x35545844)
                {
                    format = TextureFormat.DXT5;
                }
                else if (fourCc == 0x30315844)
                {
                    format = TextureFormat.BC7;
                }
                else
                {
                    return true;
                }
                var offset = format == TextureFormat.BC7 ? 148 : 128;
                if (entry.Length <= offset)
                {
                    return true;
                }
                var texture = new Texture2D((int)width, (int)height, format, hasMipMaps = (int)mips > 1);
                try
                {
                    texture.LoadRawTextureData((IntPtr)(bytes + offset), entry.Length - offset);
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    hasMipMaps = false;
                    return true;
                }
                Hits++;
                __result = texture;
                return false;
            }
            finally
            {
                Free(entry.Data, entry.Length);
            }
        }

        [StaticConstructorOnStartup]
        public static class StopAtStartup
        {
            static StopAtStartup()
            {
                Stop();
            }
        }

        public static string Stats() => buffers
            ? $"DDS prefetch (Graphics Settings+ buffers, {Workers} threads): {Hits} hits, {Misses} misses, {Discarded} discarded, {BytesRead >> 20} MB read ahead, peak {PeakInFlight >> 20} MB buffered"
            : $"DDS prefetch (read-ahead for the game's own loader): {Warmed} files, {BytesRead >> 20} MB read ahead, progress at file {consumed + 1} of {files?.Length ?? 0}";
    }
}
