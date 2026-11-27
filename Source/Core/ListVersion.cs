#if RW17
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ListVersion
    {
        // List<T> counts every change through its own methods in _version, so a thing replaced in place is seen
        // right away instead of at the next timed identity check.
        public static readonly bool Available = Probe();

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
        public static extern ref int Of(List<ThingDef> list);

        private static bool Probe()
        {
            try
            {
                return Counts();
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Counts()
        {
            var probe = new List<ThingDef>();
            var before = Of(probe);
            probe.Add(null);
            return Of(probe) != before;
        }
    }
}
#endif
