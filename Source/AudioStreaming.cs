using System;
using System.Threading;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AudioStreaming
    {
        private const long MinStreamBytes = 100 * 1024;

        [ThreadStatic] private static int yieldUntil;
        [ThreadStatic] private static bool yieldArmed;

        public static int YieldedWaits;
        public static int Streamed;
        public static long StreamedBytes;

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ModContentLoader<AudioClip>), "ShouldStreamAudioClipFromFile");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(AudioStreaming), nameof(Postfix)));
            harmony.Patch(AccessTools.Method(typeof(Thread), nameof(Thread.Sleep), new[] { typeof(int) }),
                prefix: new HarmonyMethod(typeof(AudioStreaming), nameof(SleepPrefix)));
        }

        public static bool SleepPrefix(int millisecondsTimeout)
        {
            if (millisecondsTimeout != 1 || !yieldArmed || unchecked(Environment.TickCount - yieldUntil) > 0)
            {
                return true;
            }
            YieldedWaits++;
            Thread.Yield();
            return false;
        }

        public static void Postfix(VirtualFile file, ref bool __result)
        {
            yieldUntil = unchecked(Environment.TickCount + 250);
            yieldArmed = true;
            if (__result || file.GetType().Name != "FilesystemFile" || !file.Exists || !file.Name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var length = file.Length;
            if (length >= MinStreamBytes)
            {
                __result = true;
                Streamed++;
                StreamedBytes += length;
            }
        }
    }
}
