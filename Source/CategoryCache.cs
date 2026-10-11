using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CategoryCache
    {
        private sealed class Entry
        {
            public IEnumerable<ThingDef> Defs;
            public int Signature;
            public int Identity;
            public int IdentityCheckedAt;
        }

        private static ConcurrentDictionary<ThingCategoryDef, Entry> cache = new ConcurrentDictionary<ThingCategoryDef, Entry>();

        public static int Hits;
        public static int Builds;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(ThingCategoryDef), nameof(ThingCategoryDef.DescendantThingDefs)),
                prefix: new HarmonyMethod(typeof(CategoryCache), nameof(Prefix)));
            harmony.Patch(AccessTools.Method(typeof(ThingCategoryNodeDatabase), nameof(ThingCategoryNodeDatabase.Clear)),
                postfix: new HarmonyMethod(typeof(CategoryCache), nameof(Reset)));
        }

        public static void Reset()
        {
            cache = new ConcurrentDictionary<ThingCategoryDef, Entry>();
        }

        public static bool Prefix(ThingCategoryDef __instance, ref IEnumerable<ThingDef> __result)
        {
            if (!ThingCategoryNodeDatabase.initialized)
            {
                return true;
            }
            var signature = Signature(__instance);
            var now = Environment.TickCount;
            if (cache.TryGetValue(__instance, out var entry) && entry.Signature == signature)
            {
                if (unchecked(now - entry.IdentityCheckedAt) <= 1000 || Identity(__instance) == entry.Identity)
                {
                    entry.IdentityCheckedAt = now;
                    Interlocked.Increment(ref Hits);
                    __result = entry.Defs;
                    return false;
                }
            }
            var defs = new List<ThingDef>();
            Collect(__instance, defs);
            var readOnly = defs.AsReadOnly();
            cache[__instance] = new Entry { Defs = readOnly, Signature = signature, Identity = Identity(__instance), IdentityCheckedAt = now };
            Interlocked.Increment(ref Builds);
            __result = readOnly;
            return false;
        }

        private static void Collect(ThingCategoryDef category, List<ThingDef> defs)
        {
            defs.AddRange(category.childThingDefs);
            foreach (var child in category.childCategories)
            {
                Collect(child, defs);
            }
        }

        private static int Identity(ThingCategoryDef category)
        {
            unchecked
            {
                var hash = RuntimeHelpers.GetHashCode(category.childThingDefs);
                foreach (var def in category.childThingDefs)
                {
                    hash = hash * 31 + (def == null ? 0 : RuntimeHelpers.GetHashCode(def));
                }
                foreach (var child in category.childCategories)
                {
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(child) + Identity(child);
                }
                return hash;
            }
        }

        private static int Signature(ThingCategoryDef category)
        {
            unchecked
            {
                var hash = 17 * 31 + category.childThingDefs.Count;
                hash = hash * 31 + category.childCategories.Count;
                foreach (var child in category.childCategories)
                {
                    hash = hash * 31 + Signature(child);
                }
                return hash;
            }
        }
    }
}
