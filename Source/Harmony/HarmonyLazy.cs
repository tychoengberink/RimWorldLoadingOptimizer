#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

        private sealed class Pending
        {
            public int Id;
            public MethodBase Original;
            public MethodInfo Stub;
            public byte[] Previous;
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
        private static object locker;
        private static FastInvokeHandler updateWrapper, updatePatchInfo, detourMethod, validate;
        private static Func<PatchInfo, byte[]> serialize;
        private static Func<byte[], PatchInfo> deserialize;
        private static FieldInfo configOriginal, configSource, creatorConfig;
        private static MethodInfo ensure;
        private static SelfCheck check;
        private static string path, header;
        private static int listed, deferred, stubs, resolvedEarly, resolvedLate, superseded, notProven, calledEarly, impure, failed, compared;
        private static long stubTicks, resolveTicks;

        [ThreadStatic] private static Pending resolving;
        [ThreadStatic] private static Dry dry;

        public static void Install(Harmony harmony)
        {
            var asm = typeof(Harmony).Assembly;
            Type T(string name) => asm.GetType(name);
            var configType = T("HarmonyLib.MethodCreatorConfig");
            var creatorType = T("HarmonyLib.MethodCreator");
            var shared = T("HarmonyLib.HarmonySharedState");
            var serialization = T("HarmonyLib.PatchInfoSerialization");
            var wrapper = AccessTools.Method(T("HarmonyLib.PatchFunctions"), "UpdateWrapper");
            var create = creatorType == null ? null : AccessTools.Method(creatorType, "CreateReplacement");
            var prepare = configType == null ? null : AccessTools.Method(configType, "Prepare");
            var getPatchInfo = shared == null ? null : AccessTools.Method(shared, "GetPatchInfo", new[] { typeof(MethodBase) });
            var update = shared == null ? null : AccessTools.Method(shared, "UpdatePatchInfo");
            var detour = AccessTools.Method(T("HarmonyLib.PatchTools"), "DetourMethod");
            var validateMethod = AccessTools.Method(typeof(PatchInfo), "ValidateSurvivingMetadata");
            var serializeMethod = serialization == null ? null : AccessTools.Method(serialization, "SerializeValidated");
            var deserializeMethod = serialization == null ? null : AccessTools.Method(serialization, "Deserialize", new[] { typeof(byte[]) });
            var hooks = AccessTools.Method(T("HarmonyLib.PersistentStateHooks"), "Prepare");
            locker = AccessTools.Field(typeof(PatchProcessor), "locker")?.GetValue(null);
            state = shared == null ? null : AccessTools.Field(shared, "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
            configOriginal = configType == null ? null : AccessTools.Field(configType, "original");
            configSource = configType == null ? null : AccessTools.Field(configType, "source");
            creatorConfig = creatorType == null ? null : AccessTools.Field(creatorType, "config");
            ensure = AccessTools.Method(typeof(HarmonyLazy), nameof(Ensure));
            if (new object[] { wrapper, create, prepare, getPatchInfo, update, detour, validateMethod, serializeMethod, deserializeMethod, hooks, locker, state, configOriginal, configSource, creatorConfig }.Any(x => x == null)
                || create.ReturnType != typeof((MethodInfo, Dictionary<int, CodeInstruction>)) || deserializeMethod.ReturnType != typeof(PatchInfo) || serializeMethod.ReturnType != typeof(byte[]))
            {
                VersionCheck.Skip(Feature, "a Harmony method it needs no longer exists");
                return;
            }
            if (!VersionCheck.Pattern(Feature, wrapper, code => VersionCheck.CallsExactly(code, Coalesce.WrapperCalls), "makes exactly the calls this speedup was built around")
                || !VersionCheck.Pattern(Feature, hooks, code => VersionCheck.CallsExactly(code, "AppDomain.GetData,AppDomain.get_CurrentDomain,PersistentStateHooks..ctor"), "makes exactly the calls this speedup was built around"))
            {
                return;
            }
            if (!Coalesce.InitSafety())
            {
                VersionCheck.Skip(Feature, "a Harmony method it needs no longer exists");
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(wrapper, create, prepare, getPatchInfo, update, detour, validateMethod, serializeMethod, deserializeMethod, hooks));
            if (check.IsOff)
            {
                return;
            }
            updateWrapper = HarmonyLib.MethodInvoker.GetHandler(wrapper);
            updatePatchInfo = HarmonyLib.MethodInvoker.GetHandler(update);
            detourMethod = HarmonyLib.MethodInvoker.GetHandler(detour);
            validate = HarmonyLib.MethodInvoker.GetHandler(validateMethod);
            serialize = AccessTools.MethodDelegate<Func<PatchInfo, byte[]>>(serializeMethod);
            deserialize = AccessTools.MethodDelegate<Func<byte[], PatchInfo>>(deserializeMethod);
            header = $"{asm.ManifestModule.ModuleVersionId}|{Environment.Version}";
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
            StartupPhaseFixes.Reports.Add(Stats);
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
                    && (method.CallingConvention & CallingConventions.VarArgs) == 0 && !assembly.IsDynamic
                    && assembly != typeof(HarmonyLazy).Assembly && !Coalesce.TrustedAssembly(assembly)
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

        public static bool WrapperPrefix(MethodBase original, PatchInfo patchInfo, ref MethodInfo __result, out byte[] __state)
        {
            __state = null;
            if (dry != null)
            {
                return true;
            }
            var before = Stored(original);
            __state = before;
            if (resolving != null || MainMenu.Reached || Harmony.DEBUG || check.IsOff || proven.Count == 0 || !Eligible(original))
            {
                return true;
            }
            var key = Key(original);
            if (!proven.TryGetValue(key, out var list))
            {
                Interlocked.Increment(ref notProven);
                return true;
            }
            if (early.Contains(key))
            {
                Interlocked.Increment(ref calledEarly);
                return true;
            }
            if (AppDomain.CurrentDomain.GetData("Harmony.Infix.PersistentState.v1") != null || !Coalesce.Pure(original, patchInfo))
            {
                Interlocked.Increment(ref impure);
                return true;
            }
            byte[] bytes;
            try
            {
                validate(patchInfo);
            }
            catch (Exception)
            {
                return true;
            }
            patchInfo.VersionCount++;
            try
            {
                bytes = serialize(patchInfo);
            }
            catch (Exception)
            {
                patchInfo.VersionCount--;
                return true;
            }
            if (!list.Any(pair => Same(pair.previous, before) && Same(pair.now, bytes)))
            {
                patchInfo.VersionCount--;
                Interlocked.Increment(ref notProven);
                return true;
            }
            string expected = null;
            if (DevSwitches.Verify || check.IsChecking)
            {
                var holder = new Dry();
                dry = holder;
                try
                {
                    var copy = deserialize(bytes);
                    copy.VersionCount--;
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
                    patchInfo.VersionCount--;
                    mismatches.Report($"{original.DeclaringType?.FullName}.{original.Name}: a replacement that was built on the last start could not be built now");
                    check.Report(false, $"replacement of {original.DeclaringType?.FullName}.{original.Name}");
                    return true;
                }
            }
            Pending entry;
            var start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (!pending.TryGetValue(original, out entry) || entry.Done)
                {
                    entry = new Pending { Id = table.Count, Original = original };
                    try
                    {
                        entry.Stub = MakeStub(entry);
                        table.Add(entry);
                        detourMethod(null, original, entry.Stub);
                    }
                    catch (Exception)
                    {
                        entry.Done = true;
                        patchInfo.VersionCount--;
                        Interlocked.Increment(ref failed);
                        return true;
                    }
                    pending[original] = entry;
                    stubs++;
                }
                entry.Previous = before;
                entry.Expected = expected;
                updatePatchInfo(null, original, entry.Stub, bytes);
            }
            Interlocked.Add(ref stubTicks, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref deferred);
            lock (recorded)
            {
                recorded.Add((original, before, bytes));
            }
            if (expected != null)
            {
                Resolve(entry, false);
            }
            __result = entry.Stub;
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
            else
            {
                lock (gate)
                {
                    if (pending.TryGetValue(original, out var entry) && !entry.Done)
                    {
                        entry.Done = true;
                        superseded++;
                    }
                }
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

        private static MethodInfo MakeStub(Pending entry)
        {
            var original = (MethodInfo)entry.Original;
            var parameters = new List<Type>();
            if (!original.IsStatic)
            {
                parameters.Add(original.DeclaringType.IsValueType ? original.DeclaringType.MakeByRefType() : original.DeclaringType);
            }
            parameters.AddRange(original.GetParameters().Select(p => p.ParameterType));
            var stub = new DynamicMethod($"{original.DeclaringType.FullName}.{original.Name}_Lazy", original.ReturnType, parameters.ToArray(), original.DeclaringType, true);
            var il = stub.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, entry.Id);
            il.Emit(OpCodes.Call, ensure);
            for (var i = 0; i < parameters.Count; i++)
            {
                il.Emit(OpCodes.Ldarg, (short)i);
            }
            il.Emit(OpCodes.Call, original);
            il.Emit(OpCodes.Ret);
            return stub;
        }

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
                    updateWrapper(null, entry.Original, info);
                    if (!Same(bytes, Stored(entry.Original)))
                    {
                        mismatches.Report($"{entry.Original.DeclaringType?.FullName}.{entry.Original.Name}: the patch state changed while its replacement was built at the first call");
                    }
                    entry.Done = true;
                }
                catch (Exception e)
                {
                    entry.Failure = e;
                    RLOLog.Error($"{Feature}: the replacement of {entry.Original.DeclaringType?.FullName}.{entry.Original.Name} could not be built at its first call, although the same patches were built on the last start. Calls to it fail until the game is restarted; switch off \"Harmony rebuild caches\" in the mod settings if this repeats. {e}");
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
                + $"{resolvedEarly} of those methods built at their first call before the main menu ({StartupPhaseFixes.Seconds(resolveTicks):F2} s), {superseded} built by a later patch; built right away as before: {calledEarly} of methods called before the main menu, "
                + $"{impure} with a transpiler or other mod code, {notProven} not seen on the last start, {failed} whose stand-in could not be made; {recordedCount} rebuilds and {early.Count + firedCount} early methods recorded for the next start";
            if (DevSwitches.Verify || compared > 0)
            {
                text += $"; {compared} compared with the replacement Harmony builds at patch time, {mismatches.Count} mismatches";
            }
            return text;
        }
    }
}
#endif
