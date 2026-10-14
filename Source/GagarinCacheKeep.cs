using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    // Missile Girl rebuilds its XML cache whenever a file changed, but two of its own steps make the next start cold too:
    // - turning the cache off deletes ModList.xml and nothing writes it back, so the next start reads "Mod list changed"
    //   and throws away the cache this start just built;
    // - after a mod-list switch the hash file keeps the hashes of files that are no longer loaded, so the next start
    //   reads "Total number of files changed" and rebuilds again.
    // When this start rebuilt the cache, this drops the hashes of files that were not loaded this start and writes
    // ModList.xml again, both with Missile Girl's own Dump methods and the values Missile Girl itself collected this start.
    // Missile Girl still checks every file hash on the next start, so any real change still rebuilds the cache.
    public static class GagarinCacheKeep
    {
        private const string Feature = "Missile Girl cache kept after a rebuild";
        private static Func<bool> usingCache;
        private static Func<ICollection<string>> assets;
        private static Func<IDictionary> hashes, hashesInt;
        private static Func<object> runningMods;
        private static Func<string> modListPath, unifiedPath, hashPath, hashIntPath;
        private static MethodInfo dumpModList, dumpHashes, dumpHashesInt;
        private static bool rebuilt, pruned, modListWritten;
        private static int prunedCount;

        public static void Install(Harmony harmony)
        {
            var context = AccessTools.TypeByName("Gagarin.Context");
            var env = AccessTools.TypeByName("Gagarin.GagarinEnvironmentInfo");
            var startup = AccessTools.TypeByName("Gagarin.StartupHelper");
            var modSet = AccessTools.TypeByName("Gagarin.RunningModsSetUtility");
            var hashing = AccessTools.TypeByName("Gagarin.AssetHashingUtility");
            if (context == null || env == null || startup == null || modSet == null || hashing == null)
            {
                return;
            }
            var finished = AccessTools.Method(startup, "StartUpFinished");
            var setter = AccessTools.PropertySetter(context, "IsUsingCache");
            dumpModList = AccessTools.Method(modSet, "Dump");
            var dump = AccessTools.Method(hashing, "Dump");
            if (!Fingerprint.Matches(Feature,
                    Fingerprint.Known(finished, "CA1105A53C1647D9"),
                    Fingerprint.Known(setter, "3BCC30CD5478812F"),
                    Fingerprint.Known(dumpModList, "6C3D6DF3389BDF7F"),
                    Fingerprint.Known(dump, "079CE9B6920F85D9"),
                    Fingerprint.Known(AccessTools.PropertyGetter(env, "CacheExists"), "D34A16C7739F5EB3")))
            {
                return;
            }
            dumpHashes = dump.MakeGenericMethod(typeof(string));
            dumpHashesInt = dump.MakeGenericMethod(typeof(ulong));
            Func<T> Static<T>(Type type, string name)
            {
                var property = AccessTools.Property(type, name);
                var field = property == null ? AccessTools.Field(type, name) : null;
                if (property == null && field == null)
                {
                    throw new MissingMemberException(type.FullName, name);
                }
                return () => (T)(property != null ? property.GetValue(null) : field.GetValue(null));
            }
            usingCache = Static<bool>(context, "IsUsingCache");
            assets = Static<ICollection<string>>(context, "Assets");
            hashes = Static<IDictionary>(context, "AssetsHashes");
            hashesInt = Static<IDictionary>(context, "AssetsHashesInt");
            runningMods = Static<object>(context, "RunningMods");
            modListPath = Static<string>(env, "ModListFilePath");
            unifiedPath = Static<string>(env, "UnifiedXmlFilePath");
            hashPath = Static<string>(env, "HashFilePath");
            hashIntPath = Static<string>(env, "HashFilePathInt");
            harmony.Patch(finished, prefix: new HarmonyMethod(typeof(GagarinCacheKeep), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(GagarinCacheKeep), nameof(Postfix)) { priority = Priority.Last });
            LeanFixes.Reports.Add(Stats);
        }

        public static void Prefix()
        {
            try
            {
                rebuilt = !usingCache();
                if (!rebuilt)
                {
                    return;
                }
                var loaded = new HashSet<string>(assets());
                var table = hashes();
                var tableInt = hashesInt();
                if (loaded.Count == 0)
                {
                    return;
                }
                lock (table)
                {
                    var stale = table.Keys.Cast<string>().Where(k => !loaded.Contains(k)).ToList();
                    var staleInt = tableInt.Keys.Cast<string>().Where(k => !loaded.Contains(k)).ToList();
                    if (stale.Count == 0 && staleInt.Count == 0)
                    {
                        return;
                    }
                    foreach (var key in stale)
                    {
                        table.Remove(key);
                    }
                    foreach (var key in staleInt)
                    {
                        tableInt.Remove(key);
                    }
                    prunedCount = Math.Max(stale.Count, staleInt.Count);
                    dumpHashes.Invoke(null, new object[] { table, hashPath() });
                    dumpHashesInt.Invoke(null, new object[] { tableInt, hashIntPath() });
                    pruned = true;
                }
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not tidy Missile Girl's hash file ({e.GetType().Name}: {e.Message}); Missile Girl behaves as usual.");
            }
        }

        public static void Postfix()
        {
            try
            {
                if (!rebuilt || File.Exists(modListPath()) || !File.Exists(unifiedPath()) || !File.Exists(hashPath()) || !File.Exists(hashIntPath()))
                {
                    return;
                }
                dumpModList.Invoke(null, new[] { runningMods(), modListPath() });
                modListWritten = true;
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not write Missile Girl's mod list ({e.GetType().Name}: {e.Message}); the next start may be cold.");
            }
        }

        public static string Stats() => !rebuilt
            ? $"{Feature}: Missile Girl used its cache, nothing to do"
            : $"{Feature}: cache rebuilt this start; {(pruned ? $"{prunedCount} hashes of files not loaded any more dropped" : "no stale hashes")}, {(modListWritten ? "mod list written again so the next start can use the new cache" : "mod list left as Missile Girl wrote it")}";
    }
}
