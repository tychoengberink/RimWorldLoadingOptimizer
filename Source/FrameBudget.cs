using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class FrameBudget
    {
        private static readonly float Startup = float.TryParse(Environment.GetEnvironmentVariable("MAINPHASES_FRAME"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0.1f ? f : 0.25f;
        private const float Default = 0.1f;

        public static bool MenuReached;
        public static int Replaced;

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(LongEventHandler), "UpdateCurrentEnumeratorEvent");
            if (!Fingerprint.Matches("main phases frame budget", Fingerprint.Known(target, "A134D3C247E099D2", "0D0F8F066738F9F3")))
            {
                return;
            }
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(FrameBudget), nameof(Transpiler)));
            LeanFixes.Reports.Add(() => $"frame budget: {Replaced} constant(s) replaced, {Startup:F2} s per frame until the main menu");
        }

        public static float Budget() => MenuReached || Current.Game != null ? Default : Startup;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == Default)
                {
                    Replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FrameBudget), nameof(Budget))).MoveLabelsFrom(instruction);
                    continue;
                }
                yield return instruction;
            }
        }
    }
}
