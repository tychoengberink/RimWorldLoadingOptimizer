using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class HookAttribute : Attribute
    {
    }

    [Hook]
    public static class OptimizerOverlap
    {
        private static readonly string[] Optimizers = { "Faster Game Loading", "Performance Fish", "Performance Optimizer", "Performance Esmolas", "Yet another Optimizer", "WOWGAG's Performance Patch", "Hyperdrive" };
        private static readonly List<string> steppedAside = new List<string>();
        private static Harmony harmony;
        private static HashSet<string> optimizerAssemblies;
        private static readonly HashSet<MethodBase> ours = new HashSet<MethodBase>();
        private static readonly Stopwatch checkTime = new Stopwatch();
        private static int checks;

        public static void Install(Harmony h)
        {
            harmony = h;
            foreach (var method in Harmony.GetAllPatchedMethods().ToList())
            {
                if (Harmony.GetPatchInfo(method)?.Owners.Contains(h.Id) == true)
                {
                    ours.Add(method);
                }
            }
            h.Patch(AccessTools.Constructor(typeof(PatchProcessor), new[] { typeof(Harmony), typeof(MethodBase) }), postfix: new HarmonyMethod(typeof(OptimizerOverlap), nameof(Track)));
            h.Patch(AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.Start)), prefix: new HarmonyMethod(typeof(OptimizerOverlap), nameof(BeforePhase)));
            h.Patch(AccessTools.Method(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll)), prefix: new HarmonyMethod(typeof(OptimizerOverlap), nameof(Check)) { priority = Priority.First });
            MainMenu.Then(Check);
        }

        public static void Track(Harmony instance, MethodBase original)
        {
            if (original != null && instance?.Id == harmony.Id)
            {
                lock (ours)
                {
                    ours.Add(original);
                }
            }
        }

        public static void BeforePhase(string label)
        {
            if (label == "LoadModXML()")
            {
                harmony.Unpatch(AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.Start)), AccessTools.Method(typeof(OptimizerOverlap), nameof(BeforePhase)));
                Check();
            }
        }

        public static void Check()
        {
            checks++;
            checkTime.Start();
            try
            {
                optimizerAssemblies = optimizerAssemblies ?? new HashSet<string>(LoadedModManager.RunningModsListForReading
                    .Where(m => m.Name != null && Optimizers.Any(o => m.Name.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0))
                    .SelectMany(m => m.assemblies.loadedAssemblies.Select(a => a.GetName().Name)));
                if (optimizerAssemblies.Count == 0)
                {
                    return;
                }
                var byFeature = new Dictionary<Type, List<(MethodBase original, MethodInfo patch)>>();
                var clashes = new Dictionary<Type, MethodBase>();
                List<MethodBase> candidates;
                lock (ours)
                {
                    candidates = ours.ToList();
                }
                foreach (var original in candidates)
                {
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null || !info.Owners.Contains(harmony.Id))
                    {
                        continue;
                    }
                    var patches = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).ToList();
                    var replaced = info.Prefixes.Where(p => p.PatchMethod.ReturnType == typeof(bool)).Concat(info.Transpilers).Any(p => p.owner != harmony.Id && IsOptimizer(p.PatchMethod));
                    foreach (var patch in patches.Where(p => p.owner == harmony.Id && !IsHook(p.PatchMethod)))
                    {
                        var feature = Outer(patch.PatchMethod.DeclaringType);
                        if (!byFeature.TryGetValue(feature, out var list))
                        {
                            byFeature[feature] = list = new List<(MethodBase, MethodInfo)>();
                        }
                        list.Add((original, patch.PatchMethod));
                        if (replaced && !clashes.ContainsKey(feature))
                        {
                            clashes[feature] = original;
                        }
                    }
                }
                foreach (var clash in clashes)
                {
                    StepAside(clash.Key, byFeature[clash.Key], clash.Value);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] optimizer overlap check failed: {e}");
            }
            finally
            {
                checkTime.Stop();
            }
        }

        private static void StepAside(Type feature, List<(MethodBase original, MethodInfo patch)> patches, MethodBase original)
        {
            foreach (var (method, patch) in patches)
            {
                harmony.Unpatch(method, patch);
            }
            feature.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
            var name = Regex.Replace(feature.Name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            var where = $"{original.DeclaringType?.FullName}.{original.Name}";
            lock (steppedAside)
            {
                steppedAside.Add($"{name} (on {where})");
            }
            Log.Message($"[RLO] {name}: another optimizer also replaces or rewrites {where}. This speedup steps aside and leaves that code to it; nothing is broken.");
        }

        private static bool IsOptimizer(MethodInfo patch)
        {
            var assembly = patch?.DeclaringType?.Assembly.GetName().Name;
            return assembly != null && optimizerAssemblies.Contains(assembly);
        }

        private static bool IsHook(MethodInfo patch) => patch.IsDefined(typeof(HookAttribute), false) || Outer(patch.DeclaringType).IsDefined(typeof(HookAttribute), false);

        private static Type Outer(Type type)
        {
            while (type.DeclaringType != null)
            {
                type = type.DeclaringType;
            }
            return type;
        }

        public static string Stats()
        {
            lock (steppedAside)
            {
                var time = $"{checks} checks of {ours.Count} methods in {checkTime.Elapsed.TotalSeconds:F2} s";
                return steppedAside.Count == 0 ? $"optimizer overlap: none ({time})" : $"stepped aside for other optimizers ({time}): {string.Join(", ", steppedAside)}";
            }
        }
    }
}
