#if RW17
using System;
using System.Collections.Generic;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using HarmonyLib;
using Verse;
using Waylith.Graphics.Meshes;

namespace RimWorldLoadingOptimizer
{
    // When device-local memory is over its budget (tens of thousands of mod textures), VMA no longer creates new blocks
    // in its default pools and gives every small device-local buffer its own vkAllocateMemory instead: the one frame
    // that uploads all meshes before the main menu spends ~15 s in 34k kernel allocations for 6 MB of mesh data, and
    // each of them takes at least a memory page. A custom pool with an explicit block size never falls back to
    // dedicated allocations, so small mesh streams are placed in 32 MB blocks of the same memory type. Every stream
    // stays its own VkBuffer at offset 0 with the engine's usage flags, so the draw path, barriers and tracking are
    // unchanged; only the placement inside device memory differs. Anything unusual runs the engine's own path.
    public static class MeshPool
    {
        private const string Feature = "Mesh buffer pool";
        private const long MaxPooledBytes = 256L << 10;
        private const long BlockBytes = 32L << 20;
        private static readonly object gate = new object();
        private static readonly Dictionary<nint, BufferAllocationInfo> live = new Dictionary<nint, BufferAllocationInfo>();
        private static AccessTools.FieldRef<MeshUploader, VmaAllocator> allocatorOf;
        private static AccessTools.FieldRef<MeshUploader, long> renderFrameSerialOf, hostFallbackUntilFrameOf;
        private static VmaPool pool;
        private static VmaAllocator poolAllocator;
        private static bool failed;
        private static long pooled, refused, freed, peak, leftAtShutdown;

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
            if (!Fingerprint.Matches(Feature,
                    Fingerprint.Known(allocate, "661045207CDF7941"),
                    Fingerprint.Known(shutdown, ShutdownHash),
                    Fingerprint.Known(free, FreeHash)))
            {
                return;
            }
            allocatorOf = AccessTools.FieldRefAccess<MeshUploader, VmaAllocator>("allocator");
            renderFrameSerialOf = AccessTools.FieldRefAccess<MeshUploader, long>("renderFrameSerial");
            hostFallbackUntilFrameOf = AccessTools.FieldRefAccess<MeshUploader, long>("hostFallbackUntilFrame");
            harmony.Patch(allocate, prefix: new HarmonyMethod(typeof(MeshPool), nameof(AllocatePrefix)) { priority = Priority.Last });
            harmony.Patch(free, prefix: new HarmonyMethod(typeof(MeshPool), nameof(FreePrefix)));
            harmony.Patch(shutdown, postfix: new HarmonyMethod(typeof(MeshPool), nameof(ShutdownPostfix)));
            LeanFixes.Reports.Add(Stats);
            MainMenu.Then(() => Log.Message("[RLO] " + Stats()));
        }

        private const string ShutdownHash = "F5361A1E22C51D95";
        private const string FreeHash = "BC9D5D54422D66E9";

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
            if (bytes <= 0 || bytes > MaxPooledBytes || hostFallbackUntilFrameOf(__instance) >= 0 || renderFrameSerialOf(__instance) < hostFallbackUntilFrameOf(__instance))
            {
                return true;
            }
            try
            {
                lock (gate)
                {
                    var allocator = allocatorOf(__instance);
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
                    : $"{Feature}: {pooled} mesh streams placed in 32 MB blocks instead of their own device allocation ({live.Count} live, peak {peak}, {freed} freed, {refused} left to the engine after a refusal)";
            }
        }
    }
}
#endif
