#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    // Cherry Picker removes the defs a player picked from inside CherryPickerUtility's static constructor (Setup()).
    // That code reads CherryPickerUtility's own static fields hundreds of thousands of times while the class is still
    // being initialized, which takes the runtime's slow class-init path on every read. The constructor's last part
    // (timing, Setup(), the result message) runs unchanged right after the class is initialized instead, still inside
    // the same RunClassConstructor call; see CctorDeferral.
    public static class CherryPickerSetup
    {
        public const string Feature = "Cherry Picker setup after its static constructor";

        private static CctorDeferral.Entry entry;
        private static string verifyResult;

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("CherryPicker.CherryPickerUtility");
            if (type == null)
            {
                return;
            }
            var cctor = type.TypeInitializer;
            var setup = AccessTools.Method(type, "Setup", new[] { typeof(bool) });
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(cctor, "6B24CCF939C9E188"),
                Fingerprint.Known(setup, "72875492E04CCC85")))
            {
                return;
            }
            if (!Fingerprint.OnlyOurs(cctor))
            {
                Log.Message($"[RLO] {Feature}: another mod patches Cherry Picker's static constructor. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var stub = AccessTools.Method(typeof(CherryPickerSetup), nameof(Tail));
            entry = new CctorDeferral.Entry
            {
                Type = type,
                Cctor = cctor,
                Stub = stub,
                FindTail = list => FindTail(list, setup),
                Allowed = () => Fingerprint.OnlyOurs(cctor) && Fingerprint.OnlyOurs(stub),
            };
            if (!CctorDeferral.Register(harmony, entry, out var problem))
            {
                Log.Message($"[RLO] {Feature}: {problem}. This speedup is skipped and the original code runs; nothing is broken.");
                entry = null;
                return;
            }
            if (RLOMod.Verify)
            {
                verifyResult = CctorDeferral.CompareTail(entry);
                if (verifyResult != null)
                {
                    entry.Problem = verifyResult;
                    Log.Warning($"[RLO] {Feature}: verify: the moved code differs from Cherry Picker's original ({verifyResult}); the original runs instead.");
                }
            }
            LeanFixes.Reports.Add(Stats);
        }

        // newobj Stopwatch::.ctor; dup; callvirt Stopwatch::Start; ldc.i4.0; call Setup(bool) ... ret
        private static int FindTail(List<CodeInstruction> list, MethodInfo setup)
        {
            var found = -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Call && list[i].operand is MethodInfo m && m == setup)
                {
                    if (found >= 0)
                    {
                        return -1;
                    }
                    found = i;
                }
            }
            if (found < 4
                || !(list[found - 4].opcode == OpCodes.Newobj && list[found - 4].operand is ConstructorInfo ctor && ctor.DeclaringType == typeof(Stopwatch))
                || list[found - 3].opcode != OpCodes.Dup
                || !(list[found - 2].opcode == OpCodes.Callvirt && list[found - 2].operand is MethodInfo start && start.DeclaringType == typeof(Stopwatch) && start.Name == nameof(Stopwatch.Start))
                || list[found - 1].opcode != OpCodes.Ldc_I4_0)
            {
                return -1;
            }
            return found - 4;
        }

        // Replaced by the tail of CherryPickerUtility's static constructor.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Tail()
        {
            throw new NotImplementedException("[RLO] Cherry Picker static constructor tail was not built");
        }

        private static string Stats()
        {
            if (entry == null)
            {
                return $"{Feature}: off";
            }
            var text = entry.Deferred > 0
                ? $"{Feature}: ran after the class was initialized in {LeanFixes.Seconds(entry.Ticks):F2} s"
                : $"{Feature}: not used this start (the class was first used outside its startup constructor call: {entry.Inline}x)";
            if (entry.Failed > 0)
            {
                text += $", {entry.Failed} failed and were reported as a static constructor error";
            }
            if (RLOMod.Verify)
            {
                text += $"; verify: moved code {(verifyResult == null ? "identical to the original" : "differs: " + verifyResult)}";
            }
            return text;
        }
    }
}
#endif
