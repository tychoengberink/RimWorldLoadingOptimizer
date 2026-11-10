using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    [Hook]
    public static class MainMenu
    {
        private static readonly List<Action> actions = new List<Action>();
        private static readonly List<MethodBase> startupOnly = new List<MethodBase>();
        private static Harmony harmony;

        public static bool Reached { get; private set; }

        public static void Install(Harmony h)
        {
            harmony = h;
            harmony.Patch(
                AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)),
                prefix: new HarmonyMethod(typeof(MainMenu), nameof(Prefix)) { priority = Priority.First }
            );
            var gameStart = new HarmonyMethod(typeof(MainMenu), nameof(GameStart));
            harmony.Patch(
                AccessTools.Method(typeof(SavedGameLoaderNow), nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow)),
                prefix: gameStart
            );
            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)), prefix: gameStart);
        }

        public static void GameStart()
        {
            if (!Reached)
            {
                LongEventHandler.ExecuteWhenFinished(Prefix);
            }
        }

        public static void Then(Action action)
        {
            lock (actions)
            {
                actions.Add(action);
            }
        }

        public static void UnpatchAfterStartup(MethodBase method)
        {
            if (method != null)
            {
                startupOnly.Add(method);
            }
        }

        public static void Prefix()
        {
            Action[] pending;
            lock (actions)
            {
                if (Reached)
                {
                    return;
                }
                Reached = true;
                pending = actions.ToArray();
            }
            foreach (var action in pending)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    RLOLog.Warning($"main menu step failed: {e}");
                }
            }
            if (DevSwitches.KeepStartupPatches)
            {
                return;
            }
            foreach (var method in startupOnly)
            {
                try
                {
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                }
                catch (Exception e)
                {
                    RLOLog.Warning(
                        $"could not remove the startup patch on {method.DeclaringType?.FullName}.{method.Name}: {e.Message}"
                    );
                }
            }
        }
    }
}
