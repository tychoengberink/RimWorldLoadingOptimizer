#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using HarmonyLib;
using Verse;
using Waylith.Graphics;
using Waylith.Graphics.Meshes;

namespace RimWorldLoadingOptimizer
{
    // With tens of thousands of mod textures the graphics memory budget is used up, and each small device-local mesh
    // buffer from VMA's default pools then takes about 0.45 ms: the one frame that uploads all meshes before the main
    // menu spends ~15 s on 34k allocations for 6 MB of mesh data. Placing them in a custom VMA pool with a fixed 32 MB
    // block size takes microseconds per buffer (measured: that frame goes from ~16 s to 0.1 s). Every stream
    // stays its own VkBuffer at offset 0 with the engine's usage flags, so the draw path, barriers and tracking are
    // unchanged; only the placement inside device memory differs. Anything unusual runs the engine's own path.
    // Only taken once the engine's own small mesh allocations are measured to be slow (16 in a row averaging more than
    // 100 us; a normal VMA sub-allocation takes a few microseconds, these took about 450 us each with 95k mod textures),
    // and only until shortly after the main menu. Buffers allocated by then stay valid for the session and are released
    // before the allocator is destroyed. The pool adds at most one 32 MB block per 32 MB of small mesh data.
    public static class MeshPool
    {
        private const string Feature = "Mesh buffer pool";
        private const long MaxPooledBytes = 256L << 10;
        private const long BlockBytes = 32L << 20;
        private const double GraceSeconds = 10;
        private const int Window = 16;
        private const double SlowMicroseconds = 100;
        private static long menuAt;
        private static bool slowSeen;
        private static int measured;
        private static long measuredTicks;
        private static long beforeSlow;
        [ThreadStatic] private static long engineStart;
        private static readonly object gate = new object();
        private static readonly Dictionary<nint, BufferAllocationInfo> live = new Dictionary<nint, BufferAllocationInfo>();
        private static AccessTools.FieldRef<MeshUploader, VmaAllocator> allocatorOf;
        private static AccessTools.FieldRef<MeshUploader, long> renderFrameSerialOf, hostFallbackUntilFrameOf;
        private static VmaPool pool;
        private static VmaAllocator poolAllocator;
        private static bool failed;
        private static bool fallbackSeen;
        private static long pooled, refused, freed, peak, leftAtShutdown;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            var type = typeof(MeshUploader);
            var allocate = AccessTools.Method(type, "AllocateMeshBuffer");
            var shutdown = AccessTools.Method(type, nameof(MeshUploader.Shutdown));
            var free = AccessTools.Method(typeof(VmaAllocator), nameof(VmaAllocator.FreeAllocation), new[] { typeof(BufferAllocationInfo).MakeByRefType() });
            if (allocate == null || shutdown == null || free == null)
            {
                Log.Message($"[RLO] {Feature}: the engine's mesh uploader does not have the expected shape. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var release = AccessTools.Method(typeof(GpuMeshBuffer), nameof(GpuMeshBuffer.Release), new[] { typeof(IBufferAllocator) });
            var freeInterface = AccessTools.Method(typeof(IBufferAllocator), nameof(IBufferAllocator.FreeAllocation), new[] { typeof(BufferAllocationInfo).MakeByRefType() });
            if (!VersionCheck.Pattern(Feature, shutdown, code => VersionCheck.Calls(code, release) > 0, "releases the mesh buffers")
                || !VersionCheck.Pattern(Feature, release, code => VersionCheck.Calls(code, freeInterface) > 0 && FreesThrough(freeInterface, free), "frees through the allocator"))
            {
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(allocate));
            if (check.IsOff)
            {
                return;
            }
            allocatorOf = AccessTools.FieldRefAccess<MeshUploader, VmaAllocator>("allocator");
            renderFrameSerialOf = AccessTools.FieldRefAccess<MeshUploader, long>("renderFrameSerial");
            hostFallbackUntilFrameOf = AccessTools.FieldRefAccess<MeshUploader, long>("hostFallbackUntilFrame");
            harmony.Patch(allocate, prefix: new HarmonyMethod(typeof(MeshPool), nameof(AllocatePrefix)) { priority = Priority.Last }, postfix: new HarmonyMethod(typeof(MeshPool), nameof(AllocatePostfix)));
            harmony.Patch(free, prefix: new HarmonyMethod(typeof(MeshPool), nameof(FreePrefix)));
            harmony.Patch(shutdown, postfix: new HarmonyMethod(typeof(MeshPool), nameof(ShutdownPostfix)));
            LeanFixes.Reports.Add(Stats);
            MainMenu.Then(() => menuAt = Stopwatch.GetTimestamp());
            MainMenu.Then(() => Log.Message("[RLO] " + Stats()));
        }

        private static bool FreesThrough(MethodInfo declared, MethodInfo free)
        {
            if (declared == null)
            {
                return false;
            }
            var map = typeof(VmaAllocator).GetInterfaceMap(typeof(IBufferAllocator));
            var at = Array.IndexOf(map.InterfaceMethods, declared);
            if (at < 0)
            {
                return false;
            }
            var target = map.TargetMethods[at];
            return target == free || VersionCheck.Calls(PatchProcessor.GetOriginalInstructions(target), free) > 0;
        }

        private static VmaAllocationCreateInfo Request() => new VmaAllocationCreateInfo
        {
            Usage = VmaMemoryUsage.AutoPreferDevice,
            RequiredFlags = VkMemoryPropertyFlags.DeviceLocalBit,
            PreferredFlags = VkMemoryPropertyFlags.DeviceLocalBit,
            Flags = VmaAllocationCreateFlags.None,
            Priority = 1f,
            MemoryTypeBits = 0u
        };

        private static uint TypeFor(VmaAllocator allocator, VkBufferUsageFlags usage)
        {
            var info = new VkBufferCreateInfo { Size = ByteSize.Of(65536), Usage = usage | VkBufferUsageFlags.TransferDstBit, SharingMode = VkSharingMode.Exclusive };
            return allocator.FindMemoryTypeIndexForBufferInfo(info, Request(), out var index) == VkResult.Success ? index : uint.MaxValue;
        }

        private static bool EnsurePool(VmaAllocator allocator)
        {
            if (failed)
            {
                return false;
            }
            if (!pool.IsNull())
            {
                return poolAllocator == allocator;
            }
            var vertex = TypeFor(allocator, VkBufferUsageFlags.VertexBufferBit);
            var index = TypeFor(allocator, VkBufferUsageFlags.IndexBufferBit);
            if (vertex == uint.MaxValue || vertex != index)
            {
                failed = true;
                Log.Message($"[RLO] {Feature}: vertex and index buffers use different memory types ({vertex}, {index}); mesh buffers keep the engine's allocation.");
                return false;
            }
            var create = new VmaPoolCreateInfo { MemoryTypeIndex = vertex, BlockSize = ByteSize.Of(BlockBytes), Priority = 1f };
            if (allocator.CreatePool(create, out pool) != VkResult.Success)
            {
                failed = true;
                pool = default;
                return false;
            }
            poolAllocator = allocator;
            return true;
        }

        // Runs under MeshUploader's own lock. Only plain device-local requests that the engine would send to VMA unchanged
        // are taken: no host fallback window, never a fallback before (so DeviceLocalHasRoom is not consulted either).
        public static bool AllocatePrefix(MeshUploader __instance, long bytes, VkBufferUsageFlags usage, ref bool hostResident, ref BufferAllocationInfo __result)
        {
            if (hostFallbackUntilFrameOf(__instance) >= 0)
            {
                fallbackSeen = true;
            }
            if (check.IsOff || fallbackSeen || bytes <= 0 || bytes > MaxPooledBytes || renderFrameSerialOf(__instance) < hostFallbackUntilFrameOf(__instance))
            {
                return true;
            }
            try
            {
                lock (gate)
                {
                    if (menuAt != 0 && (Stopwatch.GetTimestamp() - menuAt) / (double)Stopwatch.Frequency > GraceSeconds)
                    {
                        return true;
                    }
                    var allocator = allocatorOf(__instance);
                    if (!pool.IsNull() && allocator != poolAllocator)
                    {
                        failed = true;
                        Log.Warning($"[RLO] {Feature}: the engine created a new allocator; new mesh buffers keep the engine's allocation.");
                        return true;
                    }
                    if (!slowSeen || check.IsChecking)
                    {
                        beforeSlow++;
                        engineStart = Stopwatch.GetTimestamp();
                        return true;
                    }
                    if (allocator == null || !EnsurePool(allocator))
                    {
                        return true;
                    }
                    var info = new VkBufferCreateInfo { Size = ByteSize.Of(bytes), Usage = usage | VkBufferUsageFlags.TransferDstBit, SharingMode = VkSharingMode.Exclusive };
                    var request = Request();
                    request.Pool = pool;
                    if (allocator.CreateBuffer(info, request, out var vkBuffer, out var allocation, out var allocationInfo) != VkResult.Success)
                    {
                        refused++;
                        return true;
                    }
                    var result = new BufferAllocationInfo(vkBuffer, default, info.Size, info.Usage, new AllocationInfo
                    {
                        Allocator = allocator,
                        Memory = allocationInfo.DeviceMemory,
                        MemoryTypeIndex = (int)allocationInfo.MemoryType,
                        AllocOffset = allocationInfo.Offset,
                        AllocSize = allocationInfo.Size,
                        AllocHandle = allocation.VkHandle
                    });
                    live[allocation.VkHandle] = result;
                    pooled++;
                    peak = Math.Max(peak, live.Count);
                    hostResident = false;
                    __result = result;
                    return false;
                }
            }
            catch (Exception e)
            {
                failed = true;
                Log.Warning($"[RLO] {Feature}: switched off after an error ({e.GetType().Name}: {e.Message}); mesh buffers keep the engine's allocation.");
                return true;
            }
        }

        // Times the engine's own small mesh allocations; when 16 in a row average more than 100 us, the pool takes the
        // following ones.
        public static void AllocatePostfix(MeshUploader __instance, long bytes, VkBufferUsageFlags usage, ref bool hostResident, BufferAllocationInfo __result)
        {
            var start = engineStart;
            if (start == 0)
            {
                return;
            }
            engineStart = 0;
            measuredTicks += Stopwatch.GetTimestamp() - start;
            if (check.IsChecking && !hostResident)
            {
                Compare(__instance, bytes, usage, __result);
            }
            if (++measured < Window)
            {
                return;
            }
            if (measuredTicks * 1e6 / Stopwatch.Frequency / measured > SlowMicroseconds)
            {
                slowSeen = true;
            }
            measured = 0;
            measuredTicks = 0;
        }

        private static void Compare(MeshUploader uploader, long bytes, VkBufferUsageFlags usage, BufferAllocationInfo result)
        {
            try
            {
                var allocator = allocatorOf(uploader);
                if (allocator == null)
                {
                    return;
                }
                var type = TypeFor(allocator, usage);
                var expected = usage | VkBufferUsageFlags.TransferDstBit;
                var same = (long)result.BufferSize.Value == bytes && result.Usage == expected && result.AllocationInfo.MemoryTypeIndex == (int)type;
                check.Report(same, $"{bytes} bytes usage {usage}: engine gave {(long)result.BufferSize.Value} bytes usage {result.Usage} memory type {result.AllocationInfo.MemoryTypeIndex}, pool would use memory type {type}");
            }
            catch (Exception e)
            {
                check.Report(false, $"{e.GetType().Name}: {e.Message}");
            }
        }

        public static void FreePrefix(in BufferAllocationInfo inAllocation)
        {
            if (live.Count == 0)
            {
                return;
            }
            lock (gate)
            {
                if (live.Remove(inAllocation.AllocationInfo.AllocHandle))
                {
                    freed++;
                }
            }
        }

        // MeshUploader.Shutdown frees every mesh buffer and runs before the allocator is destroyed. vma.dll is built
        // with assertions, so the pool must be empty and destroyed before vmaDestroyAllocator or the game aborts on quit.
        public static void ShutdownPostfix()
        {
            lock (gate)
            {
                if (pool.IsNull())
                {
                    return;
                }
                try
                {
                    poolAllocator.GetPoolStatistics(pool, out var statistics);
                    if (statistics.AllocationCount != live.Count)
                    {
                        Log.Warning($"[RLO] {Feature}: the pool holds {statistics.AllocationCount} buffers but {live.Count} are known to be live; the pool is left as it is.");
                        return;
                    }
                    leftAtShutdown = live.Count;
                    foreach (var allocation in new List<BufferAllocationInfo>(live.Values))
                    {
                        poolAllocator.FreeAllocation(allocation);
                    }
                    live.Clear();
                    poolAllocator.DestroyPool(pool);
                    pool = default;
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] {Feature}: could not release the pool at shutdown: {e.Message}");
                }
            }
        }

        public static string Stats()
        {
            lock (gate)
            {
                return failed && pooled == 0
                    ? $"{Feature}: off"
                    : $"{Feature}: {pooled} mesh streams placed in 32 MB blocks instead of their own device allocation ({live.Count} live, peak {peak}, {freed} freed, {refused} left to the engine after a refusal, {beforeSlow} left to the engine before its allocations were measured to be slow)";
            }
        }
    }
}
#endif
