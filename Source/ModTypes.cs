using System;
using System.Collections.Generic;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ModTypes
    {
        private static Dictionary<string, Type> byName;

        public static Type Find(string fullName)
        {
            if (byName == null)
            {
                byName = new Dictionary<string, Type>();
                foreach (var type in GenTypes.AllTypes)
                {
                    if (type.FullName != null && !byName.ContainsKey(type.FullName))
                    {
                        byName[type.FullName] = type;
                    }
                }
            }
            return byName.TryGetValue(fullName, out var found) ? found : null;
        }
    }
}
