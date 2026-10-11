using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ColonyGroupsTexture
    {
        public static bool Repaired;

        public static void Repair()
        {
            var type = AccessTools.TypeByName("TacticalGroups.GenMapUIOptimized");
            var field = type == null ? null : AccessTools.Field(type, "OverlayHealthTex");
            if (field == null || field.GetValue(null) is Texture2D existing && existing != null)
            {
                return;
            }
            field.SetValue(null, SolidColorMaterials.NewSolidColorTexture(new Color(1f, 0f, 0f, 0.25f)));
            Repaired = true;
            Log.Message("[RLO] Colony Groups health overlay texture was created off the main thread by another mod's patch; recreated it on the main thread.");
        }
    }
}
