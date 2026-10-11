using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class LunarInlining
    {
        private static int replaced = -1, scanned, fallbacks, mismatches, withTargets;
        private static long ticks;
        private static readonly MethodInfo original = AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.ReadMethodBody), new[] { typeof(MethodBase) });

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("LunarFramework.Patching.HarmonyInliningFixer");
            var apply = type == null ? null : AccessTools.Method(type, "Apply");
            if (apply == null || !Fingerprint.Matches("Lunar inlining fixer",
                Fingerprint.Known(apply, "EB15E93BFD0A2FCE", "0EB2AD996306A30A"),
                Fingerprint.Known(original, "05BA8CAC5E457CEB", "9AAB7BE113AD5532")))
            {
                return;
            }
            harmony.Patch(apply, transpiler: new HarmonyMethod(typeof(LunarInlining), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(LunarInlining), nameof(ReadMethodBody));
            var count = 0;
            var list = instructions.ToList();
            foreach (var code in list)
            {
                if (code.Calls(original))
                {
                    code.operand = target;
                    count++;
                }
            }
            replaced = count;
            return list;
        }

        public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method)
        {
            var start = Stopwatch.GetTimestamp();
            scanned++;
            List<KeyValuePair<OpCode, object>> fast;
            try
            {
                fast = Scan(method);
            }
            catch
            {
                fast = null;
            }
            if (fast == null)
            {
                fallbacks++;
                ticks += Stopwatch.GetTimestamp() - start;
                return PatchProcessor.ReadMethodBody(method);
            }
            if (fast.Count > 0)
            {
                withTargets++;
            }
            ticks += Stopwatch.GetTimestamp() - start;
            if (RLOMod.Verify)
            {
                var slow = PatchProcessor.ReadMethodBody(method).Select(e => e.Value).OfType<MethodBase>().ToList();
                if (!slow.SequenceEqual(fast.Select(e => (MethodBase)e.Value)))
                {
                    if (++mismatches <= 10)
                    {
                        Log.Warning($"[RLO] Lunar scan verify mismatch for {method.FullDescription()}: fast {fast.Count}, original {slow.Count} method operands");
                    }
                }
            }
            return fast;
        }

        private static List<KeyValuePair<OpCode, object>> Scan(MethodBase method)
        {
            if (method is MethodInfo info && (info.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                return null;
            }
            var result = new List<KeyValuePair<OpCode, object>>();
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length == 0)
            {
                return result;
            }
            var module = method.Module;
            Type[] typeArguments = null;
            Type[] methodArguments = null;
            var declaringType = method.DeclaringType;
            if (declaringType != null && declaringType.IsGenericType)
            {
                try
                {
                    typeArguments = declaringType.GetGenericArguments();
                }
                catch
                {
                    typeArguments = null;
                }
            }
            if (method.IsGenericMethod)
            {
                try
                {
                    methodArguments = method.GetGenericArguments();
                }
                catch
                {
                    methodArguments = null;
                }
            }
            var pos = 0;
            while (pos < il.Length)
            {
                OpCode op;
                var b = il[pos++];
                if (b == 0xFE)
                {
                    var b2 = il[pos++];
                    if (!IlScan.TwoKnown[b2])
                    {
                        return null;
                    }
                    op = IlScan.TwoByte[b2];
                }
                else
                {
                    if (!IlScan.OneKnown[b])
                    {
                        return null;
                    }
                    op = IlScan.OneByte[b];
                }
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        pos += 1;
                        break;
                    case OperandType.InlineVar:
                        pos += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        pos += 8;
                        break;
                    case OperandType.InlineSwitch:
                        pos += 4 + 4 * BitConverter.ToInt32(il, pos);
                        break;
                    case OperandType.InlineMethod:
                        result.Add(new KeyValuePair<OpCode, object>(op, module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArguments, methodArguments)));
                        pos += 4;
                        break;
                    case OperandType.InlineTok:
                        if (module.ResolveMember(BitConverter.ToInt32(il, pos), typeArguments, methodArguments) is MethodBase member)
                        {
                            result.Add(new KeyValuePair<OpCode, object>(op, member));
                        }
                        pos += 4;
                        break;
                    default:
                        pos += 4;
                        break;
                }
            }
            if (pos != il.Length)
            {
                return null;
            }
            return result;
        }

        public static string Stats()
        {
            if (replaced < 0)
            {
                return "Lunar inlining fixer: not patched";
            }
            return $"Lunar inlining fixer: {replaced} call sites redirected, {scanned} method bodies scanned in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms ({withTargets} with method operands), {fallbacks} fallbacks to Harmony's reader, {mismatches} verify mismatches";
        }
    }
}
