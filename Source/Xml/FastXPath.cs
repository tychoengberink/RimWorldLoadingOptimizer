using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class FastXPath
    {
        private static readonly Regex RootPath = new Regex(
            @"^/?(?:Defs|\*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static readonly Regex DefPath = new Regex(
            @"^/?(?:Defs|\*)/(?<tag>\*|[A-Za-z_][\w.\-]*)\[\s*(?<k>defName|@Name|@ParentName)\s*=\s*(?<v>""[^""]*""|'[^']*')(?:\s+or\s+(?<k>defName|@Name|@ParentName)\s*=\s*(?<v>""[^""]*""|'[^']*'))*\s*\](?<preds>(?:\[[^\[\]]*\])*)(?:/(?<rest>.+))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static readonly Regex UnsafeRest = new Regex(@"^/|\.\.|::|\bDefs\b|\|", RegexOptions.Compiled);

        private static readonly Regex UnsafePredicate = new Regex(
            @"\[\s*[\d(\-]|position\s*\(|last\s*\(|count\s*\(|sum\s*\(|number\s*\(|string-length\s*\(|floor\s*\(|ceiling\s*\(|round\s*\(|\.\.|::|/|\|",
            RegexOptions.Compiled
        );

        private static readonly Regex Positional = new Regex(@"\b(?:position|last)\s*\(", RegexOptions.Compiled);

        private static readonly Regex BooleanCall = new Regex(
            @"^(?:not|boolean|contains|starts-with|true|false)\s*\(",
            RegexOptions.Compiled
        );

        private static readonly Regex SimplePath = new Regex(
            @"^@?(?:[A-Za-z_][\w.\-]*|\*)(?:/@?(?:[A-Za-z_][\w.\-]*|\*))*$",
            RegexOptions.Compiled
        );

        private static readonly Regex Name = new Regex(@"^[A-Za-z_][\w.\-]*", RegexOptions.Compiled);

        private static readonly Regex PositionOnly = new Regex(
            @"^\[\s*(?:position\s*\(\s*\)\s*=\s*)?(?<n>\d+)\s*\]$",
            RegexOptions.Compiled
        );

        private static bool patching;
        private static int patchingThread;

        private static ConditionalWeakTable<XmlDocument, DefIndex> Indexes =
            new ConditionalWeakTable<XmlDocument, DefIndex>();

        private static readonly List<DefIndex> Attached = new List<DefIndex>();

        private static readonly Dictionary<string, int> FallbackShapes = new Dictionary<string, int>();
        private static readonly Regex Literal = new Regex(@"(""[^""]*""|'[^']*')", RegexOptions.Compiled);

        public static int Hits;
        public static int Fallbacks;
        public static readonly MismatchLog Mismatches = new MismatchLog("fast XPath");
        public static int PatchedMethods;
        public static readonly Stopwatch PatchTime = new Stopwatch();

        private static readonly MethodInfo XmlSelectNodes = AccessTools.Method(
            typeof(XmlNode),
            nameof(XmlNode.SelectNodes),
            new[] { typeof(string) }
        );
        private static readonly MethodInfo XmlSelectSingleNode = AccessTools.Method(
            typeof(XmlNode),
            nameof(XmlNode.SelectSingleNode),
            new[] { typeof(string) }
        );

        public static void Install(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(FastXPath), nameof(Transpiler)) { priority = Priority.Last };
            foreach (var type in CandidateTypes())
            {
                foreach (
                    var method in type.GetMethods(
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                )
                {
                    if (
                        method.IsAbstract
                        || method.ContainsGenericParameters
                        || type.ContainsGenericParameters
                        || !IlScan.CallsAny(method, XmlSelectNodes, XmlSelectSingleNode)
                    )
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, transpiler: transpiler);
                        PatchedMethods++;
                    }
                    catch (Exception e)
                    {
                        RLOLog.Warning($"could not accelerate {type.FullName}.{method.Name}: {e.Message}");
                    }
                }
            }
            harmony.Patch(
                AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                prefix: new HarmonyMethod(typeof(FastXPath), nameof(StartTimer)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(FastXPath), nameof(StopTimer)) { priority = Priority.Last }
            );
            var postInheritance = AccessTools.Method(
                AccessTools.TypeByName("PostInheritanceOperation.PatchQueue"),
                "ApplyPatches"
            );
            if (postInheritance != null)
            {
                harmony.Patch(
                    postInheritance,
                    prefix: new HarmonyMethod(typeof(FastXPath), nameof(StartTimer)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(FastXPath), nameof(StopWindow)) { priority = Priority.Last }
                );
            }
        }

        private static IEnumerable<Type> CandidateTypes()
        {
            var patchOperations = GenTypes
                .AllSubclasses(typeof(PatchOperation))
                .Concat(new[] { typeof(PatchOperation) })
                .ToList();
            var gameAssembly = typeof(PatchOperation).Assembly;
            var modAssemblies = patchOperations
                .Select(t => t.Assembly)
                .Where(a => a != gameAssembly && a != typeof(FastXPath).Assembly)
                .Distinct()
                .Where(AssemblySafety.CanScan);
            var types = new HashSet<Type>(patchOperations.Where(t => t.Assembly == gameAssembly));
            foreach (var assembly in modAssemblies)
            {
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    types.Add(type);
                }
            }
            return types;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(XmlSelectNodes))
                {
                    yield return new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(typeof(FastXPath), nameof(SelectNodes))
                    )
                        .WithLabels(instruction.labels)
                        .WithBlocks(instruction.blocks);
                }
                else if (instruction.Calls(XmlSelectSingleNode))
                {
                    yield return new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(typeof(FastXPath), nameof(SelectSingleNode))
                    )
                        .WithLabels(instruction.labels)
                        .WithBlocks(instruction.blocks);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        [Hook]
        public static void StartTimer()
        {
            patchingThread = Thread.CurrentThread.ManagedThreadId;
            patching = true;
            PatchTime.Start();
        }

        public static bool HasIndex(XmlDocument doc) => patching && Indexes.TryGetValue(doc, out _);

        public static Exception StopWindow(Exception __exception)
        {
            PatchTime.Stop();
            patching = false;
            foreach (var attached in Attached)
            {
                attached.Detach();
            }
            Attached.Clear();
            Indexes = new ConditionalWeakTable<XmlDocument, DefIndex>();
            return __exception;
        }

        [Hook]
        public static Exception StopTimer(Exception __exception)
        {
            StopWindow(null);
            RLOLog.Detail(
                $"XML patching took {PatchTime.Elapsed.TotalSeconds:F1} s; fast XPath hits {Hits}, fallbacks {Fallbacks}, accelerated methods {PatchedMethods}, index rebuilds {DefIndex.Rebuilds}, defs re-indexed {DefIndex.Reindexed}"
                    + (DevSwitches.Verify ? $", verify mismatches {Mismatches.Count}" : "")
                    + "\n  top fallback shapes:"
                    + string.Concat(
                        FallbackShapes
                            .OrderByDescending(p => p.Value)
                            .Take(12)
                            .Select(p => $"\n    {p.Value, 5}  {p.Key}")
                    )
            );
            return __exception;
        }

        public static XmlNodeList SelectNodes(XmlNode context, string xpath)
        {
            if (TryFast(context, xpath, out var fast))
            {
                if (DevSwitches.Verify)
                {
                    Check(xpath, fast, context.SelectNodes(xpath));
                }
                return fast;
            }
            return context.SelectNodes(xpath);
        }

        public static XmlNode SelectSingleNode(XmlNode context, string xpath)
        {
            if (TryFast(context, xpath, out var fast))
            {
                var node = fast.Count > 0 ? fast[0] : null;
                if (DevSwitches.Verify && node != context.SelectSingleNode(xpath))
                {
                    Mismatches.Report(xpath);
                }
                return node;
            }
            return context.SelectSingleNode(xpath);
        }
    }
}
