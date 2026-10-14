using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Convai.Domain.Embodiment.Modules;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards for the embodiment release pass: the rules that, once broken, reintroduce a defect
    ///     this pass fixed.
    /// </summary>
    /// <remarks>
    ///     Two of these gates are deliberately shaped as <b>allow-lists</b> rather than absolutes,
    ///     because an adversarial review proved the absolute versions unshippable: the layer contains
    ///     intentional static registries, and "no references" is unsound in Unity because serialized
    ///     components are referenced by GUID rather than by type name.
    /// </remarks>
    [Category("Architecture")]
    public sealed class EmbodimentReleaseGuardTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        // ── hidden components ───────────────────────────────────────────────────────

        [Test]
        public void NoPackageRuntimeComponent_HidesItselfInTheInspector()
        {
            // Regression guard for the defect that shipped: because the module base class runs in
            // Edit Mode, a HideInInspector stamp was serialized into every scene a Convai module was
            // added to — including both samples, which shipped with an invisible composition root.
            var offenders = new List<string>();

            foreach (string file in EnumerateSourceFiles("SDK"))
            {
                string source = File.ReadAllText(file);
                if (source.Contains("HideFlags.HideInInspector") && !IsOnlyInComment(source, "HideFlags.HideInInspector"))
                    offenders.Add(ToPackagePath(file));
            }

            Assert.IsEmpty(offenders,
                "A component the user cannot see is a component they cannot debug:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void ShippedSampleScenes_ContainNoHiddenComponent()
        {
            var offenders = new List<string>();
            string samples = Path.Combine(PackageRoot, "Samples");
            if (!Directory.Exists(samples)) Assert.Pass("No samples in this package layout.");

            foreach (string scene in Directory.EnumerateFiles(samples, "*.unity", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(scene).Contains("m_ObjectHideFlags: 2"))
                    offenders.Add(ToPackagePath(scene));
            }

            Assert.IsEmpty(offenders,
                "A shipped sample must not carry a component the user cannot select:\n" +
                string.Join("\n", offenders));
        }

        // ── logging ─────────────────────────────────────────────────────────────────

        [Test]
        public void SharedEmbodimentLayer_LogsThroughConvaiLogger()
        {
            // One file is allowed: the shared setup reporter, whose whole job is to guarantee a
            // setup error reaches the console even when no ConvaiLogger sink is installed — which is
            // exactly the case where routing through the logger alone would lose the message. Every
            // other file must go through it or through ConvaiLogger.
            var allowed = new HashSet<string> { "SDK/Runtime/Embodiment/EmbodimentDiagnostics.cs" };
            var offenders = new List<string>();

            // SDK/Modules/Embodiment is in this list because leaving it out is how a third copy of
            // the fallback survived: the preset binding spelled out ConvaiLogger-then-Debug inline,
            // in the one embodiment directory nothing scanned.
            foreach (string root in new[]
                     { "SDK/Runtime/Animation", "SDK/Runtime/Embodiment", "SDK/Modules/Embodiment" })
            {
                string absolute = Path.Combine(PackageRoot, root.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(absolute)) continue;

                foreach (string file in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
                {
                    string packagePath = ToPackagePath(file);
                    if (allowed.Contains(packagePath)) continue;

                    string source = File.ReadAllText(file);
                    foreach (string call in new[] { "Debug.Log(", "Debug.LogWarning(", "Debug.LogError(", "Debug.LogException(" })
                    {
                        if (source.Contains(call) && !IsOnlyInComment(source, call))
                            offenders.Add($"{packagePath} uses {call}");
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "Embodiment diagnostics go through ConvaiLogger so they carry a category and can be " +
                "filtered:\n" + string.Join("\n", offenders));
        }

        // ── static state ────────────────────────────────────────────────────────────

        [Test]
        public void EmbodimentStaticState_MatchesTheReviewedAllowList()
        {
            // NOT "zero static mutable fields" — that gate is impossible. The layer has intentional
            // static registries (cross-character contagion, driver rosters, a ref-counted mask cache)
            // that this pass does not remove. The point of the allow-list is that adding a *new* one
            // is a review decision rather than an accident.
            var allowed = new HashSet<string>
            {
                "ConvaiConversationFlowDriverRegistry",
                "EmotionContagionRegistry",
                "ConvaiCharacterGazeRegistry",
                "RuntimeMaskCache",
                // The one Runtime-cannot-reference-a-module seam. Write-once and logged.
                "EmbodimentContextConversationFlowProvisioner",
            };

            var offenders = new List<string>();

            foreach (string file in EnumerateSourceFiles("SDK/Runtime/Embodiment"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (allowed.Contains(name)) continue;

                foreach (string line in File.ReadAllLines(file))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("private static") && !trimmed.StartsWith("public static")
                        && !trimmed.StartsWith("internal static")) continue;
                    if (trimmed.Contains("static readonly") || trimmed.Contains("const ")) continue;
                    // Methods and properties are not state.
                    if (trimmed.Contains("(") || trimmed.Contains("=>")) continue;
                    // Neither is a type declaration — `internal static class Foo` is not a field.
                    if (trimmed.Contains(" class ") || trimmed.Contains(" struct ")
                        || trimmed.Contains(" enum ") || trimmed.Contains(" interface ")) continue;

                    offenders.Add($"{ToPackagePath(file)}: {trimmed}");
                }
            }

            Assert.IsEmpty(offenders,
                "New process-global mutable state in the embodiment layer needs a deliberate decision " +
                "and an allow-list entry:\n" + string.Join("\n", offenders));
        }

        // ── the catalog is the single source of truth ────────────────────────────────

        [Test]
        public void EveryModuleIdConstant_IsClaimedByExactlyOneModule()
        {
            // The drift that shipped a false warning on Convai's own sample asset: ModuleIds said one
            // thing, a hand-written editor map said another. There is one list now, and this asserts
            // the constants and the declarations agree in BOTH directions.
            List<string> constants = typeof(ModuleIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .ToList();

            var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Type type in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name.StartsWith("Convai."))
                         .SelectMany(SafeTypes))
            {
                var attribute = type.GetCustomAttribute<EmbodimentModuleAttribute>(false);
                if (attribute == null) continue;

                Assert.IsFalse(declared.ContainsKey(attribute.ModuleId),
                    $"'{attribute.ModuleId}' is claimed by both {declared.GetValueOrDefault(attribute.ModuleId)} " +
                    $"and {type.Name}.");
                declared[attribute.ModuleId] = type.Name;
            }

            var undeclared = constants.Where(c => !declared.ContainsKey(c)).ToList();
            Assert.IsEmpty(undeclared,
                "These ModuleIds constants route to nothing, so a preset using one silently does " +
                "nothing: " + string.Join(", ", undeclared));

            var unconstanted = declared.Keys.Where(k => !constants.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            Assert.IsEmpty(unconstanted,
                "These modules declare an id that is not a ModuleIds constant, so it cannot be " +
                "referenced safely: " + string.Join(", ", unconstanted));
        }

        [Test]
        public void EveryDeclaredModule_ResolvesItsProfileTypeFromItsBase()
        {
            var offenders = new List<string>();

            foreach (Type type in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name.StartsWith("Convai."))
                         .SelectMany(SafeTypes))
            {
                if (type.GetCustomAttribute<EmbodimentModuleAttribute>(false) == null) continue;

                bool derivesFromModuleBase = false;
                for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                {
                    if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(ConvaiCharacterModule<>)) continue;
                    derivesFromModuleBase = true;
                    break;
                }

                if (!derivesFromModuleBase)
                    offenders.Add(type.Name);
            }

            Assert.IsEmpty(offenders,
                "A declared module must derive from ConvaiCharacterModule<TProfile> — that base is where " +
                "the profile type comes from, so the catalog cannot describe one without it: " +
                string.Join(", ", offenders));
        }

        // ── menu grammar ────────────────────────────────────────────────────────────

        [Test]
        public void EmbodimentMenuLeaves_CarryNoImplementationSuffix()
        {
            string[] banned = { "Controller", "Binding", "Driver", "Handler", "Player", "Source", "Host" };
            var offenders = new List<string>();

            foreach (Type type in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name.StartsWith("Convai."))
                         .SelectMany(SafeTypes))
            {
                var menu = type.GetCustomAttribute<AddComponentMenu>(false);
                if (menu == null || string.IsNullOrEmpty(menu.componentMenu)) continue;
                if (!menu.componentMenu.StartsWith("Convai/Embodiment/")) continue;

                string leaf = menu.componentMenu.Substring("Convai/Embodiment/".Length);
                if (leaf.Contains("/")) continue; // satellites live under their own root

                foreach (string suffix in banned)
                {
                    if (leaf.EndsWith(" " + suffix))
                        offenders.Add($"{menu.componentMenu} ({type.Name})");
                }
            }

            Assert.IsEmpty(offenders,
                "A user picking from Add Component reads a feature name, not a class role:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void NoMenuItem_IsGatedBehindAnUndefinedSymbol()
        {
            // The Embodiment Live Inspector shipped with its [MenuItem] inside
            // #if CONVAI_INTERNAL_EMBODIMENT_LIVE_INSPECTOR, a symbol defined in no asmdef, no rsp and
            // no project setting — so 206 lines of editor code had no entry point at all.
            //
            // Scoped to the embodiment editor surface, which is what this release pass owns. Running
            // it across all of SDK/Editor also catches
            // ConvaiConfigurationWindowEditor's CONVAI_ENABLE_UPDATES_SECTION — a real instance of the
            // same bug, but pre-existing and outside this pass; widening the scope is a deliberate
            // follow-up, not something to smuggle in here.
            var offenders = new List<string>();

            foreach (string file in EnumerateSourceFiles("SDK/Editor/Embodiment"))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].TrimStart().StartsWith("#if ")) continue;

                    string symbol = lines[i].Trim().Substring(4).Trim();
                    if (!symbol.StartsWith("CONVAI_")) continue;

                    // Does a [MenuItem] sit inside this block?
                    for (int j = i + 1; j < lines.Length && !lines[j].TrimStart().StartsWith("#endif"); j++)
                    {
                        if (!lines[j].Contains("[MenuItem(")) continue;
                        if (IsSymbolDefinedAnywhere(symbol)) break;

                        offenders.Add($"{ToPackagePath(file)}: [MenuItem] gated behind undefined '{symbol}'");
                        break;
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "An editor entry point behind a symbol nobody defines is dead code that looks alive:\n" +
                string.Join("\n", offenders));
        }

        // ── the documented extension point is reachable ──────────────────────────────

        [Test]
        public void PublicTickableContract_HasAPublicRegistrationPath()
        {
            // IEmbodimentTickable is public by a recorded decision, and EMBODIMENT.md tells a
            // customer to implement it. The scheduler that drives it is internal, so without a
            // public register/unregister pair on the context that promise cannot be kept: the
            // interface was implementable and unreachable at the same time.
            Assert.IsTrue(typeof(IEmbodimentTickable).IsPublic,
                "IEmbodimentTickable is the documented extension point and must stay public.");

            MethodInfo register = typeof(EmbodimentContext).GetMethod(
                "RegisterTickable", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(IEmbodimentTickable) }, null);
            MethodInfo unregister = typeof(EmbodimentContext).GetMethod(
                "UnregisterTickable", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(IEmbodimentTickable) }, null);

            Assert.NotNull(register,
                "EmbodimentContext must expose a public RegisterTickable(IEmbodimentTickable).");
            Assert.NotNull(unregister,
                "EmbodimentContext must expose a public UnregisterTickable(IEmbodimentTickable).");
            Assert.AreEqual(typeof(bool), register.ReturnType,
                "RegisterTickable reports whether the tickable joined a scheduler.");
        }

        // ── every module can describe its own absence ────────────────────────────────

        [Test]
        public void EveryDeclaredModule_SaysWhatIsLostWithoutIt()
        {
            // The character map builds "Without it, …" from this. It used to build that line by
            // lower-casing the Description instead, which shipped "Without it: no where the
            // character looks — eye contact, glances, and attention."
            var offenders = new List<string>();

            foreach (Type type in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name.StartsWith("Convai."))
                         .SelectMany(SafeTypes))
            {
                var attribute = type.GetCustomAttribute<EmbodimentModuleAttribute>(false);
                if (attribute == null) continue;

                if (string.IsNullOrWhiteSpace(attribute.Absence))
                {
                    offenders.Add($"{type.Name} declares no Absence");
                    continue;
                }

                // It has to complete "Without it, " — so it starts lower-case and ends a sentence.
                if (char.IsUpper(attribute.Absence[0]))
                    offenders.Add($"{type.Name}: Absence must continue the sentence, so it starts lower-case");
                if (!attribute.Absence.TrimEnd().EndsWith("."))
                    offenders.Add($"{type.Name}: Absence must end with a full stop");
            }

            Assert.IsEmpty(offenders,
                "A module's Absence completes \"Without it, \" in the character map:\n" +
                string.Join("\n", offenders));
        }

        // ── no inspector claims behaviour the runtime removed ────────────────────────

        [Test]
        public void NoEditorSurface_DescribesTheRemovedLocalContextFallback()
        {
            // An embodiment component off a Convai character is disabled — TryResolve refuses to
            // grow a context on a non-character object. Editor copy that still promises a "local
            // context" or a "local fallback" is telling the user the opposite of what happens, at
            // the exact moment they made the mistake.
            string[] banned = { "local context", "Local Fallback", "local fallback" };
            var offenders = new List<string>();

            foreach (string root in new[] { "SDK/Editor", "SDK/Modules" })
            {
                foreach (string file in EnumerateSourceFiles(root))
                {
                    string source = File.ReadAllText(file);
                    foreach (string phrase in banned)
                    {
                        if (source.IndexOf(phrase, StringComparison.Ordinal) >= 0)
                            offenders.Add($"{ToPackagePath(file)} mentions '{phrase}'");
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "The runtime disables a misplaced embodiment component; no surface may promise it " +
                "still works:\n" + string.Join("\n", offenders));
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private static bool IsSymbolDefinedAnywhere(string symbol)
        {
            foreach (string asmdef in Directory.EnumerateFiles(
                         Path.Combine(PackageRoot, "SDK"), "*.asmdef", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(asmdef).Contains(symbol)) return true;
            }

            foreach (string rsp in Directory.EnumerateFiles(PackageRoot, "*.rsp", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(rsp).Contains(symbol)) return true;
            }

            string projectSettings = Path.GetFullPath(Path.Combine(
                UnityEngine.Application.dataPath, "..", "ProjectSettings", "ProjectSettings.asset"));
            return File.Exists(projectSettings) && File.ReadAllText(projectSettings).Contains(symbol);
        }

        private static IEnumerable<string> EnumerateSourceFiles(string packageRelativeRoot)
        {
            string absolute = Path.Combine(
                PackageRoot, packageRelativeRoot.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(absolute)
                ? Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
        }

        /// <summary>
        ///     Whether every occurrence of <paramref name="token" /> sits in a comment or doc block —
        ///     so a rule can be *explained* in the file that used to break it.
        /// </summary>
        private static bool IsOnlyInComment(string source, string token)
        {
            foreach (string line in source.Split('\n'))
            {
                int at = line.IndexOf(token, StringComparison.Ordinal);
                if (at < 0) continue;

                string before = line.Substring(0, at);
                if (!before.Contains("//") && !before.Contains("///") && !before.TrimStart().StartsWith("*"))
                    return false;
            }

            return true;
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        private static string ToPackagePath(string path) =>
            Path.GetRelativePath(PackageRoot, path).Replace('\\', '/');
    }
}
