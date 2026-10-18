#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Waylith.Graphics.Meshes;

namespace RimWorldLoadingOptimizer
{
    [Hook]
    public static class MeshUploadTiming
    {
        private const double GraceSeconds = 10;

        private static Harmony harmony;
        private static bool done;
        private static long startupAt;

        [ThreadStatic]
        private static int batchesAtDrain;

        private static int drains, batches, meshes, leases, streams;
        private static long drainTicks, longestDrain, batchTicks, recordTicks, createTicks, deviceTicks, submitTicks, frameTicks;
        private static long streamBytes;

        public static void Install(Harmony h)
        {
            harmony = h;
            var type = typeof(MeshUploader);
            var drain = AccessTools.Method(type, nameof(MeshUploader.DrainPendingUploads));
            var uploadBatch = AccessTools.Method(type, "UploadBatch");
            var record = AccessTools.Method(type, "RecordMeshUpload");
            var create = AccessTools.Method(type, "CreateDeviceLocalBuffer");
            var allocate = AccessTools.Method(type, "AllocateMeshBuffer");
            var submit = AccessTools.Method(type, "SubmitUploadCommandBuffer");
            var beginFrame = AccessTools.Method(type, nameof(MeshUploader.BeginFrame));
            if (drain == null || uploadBatch == null || record == null || create == null || allocate == null || submit == null || beginFrame == null)
            {
                Log.Message("[RLO] mesh uploads: the engine's mesh uploader was not found; skipped");
                return;
            }
            Patch(drain, nameof(DrainPrefix), nameof(DrainPostfix));
            Patch(uploadBatch, nameof(BatchPrefix), nameof(BatchPostfix));
            Patch(record, nameof(TimePrefix), nameof(RecordPostfix));
            Patch(create, nameof(CreatePrefix), nameof(CreatePostfix));
            Patch(allocate, nameof(TimePrefix), nameof(AllocatePostfix));
            Patch(submit, nameof(TimePrefix), nameof(SubmitPostfix));
            Patch(beginFrame, nameof(TimePrefix), nameof(FramePostfix));
            harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)), postfix: new HarmonyMethod(typeof(MeshUploadTiming), nameof(CheckEnd)));
            LeanFixes.Reports.Add(Stats);
        }

        private static void Patch(MethodBase method, string prefix, string postfix)
        {
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(MeshUploadTiming), prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(MeshUploadTiming), postfix));
        }

        public static void TimePrefix(out long __state) => __state = done ? 0 : Stopwatch.GetTimestamp();

        public static void DrainPrefix(out long __state)
        {
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

        public static void BatchPrefix(IReadOnlyList<Mesh> meshes, out long __state)
        {
            CheckEnd();
            __state = done ? 0 : Stopwatch.GetTimestamp();
            if (__state != 0)
            {
                Interlocked.Increment(ref batches);
                Interlocked.Add(ref MeshUploadTiming.meshes, meshes?.Count ?? 0);
            }
        }

        public static void BatchPostfix(long __state) => Add(ref batchTicks, __state);

        public static void RecordPostfix(long __state) => Add(ref recordTicks, __state);

        public static void CreatePrefix(NativeByteBuffer source, out long __state)
        {
            __state = done ? 0 : Stopwatch.GetTimestamp();
            var length = source?.LengthBytes ?? 0;
            if (__state != 0 && length > 0)
            {
                Interlocked.Increment(ref streams);
                Interlocked.Add(ref streamBytes, length);
            }
        }

        public static void CreatePostfix(long __state) => Add(ref createTicks, __state);

        public static void AllocatePostfix(long __state) => Add(ref deviceTicks, __state);

        public static void SubmitPostfix(long __state)
        {
            if (__state != 0)
            {
                Interlocked.Increment(ref leases);
            }
            Add(ref submitTicks, __state);
        }

        public static void FramePostfix(long __state)
        {
            Add(ref frameTicks, __state);
            CheckEnd();
        }

        private static void Add(ref long total, long start)
        {
            if (start != 0)
            {
                Interlocked.Add(ref total, Stopwatch.GetTimestamp() - start);
            }
        }

        public static void CheckEnd()
        {
            if (done)
            {
                return;
            }
            if (startupAt == 0 && (MainMenu.Reached || Current.ProgramState != ProgramState.Entry))
            {
                startupAt = Stopwatch.GetTimestamp();
            }
            if (startupAt != 0 && Stopwatch.GetTimestamp() - startupAt >= GraceSeconds * Stopwatch.Frequency)
            {
                done = true;
                Log.Message($"[RLO] mesh uploads until {GraceSeconds:F0} s after startup: {Summary()}");
            }
        }

        private static string Summary()
        {
            if (batches == 0)
            {
                return "none seen";
            }
            var staging = LeanFixes.Seconds(createTicks - deviceTicks);
            return $"{drains} drain(s) {LeanFixes.Seconds(drainTicks):F2} s (longest {LeanFixes.Seconds(longestDrain):F2} s), {batches} batch(es) of {meshes} meshes {LeanFixes.Seconds(batchTicks):F2} s, {leases} command buffer(s); "
                + $"recording the meshes {LeanFixes.Seconds(recordTicks):F2} s, of which {streams} device-local streams ({streamBytes / 1048576.0:F0} MB) {LeanFixes.Seconds(createTicks):F2} s "
                + $"= device buffer allocation {LeanFixes.Seconds(deviceTicks):F2} s + staging (allocate, map, copy, unmap, record copy) {staging:F2} s; "
                + $"submits {LeanFixes.Seconds(submitTicks):F2} s, frame starts {LeanFixes.Seconds(frameTicks):F2} s";
        }

        private static string Stats() => $"mesh uploads so far: {Summary()}";
    }
}
#endif
