using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Prepatcher;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class TypesCache
    {
        private static readonly Dictionary<Assembly, Type[]> cache = new Dictionary<Assembly, Type[]>();
        private static readonly HashSet<Assembly> uncacheable = new HashSet<Assembly>();
        private static readonly object gate = new object();
        [ThreadStatic] private static bool inside;
        private static int hits, misses, mismatches, early;
        private static bool installed;

        public static void Install(Harmony harmony)
        {
            if (installed)
            {
                return;
            }
            installed = true;
            var getTypes = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetTypes), Type.EmptyTypes);
            harmony.Patch(getTypes, prefix: new HarmonyMethod(typeof(TypesCache), nameof(Prefix)));
            MainMenu.UnpatchAfterStartup(getTypes);
            LeanFixes.Reports.Add(() => $"assembly type cache: {early} calls before the mod constructors, {hits} hits, {misses} assemblies read, {uncacheable.Count} uncacheable, {mismatches} verify mismatches");
        }

        public static Type[] Get(Assembly assembly)
        {
            Interlocked.Increment(ref early);
            Type[] result = null;
            return Prefix(assembly, ref result) ? assembly.GetTypes() : result;
        }

        [FreePatch]
        private static void Redirect(object module)
        {
            string status;
            try
            {
                var allTypes = AccessTools.PropertyGetter(typeof(GenTypes), nameof(GenTypes.AllTypes));
                var usable = AccessTools.Method(typeof(ModAssemblyHandler), "AssemblyIsUsable");
                if (!FasterCtors.PassOne("earlytypes"))
                {
                    status = "off";
                }
                else if (!Fingerprint.Matches("Early assembly type cache",
                    Fingerprint.Known(allTypes, "5C5C18E824D5F949", "D271D237CCDA378F"),
                    Fingerprint.Known(usable, "AA9E7301887E8506", "F303DE8C8E0D0191")))
                {
                    status = "fingerprint mismatch";
                }
                else
                {
                    var call = module.GetType().Assembly.GetType("Mono.Cecil.Cil.OpCodes").GetField("Call").GetValue(null);
                    var import = AccessTools.Method(module.GetType(), "ImportReference", new[] { typeof(MethodBase) });
                    var target = import.Invoke(module, new object[] { typeof(TypesCache).GetMethod(nameof(Get)) });
                    var getType = AccessTools.Method(module.GetType(), "GetType", new[] { typeof(string) });
                    var replaced = 0;
                    foreach (var (typeName, methodName) in new[] { ("Verse.GenTypes", "get_AllTypes"), ("Verse.ModAssemblyHandler", "AssemblyIsUsable") })
                    {
                        var type = getType.Invoke(module, new object[] { typeName });
                        var method = ((IEnumerable)Traverse.Create(type).Property("Methods").GetValue()).Cast<object>().First(m => Traverse.Create(m).Property("Name").GetValue<string>() == methodName);
                        foreach (var instruction in (IEnumerable)Traverse.Create(method).Property("Body").Property("Instructions").GetValue())
                        {
                            var access = Traverse.Create(instruction);
                            var operand = access.Property("Operand").GetValue();
                            if (operand == null || !operand.GetType().Name.StartsWith("Method", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var reference = Traverse.Create(operand);
                            if (reference.Property("Name").GetValue<string>() == "GetTypes" && reference.Property("DeclaringType").Property("FullName").GetValue<string>() == "System.Reflection.Assembly"
                                && ((IList)reference.Property("Parameters").GetValue()).Count == 0)
                            {
                                access.Property("OpCode").SetValue(call);
                                access.Property("Operand").SetValue(target);
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
            UnityEngine.Debug.Log("[RLO] pass 1 free patch: earlytypes " + status);
        }

        public static bool Prefix(Assembly __instance, ref Type[] __result)
        {
            if (inside || __instance.IsDynamic || __instance.GetType().FullName != "System.Reflection.RuntimeAssembly")
            {
                return true;
            }
            Type[] cached;
            lock (gate)
            {
                if (uncacheable.Contains(__instance))
                {
                    return true;
                }
                cache.TryGetValue(__instance, out cached);
            }
            if (cached == null)
            {
                Type[] read;
                inside = true;
                try
                {
                    read = __instance.GetTypes();
                }
                catch
                {
                    lock (gate)
                    {
                        uncacheable.Add(__instance);
                    }
                    return true;
                }
                finally
                {
                    inside = false;
                }
                lock (gate)
                {
                    if (!cache.TryGetValue(__instance, out cached))
                    {
                        cache[__instance] = cached = read;
                        misses++;
                    }
                }
                __result = (Type[])cached.Clone();
                return false;
            }
            if (RLOMod.Verify)
            {
                inside = true;
                try
                {
                    var fresh = __instance.GetTypes();
                    var same = fresh.Length == cached.Length;
                    for (var i = 0; same && i < fresh.Length; i++)
                    {
                        same = ReferenceEquals(fresh[i], cached[i]);
                    }
                    if (!same && mismatches++ < 10)
                    {
                        Log.Warning($"[RLO] assembly type cache verify mismatch for {__instance.GetName().Name}");
                    }
                }
                finally
                {
                    inside = false;
                }
            }
            lock (gate)
            {
                hits++;
            }
            __result = (Type[])cached.Clone();
            return false;
        }
    }
}
