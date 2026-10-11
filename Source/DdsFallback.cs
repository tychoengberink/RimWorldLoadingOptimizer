using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class DdsFallback
    {
        private static readonly MethodInfo LoadPng = AccessTools.Method(typeof(ModContentLoader<Texture2D>), "LoadTextureViaImageConversion");
        private static readonly ConstructorInfo FileCtor = AccessTools.Constructor(AccessTools.TypeByName("RimWorld.IO.FilesystemFile"), new[] { typeof(FileInfo) });

        public static int Recovered;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ModDdsLoader), nameof(ModDdsLoader.TryLoadDds)),
                finalizer: new HarmonyMethod(typeof(DdsFallback), nameof(Finalizer)));
        }

        public static Exception Finalizer(Exception __exception, VirtualFile file, ref Texture2D __result)
        {
            if (__exception == null || file == null || LoadPng == null || FileCtor == null)
            {
                return __exception;
            }
            try
            {
                var png = Path.ChangeExtension(file.FullPath, ".png");
                if (!File.Exists(png))
                {
                    png = Path.ChangeExtension(file.FullPath, ".PNG");
                }
                if (!File.Exists(png))
                {
                    return __exception;
                }
                var pngFile = (VirtualFile)FileCtor.Invoke(new object[] { new FileInfo(png) });
                var texture = (Texture2D)LoadPng.Invoke(null, new object[] { pngFile });
                if (texture == null)
                {
                    return __exception;
                }
                texture.name = Path.GetFileNameWithoutExtension(file.Name);
                __result = texture;
                Recovered++;
                Log.Warning($"[RLO] {file.FullPath} could not be loaded ({__exception.Message}); loaded the PNG instead.");
                return null;
            }
            catch
            {
                return __exception;
            }
        }
    }
}
