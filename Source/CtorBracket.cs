using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CtorBracket
    {
        private static readonly List<Type> stack = new List<Type>();
        public static event Action<Type> Started;
        public static event Action<Type> Ended;

        private static bool installed;

        public static void InstallEarly(Harmony harmony)
        {
            if (installed)
            {
                return;
            }
            installed = true;
            harmony.Patch(AccessTools.Method(typeof(RuntimeHelpers), nameof(RuntimeHelpers.RunClassConstructor), new[] { typeof(RuntimeTypeHandle) }),
                prefix: new HarmonyMethod(typeof(CtorBracket), nameof(Start)),
                finalizer: new HarmonyMethod(typeof(CtorBracket), nameof(End)));
        }

        public static bool Inside(Type type) => type != null && UnityData.IsInMainThread && stack.Contains(type);

        public static void Start(RuntimeTypeHandle type)
        {
            if (!UnityData.IsInMainThread)
            {
                return;
            }
            var t = Type.GetTypeFromHandle(type);
            stack.Add(t);
            Started?.Invoke(t);
        }

        public static void End(RuntimeTypeHandle type)
        {
            if (!UnityData.IsInMainThread || stack.Count == 0)
            {
                return;
            }
            var t = Type.GetTypeFromHandle(type);
            var i = stack.LastIndexOf(t);
            if (i < 0)
            {
                return;
            }
            stack.RemoveAt(i);
            Ended?.Invoke(t);
        }
    }
}
