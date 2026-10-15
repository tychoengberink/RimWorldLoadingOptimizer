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
using RimWorld;
using Verse;
using Waylith.Graphics;
using Waylith.Graphics.Meshes;

namespace RimWorldLoadingOptimizer
{
    // Mesh uploads: times the engine's mesh upload path until shortly after the main menu and, when enabled,
    // gives every upload command buffer one mapped staging buffer that its streams are copied from, instead of
    // one staging buffer (VMA allocation, map, unmap and later free) per vertex or index stream.
    public static class MeshStaging
    {
        private const long ArenaLimit = (128L << 20) + (16L << 20);
        private const int Alignment = 16;
        private const double GraceSeconds = 10;

        private delegate BufferAllocationInfo AllocateMesh(MeshUploader uploader, string label, long bytes, VkBufferUsageFlags usage, out bool hostResident);

        private static Harmony harmony;
        private static readonly List<MethodBase> patched = new List<MethodBase>();
        private static MethodBase create;
        private static AllocateMesh allocateMeshBuffer;
        private static AccessTools.FieldRef<MeshUploader, VmaAllocator> allocatorOf;
        private static AccessTools.FieldRef<MeshUploader, object> syncOf;
        private static bool arenaEnabled, done, othersLogged;
        private static string arenaState = "off";
        private static MeshUploader uploader;
        private static long menuAt;

        [ThreadStatic]
        private static Batch batch;

        [ThreadStatic]
        private static int batchesAtDrain;

        private static int drains, batches, meshes, leases, streams, arenas, arenaStreams, fallbackStreams, arenaFailures, mismatches;
        private static long drainTicks, longestDrain, batchTicks, recordTicks, createTicks, deviceTicks, submitTicks, frameTicks, arenaTicks;
        private static long streamBytes, arenaBytes, arenaReserved;

        public sealed class Batch
        {
            public long Remaining;
            public Arena Current;
            public readonly List<Arena> Open = new List<Arena>();
            public bool Failed;
        }

        public sealed class Arena
        {
            public List<BufferAllocationInfo> List;
            public BufferAllocationInfo Allocation;
            public AllocationMapInfo Map;
            public VmaAllocator Allocator;
            public IntPtr Pointer;
            public long Used, Capacity;
            public bool Mapped;
        }

        public static void Install(Harmony h, bool arena)
        {
            harmony = h;
            var type = typeof(MeshUploader);
            var drain = AccessTools.Method(type, nameof(MeshUploader.DrainPendingUploads));
            var uploadBatch = AccessTools.Method(type, "UploadBatch");
            var record = AccessTools.Method(type, "RecordMeshUpload");
            create = AccessTools.Method(type, "CreateDeviceLocalBuffer");
            var allocate = AccessTools.Method(type, "AllocateMeshBuffer");
            var submit = AccessTools.Method(type, "SubmitUploadCommandBuffer");
            var beginFrame = AccessTools.Method(type, nameof(MeshUploader.BeginFrame));
            if (drain == null || uploadBatch == null || record == null || create == null || allocate == null || submit == null || beginFrame == null)
            {
                Log.Message("[RLO] mesh uploads: the engine's mesh uploader was not found; skipped");
                return;
            }
            arenaEnabled = arena && Environment.GetEnvironmentVariable("LEANMEMORY_NOMESHARENA") != "1" && ArenaShapeMatches(type, drain, uploadBatch, record, allocate, submit);
            arenaState = arenaEnabled ? "on" : arena ? "off (the engine code differs, or LEANMEMORY_NOMESHARENA=1)" : "off (timing only)";
            if (arenaEnabled)
            {
                allocateMeshBuffer = (AllocateMesh)Delegate.CreateDelegate(typeof(AllocateMesh), null, (MethodInfo)allocate);
                allocatorOf = AccessTools.FieldRefAccess<MeshUploader, VmaAllocator>("allocator");
            }
            syncOf = AccessTools.FieldRefAccess<MeshUploader, object>("sync");
            Patch(drain, nameof(DrainPrefix), nameof(DrainPostfix));
            Patch(uploadBatch, nameof(BatchPrefix), finalizer: nameof(BatchFinalizer));
            Patch(record, nameof(TimePrefix), finalizer: nameof(RecordFinalizer));
            Patch(create, nameof(CreatePrefix), nameof(CreatePostfix));
            Patch(allocate, nameof(TimePrefix), nameof(AllocatePostfix));
            Patch(submit, nameof(TimePrefix), nameof(SubmitPostfix));
            Patch(beginFrame, nameof(TimePrefix), nameof(FramePostfix));
            MainMenu.Then(() => menuAt = Stopwatch.GetTimestamp());
            harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)), postfix: new HarmonyMethod(typeof(MeshStaging), nameof(MenuPostfix)));
            LeanFixes.Reports.Add(Stats);
        }

        private static bool ArenaShapeMatches(Type type, MethodBase drain, MethodBase uploadBatch, MethodBase record, MethodBase allocate, MethodBase submit)
        {
            var extensions = typeof(VmaAllocationExtensions);
            var retirements = AccessTools.Inner(type, "UploadRetirementQueue");
            return Fingerprint.Matches("Mesh staging buffer",
                Fingerprint.Known(drain, "1F748AFBDF3BBE63"),
                Fingerprint.Known(uploadBatch, "5958D65DAB7F282E"),
                Fingerprint.Known(record, "93E926ACD9DE45D6"),
                Fingerprint.Known(create, "B2F529A82799A514"),
                Fingerprint.Known(allocate, "661045207CDF7941"),
                Fingerprint.Known(submit, "93EF8BA6088DF5EC"),
                Fingerprint.Known(AccessTools.Method(type, "FreeAllocations"), "FD239C61BDBB8145"),
                Fingerprint.Known(retirements == null ? null : AccessTools.Method(retirements, "FlushCompleted"), "CCF37DD0E764B4BB"),
                Fingerprint.Known(AccessTools.Method(extensions, nameof(VmaAllocationExtensions.AllocateBufferChecked), new[] { typeof(VmaAllocator), typeof(string), typeof(VkBufferCreateInfo).MakeByRefType(), typeof(AllocationInfo.CreateInfo).MakeByRefType(), typeof(bool) }), "B73C0D5D89ADDAE2"),
                Fingerprint.Known(AccessTools.Method(extensions, "ConvertInfo"), "2D80315D5879AD71"));
        }

        private static void Patch(MethodBase method, string prefix, string postfix = null, string finalizer = null)
        {
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(MeshStaging), prefix) { priority = Priority.First },
                postfix: postfix == null ? null : new HarmonyMethod(typeof(MeshStaging), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(MeshStaging), finalizer));
            patched.Add(method);
        }

        private static long Align(long bytes) => (bytes + Alignment - 1) & ~(long)(Alignment - 1);

        public static void TimePrefix(out long __state) => __state = done ? 0 : Stopwatch.GetTimestamp();

        public static void DrainPrefix(MeshUploader __instance, out long __state)
        {
            uploader = __instance;
            __state = done ? 0 : Stopwatch.GetTimestamp();
            batchesAtDrain = Volatile.Read(ref batches);
        }

        public static void DrainPostfix(long __state)
        {
            if (__state == 0 || Volatile.Read(ref batches) == batchesAtDrain)
            {
                return;
            }
            var ticks = Stopwatch.GetTimestamp() - __state;
            Interlocked.Increment(ref drains);
            Interlocked.Add(ref drainTicks, ticks);
            long longest;
            while (ticks > (longest = Interlocked.Read(ref longestDrain)) && Interlocked.CompareExchange(ref longestDrain, ticks, longest) != longest)
            {
            }
        }

        public static void BatchPrefix(MeshUploader __instance, IReadOnlyList<Mesh> meshes, out (long start, Batch owned) __state)
        {
            __state = (0, null);
            if (done)
            {
                return;
            }
            Interlocked.Increment(ref batches);
            Interlocked.Add(ref MeshStaging.meshes, meshes?.Count ?? 0);
            if (batch != null || !arenaEnabled || meshes == null)
            {
                __state = (Stopwatch.GetTimestamp(), null);
                return;
            }
            if (!Fingerprint.OnlyOurs(create))
            {
                if (!othersLogged)
                {
                    othersLogged = true;
                    Log.Message("[RLO] mesh staging buffer: another mod patches MeshUploader.CreateDeviceLocalBuffer. This speedup is skipped and the original code runs; nothing is broken.");
                }
                __state = (Stopwatch.GetTimestamp(), null);
                return;
            }
            batch = new Batch { Remaining = Estimate(meshes) };
            __state = (Stopwatch.GetTimestamp(), batch);
        }

        // Only sizes the staging buffers: a too small estimate sends the extra streams down the original path,
        // a too large one only reserves more staging memory until the upload finishes.
        private static long Estimate(IReadOnlyList<Mesh> meshes)
        {
            long total = 0;
            for (var i = 0; i < meshes.Count; i++)
            {
                try
                {
                    var mesh = meshes[i];
                    if (mesh == null || mesh.Residency == MeshResidency.CpuOnly || mesh.Residency == MeshResidency.DynamicMapped || !mesh.GpuResource.NeedsUpload(mesh.Version))
                    {
                        continue;
                    }
                    var cpu = mesh.CpuStorage;
                    if (cpu == null || cpu.VertexCount == 0)
                    {
                        continue;
                    }
                    for (var s = 0; s < cpu.Layout.StreamCount; s++)
                    {
                        total += Align(cpu.GetVertexStream(s)?.LengthBytes ?? 0);
                    }
                    if (cpu.IndexCount > 0)
                    {
                        total += Align(cpu.GetIndexBuffer()?.LengthBytes ?? 0);
                    }
                }
                catch (Exception)
                {
                }
            }
            return total;
        }

        public static Exception BatchFinalizer(Exception __exception, (long start, Batch owned) __state)
        {
            if (__state.owned != null)
            {
                CloseAll(__state.owned);
                if (ReferenceEquals(batch, __state.owned))
                {
                    batch = null;
                }
            }
            if (__state.start != 0)
            {
                Interlocked.Add(ref batchTicks, Stopwatch.GetTimestamp() - __state.start);
            }
            return __exception;
        }

        // An exception here reaches UploadBatch's catch, which frees the staging allocations of the open command
        // buffer: unmap first, like the original does before every free.
        public static Exception RecordFinalizer(Exception __exception, long __state)
        {
            if (__exception != null && batch != null)
            {
                batch.Failed = true;
                CloseAll(batch);
            }
            if (__state != 0)
            {
                Interlocked.Add(ref recordTicks, Stopwatch.GetTimestamp() - __state);
            }
            return __exception;
        }

        private static void CloseAll(Batch owned)
        {
            foreach (var arena in owned.Open)
            {
                if (arena.Mapped)
                {
                    arena.Mapped = false;
                    arena.Allocator.UnmapMemory(in arena.Map);
                    Interlocked.Add(ref arenaBytes, arena.Used);
                }
            }
            owned.Open.Clear();
            owned.Current = null;
        }

        public static unsafe bool CreatePrefix(MeshUploader __instance, string label, NativeByteBuffer source, VkBufferUsageFlags usage, CommandBuffer commandBuffer, List<BufferAllocationInfo> stagingAllocations, string trackingLabel, ref GpuMeshBuffer __result, out long __state)
        {
            __state = done ? 0 : Stopwatch.GetTimestamp();
            var current = batch;
            var length = source.LengthBytes;
            if (__state == 0 || length == 0)
            {
                return true;
            }
            Interlocked.Increment(ref streams);
            Interlocked.Add(ref streamBytes, length);
            if (current == null)
            {
                return true;
            }
            var need = Align(length);
            var wanted = Math.Min(Math.Max(current.Remaining, need), ArenaLimit);
            current.Remaining -= need;
            if (current.Failed || need > ArenaLimit)
            {
                fallbackStreams++;
                return true;
            }
            var arena = current.Current;
            if (arena == null || !ReferenceEquals(arena.List, stagingAllocations))
            {
                arena = Open(__instance, stagingAllocations, wanted);
                current.Current = arena;
                if (arena == null)
                {
                    current.Failed = true;
                    fallbackStreams++;
                    return true;
                }
                current.Open.Add(arena);
            }
            var offset = Align(arena.Used);
            if (!arena.Mapped || offset + length > arena.Capacity)
            {
                fallbackStreams++;
                return true;
            }
            var device = allocateMeshBuffer(__instance, label, length, usage, out var hostResident);
            if (hostResident)
            {
                trackingLabel = (trackingLabel ?? "Mesh") + "/Host";
            }
            var target = (byte*)arena.Pointer + offset;
            Buffer.MemoryCopy((void*)source.Pointer, target, length, length);
            arena.Used = offset + length;
            if (RLOMod.Verify && ((long)device.BufferSize.Value != length || !new ReadOnlySpan<byte>((void*)source.Pointer, length).SequenceEqual(new ReadOnlySpan<byte>(target, length))))
            {
                Interlocked.Increment(ref mismatches);
            }
            var region = new VkBufferCopy
            {
                SrcOffset = ByteSize.Of(offset),
                DstOffset = ByteSize.Zero,
                Size = device.BufferSize
            };
            commandBuffer.CopyBuffer(arena.Allocation.VkBuffer, device.VkBuffer, new ReadOnlySpan<VkBufferCopy>(ref region));
            var buffer = new GpuMeshBuffer();
            buffer.Initialize(device, (uint)length, usage, null, 0, trackingLabel);
            __result = buffer;
            arenaStreams++;
            return false;
        }

        // One host-visible, coherent transfer-source buffer with the exact memory request the original staging
        // buffers use (Waylith's ConvertInfo of PreferCpu + HostVisible|HostCoherent, priority 1). It goes into the
        // command buffer's staging list, so the engine frees it after that command buffer's fence like the others.
        // Allocated with plain vmaCreateBuffer: if it does not fit, nothing is reclaimed and the original path runs.
        private static Arena Open(MeshUploader owner, List<BufferAllocationInfo> stagingAllocations, long bytes)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var allocator = allocatorOf(owner);
                var info = new VkBufferCreateInfo
                {
                    Size = ByteSize.Of(bytes),
                    Usage = VkBufferUsageFlags.TransferSrcBit,
                    SharingMode = VkSharingMode.Exclusive
                };
                var request = new VmaAllocationCreateInfo
                {
                    Usage = VmaMemoryUsage.AutoPreferHost,
                    Flags = VmaAllocationCreateFlags.HostAccessRandomBit,
                    RequiredFlags = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                    PreferredFlags = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                    Priority = 1f,
                    MemoryTypeBits = 0u
                };
                if (allocator.CreateBuffer(info, request, out var vkBuffer, out var allocation, out var allocationInfo) != VkResult.Success)
                {
                    arenaFailures++;
                    return null;
                }
                var staging = new BufferAllocationInfo(vkBuffer, default, info.Size, info.Usage, new AllocationInfo
                {
                    Allocator = allocator,
                    Memory = allocationInfo.DeviceMemory,
                    MemoryTypeIndex = (int)allocationInfo.MemoryType,
                    AllocOffset = allocationInfo.Offset,
                    AllocSize = allocationInfo.Size,
                    AllocHandle = allocation.VkHandle
                });
                var map = new AllocationMapInfo(staging.AllocationInfo)
                {
                    Region = new MapRegion
                    {
                        Offset = ByteSize.Zero,
                        Size = staging.BufferSize
                    }
                };
                IntPtr pointer;
                try
                {
                    unsafe
                    {
                        pointer = (IntPtr)(void*)allocator.MapMemory(in map);
                    }
                }
                catch (Exception)
                {
                    allocator.FreeAllocation(in staging);
                    arenaFailures++;
                    return null;
                }
                GpuMemoryTracker.Add(GpuMemoryCategory.Staging, in staging, "Staging/Mesh");
                stagingAllocations.Add(staging);
                arenas++;
                arenaReserved += bytes;
                return new Arena { List = stagingAllocations, Allocation = staging, Map = map, Allocator = allocator, Pointer = pointer, Capacity = bytes, Mapped = true };
            }
            finally
            {
                arenaTicks += Stopwatch.GetTimestamp() - start;
            }
        }

        public static void CreatePostfix(long __state)
        {
            if (__state != 0)
            {
                Interlocked.Add(ref createTicks, Stopwatch.GetTimestamp() - __state);
            }
        }

        public static void AllocatePostfix(long __state)
        {
            if (__state != 0)
            {
                Interlocked.Add(ref deviceTicks, Stopwatch.GetTimestamp() - __state);
            }
        }

        public static void SubmitPostfix(long __state)
        {
            if (__state != 0)
            {
                Interlocked.Increment(ref leases);
                Interlocked.Add(ref submitTicks, Stopwatch.GetTimestamp() - __state);
            }
        }

        public static void FramePostfix(long __state)
        {
            if (__state != 0)
            {
                Interlocked.Add(ref frameTicks, Stopwatch.GetTimestamp() - __state);
            }
        }

        public static void MenuPostfix()
        {
            if (done || menuAt == 0 || Stopwatch.GetTimestamp() - menuAt < GraceSeconds * Stopwatch.Frequency)
            {
                return;
            }
            var owner = uploader;
            var sync = owner == null ? null : syncOf(owner);
            if (sync != null && !Monitor.TryEnter(sync))
            {
                return;
            }
            try
            {
                done = true;
                foreach (var method in patched)
                {
                    try
                    {
                        harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"[RLO] could not remove the startup patch on {method.DeclaringType?.FullName}.{method.Name}: {e.Message}");
                    }
                }
            }
            finally
            {
                if (sync != null)
                {
                    Monitor.Exit(sync);
                }
            }
            Log.Message($"[RLO] mesh uploads until {GraceSeconds:F0} s after the main menu: {Summary()}");
        }

        private static string Summary()
        {
            if (batches == 0)
            {
                return "none seen";
            }
            var staging = LeanFixes.Seconds(createTicks - deviceTicks);
            var arenaPart = !arenaEnabled
                ? $"staging buffer per command buffer {arenaState}"
                : $"staging buffer per command buffer on: {arenas} buffer(s), {arenaReserved / 1048576.0:F0} MB reserved in {LeanFixes.Seconds(arenaTicks):F2} s, {arenaStreams} streams ({arenaBytes / 1048576.0:F0} MB) copied through them, {fallbackStreams} streams on the original path, {arenaFailures} allocation failure(s)";
            var verify = RLOMod.Verify ? $"; verify: {mismatches} streams whose staged bytes or buffer size differ from the source" : "";
            return $"{drains} drain(s) {LeanFixes.Seconds(drainTicks):F2} s (longest {LeanFixes.Seconds(longestDrain):F2} s), {batches} batch(es) of {meshes} meshes {LeanFixes.Seconds(batchTicks):F2} s, {leases} command buffer(s); "
                + $"recording the meshes {LeanFixes.Seconds(recordTicks):F2} s, of which {streams} device-local streams ({streamBytes / 1048576.0:F0} MB) {LeanFixes.Seconds(createTicks):F2} s "
                + $"= device buffer allocation {LeanFixes.Seconds(deviceTicks):F2} s + staging (allocate, map, copy, unmap, record copy) {staging:F2} s; "
                + $"submits {LeanFixes.Seconds(submitTicks):F2} s, frame starts (free staging after the fence) {LeanFixes.Seconds(frameTicks):F2} s; {arenaPart}{verify}";
        }

        private static string Stats() => $"mesh uploads so far: {Summary()}";
    }
}
#endif
