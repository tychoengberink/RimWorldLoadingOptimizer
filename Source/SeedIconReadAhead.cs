#if RW17
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Waylith.Rendering;

namespace RimWorldLoadingOptimizer
{
    // PA Dynamic Seed Icons loads one cached 768x768 PNG per seed bundle on the main thread when loading ends
    // (SeedIconGenerator.TryLoadFromCache: File.ReadAllBytes, then ImageConversion.LoadImage, which decodes it).
    // Once the bundles exist, this reads those files on workers and decodes them with the engine's own decoder
    // (GpuTextureService.TryLoadTextureFromMemory, the call LoadImage makes). TryLoadFromCache then gets the same bytes, and the
    // decode inside LoadImage returns the result already made for exactly that byte array. The textures are still made by the
    // mod's own code on the main thread; a file that is not ready is read and decoded there as before.
    public static class SeedIconReadAhead
    {
        public const string Feature = "PA Dynamic Seed Icons read-ahead";
        private const long CapBytes = 160L << 20;

        private sealed class Entry
        {
            public byte[] Bytes;
            public bool Decoded;
            public LoadedTextureData Texture;

            public long Size => Bytes.LongLength + (Decoded && Texture.Levels != null ? Texture.Levels.Sum(l => (long)(l?.Length ?? 0)) : 0);
        }

        private static MethodInfo readAllBytes, tryLoad, decodeMethod, cacheDir;
        private static FieldInfo bundlesField, cropField;
        private static Type unlockType;
        private static volatile OrderedReadAhead<Entry> queue;
        private static volatile bool done;
        private static byte[] handedBytes;
        private static LoadedTextureData handedTexture;
        private static int started, finished, readRedirected, decodeRedirected;
        private static int handed, ownReads, otherPaths, compared, mismatches;
        private static long ownTicks;

        public static void Install(Harmony harmony)
        {
            var generator = AccessTools.TypeByName("PA_DynamicSeedIcons.SeedIconGenerator");
            if (generator == null || PlayDataLoader.Loaded)
            {
                return;
            }
            var patchType = AccessTools.TypeByName("ProgressionAgriculture.DefGenerator_GenerateImpliedDefs_PreResolve_Patch");
            unlockType = AccessTools.TypeByName("ProgressionAgriculture.CompProperties_UnlockCrop");
            tryLoad = AccessTools.Method(generator, "TryLoadFromCache");
            var applyAll = AccessTools.Method(generator, "ApplyAll");
            var applyOne = AccessTools.Method(generator, "ApplyOne");
            cacheDir = AccessTools.PropertyGetter(generator, "CacheDir");
            var paPostfix = patchType == null ? null : AccessTools.Method(patchType, "Postfix");
            bundlesField = patchType == null ? null : AccessTools.Field(patchType, "generatedBundles");
            cropField = unlockType == null ? null : AccessTools.Field(unlockType, "cropDef");
            readAllBytes = AccessTools.Method(typeof(File), nameof(File.ReadAllBytes), new[] { typeof(string) });
            decodeMethod = AccessTools.Method(typeof(GpuTextureService), nameof(GpuTextureService.TryLoadTextureFromMemory));
            var engineLoad = AccessTools.Method(typeof(Waylith.Rendering.Texture2D), nameof(Waylith.Rendering.Texture2D.LoadEncodedImage));
            var stubLoad = AccessTools.Method(typeof(UnityEngine.Texture2D), "LoadEncodedImage");
            var loadImage = AccessTools.Method(typeof(UnityEngine.ImageConversion), nameof(UnityEngine.ImageConversion.LoadImage), new[] { typeof(UnityEngine.Texture2D), typeof(byte[]) });
            var preResolve = AccessTools.Method(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve));
            if (bundlesField == null || cropField?.FieldType != typeof(ThingDef) || cacheDir?.ReturnType != typeof(string) || !typeof(IEnumerable).IsAssignableFrom(bundlesField.FieldType))
            {
                Log.Message($"[RLO] {Feature}: a member it needs is missing. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(tryLoad, "AC123FC6D8750681"),
                Fingerprint.Known(applyAll, "A932E92B416FF809"),
                Fingerprint.Known(applyOne, "7E08BE53C794DD55"),
                Fingerprint.Known(cacheDir, "934C28E9FB6C2D6A"),
                Fingerprint.Known(paPostfix, "1E24D7F6DB9FFB9F"),
                Fingerprint.Known(loadImage, "52D49F0E5D99F88D"),
                Fingerprint.Known(stubLoad, "5204635272A28453"),
                Fingerprint.Known(engineLoad, "AE0EA0C746AD4AD8"),
                Fingerprint.Known(decodeMethod, "98D51764E2E9E45D"),
                Fingerprint.Known(preResolve, "CE34043FA0D506B5")))
            {
                return;
            }
            harmony.Patch(tryLoad, transpiler: new HarmonyMethod(typeof(SeedIconReadAhead), nameof(ReadTranspiler)));
            harmony.Patch(engineLoad, transpiler: new HarmonyMethod(typeof(SeedIconReadAhead), nameof(DecodeTranspiler)));
            if (readRedirected != 1 || decodeRedirected != 1)
            {
                harmony.Unpatch(tryLoad, HarmonyPatchType.Transpiler, harmony.Id);
                harmony.Unpatch(engineLoad, HarmonyPatchType.Transpiler, harmony.Id);
                Log.Message($"[RLO] {Feature}: the file read and decode calls were not found as expected ({readRedirected} and {decodeRedirected} of 1). This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            harmony.Patch(preResolve, postfix: new HarmonyMethod(typeof(SeedIconReadAhead), nameof(Begin)) { priority = Priority.Last });
            harmony.Patch(applyAll, postfix: new HarmonyMethod(typeof(SeedIconReadAhead), nameof(Finish)));
            MainMenu.UnpatchAfterStartup(preResolve);
            MainMenu.UnpatchAfterStartup(tryLoad);
            MainMenu.UnpatchAfterStartup(engineLoad);
            MainMenu.UnpatchAfterStartup(applyAll);
            MainMenu.Then(Finish);
            LeanFixes.Reports.Add(Stats);
        }

        private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions, MethodInfo from, string to, out int count)
        {
            var list = instructions.ToList();
            count = list.Count(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m && m == from);
            if (count != 1)
            {
                return list;
            }
            foreach (var instruction in list)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m && m == from)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(SeedIconReadAhead), to);
                }
            }
            return list;
        }

        public static IEnumerable<CodeInstruction> ReadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = Redirect(instructions, readAllBytes, nameof(ReadAllBytes), out var count);
            readRedirected = count;
            return list;
        }

        public static IEnumerable<CodeInstruction> DecodeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = Redirect(instructions, decodeMethod, nameof(Decode), out var count);
            decodeRedirected = count;
            return list;
        }

        // After Progression: Agriculture's own postfix has made the seed bundles; the icons are applied when loading ends.
        public static void Begin()
        {
            if (Interlocked.Exchange(ref started, 1) != 0 || done)
            {
                return;
            }
            string dir;
            var files = new List<string>();
            try
            {
                dir = (string)cacheDir.Invoke(null, null);
                foreach (var bundle in (IEnumerable)bundlesField.GetValue(null) ?? Array.Empty<object>())
                {
                    var crop = (bundle as ThingDef)?.comps?.FirstOrDefault(c => unlockType.IsInstanceOfType(c)) is CompProperties comp ? cropField.GetValue(comp) as ThingDef : null;
                    if (crop?.defName != null)
                    {
                        files.Add(Path.Combine(dir, crop.defName + ".png"));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not list the seed bundles ({e.Message}); skipped");
                return;
            }
            var made = new OrderedReadAhead<Entry>(files, Make, entry => entry.Size, _ => { }, CapBytes);
            if (made.Count == 0)
            {
                return;
            }
            queue = made;
            made.Start(Math.Max(1, Math.Min(3, Environment.ProcessorCount / 4)), "RLO seed icon read-ahead");
        }

        private static Entry Make(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var entry = new Entry { Bytes = File.ReadAllBytes(path) };
            try
            {
                entry.Decoded = GpuTextureService.TryLoadTextureFromMemory(entry.Bytes, out entry.Texture);
            }
            catch (Exception)
            {
                entry.Decoded = false;
            }
            if (!entry.Decoded)
            {
                entry.Texture = default;
            }
            return entry;
        }

        public static byte[] ReadAllBytes(string path)
        {
            var q = queue;
            if (q == null || done || !UnityData.IsInMainThread)
            {
                return File.ReadAllBytes(path);
            }
            if (!q.Contains(path))
            {
                otherPaths++;
                return File.ReadAllBytes(path);
            }
            if (!q.TryTake(path, true, out var entry))
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    return File.ReadAllBytes(path);
                }
                finally
                {
                    ownReads++;
                    ownTicks += Stopwatch.GetTimestamp() - start;
                }
            }
            if (RLOMod.Verify)
            {
                var fresh = File.ReadAllBytes(path);
                compared++;
                if (!fresh.AsSpan().SequenceEqual(entry.Bytes))
                {
                    Mismatch(path, "file bytes differ");
                    return fresh;
                }
            }
            if (entry.Decoded)
            {
                handedBytes = entry.Bytes;
                handedTexture = entry.Texture;
            }
            return entry.Bytes;
        }

        // GpuTextureService.TryLoadTextureFromMemory, called from Texture2D.LoadEncodedImage; answers from the decode made ahead
        // only for the exact byte array ReadAllBytes just handed out.
        public static bool Decode(ReadOnlySpan<byte> bytes, out LoadedTextureData loadedTexture)
        {
            var mine = handedBytes;
            if (mine == null || mine.Length == 0 || bytes.Length != mine.Length || !UnityData.IsInMainThread
                || !Unsafe.AreSame(ref MemoryMarshal.GetReference(bytes), ref MemoryMarshal.GetArrayDataReference(mine)))
            {
                return GpuTextureService.TryLoadTextureFromMemory(bytes, out loadedTexture);
            }
            loadedTexture = handedTexture;
            handedBytes = null;
            handedTexture = default;
            handed++;
            if (RLOMod.Verify)
            {
                var ok = GpuTextureService.TryLoadTextureFromMemory(bytes, out var fresh);
                var difference = !ok ? "a fresh decode failed" : Difference(loadedTexture, fresh);
                if (difference != null)
                {
                    Mismatch("decoded icon", difference);
                    loadedTexture = fresh;
                    return ok;
                }
            }
            return true;
        }

        private static string Difference(in LoadedTextureData a, in LoadedTextureData b)
        {
            if (a.Width != b.Width || a.Height != b.Height || a.MipCount != b.MipCount || a.Format != b.Format || a.Opaque != b.Opaque || a.SourceStamp != b.SourceStamp || !a.Swizzle.Equals(b.Swizzle))
            {
                return $"{a.Width}x{a.Height} {a.Format} mips {a.MipCount} opaque {a.Opaque} vs {b.Width}x{b.Height} {b.Format} mips {b.MipCount} opaque {b.Opaque}";
            }
            if (a.Levels == null || b.Levels == null || a.Levels.Length != b.Levels.Length)
            {
                return "level count differs";
            }
            for (var i = 0; i < a.Levels.Length; i++)
            {
                if (!a.Levels[i].AsSpan().SequenceEqual(b.Levels[i]))
                {
                    return $"level {i} pixels differ";
                }
            }
            return null;
        }

        private static void Mismatch(string what, string difference)
        {
            if (++mismatches <= 5)
            {
                Log.Warning($"[RLO] {Feature} verify mismatch: {what}: {difference}");
            }
        }

        public static void Finish()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0)
            {
                return;
            }
            done = true;
            handedBytes = null;
            handedTexture = default;
            queue?.Stop();
            if (RLOMod.Verify)
            {
                Log.Message($"[RLO] {Feature} verify: {compared} compared, {mismatches} mismatches");
            }
        }

        private static string Stats()
        {
            var q = queue;
            if (q == null)
            {
                return $"{Feature}: {(started == 0 ? "off" : "nothing read ahead")}";
            }
            var text = $"{Feature}: {q.Summary("cached seed icons")}; {handed} decodes handed over, {ownReads} listed files read by the main thread itself in {LeanFixes.Seconds(ownTicks):F2} s, {otherPaths} files outside the list";
            if (RLOMod.Verify)
            {
                text += $"; verify: {compared} compared, {mismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
