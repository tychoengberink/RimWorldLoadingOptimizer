#if RW17
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class Coalesce
    {
        private static bool ModCodeOrUnknown(MethodBase original, Patch[][] arrays)
        {
            try
            {
                return RunsModCode(original, arrays);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool RunsModCode(MethodBase original, Patch[][] arrays)
        {
            if (arrays[2].Length > 0)
            {
                return true;
            }
            if (!TypeSafe(original.DeclaringType) || original is MethodInfo info && !TypeSafe(info.ReturnType) || original.GetParameters().Any(p => !TypeSafe(p.ParameterType)))
            {
                return true;
            }
            foreach (var patch in arrays[0].Concat(arrays[1]).Concat(arrays[3]))
            {
                var method = patch.PatchMethod;
                if (method == null || (bool)isFactory(null, method) || !MethodSafe(method) || !TypeSafe(method.DeclaringType) || !TypeSafe(method.ReturnType) || method.GetParameters().Any(p => !TypeSafe(p.ParameterType)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ClassSafe(Type type) => type.BaseType == typeof(object) && type.GetMethods(AccessTools.all).All(MethodSafe);

        private static bool MethodSafe(MethodBase method) => Cached(method, () => AttributesSafe(method.GetCustomAttributesData())
            && method.GetParameters().All(p => AttributesSafe(p.GetCustomAttributesData()))
            && (!(method is MethodInfo info) || info.ReturnParameter == null || AttributesSafe(info.ReturnParameter.GetCustomAttributesData())));

        private static bool TypeSafe(Type type)
        {
            while (type != null && (type.HasElementType || type.IsGenericType && !type.IsGenericTypeDefinition))
            {
                type = type.HasElementType ? type.GetElementType() : type.GetGenericTypeDefinition();
            }
            if (type == null || type.IsGenericParameter)
            {
                return true;
            }
            var current = type;
            return Cached(current, () =>
            {
                for (var t = current; t != null && !Trusted(t.Assembly); t = t.BaseType)
                {
                    if (!AttributesSafe(t.GetCustomAttributesData()))
                    {
                        return false;
                    }
                }
                return true;
            });
        }

        private static bool AttributesSafe(IList<CustomAttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                var ctor = attribute.Constructor;
                if (Trusted(ctor.DeclaringType.Assembly))
                {
                    continue;
                }
                if (!CodeSafe(ctor))
                {
                    return false;
                }
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.MemberInfo is PropertyInfo property && !CodeSafe(property.GetSetMethod(true)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool CodeSafe(MethodBase method)
        {
            if (method == null)
            {
                return false;
            }
            if (Trusted(method.DeclaringType.Assembly))
            {
                return true;
            }
            return Cached(method, () =>
            {
                for (var t = method.DeclaringType; t != null && !Trusted(t.Assembly); t = t.BaseType)
                {
                    if (t.TypeInitializer != null)
                    {
                        return false;
                    }
                }
                if (method.GetMethodBody() == null)
                {
                    return false;
                }
                foreach (var instruction in PatchProcessor.ReadMethodBody(method))
                {
                    var op = instruction.Key;
                    if (op == OpCodes.Calli || op == OpCodes.Newobj || op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld || op == OpCodes.Jmp || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                    {
                        return false;
                    }
                    if (op != OpCodes.Call && op != OpCodes.Callvirt)
                    {
                        continue;
                    }
                    var target = instruction.Value as MethodBase;
                    if (target == getTypeFromHandle)
                    {
                        continue;
                    }
                    if (!(target is ConstructorInfo) || !target.DeclaringType.IsAssignableFrom(method.DeclaringType) || !CodeSafe(target))
                    {
                        return false;
                    }
                }
                return true;
            });
        }

        private static bool Trusted(Assembly assembly)
        {
            if (assembly == typeof(object).Assembly || assembly == typeof(Harmony).Assembly)
            {
                return true;
            }
            var name = assembly.GetName().Name;
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System.", StringComparison.Ordinal);
        }

        private static bool Cached(object key, Func<bool> compute)
        {
            lock (safety)
            {
                if (safety.TryGetValue(key, out var known))
                {
                    return known;
                }
            }
            bool result;
            try
            {
                result = compute();
            }
            catch (Exception)
            {
                result = false;
            }
            lock (safety)
            {
                safety[key] = result;
            }
            return result;
        }
    }
}
#endif
