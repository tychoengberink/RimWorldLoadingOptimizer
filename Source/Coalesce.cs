#if RW17
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Coalesce
    {
        internal sealed class Step
        {
            public MethodBase Original;
            public object Config;
            public Dictionary<int, CodeInstruction> Instructions;
            public byte[] Bytes;
            public MethodInfo Built;
            public Exception PrebuildError;
        }

        internal sealed class Chain
        {
            public MethodBase Original;
            public readonly List<Step> Steps = new List<Step>();
            public bool Finalizers;
            public bool HadState;
            public byte[] PreBytes;
            public object PreDetour;
            public bool PreApplied;
            public bool Touched;
            public bool Superseded;
        }

        private sealed class Window
        {
            public readonly Thread Owner = Thread.CurrentThread;
            public readonly List<Step> Steps = new List<Step>();
            public readonly List<Chain> Chains = new List<Chain>();
            public readonly Dictionary<MethodBase, Chain> ByOriginal = new Dictionary<MethodBase, Chain>();
            public bool Suspended;
            public volatile Exception Failure;
        }

        internal sealed class Dry
        {
            public MethodBase Original;
            public bool Finalizers;
            public Chain Chain;
            public bool HadState;
            public byte[] PreBytes;
            public object PreDetour;
            public bool PreApplied;
            public object Config;
            public Dictionary<int, CodeInstruction> Instructions;
            public byte[] Bytes;
            public bool Real;
        }

        public sealed class Scope
        {
            internal Dry Saved;
            internal Dry Mine;
        }

        private sealed class AbortStep : Exception
        {
        }

        [ThreadStatic] private static Window window;
        [ThreadStatic] private static Dry dry;
        [ThreadStatic] private static bool flushing;
        [ThreadStatic] private static MethodBase jobOriginal;
        [ThreadStatic] private static bool jobPlain;

        private static readonly Dictionary<MethodBase, Window> pending = new Dictionary<MethodBase, Window>();
        private static volatile int pendingCount;
        private static readonly Dictionary<object, bool> safety = new Dictionary<object, bool>();

        private static object locker;
        private static Dictionary<MethodBase, byte[]> state;
        private static IDictionary detours;
        private static FastInvokeHandler generate, detour, update, createException, isFactory;
        private static FieldInfo configOriginal, configSource, configPersistence, creatorConfig;
        private static FieldInfo auxiliary, patchMethods, containerAttributes, attributeType;
        private static FieldInfo[] infoArrays;
        private static PropertyInfo infoDebugging, detourTarget, detourApplied;
        private static FieldInfo jobOriginalField, jobRequestedField;
        private static object platform;
        private static MethodInfo compile;
        private static MethodInfo getTypeFromHandle;
        private static ConstructorInfo wrapException;
        private static MethodInfo placeholder;
        private static bool installed, broken;

        private static long windows, steps, merged, builds, skipped, replays, failures, superseded, crossThread, verifyChecked, verifyMismatches, flushTicks;
        private static readonly Dictionary<string, long> flushReasons = new Dictionary<string, long>();

        public static void Install(Harmony harmony)
        {
            var asm = typeof(Harmony).Assembly;
            Type T(string name) => asm.GetType(name, true);
            var configType = T("HarmonyLib.MethodCreatorConfig");
            var creatorType = T("HarmonyLib.MethodCreator");
            var toolsType = T("HarmonyLib.PatchTools");
            var sharedType = T("HarmonyLib.HarmonySharedState");
            var patchAll = AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), new[] { typeof(Assembly) });
            var processJob = AccessTools.Method(typeof(PatchClassProcessor), "ProcessPatchJob");
            var wrapper = AccessTools.Method(T("HarmonyLib.PatchFunctions"), "UpdateWrapper");
            var generateMethod = AccessTools.Method(configType, "GenerateMethod");
            var createReplacement = AccessTools.Method(creatorType, "CreateReplacement");
            var detourMethod = AccessTools.Method(toolsType, "DetourMethod");
            var updatePatchInfo = AccessTools.Method(sharedType, "UpdatePatchInfo");
            var getPatchInfo = AccessTools.Method(sharedType, "GetPatchInfo");
            var getPatched = AccessTools.Method(sharedType, "GetPatchedMethods");
            var exceptionCreate = AccessTools.Method(typeof(HarmonyException), "Create");
            var factory = AccessTools.Method(typeof(Patch), "IsFactory");
            if (!Fingerprint.Matches("Harmony rebuild coalescing",
                Fingerprint.Known(patchAll, "7501920D86FDA870"),
                Fingerprint.Known(processJob, "5371576601E978BA"),
                Fingerprint.Known(wrapper, "4F007C93FF50DD95"),
                Fingerprint.Known(generateMethod, "C830F7F89F2A09F2"),
                Fingerprint.Known(createReplacement, "E08133BE98F80F7A"),
                Fingerprint.Known(detourMethod, "1D823234BBA87170"),
                Fingerprint.Known(updatePatchInfo, "1648080B49C56B0C"),
                Fingerprint.Known(getPatchInfo, "FCBBA976F9FCCF54"),
                Fingerprint.Known(getPatched, "3E859CDA5B2B57F4"),
                Fingerprint.Known(AccessTools.Method(typeof(PatchClassProcessor), "Patch"), "A6B184C7B47EA807"),
                Fingerprint.Known(AccessTools.Constructor(typeof(PatchClassProcessor), new[] { typeof(Harmony), typeof(Type) }), "DEB7684BDCD13818"),
                Fingerprint.Known(AccessTools.Method(typeof(PatchClassProcessor), "ReportException"), "72FB52C83DBB8273"),
                Fingerprint.Known(AccessTools.Method(typeof(PatchClassProcessor), "PatchWithAttributes"), "8C881B8DD408EF34"),
                Fingerprint.Known(AccessTools.Method(typeof(PatchClassProcessor), "BulkPatch"), "490285EDCCA67BA5"),
                Fingerprint.Known(AccessTools.Method(configType, "Prepare"), "D9F69F4344DA456D"),
                Fingerprint.Known(exceptionCreate, "718BF3C405CAB698"),
                Fingerprint.Known(AccessTools.Method(typeof(Patch), "GetMethod"), "58D9D468012A8A5B"),
                Fingerprint.Known(AccessTools.Method(typeof(GeneralExtensions), nameof(GeneralExtensions.HasHarmonyAttribute)), "46C356B6DB840A79"),
                Fingerprint.Known(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.GetTypesFromAssembly)), "4756771C0711DDC4"),
                Fingerprint.Known(AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.GetPatchInfo)), "5AB5534B8F297319"),
                Fingerprint.Known(AccessTools.Method(T("HarmonyLib.PersistentStateHooks"), "Prepare"), "EBCC2C81190AD100")))
            {
                return;
            }
            locker = AccessTools.Field(typeof(PatchProcessor), "locker").GetValue(null);
            state = (Dictionary<MethodBase, byte[]>)AccessTools.Field(sharedType, "state").GetValue(null);
            detours = (IDictionary)AccessTools.Field(toolsType, "detours").GetValue(null);
            generate = HarmonyLib.MethodInvoker.GetHandler(generateMethod);
            detour = HarmonyLib.MethodInvoker.GetHandler(detourMethod);
            update = HarmonyLib.MethodInvoker.GetHandler(updatePatchInfo);
            createException = HarmonyLib.MethodInvoker.GetHandler(exceptionCreate);
            isFactory = HarmonyLib.MethodInvoker.GetHandler(factory);
            configOriginal = AccessTools.Field(configType, "original");
            configSource = AccessTools.Field(configType, "source");
            configPersistence = AccessTools.Field(configType, "persistence");
            creatorConfig = AccessTools.Field(creatorType, "config");
            auxiliary = AccessTools.Field(typeof(PatchClassProcessor), "auxilaryMethods");
            patchMethods = AccessTools.Field(typeof(PatchClassProcessor), "patchMethods");
            containerAttributes = AccessTools.Field(typeof(PatchClassProcessor), "containerAttributes");
            attributeType = AccessTools.Field(T("HarmonyLib.AttributePatch"), "type");
            var infoType = T("HarmonyLib.PatchInfo");
            infoArrays = new[] { "prefixes", "postfixes", "transpilers", "finalizers", "innerprefixes", "innerpostfixes", "innerfinalizers" }.Select(n => AccessTools.Field(infoType, n)).ToArray();
            infoDebugging = AccessTools.Property(infoType, "Debugging");
            detourTarget = AccessTools.Property(T("MonoMod.Core.ICoreDetour"), "Target");
            detourApplied = AccessTools.Property(T("MonoMod.Core.ICoreDetourBase"), "IsApplied");
            var jobType = T("HarmonyLib.PatchJobs`1+Job").MakeGenericType(typeof(MethodInfo));
            jobOriginalField = AccessTools.Field(jobType, "original");
            jobRequestedField = AccessTools.Field(jobType, "requestedOriginals");
            getTypeFromHandle = AccessTools.Method(typeof(Type), nameof(Type.GetTypeFromHandle));
            wrapException = AccessTools.Constructor(typeof(HarmonyException), new[] { typeof(string), typeof(Exception) });
            placeholder = AccessTools.Method(typeof(Coalesce), nameof(Placeholder));
            var triple = asm.GetType("MonoMod.Core.Platforms.PlatformTriple");
            platform = triple?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            compile = triple?.GetMethod("Compile", new[] { typeof(MethodBase) });
            if (locker == null || state == null || detours == null || infoArrays.Any(f => f == null) || new object[] { wrapException, configOriginal, configSource, configPersistence, creatorConfig, auxiliary, patchMethods, containerAttributes, attributeType, infoDebugging, detourTarget, detourApplied, jobOriginalField, jobRequestedField }.Any(x => x == null))
            {
                Log.Message("[RLO] Harmony rebuild coalescing: Harmony's internals differ from the version it was checked against. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var self = typeof(Coalesce);
            harmony.Patch(patchAll, prefix: new HarmonyMethod(self, nameof(PatchAllPrefix)));
            harmony.Patch(processJob, prefix: new HarmonyMethod(self, nameof(JobPrefix)));
            harmony.Patch(wrapper, prefix: new HarmonyMethod(self, nameof(WrapperPrefix)), finalizer: new HarmonyMethod(self, nameof(WrapperFinalizer)));
            harmony.Patch(generateMethod, prefix: new HarmonyMethod(self, nameof(GeneratePrefix)));
            harmony.Patch(createReplacement, postfix: new HarmonyMethod(self, nameof(CreatePostfix)));
            harmony.Patch(detourMethod, prefix: new HarmonyMethod(self, nameof(DetourPrefix)));
            harmony.Patch(updatePatchInfo, prefix: new HarmonyMethod(self, nameof(UpdatePrefix)));
            harmony.Patch(getPatchInfo, prefix: new HarmonyMethod(self, nameof(ReadPrefix)));
            harmony.Patch(getPatched, prefix: new HarmonyMethod(self, nameof(ReadAllPrefix)));
            installed = true;
            LeanFixes.Reports.Add(Stats);
        }

        private static void Placeholder()
        {
        }

        public static bool PatchAllPrefix(Harmony __instance, Assembly assembly, bool __runOriginal)
        {
            if (!__runOriginal || broken || dry != null || flushing || Harmony.DEBUG)
            {
                return true;
            }
            var outer = window;
            var w = new Window();
            window = w;
            Interlocked.Increment(ref windows);
            try
            {
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    if (w.Failure != null)
                    {
                        break;
                    }
                    if (w.Chains.Count > 0 && !TypeSafe(type))
                    {
                        Flush(w, "before attribute code");
                        if (w.Failure != null)
                        {
                            break;
                        }
                    }
                    if (!type.HasHarmonyAttribute())
                    {
                        continue;
                    }
                    var impure = !ClassSafe(type);
                    if (impure)
                    {
                        Flush(w, "before attribute code");
                        if (w.Failure != null)
                        {
                            break;
                        }
                    }
                    var processor = __instance.CreateClassProcessor(type);
                    impure = impure || RunsModCode(processor);
                    if (!impure)
                    {
                        processor.Patch();
                        continue;
                    }
                    Flush(w, "before Prepare, TargetMethod, Cleanup or reverse patch");
                    if (w.Failure != null)
                    {
                        break;
                    }
                    w.Suspended = true;
                    processor.Patch();
                    w.Suspended = false;
                }
            }
            catch
            {
                window = outer;
                Flush(w, "end of PatchAll");
                if (w.Failure != null)
                {
                    throw w.Failure;
                }
                throw;
            }
            window = outer;
            Flush(w, "end of PatchAll");
            if (w.Failure != null)
            {
                throw w.Failure;
            }
            return false;
        }

        private static bool RunsModCode(PatchClassProcessor processor)
        {
            if (((IDictionary)auxiliary.GetValue(processor)).Count > 0)
            {
                return true;
            }
            if (((HarmonyMethod)containerAttributes.GetValue(processor))?.debug == true)
            {
                return true;
            }
            foreach (var patch in (IEnumerable)patchMethods.GetValue(processor))
            {
                if ((HarmonyPatchType?)attributeType.GetValue(patch) == HarmonyPatchType.ReversePatch)
                {
                    return true;
                }
            }
            return false;
        }

        public static void JobPrefix(object job)
        {
            var original = (MethodBase)jobOriginalField.GetValue(job);
            jobOriginal = original;
            jobPlain = ((IEnumerable<MethodBase>)jobRequestedField.GetValue(job)).All(m => m == original);
        }

        public static bool WrapperPrefix(MethodBase original, object patchInfo, ref MethodInfo __result, out Scope __state)
        {
            __state = null;
            if (dry != null)
            {
                __state = new Scope { Saved = dry };
                dry = null;
                return true;
            }
            var w = window;
            var job = jobOriginal;
            var plain = jobPlain;
            jobOriginal = null;
            if (w == null || w.Suspended || flushing || broken)
            {
                return true;
            }
            if (w.Failure != null)
            {
                Interlocked.Increment(ref skipped);
                __result = placeholder;
                return false;
            }
            if (job != original || !plain)
            {
                Flush(w, "before a step that cannot be deferred");
                return Continue(w, ref __result);
            }
            var arrays = infoArrays.Select(f => (Patch[])f.GetValue(patchInfo)).ToArray();
            var finalizers = arrays[3].Length > 0;
            var deferrable = arrays[4].Length == 0 && arrays[5].Length == 0 && arrays[6].Length == 0 && !(bool)infoDebugging.GetValue(patchInfo);
            w.ByOriginal.TryGetValue(original, out var chain);
            if (!deferrable)
            {
                Flush(w, "before a step that cannot be deferred");
                return Continue(w, ref __result);
            }
            if (ModCodeOrUnknown(original, arrays))
            {
                Flush(w, "before a step that runs mod code");
                chain = null;
            }
            else if (chain != null && chain.Finalizers != finalizers)
            {
                Flush(w, "first finalizer on a method");
                chain = null;
            }
            if (w.Failure != null)
            {
                Interlocked.Increment(ref skipped);
                __result = placeholder;
                return false;
            }
            var d = new Dry { Original = original, Finalizers = finalizers, Chain = chain };
            if (chain == null)
            {
                lock (state)
                {
                    d.HadState = state.TryGetValue(original, out d.PreBytes);
                }
                lock (detours)
                {
                    d.PreDetour = detours[original];
                }
                d.PreApplied = d.PreDetour != null && (bool)detourApplied.GetValue(d.PreDetour);
            }
            dry = d;
            __state = new Scope { Mine = d };
            return true;
        }

        private static bool Continue(Window w, ref MethodInfo __result)
        {
            if (w.Failure == null)
            {
                return true;
            }
            Interlocked.Increment(ref skipped);
            __result = placeholder;
            return false;
        }

        public static void WrapperFinalizer(Scope __state, Exception __exception)
        {
            if (__state == null)
            {
                return;
            }
            if (__state.Mine == null)
            {
                dry = __state.Saved;
                return;
            }
            dry = null;
            var d = __state.Mine;
            if (__exception != null || d.Real)
            {
                return;
            }
            if (d.Config == null || d.Bytes == null)
            {
                broken = true;
                Log.Warning($"[RLO] Harmony rebuild coalescing: a deferred rebuild of {d.Original.FullDescription()} did not go through the expected steps; coalescing is switched off for the rest of this start.");
                return;
            }
            var w = window;
            var chain = d.Chain;
            if (chain == null)
            {
                chain = new Chain { Original = d.Original, Finalizers = d.Finalizers, HadState = d.HadState, PreBytes = d.PreBytes, PreDetour = d.PreDetour, PreApplied = d.PreApplied };
                w.Chains.Add(chain);
                w.ByOriginal[d.Original] = chain;
                lock (pending)
                {
                    pending[d.Original] = w;
                    pendingCount = pending.Count;
                }
            }
            var step = new Step { Original = d.Original, Config = d.Config, Instructions = d.Instructions, Bytes = d.Bytes };
            chain.Steps.Add(step);
            w.Steps.Add(step);
            Interlocked.Increment(ref steps);
        }

        public static bool GeneratePrefix(object __instance, bool structuredHelper, ref MethodInfo __result)
        {
            var d = dry;
            if (d == null || structuredHelper || flushing || d.Config != null || d.Real)
            {
                return true;
            }
            if ((MethodBase)configOriginal.GetValue(__instance) != d.Original || configSource.GetValue(__instance) != null)
            {
                return true;
            }
            if (configPersistence.GetValue(__instance) != null)
            {
                d.Real = true;
                var w = window;
                Flush(w, "before a step that cannot be deferred");
                if (w.Failure != null)
                {
                    throw new AbortStep();
                }
                return true;
            }
            d.Config = __instance;
            __result = placeholder;
            return false;
        }

        public static void CreatePostfix(object __instance, (MethodInfo, Dictionary<int, CodeInstruction>) __result)
        {
            var d = dry;
            if (d != null && d.Config != null && d.Instructions == null && creatorConfig.GetValue(__instance) == d.Config)
            {
                d.Instructions = __result.Item2;
            }
        }

        public static bool DetourPrefix(MethodBase replacement) => replacement != placeholder;

        public static bool UpdatePrefix(MethodBase original, MethodInfo replacement, byte[] bytes)
        {
            if (replacement != placeholder)
            {
                return true;
            }
            var d = dry;
            if (d != null && d.Original == original)
            {
                d.Bytes = bytes;
            }
            lock (state)
            {
                state[original] = bytes;
            }
            return false;
        }

        public static void ReadPrefix(MethodBase method)
        {
            if (pendingCount == 0 || method == null)
            {
                return;
            }
            Window w;
            lock (pending)
            {
                pending.TryGetValue(method, out w);
            }
            if (w != null && w.Owner != Thread.CurrentThread)
            {
                FromOtherThread(w);
            }
        }

        public static void ReadAllPrefix()
        {
            if (pendingCount == 0)
            {
                return;
            }
            List<Window> others;
            lock (pending)
            {
                others = pending.Values.Where(w => w.Owner != Thread.CurrentThread).Distinct().ToList();
            }
            foreach (var w in others)
            {
                FromOtherThread(w);
            }
        }

        private static void FromOtherThread(Window w)
        {
            if (!Monitor.TryEnter(locker, 10000))
            {
                Log.Warning("[RLO] Harmony rebuild coalescing: another thread read patch state while a merged rebuild was pending and the patch lock stayed busy; it reads the stored state as is.");
                return;
            }
            try
            {
                Interlocked.Increment(ref crossThread);
                Flush(w, "patch state read from another thread");
            }
            finally
            {
                Monitor.Exit(locker);
            }
        }

        private static void Flush(Window w, string reason)
        {
            lock (locker)
            {
                if (w.Chains.Count == 0)
                {
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                var chains = w.Chains.ToList();
                var order = w.Steps.ToList();
                w.Chains.Clear();
                w.Steps.Clear();
                w.ByOriginal.Clear();
                lock (pending)
                {
                    foreach (var chain in chains)
                    {
                        pending.Remove(chain.Original);
                    }
                    pendingCount = pending.Count;
                }
                lock (flushReasons)
                {
                    flushReasons.TryGetValue(reason, out var n);
                    flushReasons[reason] = n + 1;
                }
                var wasFlushing = flushing;
                flushing = true;
                try
                {
                    if (!Optimistic(chains))
                    {
                        Replay(w, chains, order);
                    }
                    else if (RLOMod.Verify)
                    {
                        VerifyIntermediates(chains);
                    }
                }
                finally
                {
                    flushing = wasFlushing;
                    Interlocked.Add(ref flushTicks, Stopwatch.GetTimestamp() - start);
                }
            }
        }

        private static bool Optimistic(List<Chain> chains)
        {
            if (HarmonyBatchLoad.Installed)
            {
                var last = new List<Step>();
                foreach (var chain in chains)
                {
                    var step = chain.Steps[chain.Steps.Count - 1];
                    byte[] current;
                    lock (state)
                    {
                        state.TryGetValue(chain.Original, out current);
                    }
                    if (current == step.Bytes)
                    {
                        last.Add(step);
                    }
                }
                HarmonyBatchLoad.Prebuild(last, config => (MethodInfo)generate(config, false));
            }
            foreach (var chain in chains)
            {
                var last = chain.Steps[chain.Steps.Count - 1];
                byte[] current;
                lock (state)
                {
                    state.TryGetValue(chain.Original, out current);
                }
                if (current != last.Bytes)
                {
                    chain.Superseded = true;
                    Interlocked.Increment(ref superseded);
                    continue;
                }
                chain.Touched = true;
                if (Build(last) != null)
                {
                    return false;
                }
                Interlocked.Increment(ref builds);
                Interlocked.Add(ref merged, chain.Steps.Count - 1);
            }
            return true;
        }

        private static void Replay(Window w, List<Chain> chains, List<Step> order)
        {
            Interlocked.Increment(ref replays);
            var skip = new HashSet<MethodBase>(chains.Where(c => c.Superseded).Select(c => c.Original));
            foreach (var chain in chains.Where(c => !c.Superseded))
            {
                Restore(chain);
            }
            foreach (var step in order)
            {
                if (skip.Contains(step.Original))
                {
                    continue;
                }
                var error = Build(step);
                if (error != null)
                {
                    Interlocked.Increment(ref failures);
                    w.Failure = error;
                    return;
                }
            }
        }

        private static Exception Build(Step step)
        {
            var original = step.Original;
            var replacement = step.Built;
            if (replacement == null)
            {
                if (step.PrebuildError != null)
                {
                    return Wrap(step.PrebuildError, original);
                }
                try
                {
                    replacement = (MethodInfo)generate(step.Config, false);
                }
                catch (Exception e)
                {
                    return Wrap(e, original);
                }
                if (replacement == null)
                {
                    return Wrap(new MissingMethodException("Cannot create replacement for " + original.FullDescription()), original);
                }
                step.Built = replacement;
            }
            try
            {
                detour(null, original, replacement);
            }
            catch (Exception e)
            {
                return Wrap((Exception)createException(null, e, step.Instructions), original);
            }
            try
            {
                update(null, original, replacement, step.Bytes);
            }
            catch (Exception e)
            {
                return Wrap(e, original);
            }
            return null;
        }

        private static Exception Wrap(Exception e, MethodBase original) => e is HarmonyException ? e : (Exception)wrapException.Invoke(new object[] { "Patching exception in method " + original.FullDescription(), e });

        private static void Restore(Chain chain)
        {
            lock (state)
            {
                if (chain.HadState)
                {
                    state[chain.Original] = chain.PreBytes;
                }
                else
                {
                    state.Remove(chain.Original);
                }
            }
            if (!chain.Touched)
            {
                return;
            }
            try
            {
                if (chain.PreDetour != null && chain.PreApplied)
                {
                    detour(null, chain.Original, (MethodBase)detourTarget.GetValue(chain.PreDetour));
                    return;
                }
                lock (detours)
                {
                    var current = detours[chain.Original];
                    if (current != null && current != chain.PreDetour)
                    {
                        ((IDisposable)current).Dispose();
                    }
                    if (chain.PreDetour == null)
                    {
                        detours.Remove(chain.Original);
                    }
                    else
                    {
                        detours[chain.Original] = chain.PreDetour;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] Harmony rebuild coalescing: could not restore {chain.Original.FullDescription()} before replaying a failed patch: {e.Message}");
            }
        }

        private static void VerifyIntermediates(List<Chain> chains)
        {
            foreach (var chain in chains.Where(c => !c.Superseded))
            {
                byte[] current;
                lock (state)
                {
                    state.TryGetValue(chain.Original, out current);
                }
                if (current != chain.Steps[chain.Steps.Count - 1].Bytes)
                {
                    Mismatch($"stored patch state of {chain.Original.FullDescription()} is not the last sequential state");
                }
                for (var i = 0; i < chain.Steps.Count - 1; i++)
                {
                    Interlocked.Increment(ref verifyChecked);
                    try
                    {
                        var replacement = (MethodInfo)generate(chain.Steps[i].Config, false);
                        if (replacement == null)
                        {
                            Mismatch($"intermediate rebuild {i + 1}/{chain.Steps.Count} of {chain.Original.FullDescription()} returned no method");
                        }
                        else if (platform != null && compile != null)
                        {
                            compile.Invoke(platform, new object[] { replacement });
                        }
                    }
                    catch (Exception e)
                    {
                        Mismatch($"intermediate rebuild {i + 1}/{chain.Steps.Count} of {chain.Original.FullDescription()} fails although the merged one works: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
                    }
                }
            }
        }

        private static void Mismatch(string what)
        {
            if (Interlocked.Increment(ref verifyMismatches) <= 20)
            {
                Log.Warning("[RLO] Harmony rebuild coalescing verify mismatch: " + what);
            }
        }

        private static bool ModCodeOrUnknown(MethodBase original, Patch[][] arrays)
        {
            try
            {
                return RunsModCode(original, arrays);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool RunsModCode(MethodBase original, Patch[][] arrays)
        {
            if (arrays[2].Length > 0)
            {
                return true;
            }
            if (!TypeSafe(original.DeclaringType) || original is MethodInfo info && !TypeSafe(info.ReturnType) || original.GetParameters().Any(p => !TypeSafe(p.ParameterType)))
            {
                return true;
            }
            foreach (var patch in arrays[0].Concat(arrays[1]).Concat(arrays[3]))
            {
                var method = patch.PatchMethod;
                if (method == null || (bool)isFactory(null, method) || !MethodSafe(method) || !TypeSafe(method.DeclaringType) || !TypeSafe(method.ReturnType) || method.GetParameters().Any(p => !TypeSafe(p.ParameterType)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ClassSafe(Type type) => type.BaseType == typeof(object) && type.GetMethods(AccessTools.all).All(MethodSafe);

        private static bool MethodSafe(MethodBase method) => Cached(method, () => AttributesSafe(method.GetCustomAttributesData())
            && method.GetParameters().All(p => AttributesSafe(p.GetCustomAttributesData()))
            && (!(method is MethodInfo info) || info.ReturnParameter == null || AttributesSafe(info.ReturnParameter.GetCustomAttributesData())));

        private static bool TypeSafe(Type type)
        {
            while (type != null && (type.HasElementType || type.IsGenericType && !type.IsGenericTypeDefinition))
            {
                type = type.HasElementType ? type.GetElementType() : type.GetGenericTypeDefinition();
            }
            if (type == null || type.IsGenericParameter)
            {
                return true;
            }
            var current = type;
            return Cached(current, () =>
            {
                for (var t = current; t != null && !Trusted(t.Assembly); t = t.BaseType)
                {
                    if (!AttributesSafe(t.GetCustomAttributesData()))
                    {
                        return false;
                    }
                }
                return true;
            });
        }

        private static bool AttributesSafe(IList<CustomAttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                var ctor = attribute.Constructor;
                if (Trusted(ctor.DeclaringType.Assembly))
                {
                    continue;
                }
                if (!CodeSafe(ctor))
                {
                    return false;
                }
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.MemberInfo is PropertyInfo property && !CodeSafe(property.GetSetMethod(true)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool CodeSafe(MethodBase method)
        {
            if (method == null)
            {
                return false;
            }
            if (Trusted(method.DeclaringType.Assembly))
            {
                return true;
            }
            return Cached(method, () =>
            {
                for (var t = method.DeclaringType; t != null && !Trusted(t.Assembly); t = t.BaseType)
                {
                    if (t.TypeInitializer != null)
                    {
                        return false;
                    }
                }
                if (method.GetMethodBody() == null)
                {
                    return false;
                }
                foreach (var instruction in PatchProcessor.ReadMethodBody(method))
                {
                    var op = instruction.Key;
                    if (op == OpCodes.Calli || op == OpCodes.Newobj || op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld || op == OpCodes.Jmp || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                    {
                        return false;
                    }
                    if (op != OpCodes.Call && op != OpCodes.Callvirt)
                    {
                        continue;
                    }
                    var target = instruction.Value as MethodBase;
                    if (target == getTypeFromHandle)
                    {
                        continue;
                    }
                    if (!(target is ConstructorInfo) || !target.DeclaringType.IsAssignableFrom(method.DeclaringType) || !CodeSafe(target))
                    {
                        return false;
                    }
                }
                return true;
            });
        }

        private static bool Trusted(Assembly assembly)
        {
            if (assembly == typeof(object).Assembly || assembly == typeof(Harmony).Assembly)
            {
                return true;
            }
            var name = assembly.GetName().Name;
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System.", StringComparison.Ordinal);
        }

        private static bool Cached(object key, Func<bool> compute)
        {
            lock (safety)
            {
                if (safety.TryGetValue(key, out var known))
                {
                    return known;
                }
            }
            bool result;
            try
            {
                result = compute();
            }
            catch (Exception)
            {
                result = false;
            }
            lock (safety)
            {
                safety[key] = result;
            }
            return result;
        }

        public static string Stats()
        {
            if (!installed)
            {
                return "Harmony rebuild coalescing: off";
            }
            string reasons;
            lock (flushReasons)
            {
                reasons = string.Join(", ", flushReasons.OrderByDescending(p => p.Value).Select(p => $"{p.Value} {p.Key}"));
            }
            var text = $"Harmony rebuild coalescing: {windows} PatchAll calls, {steps} rebuilds deferred, {builds} built after merging ({merged} rebuilds saved), {((double)flushTicks / Stopwatch.Frequency).ToString("0.00", CultureInfo.InvariantCulture)} s in merged builds; flushes: {reasons}";
            if (superseded + crossThread + replays + skipped > 0)
            {
                text += $"; {superseded} superseded by another patcher, {crossThread} flushed for another thread, {replays} replayed one by one ({failures} patch errors reproduced), {skipped} steps skipped after an error";
            }
            if (broken)
            {
                text += "; switched off during startup after an unexpected rebuild";
            }
            if (RLOMod.Verify)
            {
                text += $"; verify: {verifyChecked} intermediate rebuilds checked, {verifyMismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
