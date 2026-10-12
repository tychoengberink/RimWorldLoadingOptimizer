using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Prepatcher;

namespace RimWorldLoadingOptimizer
{
    public static class LoadFromFix
    {
        private static readonly object gate = new object();
        private static Dictionary<string, Assembly> byName;
        private static bool broken;
        private static int calls, hits, fallbacks, mismatches;
        private static long ticks;

        [FreePatch]
        private static void Redirect(object module)
        {
            string status;
            try
            {
                var prepatcher = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PrepatcherImpl")?.GetType("Prepatcher.AssemblyLoadingFreePatch");
                var original = prepatcher?.GetMethod("LoadFrom", BindingFlags.Public | BindingFlags.Static);
                var reloadAll = typeof(Verse.ModAssemblyHandler).GetMethod("ReloadAll");
                if (!FasterCtors.PassOne("loadfrom"))
                {
                    status = "off";
                }
                else if (original == null || !Fingerprint.Matches("Faster mod assembly loading",
                    Fingerprint.Known(original, "A43CD2139DE39401", "92A9831495730C60"),
                    Fingerprint.Known(reloadAll, "5AE51417B2EE8201", "A7C249B93DA1940C")))
                {
                    status = "fingerprint mismatch or Prepatcher LoadFrom missing";
                }
                else
                {
                    var target = typeof(LoadFromFix).GetMethod(nameof(FastLoadFrom));
                    var replaced = 0;
                    var type = AccessTools.Method(module.GetType(), "GetType", new[] { typeof(string) }).Invoke(module, new object[] { "Verse.ModAssemblyHandler" });
                    var method = ((IEnumerable)Traverse.Create(type).Property("Methods").GetValue()).Cast<object>().First(m => Traverse.Create(m).Property("Name").GetValue<string>() == "ReloadAll");
                    var import = AccessTools.Method(module.GetType(), "ImportReference", new[] { typeof(MethodBase) });
                    foreach (var instruction in (IEnumerable)Traverse.Create(method).Property("Body").Property("Instructions").GetValue())
                    {
                        var op = Traverse.Create(instruction).Property("Operand");
                        var operand = op.GetValue();
                        if (operand != null && operand.GetType().Name.Contains("MethodReference") || operand?.GetType().Name == "MethodDefinition")
                        {
                            var reference = Traverse.Create(operand);
                            var declaring = reference.Property("DeclaringType").Property("FullName").GetValue<string>();
                            if (reference.Property("Name").GetValue<string>() == "LoadFrom" && ((IList)reference.Property("Parameters").GetValue()).Count == 1
                                && (declaring == "System.Reflection.Assembly" || declaring == "Prepatcher.AssemblyLoadingFreePatch"))
                            {
                                op.SetValue(import.Invoke(module, new object[] { target }));
                                replaced++;
                            }
                        }
                    }
                    status = $"{replaced} call(s) redirected";
                }
            }
            catch (Exception e)
            {
                status = "free patch failed: " + e;
            }
            UnityEngine.Debug.Log("[RLO] pass 1 free patch: loadfrom " + status);
        }

        public static Assembly FastLoadFrom(string filePath)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                calls++;
                var name = AssemblyName.GetAssemblyName(filePath).Name;
                var found = Find(name);
                if (found == null)
                {
                    fallbacks++;
                    return Original(filePath, name);
                }
                hits++;
                if (RLOMod.Verify)
                {
                    var expected = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                    if (!ReferenceEquals(expected, found))
                    {
                        mismatches++;
                        return Original(filePath, name);
                    }
                }
                return found;
            }
            finally
            {
                ticks += Stopwatch.GetTimestamp() - start;
            }
        }

        private static Assembly Find(string name)
        {
            lock (gate)
            {
                if (broken)
                {
                    return null;
                }
                if (byName == null)
                {
                    try
                    {
                        AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
                        MainMenu.Then(Forget);
                        var index = new Dictionary<string, Assembly>(StringComparer.Ordinal);
                        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            var assemblyName = assembly.GetName().Name;
                            if (assemblyName != null && !index.ContainsKey(assemblyName))
                            {
                                index[assemblyName] = assembly;
                            }
                        }
                        byName = index;
                    }
                    catch (Exception)
                    {
                        broken = true;
                        return null;
                    }
                }
                return byName.TryGetValue(name, out var found) && !found.ReflectionOnly ? found : null;
            }
        }

        private static void Forget()
        {
            lock (gate)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;
                byName = null;
            }
        }

        private static void OnLoad(object sender, AssemblyLoadEventArgs args)
        {
            lock (gate)
            {
                if (byName == null || broken)
                {
                    return;
                }
                try
                {
                    var name = args.LoadedAssembly.GetName().Name;
                    if (name != null && !byName.ContainsKey(name))
                    {
                        byName[name] = args.LoadedAssembly;
                    }
                }
                catch (Exception)
                {
                    broken = true;
                }
            }
        }

        private static Assembly Original(string filePath, string name)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (assembly != null)
            {
                return assembly;
            }
            var raw = File.ReadAllBytes(filePath);
            var pdb = new FileInfo(Path.Combine(Path.GetDirectoryName(filePath), Path.GetFileNameWithoutExtension(filePath)) + ".pdb");
            if (pdb.Exists)
            {
                return AppDomain.CurrentDomain.Load(raw, File.ReadAllBytes(pdb.FullName));
            }
            return AppDomain.CurrentDomain.Load(raw);
        }

        public static string Stats() => calls == 0 ? "mod assembly loading: not redirected (needs Prepatcher)" : $"mod assembly loading: {calls} calls in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms, {hits} index hits, {fallbacks} fallbacks" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
    }
}
