using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class EarlyContent
    {
        private const double FrameBudgetSeconds = 0.15;

        private static Action[] originals;
        private static int[] state;
        private static int next;
        private static volatile bool ready;
        private static int ranEarly, ranLate;
        private static long earlyTicks;

        public static void Install(Harmony harmony)
        {
            if (PlayDataLoader.Loaded)
            {
                return;
            }
            var loadModXml = AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML));
            var update = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsUpdate));
#if RW17
            if (loadModXml == null || update == null)
            {
                VersionCheck.Skip("Early content loading", "a method it needs no longer exists");
                return;
            }
            var reloadInt = AccessTools.Method(typeof(ModContentPack), "ReloadContentInt");
            if (!VersionCheck.Pattern("Early content loading", reloadInt, code => VersionCheck.Called(code).All(OwnContentOnly), "only reloads the mod's own sounds, textures, strings and asset bundles"))
            {
                return;
            }
#endif
            var list = (List<Action>)AccessTools.Field(typeof(LongEventHandler), "toExecuteWhenFinished").GetValue(null);
            var reloads = new List<int>();
            for (var i = 0; i < list.Count; i++)
            {
                if (IsReload(list[i]))
                {
                    reloads.Add(i);
                }
            }
            if (reloads.Count == 0)
            {
                Log.Message("[RLO] early content loading: no queued content reloads found; skipped");
                return;
            }
            var first = reloads[0];
            var last = reloads[reloads.Count - 1];
            if (last - first + 1 != reloads.Count || first < 1 || !IsLoadModContentStep(list[first - 1]) || last + 1 >= list.Count || !IsLoadModContentStep(list[last + 1]))
            {
                Log.Message("[RLO] early content loading: the queued content reloads are not in the expected order; skipped");
                return;
            }
            for (var i = 0; i < first - 1; i++)
            {
                if (!IsLoadModContentStep(list[i]))
                {
                    Log.Message($"[RLO] early content loading: {first - 1} other queued action(s) must run before the content reloads; skipped");
                    return;
                }
            }
#if RW17
            if (!VersionCheck.Pattern("Early content loading", list[first].Method, code => VersionCheck.Called(code).All(m => m == reloadInt), "only runs ModContentPack.ReloadContentInt"))
            {
                return;
            }
#endif
            originals = new Action[reloads.Count];
            state = new int[reloads.Count];
            for (var k = 0; k < reloads.Count; k++)
            {
                originals[k] = list[first + k];
                var index = k;
                list[first + k] = () => RunOne(index, false);
            }
            harmony.Patch(loadModXml, prefix: new HarmonyMethod(typeof(EarlyContent), nameof(Ready)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(EarlyContent), nameof(UpdatePostfix)));
            MainMenu.UnpatchAfterStartup(update);
            LeanFixes.Reports.Add(Stats);
        }

        private static bool IsReload(Action action) =>
            action.Method.DeclaringType?.DeclaringType == typeof(ModContentPack) && action.Method.Name.Contains("<ReloadContent>");

#if RW17
        private static bool OwnContentOnly(MethodBase method) =>
            method?.DeclaringType == typeof(DeepProfiler)
            || method?.Name == nameof(ModContentHolder<string>.ReloadAll) && (method.DeclaringType == typeof(ModAssetBundlesHandler)
                || method.DeclaringType.IsGenericType && method.DeclaringType.GetGenericTypeDefinition() == typeof(ModContentHolder<>));
#endif

        private static bool IsLoadModContentStep(Action action)
        {
            var type = action.Method.DeclaringType;
            return (type == typeof(LoadedModManager) || type?.DeclaringType == typeof(LoadedModManager)) && action.Method.Name.Contains("<LoadModContent>");
        }

        public static void Ready()
        {
            if (originals != null)
            {
                ready = true;
            }
        }

        public static void UpdatePostfix()
        {
            if (!ready || originals == null || next >= originals.Length || !UnityData.IsInMainThread || PlayDataLoader.Loaded)
            {
                return;
            }
            var start = Stopwatch.GetTimestamp();
            var budget = (long)(FrameBudgetSeconds * Stopwatch.Frequency);
            while (next < originals.Length && Stopwatch.GetTimestamp() - start < budget)
            {
                RunOne(next++, true);
            }
            earlyTicks += Stopwatch.GetTimestamp() - start;
        }

        private static void RunOne(int index, bool early)
        {
            if (Interlocked.Exchange(ref state[index], 1) != 0)
            {
                return;
            }
            if (early)
            {
                ranEarly++;
            }
            else
            {
                ranLate++;
            }
            try
            {
                originals[index]();
            }
            catch (Exception e)
            {
                Log.Error("[RLO] early content loading: " + e);
            }
        }

        public static int Total => originals?.Length ?? 0;

        public static int Done => ranEarly;

        public static int Loaded => ranEarly + ranLate;

        private static string Stats()
        {
            if (originals == null)
            {
                return "early content loading: off";
            }
            int textures = 0, sounds = 0, strings = 0;
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                textures += mod.GetContentHolder<Texture2D>().contentList.Count;
                sounds += mod.GetContentHolder<AudioClip>().contentList.Count;
                strings += mod.GetContentHolder<string>().contentList.Count;
            }
            return $"early content loading: {ranEarly} of {originals.Length} mods loaded while the XML was loading ({earlyTicks * 1.0 / Stopwatch.Frequency:F1} s on the main thread), {ranLate} after; content loaded: {textures} textures, {sounds} sounds, {strings} string files";
        }
    }
}
