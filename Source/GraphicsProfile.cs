#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GraphicsProfile
    {
        private sealed class Cost
        {
            public long Inclusive, Exclusive;
            public int Calls;
        }

        private static readonly Dictionary<string, Cost> Costs = new Dictionary<string, Cost>();
        [ThreadStatic] private static Stack<long> children;
        private static volatile bool resolving;
        private static int depth;
        private static long lambdaTicks;
        private static int lambdas;

        public static bool Wanted => Environment.GetEnvironmentVariable("RLO_GRAPHICS_PROFILE") == "1";

        public static void Install(Harmony harmony)
        {
            var lambda = AccessTools.GetDeclaredMethods(typeof(ThingDef)).FirstOrDefault(m => m.Name.StartsWith("<PostLoad>b__", StringComparison.Ordinal) && m.GetParameters().Length == 0 && m.ReturnType == typeof(void));
            if (lambda == null)
            {
                Log.Warning("[RLO] graphics profile: ThingDef graphics lambda not found");
                return;
            }
            var roots = new List<MethodBase> { lambda, AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases)) };
            roots.AddRange(AccessTools.GetDeclaredMethods(typeof(Verse.Sound.SubSoundDef)).Where(m => m.Name.StartsWith("<ResolveReferences>b__", StringComparison.Ordinal)));
            roots.AddRange(AccessTools.GetDeclaredMethods(typeof(SongDef)).Where(m => m.Name.StartsWith("<ResolveReferences>b__", StringComparison.Ordinal)));
            foreach (var root in roots.Where(r => r != null))
            {
                harmony.Patch(root, prefix: new HarmonyMethod(typeof(GraphicsProfile), nameof(LambdaPrefix)), postfix: new HarmonyMethod(typeof(GraphicsProfile), nameof(LambdaPostfix)));
            }
            var targets = new List<MethodBase>
            {
                AccessTools.Method(typeof(GraphicData), "Init"),
                AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get)),
                AccessTools.Method(typeof(MaterialPool), nameof(MaterialPool.MatFrom), new[] { typeof(MaterialRequest) }),
                AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.TryInsertStatic)),
                AccessTools.Method(typeof(Resources), nameof(Resources.LoadAll), new[] { typeof(string), typeof(Type) }),
            };
            targets.AddRange(AccessTools.GetDeclaredMethods(typeof(StaticTextureAtlas)).Where(m => !m.IsGenericMethodDefinition && !m.Name.StartsWith("<", StringComparison.Ordinal)));
            targets.AddRange(AccessTools.GetDeclaredMethods(typeof(GlobalTextureAtlasManager)).Where(m => !m.IsGenericMethodDefinition && m.Name != nameof(GlobalTextureAtlasManager.BakeStaticAtlases) && !m.Name.StartsWith("<", StringComparison.Ordinal)));
            targets.Add(AccessTools.Method(typeof(Waylith.Rendering.Texture2D), "EndDeferredLoading"));
            targets.Add(AccessTools.Method(typeof(ContentFinder<AudioClip>), nameof(ContentFinder<AudioClip>.Get)));
            targets.Add(AccessTools.Method(typeof(ContentFinder<AudioClip>), nameof(ContentFinder<AudioClip>.GetAllInFolder)));
            targets.Add(AccessTools.Method(typeof(UnityStubs.ImportDefaults), nameof(UnityStubs.ImportDefaults.KeepStereo)));
            foreach (var type in typeof(Verse.Sound.AudioGrain).AllSubclasses())
            {
                targets.Add(AccessTools.DeclaredMethod(type, nameof(Verse.Sound.AudioGrain.GetResolvedGrains)));
            }
            foreach (var type in typeof(Def).AllSubclasses().Concat(new[] { typeof(Def) }))
            {
                targets.Add(AccessTools.DeclaredMethod(type, nameof(Def.ResolveReferences), Type.EmptyTypes));
            }
            foreach (var type in typeof(CompProperties).AllSubclasses().Concat(new[] { typeof(CompProperties) }))
            {
                targets.Add(AccessTools.DeclaredMethod(type, nameof(CompProperties.ResolveReferences), new[] { typeof(ThingDef) }));
            }
            targets.Add(AccessTools.Method(typeof(ThingFilter), nameof(ThingFilter.ResolveReferences)));
            targets.AddRange(AccessTools.GetDeclaredMethods(typeof(ThingFilter)).Where(m => m.Name == nameof(ThingFilter.SetAllow) || m.Name == "SetAllowAllWhoCanMake"));
            targets.Add(AccessTools.Method(typeof(ThingDef), nameof(ThingDef.EverStorable)));
            targets.Add(AccessTools.PropertyGetter(typeof(ThingDef), nameof(ThingDef.BaseMarketValue)));
            targets.Add(AccessTools.Method(typeof(ThingDef), nameof(ThingDef.HasComp), new[] { typeof(Type) }));
            targets.Add(AccessTools.Method(typeof(RaceProperties), nameof(RaceProperties.CanEverEat), new[] { typeof(ThingDef) }));
            targets.Add(AccessTools.PropertyGetter(typeof(ThingCategoryDef), nameof(ThingCategoryDef.DescendantThingDefs)));
            harmony.Patch(AccessTools.Method(typeof(PlayDataLoader), "ResetStaticDataPre"), postfix: new HarmonyMethod(typeof(GraphicsProfile), nameof(ResolveStart)));
            harmony.Patch(AccessTools.Method(typeof(RimWorld.DefGenerator), nameof(RimWorld.DefGenerator.GenerateImpliedDefs_PostResolve)), prefix: new HarmonyMethod(typeof(GraphicsProfile), nameof(ResolveEnd)));
            foreach (var type in typeof(Graphic).AllSubclasses().Concat(new[] { typeof(Graphic) }))
            {
                targets.Add(AccessTools.DeclaredMethod(type, "Init", new[] { typeof(GraphicRequest) }));
                targets.Add(AccessTools.DeclaredMethod(type, nameof(Graphic.TryInsertIntoAtlas)));
            }
            foreach (var target in targets.Where(t => t != null && !t.IsAbstract && t.GetMethodBody() != null).Distinct())
            {
                try
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(GraphicsProfile), nameof(Enter)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(GraphicsProfile), nameof(Leave)) { priority = Priority.Last });
                }
                catch (Exception e)
                {
                    Log.Message($"[RLO] graphics profile: {target.DeclaringType?.Name}.{target.Name} not measured ({e.GetType().Name})");
                }
            }
            LeanFixes.Reports.Add(Stats);
        }

        public static void ResolveStart() => resolving = true;

        public static void ResolveEnd() => resolving = false;

        public static void LambdaPrefix(out long __state)
        {
            depth++;
            __state = Stopwatch.GetTimestamp();
        }

        public static void LambdaPostfix(long __state)
        {
            lambdaTicks += Stopwatch.GetTimestamp() - __state;
            lambdas++;
            depth--;
        }

        public static void Enter(out long __state)
        {
            __state = 0;
            if (!resolving && (depth == 0 || !UnityData.IsInMainThread))
            {
                return;
            }
            (children ??= new Stack<long>()).Push(0);
            __state = Stopwatch.GetTimestamp();
        }

        public static Exception Leave(Exception __exception, MethodBase __originalMethod, long __state)
        {
            if (__state == 0)
            {
                return __exception;
            }
            var elapsed = Stopwatch.GetTimestamp() - __state;
            var nested = children.Pop();
            if (children.Count > 0)
            {
                children.Push(children.Pop() + elapsed);
            }
            var key = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
            lock (Costs)
            {
                if (!Costs.TryGetValue(key, out var cost))
                {
                    cost = new Cost();
                    Costs[key] = cost;
                }
                cost.Inclusive += elapsed;
                cost.Exclusive += elapsed - nested;
                cost.Calls++;
            }
            return __exception;
        }

        public static string Stats()
        {
            double S(long t) => t / (double)Stopwatch.Frequency;
            var sb = new StringBuilder($"graphics profile: {lambdas:N0} measured actions (ThingDef graphics, atlas baking, sounds, songs) in {S(lambdaTicks):F2} s; per method inclusive / exclusive / calls:");
            foreach (var pair in Costs.OrderByDescending(p => p.Value.Exclusive).Take(40))
            {
                sb.Append($"\n[RLO]   {S(pair.Value.Inclusive),7:F2} {S(pair.Value.Exclusive),7:F2} {pair.Value.Calls,8:N0}  {pair.Key}");
            }
            return sb.ToString();
        }
    }
}
#endif
