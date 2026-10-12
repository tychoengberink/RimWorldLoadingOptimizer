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
    public static class IlScan
    {
        internal static readonly OpCode[] OneByte = new OpCode[256];
        internal static readonly OpCode[] TwoByte = new OpCode[256];
        internal static readonly bool[] OneKnown = new bool[256];
        internal static readonly bool[] TwoKnown = new bool[256];
        private static readonly Dictionary<(Module, int), object> resolved = new Dictionary<(Module, int), object>();
        private static readonly object gate = new object();
        private static int scans, positives, fallbacks, mismatches;
        private static long ticks;

        static IlScan()
        {
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)field.GetValue(null);
                var value = (ushort)op.Value;
                if (op.Size == 1)
                {
                    OneByte[value] = op;
                    OneKnown[value] = true;
                }
                else
                {
                    TwoByte[value & 0xFF] = op;
                    TwoKnown[value & 0xFF] = true;
                }
            }
        }

        private static bool Original(MethodInfo method, Func<object, bool> match)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(p => match(p.Value));
            }
            catch
            {
                return false;
            }
        }

        public static bool CallsAny(MethodInfo method, params MethodBase[] targets) => Decide(method, v => v is MethodBase m && targets.Contains(m));

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

        public static bool Decide(MethodInfo method, Func<object, bool> match)
        {
            if (!FasterCtors.Enabled("ilscan"))
            {
                return Original(method, match);
            }
            lock (gate)
            {
                var start = Stopwatch.GetTimestamp();
                scans++;
                bool result;
                var operands = Scan(method);
                if (operands == null)
                {
                    fallbacks++;
                    result = Original(method, match);
                }
                else if (operands.Any(match))
                {
                    positives++;
                    result = Original(method, match);
                }
                else
                {
                    result = false;
                    if (RLOMod.Verify && Original(method, match))
                    {
                        result = true;
                        if (mismatches++ < 10)
                        {
                            Log.Warning($"[RLO] IL scan verify mismatch: {method.FullDescription()}");
                        }
                    }
                }
                ticks += Stopwatch.GetTimestamp() - start;
                return result;
            }
        }

        private static object Resolve(Module module, int token, Type[] typeArguments, Type[] methodArguments, bool member)
        {
            if (typeArguments != null || methodArguments != null)
            {
                return member ? module.ResolveMember(token, typeArguments, methodArguments) : module.ResolveMethod(token, typeArguments, methodArguments);
            }
            var key = (module, member ? token : ~token);
            if (resolved.TryGetValue(key, out var value))
            {
                return value;
            }
            value = member ? (object)module.ResolveMember(token) : module.ResolveMethod(token);
            resolved[key] = value;
            return value;
        }

        private static List<object> Scan(MethodInfo method)
        {
            try
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    return null;
                }
                var result = new List<object>();
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                {
                    return null;
                }
                var module = method.Module;
                Type[] typeArguments = null;
                Type[] methodArguments = null;
                var declaringType = method.DeclaringType;
                if (declaringType != null && declaringType.IsGenericType)
                {
                    typeArguments = declaringType.GetGenericArguments();
                }
                if (method.IsGenericMethod)
                {
                    methodArguments = method.GetGenericArguments();
                }
                var pos = 0;
                while (pos < il.Length)
                {
                    OpCode op;
                    var b = il[pos++];
                    if (b == 0xFE)
                    {
                        var b2 = il[pos++];
                        if (!TwoKnown[b2])
                        {
                            return null;
                        }
                        op = TwoByte[b2];
                    }
                    else
                    {
                        if (!OneKnown[b])
                        {
                            return null;
                        }
                        op = OneByte[b];
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
                            result.Add(Resolve(module, BitConverter.ToInt32(il, pos), typeArguments, methodArguments, false));
                            pos += 4;
                            break;
                        case OperandType.InlineTok:
                            result.Add(Resolve(module, BitConverter.ToInt32(il, pos), typeArguments, methodArguments, true));
                            pos += 4;
                            break;
                        default:
                            pos += 4;
                            break;
                    }
                }
                return pos == il.Length ? result : null;
            }
            catch
            {
                return null;
            }
        }

        public static string Stats()
        {
            lock (gate)
            {
                return $"IL scan: {scans} bodies in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms, {positives} positives confirmed by Harmony's reader, {fallbacks} fallbacks, {resolved.Count} cached tokens" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
            }
        }
    }
}
