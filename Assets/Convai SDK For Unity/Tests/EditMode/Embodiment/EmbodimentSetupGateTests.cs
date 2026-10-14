using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Editor.Embodiment.Setup;
using Convai.Modules.Embodiment.Presets;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     The embodiment setup release gate, as tests rather than as a walkthrough.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The gate was originally written as "a character with only a Convai Character component
    ///         can be taken to a working setup without opening the docs and without typing a string",
    ///         judged by a person. An adversarial review pointed out that this is an aspiration, not a
    ///         check: nothing about it can fail in CI. So each clause is restated here against a
    ///         fixture built in code.
    ///     </para>
    ///     <para>
    ///         Built in code rather than as a committed <c>.unity</c> asset on purpose: a fixture
    ///         scene would need a real humanoid avatar to be meaningful, which means shipping a model
    ///         into the test folder, and it would drift silently the moment someone edited it. A rig
    ///         assembled per test states its own preconditions.
    ///     </para>
    /// </remarks>
    public sealed class EmbodimentSetupGateTests
    {
        private readonly List<GameObject> _spawned = new();
        private readonly List<Object> _ownedAssets = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();

            for (int i = 0; i < _ownedAssets.Count; i++)
                if (_ownedAssets[i] != null) Object.DestroyImmediate(_ownedAssets[i]);
            _ownedAssets.Clear();
        }

        // ── the cold-character gate ─────────────────────────────────────────────────

        [Test]
        public void AnAnimatorFreeCharacter_IsWarnedWithoutBeingBlocked()
        {
            GameObject character = NewBareCharacter("Gate_Cold");

            EmbodimentSetupReport report = EmbodimentRigSetupService.Inspect(character);
            EmbodimentFinding finding = FindFinding(report, "rig.no-animator");

            Assert.That(finding.Id, Is.EqualTo("rig.no-animator"));
            Assert.That(finding.Severity, Is.EqualTo(EmbodimentFindingSeverity.Warning));
            Assert.That(report.HasBlocker, Is.False,
                "Animator-free characters are valid; modules that truly need one report their own blocker.");
            Assert.That(report.HeaderStatus, Is.EqualTo("Needs Attention"));
            Assert.That(finding.Message, Does.Contain("fine"));
            Assert.That(finding.Message, Does.Contain("their own blocking setup issue"));
        }

        [Test]
        public void AnObjectWithoutAConvaiCharacter_IsReportedWithAOneClickFix()
        {
            var stray = new GameObject("Gate_NotACharacter");
            _spawned.Add(stray);

            EmbodimentSetupReport report = EmbodimentRigSetupService.Inspect(stray);

            Assert.IsTrue(report.HasBlocker);
            Assert.IsTrue(HasFinding(report, "rig.not-a-character"));
            Assert.IsTrue(report.HasFixes,
                "The user must be able to fix this from the inspector, not from the docs.");
        }

        [Test]
        public void OneButton_TakesAHumanoidCharacterToReady()
        {
            // This is the gate: Inspect -> Apply -> Ready, with no string typed and nothing read.
            GameObject character = NewHumanoidCharacterWithFace("Gate_OneButton", ArKitBlendshapes());

            EmbodimentSetupReport before = EmbodimentRigSetupService.Inspect(character);
            Assert.AreNotEqual("Ready", before.HeaderStatus, "The fixture must start unconfigured.");

            EmbodimentRigSetupResult result = EmbodimentRigSetupService.Apply(character);
            Assert.IsTrue(result.Changed);
            Assert.IsNotEmpty(result.Summary, "Apply must say what it did.");

            EmbodimentSetupReport after = EmbodimentRigSetupService.Inspect(character);
            Assert.IsFalse(after.HasBlocker,
                "After the one button, nothing may still be blocking:\n" + Describe(after));
            Assert.NotNull(character.GetComponent<StandardRigBinding>(),
                "Apply must leave the rig authored and inspectable, not implicit.");
        }

        [Test]
        public void Apply_IsIdempotent()
        {
            GameObject character = NewHumanoidCharacterWithFace("Gate_Idempotent", ArKitBlendshapes());

            EmbodimentRigSetupService.Apply(character);
            EmbodimentRigSetupService.Apply(character);

            StandardRigBinding[] bindings = character.GetComponentsInChildren<StandardRigBinding>(true);
            Assert.AreEqual(1, bindings.Length,
                "Pressing the button twice must not add a second rig component.");
        }

        // ── rig detection per supported convention ──────────────────────────────────

        [Test]
        public void ArKitFace_IsDetectedWithStatedConfidence()
        {
            AssertConventionDetected("Gate_ARKit", ArKitBlendshapes(), RigConvention.ARKit);
        }

        [Test]
        public void ReallusionCC3Face_IsDetectedWithStatedConfidence()
        {
            AssertConventionDetected("Gate_CC3", ReallusionCC3Blendshapes(), RigConvention.ReallusionCC3);
        }

        [Test]
        public void ReallusionCC4ExtendedFace_IsPromotedAboveTheCC3Base()
        {
            // The resolver only promotes to Extended when the CC3 base shapes are present too, so
            // this fixture is the base set plus the Extended-only signatures that trigger it.
            AssertConventionDetected(
                "Gate_CC4Extended", ReallusionCC4ExtendedBlendshapes(), RigConvention.ReallusionCC4Extended);
        }

        [Test]
        public void MetaHumanFace_IsDetectedWithStatedConfidence()
        {
            AssertConventionDetected("Gate_MetaHuman", MetaHumanBlendshapes(), RigConvention.MetaHuman);
        }

        [Test]
        public void AnUnrecognizedFace_IsReportedRatherThanGuessedSilently()
        {
            GameObject character = NewHumanoidCharacterWithFace(
                "Gate_Unknown", new[] { "Wobble01", "Wobble02", "Wobble03", "Squish_A", "Squish_B" });

            EmbodimentRigSetupService.Apply(character);
            EmbodimentSetupReport report = EmbodimentRigSetupService.Inspect(character);

            bool saysSomething =
                HasFinding(report, "rig.unknown-convention") || HasFinding(report, "rig.low-confidence");
            Assert.IsTrue(saysSomething,
                "A face Convai cannot read must be reported, because 'expression does nothing' is " +
                "otherwise unexplainable to the user:\n" + Describe(report));
        }

        [Test]
        public void ACharacterWithNoFaceMeshes_SaysWhatThatCosts()
        {
            GameObject character = NewHumanoidCharacter("Gate_NoFace");

            EmbodimentRigSetupService.Apply(character);
            EmbodimentSetupReport report = EmbodimentRigSetupService.Inspect(character);

            Assert.IsTrue(HasFinding(report, "rig.no-face-meshes"), Describe(report));
        }

        // ── the preset gate ─────────────────────────────────────────────────────────

        [Test]
        public void APresetOfEveryShippedFeature_IsReady()
        {
            // The regression guard for the drift that shipped: a preset naming every real feature
            // must report Ready, not warn about its own correct configuration.
            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            _ownedAssets.Add(preset);

            var slots = new List<EmbodimentProfileSlot>();
            IReadOnlyList<EmbodimentModuleDescriptor> modules = EmbodimentModuleCatalog.Modules;
            Assert.IsNotEmpty(modules, "The catalog must find the shipped features.");

            for (int i = 0; i < modules.Count; i++)
                slots.Add(new EmbodimentProfileSlot(modules[i].ModuleId, null));

            preset.SetProfileSlots(slots);

            EmbodimentSetupReport report = EmbodimentPresetTroubleshooter.Evaluate(preset);

            Assert.IsFalse(report.HasBlocker, Describe(report));
            Assert.IsFalse(HasFinding(report, "preset.slot-unknown-module"),
                "Every feature the catalog lists must be a feature the troubleshooter recognizes:\n"
                + Describe(report));
        }

        [Test]
        public void EveryCatalogedFeature_OffersATypeToFilterItsSettingsPickerBy()
        {
            // What makes a wrong asset type unrepresentable instead of diagnosed.
            var missing = new List<string>();

            foreach (EmbodimentModuleDescriptor module in EmbodimentModuleCatalog.Modules)
                if (module.ProfileType == null) missing.Add(module.DisplayName);

            Assert.IsEmpty(missing,
                "Without a profile type the settings slot falls back to accepting any asset: "
                + string.Join(", ", missing));
        }

        [Test]
        public void EveryCatalogedFeature_HasAPlainEnglishNameAndDescription()
        {
            var offenders = new List<string>();

            foreach (EmbodimentModuleDescriptor module in EmbodimentModuleCatalog.Modules)
            {
                if (string.IsNullOrWhiteSpace(module.DisplayName) || module.DisplayName.Contains("."))
                    offenders.Add($"{module.ModuleId}: display name '{module.DisplayName}'");
                if (string.IsNullOrWhiteSpace(module.Description))
                    offenders.Add($"{module.ModuleId}: no description");
            }

            Assert.IsEmpty(offenders,
                "A user reads these in the dropdown and the character map:\n" + string.Join("\n", offenders));
        }

        // ── fixture construction ────────────────────────────────────────────────────

        private void AssertConventionDetected(string name, string[] blendshapes, RigConvention expected)
        {
            GameObject character = NewHumanoidCharacterWithFace(name, blendshapes);

            EmbodimentRigSetupService.Apply(character);

            var binding = character.GetComponent<StandardRigBinding>();
            Assert.NotNull(binding);
            Assert.AreEqual(expected, binding.DetectedConvention,
                $"Expected {expected} from these blendshape names.");
            Assert.Greater(binding.DetectionConfidence, 0f,
                "A detection with no stated confidence is a guess the user cannot evaluate.");
        }

        private GameObject NewBareCharacter(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.AddComponent<ConvaiCharacter>();
            return go;
        }

        private GameObject NewHumanoidCharacter(string name)
        {
            GameObject go = NewBareCharacter(name);
            go.AddComponent<Animator>();
            return go;
        }

        private GameObject NewHumanoidCharacterWithFace(string name, string[] blendshapes)
        {
            GameObject go = NewHumanoidCharacter(name);

            var face = new GameObject("Head");
            face.transform.SetParent(go.transform);
            SkinnedMeshRenderer renderer = face.AddComponent<SkinnedMeshRenderer>();

            Mesh mesh = CreateMeshWithBlendshapes(blendshapes);
            _ownedAssets.Add(mesh);
            renderer.sharedMesh = mesh;

            return go;
        }

        private static string[] ArKitBlendshapes() => new[]
        {
            "eyeBlinkLeft", "eyeBlinkRight", "jawOpen", "mouthSmileLeft", "mouthSmileRight",
            "browInnerUp", "browDownLeft", "browDownRight", "cheekPuff", "mouthFunnel"
        };

        /// <summary>The CC3 base signatures, and nothing the resolver treats as Extended-only.</summary>
        private static string[] ReallusionCC3Blendshapes() => new[]
        {
            "Eye_Blink_L", "Eye_Blink_R", "V_Open", "V_Explosive",
            "Mouth_Smile_L", "Mouth_Smile_R", "Brow_Raise_Inner_L"
        };

        /// <summary>The CC3 base plus Extended-only shapes, which is what earns the promotion.</summary>
        private static string[] ReallusionCC4ExtendedBlendshapes() => new[]
        {
            "Eye_Blink_L", "Eye_Blink_R", "V_Open", "V_Explosive",
            "Mouth_Smile_L", "Mouth_Smile_R", "Brow_Raise_Inner_L",
            "Mouth_Press_L", "Mouth_Press_R", "Mouth_Shrug_Upper", "Mouth_Shrug_Lower",
            "Eyelash_Upper_Down_L", "Eyelash_Upper_Down_R"
        };

        private static string[] MetaHumanBlendshapes() => new[]
        {
            "CTRL_L_eye", "CTRL_R_eye", "CTRL_C_jaw", "CTRL_C_mouth_lipsTogether",
            "CTRL_L_brow_up", "CTRL_R_brow_up"
        };

        private static Mesh CreateMeshWithBlendshapes(params string[] blendshapeNames)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero } };
            for (int i = 0; i < blendshapeNames.Length; i++)
            {
                mesh.AddBlendShapeFrame(
                    blendshapeNames[i], 100f,
                    new[] { Vector3.zero }, new[] { Vector3.zero }, new[] { Vector3.zero });
            }

            return mesh;
        }

        private static bool HasFinding(EmbodimentSetupReport report, string id)
        {
            for (int i = 0; i < report.Findings.Count; i++)
                if (report.Findings[i].Id == id) return true;
            return false;
        }

        private static EmbodimentFinding FindFinding(EmbodimentSetupReport report, string id)
        {
            for (int i = 0; i < report.Findings.Count; i++)
                if (report.Findings[i].Id == id) return report.Findings[i];
            return default;
        }

        private static string Describe(EmbodimentSetupReport report)
        {
            var lines = new List<string>();
            for (int i = 0; i < report.Findings.Count; i++)
            {
                EmbodimentFinding f = report.Findings[i];
                lines.Add($"  [{f.Severity}] {f.Id}: {f.Title}");
            }

            return string.Join("\n", lines);
        }
    }
}
