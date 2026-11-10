using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AssemblySafety
    {
        private static readonly Dictionary<Assembly, bool> verdicts = new Dictionary<Assembly, bool>();
#if !RW17
        private static HashSet<string> loaded;
#else
        private static readonly Dictionary<string, bool> available = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static List<(string pattern, string target)> redirects;
#endif

        public static readonly List<string> Skipped = new List<string>();

        public static bool CanScan(Assembly assembly)
        {
            lock (verdicts)
            {
                if (verdicts.TryGetValue(assembly, out var ok))
                {
                    return ok;
                }
#if !RW17
                if (loaded == null)
                {
                    loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name), StringComparer.OrdinalIgnoreCase);
                }
#endif
                string missing = null;
                try
                {
#if RW17
                    missing = assembly.GetReferencedAssemblies().FirstOrDefault(r => !Available(r))?.Name;
#else
                    missing = assembly.GetReferencedAssemblies().Select(r => r.Name).FirstOrDefault(n => !loaded.Contains(n));
#endif
                }
                catch (Exception e)
                {
                    missing = e.GetType().Name;
                }
                ok = missing == null;
                verdicts[assembly] = ok;
                if (!ok)
                {
                    Skipped.Add($"{assembly.GetName().Name} (needs {missing})");
                }
                return ok;
            }
        }

#if RW17
        private static bool Available(AssemblyName reference)
        {
            var name = Redirected(reference.Name);
            if (!available.TryGetValue(name, out var result))
            {
                result = AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) || CanLoad(reference);
                available[name] = result;
            }
            return result;
        }

        private static bool CanLoad(AssemblyName reference)
        {
            try
            {
                Assembly.Load(reference);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
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
#endif

        public static bool CanScan(Type type) => type != null && CanScan(type.Assembly);

        public static string Stats() => Skipped.Count == 0 ? "assemblies with missing references: none" : $"assemblies not scanned because they reference an assembly that is not loaded: {string.Join(", ", Skipped)}";
    }
}
