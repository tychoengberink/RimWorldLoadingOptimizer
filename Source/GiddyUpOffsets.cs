#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GiddyUpOffsets
    {
        public const string Feature = "Giddy-Up draw offsets";

        private delegate bool OpenCache(string sourcePath, bool premultiplied, out Waylith.Rendering.ITextureCacheSource file);

        private sealed class Pending
        {
            public UnityEngine.Texture2D Readable;
            public string Key;
            public bool Hit;
            public (int back, int height)? Expected;
            public int Back;
            public bool HaveBack;
            public long Start;
        }

        private static readonly Dictionary<string, (int back, int height)> stored = new Dictionary<string, (int, int)>();
        private static readonly Dictionary<string, (int back, int height)> used = new Dictionary<string, (int, int)>();
        private static OpenCache open;
        private static Func<UnityEngine.Texture2D, UnityEngine.Texture2D> readable;
        private static Func<UnityEngine.Texture2D, int> backHeight;
        private static MethodInfo originalReadable, originalBackHeight, originalHeight;
        private static Assembly giddyUp;
        private static string global;
        private static Pending current;
        private static double storedMissMs;
        private static int hits, misses, uncacheable, verified, mismatches, keyErrors, redirected;
        private static long hitTicks, missTicks;
        private static bool dirty;

        private static string FilePath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "giddyup-offsets.txt");

        public static void Install(Harmony harmony)
        {
            var utility = AccessTools.TypeByName("GiddyUp.TextureUtility");
            if (utility == null)
            {
                return;
            }
            var setDrawOffset = AccessTools.Method(utility, "SetDrawOffset");
            originalReadable = AccessTools.Method(utility, "GetReadableTexture");
            originalBackHeight = AccessTools.Method(utility, "GetBackHeight");
            originalHeight = AccessTools.PropertyGetter(typeof(UnityEngine.Texture), nameof(UnityEngine.Texture.height));
            var openCache = AccessTools.Method(typeof(Waylith.Rendering.GpuTextureService), "TryOpenTextureCache", new[] { typeof(string), typeof(bool), typeof(Waylith.Rendering.ITextureCacheSource).MakeByRefType() });
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(setDrawOffset, "B83049240BD8E441"),
                Fingerprint.Known(originalReadable, "648F7E6B1FB4FEE6"),
                Fingerprint.Known(originalBackHeight, "9E7385386D42EFFD"),
                Fingerprint.Known(openCache, "A4725E86D6249443")))
            {
                return;
            }
            open = (OpenCache)openCache.CreateDelegate(typeof(OpenCache));
            readable = (Func<UnityEngine.Texture2D, UnityEngine.Texture2D>)originalReadable.CreateDelegate(typeof(Func<UnityEngine.Texture2D, UnityEngine.Texture2D>));
            backHeight = (Func<UnityEngine.Texture2D, int>)originalBackHeight.CreateDelegate(typeof(Func<UnityEngine.Texture2D, int>));
            giddyUp = utility.Assembly;
            Load();
            harmony.Patch(setDrawOffset, transpiler: new HarmonyMethod(typeof(GiddyUpOffsets), nameof(Transpiler)));
            if (redirected != 3)
            {
                harmony.Unpatch(setDrawOffset, HarmonyPatchType.Transpiler, harmony.Id);
                Log.Message($"[RLO] {Feature}: the texture read-back calls were not found as expected ({redirected} of 3). This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            MainMenu.Then(Save);
            MainMenu.UnpatchAfterStartup(setDrawOffset);
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var targets = new (MethodInfo from, string to)[]
            {
                (originalReadable, nameof(Readable)),
                (originalBackHeight, nameof(BackHeight)),
                (originalHeight, nameof(Height)),
            };
            var found = targets.Select(t => list.Count(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m && m == t.from)).ToArray();
            if (found.Any(n => n != 1))
            {
                redirected = 0;
                return list;
            }
            redirected = 0;
            foreach (var instruction in list)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m)
                {
                    foreach (var (from, to) in targets)
                    {
                        if (m == from)
                        {
                            instruction.opcode = OpCodes.Call;
                            instruction.operand = AccessTools.Method(typeof(GiddyUpOffsets), to);
                            redirected++;
                        }
                    }
                }
            }
            return list;
        }

        public static UnityEngine.Texture2D Readable(UnityEngine.Texture2D texture)
        {
            var start = Stopwatch.GetTimestamp();
            string key = null;
            try
            {
                key = Key(texture);
            }
            catch (Exception)
            {
                keyErrors++;
            }
            (int back, int height)? expected = null;
            if (key != null && stored.TryGetValue(key, out var entry))
            {
                if (!RLOMod.Verify)
                {
                    current = new Pending { Readable = texture, Key = key, Hit = true, Back = entry.back, Expected = entry, Start = start };
                    return texture;
                }
                expected = entry;
            }
            var result = readable(texture);
            current = new Pending { Readable = result, Key = key, Expected = expected, Start = start };
            return result;
        }

        public static int BackHeight(UnityEngine.Texture2D texture)
        {
            var pending = current;
            if (pending == null || !ReferenceEquals(pending.Readable, texture))
            {
                return backHeight(texture);
            }
            if (pending.Hit)
            {
                return pending.Back;
            }
            pending.Back = backHeight(texture);
            pending.HaveBack = true;
            return pending.Back;
        }

        public static int Height(UnityEngine.Texture texture)
        {
            var pending = current;
            current = null;
            if (pending == null || !ReferenceEquals(pending.Readable, texture))
            {
                return texture.height;
            }
            if (pending.Hit)
            {
                var cached = pending.Expected.Value;
                hits++;
                hitTicks += Stopwatch.GetTimestamp() - pending.Start;
                used[pending.Key] = cached;
                return cached.height;
            }
            var height = texture.height;
            if (pending.Key == null || !pending.HaveBack)
            {
                uncacheable++;
                return height;
            }
            var measured = (pending.Back, height);
            if (pending.Expected.HasValue)
            {
                verified++;
                if (pending.Expected.Value != measured)
                {
                    mismatches++;
                    dirty = true;
                }
            }
            else
            {
                misses++;
                missTicks += Stopwatch.GetTimestamp() - pending.Start;
                dirty = true;
            }
            used[pending.Key] = measured;
            return height;
        }

        private static string Key(UnityEngine.Texture2D texture)
        {
            var core = texture?.CoreTexture;
            var cpu = core?.CpuData;
            if (core?.SourcePath == null || cpu == null)
            {
                return null;
            }
            if (!open(core.SourcePath, cpu.AlphaIsPremultiplied, out var file))
            {
                return null;
            }
            using (file)
            {
                if (file.Format != cpu.Format || file.Width != core.width || file.Height != core.height || file.MipCount <= 0)
                {
                    return null;
                }
                var length = file.LevelLength(0);
                if (length <= 0)
                {
                    return null;
                }
                var level = new byte[length];
                file.ReadLevel(0, level);
                global ??= string.Join("|", "1", Mvid(typeof(Waylith.Rendering.GpuTextureService)), Mvid(typeof(UnityEngine.Texture2D)), Mvid(typeof(Def)), Mvid(giddyUp.ManifestModule),
                    SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceVersion, Prefs.TextureCompression);
                var text = string.Join("|", global, file.Format, file.Swizzle, file.Width, file.Height, file.MipCount, cpu.AlphaIsPremultiplied, file.Opaque,
                    (int)texture.filterMode, Convert.ToHexString(SHA256.HashData(level)));
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            }
        }

        private static string Mvid(Type type) => Mvid(type.Assembly.ManifestModule);

        private static string Mvid(Module module) => module.ModuleVersionId.ToString("N");

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 2 && parts[0] == "#miss-ms" && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                    {
                        storedMissMs = ms;
                    }
                    else if (parts.Length == 3 && parts[0].Length == 64
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var back)
                        && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                    {
                        stored[parts[0]] = (back, height);
                    }
                }
            }
            catch (Exception e)
            {
                stored.Clear();
                Log.Message($"[RLO] {Feature}: could not read the offset cache ({e.Message}); all offsets are measured this start.");
            }
        }

        private static void Save()
        {
            if (!dirty && used.Count == stored.Count)
            {
                return;
            }
            try
            {
                var lines = new List<string> { "#miss-ms\t" + MissMs().ToString("R", CultureInfo.InvariantCulture) };
                lines.AddRange(used.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}\t{e.Value.back.ToString(CultureInfo.InvariantCulture)}\t{e.Value.height.ToString(CultureInfo.InvariantCulture)}"));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var temp = FilePath + ".tmp";
                File.WriteAllLines(temp, lines);
                File.Move(temp, FilePath, true);
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not write the offset cache ({e.Message}); the next start measures again.");
            }
        }

        private static double MissMs() => misses > 0 ? LeanFixes.Seconds(missTicks) * 1000 / misses : storedMissMs;

        private static string Stats()
        {
            var hitSeconds = LeanFixes.Seconds(hitTicks);
            var missMs = MissMs();
            var saved = missMs > 0 ? $"about {hits * missMs / 1000 - hitSeconds:F1} s saved" : "time saved not known yet";
            var text = $"{Feature}: {hits} from cache in {hitSeconds:F2} s, {misses} measured in {LeanFixes.Seconds(missTicks):F2} s and stored, {uncacheable} not cacheable, {saved}";
            if (keyErrors > 0)
            {
                text += $", {keyErrors} texture checks failed (measured instead)";
            }
            if (RLOMod.Verify)
            {
                text += $"; verify: {verified} cached offsets compared with a fresh read-back, {mismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
