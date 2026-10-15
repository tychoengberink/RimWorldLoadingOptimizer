#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security;
using HarmonyLib;
using Mono.Cecil;
using MonoMod;
using MonoMod.Utils;
using Verse;

namespace RimWorldLoadingOptimizer
{
    // Harmony 3 builds every patched method that has exception handlers as its own Cecil assembly, and on Windows the
    // runtime has the antivirus scan (AMSI) check every assembly loaded from memory first: about 10 ms per assembly,
    // more than 3,000 times per start with many mods. When Harmony rebuild coalescing builds the deferred methods of
    // one PatchAll, this collects the methods that would take that path and builds them the way Harmony's own
    // GeneratedAssemblyLoader.Generate does (same clone, relinking, attributes, type and method names), but up to 64
    // of them in one module that is loaded once. Anything unusual (debug dumps, a binding conflict, an error) builds
    // those methods one by one through Harmony's own code, so errors appear exactly as before.
    public static class HarmonyBatchLoad
    {
        private const string Feature = "Harmony batched assembly loads";
        private const int MaxPerModule = 64;
        [ThreadStatic] private static bool collecting;
        [ThreadStatic] private static DynamicMethodDefinition capturedMethod;
        [ThreadStatic] private static List<Assembly> capturedExtra;
        private static readonly MethodInfo placeholder = AccessTools.Method(typeof(HarmonyBatchLoad), nameof(Placeholder));
        private static long collected, modules, batched, single, failedModules, ticks;

        public static bool Installed { get; private set; }

        public static void Install(Harmony harmony)
        {
            var generate = AccessTools.Method(typeof(GeneratedAssemblyLoader), nameof(GeneratedAssemblyLoader.Generate), new[] { typeof(DynamicMethodDefinition), typeof(IEnumerable<Assembly>) });
            var load = AccessTools.Method(typeof(GeneratedAssemblyLoader), "LoadCollectible");
            var ctor = typeof(GeneratedAssemblyLoader).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault();
            if (generate == null || load == null || ctor == null)
            {
                Log.Message($"[RLO] {Feature}: Harmony's generated assembly loader does not have the expected shape. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            if (!Fingerprint.Matches(Feature,
                    Fingerprint.Known(generate, "1DF21651824663A3"),
                    Fingerprint.Known(load, "7141C1E3A7D26AB8"),
                    Fingerprint.Known(ctor, "0995A15C7BF271F3")))
            {
                return;
            }
            harmony.Patch(generate, prefix: new HarmonyMethod(typeof(HarmonyBatchLoad), nameof(GeneratePrefix)) { priority = Priority.First });
            Installed = true;
            LeanFixes.Reports.Add(Stats);
        }

        private static void Placeholder()
        {
        }

        public static bool GeneratePrefix(DynamicMethodDefinition method, IEnumerable<Assembly> additionalAssemblies, ref MethodInfo __result)
        {
            if (!collecting)
            {
                return true;
            }
            capturedMethod = method;
            capturedExtra = additionalAssemblies?.ToList() ?? new List<Assembly>();
            __result = placeholder;
            return false;
        }

        // Called by Coalesce right before it builds the last step of every chain in a flush, in that order.
        internal static void Prebuild(List<Coalesce.Step> steps, Func<object, MethodInfo> generate)
        {
            var start = Stopwatch.GetTimestamp();
            var pending = new List<(Coalesce.Step step, DynamicMethodDefinition method, List<Assembly> extra)>();
            foreach (var step in steps)
            {
                if (step.Built != null || step.PrebuildError != null)
                {
                    continue;
                }
                MethodInfo result;
                collecting = true;
                capturedMethod = null;
                capturedExtra = null;
                try
                {
                    result = generate(step.Config);
                }
                catch (Exception e)
                {
                    step.PrebuildError = e;
                    continue;
                }
                finally
                {
                    collecting = false;
                }
                if (result == placeholder && capturedMethod != null)
                {
                    pending.Add((step, capturedMethod, capturedExtra));
                    collected++;
                }
                else if (result != null && result != placeholder)
                {
                    step.Built = result;
                }
            }
            capturedMethod = null;
            capturedExtra = null;
            for (var i = 0; i < pending.Count; i += MaxPerModule)
            {
                var chunk = pending.GetRange(i, Math.Min(MaxPerModule, pending.Count - i));
                var built = chunk.Count > 1 ? TryBuildModule(chunk) : null;
                if (built != null)
                {
                    modules++;
                    batched += chunk.Count;
                    for (var k = 0; k < chunk.Count; k++)
                    {
                        chunk[k].step.Built = built[k];
                    }
                    continue;
                }
                foreach (var (step, method, extra) in chunk)
                {
                    single++;
                    try
                    {
                        step.Built = GeneratedAssemblyLoader.Generate(method, extra);
                    }
                    catch (Exception e)
                    {
                        step.PrebuildError = e;
                    }
                }
            }
            ticks += Stopwatch.GetTimestamp() - start;
        }

        // Harmony's GeneratedAssemblyLoader.Generate(DynamicMethodDefinition, IEnumerable<Assembly>) for several methods.
        private static MethodInfo[] TryBuildModule(List<(Coalesce.Step step, DynamicMethodDefinition method, List<Assembly> extra)> chunk)
        {
            if (chunk.Any(c => c.method.Debug) || Switches.TryGetSwitchValue("DMDDumpTo", out var dump) && dump is string path && !string.IsNullOrEmpty(path))
            {
                return null;
            }
            var extra = chunk.SelectMany(c => c.extra).Distinct().ToList();
            var module = ModuleDefinition.CreateModule(chunk[0].method.GetDumpName("Cecil"), new ModuleParameters
            {
                Kind = ModuleKind.Dll,
                ReflectionImporterProvider = MMReflectionImporter.ProviderNoDefault
            });
            try
            {
                var type = new TypeDefinition("", "HarmonyGenerated", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
                module.Types.Add(type);
                var clones = new List<MethodDefinition>(chunk.Count);
                foreach (var item in chunk)
                {
                    var definition = item.method.Definition;
                    var clone = definition.Clone();
                    clone.DeclaringType = null;
                    type.Methods.Add(clone);
                    clone.NoInlining = true;
                    Relinker relinker = (reference, _) => reference != definition ? module.ImportReference(reference) : clone;
                    clone.ReturnType = definition.ReturnType.Relink(relinker, clone);
                    foreach (var parameter in clone.Parameters)
                    {
                        parameter.ParameterType = parameter.ParameterType.Relink(relinker, clone);
                    }
                    foreach (var variable in clone.Body.Variables)
                    {
                        variable.VariableType = variable.VariableType.Relink(relinker, clone);
                    }
                    foreach (var handler in clone.Body.ExceptionHandlers)
                    {
                        if (handler.CatchType != null)
                        {
                            handler.CatchType = handler.CatchType.Relink(relinker, clone);
                        }
                    }
                    foreach (var instruction in clone.Body.Instructions)
                    {
                        if (instruction.Operand is IMetadataTokenProvider provider && !(provider is ParameterDefinition))
                        {
                            instruction.Operand = provider.Relink(relinker, clone);
                        }
                    }
                    clones.Add(clone);
                }
                if (clones.Select(c => c.FullName).Distinct().Count() != clones.Count)
                {
                    return null;
                }
                var ignores = (ConstructorInfo)AccessTools.Field(typeof(DynamicMethodDefinition), "c_IgnoresAccessChecksToAttribute").GetValue(null);
                foreach (var reference in module.AssemblyReferences.ToArray())
                {
                    var attribute = new CustomAttribute(module.ImportReference(ignores));
                    attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, reference.Name));
                    module.Assembly.CustomAttributes.Add(attribute);
                }
                module.Assembly.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(UnverifiableCodeAttribute).GetConstructor(Type.EmptyTypes))));
                var loader = new GeneratedAssemblyLoader(module, extra);
                var assembly = loader.LoadCollectible(module);
                var loaded = assembly.ManifestModule;
                var result = new MethodInfo[clones.Count];
                for (var i = 0; i < clones.Count; i++)
                {
                    result[i] = loaded.ResolveMethod(clones[i].MetadataToken.ToInt32()) as MethodInfo;
                    if (result[i] == null || result[i].Name != clones[i].Name)
                    {
                        failedModules++;
                        return null;
                    }
                }
                return result;
            }
            catch (Exception e)
            {
                if (failedModules++ < 3)
                {
                    Log.Message($"[RLO] {Feature}: a combined module could not be built ({e.GetType().Name}: {e.Message}); those methods are built one by one by Harmony.");
                }
                return null;
            }
            finally
            {
                ((IDisposable)module).Dispose();
            }
        }

        public static string Stats() => $"{Feature}: {collected} methods on the assembly path collected in coalesced rebuilds, {batched} built in {modules} combined modules, {single} built one by one, {failedModules} combined modules fell back; {ticks / (double)Stopwatch.Frequency:F1} s";
    }
}
#endif
