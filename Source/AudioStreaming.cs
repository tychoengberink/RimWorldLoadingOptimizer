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

        private static readonly Type FilesystemFile = AccessTools.TypeByName("RimWorld.IO.FilesystemFile");

        public static int YieldedWaits;
        public static int Streamed;
        public static long StreamedBytes;

        public static void Install(Harmony harmony)
        {
            if (FilesystemFile == null)
            {
                return;
            }
            var target = AccessTools.Method(typeof(ModContentLoader<AudioClip>), "ShouldStreamAudioClipFromFile");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(AudioStreaming), nameof(Postfix)));
            var sleep = AccessTools.Method(typeof(Thread), nameof(Thread.Sleep), new[] { typeof(int) });
            harmony.Patch(sleep, prefix: new HarmonyMethod(typeof(AudioStreaming), nameof(SleepPrefix)));
            MainMenu.UnpatchAfterStartup(sleep);
        }

        public static bool SleepPrefix(int millisecondsTimeout)
        {
            if (millisecondsTimeout != 1 || !yieldArmed || unchecked(Environment.TickCount - yieldUntil) > 0)
            {
                return true;
            }
            Interlocked.Increment(ref YieldedWaits);
            Thread.Yield();
            return false;
        }

        public static void Postfix(VirtualFile file, ref bool __result)
        {
            yieldUntil = unchecked(Environment.TickCount + 250);
            yieldArmed = true;
            if (__result || !FilesystemFile.IsInstanceOfType(file) || !file.Exists || !file.Name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var length = file.Length;
            if (length >= MinStreamBytes)
            {
                __result = true;
                Interlocked.Increment(ref Streamed);
                Interlocked.Add(ref StreamedBytes, length);
            }
        }
    }
}
