using System;
using System.Reflection;
using HarmonyLib;

namespace RimWorldLoadingOptimizer
{
    public static class OriginalBody
    {
        private static readonly object gate;
        private static readonly Func<MethodBase, MethodBody> read;

        static OriginalBody()
        {
            try
            {
                var bodies = typeof(Harmony).Assembly.GetType("HarmonyLib.MethodBodyPatching");
                var method = bodies == null ? null : AccessTools.Method(bodies, "GetMethodBody", new[] { typeof(MethodBase) });
                var protocol = bodies == null ? null : AccessTools.Property(bodies, "ProtocolLock")?.GetValue(null);
                if (method != null && protocol != null && method.ReturnType == typeof(MethodBody))
                {
                    read = AccessTools.MethodDelegate<Func<MethodBase, MethodBody>>(method);
                    gate = protocol;
                }
            }
            catch (Exception)
            {
                read = null;
            }
        }

        public static MethodBody Of(MethodBase method)
        {
            if (method == null)
            {
                return null;
            }
            if (read != null)
            {
                try
                {
                    lock (gate)
                    {
                        return read(method);
                    }
                }
                catch (Exception)
                {
                }
            }
            return method.GetMethodBody();
        }
    }
}
