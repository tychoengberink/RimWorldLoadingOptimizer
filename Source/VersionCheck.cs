using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class VersionCheck
    {
        private static readonly List<SelfCheck> checks = new List<SelfCheck>();

        private static readonly bool forceSelfCheck = Environment.GetEnvironmentVariable("LEANMEMORY_SELFCHECK") == "1";

        private static readonly object trustGate = new object();
        private static HashSet<string> trusted;
        [ThreadStatic] private static string pendingKey;

        private static string TrustFile => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "verified-code.txt");

        public static bool Known(params (MethodBase method, string[] known)[] targets)
        {
            pendingKey = null;
            if (targets.Any(t => t.method == null))
            {
                return false;
            }
            if (!forceSelfCheck && targets.All(t => Fingerprint.MatchedHash(t.method, t.known) != null))
            {
                return true;
            }
            pendingKey = string.Join(";", targets.Select(t => Fingerprint.Normalized(t.method) ?? Fingerprint.Of(t.method)));
            return false;
        }

        internal static string TakePendingKey()
        {
            var key = pendingKey;
            pendingKey = null;
            return key;
        }

        internal static bool Trusted(string feature, string key)
        {
            if (forceSelfCheck || key == null)
            {
                return false;
            }
            lock (trustGate)
            {
                if (trusted == null)
                {
                    trusted = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        if (File.Exists(TrustFile))
                        {
                            trusted.UnionWith(File.ReadAllLines(TrustFile));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                return trusted.Contains(feature + "\t" + key);
            }
        }

        internal static void Remember(string feature, string key)
        {
            if (forceSelfCheck || key == null)
            {
                return;
            }
            var line = feature + "\t" + key;
            lock (trustGate)
            {
                try
                {
                    if (trusted != null && !trusted.Add(line))
                    {
                        return;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(TrustFile));
                    File.AppendAllText(TrustFile, line + Environment.NewLine);
                }
                catch (Exception)
                {
                }
            }
        }

        public static bool Pattern(string feature, MethodBase method, Func<List<CodeInstruction>, bool> matches, string expectation)
        {
            if (method == null)
            {
                Skip(feature, "a method it needs no longer exists");
                return false;
            }
            List<CodeInstruction> code;
            try
            {
                code = PatchProcessor.GetOriginalInstructions(method);
            }
            catch (Exception e)
            {
                Skip(feature, $"{Where(method)} could not be read ({e.GetType().Name})");
                return false;
            }
            if (!matches(code))
            {
                Skip(feature, $"{Where(method)} no longer {expectation}");
                return false;
            }
            return true;
        }

        public static int Calls(List<CodeInstruction> code, MethodBase target) =>
            code.Count(i => i.operand is MethodBase m && m == target && (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt));

        internal static void Register(SelfCheck check)
        {
            lock (checks)
            {
                checks.Add(check);
            }
        }

        internal static void Skip(string feature, string reason)
        {
            lock (Fingerprint.Changed)
            {
                if (!Fingerprint.Changed.Contains(feature))
                {
                    Fingerprint.Changed.Add(feature);
                }
            }
            Log.Message($"[RLO] {feature}: {reason}. This speedup is skipped and the original code runs; nothing is broken.");
        }

        internal static string Where(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}";

        public static string Stats()
        {
            lock (checks)
            {
                if (checks.Count == 0)
                {
                    return "self-checks: none needed (all code versions known)";
                }
                return "self-checks: " + string.Join(", ", checks.Select(c => c.Summary));
            }
        }
    }

    public sealed class SelfCheck
    {
        private const int Checking = 0;
        private const int Trusted = 1;
        private const int Off = 2;

        private readonly string feature;
        private readonly int needed;
        private int matched;
        private int state;
        private string difference;
        private readonly string key;

        public SelfCheck(string feature, bool known, int needed = 200)
        {
            this.feature = feature;
            this.needed = needed;
            key = known ? null : VersionCheck.TakePendingKey();
            state = known || VersionCheck.Trusted(feature, key) ? Trusted : Checking;
            if (state == Checking)
            {
                VersionCheck.Register(this);
                Log.Message($"[RLO] {feature}: this code version is not in RimWorld Loading Optimizer's list; the first {needed} results are compared with the original code before the speedup takes over.");
            }
        }

        public bool IsOff => Volatile.Read(ref state) == Off;

        public bool IsChecking => Volatile.Read(ref state) == Checking;

        public void Report(bool same, string what = null)
        {
            if (same)
            {
                if (Volatile.Read(ref state) == Checking && Interlocked.Increment(ref matched) >= needed
                    && Interlocked.CompareExchange(ref state, Trusted, Checking) == Checking)
                {
                    VersionCheck.Remember(feature, key);
                }
                return;
            }
            if (Interlocked.Exchange(ref state, Off) != Off)
            {
                difference = what;
                VersionCheck.Skip(feature, $"a result differed from the original code{(what == null ? "" : $" ({what})")}");
            }
        }

        internal string Summary
        {
            get
            {
                switch (Volatile.Read(ref state))
                {
                    case Trusted:
                        return $"{feature} passed ({needed} results compared)";
                    case Off:
                        return $"{feature} switched off after {matched} matching results ({difference ?? "result differed"})";
                    default:
                        return $"{feature} still checking ({matched} of {needed} results compared)";
                }
            }
        }
    }
}
