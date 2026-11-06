using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ThreadTimeline
    {
        private const int ThreadExtendedInfo = 5;
        private const int ThreadExtendedInfoCount = 28;
        private const int ThreadIdentifierInfo = 4;
        private const int ThreadIdentifierInfoCount = 6;

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedInfo
        {
            public ulong UserTime;
            public ulong SystemTime;
            public int CpuUsage, Policy, RunState, Flags, SleepTime, CurrentPriority, Priority, MaxPriority;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IdentifierInfo
        {
            public ulong ThreadId, ThreadHandle, DispatchQueue;
        }

        [DllImport("libSystem.dylib")] private static extern uint task_self_trap();
        [DllImport("libSystem.dylib")] private static extern int task_threads(uint task, out IntPtr threads, out uint count);
        [DllImport("libSystem.dylib")] private static extern int thread_info(uint thread, int flavor, out ExtendedInfo info, ref uint count);
        [DllImport("libSystem.dylib", EntryPoint = "thread_info")] private static extern int thread_identifier(uint thread, int flavor, out IdentifierInfo info, ref uint count);
        [DllImport("libSystem.dylib")] private static extern int mach_port_deallocate(uint task, uint name);
        [DllImport("libSystem.dylib")] private static extern int vm_deallocate(uint task, IntPtr address, UIntPtr size);
        [DllImport("libSystem.dylib")] private static extern int pthread_threadid_np(IntPtr thread, out ulong id);

        private static readonly StringBuilder Rows = new StringBuilder();
        private static readonly Dictionary<ulong, ulong> Last = new Dictionary<ulong, ulong>();
        private static volatile bool running;
        private static ulong loadingThread, mainThread;

        public static bool Wanted => Environment.GetEnvironmentVariable("RLO_THREAD_TIMELINE") == "1" && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static void Install()
        {
            pthread_threadid_np(IntPtr.Zero, out loadingThread);
            running = true;
            new Thread(Sample) { IsBackground = true, Name = "RLO thread timeline" }.Start();
            LongEventHandler.ExecuteWhenFinished(() => pthread_threadid_np(IntPtr.Zero, out mainThread));
            MainMenu.Then(Finish);
        }

        private static double Now => (DateTime.Now - (Measurement.ProcessStart ?? DateTime.Now)).TotalSeconds;

        private static void Sample()
        {
            var task = task_self_trap();
            while (running)
            {
                var t = Now;
                if (task_threads(task, out var list, out var count) == 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var port = (uint)Marshal.ReadInt32(list, i * 4);
                        uint n = ThreadIdentifierInfoCount;
                        uint m = ThreadExtendedInfoCount;
                        if (thread_identifier(port, ThreadIdentifierInfo, out var id, ref n) == 0 && thread_info(port, ThreadExtendedInfo, out var info, ref m) == 0)
                        {
                            var cpu = info.UserTime + info.SystemTime;
                            Last.TryGetValue(id.ThreadId, out var before);
                            if (cpu > before)
                            {
                                var length = info.Name == null ? 0 : Array.IndexOf(info.Name, (byte)0);
                                var name = info.Name == null ? "" : Encoding.ASCII.GetString(info.Name, 0, length < 0 ? info.Name.Length : length);
                                lock (Rows)
                                {
                                    Rows.Append(t.ToString("F2")).Append(',').Append(id.ThreadId).Append(',').Append(((cpu - before) / 1000000.0).ToString("F1")).Append(',').Append(name.Replace(',', ' ')).Append('\n');
                                }
                                Last[id.ThreadId] = cpu;
                            }
                        }
                        mach_port_deallocate(task, port);
                    }
                    vm_deallocate(task, list, (UIntPtr)(count * 4));
                }
                Thread.Sleep(100);
            }
        }

        private static void Finish()
        {
            running = false;
            try
            {
                var path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "thread-timeline.csv");
                lock (Rows)
                {
                    File.WriteAllText(path, $"# loading {loadingThread} main {mainThread}\nt,thread,cpu_ms,name\n" + Rows);
                }
                Log.Message($"[RLO] thread timeline written to {path}");
            }
            catch (Exception e)
            {
                Log.Warning("[RLO] thread timeline could not be written: " + e.Message);
            }
        }
    }
}
