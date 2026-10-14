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
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(count, "EEF47B4868A2641B"),
                Fingerprint.Known(tradeabilityFor, "43C1B8045E1D5491"),
                Fingerprint.Known(handlesThingDef, "7BEBC7F85A979A4A"),
                Fingerprint.Known(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Storyteller)), "101547EDE4BE4F3C")))
            {
                return;
            }
            if (AccessTools.Method(typeof(StockGenerator_Tomes), nameof(StockGenerator.TradeabilityFor)).DeclaringType != typeof(StockGenerator))
            {
                Log.Message($"[RLO] {Feature}: the tome stock generator has its own tradeability check in this RimWorld version. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            harmony.Patch(count, transpiler: new HarmonyMethod(typeof(WorksitesTraders), nameof(Transpiler)));
            if (redirected != 1)
            {
                harmony.Unpatch(count, HarmonyPatchType.Transpiler, harmony.Id);
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
            if (generator == null || generator.GetType() != typeof(StockGenerator_Tomes) || !ModsConfig.AnomalyActive || Current.Game != null || !Unpatched())
            {
                return generator.TradeabilityFor(thingDef);
            }
            shortcuts++;
            if (RLOMod.Verify)
            {
                return Verify(generator, thingDef);
            }
            return Tradeability.None;
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
