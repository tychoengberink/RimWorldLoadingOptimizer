using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RimWorldLoadingOptimizer
{
    public static class MonoHeap
    {
        private delegate UIntPtr GetSize();

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        public static string Stats()
        {
#if RW17
            var info = GC.GetGCMemoryInfo();
            return $"managed heap {info.HeapSizeBytes >> 20} MB, committed {info.TotalCommittedBytes >> 20} MB";
#else
            if (Application.platform != RuntimePlatform.OSXPlayer && Application.platform != RuntimePlatform.OSXEditor)
            {
                return "Mono heap stats only available on macOS";
            }
            try
            {
                return $"Mono heap {Size("GC_get_heap_size") >> 20} MB, free {Size("GC_get_free_bytes") >> 20} MB";
            }
            catch (Exception)
            {
                return "Mono heap stats unavailable on this platform";
            }
#endif
        }

        private static ulong Size(string symbol)
        {
            var ptr = dlsym(new IntPtr(-2), symbol);
            if (ptr == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException(symbol);
            }
            return ((GetSize)Marshal.GetDelegateForFunctionPointer(ptr, typeof(GetSize)))().ToUInt64();
        }
    }
}
