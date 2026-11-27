#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class TypeTokenCache
    {
        private const string Feature = "Harmony type token cache";

        private static readonly Dictionary<(Module, Type), int> tokens = new Dictionary<(Module, Type), int>();
        private static readonly Dictionary<(Module, IntPtr), int> methods = new Dictionary<(Module, IntPtr), int>();
        private static readonly HashSet<(Module, IntPtr)> waiting = new HashSet<(Module, IntPtr)>();
        private static readonly List<(Module into, Module from, int token)> asked = new List<(Module, Module, int)>();
        private static readonly MismatchLog mismatches = new MismatchLog(Feature);
        private static SelfCheck check;
        private static bool methodsOn;
        private static long hits, misses, again, methodHits, methodMisses, made, used, listed, spent, methodSpent, aheadTicks;
        [ThreadStatic] private static bool ahead;
        [ThreadStatic] private static long started;
        [ThreadStatic] private static long methodStarted;

        private static string ListPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "harmony-tokens.txt");

        public static void Install(Harmony harmony)
        {
            var updater = typeof(object).Assembly.GetType("WaylithClr.MethodBodyUpdater");
            var target = updater == null ? null : AccessTools.Method(updater, "GetNamedTypeToken");
            var parameters = target?.GetParameters();
            if (target == null || !target.IsStatic || target.ReturnType != typeof(int) || parameters.Length != 2
                || !typeof(Module).IsAssignableFrom(parameters[0].ParameterType) || !typeof(Type).IsAssignableFrom(parameters[1].ParameterType))
            {
                VersionCheck.Skip(Feature, "the runtime's GetNamedTypeToken no longer exists in this form");
                return;
            }
            var methodTarget = AccessTools.Method(updater, "GetMethodToken");
            var methodParameters = methodTarget?.GetParameters();
            var typeFor = AccessTools.Method(updater, "GetTokenFor", new[] { typeof(Module), typeof(Type) });
            var methodFor = AccessTools.Method(updater, "GetTokenFor", new[] { typeof(Module), typeof(MethodBase) });
            methodsOn = methodTarget != null && methodTarget.IsStatic && methodTarget.ReturnType == typeof(int) && methodParameters.Length == 4
                && typeof(Module).IsAssignableFrom(methodParameters[0].ParameterType) && methodParameters[1].ParameterType == typeof(MethodBase)
                && methodParameters[2].ParameterType == typeof(Type[]) && methodParameters[3].ParameterType == typeof(bool)
                && typeFor != null && typeFor.IsStatic && typeFor.ReturnType == typeof(int)
                && methodFor != null && methodFor.IsStatic && methodFor.ReturnType == typeof(int);
            check = new SelfCheck(Feature, methodsOn ? VersionCheck.Code(target, methodTarget, typeFor, methodFor) : VersionCheck.Code(target));
            if (check.IsOff)
            {
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(TypeTokenCache), nameof(Prefix)), postfix: new HarmonyMethod(typeof(TypeTokenCache), nameof(Postfix)));
            StartupPhaseFixes.Reports.Add(Stats);
            if (!methodsOn)
            {
                RLOLog.Detail($"{Feature}: the runtime's GetMethodToken no longer exists in this form. Method tokens are not remembered and nothing is made ahead; type tokens are remembered as before.");
                return;
            }
            harmony.Patch(methodTarget, prefix: new HarmonyMethod(typeof(TypeTokenCache), nameof(MethodPrefix)), postfix: new HarmonyMethod(typeof(TypeTokenCache), nameof(MethodPostfix)));
            var makeType = (Func<Module, Type, int>)Delegate.CreateDelegate(typeof(Func<Module, Type, int>), typeFor);
            var makeMethod = (Func<Module, MethodBase, int>)Delegate.CreateDelegate(typeof(Func<Module, MethodBase, int>), methodFor);
            new Thread(() => Ahead(makeType, makeMethod)) { IsBackground = true, Name = "RLO Harmony tokens" }.Start();
            MainMenu.Then(Save);
        }

        private static void Ahead(Func<Module, Type, int> makeType, Func<Module, MethodBase, int> makeMethod)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                if (!File.Exists(ListPath))
                {
                    return;
                }
                ahead = true;
                var byName = new Dictionary<string, Module>(StringComparer.Ordinal);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic || assembly.IsCollectible)
                    {
                        continue;
                    }
                    var name = assembly.GetName().Name;
                    byName[name] = byName.ContainsKey(name) ? null : assembly.ManifestModule;
                }
                var versions = new Dictionary<Module, string>();
                foreach (var line in File.ReadLines(ListPath))
                {
                    if (check.IsOff)
                    {
                        return;
                    }
                    var parts = line.Split('\t');
                    if (parts.Length != 4 || !byName.TryGetValue(parts[0], out var into) || into == null || !byName.TryGetValue(parts[1], out var from) || from == null
                        || !int.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var token))
                    {
                        continue;
                    }
                    if (!versions.TryGetValue(from, out var version))
                    {
                        versions[from] = version = from.ModuleVersionId.ToString("N");
                    }
                    if (version != parts[2])
                    {
                        continue;
                    }
                    Interlocked.Increment(ref listed);
                    try
                    {
                        if (token >> 24 == 0x02)
                        {
                            makeType(into, from.ResolveType(token));
                        }
                        else if (from.ResolveMethod(token) is MethodBase method && Plain(method))
                        {
                            makeMethod(into, method);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception e)
            {
                RLOLog.Detail($"{Feature}: making last start's tokens ahead stopped ({e.GetType().Name}: {e.Message}). The remaining tokens are made when Harmony asks for them; nothing is broken.");
            }
            finally
            {
                Interlocked.Exchange(ref aheadTicks, Stopwatch.GetTimestamp() - start);
            }
        }

        private static void Save()
        {
            (Module into, Module from, int token)[] entries;
            lock (tokens)
            {
                entries = asked.ToArray();
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var names = new Dictionary<Module, string>();
                    var versions = new Dictionary<Module, string>();
                    string Name(Module module) => names.TryGetValue(module, out var name) ? name : names[module] = module.Assembly.GetName().Name;
                    string Version(Module module) => versions.TryGetValue(module, out var version) ? version : versions[module] = module.ModuleVersionId.ToString("N");
                    Directory.CreateDirectory(Path.GetDirectoryName(ListPath));
                    File.WriteAllLines(ListPath, entries.Select(e => Name(e.into) + "\t" + Name(e.from) + "\t" + Version(e.from) + "\t" + e.token.ToString("X8")));
                }
                catch (Exception e)
                {
                    RLOLog.Detail($"{Feature}: could not save the token list for the next start: {e.Message}");
                }
            });
        }

        public static bool Prefix(Module module, Type type, ref int __result, out bool __state)
        {
            __state = true;
            if (check.IsOff || type.Assembly.IsCollectible || module.Assembly.IsCollectible)
            {
                __state = false;
                return true;
            }
            lock (tokens)
            {
                if (!tokens.TryGetValue((module, type), out var token))
                {
                    started = Stopwatch.GetTimestamp();
                    return true;
                }
                if (ahead)
                {
                    __result = token;
                    __state = false;
                    return false;
                }
                hits++;
                if (waiting.Count != 0 && waiting.Remove((module, type.TypeHandle.Value)))
                {
                    asked.Add((module, type.Module, type.MetadataToken));
                    used++;
                }
                if (DevSwitches.Verify || check.IsChecking)
                {
                    return true;
                }
                __result = token;
            }
            __state = false;
            return false;
        }

        public static void Postfix(Module module, Type type, int __result, bool __state)
        {
            if (!__state)
            {
                return;
            }
            lock (tokens)
            {
                if (!tokens.TryGetValue((module, type), out var known))
                {
                    tokens[(module, type)] = __result;
                    if (ahead)
                    {
                        waiting.Add((module, type.TypeHandle.Value));
                        made++;
                    }
                    else
                    {
                        if (methodsOn)
                        {
                            asked.Add((module, type.Module, type.MetadataToken));
                        }
                        misses++;
                        spent += Stopwatch.GetTimestamp() - started;
                    }
                    return;
                }
                if (known != __result)
                {
                    again++;
                }
                var same = known == __result || Denotes(module, known, type) && Denotes(module, __result, type);
                check.Report(same, $"{type.FullName} in {module.Name}");
                if (!same)
                {
                    mismatches.Report($"{type.FullName} in {module.Name}: token {known:X8} from the cache, {__result:X8} from the runtime");
                    tokens[(module, type)] = __result;
                }
            }
        }

        public static bool MethodPrefix(Module module, MethodBase member, Type[] optionalParameterTypes, ref int __result, out IntPtr __state)
        {
            __state = IntPtr.Zero;
            if (check.IsOff || optionalParameterTypes.Length != 0 || !Plain(member) || module.Assembly.IsCollectible)
            {
                return true;
            }
            var handle = member.MethodHandle.Value;
            lock (tokens)
            {
                if (!methods.TryGetValue((module, handle), out var token))
                {
                    __state = handle;
                    methodStarted = Stopwatch.GetTimestamp();
                    return true;
                }
                if (ahead)
                {
                    __result = token;
                    return false;
                }
                methodHits++;
                if (waiting.Count != 0 && waiting.Remove((module, handle)))
                {
                    asked.Add((module, member.Module, member.MetadataToken));
                    used++;
                }
                if (DevSwitches.Verify || check.IsChecking)
                {
                    __state = handle;
                    return true;
                }
                __result = token;
            }
            return false;
        }

        public static void MethodPostfix(Module module, MethodBase member, int __result, IntPtr __state)
        {
            if (__state == IntPtr.Zero)
            {
                return;
            }
            lock (tokens)
            {
                if (!methods.TryGetValue((module, __state), out var known))
                {
                    methods[(module, __state)] = __result;
                    if (ahead)
                    {
                        waiting.Add((module, __state));
                        made++;
                    }
                    else
                    {
                        asked.Add((module, member.Module, member.MetadataToken));
                        methodMisses++;
                        methodSpent += Stopwatch.GetTimestamp() - methodStarted;
                    }
                    return;
                }
                if (known != __result)
                {
                    again++;
                }
                var same = known == __result || Denotes(module, known, member) && Denotes(module, __result, member);
                check.Report(same, $"{member.DeclaringType.FullName}.{member.Name} in {module.Name}");
                if (!same)
                {
                    mismatches.Report($"{member.DeclaringType.FullName}.{member.Name} in {module.Name}: token {known:X8} from the cache, {__result:X8} from the runtime");
                    methods[(module, __state)] = __result;
                }
            }
        }

        private static bool Plain(MethodBase member) =>
            !(member is DynamicMethod) && !member.IsGenericMethod && member.DeclaringType is Type type && !type.IsGenericType && !type.IsArray && !type.Assembly.IsCollectible;

        private static bool Denotes(Module module, int token, Type type)
        {
            try
            {
                return module.ResolveType(token) == type;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Denotes(Module module, int token, MethodBase member)
        {
            try
            {
                return module.ResolveMethod(token).MethodHandle.Value == member.MethodHandle.Value;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Stats()
        {
            lock (tokens)
            {
                return $"{Feature}: {hits} type tokens and {methodHits} method tokens answered from the cache; the runtime made {misses} type tokens ({StartupPhaseFixes.Seconds(spent):F2} s) and {methodMisses} method tokens ({StartupPhaseFixes.Seconds(methodSpent):F2} s, their class's type token included) when Harmony asked; {made} tokens made ahead on a worker thread from the {Interlocked.Read(ref listed)} entries of the last start's list ({StartupPhaseFixes.Seconds(Interlocked.Read(ref aheadTicks)):F2} s), {used} of them asked for so far; while checking, the runtime made a second token for the same type or method {again} times, {mismatches}";
            }
        }
    }
}
#endif
