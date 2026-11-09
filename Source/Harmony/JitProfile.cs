#if RW17
using System;
using System.IO;
using System.Runtime;
using System.Threading.Tasks;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class JitProfile
    {
        private const string Name = "startup.jitprofile";

        private static string state;
        private static string file;
        private static DateTime started;

        public static void Install()
        {
            var root = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer");
            Directory.CreateDirectory(root);
            file = Path.Combine(root, Name);
            var existing = File.Exists(file) ? new FileInfo(file).Length : -1;
            started = DateTime.UtcNow;
            ProfileOptimization.SetProfileRoot(root);
            ProfileOptimization.StartProfile(Name);
            state = existing < 0
                ? "recording a first profile, used from the next start"
                : $"replaying last start's profile ({existing / 1024} KB) on a background thread and recording a new one";
            MainMenu.Then(Stop);
            StartupPhaseFixes.Reports.Add(Stats);
        }

        private static void Stop()
        {
            Task.Run(() =>
            {
                try
                {
                    ProfileOptimization.StartProfile(null);
                    if (DevSwitches.Verify)
                    {
                        var info = new FileInfo(file);
                        RLOLog.Message(info.Exists && info.LastWriteTimeUtc >= started
                            ? $"background JIT profile verify: profile written at the main menu ({info.Length / 1024} KB)"
                            : "background JIT profile verify: the runtime did not write a profile (another mod may have set the profile folder first, or this system has a single core)");
                    }
                }
                catch (Exception e)
                {
                    RLOLog.Message($"background JIT profile: could not stop recording: {e.Message}");
                }
            });
        }

        private static string Stats() => $"background JIT profile: {state}; JIT so far {JitInfo.GetCompiledMethodCount():N0} methods, {JitInfo.GetCompilationTime().TotalSeconds:F1} s summed over all threads";
    }
}
#endif
