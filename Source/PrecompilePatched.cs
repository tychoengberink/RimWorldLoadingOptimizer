#if RW17
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class PrecompilePatched
    {
        private static readonly ConcurrentDictionary<MethodBase, bool> preparedMethods = new ConcurrentDictionary<MethodBase, bool>();
        private static readonly Dictionary<string, int> skipReasons = new Dictionary<string, int>();
        private static readonly Dictionary<Assembly, string> verdicts = new Dictionary<Assembly, string>();
        private static readonly ConcurrentDictionary<string, int> missingReferences = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> moduleInitializers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<Assembly>> loaded;
        private static HashSet<string> platform;
        private static List<(string pattern, string target)> redirects;
        private static string path;
        private static volatile string state = "not started";
        private static int listed, notFound, skipped, prepared, failed, workers, cursor = -1, stopped;
        private static long ticks, jitTicks;

        public static void Install()
        {
            path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "patched-methods.txt");
            MainMenu.Then(Save);
            LeanFixes.Reports.Add(Stats);
            if (!File.Exists(path))
            {
                state = "no list yet; it is written at the main menu and used from the next start";
                return;
            }
            state = "running";
            new Thread(() => Run(path)) { IsBackground = true, Name = "RLO pre-compile" }.Start();
        }

        public static void Run(string file)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var methods = Resolve(File.ReadAllLines(file));
                workers = Math.Max(1, Environment.ProcessorCount - 1);
                var threads = new List<Thread>();
                for (var i = 0; i < workers; i++)
                {
                    var thread = new Thread(() => Work(methods)) { IsBackground = true, Name = "RLO pre-compile " + i };
                    threads.Add(thread);
                    thread.Start();
                }
                foreach (var thread in threads)
                {
                    thread.Join();
                }
                state = stopped > 0 ? "stopped at the main menu" : "done";
            }
            catch (Exception e)
            {
                state = "skipped after an error: " + e.Message;
            }
            ticks = Stopwatch.GetTimestamp() - start;
        }

        private static void Work(List<MethodBase> methods)
        {
            var jitStart = System.Runtime.JitInfo.GetCompilationTime(true);
            while (true)
            {
                if (MainMenu.Reached)
                {
                    Interlocked.Exchange(ref stopped, 1);
                    break;
                }
                var i = Interlocked.Increment(ref cursor);
                if (i >= methods.Count)
                {
                    break;
                }
                try
                {
                    RuntimeHelpers.PrepareMethod(methods[i].MethodHandle);
                    Interlocked.Increment(ref prepared);
                    if (RLOMod.Verify)
                    {
                        preparedMethods[methods[i]] = true;
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
            }
            Interlocked.Add(ref jitTicks, (System.Runtime.JitInfo.GetCompilationTime(true) - jitStart).Ticks);
        }

        private static List<MethodBase> Resolve(string[] lines)
        {
            loaded = new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }
                var name = assembly.GetName().Name;
                if (!loaded.TryGetValue(name, out var list))
                {
                    loaded[name] = list = new List<Assembly>();
                }
                list.Add(assembly);
            }
            platform = new HashSet<string>(((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { Path.GetDirectoryName(typeof(object).Assembly.Location), AppContext.BaseDirectory }.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).Distinct())
            {
                platform.UnionWith(Directory.GetFiles(folder, "*.dll").Select(Path.GetFileNameWithoutExtension));
            }
            var result = new List<MethodBase>();
            var seen = new HashSet<MethodBase>();
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length != 5 || !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var token))
                {
                    continue;
                }
                listed++;
                var method = Find(parts, token);
                if (method == null)
                {
                    notFound++;
                    continue;
                }
                var reason = Unsafe(method);
                if (reason != null)
                {
                    skipped++;
                    skipReasons[reason] = skipReasons.TryGetValue(reason, out var n) ? n + 1 : 1;
                    continue;
                }
                if (seen.Add(method))
                {
                    result.Add(method);
                }
            }
            return result;
        }

        private static MethodBase Find(string[] parts, int token)
        {
            if (!loaded.TryGetValue(parts[0], out var candidates))
            {
                return null;
            }
            foreach (var assembly in candidates)
            {
                foreach (var module in assembly.GetModules())
                {
                    MethodBase method;
                    try
                    {
                        method = module.ResolveMethod(token);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (method?.DeclaringType?.FullName == parts[2] && method.Name == parts[3] && method.ToString() == parts[4])
                    {
                        return method;
                    }
                }
            }
            return null;
        }

        private static string Unsafe(MethodBase method)
        {
            if (method.IsAbstract)
            {
                return "abstract";
            }
            if (method.IsGenericMethod || method.ContainsGenericParameters || method.DeclaringType.IsGenericType)
            {
                return "generic";
            }
            if (method.IsStatic && method.IsConstructor)
            {
                return "static constructor";
            }
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 || (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.CodeTypeMask)) != 0)
            {
                return "no IL body";
            }
            return Verdict(method.Module.Assembly);
        }

        private static string Verdict(Assembly assembly)
        {
            if (verdicts.TryGetValue(assembly, out var reason))
            {
                return reason;
            }
            verdicts[assembly] = null;
            reason = Check(assembly);
            verdicts[assembly] = reason;
            return reason;
        }

        private static string Check(Assembly assembly)
        {
            if (assembly.IsCollectible || string.IsNullOrEmpty(assembly.Location))
            {
                return "assembly not loaded from a file";
            }
            try
            {
                using (var stream = File.OpenRead(assembly.Location))
                using (var pe = new PEReader(stream))
                {
                    if (pe.PEHeaders.CorHeader == null || pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size > 0)
                    {
                        return "precompiled assembly";
                    }
                    moduleInitializers[assembly.Location] = HasModuleInitializer(pe.GetMetadataReader());
                }
                if (moduleInitializers[assembly.Location])
                {
                    return "assembly has a module initializer";
                }
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    var name = Redirected(reference.Name);
                    if (platform.Contains(name))
                    {
                        continue;
                    }
                    if (!loaded.TryGetValue(name, out var targets))
                    {
                        if (platform.Contains(name))
                        {
                            continue;
                        }
                        missingReferences.AddOrUpdate(name, 1, (_, n) => n + 1);
                        return "references an assembly that is not loaded";
                    }
                    foreach (var target in targets)
                    {
                        if (target != assembly && Verdict(target) is string inner && inner != "precompiled assembly")
                        {
                            return inner == "references an assembly that is not loaded" ? inner : "references an assembly that may run initialization code";
                        }
                    }
                }
                return null;
            }
            catch (Exception e)
            {
                return "assembly could not be inspected (" + e.GetType().Name + ")";
            }
        }

        private static bool HasModuleInitializer(MetadataReader metadata)
        {
            if (metadata.TypeDefinitions.Count == 0)
            {
                return false;
            }
            var global = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1));
            return global.GetMethods().Any(handle => metadata.StringComparer.Equals(metadata.GetMethodDefinition(handle).Name, ".cctor"));
        }

        private static string Redirected(string name)
        {
            redirects ??= (AppContext.GetData("WaylithClr.AssemblyNameRedirects") as string ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('='))
                .Where(pair => pair.Length == 2)
                .Select(pair => (pattern: pair[0].Trim(), target: pair[1].Trim()))
                .ToList();
            foreach (var (pattern, target) in redirects)
            {
                var match = pattern.EndsWith("*", StringComparison.Ordinal)
                    ? name.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase)
                    : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    return target.Length == 0 ? name : target;
                }
            }
            return name;
        }

        public static string Line(MethodBase method)
        {
            var type = method?.DeclaringType;
            if (type == null || method.IsAbstract || method.IsGenericMethod || type.IsGenericType || method.Module.Assembly.IsDynamic)
            {
                return null;
            }
            return $"{method.Module.Assembly.GetName().Name}\t{method.MetadataToken:X8}\t{type.FullName}\t{method.Name}\t{method}";
        }

        private static void Save()
        {
            var methods = Harmony.GetAllPatchedMethods().ToList();
            Task.Run(() =>
            {
                try
                {
                    var lines = methods.Select(Line).Where(line => line != null && line.Split('\t').Length == 5).Distinct().ToList();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var temp = path + ".tmp";
                    File.WriteAllLines(temp, lines);
                    File.Move(temp, path, true);
                    if (RLOMod.Verify)
                    {
                        var patched = methods.Count(preparedMethods.ContainsKey);
                        Log.Message($"[RLO] pre-compile patched methods verify: {patched} of {preparedMethods.Count} prepared methods were patched this start, {lines.Count} methods listed for the next start, no errors reached game code");
                    }
                }
                catch (Exception e)
                {
                    Log.Message($"[RLO] pre-compile patched methods: could not save the list for the next start: {e.Message}");
                }
            });
        }

        public static string Stats()
        {
            var reasons = state == "running" || skipReasons.Count == 0 ? "" : " (" + string.Join(", ", skipReasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}")) + ")";
            var missing = missingReferences.IsEmpty ? "" : "; references not loaded: " + string.Join(", ", missingReferences.OrderByDescending(r => r.Value).Take(8).Select(r => $"{r.Key} ({r.Value})"));
            return $"pre-compile patched methods: {state}; listed {listed}, prepared {prepared}, skipped {skipped}{reasons}, not loaded or changed {notFound}, failed {failed}; {workers} threads, {LeanFixes.Seconds(ticks):F2} s wall, {TimeSpan.FromTicks(jitTicks).TotalSeconds:F2} s JIT on those threads{missing}";
        }
    }
}
#endif
