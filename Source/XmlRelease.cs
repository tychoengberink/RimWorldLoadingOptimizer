using System;
using System.Collections;
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
            installed = true;
#if RW17
            var bake = AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases));
            if (bake != null)
            {
                harmony.Patch(bake, prefix: new HarmonyMethod(typeof(XmlRelease), nameof(ReleaseXmlExtensionsDocument)));
            }
#endif
        }

        private static bool installed;

        [StaticConstructorOnStartup]
        public static class AfterStaticConstructors
        {
            static AfterStaticConstructors()
            {
                if (installed)
                {
                    LongEventHandler.ExecuteWhenFinished(ReleaseXmlExtensionsDocument);
                }
            }
        }

        [Hook]
        private static void ReleaseXmlExtensionsDocument()
        {
            try
            {
                var manager = AccessTools.Field(AccessTools.TypeByName("XmlExtensions.PatchManager"), "XmlDocs")?.GetValue(null);
                var main = manager == null ? null : AccessTools.Property(manager.GetType(), "MainDocument");
                if (main == null || main.GetValue(manager) == null || SettingsReadDocument(manager.GetType().Assembly))
                {
                    return;
                }
                main.SetValue(manager, null);
                ReleasedXmlExtensionsDocument = true;
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] could not release XML Extensions' document reference: {e.Message}");
            }
        }

        private static bool SettingsReadDocument(Assembly xmlExtensions)
        {
            var text = xmlExtensions.GetType("XmlExtensions.Setting.Text");
            var xpath = text == null ? null : AccessTools.Field(text, "xpath");
            var menuDef = xmlExtensions.GetType("XmlExtensions.SettingsMenuDef");
            if (xpath == null || menuDef == null)
            {
                return true;
            }
            var seen = new HashSet<object>(ReferenceComparer<object>.Instance);
            var pending = new Stack<object>(GenDefDatabase.GetAllDefsInDatabaseForDef(menuDef));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == null || current is string || !seen.Add(current))
                {
                    continue;
                }
                if (text.IsInstanceOfType(current) && xpath.GetValue(current) != null)
                {
                    return true;
                }
                if (current is IEnumerable items)
                {
                    foreach (var item in items)
                    {
                        pending.Push(item);
                    }
                    continue;
                }
                for (var type = current.GetType(); type != null && type.Assembly == xmlExtensions; type = type.BaseType)
                {
                    foreach (var field in AccessTools.GetDeclaredFields(type))
                    {
                        if (!field.IsStatic && !field.FieldType.IsValueType)
                        {
                            pending.Push(field.GetValue(current));
                        }
                    }
                }
            }
            return false;
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
