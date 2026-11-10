using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AppearanceFolders
    {
        public const string Feature = "Stuff appearance folder lookups";

        private sealed class Replay : IEnumerable<Texture2D>
        {
            private readonly IEnumerator<Texture2D> source;
            private readonly List<Texture2D> seen = new List<Texture2D>();
            private bool done;

            public Replay(IEnumerable<Texture2D> source)
            {
                this.source = source.GetEnumerator();
            }

            public IEnumerator<Texture2D> GetEnumerator()
            {
                for (var i = 0; ; i++)
                {
                    if (i < seen.Count)
                    {
                        if (i > 0)
                        {
                            replayed++;
                        }
                        yield return seen[i];
                        continue;
                    }
                    if (done || !source.MoveNext())
                    {
                        done = true;
                        yield break;
                    }
                    seen.Add(source.Current);
                    yield return source.Current;
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static readonly MethodInfo Original = AccessTools.Method(
            typeof(ContentFinder<Texture2D>),
            nameof(ContentFinder<Texture2D>.GetAllInFolder)
        );
        private static readonly MethodInfo Fast = AccessTools.Method(
            typeof(FastContent),
            nameof(FastContent.GetAllInFolder)
        );
        private static readonly Dictionary<string, Replay> Folders = new Dictionary<string, Replay>();
        private static int lookups,
            reused,
            replayed,
            graphics;

        public static void Install(Harmony harmony)
        {
            var init = AccessTools.DeclaredMethod(
                typeof(Graphic_Appearances),
                nameof(Graphic_Appearances.Init),
                new[] { typeof(GraphicRequest) }
            );
            if (
                init == null
                || Original == null
                || Fast == null
                || PatchProcessor.GetOriginalInstructions(init).Count(i => i.Calls(Original)) != 1
            )
            {
                VersionCheck.Skip(
                    Feature,
                    "Graphic_Appearances.Init no longer makes the one folder lookup this was written for"
                );
                return;
            }
            harmony.Patch(
                init,
                prefix: new HarmonyMethod(typeof(AppearanceFolders), nameof(Clear)),
                finalizer: new HarmonyMethod(typeof(AppearanceFolders), nameof(ClearAfter)),
                transpiler: new HarmonyMethod(typeof(AppearanceFolders), nameof(Transpiler))
            );
            StartupPhaseFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(AppearanceFolders), nameof(GetAllInFolder));
            return IlScan.Replace(IlScan.Replace(instructions, Original, target), Fast, target);
        }

        public static void Clear()
        {
            Folders.Clear();
            graphics++;
        }

        public static System.Exception ClearAfter(System.Exception __exception)
        {
            Folders.Clear();
            return __exception;
        }

        public static IEnumerable<Texture2D> GetAllInFolder(string folderPath)
        {
            lookups++;
            if (!UnityData.IsInMainThread)
            {
                return FastContent.GetAllInFolder(folderPath);
            }
            if (Folders.TryGetValue(folderPath, out var replay))
            {
                reused++;
                return replay;
            }
            replay = new Replay(FastContent.GetAllInFolder(folderPath));
            Folders[folderPath] = replay;
            return replay;
        }

        public static string Stats() =>
            $"stuff appearance folder lookups: {graphics} graphics, {lookups} folder lookups, {reused} answered from the same graphic's earlier lookup ({replayed} items replayed)";
    }
}
