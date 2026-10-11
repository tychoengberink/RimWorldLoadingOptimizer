using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class XmlRelease
    {
        private static readonly PropertyInfo HardcodedMapping = AccessTools.Property(typeof(TKeySystem), "ShouldUseHardcodedMapping");
        private static readonly AccessTools.FieldRef<HashSet<XmlNode>> TreatAsList = AccessTools.StaticFieldRefAccess<HashSet<XmlNode>>(AccessTools.Field(typeof(TKeySystem), "treatAsList"));

        public static int Skipped;
        public static bool ReleasedXmlExtensionsDocument;
        public static int Cleared;

        public static void Install(Harmony harmony)
        {
            if (HardcodedMapping == null || TreatAsList == null)
            {
                Log.Warning("[RLO] TKeySystem layout changed; def XML release skipped");
                return;
            }
            harmony.Patch(AccessTools.Method(typeof(TKeySystem), nameof(TKeySystem.MarkTreatAsList)),
                prefix: new HarmonyMethod(typeof(XmlRelease), nameof(SkipWhenUnused)));
            harmony.Patch(AccessTools.Method(typeof(TKeySystem), nameof(TKeySystem.BuildMappings)),
                postfix: new HarmonyMethod(typeof(XmlRelease), nameof(ReleaseAfterMappings)));
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                finalizer: new HarmonyMethod(typeof(XmlRelease), nameof(ReleaseXmlExtensionsDocument)) { priority = Priority.Last });
        }

        public static Exception ReleaseXmlExtensionsDocument(Exception __exception)
        {
            try
            {
                var manager = AccessTools.Field(AccessTools.TypeByName("XmlExtensions.PatchManager"), "XmlDocs")?.GetValue(null);
                var main = manager == null ? null : AccessTools.Property(manager.GetType(), "MainDocument");
                if (main != null && main.GetValue(manager) != null)
                {
                    main.SetValue(manager, null);
                    ReleasedXmlExtensionsDocument = true;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] could not release XML Extensions' document reference: {e.Message}");
            }
            return __exception;
        }

        private static bool? usesHardcodedMapping;

        private static bool UsesHardcodedMapping => usesHardcodedMapping ?? (usesHardcodedMapping = (bool)HardcodedMapping.GetValue(null)).Value;

        public static bool SkipWhenUnused()
        {
            if (!UsesHardcodedMapping)
            {
                return true;
            }
            Skipped++;
            return false;
        }

        public static void ReleaseAfterMappings()
        {
            if (!UsesHardcodedMapping)
            {
                return;
            }
            var set = TreatAsList();
            Cleared += set.Count;
            set.Clear();
            set.TrimExcess();
        }
    }
}
