using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ObjectStats
    {
        public static void Report()
        {
            if (Environment.GetEnvironmentVariable("LEANMEMORY_OBJECTSTATS") != "1")
            {
                return;
            }
            var rows = Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .GroupBy(o => o.GetType().Name)
                .Select(g => new { Type = g.Key, Count = g.Count(), Bytes = g.Sum(o => Profiler.GetRuntimeMemorySizeLong(o)) })
                .OrderByDescending(r => r.Bytes)
                .ThenByDescending(r => r.Count)
                .Take(15);
#if RW17
            var sb = new StringBuilder($"[RLO] unity objects (allocated {Profiler.GetTotalAllocatedMemoryLong() >> 20} MB, reserved {Profiler.GetTotalReservedMemoryLong() >> 20} MB):");
#else
            var sb = new StringBuilder($"[RLO] unity objects (allocated {Profiler.GetTotalAllocatedMemoryLong() >> 20} MB, reserved {Profiler.GetTotalReservedMemoryLong() >> 20} MB, gfx driver {Profiler.GetAllocatedMemoryForGraphicsDriver() >> 20} MB):");
#endif
            foreach (var row in rows)
            {
                sb.Append($"\n  {row.Bytes >> 20,8} MB  x{row.Count,-8} {row.Type}");
            }
            Log.Message(sb.ToString());
        }
    }
}
