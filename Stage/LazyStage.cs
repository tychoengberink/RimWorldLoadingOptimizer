using System;
using System.Collections.Generic;
using System.Reflection;
using WaylithClr;

namespace RimWorldLoadingOptimizer
{
    public static class LazyStage
    {
        private sealed class Slot
        {
            public int Id;
            public MethodBase Method;
            public volatile bool Active;
        }

        private sealed class Stage : MethodBodyTransformer
        {
            public Stage() : base(1000)
            {
            }

            protected override bool CanTransform(RuntimeMethodHandle method)
            {
                var slot = Find(method);
                return slot != null && slot.Active;
            }

            protected override bool TryTransform(RuntimeMethodHandle method, in MethodBodyView body, ReadOnlySpan<MethodBodyClause> clauses, MethodBodyBuilder result)
            {
                var slot = Find(method);
                if (slot == null || !slot.Active)
                {
                    return false;
                }
                var target = slot.Method;
                var count = target.GetParameters().Length + (target.IsStatic ? 0 : 1);
                var code = new List<byte>(48);
                code.Add(0x20);
                Int(code, slot.Id);
                code.Add(0x28);
                Int(code, result.GetTokenFor(ensure));
                for (var i = 0; i < count; i++)
                {
                    if (i < 4)
                    {
                        code.Add((byte)(0x02 + i));
                    }
                    else if (i < 256)
                    {
                        code.Add(0x0E);
                        code.Add((byte)i);
                    }
                    else
                    {
                        code.Add(0xFE);
                        code.Add(0x09);
                        code.Add((byte)i);
                        code.Add((byte)(i >> 8));
                    }
                }
                code.Add(0x28);
                Int(code, target.MetadataToken);
                code.Add(0x2A);
                var stack = Math.Max(count, 1);
                var blob = new List<byte>(12 + code.Count) { 0x03, 0x30, (byte)stack, (byte)(stack >> 8) };
                Int(blob, code.Count);
                Int(blob, 0);
                blob.AddRange(code);
                result.Write(blob.ToArray());
                return true;
            }

            private static void Int(List<byte> to, int value)
            {
                to.Add((byte)value);
                to.Add((byte)(value >> 8));
                to.Add((byte)(value >> 16));
                to.Add((byte)(value >> 24));
            }
        }

        private static readonly Dictionary<RuntimeMethodHandle, Slot> slots = new Dictionary<RuntimeMethodHandle, Slot>();
        private static Stage stage;
        private static MethodInfo ensure;

        private static Slot Find(RuntimeMethodHandle handle)
        {
            lock (slots)
            {
                slots.TryGetValue(handle, out var slot);
                return slot;
            }
        }

        private static Slot Find(MethodBase method, out RuntimeMethodHandle handle)
        {
            handle = MethodBodyUpdater.GetDefinitionHandle(method);
            return Find(handle);
        }

        public static bool Start(MethodInfo ensureMethod)
        {
            if (!MethodBodyUpdater.IsSupported)
            {
                return false;
            }
            ensure = ensureMethod;
            stage = new Stage();
            stage.Register();
            return true;
        }

        public static Exception Place(MethodBase method, int id)
        {
            var handle = MethodBodyUpdater.GetDefinitionHandle(method);
            var slot = new Slot { Id = id, Method = method, Active = true };
            lock (slots)
            {
                slots[handle] = slot;
            }
            var failure = stage.Rebuild(handle);
            if (failure != null)
            {
                slot.Active = false;
            }
            return failure;
        }

        public static Exception Lift(MethodBase method)
        {
            var slot = Find(method, out var handle);
            if (slot == null)
            {
                return null;
            }
            slot.Active = false;
            return stage.Rebuild(handle);
        }

        public static Exception Restore(MethodBase method)
        {
            var slot = Find(method, out var handle);
            if (slot == null)
            {
                return null;
            }
            slot.Active = true;
            return stage.Rebuild(handle);
        }
    }
}
