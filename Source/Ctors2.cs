using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Ctors2Mod
    {
        public static readonly bool Verify = RLOMod.Verify || Environment.GetEnvironmentVariable("CTORS2_VERIFY") == "1";
        private static readonly string[] Off = (Environment.GetEnvironmentVariable("CTORS2_OFF") ?? "").Split(',').Select(s => s.Trim().ToLowerInvariant()).ToArray();
        public static Harmony Harmony;
        public static readonly List<Func<string>> Reports = new List<Func<string>>();
        private static bool reported;
        private static bool vanillaDone;

        public static bool Enabled(string fix) => !Off.Contains(fix) && !Off.Contains("all");

        public static void Install(Harmony harmony)
        {
            Harmony = harmony;
            if (Enabled("bracket") || Timing.On)
            {
                CtorBracket.InstallEarly(Harmony);
            }
            Timing.Install();
            Harmony.Patch(AccessTools.Method(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll)),
                prefix: new HarmonyMethod(typeof(Ctors2Mod), nameof(BeforeVanillaCallAll)) { priority = Priority.First });
            Harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)), prefix: new HarmonyMethod(typeof(Ctors2Mod), nameof(Report)));
        }

        public static void Try(string fix, Action install)
        {
            if (!Enabled(fix))
            {
                return;
            }
            try
            {
                install();
            }
            catch (Exception e)
            {
                Log.Error($"[RLO] {fix} could not be enabled and was skipped: {e}");
            }
        }

        public static void BeforeVanillaCallAll()
        {
            if (vanillaDone || !UnityData.IsInMainThread)
            {
                return;
            }
            vanillaDone = true;
            Try("vte", () => VteCraftable.Install(Harmony));
        }

        public static void Report()
        {
            if (reported)
            {
                return;
            }
            reported = true;
            KcsgRequirements.MenuReached = true;
            foreach (var report in Reports)
            {
                try
                {
                    Log.Message("[RLO] " + report());
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] report failed: {e.Message}");
                }
            }
        }

        public static bool Calls(MethodBase method, MethodBase target)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(p => p.Value is MethodBase m && m == target);
            }
            catch
            {
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodBase from, MethodInfo to)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodBase m && m == from && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt))
                {
                    yield return new CodeInstruction(OpCodes.Call, to).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static bool OnlyOurs(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info == null || info.Owners.All(o => o == Harmony.Id);
        }
    }

    [StaticConstructorOnStartup]
    public static class Ctors2Late
    {
        static Ctors2Late()
        {
            if (Ctors2Mod.Harmony == null)
            {
                return;
            }
            Timing.InstallMethods(Ctors2Mod.Harmony);
            Ctors2Mod.Try("lunar", () => LunarConflicts.Install(Ctors2Mod.Harmony));
            Ctors2Mod.Try("tdf", () => TdFindLib.Install(Ctors2Mod.Harmony));
            Ctors2Mod.Try("av", () => AvSkins.Install(Ctors2Mod.Harmony));
            Ctors2Mod.Try("linkable", () => LinkableFacilities.Install(Ctors2Mod.Harmony));
            Ctors2Mod.Try("kcsg", () => KcsgRequirements.Install(Ctors2Mod.Harmony));
            Ctors2Mod.Try("vehicles", () => VehiclePatternLookups.Install(Ctors2Mod.Harmony));
        }
    }
}
