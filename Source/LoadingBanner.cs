using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    [Hook]
    public static class LoadingBanner
    {
        private static readonly Color Background = new Color(0.08f, 0.16f, 0.12f, 0.92f);
        private static readonly Color Accent = new Color(0.45f, 0.9f, 0.6f);
        private static bool active;
        private static bool featuresOff;
        private static float refreshedAt = float.MinValue;
        private static string live = "", summary = "";

        public static void Install(Harmony harmony, bool off)
        {
            featuresOff = off;
            active = true;
            harmony.Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsOnGUI)), postfix: new HarmonyMethod(typeof(LoadingBanner), nameof(Draw)));
            MainMenu.Then(Hide);
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
                if (Time.realtimeSinceStartup - refreshedAt >= 0.5f)
                {
                    refreshedAt = Time.realtimeSinceStartup;
                    live = Live();
                    summary = Summary();
                }
                var height = summary.Length > 0 ? 74f : 54f;
                var bars = featuresOff ? new List<(string label, int done, int total)>() : Bars();
                height += bars.Count * 20f;
                Widgets.DrawBoxSolid(new Rect(0f, 0f, UI.screenWidth, height), Background);
                Widgets.DrawBoxSolid(new Rect(0f, height - 2f, UI.screenWidth, 2f), Accent);
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font = GameFont.Medium;
                GUI.color = featuresOff ? ColoredText.WarningColor : Accent;
                Widgets.Label(new Rect(0f, 2f, UI.screenWidth, 30f), featuresOff ? "<b>RIMWORLD LOADING OPTIMIZER IS OFF FOR THIS MEASUREMENT START</b>" : "<b>RIMWORLD LOADING OPTIMIZER ACTIVE</b>");
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Widgets.Label(new Rect(0f, 30f, UI.screenWidth, 20f), live);
                if (summary.Length > 0)
                {
                    GUI.color = new Color(0.8f, 0.85f, 0.9f);
                    Widgets.Label(new Rect(0f, 50f, UI.screenWidth, 20f), summary);
                }
                var top = summary.Length > 0 ? 72f : 52f;
                foreach (var (label, done, total) in bars)
                {
                    DrawBar(new Rect(UI.screenWidth * 0.25f, top + 3f, UI.screenWidth * 0.5f, 14f), label, done, total);
                    top += 20f;
                }
            }
            catch (System.Exception e)
            {
                active = false;
                Log.Warning($"[RLO] loading banner switched off: {e.Message}");
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = color;
            }
        }

        private static List<(string label, int done, int total)> Bars()
        {
            var bars = new List<(string label, int done, int total)>();
            if (EarlyContent.Total > 0)
            {
                bars.Add(("Mod content loaded early", EarlyContent.Done, EarlyContent.Total));
            }
            return bars;
        }

        private static void DrawBar(Rect rect, string label, int done, int total)
        {
            var fraction = total > 0 ? Mathf.Clamp01((float)done / total) : 0f;
            Widgets.DrawBoxSolid(rect, new Color(0.03f, 0.08f, 0.05f, 0.95f));
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width * fraction, rect.height), done >= total ? Accent : new Color(0.25f, 0.7f, 0.4f));
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.white;
            Widgets.Label(rect, $"{label}: {done:N0} / {total:N0}");
        }

        private static string Summary()
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
            return string.Join("  ·  ", parts);
        }

        private static string Live()
        {
            var parts = new List<string> { $"{Measurement.Duration(Time.realtimeSinceStartup)} since start" };
            if (featuresOff)
            {
                return parts[0];
            }
#if !NATIVE_ASSETS && !RW17
            var textures = DdsPrefetch.Hits + DdsPrefetch.Warmed;
            if (textures > 0)
            {
                parts.Add($"<b>{textures:N0}</b> textures read ahead ({DdsPrefetch.BytesRead >> 20:N0} MB)");
            }
#endif
            if (FastXPath.Hits > 0)
            {
                parts.Add($"<b>{FastXPath.Hits:N0}</b> XML patch queries answered fast");
            }
            if (HarmonyIdentityMap.Updates > 0)
            {
                parts.Add($"<b>{HarmonyIdentityMap.Updates:N0}</b> Harmony patches built faster");
            }
#if !NATIVE_ASSETS
            if (AtlasCache.Hits > 0)
            {
                parts.Add($"<b>{AtlasCache.Hits}</b> texture atlases loaded from cache");
            }
#endif
            return string.Join("   ·   ", parts);
        }
    }
}
