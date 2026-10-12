using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public sealed class HashedDefs : ReadOnlyCollection<ThingDef>, ICollection<ThingDef>
    {
        private HashSet<ThingDef> hashed;

        public HashedDefs(IList<ThingDef> list) : base(list)
        {
        }

        public new bool Contains(ThingDef item)
        {
            var current = hashed;
            if (current == null)
            {
                current = new HashSet<ThingDef>(Items, ReferenceComparer<ThingDef>.Instance);
                hashed = current;
                Interlocked.Increment(ref CategorySet.Sets);
            }
            var result = current.Contains(item);
            if (RLOMod.Verify)
            {
                Interlocked.Increment(ref CategorySet.Checks);
                if (result != base.Contains(item) && Interlocked.Increment(ref CategorySet.Mismatches) <= 10)
                {
                    Log.Warning($"[RLO] Category sets verify mismatch for {item}");
                }
            }
            return result;
        }
    }

    public static class CategorySet
    {
        private static readonly ConditionalWeakTable<ReadOnlyCollection<ThingDef>, HashedDefs> wrappers = new ConditionalWeakTable<ReadOnlyCollection<ThingDef>, HashedDefs>();
        private static bool installed;
        public static int Sets;
        public static int Checks;
        public static int Mismatches;

        public static void Install(Harmony harmony)
        {
            if (!CategoryCache.Installed)
            {
                return;
            }
            harmony.Patch(AccessTools.PropertyGetter(typeof(ThingCategoryDef), nameof(ThingCategoryDef.DescendantThingDefs)),
                postfix: new HarmonyMethod(typeof(CategorySet), nameof(Postfix)) { priority = Priority.Last });
            installed = true;
        }

        public static void Postfix(ref IEnumerable<ThingDef> __result)
        {
            if (__result is ReadOnlyCollection<ThingDef> list && !(list is HashedDefs))
            {
                __result = wrappers.GetValue(list, l => new HashedDefs(l));
            }
        }

        public static string Stats() => !installed ? "Category sets: not active (needs Category cache)" : $"Category sets: hashed sets built {Sets}" + (RLOMod.Verify ? $", verify checks {Checks}, mismatches {Mismatches}" : "");
    }
}
