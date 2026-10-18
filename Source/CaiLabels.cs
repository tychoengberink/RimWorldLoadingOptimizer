using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CaiLabels
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<Type, bool> declares = new Dictionary<Type, bool>();
        private static readonly MethodInfo harmonyPatch = AccessTools.Method(typeof(Harmony), nameof(Harmony.Patch), new[] { typeof(MethodBase), typeof(HarmonyMethod), typeof(HarmonyMethod), typeof(HarmonyMethod), typeof(HarmonyMethod) });
        private static bool filterMethods;
        private static readonly MethodInfo getMethods = AccessTools.Method(typeof(Type), nameof(Type.GetMethods), new[] { typeof(BindingFlags) });
        private static string[] names;
        private static bool installed;
        private static int replaced;
        private static int patchCalls;
        private static int deferred;
        private static int types;
        private static int skipped;
        private static int mismatches;
        private static long ticks;
        private static int checkedTypes;

        public static void Release()
        {
            lock (declares)
            {
                checkedTypes += declares.Count;
                declares.Clear();
            }
        }

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("CombatAI.Patches.LabelSuppressor_Patch");
            if (type == null)
            {
                return;
            }
            var patch = AccessTools.Method(type, "Patch");
            var candidateNames = AccessTools.Field(type, "CandidateNames");
            if (candidateNames == null || candidateNames.FieldType != typeof(string[])
                || !VersionCheck.Pattern("CombatAI label scan", patch, code => VersionCheck.Calls(code, harmonyPatch) == 1, "applies its label patches with one Harmony.Patch call"))
            {
                return;
            }
            filterMethods = VersionCheck.Pattern("CombatAI label scan filter", patch, code => VersionCheck.Calls(code, getMethods) == 1 && code.Any(i => i.LoadsField(candidateNames)), "lists methods with one GetMethods call and matches them against CandidateNames");
            names = (string[])candidateNames.GetValue(null);
            harmony.Patch(patch, prefix: new HarmonyMethod(typeof(CaiLabels), nameof(Start)), postfix: new HarmonyMethod(typeof(CaiLabels), nameof(Stop)),
                transpiler: new HarmonyMethod(typeof(CaiLabels), nameof(Transpiler)));
            installed = replaced == 1;
            if (!installed && filterMethods)
            {
                Log.Warning($"[RLO] CombatAI label scan: expected 1 GetMethods call, found {replaced}");
            }
            if (patchCalls != 1)
            {
                Log.Warning($"[RLO] CombatAI label scan: expected 1 Harmony.Patch call, found {patchCalls}; label patches are not moved to the main thread");
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            replaced = 0;
            patchCalls = 0;
            foreach (var instruction in code)
            {
                if (filterMethods && instruction.Calls(getMethods))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(CaiLabels), nameof(Methods));
                    replaced++;
                }
                else if (instruction.Calls(harmonyPatch))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(CaiLabels), nameof(PatchOnMainThread));
                    patchCalls++;
                }
                yield return instruction;
            }
        }

        public static MethodInfo PatchOnMainThread(Harmony harmony, MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod transpiler, HarmonyMethod finalizer)
        {
            if (UnityData.IsInMainThread || original.DeclaringType?.TypeInitializer == null)
            {
                return harmony.Patch(original, prefix, postfix, transpiler, finalizer);
            }
            Interlocked.Increment(ref deferred);
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    harmony.Patch(original, prefix, postfix, transpiler, finalizer);
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] CombatAI label scan: CombatAI could not patch {original.DeclaringType?.FullName}.{original.Name}: {e.Message}");
                }
            });
            return null;
        }

        public static void Start(out long __state) => __state = Stopwatch.GetTimestamp();

        public static void Stop(long __state) => Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - __state);

        public static MethodInfo[] Methods(Type type, BindingFlags flags)
        {
            Interlocked.Increment(ref types);
            bool candidate;
            try
            {
                candidate = MayMatch(type);
            }
            catch (Exception)
            {
                candidate = true;
            }
            if (candidate)
            {
                return type.GetMethods(flags);
            }
            Interlocked.Increment(ref skipped);
            if (RLOMod.Verify)
            {
                CheckSkipped(type, flags);
            }
            return new MethodInfo[0];
        }

        private static void CheckSkipped(Type type, BindingFlags flags)
        {
            MethodInfo[] all;
            try
            {
                all = type.GetMethods(flags);
            }
            catch (Exception)
            {
                return;
            }
            var hit = all.FirstOrDefault(m => names.Contains(m.Name) && Candidate(m));
            if (hit != null && Interlocked.Increment(ref mismatches) <= 10)
            {
                Log.Warning($"[RLO] CombatAI label scan verify mismatch: {type.FullName}.{hit.Name} was skipped");
            }
        }

        private static bool MayMatch(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (Declares(t))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Declares(Type type)
        {
            lock (declares)
            {
                if (declares.TryGetValue(type, out var known))
                {
                    return known;
                }
            }
            var result = FasterCtors.Enabled("cai")
                ? names.Any(name => type.GetMember(name, MemberTypes.Method, Declared).Any(Candidate))
                : names.Any(name => type.GetMember(name, MemberTypes.Method, Declared).Length > 0);
            lock (declares)
            {
                declares[type] = result;
            }
            return result;
        }

        private static int DeclaresCount()
        {
            lock (declares)
            {
                return declares.Count;
            }
        }

        private static bool Candidate(MemberInfo member)
        {
            try
            {
                return !(member is MethodInfo m) || m.GetParameters().Any(p => typeof(Pawn).IsAssignableFrom(p.ParameterType));
            }
            catch (Exception)
            {
                return true;
            }
        }

        public static string Stats() => !installed ? "CombatAI label scan: not active" : types == 0 ? "CombatAI label scan: CombatAI's scan did not run after RimWorld Loading Optimizer was set up (it may be switched off in CombatAI, or CombatAI loads before RimWorld Loading Optimizer)" : $"CombatAI label scan: {LeanFixes.Seconds(ticks):F2} s, types {types}, skipped {skipped}, distinct types checked {checkedTypes + DeclaresCount()}, patches moved to the main thread {deferred}" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
    }
}
