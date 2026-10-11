using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GiddyOffsets
    {
        public struct State
        {
            public long Start;
            public float? Fast;
        }

        private static readonly Dictionary<Texture2D, int> backHeights = new Dictionary<Texture2D, int>();
        private static int calls, cacheHits, readbacks, mismatches, installed;
        private static long ticks, originalTicks;

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("GiddyUp.TextureUtility");
            var method = type == null ? null : AccessTools.Method(type, "SetDrawOffset", new[] { typeof(PawnKindLifeStage) });
            if (method == null || !Fingerprint.Matches("Giddy-Up draw offsets", Fingerprint.Known(method, "51410F38A5B67C62", "3B1977565FB50122")))
            {
                return;
            }
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(GiddyOffsets), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(GiddyOffsets), nameof(Postfix)));
            installed = 1;
        }

        public static bool Prefix(PawnKindLifeStage age, ref float? __result, out State __state)
        {
            var start = Stopwatch.GetTimestamp();
            calls++;
            __state = new State { Fast = Compute(age) };
            ticks += Stopwatch.GetTimestamp() - start;
            if (RLOMod.Verify)
            {
                __state.Start = Stopwatch.GetTimestamp();
                return true;
            }
            __result = __state.Fast;
            return false;
        }

        public static void Postfix(float? __result, State __state, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return;
            }
            originalTicks += Stopwatch.GetTimestamp() - __state.Start;
            var same = __result.HasValue == __state.Fast.HasValue && (!__result.HasValue || System.BitConverter.DoubleToInt64Bits(__result.Value) == System.BitConverter.DoubleToInt64Bits(__state.Fast.Value));
            if (!same && ++mismatches <= 10)
            {
                Log.Warning($"[RLO] Giddy-Up offset verify mismatch: fast {__state.Fast}, original {__result}");
            }
        }

        private static float? Compute(PawnKindLifeStage age)
        {
            object obj;
            if (age == null)
            {
                obj = null;
            }
            else
            {
                var bodyGraphicData = age.bodyGraphicData;
                if (bodyGraphicData == null)
                {
                    obj = null;
                }
                else
                {
                    var graphic = bodyGraphicData.Graphic;
                    if (graphic == null)
                    {
                        obj = null;
                    }
                    else
                    {
                        var matEast = graphic.MatEast;
                        obj = matEast != null ? matEast.mainTexture : null;
                    }
                }
            }
            if ((Object)(obj as Texture2D) == (Object)null)
            {
                return null;
            }
            var texture = age.bodyGraphicData.Graphic.MatEast.mainTexture as Texture2D;
            float num = (float)BackHeight(texture) / (float)texture.height;
            float y = age.bodyGraphicData.drawSize.y;
            float num2 = y > 1f ? (y - 1f) / 2f : 0f;
            num2 += y * num / 20f;
            return y * num - num2;
        }

        private static int BackHeight(Texture2D texture)
        {
            if (backHeights.TryGetValue(texture, out var cached))
            {
                cacheHits++;
                return cached;
            }
            readbacks++;
            var width = texture.width;
            var height = texture.height;
            var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
            Graphics.Blit(texture, temporary);
            var active = RenderTexture.active;
            RenderTexture.active = temporary;
            var column = new Texture2D(1, height);
            column.ReadPixels(new Rect(width / 2, 0f, 1f, height), 0, 0);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(temporary);
            var backHeight = 0;
            var seenOpaque = false;
            var threshold = 0.8f;
            for (var i = 0; i < height; i++)
            {
                var pixel = column.GetPixel(0, i);
                if (seenOpaque && pixel.a < threshold)
                {
                    backHeight = i;
                    break;
                }
                if (pixel.a >= threshold)
                {
                    seenOpaque = true;
                }
            }
            Object.Destroy(column);
            backHeights[texture] = backHeight;
            return backHeight;
        }

        public static string Stats()
        {
            if (installed == 0)
            {
                return "Giddy-Up draw offsets: not patched";
            }
            var msPerTick = 1000.0 / Stopwatch.Frequency;
            return $"Giddy-Up draw offsets: {calls} life stages in {ticks * msPerTick:F0} ms ({readbacks} column readbacks, {cacheHits} same-texture hits){(RLOMod.Verify ? $"; original took {originalTicks * msPerTick:F0} ms" : "")}, {mismatches} verify mismatches";
        }
    }
}
