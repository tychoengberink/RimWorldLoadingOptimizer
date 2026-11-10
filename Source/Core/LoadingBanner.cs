using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    [Hook]
    public static class LoadingBanner
    {
        private const float WindowMargin = 21f;
        private const float PanelPadding = 12f;
        private const float PanelWidth = 640f;
        private const float PanelTop = 17f;
        private const float BarHeight = 20f;
        private const float Gap = 4f;
        private static readonly Color BarColor = new Color(0.2f, 0.8f, 0.85f);
        private static readonly Color Faded = new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color Soft = new Color(1f, 1f, 1f, 0.8f);

        private static bool active;
        private static bool featuresOff;
        private static bool insideLoadingProgress;
        private static float refreshedAt = float.MinValue;
        private static string live = "",
            summary = "";
        private static List<(string label, int done, int total)> bars = new List<(string label, int done, int total)>();

        public static void Install(Harmony harmony, bool off)
        {
            featuresOff = off;
            active = true;
            HookLoadingProgress(harmony);
            harmony.Patch(
                AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsOnGUI)),
                postfix: new HarmonyMethod(typeof(LoadingBanner), nameof(DrawPanel))
            );
            MainMenu.Then(Hide);
        }

        private static void HookLoadingProgress(Harmony harmony)
        {
            var window = AccessTools.TypeByName("ilyvion.LoadingProgress.LoadingProgressWindow");
            var size = window == null ? null : AccessTools.PropertyGetter(window, "WindowSize");
            var contents = window == null ? null : AccessTools.Method(window, "DrawContents", new[] { typeof(Rect) });
            if (size == null || contents == null)
            {
                return;
            }
            harmony.Patch(size, postfix: new HarmonyMethod(typeof(LoadingBanner), nameof(AddSectionHeight)));
            harmony.Patch(contents, postfix: new HarmonyMethod(typeof(LoadingBanner), nameof(DrawInLoadingProgress)));
            insideLoadingProgress = true;
        }

        public static void Hide() => active = false;

        private static bool Showing => active && LongEventHandler.AnyEventNowOrWaiting;

        public static void AddSectionHeight(ref Vector2 __result)
        {
            if (Showing && insideLoadingProgress)
            {
                __result.y += StatusLineHeight();
            }
        }

        public static void DrawInLoadingProgress(Rect rect)
        {
            if (!Showing || !insideLoadingProgress)
            {
                return;
            }
            Guarded(
                "inside Loading Progress",
                () =>
                {
                    var height = StatusLineHeight();
                    DrawStatusLine(
                        new Rect(
                            rect.x + WindowMargin,
                            rect.yMax - WindowMargin - height,
                            rect.width - 2f * WindowMargin,
                            height
                        )
                    );
                },
                () => insideLoadingProgress = false
            );
        }

        public static void DrawPanel()
        {
            if (!Showing || insideLoadingProgress)
            {
                return;
            }
            Guarded(
                "panel",
                () =>
                {
                    var panel = new Rect(
                        (UI.screenWidth - PanelWidth) / 2f,
                        PanelTop,
                        PanelWidth,
                        SectionHeight() + 2f * PanelPadding
                    );
                    Widgets.DrawShadowAround(panel);
                    Widgets.DrawWindowBackground(panel);
                    DrawSection(panel.ContractedBy(PanelPadding));
                },
                Hide
            );
        }

        private static void Guarded(string where, Action draw, Action onError)
        {
            var font = Text.Font;
            var anchor = Text.Anchor;
            var color = GUI.color;
            try
            {
                draw();
            }
            catch (Exception e)
            {
                onError();
                RLOLog.Warning($"loading banner {where} switched off: {e.Message}");
            }
            finally
            {
                Text.Font = font;
                Text.Anchor = anchor;
                GUI.color = color;
            }
        }

        private static string Status =>
            featuresOff
                ? "RimWorld Loading Optimizer is off for this measurement start"
                : "RimWorld Loading Optimizer enabled";

        private static float StatusLineHeight() => Gap + Text.LineHeightOf(GameFont.Small);

        private static void DrawStatusLine(Rect rect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.LowerCenter;
            GUI.color = featuresOff ? ColoredText.WarningColor : Soft;
            Widgets.Label(rect, $"<b>{Status}</b>");
        }

        private static float SectionHeight()
        {
            Refresh();
            var line = Text.LineHeightOf(GameFont.Small);
            return line + Gap + line + bars.Count * (Gap + BarHeight);
        }

        private static void DrawSection(Rect rect)
        {
            var y = rect.y;
            Text.Font = GameFont.Small;
            var line = Text.LineHeight;
            var title = new Rect(rect.x, y, rect.width, line);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = featuresOff ? ColoredText.WarningColor : Color.white;
            Widgets.Label(title, Status);
            if (summary.Length > 0)
            {
                Text.Anchor = TextAnchor.UpperRight;
                GUI.color = Faded;
                Widgets.Label(title, summary);
            }
            y += line;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Soft;
            Widgets.Label(new Rect(rect.x, y + Gap, rect.width, line), live);
            y += Gap + line;
            foreach (var (label, done, total) in bars)
            {
                y += Gap;
                DrawBar(new Rect(rect.x, y, rect.width, BarHeight), label, done, total);
                y += BarHeight;
            }
        }

        private static void DrawBar(Rect rect, string label, int done, int total)
        {
            GUI.color = Color.gray;
            Widgets.DrawBox(rect.ContractedBy(1f));
            GUI.color = Color.white;
            var fill = rect.ContractedBy(2f);
            fill.width *= total > 0 ? Mathf.Clamp01((float)done / total) : 0f;
            Widgets.DrawBoxSolid(fill, BarColor);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, $"{label}: {done:N0} / {total:N0}");
        }

        private static void Refresh()
        {
            if (Time.realtimeSinceStartup - refreshedAt < 0.5f)
            {
                return;
            }
            refreshedAt = Time.realtimeSinceStartup;
            live = Live();
            summary = Summary();
            bars = featuresOff ? new List<(string label, int done, int total)>() : Bars();
        }

        private static List<(string label, int done, int total)> Bars()
        {
            var list = new List<(string label, int done, int total)>();
            if (EarlyContent.Total > 0)
            {
                list.Add(("Mod content loaded early", EarlyContent.Done, EarlyContent.Total));
            }
            return list;
        }

        private static string Summary()
        {
            var supported = featuresOff ? 0 : Compatibility.ExtraSupport().Count();
            var others = featuresOff ? 0 : Compatibility.Alongside().Count();
            var parts = new List<string>();
            if (supported > 0)
            {
                parts.Add($"extra support for {supported} {(supported == 1 ? "mod" : "mods")}");
            }
            if (others > 0)
            {
                parts.Add($"alongside {others} other {(others == 1 ? "optimizer" : "optimizers")}");
            }
            return string.Join(" · ", parts);
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
                parts.Add($"{textures:N0} textures read ahead ({DdsPrefetch.BytesRead >> 20:N0} MB)");
            }
#endif
            if (FastXPath.Hits > 0)
            {
                parts.Add($"{FastXPath.Hits:N0} XML patch queries answered fast");
            }
            if (HarmonyIdentityMap.Updates > 0)
            {
                parts.Add($"{HarmonyIdentityMap.Updates:N0} Harmony patches built faster");
            }
#if !NATIVE_ASSETS
            if (AtlasCache.Hits > 0)
            {
                parts.Add($"{AtlasCache.Hits} texture atlases loaded from cache");
            }
#endif
            return string.Join(" · ", parts);
        }
    }
}
