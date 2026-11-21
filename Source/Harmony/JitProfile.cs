#if RW17
using System;
using System.IO;
using System.Runtime;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class JitProfile
    {
        private const int PerProfile = 500;
        private const int Limit = 16384;
        private const uint HeaderRecord = 0x01000040;

        private static readonly object gate = new object();
        private static readonly byte[] header = new byte[24];
        private static string root;
        private static string file;
        private static DateTime started;
        private static bool stopped;
        private static int phase = 1;
        private static int constructors;
        private static int profiles, replayed, full;
        private static long methods;
        private static TimeSpan loader;

        public static void Install()
        {
            root = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer");
            Directory.CreateDirectory(root);
            File.Delete(Path.Combine(root, "startup.jitprofile"));
            started = DateTime.UtcNow;
            ProfileOptimization.SetProfileRoot(root);
            Next();
            MainMenu.Then(Stop);
            StartupPhaseFixes.Reports.Add(Stats);
        }

        public static void Hook(Harmony harmony)
        {
            if (root == null)
            {
                return;
            }
            GcPhases.Hook(harmony);
            GcPhases.PhaseStarted += OnPhase;
            StaticConstructorTracker.InstallEarly(harmony);
            StaticConstructorTracker.Started += OnStaticConstructor;
        }

        private static void OnPhase(int next)
        {
            lock (gate)
            {
                if (!UnityData.IsInMainThread)
                {
                    loader = JitInfo.GetCompilationTime(true);
                }
                phase = next;
                constructors = 0;
                Next();
            }
        }

        private static void OnStaticConstructor(Type type)
        {
            lock (gate)
            {
                if (++constructors % PerProfile == 0)
                {
                    Next();
                }
            }
        }

        private static void Next()
        {
            if (stopped)
            {
                return;
            }
            var name = $"startup-{phase}-{constructors / PerProfile}.jitprofile";
            file = Path.Combine(root, name);
            Count();
            ProfileOptimization.StartProfile(name);
            profiles++;
        }

        private static void Count()
        {
            if (!File.Exists(file))
            {
                return;
            }
            using (var stream = File.OpenRead(file))
            {
                if (stream.Read(header, 0, header.Length) < header.Length || BitConverter.ToUInt32(header, 0) != HeaderRecord)
                {
                    return;
                }
            }
            var count = BitConverter.ToInt32(header, 16);
            replayed++;
            methods += count;
            if (count + BitConverter.ToInt32(header, 20) >= Limit)
            {
                full++;
            }
        }

        private static void Stop()
        {
            string last;
            lock (gate)
            {
                stopped = true;
                last = file;
            }
            Task.Run(() =>
            {
                try
                {
                    ProfileOptimization.StartProfile(null);
                    if (DevSwitches.Verify)
                    {
                        var info = new FileInfo(last);
                        RLOLog.Detail(info.Exists && info.LastWriteTimeUtc >= started
                            ? $"background JIT profile verify: last profile written at the main menu ({info.Length / 1024} KB)"
                            : "background JIT profile verify: the runtime did not write a profile (another mod may have set the profile folder first, or this system has a single core)");
                    }
                }
                catch (Exception e)
                {
                    RLOLog.Detail($"background JIT profile: could not stop recording: {e.Message}");
                }
            });
        }

        private static string Stats() => $"background JIT profile: {profiles} profiles in a row so far (one per loading phase and per {PerProfile} static constructors), {replayed} of them replayed from the last start on a background thread with {methods:N0} methods, {full} at the runtime's limit of {Limit:N0} records per profile; JIT so far {JitInfo.GetCompiledMethodCount():N0} methods, {JitInfo.GetCompilationTime().TotalSeconds:F1} s summed over all threads, {JitInfo.GetCompilationTime(true).TotalSeconds:F1} s on this thread and {loader.TotalSeconds:F1} s on the loading thread until it resolved references";
    }
}
#endif
