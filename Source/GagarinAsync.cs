using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            if (!Fingerprint.Matches("Gagarin async save", Fingerprint.Known(save, "0F967FD16F60343F", "B7DCAD4B24AB6AAC"), Fingerprint.Known(clean, "0303C036B1E2BC7E", "0BFD6EB9F6900BC0")))
            {
                return;
            }
            harmony.Patch(save, transpiler: new HarmonyMethod(typeof(GagarinAsync), nameof(Transpiler)));
            harmony.Patch(clean, prefix: new HarmonyMethod(typeof(GagarinAsync), nameof(Wait)));
            installed = true;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var create = AccessTools.Method(typeof(XmlWriter), nameof(XmlWriter.Create), new[] { typeof(string), typeof(XmlWriterSettings) });
            var docSave = AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.Save), new[] { typeof(XmlWriter) });
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(create))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinAsync), nameof(CreateWriter))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else if (instruction.Calls(docSave))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinAsync), nameof(SaveAsync))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else
                {
                    yield return instruction;
                }
            }
            if (replaced != 2)
            {
                throw new InvalidOperationException($"[RLO] Gagarin Save shape changed ({replaced} call sites)");
            }
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
            var temporary = path + ".rimory-tmp";
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
