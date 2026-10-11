using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class VehiclePatterns
    {
        private static MethodInfo createReadable;
        private static MethodInfo rotateSquare;
        private static Action<Texture2D, Texture2D> compressAndApply;
        private static int depth;
        private static Texture2D cachedSource;
        private static Color32[] cachedPixels;
        private static int installed, rotations, readbacks, mismatches;
        private static long ticks;

        public static void Install(Harmony harmony)
        {
            var textureExtensions = ModTypes.Find("SmashTools.Ext_Texture");
            var pattern = ModTypes.Find("Vehicles.PatternDef");
            if (textureExtensions == null || pattern == null)
            {
                return;
            }
            var rotate = AccessTools.Method(textureExtensions, "Rotate", new[] { typeof(Texture2D), typeof(float) });
            createReadable = AccessTools.Method(textureExtensions, "CreateReadableTexture", new[] { typeof(Texture2D), typeof(TextureWrapMode?) });
            rotateSquare = AccessTools.Method(textureExtensions, "RotateSquare", new[] { typeof(Color32[]), typeof(float), typeof(Texture2D) });
            var compress = AccessTools.Method(textureExtensions, "CompressAndApply", new[] { typeof(Texture2D), typeof(Texture2D) });
            var recache = AccessTools.Method(pattern, "RecacheTextures");
            if (rotate == null || rotate.ReturnType != typeof(Texture2D) || createReadable == null || rotateSquare == null || compress == null || recache == null)
            {
                return;
            }
            if (!Fingerprint.Matches("Vehicle Framework pattern rotation",
                Fingerprint.Known(rotate, "FE23678F9C93CF9E", "52018DD616D24030"),
                Fingerprint.Known(rotateSquare, "4E55AA618E333C64", "1114607E4F365B7F"),
                Fingerprint.Known(createReadable, "07D59C524A4A9F0C", "A5518944D4B84B2B"),
                Fingerprint.Known(recache, "13DE2A7CFBC8EE1D", "11EB661ACD87B954")))
            {
                return;
            }
            compressAndApply = (Action<Texture2D, Texture2D>)Delegate.CreateDelegate(typeof(Action<Texture2D, Texture2D>), compress);
            harmony.Patch(recache, prefix: new HarmonyMethod(typeof(VehiclePatterns), nameof(Enter)), finalizer: new HarmonyMethod(typeof(VehiclePatterns), nameof(Leave)));
            harmony.Patch(rotate, prefix: new HarmonyMethod(typeof(VehiclePatterns), nameof(Prefix)));
            installed = 1;
        }

        public static void Enter()
        {
            if (UnityData.IsInMainThread)
            {
                depth++;
            }
        }

        public static void Leave(Exception __exception)
        {
            if (UnityData.IsInMainThread && depth > 0 && --depth == 0)
            {
                cachedSource = null;
                cachedPixels = null;
            }
        }

        public static bool Prefix(Texture2D source, float angle, ref Texture2D __result)
        {
            if (depth == 0 || !UnityData.IsInMainThread || (object)source == null || source.isReadable || source.width != source.height
                || (!Mathf.Approximately(angle, 90f) && !Mathf.Approximately(angle, 180f) && !Mathf.Approximately(angle, 270f)))
            {
                return true;
            }
            var start = Stopwatch.GetTimestamp();
            rotations++;
            var width = source.width;
            var height = source.height;
            var rotated = new Texture2D(width, height, TextureFormat.RGBA32, source.mipmapCount > 1)
            {
                name = source.name
            };
            if (!ReferenceEquals(cachedSource, source) || cachedPixels == null)
            {
                var readable = (Texture2D)createReadable.Invoke(null, new object[] { source, null });
                cachedPixels = readable.GetPixels32();
                cachedSource = source;
                UnityEngine.Object.Destroy(readable);
                readbacks++;
            }
            var pixels = Rotate(cachedPixels, angle * ((float)Math.PI / 180f), width, height);
            if (RLOMod.Verify)
            {
                Check(source, angle, pixels);
            }
            rotated.SetPixels32(pixels);
            compressAndApply(source, rotated);
            __result = rotated;
            ticks += Stopwatch.GetTimestamp() - start;
            return false;
        }

        private static Color32[] Rotate(Color32[] sourcePixels, float radians, int width, int height)
        {
            var sin = Mathf.Sin(radians);
            var cos = Mathf.Cos(radians);
            var pixels = new Color32[width * height];
            var cx = width / 2;
            var cy = height / 2;
            Parallel.For(0, height, i =>
            {
                for (var j = 0; j < width; j++)
                {
                    var x = (int)(cos * (float)(j - cx) + sin * (float)(i - cy) + (float)cx);
                    var y = (int)((0f - sin) * (float)(j - cx) + cos * (float)(i - cy) + (float)cy);
                    pixels[i * width + j] = x > -1 && x < width && y > -1 && y < height ? sourcePixels[y * width + x] : new Color32(0, 0, 0, 0);
                }
            });
            return pixels;
        }

        private static void Check(Texture2D source, float angle, Color32[] pixels)
        {
            var readable = (Texture2D)createReadable.Invoke(null, new object[] { source, null });
            var fresh = readable.GetPixels32();
            var expected = (Color32[])rotateSquare.Invoke(null, new object[] { fresh, angle * ((float)Math.PI / 180f), readable });
            UnityEngine.Object.Destroy(readable);
            var same = expected.Length == pixels.Length && fresh.Length == cachedPixels.Length;
            for (var i = 0; same && i < fresh.Length; i++)
            {
                same = fresh[i].r == cachedPixels[i].r && fresh[i].g == cachedPixels[i].g && fresh[i].b == cachedPixels[i].b && fresh[i].a == cachedPixels[i].a;
            }
            for (var i = 0; same && i < pixels.Length; i++)
            {
                same = expected[i].r == pixels[i].r && expected[i].g == pixels[i].g && expected[i].b == pixels[i].b && expected[i].a == pixels[i].a;
            }
            if (!same && mismatches++ < 10)
            {
                Log.Warning($"[RLO] vehicles verify mismatch: rotation of {source.name} by {angle}");
            }
        }

        public static string Stats()
        {
            if (installed == 0)
            {
                return "vehicles: off";
            }
            return $"vehicles: {rotations} pattern rotations in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms with {readbacks} GPU readbacks; {mismatches} verify mismatches";
        }
    }
}
