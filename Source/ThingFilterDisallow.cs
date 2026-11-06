using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ThingFilterDisallow
    {
        public const string Feature = "Thing filter removal loops";

        private static readonly int[] RemovalLoops = { 1, 3, 4, 5, 6, 7, 8, 10 };
        private static MethodInfo allDefs;
        private static AccessTools.FieldRef<ThingFilter, HashSet<ThingDef>> allowed;
        private static MethodBase[] guarded;
        private static int state;
        private static long narrowed, full, scanned;

        public static void Install(Harmony harmony)
        {
            if (System.Environment.GetEnvironmentVariable("RLO_FILTER_DIGEST") == "1")
            {
                MainMenu.Then(() => Log.Message("[RLO] thing filter digest: " + Digest()));
            }
            if (System.Environment.GetEnvironmentVariable("RLO_NO_FILTER_LOOPS") == "1")
            {
                return;
            }
            var resolve = AccessTools.Method(typeof(ThingFilter), nameof(ThingFilter.ResolveReferences));
            allDefs = AccessTools.PropertyGetter(typeof(DefDatabase<ThingDef>), nameof(DefDatabase<ThingDef>.AllDefsListForReading));
            var field = AccessTools.Field(typeof(ThingFilter), "allowedDefs");
            if (resolve == null || allDefs == null || field == null || field.FieldType != typeof(HashSet<ThingDef>))
            {
                VersionCheck.Skip(Feature, "a method or field it needs no longer exists");
                return;
            }
            if (!VersionCheck.Pattern(Feature, resolve, code => code.Count(i => i.Calls(allDefs)) == 11, "walks every ThingDef in exactly eleven places, eight of them only to disallow"))
            {
                return;
            }
            allowed = AccessTools.FieldRefAccess<ThingFilter, HashSet<ThingDef>>(field);
            guarded = new MethodBase[]
            {
                resolve,
                AccessTools.Method(typeof(ThingFilter), nameof(ThingFilter.Allows), new[] { typeof(ThingDef) }),
                AccessTools.Method(typeof(ThingFilter), nameof(ThingFilter.SetAllow), new[] { typeof(ThingDef), typeof(bool) }),
            };
            harmony.Patch(resolve, transpiler: new HarmonyMethod(typeof(ThingFilterDisallow), nameof(Transpiler)));
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var seen = 0;
            var narrow = AccessTools.Method(typeof(ThingFilterDisallow), nameof(Candidates));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(allDefs) && RemovalLoops.Contains(seen++))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                    yield return new CodeInstruction(OpCodes.Call, narrow);
                    continue;
                }
                yield return instruction;
            }
        }

        private static bool Usable()
        {
            var current = Volatile.Read(ref state);
            if (current == 0)
            {
                current = guarded.All(Fingerprint.OnlyOurs) ? 1 : 2;
                Volatile.Write(ref state, current);
                if (current == 2)
                {
                    var owners = guarded.SelectMany(m => Harmony.GetPatchInfo(m)?.Owners ?? Enumerable.Empty<string>()).Where(o => o != RLOMod.HarmonyId).Distinct();
                    Log.Message("[RLO] " + Feature + ": another mod patches ThingFilter (" + string.Join(", ", owners) + "), the original loops run");
                }
            }
            return current == 1;
        }

        public static List<ThingDef> Candidates(ThingFilter filter)
        {
            var all = DefDatabase<ThingDef>.AllDefsListForReading;
            if (!Usable())
            {
                return all;
            }
            var set = allowed(filter);
            var result = new List<ThingDef>(set.Count);
            foreach (var def in set)
            {
                if (def == null || def.index >= all.Count || all[def.index] != def)
                {
                    Interlocked.Increment(ref full);
                    return all;
                }
                result.Add(def);
            }
            result.Sort((a, b) => a.index.CompareTo(b.index));
            Interlocked.Increment(ref narrowed);
            Interlocked.Add(ref scanned, all.Count - result.Count);
            return result;
        }

        private static string Digest()
        {
            var filters = new List<(string, ThingFilter)>();
            foreach (var recipe in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                filters.Add(("fixed " + recipe.defName, recipe.fixedIngredientFilter));
                filters.Add(("default " + recipe.defName, recipe.defaultIngredientFilter));
            }
            foreach (var thing in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                filters.Add(("fixedStorage " + thing.defName, thing.building?.fixedStorageSettings?.filter));
                filters.Add(("defaultStorage " + thing.defName, thing.building?.defaultStorageSettings?.filter));
            }
            var hash = 17L;
            var count = 0;
            foreach (var (name, filter) in filters)
            {
                if (filter == null)
                {
                    continue;
                }
                count++;
                var text = name + ":" + string.Join(",", filter.AllowedThingDefs.Select(d => d.defName).OrderBy(n => n, System.StringComparer.Ordinal));
                foreach (var c in text)
                {
                    hash = hash * 31 + c;
                }
            }
            return $"{count} filters, hash {hash:X16}";
        }

        public static string Stats() => state == 2 ? Feature + ": off, another mod patches ThingFilter" : $"thing filter removal loops: {narrowed:N0} loops walked only the allowed defs ({scanned:N0} ThingDef checks skipped), {full:N0} walked every ThingDef";
    }
}
