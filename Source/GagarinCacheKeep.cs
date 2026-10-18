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
        private static MethodInfo dumpModList, dumpHashes, dumpHashesInt, changed, loadHashes, loadHashesInt;
        private static bool rebuilt, pruned, modListWritten;
        private static int prunedCount;

        private static Harmony harmony;
        private static bool attempted;

        // Missile Girl loads Gagarin.dll itself after RimWorld Loading Optimizer's constructor, so the patch is installed
        // when the defs are parsed (StartUpFinished runs later, with the static constructors).
        public static void Install(Harmony harmony)
        {
            GagarinCacheKeep.harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ParseAndProcessXML)), prefix: new HarmonyMethod(typeof(GagarinCacheKeep), nameof(ParsePrefix)));
        }

        public static void ParsePrefix()
        {
            if (attempted)
            {
                return;
            }
            attempted = true;
            RLOMod.Try(Feature, true, InstallNow);
        }

        private static void InstallNow()
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
            changed = AccessTools.Method(modSet, "Changed", new[] { typeof(List<string>), typeof(string) });
            loadHashes = AccessTools.Method(hashing, "Load", new[] { typeof(string) });
            loadHashesInt = AccessTools.Method(hashing, "LoadInt", new[] { typeof(string) });
            if (finished == null || dumpModList == null || dump == null || changed == null || loadHashes == null || loadHashesInt == null)
            {
                VersionCheck.Skip(Feature, "a method it needs no longer exists");
                return;
            }
            var modListGetter = AccessTools.PropertyGetter(env, "ModListFilePath");
            var unifiedGetter = AccessTools.PropertyGetter(env, "UnifiedXmlFilePath");
            var delete = AccessTools.Method(typeof(File), nameof(File.Delete), new[] { typeof(string) });
            if (!VersionCheck.Pattern(Feature, setter, code => VersionCheck.Calls(code, delete) >= 2 && VersionCheck.Calls(code, modListGetter) > 0 && VersionCheck.Calls(code, unifiedGetter) > 0,
                    "deletes the mod list and the cache file when the cache is turned off"))
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
                    var path = hashPath();
                    var pathInt = hashIntPath();
                    var before = File.ReadAllBytes(path);
                    var beforeInt = File.ReadAllBytes(pathInt);
                    dumpHashes.Invoke(null, new object[] { table, path });
                    dumpHashesInt.Invoke(null, new object[] { tableInt, pathInt });
                    if (!Same(table, (IDictionary)loadHashes.Invoke(null, new object[] { path })) || !Same(tableInt, (IDictionary)loadHashesInt.Invoke(null, new object[] { pathInt })))
                    {
                        File.WriteAllBytes(path, before);
                        File.WriteAllBytes(pathInt, beforeInt);
                        VersionCheck.Skip(Feature, "Missile Girl did not read its hash file back as written");
                        return;
                    }
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
                var path = modListPath();
                var mods = runningMods();
                dumpModList.Invoke(null, new[] { mods, path });
                var ids = ((IEnumerable<ModContentPack>)mods).Select(m => m.PackageId).ToList();
                if ((bool)changed.Invoke(null, new object[] { ids, path }))
                {
                    File.Delete(path);
                    VersionCheck.Skip(Feature, "Missile Girl did not read its mod list back as written");
                    return;
                }
                modListWritten = true;
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not write Missile Girl's mod list ({e.GetType().Name}: {e.Message}); the next start may be cold.");
            }
        }

        private static bool Same(IDictionary written, IDictionary read)
        {
            if (read == null || read.Count != written.Count)
            {
                return false;
            }
            foreach (DictionaryEntry pair in written)
            {
                if (!read.Contains(pair.Key) || !Equals(read[pair.Key], pair.Value))
                {
                    return false;
                }
            }
            return true;
        }

        public static string Stats() => !rebuilt
            ? $"{Feature}: Missile Girl used its cache, nothing to do"
            : $"{Feature}: cache rebuilt this start; {(pruned ? $"{prunedCount} hashes of files not loaded any more dropped" : "no stale hashes")}, {(modListWritten ? "mod list written again so the next start can use the new cache" : "mod list left as Missile Girl wrote it")}";
    }
}
