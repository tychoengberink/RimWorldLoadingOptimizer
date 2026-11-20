#if RW17
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class HarmonyLazy
    {
        private const string Feature = "Lazy Harmony replacements";
        private const int Magic = 0x315A4C52;
        private const string StageAssembly = "RimWorldLoadingOptimizer.Stage";
        private const string WrapperCalls = "<>c__DisplayClass8_0..ctor,Action`1..ctor,Enumerable.Any,Enumerator.MoveNext,Enumerator.MoveNext,Enumerator.MoveNext,Enumerator.get_Current,Enumerator.get_Current,Enumerator.get_Current,Exception.get_Data,Exception.get_Data,Func`2..ctor,GeneralExtensions.FullDescription,HarmonyException.Create,HarmonySharedState.RegisterReplacement,HarmonySharedState.UpdatePatchInfo,IDictionary.Contains,IDictionary.Contains,IDisposable.Dispose,IDisposable.Dispose,IDisposable.Dispose,IDisposable.Dispose,IDisposable.Dispose,IEnumerable`1.GetEnumerator,IEnumerator.MoveNext,IEnumerator`1.get_Current,InvalidOperationException..ctor,List`1..ctor,List`1.Add,List`1.Add,List`1.ForEach,List`1.GetEnumerator,List`1.GetEnumerator,List`1.GetEnumerator,MethodBodyPatching.FamilyOwner,MethodBodyPatching.Install,MethodBodyPatching.Instantiations,MethodBodyPatching.IsDefinitionWide,MethodBodyPatching.Prepare,MethodBodyPatching.Release,MethodBodyPatching.ThrowIfInstalling,MethodBodyPatching.get_ProtocolLock,MethodCreator..ctor,MethodCreator.CreateReplacement,MissingMethodException..ctor,Monitor.Enter,Monitor.Exit,PatchFunctions.FamilyConfig,PatchFunctions.ForgetReverse,PatchFunctions.NameTranspilers,PatchFunctions.NameTranspilers,PatchFunctions.SingleConfig,PatchFunctions.ThrowIfRuntimeAsync,PatchInfo.ValidateSurvivingMetadata,PatchInfo.get_Debugging,PatchInfoSerialization.SerializeValidated,PersistentStateHooks.Commit,PersistentStateHooks.Dispose,PersistentStateHooks.Prepare,PersistentStateHooks.Prepare,PersistentStateHooks.Prepare,String.Concat";

        private sealed class Pending
        {
            public int Id;
            public MethodBase Original;
            public byte[] Previous;
            public Patch[][] Live;
            public string Expected;
            public volatile bool Done;
            public Exception Failure;
        }

        private sealed class Dry
        {
            public string Text;
        }

        private sealed class Captured : Exception
        {
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<(Guid, int), List<(byte[] previous, byte[] now)>> proven = new Dictionary<(Guid, int), List<(byte[], byte[])>>();
        private static readonly HashSet<(Guid, int)> early = new HashSet<(Guid, int)>();
        private static readonly HashSet<(Guid, int)> fired = new HashSet<(Guid, int)>();
        private static readonly Dictionary<MethodBase, Pending> pending = new Dictionary<MethodBase, Pending>();
        private static readonly List<Pending> table = new List<Pending>();
        private static readonly Dictionary<MethodBase, bool> eligible = new Dictionary<MethodBase, bool>();
        private static readonly Dictionary<Module, Guid> moduleIds = new Dictionary<Module, Guid>();
        private static readonly List<(MethodBase original, byte[] before, byte[] after)> recorded = new List<(MethodBase, byte[], byte[])>();
        private static readonly MismatchLog mismatches = new MismatchLog(Feature);
        private static Dictionary<MethodBase, byte[]> state;
        private static IDictionary reverseRecipes;
        private static object locker;
        private static FastInvokeHandler updateWrapper, updatePatchInfo, validate, prepare, release, isProviderTarget, throwIfInstalling;
        private static Func<PatchInfo, byte[]> serialize;
        private static Func<byte[], PatchInfo> deserialize;
        private static Func<MethodBase, int, Exception> place;
        private static Func<MethodBase, Exception> lift, restore;
        private static FieldInfo configOriginal, configSource, creatorConfig;
        private static SelfCheck check;
        private static string path, header;
        private static int listed, deferred, stubs, resolvedEarly, resolvedLate, superseded, notProven, calledEarly, impure, generic, notProvider, failed, compared, notAdopted;
        private static long stubTicks, resolveTicks;

        private static readonly long IdleMs = DevSwitches.LazyIdleMs;
        private static Harmony patcher;
        private static MethodInfo idleTarget;
        private static bool idleWaiting;
        private static int idleNext, idleBuilt, idleFailed, idleFrames;
        private static long idleTicks;

        [ThreadStatic] private static Pending resolving;
        [ThreadStatic] private static Dry dry;

        public static void Install(Harmony harmony)
        {
            var asm = typeof(Harmony).Assembly;
            Type T(string name) => asm.GetType(name);
            MethodInfo M(Type type, string name) => type == null ? null : AccessTools.Method(type, name);
            var configType = T("HarmonyLib.MethodCreatorConfig");
            var creatorType = T("HarmonyLib.MethodCreator");
            var shared = T("HarmonyLib.HarmonySharedState");
            var serialization = T("HarmonyLib.PatchInfoSerialization");
            var functions = T("HarmonyLib.PatchFunctions");
            var bodies = T("HarmonyLib.MethodBodyPatching");
            var stage = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == StageAssembly)?.GetType("RimWorldLoadingOptimizer.LazyStage");
            var wrapper = M(functions, "UpdateWrapper");
            var create = M(creatorType, "CreateReplacement");
            var getPatchInfo = shared == null ? null : AccessTools.Method(shared, "GetPatchInfo", new[] { typeof(MethodBase) });
            var update = M(shared, "UpdatePatchInfo");
            var validateMethod = AccessTools.Method(typeof(PatchInfo), "ValidateSurvivingMetadata");
            var serializeMethod = M(serialization, "SerializeValidated");
            var deserializeMethod = serialization == null ? null : AccessTools.Method(serialization, "Deserialize", new[] { typeof(byte[]) });
            var hooks = M(T("HarmonyLib.PersistentStateHooks"), "Prepare");
            var prepareMethod = M(bodies, "Prepare");
            var releaseMethod = M(bodies, "Release");
            var providerMethod = M(bodies, "IsProviderTarget");
            var installingMethod = M(bodies, "ThrowIfInstalling");
            var host = AccessTools.TypeByName("HarmonyMod.RuntimeILPatching");
            var hostRead = M(host, "ReadBody");
            var hostInstall = M(host, "Install");
            var hostRebuild = M(host, "Rebuild");
            var hostStageType = host?.GetNestedType("Transformer", BindingFlags.NonPublic);
            var hostStage = hostStageType?.GetConstructor(Type.EmptyTypes);
            var hostTransform = M(hostStageType, "TryTransform");
            locker = bodies == null ? null : AccessTools.Property(bodies, "ProtocolLock")?.GetValue(null);
            state = shared == null ? null : AccessTools.Field(shared, "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
            reverseRecipes = functions == null ? null : AccessTools.Field(functions, "reverseRecipes")?.GetValue(null) as IDictionary;
            configOriginal = configType == null ? null : AccessTools.Field(configType, "original");
            configSource = configType == null ? null : AccessTools.Field(configType, "source");
            creatorConfig = creatorType == null ? null : AccessTools.Field(creatorType, "config");
            if (new object[] { wrapper, create, getPatchInfo, update, validateMethod, serializeMethod, deserializeMethod, hooks, prepareMethod, releaseMethod, providerMethod, installingMethod, hostRead, hostInstall, hostRebuild, hostStage, hostTransform, locker, state, reverseRecipes, configOriginal, configSource, creatorConfig }.Any(x => x == null)
                || create.ReturnType != typeof((MethodInfo, Dictionary<int, CodeInstruction>)) || deserializeMethod.ReturnType != typeof(PatchInfo) || serializeMethod.ReturnType != typeof(byte[])
                || prepareMethod.ReturnType != typeof(bool) || prepareMethod.GetParameters().Length != 2 || providerMethod.ReturnType != typeof(bool) || update.GetParameters().Length != 3)
            {
                VersionCheck.Skip(Feature, "a Harmony method it needs no longer exists");
                return;
            }
            if (stage == null)
            {
                VersionCheck.Skip(Feature, $"{StageAssembly}.dll is not loaded");
                return;
            }
            if (!VersionCheck.Pattern(Feature, wrapper, code => VersionCheck.CallsExactly(code, WrapperCalls), "makes exactly the calls this speedup was built around")
                || !VersionCheck.Pattern(Feature, hooks, code => VersionCheck.CallsExactly(code, "AppDomain.GetData,AppDomain.get_CurrentDomain,PersistentStateHooks..ctor"), "makes exactly the calls this speedup was built around"))
            {
                return;
            }
            if (!PatchSafety.Init())
            {
                VersionCheck.Skip(Feature, "a Harmony method it needs no longer exists");
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(wrapper, create, getPatchInfo, update, validateMethod, serializeMethod, deserializeMethod, hooks, prepareMethod, releaseMethod, providerMethod, installingMethod, hostRead, hostInstall, hostRebuild, hostStage, hostTransform));
            if (check.IsOff)
            {
                return;
            }
            updateWrapper = HarmonyLib.MethodInvoker.GetHandler(wrapper);
            updatePatchInfo = HarmonyLib.MethodInvoker.GetHandler(update);
            validate = HarmonyLib.MethodInvoker.GetHandler(validateMethod);
            prepare = HarmonyLib.MethodInvoker.GetHandler(prepareMethod);
            release = HarmonyLib.MethodInvoker.GetHandler(releaseMethod);
            isProviderTarget = HarmonyLib.MethodInvoker.GetHandler(providerMethod);
            throwIfInstalling = HarmonyLib.MethodInvoker.GetHandler(installingMethod);
            serialize = AccessTools.MethodDelegate<Func<PatchInfo, byte[]>>(serializeMethod);
            deserialize = AccessTools.MethodDelegate<Func<byte[], PatchInfo>>(deserializeMethod);
            place = (Func<MethodBase, int, Exception>)Delegate.CreateDelegate(typeof(Func<MethodBase, int, Exception>), stage.GetMethod("Place"));
            lift = (Func<MethodBase, Exception>)Delegate.CreateDelegate(typeof(Func<MethodBase, Exception>), stage.GetMethod("Lift"));
            restore = (Func<MethodBase, Exception>)Delegate.CreateDelegate(typeof(Func<MethodBase, Exception>), stage.GetMethod("Restore"));
            var start = (Func<MethodInfo, bool>)Delegate.CreateDelegate(typeof(Func<MethodInfo, bool>), stage.GetMethod("Start"));
            if (!start(AccessTools.Method(typeof(HarmonyLazy), nameof(Ensure))))
            {
                VersionCheck.Skip(Feature, "the game cannot replace method bodies on this system");
                return;
            }
            header = $"{asm.ManifestModule.ModuleVersionId}|{Environment.Version}|{host.Module.ModuleVersionId}|{typeof(object).Module.ModuleVersionId}";
            path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "harmony-lazy.bin");
            try
            {
                Load();
            }
            catch (Exception e)
            {
                proven.Clear();
                early.Clear();
                RLOLog.Detail($"{Feature}: the list from the last start could not be read ({e.Message}); every replacement is built right away this start");
            }
            HarmonyMethod H(string name) => new HarmonyMethod(typeof(HarmonyLazy), name);
            harmony.Patch(wrapper, prefix: H(nameof(WrapperPrefix)), postfix: H(nameof(WrapperPostfix)));
            harmony.Patch(create, postfix: H(nameof(CreatePostfix)));
            harmony.Patch(getPatchInfo, prefix: H(nameof(ReadPrefix)));
            MainMenu.Then(Save);
            patcher = harmony;
            MainMenu.Then(StartIdle);
            StartupPhaseFixes.Reports.Add(Stats);
        }

        private static void StartIdle()
        {
            idleTarget = AccessTools.Method(typeof(Root_Entry), nameof(Root_Entry.Update));
            if (stubs > 0 && idleTarget != null && IdleMs > 0)
            {
                patcher.Patch(idleTarget, postfix: new HarmonyMethod(typeof(HarmonyLazy), nameof(Idle)));
            }
        }

        public static void Idle()
        {
            if (LongEventHandler.AnyEventNowOrWaiting)
            {
                if (!idleWaiting)
                {
                    idleWaiting = true;
                    RLOLog.Detail($"{Feature}: building in the main menu paused while the game loads, {idleBuilt} of {stubs} methods built there so far");
                }
                return;
            }
            idleWaiting = false;
            idleFrames++;
            var start = Stopwatch.GetTimestamp();
            var end = start + Stopwatch.Frequency * IdleMs / 1000;
            do
            {
                Pending entry = null;
                lock (gate)
                {
                    while (entry == null && idleNext < table.Count)
                    {
                        var next = table[idleNext++];
                        if (!next.Done && next.Failure == null)
                        {
                            entry = next;
                        }
                    }
                }
                if (entry == null)
                {
                    idleTicks += Stopwatch.GetTimestamp() - start;
                    patcher.Unpatch(idleTarget, AccessTools.Method(typeof(HarmonyLazy), nameof(Idle)));
                    RLOLog.Detail($"{Feature}: {idleBuilt} of {stubs} methods built in the main menu in {StartupPhaseFixes.Seconds(idleTicks):F1} s over {idleFrames} frames (at most {IdleMs} ms per frame), {idleFailed} could not be built; the others were built at their first call or by a later patch");
                    return;
                }
                try
                {
                    Resolve(entry, false);
                    idleBuilt++;
                }
                catch (Exception)
                {
                    idleFailed++;
                }
            }
            while (Stopwatch.GetTimestamp() < end);
            idleTicks += Stopwatch.GetTimestamp() - start;
        }

        private static bool Same(byte[] a, byte[] b) => a == b || a != null && b != null && a.AsSpan().SequenceEqual(b);

        private static byte[] Stored(MethodBase original)
        {
            lock (state)
            {
                state.TryGetValue(original, out var bytes);
                return bytes;
            }
        }

        private static (Guid, int) Key(MethodBase method)
        {
            var module = method.Module;
            Guid id;
            lock (moduleIds)
            {
                if (!moduleIds.TryGetValue(module, out id))
                {
                    moduleIds[module] = id = module.ModuleVersionId;
                }
            }
            return (id, method.MetadataToken);
        }

        private static bool Eligible(MethodBase method)
        {
            lock (eligible)
            {
                if (eligible.TryGetValue(method, out var known))
                {
                    return known;
                }
            }
            bool result;
            try
            {
                var assembly = method.Module.Assembly;
                result = method is MethodInfo info && !method.IsGenericMethod && !method.ContainsGenericParameters && !method.IsAbstract
                    && method.DeclaringType != null && !method.DeclaringType.IsGenericType && !method.DeclaringType.IsInterface
                    && (method.CallingConvention & CallingConventions.VarArgs) == 0 && !assembly.IsDynamic && !assembly.IsCollectible
                    && ((int)method.GetMethodImplementationFlags() & 0x2000) == 0
                    && assembly != typeof(HarmonyLazy).Assembly && assembly.GetName().Name != StageAssembly && !PatchSafety.TrustedAssembly(assembly)
                    && !info.ReturnType.IsByRefLike && method.GetParameters().All(p => !p.ParameterType.IsByRefLike)
                    && method.GetMethodBody() != null;
            }
            catch (Exception)
            {
                result = false;
            }
            lock (eligible)
            {
                eligible[method] = result;
            }
            return result;
        }

        private static bool Reversed(MethodBase original)
        {
            lock (locker)
            {
                return reverseRecipes.Contains(original)
                    || AppDomain.CurrentDomain.GetData("Harmony.MethodBodyPatching.ReverseOwners.v1") is IDictionary owners && owners.Contains(original);
            }
        }

        public static bool Overrides(MethodBase original)
        {
            var entry = resolving;
            return entry != null && entry.Original == original;
        }

        public static PatchInfo PreviousInfo()
        {
            var previous = resolving.Previous;
            return previous == null ? null : deserialize(previous);
        }

        public static bool ReadPrefix(MethodBase method, ref PatchInfo __result)
        {
            if (!Overrides(method))
            {
                return true;
            }
            __result = PreviousInfo();
            return false;
        }

        private static bool Now(Pending waiting)
        {
            if (waiting != null)
            {
                Resolve(waiting, false);
                Interlocked.Increment(ref superseded);
            }
            return true;
        }

        public static bool WrapperPrefix(MethodBase original, PatchInfo patchInfo, ref MethodInfo __result, out byte[] __state)
        {
            __state = null;
            if (dry != null)
            {
                return true;
            }
            var before = Stored(original);
            __state = before;
            Pending waiting = null;
            if (!Overrides(original))
            {
                lock (gate)
                {
                    if (pending.TryGetValue(original, out var known) && !known.Done)
                    {
                        waiting = known;
                    }
                }
            }
            if (resolving != null || MainMenu.Reached || Harmony.DEBUG || check.IsOff || proven.Count == 0 || !Eligible(original))
            {
                return Now(waiting);
            }
            var key = Key(original);
            if (!proven.TryGetValue(key, out var list))
            {
                Interlocked.Increment(ref notProven);
                return Now(waiting);
            }
            if (early.Contains(key))
            {
                Interlocked.Increment(ref calledEarly);
                return Now(waiting);
            }
            if (AppDomain.CurrentDomain.GetData("Harmony.Infix.PersistentState.v1") != null || !PatchSafety.Pure(original, patchInfo) || Reversed(original))
            {
                Interlocked.Increment(ref impure);
                return Now(waiting);
            }
            if (PatchSafety.HasGenericPatch(patchInfo))
            {
                Interlocked.Increment(ref generic);
                return Now(waiting);
            }
            Pending entry;
            byte[] bytes;
            string expected = null;
            var live = PatchSafety.Patches(patchInfo);
            lock (locker)
            {
                try
                {
                    throwIfInstalling(null, original);
                    validate(patchInfo);
                }
                catch (Exception)
                {
                    return Now(waiting);
                }
                patchInfo.VersionCount++;
                try
                {
                    bytes = serialize(patchInfo);
                }
                catch (Exception)
                {
                    patchInfo.VersionCount--;
                    return Now(waiting);
                }
                if (!list.Any(pair => Same(pair.previous, before) && Same(pair.now, bytes)))
                {
                    patchInfo.VersionCount--;
                    Interlocked.Increment(ref notProven);
                    return Now(waiting);
                }
                bool prepared;
                try
                {
                    prepared = (bool)prepare(null, original, false);
                }
                catch (Exception)
                {
                    patchInfo.VersionCount--;
                    return Now(waiting);
                }
                if (!(bool)isProviderTarget(null, original))
                {
                    if (prepared)
                    {
                        release(null, original);
                    }
                    patchInfo.VersionCount--;
                    Interlocked.Increment(ref notProvider);
                    return Now(waiting);
                }
                if (DevSwitches.Verify || check.IsChecking)
                {
                    var holder = new Dry();
                    dry = holder;
                    try
                    {
                        var copy = deserialize(bytes);
                        copy.VersionCount--;
                        PatchSafety.Adopt(copy, live);
                        updateWrapper(null, original, copy);
                    }
                    catch (Captured)
                    {
                    }
                    catch (Exception)
                    {
                        holder.Text = null;
                    }
                    finally
                    {
                        dry = null;
                    }
                    expected = holder.Text;
                    if (expected == null)
                    {
                        if (prepared)
                        {
                            release(null, original);
                        }
                        patchInfo.VersionCount--;
                        mismatches.Report($"{original.DeclaringType?.FullName}.{original.Name}: a replacement that was built on the last start could not be built now");
                        check.Report(false, $"replacement of {original.DeclaringType?.FullName}.{original.Name}");
                        return Now(waiting);
                    }
                }
                var start = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    entry = waiting;
                    if (entry == null)
                    {
                        entry = new Pending { Id = table.Count, Original = original };
                        table.Add(entry);
                        Exception failure;
                        try
                        {
                            failure = place(original, entry.Id);
                        }
                        catch (Exception e)
                        {
                            failure = e;
                        }
                        if (failure != null)
                        {
                            entry.Done = true;
                            if (prepared)
                            {
                                release(null, original);
                            }
                            patchInfo.VersionCount--;
                            Interlocked.Increment(ref failed);
                            return true;
                        }
                        pending[original] = entry;
                        stubs++;
                    }
                    entry.Previous = before;
                    entry.Live = live;
                    entry.Expected = expected;
                    updatePatchInfo(null, original, null, bytes);
                }
                Interlocked.Add(ref stubTicks, Stopwatch.GetTimestamp() - start);
            }
            Interlocked.Increment(ref deferred);
            lock (recorded)
            {
                recorded.Add((original, before, bytes));
            }
            if (expected != null)
            {
                Resolve(entry, false);
            }
            __result = original as MethodInfo;
            return false;
        }

        public static void WrapperPostfix(MethodBase original, byte[] __state, bool __runOriginal)
        {
            if (!__runOriginal || dry != null)
            {
                return;
            }
            var now = Stored(original);
            var before = __state;
            var mine = resolving;
            if (mine != null && mine.Original == original)
            {
                before = mine.Previous;
            }
            if (now == null || MainMenu.Reached || !Eligible(original))
            {
                return;
            }
            lock (recorded)
            {
                recorded.Add((original, before, now));
            }
        }

        public static void CreatePostfix(object __instance, (MethodInfo, Dictionary<int, CodeInstruction>) __result)
        {
            var holder = dry;
            if (holder != null)
            {
                holder.Text = __result.Item1 == null ? null : Text(__result.Item2);
                throw new Captured();
            }
            var entry = resolving;
            if (entry == null || entry.Expected == null)
            {
                return;
            }
            var config = creatorConfig.GetValue(__instance);
            if ((MethodBase)configOriginal.GetValue(config) != entry.Original || configSource.GetValue(config) != null)
            {
                return;
            }
            var same = entry.Expected == Text(__result.Item2);
            entry.Expected = null;
            Interlocked.Increment(ref compared);
            check.Report(same, $"replacement of {entry.Original.DeclaringType?.FullName}.{entry.Original.Name}");
            if (!same)
            {
                mismatches.Report($"{entry.Original.DeclaringType?.FullName}.{entry.Original.Name}: the replacement built at the first call differs from the one Harmony builds when the patch is applied");
            }
        }

        private static string Text(Dictionary<int, CodeInstruction> instructions) =>
            instructions == null ? "" : string.Join("\n", instructions.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));

        public static void Ensure(int id)
        {
            Pending entry;
            lock (gate)
            {
                entry = table[id];
            }
            if (!entry.Done)
            {
                Resolve(entry, true);
            }
        }

        private static void Resolve(Pending entry, bool called)
        {
            lock (locker)
            {
                if (entry.Done)
                {
                    return;
                }
                if (entry.Failure != null)
                {
                    throw entry.Failure;
                }
                var start = Stopwatch.GetTimestamp();
                var outer = resolving;
                resolving = entry;
                try
                {
                    var bytes = Stored(entry.Original);
                    var info = deserialize(bytes);
                    info.VersionCount--;
                    if (!PatchSafety.Adopt(info, entry.Live))
                    {
                        Interlocked.Increment(ref notAdopted);
                    }
                    updateWrapper(null, entry.Original, info);
                    if (!Same(bytes, Stored(entry.Original)))
                    {
                        mismatches.Report($"{entry.Original.DeclaringType?.FullName}.{entry.Original.Name}: the patch state changed while its replacement was built at the first call");
                    }
                    var stuck = lift(entry.Original);
                    if (stuck != null)
                    {
                        throw stuck;
                    }
                    entry.Done = true;
                }
                catch (Exception e)
                {
                    entry.Failure = e;
                    restore(entry.Original);
                    RLOLog.Error($"{Feature}: the replacement of {entry.Original.DeclaringType?.FullName}.{entry.Original.Name} could not be built at its first call, although the same patches were built on the last start. Calls to it fail until the game is restarted; switch off \"Build Harmony replacements at the first call\" in the mod settings if this repeats. {e}");
                    throw;
                }
                finally
                {
                    resolving = outer;
                    Interlocked.Add(ref resolveTicks, Stopwatch.GetTimestamp() - start);
                }
                if (!called)
                {
                    return;
                }
                if (MainMenu.Reached)
                {
                    resolvedLate++;
                }
                else
                {
                    resolvedEarly++;
                    lock (fired)
                    {
                        fired.Add(Key(entry.Original));
                    }
                }
            }
        }

        private static void Load()
        {
            if (!File.Exists(path))
            {
                return;
            }
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadString() != header)
                {
                    return;
                }
                var last = new Dictionary<(Guid, int), byte[]>();
                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var key = (new Guid(reader.ReadBytes(16)), reader.ReadInt32());
                    var kind = reader.ReadByte();
                    last.TryGetValue(key, out var previous);
                    if (kind == 0)
                    {
                        previous = null;
                    }
                    else if (kind == 2)
                    {
                        previous = reader.ReadBytes(reader.ReadInt32());
                    }
                    var bytes = reader.ReadBytes(reader.ReadInt32());
                    last[key] = bytes;
                    if (!proven.TryGetValue(key, out var list))
                    {
                        proven[key] = list = new List<(byte[], byte[])>();
                    }
                    list.Add((previous, bytes));
                    listed++;
                }
                count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    early.Add((new Guid(reader.ReadBytes(16)), reader.ReadInt32()));
                }
            }
        }

        private static void Save()
        {
            List<(MethodBase original, byte[] before, byte[] after)> rebuilds;
            lock (recorded)
            {
                rebuilds = recorded.ToList();
            }
            List<(Guid, int)> called;
            lock (fired)
            {
                called = early.Concat(fired).Distinct().ToList();
            }
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var temp = path + ".tmp";
                    var last = new Dictionary<(Guid, int), byte[]>();
                    using (var writer = new BinaryWriter(File.Create(temp)))
                    {
                        writer.Write(Magic);
                        writer.Write(header);
                        writer.Write(rebuilds.Count);
                        foreach (var (original, was, now) in rebuilds)
                        {
                            var key = Key(original);
                            writer.Write(key.Item1.ToByteArray());
                            writer.Write(key.Item2);
                            if (was == null)
                            {
                                writer.Write((byte)0);
                            }
                            else if (last.TryGetValue(key, out var previous) && Same(previous, was))
                            {
                                writer.Write((byte)1);
                            }
                            else
                            {
                                writer.Write((byte)2);
                                writer.Write(was.Length);
                                writer.Write(was);
                            }
                            writer.Write(now.Length);
                            writer.Write(now);
                            last[key] = now;
                        }
                        writer.Write(called.Count);
                        foreach (var key in called)
                        {
                            writer.Write(key.Item1.ToByteArray());
                            writer.Write(key.Item2);
                        }
                    }
                    File.Move(temp, path, true);
                }
                catch (Exception e)
                {
                    RLOLog.Detail($"{Feature}: could not save the list for the next start: {e.Message}");
                }
            });
        }

        public static string Stats()
        {
            int recordedCount, firedCount;
            lock (recorded)
            {
                recordedCount = recorded.Count;
            }
            lock (fired)
            {
                firedCount = fired.Count;
            }
            var text = $"{Feature}: {listed} rebuilds listed from the last start, {early.Count} methods known to be called before the main menu; {deferred} rebuilds not built at patch time for {stubs} methods ({StartupPhaseFixes.Seconds(stubTicks):F2} s for their stand-ins), "
                + $"{resolvedEarly} of those methods built at their first call before the main menu ({StartupPhaseFixes.Seconds(resolveTicks):F2} s), {superseded} built by a later patch, {notAdopted} built from the stored patch list without the patch methods of patch time; built right away as before: {calledEarly} of methods called before the main menu, "
                + $"{impure} with a transpiler or other mod code, {generic} with a generic patch method, {notProven} not seen on the last start, {notProvider} that Harmony does not install through the game, {failed} whose stand-in could not be made; {recordedCount} rebuilds and {early.Count + firedCount} early methods recorded for the next start";
            if (DevSwitches.Verify || compared > 0)
            {
                text += $"; {compared} compared with the replacement Harmony builds at patch time, {mismatches.Count} mismatches";
            }
            return text;
        }
    }
}
#endif
