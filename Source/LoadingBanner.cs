using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class LoadingBanner
    {
        private static readonly Color Background = new Color(0.08f, 0.16f, 0.12f, 0.92f);
        private static readonly Color Accent = new Color(0.45f, 0.9f, 0.6f);
        private static bool active;
        private static bool featuresOff;

        public static void Install(Harmony harmony, bool off)
        {
            featuresOff = off;
            active = true;
            harmony.Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsOnGUI)), postfix: new HarmonyMethod(typeof(LoadingBanner), nameof(Draw)));
            harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)), prefix: new HarmonyMethod(typeof(LoadingBanner), nameof(Hide)));
        }

        public static void Hide() => active = false;

        public static void Draw()
        {
            if (!active || !LongEventHandler.AnyEventNowOrWaiting)
            {
                return;
            }
            var font = Text.Font;
            var anchor = Text.Anchor;
            var color = GUI.color;
            try
            {
                var supported = featuresOff ? 0 : Compatibility.ExtraSupport().Count();
                var others = featuresOff ? 0 : Compatibility.Alongside().Count();
                var parts = new List<string>();
                if (supported > 0)
                {
                    parts.Add($"Extra support for <b>{supported}</b> {(supported == 1 ? "mod" : "mods")}");
                }
                if (others > 0)
                {
                    parts.Add($"running alongside <b>{others}</b> other {(others == 1 ? "optimizer" : "optimizers")}");
                }
                var summary = parts.Count > 0 ? string.Join("  ·  ", parts) + "  ·  full list in the log" : "";
                var height = summary.Length > 0 ? 74f : 54f;
                Widgets.DrawBoxSolid(new Rect(0f, 0f, UI.screenWidth, height), Background);
                Widgets.DrawBoxSolid(new Rect(0f, height - 2f, UI.screenWidth, 2f), Accent);
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font = GameFont.Medium;
                GUI.color = featuresOff ? ColoredText.WarningColor : Accent;
                Widgets.Label(new Rect(0f, 2f, UI.screenWidth, 30f), featuresOff ? "<b>RIMWORLD LOADING OPTIMIZER IS OFF FOR THIS MEASUREMENT START</b>" : "<b>RIMWORLD LOADING OPTIMIZER ACTIVE</b>");
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Widgets.Label(new Rect(0f, 30f, UI.screenWidth, 20f), Live());
                if (summary.Length > 0)
                {
                    GUI.color = new Color(0.8f, 0.85f, 0.9f);
                    Widgets.Label(new Rect(0f, 50f, UI.screenWidth, 20f), summary);
                }
            }
            catch
            {
                active = false;
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = color;
            }
        }

        private static string Live()
        {
            var parts = new List<string> { $"{Time.realtimeSinceStartup:F0} s since start" };
            if (featuresOff)
            {
                return parts[0];
            }
            var textures = DdsPrefetch.Hits + DdsPrefetch.Warmed;
            if (textures > 0)
            {
                parts.Add($"<b>{textures:N0}</b> textures read ahead ({DdsPrefetch.BytesRead >> 20:N0} MB)");
            }
            if (FastXPath.Hits > 0)
            {
                parts.Add($"<b>{FastXPath.Hits:N0}</b> XML patch queries answered fast");
            }
            if (HarmonyIdentityMap.Updates > 0)
            {
                parts.Add($"<b>{HarmonyIdentityMap.Updates:N0}</b> Harmony patches built faster");
            }
            if (AtlasCache.Hits > 0)
            {
                parts.Add($"<b>{AtlasCache.Hits}</b> texture atlases loaded from cache");
            }
            return string.Join("   ·   ", parts);
        }
    }
}
