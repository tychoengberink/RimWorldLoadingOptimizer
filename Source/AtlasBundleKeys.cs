#if RW17
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Texture2D = UnityEngine.Texture2D;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasBundleKeys
    {
        public const string Feature = "Atlas cache keys for bundle textures";

        private static readonly ConditionalWeakTable<Texture2D, string> Origins = new ConditionalWeakTable<Texture2D, string>();
        private static readonly ConcurrentDictionary<string, string> FileStamps = new ConcurrentDictionary<string, string>();

        private static FieldInfo filePath, refFile, refPathId, fileName;
        private static int recorded, stamped;

        public static void Install(Harmony harmony)
        {
            var reader = AccessTools.TypeByName("UnityStubs.Internal.AssetBundleReader");
            var assetRef = reader == null ? null : AccessTools.Inner(reader, "AssetRef");
            var materialize = assetRef == null ? null : AccessTools.Method(reader, "Materialize", new[] { assetRef });
            var stamp = AccessTools.Method(typeof(StaticTextureAtlas), "TryWriteSourceStamp", new[] { typeof(BinaryWriter), typeof(Texture2D) });
            filePath = reader == null ? null : AccessTools.Field(reader, "_filePath");
            refFile = assetRef == null ? null : AccessTools.Field(assetRef, "File");
            refPathId = assetRef == null ? null : AccessTools.Field(assetRef, "PathId");
            fileName = refFile == null ? null : AccessTools.Field(refFile.FieldType, "name");
            if (materialize == null || stamp == null || filePath == null || refPathId == null || fileName == null || stamp.ReturnType != typeof(bool))
            {
                VersionCheck.Skip(Feature, "a method or field it needs no longer exists");
                return;
            }
            harmony.Patch(materialize, postfix: new HarmonyMethod(typeof(AtlasBundleKeys), nameof(MaterializePostfix)));
            harmony.Patch(stamp, prefix: new HarmonyMethod(typeof(AtlasBundleKeys), nameof(StampPrefix)) { priority = Priority.High });
            MainMenu.UnpatchAfterStartup(stamp);
            LeanFixes.Reports.Add(Stats);
        }

        public static void MaterializePostfix(object __instance, object[] __args, UnityEngine.Object __result)
        {
            if (!(__result is Texture2D texture) || texture.CoreTexture?.SourcePath != null)
            {
                return;
            }
            var path = filePath.GetValue(__instance) as string;
            var file = refFile.GetValue(__args[0]);
            if (path == null || file == null)
            {
                return;
            }
            var origin = $"bundle:{path}|{FileStamp(path)}|{fileName.GetValue(file)}|{refPathId.GetValue(__args[0])}";
            Origins.AddOrUpdate(texture, origin);
            Interlocked.Increment(ref recorded);
        }

        private static string FileStamp(string path) => FileStamps.GetOrAdd(path, p =>
        {
            var info = new FileInfo(p);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
        });

        public static bool TryGet(Texture2D texture, out string origin)
        {
            origin = null;
            return texture != null && Origins.TryGetValue(texture, out origin);
        }

        public static bool StampPrefix(BinaryWriter writer, Texture2D texture, ref bool __result)
        {
            if (texture?.CoreTexture == null || texture.CoreTexture.SourcePath != null || !Origins.TryGetValue(texture, out var origin) || origin.Contains("|missing|"))
            {
                return true;
            }
            writer.Write(origin);
            stamped++;
            __result = true;
            return false;
        }

        public static string Stats() => $"atlas cache keys for bundle textures: {recorded:N0} bundle textures recorded, {stamped:N0} atlas stamps written from their bundle file";
    }
}
#endif
