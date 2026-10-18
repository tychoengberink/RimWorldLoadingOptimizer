using System;
using System.Threading;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ChildCursor
    {
        private const int Slots = 16;

        private static AccessTools.FieldRef<object, XmlNode> containerRef;
        private static XmlDocument activeDoc;
        private static int version;
        private static bool inWindow;
        private static Thread windowThread;
        private static long hits, misses, verifyMismatches, mutations;

        [ThreadStatic] private static Slot[] slots;
        [ThreadStatic] private static int nextSlot;

        private sealed class Slot
        {
            public XmlNodeList List;
            public int Index;
            public XmlNode Node;
            public int Version;
        }

        public static void Install(Harmony harmony)
        {
            var type = typeof(XmlNode).Assembly.GetType("System.Xml.XmlChildNodes", true);
#if RW17
            containerRef = AccessTools.FieldRefAccess<XmlNode>(type, "_container");
#else
            containerRef = AccessTools.FieldRefAccess<XmlNode>(type, "container");
#endif
            var item = AccessTools.Method(type, "Item", new[] { typeof(int) });
            harmony.Patch(item, prefix: new HarmonyMethod(typeof(ChildCursor), nameof(ItemPrefix)) { priority = Priority.Last });
            MainMenu.UnpatchAfterStartup(item);
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ParseAndProcessXML)),
                prefix: new HarmonyMethod(typeof(ChildCursor), nameof(WindowPrefix)),
                finalizer: new HarmonyMethod(typeof(ChildCursor), nameof(WindowFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(XmlInheritance), nameof(XmlInheritance.Resolve)), postfix: new HarmonyMethod(typeof(ChildCursor), nameof(ResolvePostfix)) { priority = Priority.Last });
        }

        [Hook]
        public static void WindowPrefix(XmlDocument xmlDoc)
        {
            inWindow = true;
            windowThread = Thread.CurrentThread;
            activeDoc = xmlDoc;
        }

        [Hook]
        public static void ResolvePostfix()
        {
            if (!inWindow || windowThread != Thread.CurrentThread || activeDoc == null || slots != null)
            {
                return;
            }
            activeDoc.NodeInserted += OnMutation;
            activeDoc.NodeRemoved += OnMutation;
            Interlocked.Increment(ref version);
            slots = new Slot[Slots];
            for (var i = 0; i < Slots; i++)
            {
                slots[i] = new Slot();
            }
        }

        [Hook]
        public static Exception WindowFinalizer(Exception __exception)
        {
            if (windowThread == Thread.CurrentThread)
            {
                if (slots != null && activeDoc != null)
                {
                    activeDoc.NodeInserted -= OnMutation;
                    activeDoc.NodeRemoved -= OnMutation;
                    Log.Message($"[RLO] child-list cursor: {hits} sequential hits, {misses} walks, {mutations} document mutations seen" + (RLOMod.Verify ? $", verify mismatches {verifyMismatches}" : ""));
                }
                slots = null;
                inWindow = false;
                windowThread = null;
                activeDoc = null;
            }
            return __exception;
        }

        private static void OnMutation(object sender, XmlNodeChangedEventArgs e)
        {
            Interlocked.Increment(ref version);
            mutations++;
        }

        public static bool ItemPrefix(XmlNodeList __instance, int i, ref XmlNode __result)
        {
            var threadSlots = slots;
            if (threadSlots == null || i < 0)
            {
                return true;
            }
            var container = containerRef(__instance);
            if (container == null || container.OwnerDocument != activeDoc)
            {
                return true;
            }
            var currentVersion = Volatile.Read(ref version);
            XmlNode found = null;
            var matched = false;
            for (var k = 0; k < Slots; k++)
            {
                var slot = threadSlots[k];
                if (slot.List != __instance)
                {
                    continue;
                }
                if (slot.Version == currentVersion && slot.Node != null)
                {
                    if (i == slot.Index)
                    {
                        found = slot.Node;
                        matched = true;
                    }
                    else if (i == slot.Index + 1)
                    {
                        found = slot.Node.NextSibling;
                        matched = true;
                    }
                }
                if (matched)
                {
                    hits++;
                    if (RLOMod.Verify && Walk(container, i) != found)
                    {
                        verifyMismatches++;
                    }
                    slot.Index = i;
                    slot.Node = found;
                    __result = found;
                    return false;
                }
                slot.List = null;
                break;
            }
            misses++;
            found = Walk(container, i);
            if (found != null)
            {
                var slot = threadSlots[nextSlot];
                nextSlot = (nextSlot + 1) % Slots;
                slot.List = __instance;
                slot.Index = i;
                slot.Node = found;
                slot.Version = currentVersion;
            }
            __result = found;
            return false;
        }

        private static XmlNode Walk(XmlNode container, int i)
        {
            var node = container.FirstChild;
            while (node != null)
            {
                if (i == 0)
                {
                    return node;
                }
                node = node.NextSibling;
                i--;
            }
            return null;
        }
    }
}
