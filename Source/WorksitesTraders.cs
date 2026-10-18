#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class WorksitesTraders
    {
        public const string Feature = "Worksites Expanded trader count";

        private static MethodInfo tradeabilityFor, handlesThingDef;
        private static int redirected, shortcuts, verified, mismatches, frame = -1;
        private static bool unpatched;
        private static long verifyTicks;
        private static SelfCheck check;
        private static Func<ThingDef, int> countOriginal;
        [ThreadStatic] private static bool bypass, served;

        public static void Install(Harmony harmony)
        {
            var loot = AccessTools.TypeByName("MiningOutpost.Loot.LootRarity");
            if (loot == null)
            {
                return;
            }
            var count = AccessTools.Method(loot, "CountTradersStocking");
            tradeabilityFor = AccessTools.Method(typeof(StockGenerator), nameof(StockGenerator.TradeabilityFor));
            handlesThingDef = AccessTools.Method(typeof(StockGenerator_Tomes), nameof(StockGenerator_Tomes.HandlesThingDef));
            if (tradeabilityFor == null || handlesThingDef == null || count?.ReturnType != typeof(int) || !count.IsStatic
                || count.GetParameters().Length != 1 || count.GetParameters()[0].ParameterType != typeof(ThingDef))
            {
                VersionCheck.Skip(Feature, "a method it needs no longer exists");
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(count, tradeabilityFor, handlesThingDef));
            if (check.IsOff)
            {
                return;
            }
            countOriginal = (Func<ThingDef, int>)count.CreateDelegate(typeof(Func<ThingDef, int>));
            if (AccessTools.Method(typeof(StockGenerator_Tomes), nameof(StockGenerator.TradeabilityFor)).DeclaringType != typeof(StockGenerator))
            {
                Log.Message($"[RLO] {Feature}: the tome stock generator has its own tradeability check in this RimWorld version. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            harmony.Patch(count, transpiler: new HarmonyMethod(typeof(WorksitesTraders), nameof(Transpiler)), postfix: new HarmonyMethod(typeof(WorksitesTraders), nameof(CountPostfix)));
            if (redirected != 1)
            {
                harmony.Unpatch(count, HarmonyPatchType.All, harmony.Id);
                Log.Message($"[RLO] {Feature}: the tradeability call was not found as expected ({redirected} found). This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            MainMenu.UnpatchAfterStartup(count);
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var calls = list.Where(i => i.opcode == OpCodes.Callvirt && i.operand is MethodInfo m && m == tradeabilityFor).ToList();
            redirected = calls.Count;
            if (calls.Count != 1)
            {
                return list;
            }
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(WorksitesTraders), nameof(TradeabilityFor));
            return list;
        }

        public static Tradeability TradeabilityFor(StockGenerator generator, ThingDef thingDef)
        {
            if (bypass || check.IsOff || generator == null || generator.GetType() != typeof(StockGenerator_Tomes) || !ModsConfig.AnomalyActive || Current.Game != null || !Unpatched())
            {
                return generator.TradeabilityFor(thingDef);
            }
            shortcuts++;
            served = true;
            if (RLOMod.Verify)
            {
                return Verify(generator, thingDef);
            }
            return Tradeability.None;
        }

        public static void CountPostfix(ThingDef def, ref int __result)
        {
            if (bypass || !served)
            {
                return;
            }
            served = false;
            if (!check.IsChecking)
            {
                return;
            }
            int original;
            bypass = true;
            try
            {
                original = countOriginal(def);
            }
            finally
            {
                bypass = false;
            }
            check.Report(original == __result, $"traders stocking {def?.defName}");
            __result = original;
        }

        private static bool Unpatched()
        {
            var now = Time.frameCount;
            if (now != frame)
            {
                frame = now;
                unpatched = Fingerprint.OnlyOurs(tradeabilityFor) && Fingerprint.OnlyOurs(handlesThingDef);
            }
            return unpatched;
        }

        private static Tradeability Verify(StockGenerator generator, ThingDef thingDef)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var result = generator.TradeabilityFor(thingDef);
                mismatches++;
                return result;
            }
            catch (Exception)
            {
                verified++;
                return Tradeability.None;
            }
            finally
            {
                verifyTicks += Stopwatch.GetTimestamp() - start;
            }
        }

        private static string Stats()
        {
            var text = $"{Feature}: {shortcuts:N0} tome-trader checks answered without an exception";
            if (RLOMod.Verify)
            {
                text += $"; verify: {verified:N0} original checks threw as expected ({LeanFixes.Seconds(verifyTicks):F2} s spent on them), {mismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
