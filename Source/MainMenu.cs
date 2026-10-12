using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class MainMenu
    {
        private static readonly List<Action> actions = new List<Action>();
        private static readonly List<MethodBase> startupOnly = new List<MethodBase>();
        private static Harmony harmony;

        public static bool Reached { get; private set; }

        public static void Install(Harmony h)
        {
            harmony = h;
            harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)),
                prefix: new HarmonyMethod(typeof(MainMenu), nameof(Prefix)) { priority = Priority.First });
        }

        public static void Then(Action action) => actions.Add(action);

        public static void UnpatchAfterStartup(MethodBase method)
        {
            if (method != null)
            {
                startupOnly.Add(method);
            }
        }

        public static void Prefix()
        {
            if (Reached)
            {
                return;
            }
            Reached = true;
            foreach (var action in actions)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] main menu step failed: {e}");
                }
            }
            foreach (var method in startupOnly)
            {
                try
                {
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] could not remove the startup patch on {method.DeclaringType?.FullName}.{method.Name}: {e.Message}");
                }
            }
        }
    }
}
