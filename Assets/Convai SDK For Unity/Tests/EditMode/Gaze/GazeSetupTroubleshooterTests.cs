using System.Collections.Generic;
using System.Linq;
using Convai.Modules.Gaze.Editor;
using Convai.Modules.Gaze.Components;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazeSetupTroubleshooterTests
    {
        private readonly List<GazeSetupFinding> _results = new();

        private static GazeSetupInput AllGoodHumanoidInput() => new()
        {
            HasAnimator = true,
            IsHumanoid = true,
            HasHeadBone = true,
            HasNeckBone = true,
            HasLeftEyeBone = true,
            HasRightEyeBone = true,
            EyeLookShapeCount = 0,
            HasMainCamera = true,
            HasProfileAsset = true,
            AutoCreatePlayerAnchor = true,
            ProviderCount = 1
        };

        [Test]
        public void AllGoodHumanoid_NoErrorsOrWarnings_ContainsEyeBackendOk()
        {
            GazeSetupInput input = AllGoodHumanoidInput();

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            Assert.IsFalse(_results.Any(f => f.Severity == GazeSetupSeverity.Error));
            Assert.IsFalse(_results.Any(f => f.Severity == GazeSetupSeverity.Warning));
            Assert.IsTrue(_results.Any(f =>
                f.Severity == GazeSetupSeverity.Ok && f.Title == "Eye Backend"));
        }

        [Test]
        public void AnimatorFreeMappedRig_IsAcceptedWithCustomRigGuidance()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasAnimator = false;
            input.IsHumanoid = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            Assert.IsFalse(_results.Any(f => f.Severity == GazeSetupSeverity.Error));
            Assert.IsTrue(_results.Any(f => f.Title == "Custom Rig"));
        }

        [Test]
        public void GenericMappedRig_IsAcceptedWithOrientationGuidance()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.IsHumanoid = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Generic Rig");
            Assert.AreEqual(GazeSetupSeverity.Info, finding.Severity);
            StringAssert.Contains("+Z", finding.Message);
        }

        [Test]
        public void HumanoidWithoutHeadBone_YieldsErrorTitledHeadBone()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasHeadBone = false;
            input.HasNeckBone = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            Assert.IsTrue(_results.Any(f =>
                f.Severity == GazeSetupSeverity.Error && f.Title == "Head Bone"));
        }

        [Test]
        public void NoEyeBonesAndNoEyeLookShapes_YieldsHeadOnlyWarningMentioningEyeLook()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasLeftEyeBone = false;
            input.HasRightEyeBone = false;
            input.EyeLookShapeCount = 0;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Eye Backend");
            Assert.AreEqual(GazeSetupSeverity.Warning, finding.Severity);
            StringAssert.Contains("EyeLook", finding.Message);
        }

        [Test]
        public void SingleEyeBone_YieldsHeadOnlyFallbackWarning()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasRightEyeBone = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Eye Backend");
            Assert.AreEqual(GazeSetupSeverity.Warning, finding.Severity);
            StringAssert.Contains("one eye bone", finding.Message.ToLowerInvariant());
            StringAssert.Contains("head-only", finding.Message.ToLowerInvariant());
        }

        [Test]
        public void SingleEyeWithEyeLookShapes_PrefersCompleteBlendshapeBackend()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasRightEyeBone = false;
            input.EyeLookShapeCount = 8;
            input.HasCompleteEyeLookBackend = true;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Eye Backend");
            Assert.AreEqual(GazeSetupSeverity.Info, finding.Severity);
            StringAssert.Contains("blendshape backend", finding.Message);
        }

        [Test]
        public void NoEyeBonesButTwelveEyeLookShapes_YieldsInfoNotError_MentionsCount()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasLeftEyeBone = false;
            input.HasRightEyeBone = false;
            input.EyeLookShapeCount = 12;
            input.HasCompleteEyeLookBackend = true;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Eye Backend");
            Assert.AreEqual(GazeSetupSeverity.Info, finding.Severity);
            StringAssert.Contains("12", finding.Message);
            Assert.IsFalse(_results.Any(f => f.Severity == GazeSetupSeverity.Error));
        }

        [Test]
        public void IncompleteEyeLookShapes_DoNotClaimUsableBackend()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasLeftEyeBone = false;
            input.HasRightEyeBone = false;
            input.EyeLookShapeCount = 1;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Eye Backend");
            Assert.AreEqual(GazeSetupSeverity.Warning, finding.Severity);
            StringAssert.Contains("head-only", finding.Message);
        }

        [TestCase("EyeLookInLeft")]
        [TestCase("EyeLookOutLeft")]
        [TestCase("EyeLookInRight")]
        [TestCase("EyeLookOutRight")]
        public void MissingAnyHorizontalEyeLookDirection_DoesNotClaimUsableBackend(string missingDirection)
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasLeftEyeBone = false;
            input.HasRightEyeBone = false;
            input.EyeLookShapeCount = 3;

            // The input models the authoritative backend predicate. Removing any one
            // directional channel must leave it false rather than producing one-sided eyes.
            input.HasCompleteEyeLookBackend = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Eye Backend");
            Assert.AreEqual(GazeSetupSeverity.Warning, finding.Severity, missingDirection);
            StringAssert.Contains("head-only", finding.Message);
        }

        [Test]
        public void InferredGenericCandidate_DoesNotClaimAuthoredSemanticMapping()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.IsHumanoid = false;
            input.HasInferredBoneCandidates = true;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Generic Rig Candidate");
            StringAssert.Contains("candidate", finding.Message.ToLowerInvariant());
            StringAssert.Contains("Character Rig", finding.Message);
        }

        [Test]
        public void CcStyleRawEyeLookChannels_AreCountedWhenTheyFormACompleteBackend()
        {
            GameObject root = new("GenericRig");
            Mesh mesh = null;
            try
            {
                root.AddComponent<ConvaiCharacter>();
                ConvaiGazeController controller = root.AddComponent<ConvaiGazeController>();
                SkinnedMeshRenderer renderer = root.AddComponent<SkinnedMeshRenderer>();
                mesh = CreateMeshWithBlendshapes(
                    "Eye_L_Look_R", "Eye_L_Look_L", "Eye_R_Look_L", "Eye_R_Look_R");
                renderer.sharedMesh = mesh;

                GazeSetupInput input = GazeSetupTroubleshooter.GatherFrom(controller, null, true);

                Assert.IsTrue(input.HasCompleteEyeLookBackend);
                Assert.AreEqual(4, input.EyeLookShapeCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        private static Mesh CreateMeshWithBlendshapes(params string[] shapeNames)
        {
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                triangles = new[] { 0, 1, 2 }
            };
            Vector3[] deltas = { Vector3.zero, Vector3.zero, Vector3.zero };
            for (int i = 0; i < shapeNames.Length; i++)
                mesh.AddBlendShapeFrame(shapeNames[i], 100f, deltas, deltas, deltas);
            return mesh;
        }

        [Test]
        public void NoMainCamera_YieldsWarningMentioningPlayerAnchorOverride()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.HasMainCamera = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Player Camera");
            Assert.AreEqual(GazeSetupSeverity.Warning, finding.Severity);
            StringAssert.Contains("Player Anchor Override", finding.Message);
        }

        [Test]
        public void NoProvidersAndAutoCreateOff_YieldsWarning_ButNotWhenAutoCreateOn()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.ProviderCount = 0;
            input.AutoCreatePlayerAnchor = false;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            Assert.IsTrue(_results.Any(f =>
                f.Severity == GazeSetupSeverity.Warning && f.Title == "Target Providers"));

            input.AutoCreatePlayerAnchor = true;
            GazeSetupTroubleshooter.Evaluate(in input, _results);

            Assert.IsFalse(_results.Any(f => f.Title == "Target Providers"));
        }

        [Test]
        public void MultipleRigBindings_YieldActionableOwnershipError()
        {
            GazeSetupInput input = AllGoodHumanoidInput();
            input.RigBindingCount = 2;

            GazeSetupTroubleshooter.Evaluate(in input, _results);

            GazeSetupFinding finding = _results.Single(f => f.Title == "Rig Binding Ownership");
            Assert.AreEqual(GazeSetupSeverity.Error, finding.Severity);
            StringAssert.Contains("exactly one", finding.Message);
        }
    }
}
