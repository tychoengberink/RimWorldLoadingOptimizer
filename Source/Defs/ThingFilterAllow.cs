using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ThingFilterAllow
    {
        public const string Feature = "Thing filter category allows";

        private sealed class Closure
        {
            public HashSet<ThingDef> All;
            public ThingDef[] WithVirtuals;
            public bool Usable;
            public int Signature;
            public int CheckedAt;
        }

        private static readonly ConditionalWeakTable<ReadOnlyCollection<ThingDef>, Closure> closures =
            new ConditionalWeakTable<ReadOnlyCollection<ThingDef>, Closure>();
        private static readonly MismatchLog mismatches = new MismatchLog(Feature);
        private static AccessTools.FieldRef<ThingFilter, HashSet<ThingDef>> allowed;
        private static AccessTools.FieldRef<ThingFilter, Action> callback;
        private static AccessTools.FieldRef<ThingFilter, TreeNode_ThingCategory> displayRoot;
        private static Fingerprint.Guard guard;
        private static SelfCheck check;

        [ThreadStatic]
        private static bool bypass;
        private static long served,
            added,
            left,
            inexact;

        public static void Install(Harmony harmony)
        {
            var setCategory = AccessTools.Method(
                typeof(ThingFilter),
                nameof(ThingFilter.SetAllow),
                new[]
                {
                    typeof(ThingCategoryDef),
                    typeof(bool),
                    typeof(IEnumerable<ThingDef>),
                    typeof(IEnumerable<SpecialThingFilterDef>),
                }
            );
            var setThing = AccessTools.Method(
                typeof(ThingFilter),
                nameof(ThingFilter.SetAllow),
                new[] { typeof(ThingDef), typeof(bool) }
            );
            var setSpecial = AccessTools.Method(
                typeof(ThingFilter),
                nameof(ThingFilter.SetAllow),
                new[] { typeof(SpecialThingFilterDef), typeof(bool) }
            );
            var allowedField = AccessTools.Field(typeof(ThingFilter), "allowedDefs");
            var callbackField = AccessTools.Field(typeof(ThingFilter), "settingsChangedCallback");
            var displayField = AccessTools.Field(typeof(ThingFilter), "displayRootCategoryInt");
            var virtualField = AccessTools.Field(typeof(ThingDef), nameof(ThingDef.virtualDefs));
            if (
                setCategory == null
                || setThing == null
                || setSpecial == null
                || allowedField?.FieldType != typeof(HashSet<ThingDef>)
                || callbackField?.FieldType != typeof(Action)
                || displayField?.FieldType != typeof(TreeNode_ThingCategory)
                || virtualField?.FieldType != typeof(List<ThingDef>)
            )
            {
                VersionCheck.Skip(Feature, "a method or field it needs no longer exists");
                return;
            }
            allowed = AccessTools.FieldRefAccess<ThingFilter, HashSet<ThingDef>>(allowedField);
            callback = AccessTools.FieldRefAccess<ThingFilter, Action>(callbackField);
            displayRoot = AccessTools.FieldRefAccess<ThingFilter, TreeNode_ThingCategory>(displayField);
            check = new SelfCheck(Feature, VersionCheck.Code(setCategory, setThing), 50);
            guard = new Fingerprint.Guard(setCategory, setThing);
            harmony.Patch(setCategory, prefix: new HarmonyMethod(typeof(ThingFilterAllow), nameof(Prefix)));
            StartupPhaseFixes.Reports.Add(Stats);
        }

        public static bool Prefix(
            ThingFilter __instance,
            ThingCategoryDef categoryDef,
            bool allow,
            IEnumerable<ThingDef> exceptedDefs,
            IEnumerable<SpecialThingFilterDef> exceptedFilters
        )
        {
            if (
                bypass
                || !allow
                || exceptedDefs != null
                || categoryDef == null
                || check.IsOff
                || !ThingCategoryNodeDatabase.initialized
                || callback(__instance) != null
                || !guard.Ours
            )
            {
                return true;
            }
            var target = allowed(__instance);
            if (
                target == null
                || !ReferenceEquals(target.Comparer, EqualityComparer<ThingDef>.Default)
                || !(categoryDef.DescendantThingDefs is ReadOnlyCollection<ThingDef> defs)
            )
            {
                Interlocked.Increment(ref left);
                return true;
            }
            var closure = closures.GetValue(defs, Build);
            if (closure.Usable && !Current(closure))
            {
                closures.Remove(defs);
                closure = closures.GetValue(defs, Build);
            }
            if (!closure.Usable)
            {
                Interlocked.Increment(ref left);
                return true;
            }
            if (!Exact(closure, target))
            {
                Interlocked.Increment(ref inexact);
                return true;
            }
            if (check.IsChecking || DevSwitches.Verify)
            {
                Compare(__instance, categoryDef, exceptedFilters, closure, target);
                return false;
            }
            var before = target.Count;
            target.UnionWith(closure.All);
            foreach (var special in categoryDef.DescendantSpecialThingFilterDefs)
            {
                if (special.configurable && (exceptedFilters == null || !exceptedFilters.Contains(special)))
                {
                    __instance.SetAllow(special, true);
                }
            }
            displayRoot(__instance) = null;
            Interlocked.Increment(ref served);
            Interlocked.Add(ref added, target.Count - before);
            return false;
        }

        private static void Compare(
            ThingFilter filter,
            ThingCategoryDef category,
            IEnumerable<SpecialThingFilterDef> exceptedFilters,
            Closure closure,
            HashSet<ThingDef> target
        )
        {
            var expected = new HashSet<ThingDef>(target);
            expected.UnionWith(closure.All);
            bypass = true;
            try
            {
                filter.SetAllow(category, true, null, exceptedFilters);
            }
            finally
            {
                bypass = false;
            }
            var same = ReferenceEquals(allowed(filter), target) && target.SetEquals(expected);
            if (!same)
            {
                mismatches.Report($"category {category.defName}");
            }
            check.Report(same, $"category {category.defName}");
        }

        private static Closure Build(ReadOnlyCollection<ThingDef> defs)
        {
            var closure = new Closure { All = new HashSet<ThingDef>(), Usable = true };
            var withVirtuals = new List<ThingDef>();
            var pending = new Stack<ThingDef>();
            foreach (var def in defs)
            {
                pending.Push(def);
                while (pending.Count > 0)
                {
                    var next = pending.Pop();
                    if (next == null || next.virtualDefs == null)
                    {
                        closure.Usable = false;
                        return closure;
                    }
                    if (!closure.All.Add(next) || next.virtualDefs.Count == 0)
                    {
                        continue;
                    }
                    withVirtuals.Add(next);
                    foreach (var virtualDef in next.virtualDefs)
                    {
                        pending.Push(virtualDef);
                    }
                }
            }
            closure.WithVirtuals = withVirtuals.ToArray();
            closure.Signature = Signature(closure);
            closure.CheckedAt = Environment.TickCount;
            return closure;
        }

        private static int Signature(Closure closure)
        {
            unchecked
            {
                var hash = closure.WithVirtuals.Length;
                foreach (var def in closure.All)
                {
                    var virtuals = def.virtualDefs;
                    if (virtuals == null)
                    {
                        return -1;
                    }
                    if (virtuals.Count == 0)
                    {
                        continue;
                    }
                    var own = RuntimeHelpers.GetHashCode(def);
                    foreach (var virtualDef in virtuals)
                    {
                        own = own * 31 + (virtualDef == null ? 0 : RuntimeHelpers.GetHashCode(virtualDef));
                    }
                    hash += own;
                }
                return hash;
            }
        }

        private static bool Current(Closure closure)
        {
            var now = Environment.TickCount;
            if (unchecked(now - closure.CheckedAt) <= 50)
            {
                return true;
            }
            if (Signature(closure) != closure.Signature)
            {
                return false;
            }
            closure.CheckedAt = now;
            return true;
        }

        private static bool Exact(Closure closure, HashSet<ThingDef> target)
        {
            if (target.Count == 0)
            {
                return true;
            }
            foreach (var def in closure.WithVirtuals)
            {
                if (!target.Contains(def))
                {
                    continue;
                }
                foreach (var virtualDef in def.virtualDefs)
                {
                    if (!target.Contains(virtualDef))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static string Stats() =>
            guard.Blocked
                ? Feature + ": off, another mod patches ThingFilter.SetAllow"
                : $"thing filter category allows: {served:N0} categories allowed in one step ({added:N0} things added), {left:N0} left to the game (no category cache or changed things), {inexact:N0} left to the game because a thing was allowed without its variants"
                    + (DevSwitches.Verify ? $", verify mismatches {mismatches.Count}" : "");
    }
}
