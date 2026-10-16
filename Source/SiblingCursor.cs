#if RW17
using System;
using System.Threading;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    // XmlLinkedNode.PreviousSibling walks the parent's child list from the first child, so XmlNode.RemoveChild and
    // InsertBefore cost O(position). Code that replaces or removes many children of one big parent in document order
    // (Adaptive Storage Framework replaces every inherited def under the <Defs> root this way) becomes quadratic: with
    // ~100k defs that is billions of steps. This remembers the last previous sibling that was found and first walks
    // forward from there; the answer is the node whose next sibling is this node, so it is the same node the full walk
    // finds. If the remembered node is no longer a child of the same parent, or the node is not found within a few
    // steps, the original walk runs.
    public static class SiblingCursor
    {
        private const string Feature = "XML previous-sibling cursor";
        private const int MaxSteps = 512;
        private static XmlNode cursor;
        private static long hits, walks, verifyMismatches;

        public static void Install(Harmony harmony)
        {
            var getter = AccessTools.PropertyGetter(typeof(XmlLinkedNode), nameof(XmlLinkedNode.PreviousSibling));
            if (!Fingerprint.Matches(Feature, Fingerprint.Known(getter, "67249CB81B31E557", "2A30411DA1C38C76")))
            {
                return;
            }
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(SiblingCursor), nameof(Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(SiblingCursor), nameof(Postfix)) { priority = Priority.First });
            MainMenu.UnpatchAfterStartup(getter);
            MainMenu.Then(() => cursor = null);
            LeanFixes.Reports.Add(Stats);
        }

        public static bool Prefix(XmlLinkedNode __instance, ref XmlNode __result)
        {
            var start = cursor;
            if (start == null || start == __instance)
            {
                return true;
            }
            var parent = __instance.ParentNode;
            if (parent == null || start.ParentNode != parent)
            {
                return true;
            }
            var node = start;
            for (var steps = 0; steps < MaxSteps && node != null; steps++)
            {
                var next = node.NextSibling;
                if (next == __instance)
                {
                    Interlocked.Increment(ref hits);
                    if (RLOMod.Verify && Walk(__instance, parent) != node && Interlocked.Increment(ref verifyMismatches) <= 10)
                    {
                        Log.Warning($"[RLO] {Feature} verify mismatch under <{parent.Name}>");
                        return true;
                    }
                    __result = node;
                    return false;
                }
                node = next;
            }
            return true;
        }

        public static void Postfix(XmlNode __result, bool __runOriginal)
        {
            if (__result != null)
            {
                cursor = __result;
            }
            if (__runOriginal)
            {
                Interlocked.Increment(ref walks);
            }
        }

        private static XmlNode Walk(XmlNode node, XmlNode parent)
        {
            var current = parent.FirstChild;
            while (current != null)
            {
                var next = current.NextSibling;
                if (next == node)
                {
                    break;
                }
                current = next;
            }
            return current;
        }

        public static string Stats() => $"{Feature}: {Interlocked.Read(ref hits)} previous siblings found from the last one, {Interlocked.Read(ref walks)} full walks"
            + (RLOMod.Verify ? $", {Interlocked.Read(ref verifyMismatches)} verify mismatches" : "");
    }
}
#endif
