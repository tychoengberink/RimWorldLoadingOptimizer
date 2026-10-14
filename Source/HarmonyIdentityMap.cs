using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class HarmonyIdentityMap
    {
        private static MethodInfo resolve;
        private static bool verify;
        [ThreadStatic] private static Dictionary<object, MemberInfo> map;
        [ThreadStatic] private static int wrapperDepth;
        [ThreadStatic] private static int collecting;
        [ThreadStatic] private static int transpiling;
        [ThreadStatic] private static int generating;
        [ThreadStatic] private static bool inVerify;
        private static long mapped;
        private static long hits;
        private static long misses;
        private static long sameObject;
        private static long equalMember;
        private static long mismatches;
        private static long originalThrew;
        private static readonly Dictionary<string, int> missKinds = new Dictionary<string, int>();

        private static bool installed;
        public static int Updates;

        public static void Install(Harmony harmony)
        {
            verify = RLOMod.Verify;
            var harmonyAssembly = typeof(Harmony).Assembly;
            Type T(string name) => harmonyAssembly.GetType(name, true);
            var provider = T("Mono.Cecil.IGenericParameterProvider");
            var importer = T("MonoMod.Utils.MMReflectionImporter");
            var imports = new[] { typeof(Type), typeof(FieldInfo), typeof(MethodBase) }.Select(t => AccessTools.Method(importer, "ImportReference", new[] { t, provider })).ToArray();
            resolve = AccessTools.Method(T("MonoMod.Utils.ReflectionHelper"), "_ResolveReflection");
            var prepare = AccessTools.Method(T("HarmonyLib.MethodCreatorConfig"), "Prepare");
            var create = AccessTools.Method(T("HarmonyLib.MethodCreator"), "CreateReplacement");
            var transpile = AccessTools.Method(T("HarmonyLib.CodeTranspiler"), "GetResult");
            var generate = AccessTools.Method(T("MonoMod.Utils.DynamicMethodDefinition"), "Generate", new[] { typeof(object) });
            var update = AccessTools.Method(T("HarmonyLib.PatchFunctions"), "UpdateWrapper");
            if (!Fingerprint.Matches("Harmony identity map",
                Fingerprint.Known(imports[0], Known.TypeImport),
                Fingerprint.Known(imports[1], Known.FieldImport),
                Fingerprint.Known(imports[2], Known.MethodImport),
                Fingerprint.Known(resolve, Known.Resolve),
                Fingerprint.Known(prepare, Known.Prepare),
                Fingerprint.Known(create, Known.Create),
                Fingerprint.Known(transpile, Known.Transpile),
                Fingerprint.Known(generate, Known.Generate),
                Fingerprint.Known(update, Known.Update)))
            {
                return;
            }
            var post = new HarmonyMethod(typeof(HarmonyIdentityMap), nameof(ImportPost));
            foreach (var import in imports)
            {
                harmony.Patch(import, postfix: post);
            }
            harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(HarmonyIdentityMap), nameof(ResolvePre)));
            void Scope(MethodBase method, string pre, string fin) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(HarmonyIdentityMap), pre), finalizer: new HarmonyMethod(typeof(HarmonyIdentityMap), fin));
            Scope(prepare, nameof(CollectPre), nameof(CollectFin));
            Scope(create, nameof(CollectPre), nameof(CollectFin));
            Scope(transpile, nameof(TranspilePre), nameof(TranspileFin));
            Scope(generate, nameof(GeneratePre), nameof(GenerateFin));
            Scope(update, nameof(WrapperPre), nameof(WrapperFin));
            installed = true;
        }

        private static class Known
        {
            public static readonly string[] TypeImport = { "FDF3EEFDB6652F06", "FD4A14B89E12DDD4", "4E6DAB8FAFA68234" };
            public static readonly string[] FieldImport = { "EB70DBBC7A127399", "7742EEB84CDAC2F2", "FB2BFD532A732E5D" };
            public static readonly string[] MethodImport = { "3BAA742293DC7802", "ADD3CD65E5F4A9F4", "C1386BBFBAD64316" };
            public static readonly string[] Resolve = { "08937D537F4F2F5B", "F7A6908998AFB966", "E5FF71FD57E161B3", "1DA1868D281439F5" };
#if RW17
            public static readonly string[] Prepare = { "43A8FEEF5EF104B6", "C8AD921021F0F563", "D726632B7BEC6E7A", "D9F69F4344DA456D" };
#else
            public static readonly string[] Prepare = { "43A8FEEF5EF104B6", "C8AD921021F0F563", "D726632B7BEC6E7A" };
#endif
#if RW17
            public static readonly string[] Create = { "7DAF0F063DFC7EB1", "F34855CB0DBD23C9", "B9317845F8764C26", "E08133BE98F80F7A" };
#else
            public static readonly string[] Create = { "7DAF0F063DFC7EB1", "F34855CB0DBD23C9", "B9317845F8764C26" };
#endif
            public static readonly string[] Transpile = { "F63C02817D2015DD", "F619C30E10FF0072" };
#if RW17
            public static readonly string[] Generate = { "F4ABFD8B96C38FDF", "A9013B5D7D38F550", "C1EAEFE01EEBBBAB", "38A8D4DC68F931AC" };
#else
            public static readonly string[] Generate = { "F4ABFD8B96C38FDF", "A9013B5D7D38F550", "C1EAEFE01EEBBBAB" };
#endif
#if RW17
            public static readonly string[] Update = { "2420BCB82066FF16", "746ABD198B833108", "0ECE39398AC683A2", "4F007C93FF50DD95" };
#else
            public static readonly string[] Update = { "2420BCB82066FF16", "746ABD198B833108", "0ECE39398AC683A2" };
#endif
        }

        public static void WrapperPre()
        {
            if (wrapperDepth++ == 0)
            {
                Interlocked.Increment(ref Updates);
                map = map ?? new Dictionary<object, MemberInfo>(ReferenceComparer<object>.Instance);
            }
        }

        public static void WrapperFin()
        {
            if (--wrapperDepth == 0)
            {
                map.Clear();
            }
        }

        public static void CollectPre() => collecting++;

        public static void CollectFin() => collecting--;

        public static void TranspilePre() => transpiling++;

        public static void TranspileFin() => transpiling--;

        public static void GeneratePre() => generating++;

        public static void GenerateFin() => generating--;

        public static void ImportPost(object __result, object __0)
        {
            if (collecting > 0 && transpiling == 0 && wrapperDepth > 0 && __result != null && __0 is MemberInfo member && Mappable(member))
            {
                map[__result] = member;
                if (verify)
                {
                    Interlocked.Increment(ref mapped);
                }
            }
        }

        private static bool Mappable(MemberInfo member)
        {
            if (member is Type type)
            {
                return !type.IsGenericParameter;
            }
            var declaring = member.DeclaringType;
            if (declaring == null || member.ReflectedType != declaring)
            {
                return false;
            }
            if (declaring.IsGenericType)
            {
                return false;
            }
            return !(member is MethodBase method) || !method.IsGenericMethod;
        }

        public static bool ResolvePre(object mref, object modules, ref MemberInfo __result)
        {
            if (generating == 0 || wrapperDepth == 0 || inVerify || mref == null)
            {
                return true;
            }
            if (!map.TryGetValue(mref, out var found))
            {
                if (verify)
                {
                    Interlocked.Increment(ref misses);
                    lock (missKinds)
                    {
                        var kind = mref.GetType().Name;
                        missKinds.TryGetValue(kind, out int n);
                        missKinds[kind] = n + 1;
                    }
                }
                return true;
            }
            if (!verify)
            {
                __result = found;
                return false;
            }
            Interlocked.Increment(ref hits);
            MemberInfo expected;
            inVerify = true;
            try
            {
                expected = (MemberInfo)resolve.Invoke(null, new[] { mref, modules });
            }
            catch (Exception)
            {
                Interlocked.Increment(ref originalThrew);
                return true;
            }
            finally
            {
                inVerify = false;
            }
            if (ReferenceEquals(expected, found))
            {
                Interlocked.Increment(ref sameObject);
            }
            else if (SameMember(expected, found))
            {
                if (Interlocked.Increment(ref equalMember) <= 10)
                {
                    Log.Warning($"[RLO] Harmony identity map equal member: {mref} -> map {found} (reflected {found?.ReflectedType}) vs original {expected} (reflected {expected?.ReflectedType})");
                }
            }
            else if (Interlocked.Increment(ref mismatches) <= 20)
            {
                Log.Warning($"[RLO] Harmony identity map mismatch: {mref} -> map {found} ({found?.GetType().Name}) vs original {expected} ({expected?.GetType().Name})");
            }
            __result = expected;
            return false;
        }

        private static bool SameMember(MemberInfo a, MemberInfo b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            if (a is Type ta && b is Type tb)
            {
                return ta == tb;
            }
            if (a is MethodBase ma && b is MethodBase mb)
            {
                return ma.MethodHandle == mb.MethodHandle && ma.DeclaringType == mb.DeclaringType;
            }
            if (a is FieldInfo fa && b is FieldInfo fb)
            {
                return fa.FieldHandle == fb.FieldHandle && fa.DeclaringType == fb.DeclaringType;
            }
            return a.Equals(b);
        }

        public static string Stats()
        {
            if (!verify)
            {
                return installed ? "Harmony identity map: on" : "Harmony identity map: off";
            }
            string kinds;
            lock (missKinds)
            {
                kinds = string.Join(", ", missKinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));
            }
            return $"Harmony identity map (verify): {mapped} imports mapped, {hits} hits, {misses} misses ({kinds}), {sameObject} same object, {equalMember} equal member (other ReflectedType), {mismatches} mismatches, {originalThrew} where the original resolver threw";
        }
    }
}
