#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using Waylith.Audio;
using Waylith.Rendering;

namespace RimWorldLoadingOptimizer
{
    // The game's own sounds (Sounds/... under the engine's Resources folder) are opened by UnityEngine.Resources on the main
    // thread the first time a sub-sound resolves its grains: AudioClip.Load -> AudioClip.Open decodes the file. Once the defs are
    // in, this lists the files those grains will ask for (with the engine's own path lookups) and opens them on workers with the
    // same AudioClip.Open call; when Resources then loads one, the clip is built from the opened sound exactly as AudioClip.Load
    // builds it, on the thread that asked. A file it does not have ready is opened by the game as before.
    public static class ResourceSoundReadAhead
    {
        public const string Feature = "game sound read-ahead";
        private const long CapBytes = 128L << 20;
        private static readonly long IdleTicks = 90 * Stopwatch.Frequency;

        private static Func<string, bool, IPrefetchedAsset> open;
        private static Func<string, bool, AudioClip> load;
        private static Func<string, string[], bool, string> findFile;
        private static Func<string, string[], IEnumerable<string>> enumerate;
        private static ConstructorInfo clipCtor;
        private static MethodInfo originalLoad;
        private static Type openedType;
        private static string[] extensions;
        private static volatile OrderedReadAhead<IPrefetchedAsset> queue;
        private static volatile bool done;
        private static int started, finished, redirected;
        private static long startedAt, listTicks, lastUse, menuAt;
        private static int clips, folders, listed, ownOpens, otherPaths, compared, mismatches;
        private static long ownTicks;
        private static Timer idle;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            if (PlayDataLoader.Loaded)
            {
                return;
            }
            var resources = typeof(UnityEngine.Resources);
            var stub = AccessTools.Method(resources, "LoadStubResource");
            originalLoad = AccessTools.Method(typeof(AudioClip), "Load", new[] { typeof(string), typeof(bool) });
            var clipOpen = AccessTools.Method(typeof(AudioClip), "Open", new[] { typeof(string), typeof(bool) });
            openedType = AccessTools.Inner(typeof(AudioClip), "Opened");
            clipCtor = openedType == null ? null : AccessTools.Constructor(typeof(AudioClip), new[] { openedType, typeof(string) });
            var waylith = typeof(Waylith.Resources);
            var find = AccessTools.Method(waylith, nameof(Waylith.Resources.FindFile), new[] { typeof(string), typeof(string[]), typeof(bool) });
            var paths = AccessTools.Method(waylith, nameof(Waylith.Resources.EnumerateResourcePaths), new[] { typeof(string), typeof(string[]) });
            var preResolve = AccessTools.Method(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve));
            extensions = (AccessTools.Field(resources, "AudioClipExtensions")?.GetValue(null) as string[])?.ToArray();
            if (clipCtor == null || extensions == null || clipOpen?.ReturnType != openedType || !typeof(IPrefetchedAsset).IsAssignableFrom(openedType))
            {
                Log.Message($"[RLO] {Feature}: the game's sound loading no longer has the members it needs. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var openForPrefetch = AccessTools.Method(typeof(AudioClip), "OpenForPrefetch", new[] { typeof(string) });
            if (stub == null || find == null || paths == null || preResolve == null)
            {
                VersionCheck.Skip(Feature, "a method it needs no longer exists");
                return;
            }
            if (!VersionCheck.Pattern(Feature, originalLoad, code => VersionCheck.CallsExactly(code, clipOpen, clipCtor), "builds the clip from AudioClip.Open and nothing else")
                || !VersionCheck.Pattern(Feature, openForPrefetch, code => VersionCheck.Calls(code, clipOpen) == 1, "opens sounds for the engine's own prefetch with AudioClip.Open"))
            {
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(clipOpen, originalLoad, clipCtor), 16);
            if (check.IsOff)
            {
                return;
            }
            open = (Func<string, bool, IPrefetchedAsset>)clipOpen.CreateDelegate(typeof(Func<string, bool, IPrefetchedAsset>));
            load = (Func<string, bool, AudioClip>)originalLoad.CreateDelegate(typeof(Func<string, bool, AudioClip>));
            findFile = (Func<string, string[], bool, string>)find.CreateDelegate(typeof(Func<string, string[], bool, string>));
            enumerate = (Func<string, string[], IEnumerable<string>>)paths.CreateDelegate(typeof(Func<string, string[], IEnumerable<string>>));
            harmony.Patch(stub, transpiler: new HarmonyMethod(typeof(ResourceSoundReadAhead), nameof(Transpiler)));
            if (redirected != 1)
            {
                harmony.Unpatch(stub, HarmonyPatchType.Transpiler, harmony.Id);
                Log.Message($"[RLO] {Feature}: the sound load call was not found as expected ({redirected} of 1). This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            harmony.Patch(preResolve, prefix: new HarmonyMethod(typeof(ResourceSoundReadAhead), nameof(Begin)));
            MainMenu.UnpatchAfterStartup(preResolve);
            MainMenu.Then(AtMainMenu);
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            redirected = list.Count(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m && m == originalLoad);
            if (redirected != 1)
            {
                return list;
            }
            foreach (var instruction in list)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m && m == originalLoad)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ResourceSoundReadAhead), nameof(Load));
                }
            }
            return list;
        }

        // Before the implied defs are made: every sound def and its grains are loaded, nothing has asked for a clip yet.
        public static void Begin()
        {
            if (Interlocked.Exchange(ref started, 1) != 0 || done || check.IsOff)
            {
                return;
            }
            if (AudioBackend.Current?.IsInitialized != true)
            {
                Log.Message($"[RLO] {Feature}: the audio system is not running; skipped");
                return;
            }
            var requests = new List<(string path, bool folder)>();
            var root = GenFilePaths.ContentPath<AudioClip>();
            foreach (var def in DefDatabase<SoundDef>.AllDefsListForReading)
            {
                if (def?.subSounds == null)
                {
                    continue;
                }
                foreach (var sub in def.subSounds)
                {
                    if (sub?.grains == null)
                    {
                        continue;
                    }
                    foreach (var grain in sub.grains)
                    {
                        if (grain == null)
                        {
                            continue;
                        }
                        if (grain.GetType() == typeof(AudioGrain_Clip) && ((AudioGrain_Clip)grain).clipPath != null)
                        {
                            requests.Add((root + ((AudioGrain_Clip)grain).clipPath, false));
                            clips++;
                        }
                        else if (grain.GetType() == typeof(AudioGrain_Folder) && ((AudioGrain_Folder)grain).clipFolderPath != null)
                        {
                            requests.Add((root + ((AudioGrain_Folder)grain).clipFolderPath, true));
                            folders++;
                        }
                    }
                }
            }
            if (RLOMod.Verify || check.IsChecking)
            {
                AudioReadAhead.PrepareCompare(openedType);
            }
            startedAt = Stopwatch.GetTimestamp();
            new Thread(() => List(requests)) { IsBackground = true, Name = "RLO game sound list", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        // The file each request ends up opening, in request order: the same lookups UnityEngine.Resources makes for Load and LoadAll.
        private static void List(List<(string path, bool folder)> requests)
        {
            var files = new List<string>();
            try
            {
                foreach (var (path, folder) in requests)
                {
                    if (done)
                    {
                        return;
                    }
                    if (!folder)
                    {
                        files.Add(findFile(path, extensions, false));
                        continue;
                    }
                    foreach (var resourcePath in enumerate(path, extensions))
                    {
                        files.Add(findFile(resourcePath, extensions, false));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not list the game's sound files ({e.Message}); skipped");
                return;
            }
            listTicks = Stopwatch.GetTimestamp() - startedAt;
            var made = new OrderedReadAhead<IPrefetchedAsset>(files, path => open(path, true), asset => Math.Max(0, asset.Bytes), Release, CapBytes);
            listed = made.Count;
            if (made.Count == 0 || done)
            {
                return;
            }
            queue = made;
            made.Start(Math.Max(1, Math.Min(2, Environment.ProcessorCount / 4)), "RLO game sound read-ahead");
            if (done)
            {
                made.Stop();
            }
        }

        public static AudioClip Load(string path, bool importedByUnity)
        {
            var q = queue;
            if (q == null || done || !importedByUnity || check.IsOff)
            {
                return load(path, importedByUnity);
            }
            if (!q.Contains(path))
            {
                Interlocked.Increment(ref otherPaths);
                return load(path, importedByUnity);
            }
            Volatile.Write(ref lastUse, Stopwatch.GetTimestamp());
            var main = UnityData.IsInMainThread;
            if (!q.TryTake(path, main, out var opened))
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    return load(path, importedByUnity);
                }
                finally
                {
                    Interlocked.Increment(ref ownOpens);
                    Interlocked.Add(ref ownTicks, Stopwatch.GetTimestamp() - start);
                }
            }
            if (RLOMod.Verify || check.IsChecking)
            {
                var difference = Compare(path, opened);
                check.Report(difference == null, path);
                if (check.IsOff)
                {
                    Release(opened);
                    Finish();
                    return load(path, importedByUnity);
                }
            }
            var clip = (AudioClip)clipCtor.Invoke(new object[] { opened, path });
            if (q.Remaining == 0)
            {
                Finish();
            }
            return clip;
        }

        private static string Compare(string path, IPrefetchedAsset ahead)
        {
            IPrefetchedAsset fresh = null;
            string difference;
            try
            {
                fresh = open(path, true);
                difference = AudioReadAhead.Difference(ahead, fresh);
            }
            catch (Exception e)
            {
                difference = "a fresh open threw " + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (fresh != null)
                {
                    Release(fresh);
                }
            }
            Interlocked.Increment(ref compared);
            if (difference != null && Interlocked.Increment(ref mismatches) <= 5)
            {
                Log.Warning($"[RLO] {Feature} verify mismatch: {path}: {difference}");
            }
            return difference;
        }

        private static void Release(IPrefetchedAsset asset)
        {
            try
            {
                asset.Release();
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] {Feature}: could not release an unused sound: {e.Message}");
            }
        }

        // Without a mod that resolves the sounds later every grain is resolved by now; with one (Faster Game Loading) they come
        // after the menu is up, so what is left is released once no listed sound was asked for in a while.
        private static void AtMainMenu()
        {
            menuAt = Stopwatch.GetTimestamp();
            if (queue == null)
            {
                Finish();
                return;
            }
            idle = new Timer(_ =>
            {
                var last = Math.Max(Volatile.Read(ref lastUse), menuAt);
                if (Stopwatch.GetTimestamp() - last > IdleTicks)
                {
                    Finish();
                }
            }, null, 5000, 5000);
        }

        private static void Finish()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0)
            {
                return;
            }
            done = true;
            idle?.Dispose();
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
                return started == 0 ? $"{Feature}: off" : $"{Feature}: {clips} clip and {folders} folder grains, {listed} files listed, nothing opened ahead";
            }
            double S(long ticks) => ticks / (double)Stopwatch.Frequency;
            var text = $"{Feature}: {clips} clip and {folders} folder grains listed {q.Count} files in {S(listTicks):F1} s; {q.Summary("game sounds")}; "
                + $"opened {ownOpens} listed sounds itself in {S(Volatile.Read(ref ownTicks)):F1} s, {otherPaths} sounds outside the list; workers done at {(q.DoneAt == 0 ? "not yet" : $"{S(q.DoneAt - startedAt):F1} s")}";
            if (RLOMod.Verify)
            {
                text += $"; verify: {compared} compared, {mismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
