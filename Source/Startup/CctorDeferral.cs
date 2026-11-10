#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace RimWorldLoadingOptimizer
{
    // While a static constructor runs, the runtime has not marked its class initialized yet, so every access to one of
    // that class's static fields from other code (methods it calls, lambdas) goes through the class-init helper's slow
    // path. A static constructor that ends with a long loop over the class's own fields pays that on every access.
    // This moves such a tail - an exact copy of the constructor's own IL from a given instruction to its end - out of
    // the constructor and runs it right after RuntimeHelpers.RunClassConstructor has initialized the class, before
    // RunClassConstructor returns to its caller. Nothing else runs in between: the tail runs in the first postfix of
    // RunClassConstructor, and is only moved while no other mod has a postfix there. When the class is initialized any
    // other way (a first static access from code, another class's constructor, another thread) the constructor runs
    // its tail inline as before. An exception from the moved tail is rethrown as the TypeInitializationException the
    // runtime would have thrown, to the same caller. Verse-free so it can be tested outside the game.
    public static class CctorDeferral
    {
        public sealed class Entry
        {
            public Type Type;
            public ConstructorInfo Cctor;
            // A static void method without parameters or locals whose body is replaced by the constructor's tail.
            public MethodInfo Stub;
            // Index of the first tail instruction in the constructor's instructions, or -1. The evaluation stack must
            // be empty there.
            public Func<List<CodeInstruction>, int> FindTail;
            // Checked each time the constructor reaches the tail; false runs the tail inline.
            public Func<bool> Allowed = () => true;

            internal IntPtr Handle;
            internal List<CodeInstruction> Tail;
            internal LocalVariableInfo[] Locals;
            internal int TailStart = -1;
            internal bool CctorPatched, StubBuilt;
            internal Action Run;

            public string Problem;
            public int Deferred, Inline, Ran, Failed;
            public long Ticks;

            public bool Ready => Run != null && Problem == null;
        }

        private static readonly Dictionary<MethodBase, Entry> byMethod = new Dictionary<MethodBase, Entry>();
        private static readonly Dictionary<IntPtr, Entry> byHandle = new Dictionary<IntPtr, Entry>();
        private static readonly object gate = new object();
        private static MethodInfo runClassConstructor;
        private static string harmonyId;

        [ThreadStatic] private static List<IntPtr> running;
        [ThreadStatic] private static Entry pending;

        public static bool Register(Harmony harmony, Entry entry, out string problem)
        {
            problem = null;
            if (entry.Type == null || entry.Cctor == null || entry.Stub == null || entry.FindTail == null)
            {
                problem = "a method it needs no longer exists";
                return false;
            }
            entry.Handle = entry.Type.TypeHandle.Value;
            lock (gate)
            {
                byMethod[entry.Cctor] = entry;
                byMethod[entry.Stub] = entry;
                byHandle[entry.Handle] = entry;
            }
            try
            {
                harmony.Patch(entry.Cctor, transpiler: new HarmonyMethod(typeof(CctorDeferral), nameof(CctorTranspiler)));
                if (!entry.CctorPatched)
                {
                    problem = entry.Problem ?? "the slow part of the static constructor was not found as expected";
                    harmony.Unpatch(entry.Cctor, HarmonyPatchType.Transpiler, harmony.Id);
                    return false;
                }
                harmony.Patch(entry.Stub, transpiler: new HarmonyMethod(typeof(CctorDeferral), nameof(StubTranspiler)));
                if (!entry.StubBuilt || entry.Problem != null)
                {
                    problem = entry.Problem ?? "the copy of the static constructor's slow part could not be built";
                    harmony.Unpatch(entry.Stub, HarmonyPatchType.Transpiler, harmony.Id);
                    harmony.Unpatch(entry.Cctor, HarmonyPatchType.Transpiler, harmony.Id);
                    return false;
                }
                Hook(harmony);
                entry.Run = (Action)Delegate.CreateDelegate(typeof(Action), entry.Stub);
                return true;
            }
            catch (Exception e)
            {
                entry.Problem = problem = $"patching failed ({e.GetType().Name}: {e.Message})";
                try
                {
                    harmony.Unpatch(entry.Stub, HarmonyPatchType.Transpiler, harmony.Id);
                    harmony.Unpatch(entry.Cctor, HarmonyPatchType.Transpiler, harmony.Id);
                }
                catch (Exception)
                {
                }
                return false;
            }
        }

        private static void Hook(Harmony harmony)
        {
            if (runClassConstructor != null)
            {
                return;
            }
            var method = AccessTools.Method(typeof(RuntimeHelpers), nameof(RuntimeHelpers.RunClassConstructor), new[] { typeof(RuntimeTypeHandle) });
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(CctorDeferral), nameof(Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(CctorDeferral), nameof(Postfix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(CctorDeferral), nameof(Finalizer)) { priority = Priority.First });
            harmonyId = harmony.Id;
            runClassConstructor = method;
        }

        public static void Prefix(RuntimeTypeHandle type, out bool __state)
        {
            (running ?? (running = new List<IntPtr>())).Add(type.Value);
            __state = true;
        }

        public static void Postfix(RuntimeTypeHandle type)
        {
            RunPending(type);
        }

        public static void Finalizer(RuntimeTypeHandle type, bool __state)
        {
            var stack = running;
            if (__state && stack != null && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            // Only reached with a moved tail still waiting when something threw after the constructor returned and
            // before the postfix ran; the class is initialized by then, so the tail still has to run.
            RunPending(type);
        }

        private static void RunPending(RuntimeTypeHandle type)
        {
            var entry = pending;
            if (entry == null || entry.Handle != type.Value)
            {
                return;
            }
            pending = null;
            var start = Stopwatch.GetTimestamp();
            try
            {
                entry.Run();
                entry.Ran++;
            }
            catch (Exception e)
            {
                entry.Failed++;
                throw new TypeInitializationException(entry.Type.FullName, e);
            }
            finally
            {
                entry.Ticks += Stopwatch.GetTimestamp() - start;
            }
        }

        // Called by the patched constructor where its tail starts; true means return now, the tail runs after.
        public static bool Defer(RuntimeTypeHandle type)
        {
            Entry entry;
            lock (gate)
            {
                if (!byHandle.TryGetValue(type.Value, out entry))
                {
                    return false;
                }
            }
            var stack = running;
            if (stack == null || stack.Count == 0 || stack[stack.Count - 1] != type.Value || pending != null || !entry.Ready || !OnlyOurPostfixes() || !Allowed(entry))
            {
                entry.Inline++;
                return false;
            }
            pending = entry;
            entry.Deferred++;
            return true;
        }

        private static bool Allowed(Entry entry)
        {
            try
            {
                return entry.Allowed();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool OnlyOurPostfixes()
        {
            var info = Harmony.GetPatchInfo(runClassConstructor);
            return info != null && info.Postfixes.All(p => p.owner == harmonyId) && info.Postfixes.Any(p => p.PatchMethod.DeclaringType == typeof(CctorDeferral));
        }

        public static IEnumerable<CodeInstruction> CctorTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
        {
            var list = instructions.ToList();
            Entry entry;
            lock (gate)
            {
                if (!byMethod.TryGetValue(__originalMethod, out entry))
                {
                    return list;
                }
            }
            if (entry.Tail != null)
            {
                // Rebuilt because another patch was added: the tail copied the first time stays as it was, and the
                // constructor only moves it while every patch on it is ours (entry.Allowed).
                if (Index(list, entry) == entry.TailStart)
                {
                    Insert(list, entry.TailStart, entry, generator);
                }
                return list;
            }
            var start = Index(list, entry);
            var problem = start < 0 ? "the slow part of the static constructor was not found as expected" : Check(list, start, __originalMethod);
            if (problem != null)
            {
                entry.Problem = problem;
                return list;
            }
            entry.TailStart = start;
            entry.Locals = __originalMethod.GetMethodBody().LocalVariables.OrderBy(l => l.LocalIndex).ToArray();
            entry.Tail = list.Skip(start).Select(Copy).ToList();
            Insert(list, start, entry, generator);
            entry.CctorPatched = true;
            return list;
        }

        private static int Index(List<CodeInstruction> list, Entry entry)
        {
            try
            {
                return entry.FindTail(list);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void Insert(List<CodeInstruction> list, int start, Entry entry, ILGenerator generator)
        {
            var tail = generator.DefineLabel();
            var first = new CodeInstruction(OpCodes.Ldtoken, entry.Type);
            first.labels.AddRange(list[start].labels);
            list[start].labels.Clear();
            list[start].labels.Add(tail);
            list.InsertRange(start, new[]
            {
                first,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CctorDeferral), nameof(Defer))),
                new CodeInstruction(OpCodes.Brfalse, tail),
                new CodeInstruction(OpCodes.Ret),
            });
        }

        private static string Check(List<CodeInstruction> list, int start, MethodBase method)
        {
            if (start <= 0 || start >= list.Count)
            {
                return "the slow part of the static constructor was not found as expected";
            }
            if (list.Any(i => i.blocks.Count > 0) || method.GetMethodBody().ExceptionHandlingClauses.Count > 0)
            {
                return "the static constructor has exception handlers";
            }
            var tailLabels = new HashSet<Label>(list.Skip(start + 1).SelectMany(i => i.labels));
            var allTailLabels = new HashSet<Label>(list.Skip(start).SelectMany(i => i.labels));
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var target in Targets(list[i]))
                {
                    if (i < start ? tailLabels.Contains(target) : !allTailLabels.Contains(target))
                    {
                        return "the static constructor jumps between its slow part and the rest";
                    }
                }
                var op = list[i].opcode;
                if (i >= start && (op == OpCodes.Ldarg || op == OpCodes.Ldarga || op == OpCodes.Starg || op == OpCodes.Ldarg_S || op == OpCodes.Ldarga_S || op == OpCodes.Starg_S || op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3 || op == OpCodes.Localloc || op == OpCodes.Jmp || op == OpCodes.Tailcall || op == OpCodes.Leave || op == OpCodes.Leave_S || op == OpCodes.Endfinally || op == OpCodes.Endfilter))
                {
                    return $"the slow part of the static constructor uses {op.Name}";
                }
            }
            if (list[list.Count - 1].opcode != OpCodes.Ret)
            {
                return "the static constructor does not end with a return";
            }
            return null;
        }

        private static IEnumerable<Label> Targets(CodeInstruction instruction)
        {
            switch (instruction.operand)
            {
                case Label label:
                    yield return label;
                    break;
                case Label[] labels:
                    foreach (var label in labels)
                    {
                        yield return label;
                    }
                    break;
            }
        }

        private static CodeInstruction Copy(CodeInstruction instruction)
        {
            var copy = new CodeInstruction(instruction.opcode, instruction.operand is Label[] labels ? labels.ToArray() : instruction.operand);
            copy.labels.AddRange(instruction.labels);
            return copy;
        }

        private static readonly OpCode[] shortLoads = { OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3 };
        private static readonly OpCode[] shortStores = { OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3 };

        public static IEnumerable<CodeInstruction> StubTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
        {
            Entry entry;
            lock (gate)
            {
                byMethod.TryGetValue(__originalMethod, out entry);
            }
            if (entry?.Tail == null)
            {
                return instructions;
            }
            var locals = entry.Locals.Select(l => generator.DeclareLocal(l.LocalType, l.IsPinned)).ToArray();
            var labels = new Dictionary<Label, Label>();
            Label Map(Label label)
            {
                if (!labels.TryGetValue(label, out var mapped))
                {
                    labels[label] = mapped = generator.DefineLabel();
                }
                return mapped;
            }
            LocalBuilder Local(int index)
            {
                if (index < 0 || index >= locals.Length)
                {
                    throw new InvalidOperationException($"local {index} out of range");
                }
                return locals[index];
            }
            var result = new List<CodeInstruction>();
            try
            {
                foreach (var source in entry.Tail)
                {
                    var opcode = source.opcode;
                    var operand = source.operand;
                    var load = Array.IndexOf(shortLoads, opcode);
                    var store = Array.IndexOf(shortStores, opcode);
                    if (load >= 0)
                    {
                        opcode = OpCodes.Ldloc;
                        operand = Local(load);
                    }
                    else if (store >= 0)
                    {
                        opcode = OpCodes.Stloc;
                        operand = Local(store);
                    }
                    else
                    {
                        switch (operand)
                        {
                            case LocalBuilder local:
                                operand = Local(local.LocalIndex);
                                break;
                            case Label label:
                                operand = Map(label);
                                break;
                            case Label[] targets:
                                operand = targets.Select(Map).ToArray();
                                break;
                            default:
                                if (opcode.OperandType == OperandType.ShortInlineVar || opcode.OperandType == OperandType.InlineVar)
                                {
                                    operand = Local(Convert.ToInt32(operand));
                                }
                                break;
                        }
                    }
                    var copy = new CodeInstruction(opcode, operand);
                    copy.labels.AddRange(source.labels.Select(Map));
                    result.Add(copy);
                }
            }
            catch (Exception e)
            {
                entry.Problem = $"the copy of the static constructor's slow part could not be built ({e.Message})";
                return instructions;
            }
            entry.StubBuilt = true;
            return result;
        }

        // Verify mode: the stub's body must be the constructor's original tail instruction for instruction.
        public static string CompareTail(Entry entry)
        {
            var original = PatchProcessor.GetOriginalInstructions(entry.Cctor);
            var start = entry.FindTail(original);
            if (start < 0)
            {
                return "tail not found in the original instructions";
            }
            var expected = Render(original.Skip(start).ToList());
            var actual = Render(PatchProcessor.GetCurrentInstructions(entry.Stub));
            if (expected.Count != actual.Count)
            {
                return $"{actual.Count} instructions instead of {expected.Count}";
            }
            for (var i = 0; i < expected.Count; i++)
            {
                if (expected[i] != actual[i])
                {
                    return $"instruction {i}: {actual[i]} instead of {expected[i]}";
                }
            }
            return null;
        }

        private static List<string> Render(List<CodeInstruction> list)
        {
            var at = new Dictionary<Label, int>();
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var label in list[i].labels)
                {
                    at[label] = i;
                }
            }
            string Target(Label label) => at.TryGetValue(label, out var i) ? "@" + i : "@?";
            return list.Select(c =>
            {
                var name = c.opcode.Name;
                var load = Array.IndexOf(shortLoads, c.opcode);
                var store = Array.IndexOf(shortStores, c.opcode);
                if (load >= 0)
                {
                    return "ldloc " + load;
                }
                if (store >= 0)
                {
                    return "stloc " + store;
                }
                if (name.EndsWith(".s", StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - 2);
                }
                switch (c.operand)
                {
                    case null:
                        return name;
                    case LocalBuilder local:
                        return name + " " + local.LocalIndex;
                    case Label label:
                        return name + " " + Target(label);
                    case Label[] targets:
                        return name + " " + string.Join(",", targets.Select(Target));
                    case MemberInfo member:
                        return name + " " + member.DeclaringType?.AssemblyQualifiedName + "::" + member + "#" + member.MetadataToken;
                    default:
                        return name + " " + c.operand.GetType().Name + ":" + c.operand;
                }
            }).ToList();
        }
    }
}
#endif
