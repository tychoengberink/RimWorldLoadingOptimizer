#if RW17
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasPageBuffers
    {
        private const int StockLimit = 64 << 20;

        private static FieldInfo buffers;
        private static ArrayPool<byte> saved;
        private static LargeBufferPool pool;
        private static int bakes;
        private static long largeRents, largeBytes, reusedBytes, freshArrays;

        public static void Install(Harmony harmony)
        {
            var store = AccessTools.TypeByName("Waylith.Rendering.TextureCacheStore");
            var source = store == null ? null : AccessTools.Inner(store, "PackedTextureSource");
            buffers = store == null ? null : AccessTools.Field(store, "_buffers");
            if (source == null || buffers == null || !buffers.IsStatic || buffers.IsInitOnly || buffers.FieldType != typeof(ArrayPool<byte>))
            {
                Log.Message("[RLO] atlas page buffers: the game's texture cache buffer pool was not found; skipped");
                return;
            }
            var bake = AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases));
            if (!Fingerprint.Matches("Atlas page buffers",
                Fingerprint.Known(bake, "45434BFD7AD5A8A9"),
                Fingerprint.Known(AccessTools.Method(source, "TryOpen"), "A2C673D6F21BD239"),
                Fingerprint.Known(AccessTools.Method(source, "Dispose"), "B84E6FDCE38F9583"),
                Fingerprint.Known(AccessTools.Method(store, "Store"), "5F42E9546B3CD097"),
                Fingerprint.Known(AccessTools.Method(store, "ReleaseBuffers"), "06DEC407375A2CF0"),
                Fingerprint.Known(AccessTools.Method(store, "NewBufferPool"), "EE90BB14052FB05A")))
            {
                return;
            }
            harmony.Patch(bake, prefix: new HarmonyMethod(typeof(AtlasPageBuffers), nameof(Prefix)), finalizer: new HarmonyMethod(typeof(AtlasPageBuffers), nameof(Finalizer)));
            MainMenu.UnpatchAfterStartup(bake);
            LeanFixes.Reports.Add(Stats);
        }

        public static void Prefix()
        {
            if (pool != null)
            {
                return;
            }
            saved = (ArrayPool<byte>)buffers.GetValue(null);
            pool = new LargeBufferPool(RLOMod.Verify);
            buffers.SetValue(null, pool);
            bakes++;
        }

        public static Exception Finalizer(Exception __exception)
        {
            if (pool != null)
            {
                if (ReferenceEquals(buffers.GetValue(null), pool))
                {
                    buffers.SetValue(null, saved);
                }
                pool = null;
                saved = null;
            }
            return __exception;
        }

        private static string Stats()
        {
            if (bakes == 0)
            {
                return "atlas page buffers: atlas baking not reached";
            }
            var verify = RLOMod.Verify ? "; verify: buffers were filled with junk before every use, so a page that still loads from the cache did not depend on fresh memory (compare the atlas cache hit count with a normal run)" : "";
            return $"atlas page buffers: {Interlocked.Read(ref largeRents)} cache reads over {StockLimit >> 20} MB ({Interlocked.Read(ref largeBytes) >> 20} MB) reusing {Interlocked.Read(ref freshArrays)} buffer(s), {Interlocked.Read(ref reusedBytes) >> 20} MB of large allocations avoided{verify}";
        }

        private sealed class LargeBufferPool : ArrayPool<byte>
        {
            private readonly ArrayPool<byte> inner = Create(1 << 29, Environment.ProcessorCount);
            private readonly HashSet<byte[]> seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
            private readonly bool poison;

            public LargeBufferPool(bool poison)
            {
                this.poison = poison;
            }

            public override byte[] Rent(int minimumLength)
            {
                var array = inner.Rent(minimumLength);
                if (minimumLength > StockLimit)
                {
                    Interlocked.Increment(ref largeRents);
                    Interlocked.Add(ref largeBytes, minimumLength);
                    bool fresh;
                    lock (seen)
                    {
                        fresh = seen.Add(array);
                    }
                    if (fresh)
                    {
                        Interlocked.Increment(ref freshArrays);
                    }
                    else
                    {
                        Interlocked.Add(ref reusedBytes, minimumLength);
                    }
                }
                if (poison)
                {
                    array.AsSpan().Fill(0xCD);
                }
                return array;
            }

            public override void Return(byte[] array, bool clearArray = false)
            {
                if (array != null && array.Length >= 16 && (array.Length & (array.Length - 1)) == 0)
                {
                    inner.Return(array, clearArray);
                }
            }
        }
    }
}
#endif
