#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Waylith.Rendering;

namespace RimWorldLoadingOptimizer
{
    // Vehicle Framework builds its pattern materials in VehicleHarmony's static constructor (PatternDef.GenerateMaterials). Every pattern
    // without its own rotated textures is rotated three times from its base texture, and each rotation reads the base texture's pixels
    // twice (Ext_Texture.Rotate and RotateSquare), so the same block-compressed level is decoded to RGBA32 on the CPU six times in a row.
    // While GenerateMaterials runs, the engine's decode (AssetBundleTextures.DecodeToRgba32, a pure function of the level bytes, format and
    // size) remembers its last few results, keyed on a copy of the exact input bytes, and hands out a fresh copy of the remembered result,
    // so every caller still gets its own array with the same pixels.
    public static class VehiclePatternDecodes
    {
        public const string Feature = "Vehicle Framework pattern decodes";

        private const int MaxEntries = 4;

        public sealed class Entry
        {
            public byte[] Input;
            public LoadedTextureFormat Format;
            public int Width;
            public int Height;
            public byte[] Result;
        }

        public sealed class State
        {
            public Entry Hit;
            public long Start;
        }

        // Most recently used first.
        private static readonly List<Entry> entries = new List<Entry>(MaxEntries);
        private static int activeThread = -1;
        private static int depth;
        private static int hits, misses, uncached, verified, mismatches, runs;
        private static long missTicks, hitTicks;
        private static bool verify = RLOMod.Verify;

        public static void Install(Harmony harmony)
        {
            var pattern = ModTypes.Find("Vehicles.PatternDef");
            if (pattern == null)
            {
                return;
            }
            var generate = AccessTools.Method(pattern, "GenerateMaterials");
            var textures = typeof(UnityEngine.Texture2D).Assembly.GetType("UnityStubs.Internal.AssetBundleTextures");
            var decodeToRgba32 = textures == null ? null : AccessTools.Method(textures, "DecodeToRgba32", new[] { typeof(byte[]), typeof(LoadedTextureFormat), typeof(int), typeof(int) });
            var decode = textures == null ? null : AccessTools.Method(textures, "Decode");
            var managed = Type.GetType("AssetsTools.NET.Texture.TextureFile, AssetsTools.NET.Texture", false);
            var decodeManaged = managed == null ? null : AccessTools.Method(managed, "DecodeManagedData");
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(generate, "38038F710768ECA2", "72E5F1534AE0CFD2"),
                Fingerprint.Known(decodeToRgba32, "D8977CB3584AE2C8", "97F35E89D5000577"),
                Fingerprint.Known(decode, "4356C116020FB0B6", "F1AA5FA6EBB78524"),
                Fingerprint.Known(decodeManaged, "94F053556E3BF624", "15CFDCADBCDD9CFB")))
            {
                return;
            }
            harmony.Patch(generate, prefix: new HarmonyMethod(typeof(VehiclePatternDecodes), nameof(Begin)), finalizer: new HarmonyMethod(typeof(VehiclePatternDecodes), nameof(End)));
            harmony.Patch(decodeToRgba32, prefix: new HarmonyMethod(typeof(VehiclePatternDecodes), nameof(DecodePrefix)), postfix: new HarmonyMethod(typeof(VehiclePatternDecodes), nameof(DecodePostfix)));
            MainMenu.UnpatchAfterStartup(generate);
            MainMenu.UnpatchAfterStartup(decodeToRgba32);
            LeanFixes.Reports.Add(Stats);
        }

        public static void Begin()
        {
            var thread = Environment.CurrentManagedThreadId;
            if (depth > 0 && activeThread != thread)
            {
                return;
            }
            activeThread = thread;
            if (depth++ == 0)
            {
                runs++;
                entries.Clear();
            }
        }

        public static Exception End(Exception __exception)
        {
            if (depth > 0 && activeThread == Environment.CurrentManagedThreadId && --depth == 0)
            {
                activeThread = -1;
                entries.Clear();
            }
            return __exception;
        }

        public static bool DecodePrefix(byte[] data, LoadedTextureFormat format, int w, int h, ref byte[] __result, out State __state)
        {
            __state = null;
            if (depth == 0 || activeThread != Environment.CurrentManagedThreadId || data == null)
            {
                return true;
            }
            var start = Stopwatch.GetTimestamp();
            var hit = Find(data, format, w, h);
            if (hit != null && !verify)
            {
                __result = (byte[])hit.Result.Clone();
                hits++;
                hitTicks += Stopwatch.GetTimestamp() - start;
                return false;
            }
            __state = new State { Hit = hit, Start = start };
            return true;
        }

        public static void DecodePostfix(byte[] data, LoadedTextureFormat format, int w, int h, byte[] __result, State __state)
        {
            if (__state == null)
            {
                return;
            }
            if (__state.Hit != null)
            {
                verified++;
                if (__result == null || !__result.AsSpan().SequenceEqual(__state.Hit.Result))
                {
                    mismatches++;
                    entries.Remove(__state.Hit);
                }
                return;
            }
            missTicks += Stopwatch.GetTimestamp() - __state.Start;
            if (__result == null)
            {
                uncached++;
                return;
            }
            misses++;
            if (entries.Count >= MaxEntries)
            {
                entries.RemoveAt(entries.Count - 1);
            }
            entries.Insert(0, new Entry { Input = (byte[])data.Clone(), Format = format, Width = w, Height = h, Result = (byte[])__result.Clone() });
        }

        private static Entry Find(byte[] data, LoadedTextureFormat format, int w, int h)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Format == format && entry.Width == w && entry.Height == h && entry.Input.Length == data.Length && entry.Input.AsSpan().SequenceEqual(data))
                {
                    if (i > 0)
                    {
                        entries.RemoveAt(i);
                        entries.Insert(0, entry);
                    }
                    return entry;
                }
            }
            return null;
        }

        private static string Stats()
        {
            if (runs == 0)
            {
                return $"{Feature}: Vehicle Framework did not build its pattern materials during startup";
            }
            var missMs = misses > 0 ? LeanFixes.Seconds(missTicks) * 1000 / misses : 0;
            var text = $"{Feature}: {misses} pattern textures decoded in {LeanFixes.Seconds(missTicks):F2} s, {hits} repeat decodes answered from memory in {LeanFixes.Seconds(hitTicks):F2} s, about {hits * missMs / 1000 - LeanFixes.Seconds(hitTicks):F1} s saved";
            if (uncached > 0)
            {
                text += $", {uncached} decodes failed (not remembered)";
            }
            if (verify)
            {
                text += $"; verify: {verified} remembered decodes compared with a fresh decode, {mismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
