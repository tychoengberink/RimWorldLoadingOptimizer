using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GagarinAsync
    {
        private static Harmony harmony;
        private static bool attempted, installed;
        private static string path;
        private static XmlWriterSettings settings;
        private static Thread writer;
        private static Exception failure;
        private static double writeSeconds;

        public static void Install(Harmony harmony)
        {
            GagarinAsync.harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ParseAndProcessXML)), prefix: new HarmonyMethod(typeof(GagarinAsync), nameof(ParsePrefix)));
        }

        [Hook]
        public static void ParsePrefix()
        {
            if (attempted)
            {
                return;
            }
            attempted = true;
            var type = AccessTools.TypeByName("Gagarin.CachedDefHelper");
            var save = type == null ? null : AccessTools.Method(type, "Save");
            var clean = type == null ? null : AccessTools.Method(type, "Clean");
            if (save == null || clean == null)
            {
                return;
            }
            try
            {
                harmony.Patch(save, transpiler: new HarmonyMethod(typeof(GagarinAsync), nameof(Transpiler)));
                if (!installed)
                {
                    return;
                }
                harmony.Patch(clean, prefix: new HarmonyMethod(typeof(GagarinAsync), nameof(Wait)));
            }
            catch (Exception e)
            {
                installed = false;
                harmony.Unpatch(save, HarmonyPatchType.Transpiler, harmony.Id);
                Log.Warning("[RLO] Gagarin async save left unchanged: " + e);
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var create = AccessTools.Method(typeof(XmlWriter), nameof(XmlWriter.Create), new[] { typeof(string), typeof(XmlWriterSettings) });
            var docSave = AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.Save), new[] { typeof(XmlWriter) });
            var original = instructions.ToList();
            if (original.Count(i => i.Calls(create)) != 1 || original.Count(i => i.Calls(docSave)) != 1)
            {
                installed = false;
                Log.Warning("[RLO] Gagarin async save left unchanged: Save no longer has one writer and one document save");
                return original;
            }
            installed = true;
            return original.Select(instruction => instruction.Calls(create)
                ? new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinAsync), nameof(CreateWriter))).WithLabels(instruction.labels).WithBlocks(instruction.blocks)
                : instruction.Calls(docSave)
                    ? new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinAsync), nameof(SaveAsync))).WithLabels(instruction.labels).WithBlocks(instruction.blocks)
                    : instruction).ToList();
        }

        public static XmlWriter CreateWriter(string file, XmlWriterSettings writerSettings)
        {
            path = file;
            settings = writerSettings;
            return XmlWriter.Create(Stream.Null, writerSettings);
        }

        public static void SaveAsync(XmlDocument document, XmlWriter ignored)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            var file = path;
            var temporary = path + ".rlo-tmp";
            writer = new Thread(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    using (var fileWriter = XmlWriter.Create(temporary, settings))
                    {
                        document.Save(fileWriter);
                    }
                    File.Move(temporary, file);
                }
                catch (Exception e)
                {
                    failure = e;
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch
                    {
                    }
                }
                writeSeconds = stopwatch.Elapsed.TotalSeconds;
            }) { IsBackground = false, Name = "Gagarin cache writer" };
            writer.Start();
        }

        public static void Wait()
        {
            writer?.Join();
            writer = null;
            if (failure != null)
            {
                Log.Warning($"[RLO] Gagarin cache write failed, next start is cold: {failure}");
                failure = null;
            }
        }

        public static string Stats() => !attempted ? "gagarin async save: not reached" : !installed ? "gagarin async save: not installed" : $"gagarin async save: write {writeSeconds:F2} s off the def thread{(writer != null ? " (still running)" : "")}";
    }
}
