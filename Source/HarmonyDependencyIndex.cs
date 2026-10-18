#if RW17
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    // For every assembly Harmony 3 generates, GeneratedAssemblyLoader's constructor compares each of the module's
    // references with every loaded assembly (about 7,000 with thousands of generated ones). The constructor's
    // AppDomain.GetAssemblies() call is redirected here: it returns only the loaded assemblies whose full name is one of
    // the module's references, from an index kept up to date on AssemblyLoad. Harmony's own name and hash checks still
    // run over that list, so it binds exactly the same assemblies. A reference that is not in the index (not loaded, or
    // loaded while the index was being read) gets the full list, as before.
    public static class HarmonyDependencyIndex
    {
        private const string Feature = "Harmony dependency name index";
        private static readonly object gate = new object();
        private static readonly Dictionary<string, List<Assembly>> strong = new Dictionary<string, List<Assembly>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<System.WeakReference<Assembly>>> weak = new Dictionary<string, List<System.WeakReference<Assembly>>>(StringComparer.Ordinal);
        private static readonly MethodInfo getAssemblies = AccessTools.Method(typeof(AppDomain), nameof(AppDomain.GetAssemblies));
        private static Func<object, IEnumerable> references;
        private static Func<object, string> runtimeFullName;
        private static int redirected;
        private static SelfCheck check;
        private static ConstructorInfo loaderCtor;
        private static FieldInfo[] loaderFields;
        [ThreadStatic] private static bool bypass;
        private static long served, fallbacks, ticks, verifyMismatches;

        public static void Install(Harmony harmony)
        {
            var harmonyAssembly = typeof(Harmony).Assembly;
            var loader = harmonyAssembly.GetType("HarmonyLib.GeneratedAssemblyLoader");
            var ctors = loader?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? new ConstructorInfo[0];
            var ctor = ctors.Length == 1 ? ctors[0] : null;
            var moduleType = harmonyAssembly.GetType("Mono.Cecil.ModuleDefinition");
            var referenceType = harmonyAssembly.GetType("Mono.Cecil.AssemblyNameReference");
            var fullName = loader == null || referenceType == null ? null : AccessTools.Method(loader, "RuntimeFullName", new[] { referenceType });
            var referencesGetter = moduleType == null ? null : AccessTools.PropertyGetter(moduleType, "AssemblyReferences");
            if (ctor == null || fullName == null || referencesGetter == null || ctor.GetParameters().Length != 2 || ctor.GetParameters()[0].ParameterType != moduleType)
            {
                Log.Message($"[RLO] {Feature}: Harmony's generated assembly loader does not have the expected shape. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var closures = PatchProcessor.ReadMethodBody(ctor)
                .Where(p => p.Key == OpCodes.Ldftn)
                .Select(p => p.Value as MethodBase)
                .Where(m => m != null && m.DeclaringType?.DeclaringType == loader)
                .Distinct();
            check = new SelfCheck(Feature, VersionCheck.Code(new MethodBase[] { ctor, fullName }.Concat(closures).ToArray()));
            if (check.IsOff)
            {
                return;
            }
            loaderCtor = ctor;
            loaderFields = AccessTools.GetDeclaredFields(loader).Where(f => !f.IsStatic).ToArray();
            var module = Expression.Parameter(typeof(object));
            references = Expression.Lambda<Func<object, IEnumerable>>(Expression.Convert(Expression.Call(Expression.Convert(module, moduleType), referencesGetter), typeof(IEnumerable)), module).Compile();
            var reference = Expression.Parameter(typeof(object));
            runtimeFullName = Expression.Lambda<Func<object, string>>(Expression.Call(fullName, Expression.Convert(reference, referenceType)), reference).Compile();
            lock (gate)
            {
                AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Add(args.LoadedAssembly);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Add(assembly);
                }
            }
            harmony.Patch(ctor, transpiler: new HarmonyMethod(typeof(HarmonyDependencyIndex), nameof(Transpiler)));
            if (redirected != 1)
            {
                harmony.Unpatch(ctor, HarmonyPatchType.Transpiler, harmony.Id);
                Log.Message($"[RLO] {Feature}: the assembly list in Harmony's generated assembly loader was not found exactly once. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            if (check.IsChecking)
            {
                harmony.Patch(ctor, finalizer: new HarmonyMethod(typeof(HarmonyDependencyIndex), nameof(Compare)));
            }
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var at = Enumerable.Range(0, list.Count).Where(i => (list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && list[i].operand is MethodInfo m && m == getAssemblies).ToList();
            if (at.Count != 1)
            {
                return list;
            }
            var call = list[at[0]];
            list[at[0]] = new CodeInstruction(OpCodes.Ldarg_1).WithLabels(call.labels).WithBlocks(call.blocks);
            list.Insert(at[0] + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HarmonyDependencyIndex), nameof(Candidates))));
            redirected = 1;
            return list;
        }

        public static Exception Compare(object __instance, object[] __args, Exception __exception)
        {
            if (bypass || !check.IsChecking || __instance == null)
            {
                return __exception;
            }
            object original = null;
            Exception originalException = null;
            bypass = true;
            try
            {
                original = loaderCtor.Invoke(__args);
            }
            catch (TargetInvocationException e)
            {
                originalException = e.InnerException ?? e;
            }
            catch (Exception)
            {
                return __exception;
            }
            finally
            {
                bypass = false;
            }
            bool same;
            if (__exception != null || originalException != null)
            {
                same = __exception != null && originalException != null && __exception.GetType() == originalException.GetType() && __exception.Message == originalException.Message;
            }
            else
            {
                same = loaderFields.All(f => Same(f.GetValue(__instance), f.GetValue(original)));
            }
            check.Report(same, "assemblies bound for a generated Harmony method");
            if (same)
            {
                return __exception;
            }
            if (originalException != null)
            {
                return originalException;
            }
            foreach (var field in loaderFields)
            {
                field.SetValue(__instance, field.GetValue(original));
            }
            return null;
        }

        private static bool Same(object a, object b)
        {
            if (a is IDictionary x && b is IDictionary y)
            {
                if (x.Count != y.Count)
                {
                    return false;
                }
                foreach (DictionaryEntry entry in x)
                {
                    if (!y.Contains(entry.Key) || !Same(entry.Value, y[entry.Key]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return a is string || a is ValueType ? Equals(a, b) : ReferenceEquals(a, b);
        }

        private static void Add(Assembly assembly)
        {
            if (assembly == null)
            {
                return;
            }
            string name;
            try
            {
                name = HarmonyAssemblyNames.FullNameOf(assembly);
            }
            catch (Exception)
            {
                return;
            }
            lock (gate)
            {
                if (assembly.IsCollectible)
                {
                    if (!weak.TryGetValue(name, out var refs))
                    {
                        weak[name] = refs = new List<System.WeakReference<Assembly>>();
                    }
                    refs.RemoveAll(r => !r.TryGetTarget(out _));
                    refs.Add(new System.WeakReference<Assembly>(assembly));
                }
                else
                {
                    if (!strong.TryGetValue(name, out var list))
                    {
                        strong[name] = list = new List<Assembly>(1);
                    }
                    if (!list.Contains(assembly))
                    {
                        list.Add(assembly);
                    }
                }
            }
        }

        public static Assembly[] Candidates(AppDomain domain, object module)
        {
            if (bypass || check.IsOff)
            {
                return domain.GetAssemblies();
            }
            var start = Stopwatch.GetTimestamp();
            try
            {
                var result = new List<Assembly>();
                var names = RLOMod.Verify ? new HashSet<string>(StringComparer.Ordinal) : null;
                lock (gate)
                {
                    foreach (var reference in references(module))
                    {
                        var name = runtimeFullName(reference);
                        names?.Add(name);
                        var found = false;
                        if (strong.TryGetValue(name, out var list))
                        {
                            found = true;
                            foreach (var assembly in list)
                            {
                                if (!result.Contains(assembly))
                                {
                                    result.Add(assembly);
                                }
                            }
                        }
                        if (weak.TryGetValue(name, out var refs))
                        {
                            foreach (var r in refs)
                            {
                                if (r.TryGetTarget(out var assembly))
                                {
                                    found = true;
                                    if (!result.Contains(assembly))
                                    {
                                        result.Add(assembly);
                                    }
                                }
                            }
                        }
                        if (!found)
                        {
                            Interlocked.Increment(ref fallbacks);
                            return domain.GetAssemblies();
                        }
                    }
                }
                if (names != null)
                {
                    var expected = domain.GetAssemblies().Where(a => names.Contains(HarmonyAssemblyNames.FullNameOf(a))).ToHashSet();
                    if (!expected.SetEquals(result) && Interlocked.Increment(ref verifyMismatches) <= 10)
                    {
                        Log.Warning($"[RLO] {Feature} verify mismatch: index {string.Join(", ", result.Select(a => a.FullName))}, full list {string.Join(", ", expected.Select(a => a.FullName))}");
                        return domain.GetAssemblies();
                    }
                }
                Interlocked.Increment(ref served);
                return result.ToArray();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref fallbacks);
                return domain.GetAssemblies();
            }
            finally
            {
                Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - start);
            }
        }

        public static string Stats()
        {
            int names;
            lock (gate)
            {
                names = strong.Count + weak.Count;
            }
            return $"Harmony dependency name index: {Interlocked.Read(ref served)} generated assemblies bound from the index in {Interlocked.Read(ref ticks) / (double)Stopwatch.Frequency:F2} s, {Interlocked.Read(ref fallbacks)} got the full assembly list, {names} names indexed"
                + (RLOMod.Verify ? $", {Interlocked.Read(ref verifyMismatches)} verify mismatches" : "");
        }
    }
}
#endif
