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
        private static HashSet<string> loaded;

        public static readonly List<string> Skipped = new List<string>();

        public static bool CanScan(Assembly assembly)
        {
            lock (verdicts)
            {
                if (verdicts.TryGetValue(assembly, out var ok))
                {
                    return ok;
                }
                if (loaded == null)
                {
                    loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name), StringComparer.OrdinalIgnoreCase);
                }
                string missing = null;
                try
                {
                    missing = assembly.GetReferencedAssemblies().Select(r => r.Name).FirstOrDefault(n => !loaded.Contains(n));
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

        public static bool CanScan(Type type) => type != null && CanScan(type.Assembly);

        public static string Stats() => Skipped.Count == 0 ? "assemblies with missing references: none" : $"assemblies not scanned because they reference an assembly that is not loaded: {string.Join(", ", Skipped)}";
    }
}
