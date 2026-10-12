using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Fingerprint
    {
        private static readonly bool ignore = Environment.GetEnvironmentVariable("LEANMEMORY_IGNORE_FINGERPRINTS") == "1";

        public static readonly List<string> Changed = new List<string>();

        public static string Of(MethodBase method)
        {
            var il = method?.GetMethodBody()?.GetILAsByteArray();
            return il == null ? null : Hash(il);
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data), 0, 8).Replace("-", "");
            }
        }

        public static string Normalized(MethodBase method)
        {
            try
            {
                var body = method?.GetMethodBody();
                var il = body?.GetILAsByteArray();
                if (il == null)
                {
                    return null;
                }
                var typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
                var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                var clauses = body.ExceptionHandlingClauses.Select(c => ((int)c.Flags, c.TryOffset, c.TryLength, c.HandlerOffset, c.HandlerLength,
                    c.Flags == ExceptionHandlingClauseOptions.Filter ? c.FilterOffset : 0,
                    c.Flags == ExceptionHandlingClauseOptions.Clause ? TypeName(c.CatchType) : null));
                var module = method.Module;
                return Normalized(il, clauses, token => (uint)token >> 24 == 0x70 ? module.ResolveString(token)
                    : (uint)token >> 24 == 0x11 ? "sig"
                    : MemberName(module.ResolveMember(token, typeArgs, methodArgs)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Normalized(byte[] il, IEnumerable<(int kind, int tryOffset, int tryLength, int handlerOffset, int handlerLength, int filterOffset, string catchType)> clauses, Func<int, string> resolve)
        {
            var offsets = new List<int>();
            var ops = new List<(OpCode op, int operand, long wide, int next, int[] targets)>();
            var pos = 0;
            while (pos < il.Length)
            {
                offsets.Add(pos);
                var op = il[pos] == 0xFE
                    ? (pos + 1 < il.Length && IlScan.TwoKnown[il[pos + 1]] ? IlScan.TwoByte[il[pos + 1]] : (OpCode?)null)
                    : IlScan.OneKnown[il[pos]] ? IlScan.OneByte[il[pos]] : (OpCode?)null;
                if (op == null)
                {
                    return null;
                }
                pos += op.Value.Size;
                int operand = 0;
                long wide = 0;
                int[] targets = null;
                switch (op.Value.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                        operand = (sbyte)il[pos] + pos + 1;
                        pos += 1;
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        operand = il[pos];
                        pos += 1;
                        break;
                    case OperandType.InlineVar:
                        operand = BitConverter.ToUInt16(il, pos);
                        pos += 2;
                        break;
                    case OperandType.InlineBrTarget:
                        operand = BitConverter.ToInt32(il, pos) + pos + 4;
                        pos += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        wide = BitConverter.ToInt64(il, pos);
                        pos += 8;
                        break;
                    case OperandType.InlineSwitch:
                        var count = BitConverter.ToInt32(il, pos);
                        var baseOffset = pos + 4 + 4 * count;
                        targets = new int[count];
                        for (var i = 0; i < count; i++)
                        {
                            targets[i] = BitConverter.ToInt32(il, pos + 4 + 4 * i) + baseOffset;
                        }
                        pos = baseOffset;
                        break;
                    default:
                        operand = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        break;
                }
                ops.Add((op.Value, operand, wide, pos, targets));
            }
            var index = new Dictionary<int, int>();
            for (var i = 0; i < offsets.Count; i++)
            {
                index[offsets[i]] = i;
            }
            index[il.Length] = offsets.Count;
            string At(int offset) => index.TryGetValue(offset, out var i) ? i.ToString(CultureInfo.InvariantCulture) : "?" + offset.ToString(CultureInfo.InvariantCulture);
            var text = new StringBuilder();
            foreach (var (op, operand, wide, _, targets) in ops)
            {
                text.Append(op.Name);
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.InlineBrTarget:
                        text.Append(" @").Append(At(operand));
                        break;
                    case OperandType.InlineSwitch:
                        text.Append(" @").Append(string.Join(",", targets.Select(At)));
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                    case OperandType.InlineVar:
                    case OperandType.InlineI:
                        text.Append(' ').Append(operand.ToString(CultureInfo.InvariantCulture));
                        break;
                    case OperandType.InlineI8:
                        text.Append(' ').Append(wide.ToString(CultureInfo.InvariantCulture));
                        break;
                    case OperandType.ShortInlineR:
                        text.Append(' ').Append(BitConverter.ToSingle(BitConverter.GetBytes(operand), 0).ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case OperandType.InlineR:
                        text.Append(' ').Append(BitConverter.Int64BitsToDouble(wide).ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case OperandType.InlineString:
                        text.Append(" \"").Append(resolve(operand)).Append('"');
                        break;
                    default:
                        text.Append(' ').Append(resolve(operand));
                        break;
                }
                text.Append('\n');
            }
            foreach (var c in clauses)
            {
                text.Append(".try ").Append(c.kind).Append(' ').Append(At(c.tryOffset)).Append(' ').Append(At(c.tryOffset + c.tryLength))
                    .Append(' ').Append(At(c.handlerOffset)).Append(' ').Append(At(c.handlerOffset + c.handlerLength))
                    .Append(' ').Append(c.kind == 1 ? At(c.filterOffset) : c.catchType ?? "").Append('\n');
            }
            return Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static string MemberName(MemberInfo member)
        {
            switch (member)
            {
                case Type type:
                    return TypeName(type);
                case FieldInfo field:
                    return TypeName(field.DeclaringType) + "::" + field.Name;
                case MethodBase method:
                    var open = method;
                    var declaring = method.DeclaringType;
                    if (declaring != null && !declaring.IsArray && (declaring.IsGenericType && !declaring.IsGenericTypeDefinition || method.IsGenericMethod && !method.IsGenericMethodDefinition))
                    {
                        open = method.Module.ResolveMethod(method.MetadataToken);
                    }
                    var generic = method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(TypeName)) + ">" : "";
                    return TypeName(declaring) + "::" + method.Name + generic + "(" + string.Join(",", open.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";
                default:
                    return member?.ToString();
            }
        }

        private static string TypeName(Type type)
        {
            if (type == null)
            {
                return "<Module>";
            }
            if (type.IsGenericParameter)
            {
                return (type.DeclaringMethod != null ? "!!" : "!") + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
            }
            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                return TypeName(type.GetElementType()) + "[" + new string(',', rank - 1) + "]";
            }
            if (type.IsByRef)
            {
                return TypeName(type.GetElementType()) + "&";
            }
            if (type.IsPointer)
            {
                return TypeName(type.GetElementType()) + "*";
            }
            if (type.IsGenericType)
            {
                return DefinitionName(type.GetGenericTypeDefinition()) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            }
            return DefinitionName(type);
        }

        private static string DefinitionName(Type type)
        {
            if (type.IsNested)
            {
                return DefinitionName(type.DeclaringType) + "+" + type.Name;
            }
            return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
        }

        public static string MatchedHash(MethodBase method, string[] known)
        {
            var hash = Of(method);
            if (hash != null && known.Contains(hash))
            {
                return hash;
            }
            var normalized = Normalized(method);
            return normalized != null && known.Contains(normalized) ? normalized : null;
        }

        public static bool Matches(string feature, params (MethodBase method, string[] known)[] targets)
        {
            foreach (var (method, known) in targets)
            {
                if (method == null)
                {
                    Skip(feature, "a method it needs no longer exists");
                    return false;
                }
                if (MatchedHash(method, known) != null)
                {
                    continue;
                }
                var hash = Of(method);
                var normalized = Normalized(method);
                var where = $"{method.DeclaringType?.FullName}.{method.Name}";
                if (ignore)
                {
                    Log.Warning($"[RLO] {feature}: {where} changed (il {hash}, normalized {normalized}), used anyway because LEANMEMORY_IGNORE_FINGERPRINTS=1");
                    continue;
                }
                Skip(feature, $"{where} is not the version RimWorld Loading Optimizer was checked against ({LikelyCause(method)}; il {hash}, normalized {normalized})");
                return false;
            }
            return true;
        }

        private static void Skip(string feature, string reason)
        {
            lock (Changed)
            {
                Changed.Add(feature);
            }
            Log.Message($"[RLO] {feature}: {reason}. This speedup is skipped and the original code runs; nothing is broken.");
        }

        private static string LikelyCause(MethodBase method)
        {
            var assembly = method.DeclaringType?.Assembly;
            var owner = assembly == typeof(Def).Assembly ? "another RimWorld version" : assembly == typeof(Harmony).Assembly ? "another Harmony version" : "another version of that mod";
            bool prepatcher;
            try
            {
                prepatcher = RLOMod.Running("zetrith.prepatcher") != null;
            }
            catch (Exception)
            {
                prepatcher = false;
            }
            return prepatcher
                ? $"{owner}, or a mod that uses Prepatcher, such as Performance Fish, rewrote the method"
                : $"{owner}, or another mod rewrote the method";
        }

        public static bool OnlyOurs(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info == null || info.Owners.All(o => o == RLOMod.HarmonyId);
        }

        public static (MethodBase, string[]) Known(MethodBase method, params string[] known) => (method, known);

        public static string Stats()
        {
            lock (Changed)
            {
                return Changed.Count == 0 ? "fingerprints: all verified" : $"fingerprints: off because the original changed: {string.Join(", ", Changed)}";
            }
        }
    }
}
