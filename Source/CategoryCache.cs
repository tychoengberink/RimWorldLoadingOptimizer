using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CategoryCache
    {
        // One category of the subtree in breadth-first order, so the children of a node are the nodes from
        // FirstChild up to the next node's FirstChild.
        private struct Node
        {
            public ThingCategoryDef Category;
            public List<ThingDef> Things;
            public List<ThingCategoryDef> Children;
            public int ThingCount;
            public int ThingVersion;
            public int FirstChild;
        }

        private sealed class Entry
        {
            public IEnumerable<ThingDef> Defs;
            public Node[] Shape;
            public int Identity;
            public int IdentityCheckedAt;
        }

        private static ConcurrentDictionary<ThingCategoryDef, Entry> cache = new ConcurrentDictionary<ThingCategoryDef, Entry>();

        private static MethodBase getter;
        private static int state;
        private static SelfCheck check;
        [ThreadStatic] private static bool bypass;

        public static bool Installed { get; private set; }

        public static int Hits;
        public static int Builds;

#if RW17
        // List<T> counts every change through its own methods in _version, so a thing replaced in place is seen
        // right away instead of at the next timed identity check.
        private static bool versions;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
        private static extern ref int Version(List<ThingDef> list);
#endif

        public static void Install(Harmony harmony)
        {
#if RW17
            try
            {
                var probe = new List<ThingDef>();
                var before = Version(probe);
                probe.Add(null);
                versions = Version(probe) != before;
            }
            catch (Exception)
            {
                versions = false;
            }
#endif
            getter = AccessTools.PropertyGetter(typeof(ThingCategoryDef), nameof(ThingCategoryDef.DescendantThingDefs));
            var children = AccessTools.PropertyGetter(typeof(ThingCategoryDef), nameof(ThingCategoryDef.ThisAndChildCategoryDefs));
            if (getter == null || children == null)
            {
                VersionCheck.Skip("Category cache", "ThingCategoryDef.DescendantThingDefs or ThisAndChildCategoryDefs no longer exists");
                return;
            }
            check = new SelfCheck("Category cache", VersionCheck.Known(
                Fingerprint.Known(getter, "591A2633DA38C9F9", "CCDE11F650DE24DF"),
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(getter), "3FC97BBCFF7FBC1D", "2991503FAE8B1F09"),
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(children), "2A83B3162CE0B4B3", "D2519FD28522DB53")));
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(CategoryCache), nameof(Prefix)));
            Installed = true;
            harmony.Patch(AccessTools.Method(typeof(ThingCategoryNodeDatabase), nameof(ThingCategoryNodeDatabase.Clear)),
                postfix: new HarmonyMethod(typeof(CategoryCache), nameof(Reset)));
        }

        public static void Reset()
        {
            cache = new ConcurrentDictionary<ThingCategoryDef, Entry>();
        }

        public static bool Prefix(ThingCategoryDef __instance, ref IEnumerable<ThingDef> __result)
        {
            if (bypass || check.IsOff || !ThingCategoryNodeDatabase.initialized)
            {
                return true;
            }
            if (state == 0)
            {
                state = Fingerprint.OnlyOurs(getter) ? 1 : 2;
            }
            if (state != 1)
            {
                return true;
            }
            var now = Environment.TickCount;
            if (cache.TryGetValue(__instance, out var entry) && Unchanged(entry.Shape))
            {
                if (unchecked(now - entry.IdentityCheckedAt) <= 1000 || Identity(__instance) == entry.Identity)
                {
                    entry.IdentityCheckedAt = now;
                    Interlocked.Increment(ref Hits);
                    __result = Checked(__instance, entry.Defs);
                    return false;
                }
            }
            var shape = Capture(__instance);
            var defs = new List<ThingDef>();
            Collect(__instance, defs);
            var readOnly = defs.AsReadOnly();
            cache[__instance] = new Entry { Defs = readOnly, Shape = shape, Identity = Identity(__instance), IdentityCheckedAt = now };
            Interlocked.Increment(ref Builds);
            __result = Checked(__instance, readOnly);
            return false;
        }

        private static IEnumerable<ThingDef> Checked(ThingCategoryDef category, IEnumerable<ThingDef> defs)
        {
            if (!check.IsChecking)
            {
                return defs;
            }
            List<ThingDef> original;
            bypass = true;
            try
            {
                original = category.DescendantThingDefs.ToList();
            }
            finally
            {
                bypass = false;
            }
            var same = original.SequenceEqual(defs);
            check.Report(same, $"category {category.defName}");
            return same ? defs : original;
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

        private static Node[] Capture(ThingCategoryDef category)
        {
            var nodes = new List<Node> { new Node { Category = category } };
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                node.Things = node.Category.childThingDefs;
                node.Children = node.Category.childCategories;
                node.ThingCount = node.Things.Count;
#if RW17
                node.ThingVersion = versions ? Version(node.Things) : 0;
#endif
                node.FirstChild = nodes.Count;
                nodes[i] = node;
                foreach (var child in node.Children)
                {
                    nodes.Add(new Node { Category = child });
                }
            }
            return nodes.ToArray();
        }

        // Same subtree as when the entry was built: every category still has the same two lists, with the same
        // number of things and the same child categories in the same order. This implies the child and thing
        // counts the old signature hashed, so it never accepts a subtree the old check would have rebuilt.
        private static bool Unchanged(Node[] nodes)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                ref var node = ref nodes[i];
                var category = node.Category;
                var children = category.childCategories;
                if (!ReferenceEquals(category.childThingDefs, node.Things) || !ReferenceEquals(children, node.Children) || node.Things.Count != node.ThingCount)
                {
                    return false;
                }
#if RW17
                if (versions && Version(node.Things) != node.ThingVersion)
                {
                    return false;
                }
#endif
                var first = node.FirstChild;
                var end = i + 1 < nodes.Length ? nodes[i + 1].FirstChild : nodes.Length;
                if (children.Count != end - first)
                {
                    return false;
                }
                for (var j = 0; j < children.Count; j++)
                {
                    if (!ReferenceEquals(children[j], nodes[first + j].Category))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
