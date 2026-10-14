using System.Collections.Generic;
using System.Linq;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="BodyAnimationTroubleshooter" />: the pure <c>Evaluate</c> findings
    ///     logic (constructed inputs, mirroring <c>GazeSetupTroubleshooterTests</c>) and the
    ///     scene-dependent <c>GatherFrom</c> profile-fallback resolution.
    /// </summary>
    public sealed class BodyAnimationTroubleshooterTests
    {
        private readonly List<BodyAnimationTroubleshooterFinding> _results = new();
        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        private static BodyAnimationTroubleshooterInput AllGoodInput() => new()
        {
            HasAnimator = true,
            IsHumanoid = true,
            HasAnimatorController = false,
            ApplyRootMotion = false,
            HasProfileAsset = true,
            HasSetAssigned = true,
            HasConfigAssigned = true,
            HasAnyIdle = true,
            HasAnyTalk = true,
            HasAnyListen = true,
            HasAnyThink = true,
            HasBeatGesture = true,
            RigMotionScale = 1f,
            SetIssues = new List<string>()
        };

        [Test]
        public void NoAnimator_YieldsErrorTitledAnimator()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasAnimator = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding = _results.Single(f => f.Title == "Animator");
            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Error, finding.Severity);
        }

        [Test]
        public void NotHumanoid_YieldsErrorTitledHumanoidAvatar()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.IsHumanoid = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding = _results.Single(f => f.Title == "Humanoid Avatar");
            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Error, finding.Severity);
        }

        [Test]
        public void NoSetAssigned_YieldsErrorTitledAnimationSet()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasSetAssigned = false;
            input.SetIssues = null;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding = _results.Single(f => f.Title == "Animation Set");
            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Error, finding.Severity);
        }

        [Test]
        public void NoConfigAssigned_YieldsInfoTitledConfig()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasConfigAssigned = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            Assert.IsTrue(_results.Any(f =>
                f.Title == "Config" && f.Severity == BodyAnimationTroubleshooterSeverity.Info));
        }

        [Test]
        public void SetIssues_SurfacedAsWarnings()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.SetIssues = new List<string> { "Idle[0] has no clip.", "Action[1] has no name." };

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            List<BodyAnimationTroubleshooterFinding> issueFindings = _results.Where(f => f.Title == "Set Issue").ToList();
            Assert.AreEqual(2, issueFindings.Count);
            Assert.IsTrue(issueFindings.All(f => f.Severity == BodyAnimationTroubleshooterSeverity.Warning));
            Assert.IsTrue(issueFindings.Any(f => f.Message.Contains("Idle[0]")));
        }

        [Test]
        public void EmptyTalks_YieldsWarningTitledTalk()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasAnyTalk = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding = _results.Single(f => f.Title == "Talk");
            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Warning, finding.Severity);
        }

        [Test]
        public void EmptyListenAndThink_YieldInfoHints()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasAnyListen = false;
            input.HasAnyThink = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            Assert.IsTrue(_results.Any(f => f.Title == "Listen" && f.Severity == BodyAnimationTroubleshooterSeverity.Info));
            Assert.IsTrue(_results.Any(f => f.Title == "Think" && f.Severity == BodyAnimationTroubleshooterSeverity.Info));
        }

        [Test]
        public void NoBeatGesture_YieldsInfoHint()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasBeatGesture = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            Assert.IsTrue(_results.Any(f =>
                f.Title == "Beat Gestures" && f.Severity == BodyAnimationTroubleshooterSeverity.Info));
        }

        [Test]
        public void GatherFrom_ProfileFallback_ResolvesSetAndConfigFromProfile()
        {
            var root = new GameObject("BodyAnimationTroubleshooterTestCharacter");
            var set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            ConvaiBodyAnimationConfig config = ConvaiBodyAnimationConfig.CreateDefault();
            var profile = ScriptableObject.CreateInstance<ConvaiBodyAnimationProfile>();
            _cleanup.Add(root);
            _cleanup.Add(set);
            _cleanup.Add(config);
            _cleanup.Add(profile);

            profile.Initialize(set, config);

            // EmbodimentContext first: without one OnEnable logs a setup error the framework counts as a failure.
            root.AddComponent<EmbodimentContext>();
            ConvaiBodyAnimationController controller = root.AddComponent<ConvaiBodyAnimationController>();

            var serializedController = new SerializedObject(controller);
            SerializedProperty setProp = serializedController.FindProperty("_animationSet");
            SerializedProperty configProp = serializedController.FindProperty("_config");
            SerializedProperty profileProp = serializedController.FindProperty("profile");
            SerializedProperty animatorOverrideProp = serializedController.FindProperty("_animatorOverride");

            Assert.NotNull(setProp, "ConvaiBodyAnimationController must have a private _animationSet field.");
            Assert.NotNull(configProp, "ConvaiBodyAnimationController must have a private _config field.");
            Assert.NotNull(profileProp, "ConvaiBodyAnimationController must have a serialized profile field.");
            Assert.NotNull(animatorOverrideProp, "ConvaiBodyAnimationController must have a private _animatorOverride field.");

            // Direct set/config stay unassigned; only the profile carries content — GatherFrom
            // must fall back to the profile's set/config when the direct fields are empty.
            profileProp.objectReferenceValue = profile;
            serializedController.ApplyModifiedPropertiesWithoutUndo();

            var issuesScratch = new List<string>();
            BodyAnimationTroubleshooterInput input = BodyAnimationTroubleshooter.GatherFrom(
                controller, setProp, configProp, profileProp, animatorOverrideProp,
                issuesScratch, out ConvaiBodyAnimationSet resolvedSet, out _);

            Assert.IsTrue(input.HasProfileAsset);
            Assert.IsTrue(input.HasSetAssigned,
                "GatherFrom must fall back to the profile's set when no direct set is assigned.");
            Assert.IsTrue(input.HasConfigAssigned,
                "GatherFrom must fall back to the profile's config when no direct config is assigned.");
            Assert.AreSame(set, resolvedSet);
        }

        // ------------------------------------------------------------------ unified finding model

        /// <summary>
        ///     Every finding must carry a stable id. Surfaces attach behaviour (Fix buttons, deep
        ///     links) to ids, and tests assert on ids rather than display text — an untagged finding
        ///     silently opts out of both.
        /// </summary>
        [Test]
        public void EveryFinding_CarriesAStableId()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasAnimatorController = true;
            input.ApplyRootMotion = true;
            input.HasAnyTalk = false;
            input.HasAnyListen = false;
            input.HasAnyThink = false;
            input.HasBeatGesture = false;
            input.HasCustomLocomotionProvider = true;
            input.HasValidLocomotionSource = true;
            input.SetIssues = new List<string> { "Idle[0] has no clip." };

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            Assert.IsNotEmpty(_results);
            foreach (BodyAnimationTroubleshooterFinding finding in _results)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Id),
                    $"Finding '{finding.Title}' has no stable id.");
            }
        }

        /// <summary>A missing set is mechanically fixable — the SDK ships default content.</summary>
        [Test]
        public void NoSetAssigned_OffersTheAssignDefaultContentFix()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.HasSetAssigned = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding =
                _results.Single(f => f.Id == BodyAnimationFindingIds.NoSet);
            Assert.AreEqual(BodyAnimationFixId.AssignDefaultContent, finding.Fix);
        }

        /// <summary>
        ///     A set that authors overlay content but no mask would drive the full skeleton. This is
        ///     the finding that used to hide inside the generic issue-string list, unfixable without
        ///     parsing its text.
        /// </summary>
        [Test]
        public void MissingUpperBodyMask_WhenNeeded_OffersTheGenerateMaskFix()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.NeedsUpperBodyMask = true;
            input.HasUpperBodyMask = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding =
                _results.Single(f => f.Id == BodyAnimationFindingIds.NoUpperBodyMask);
            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Error, finding.Severity);
            Assert.AreEqual(BodyAnimationFixId.GenerateUpperBodyMask, finding.Fix);
        }

        /// <summary>A locomotion-only set needs no overlay mask, so its absence is not a fault.</summary>
        [Test]
        public void MissingUpperBodyMask_WhenNotNeeded_IsNotReported()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.NeedsUpperBodyMask = false;
            input.HasUpperBodyMask = false;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            Assert.IsFalse(_results.Any(f => f.Id == BodyAnimationFindingIds.NoUpperBodyMask));
        }

        /// <summary>Unmeasured locomotion clips are the usual cause of sliding feet, and fixable.</summary>
        [Test]
        public void UnmeasuredLocomotionClips_OfferTheAnalyzeFix()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.LocomotionClipsMissingMetadata = 4;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            BodyAnimationTroubleshooterFinding finding =
                _results.Single(f => f.Id == BodyAnimationFindingIds.MissingClipMetadata);
            Assert.AreEqual(BodyAnimationFixId.AnalyzeClipMetadata, finding.Fix);
            Assert.IsTrue(finding.Message.Contains("4"),
                "The finding should state how many clips are unmeasured.");
        }

        [Test]
        public void MeasuredLocomotionClips_ReportNothing()
        {
            BodyAnimationTroubleshooterInput input = AllGoodInput();
            input.LocomotionClipsMissingMetadata = 0;

            BodyAnimationTroubleshooter.Evaluate(in input, _results);

            Assert.IsFalse(_results.Any(f => f.Id == BodyAnimationFindingIds.MissingClipMetadata));
        }

        /// <summary>The badge every surface shows must reflect the most serious finding.</summary>
        [Test]
        public void WorstSeverity_ReportsTheMostSeriousFinding()
        {
            _results.Clear();
            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Ok,
                BodyAnimationTroubleshooter.WorstSeverity(_results));

            _results.Add(new BodyAnimationTroubleshooterFinding
                { Severity = BodyAnimationTroubleshooterSeverity.Info });
            _results.Add(new BodyAnimationTroubleshooterFinding
                { Severity = BodyAnimationTroubleshooterSeverity.Error });
            _results.Add(new BodyAnimationTroubleshooterFinding
                { Severity = BodyAnimationTroubleshooterSeverity.Warning });

            Assert.AreEqual(BodyAnimationTroubleshooterSeverity.Error,
                BodyAnimationTroubleshooter.WorstSeverity(_results));
        }

        /// <summary>
        ///     Only fixes this class can actually perform may advertise a button — otherwise a
        ///     surface would render a button that does nothing.
        /// </summary>
        [Test]
        public void SetScopedFixes_AreTheOnlyOnesThatDescribeAButton()
        {
            Assert.IsNotNull(BodyAnimationFixes.DescribeSetFix(BodyAnimationFixId.GenerateUpperBodyMask));
            Assert.IsNotNull(BodyAnimationFixes.DescribeSetFix(BodyAnimationFixId.AnalyzeClipMetadata));

            Assert.IsNull(BodyAnimationFixes.DescribeSetFix(BodyAnimationFixId.None));
            Assert.IsNull(BodyAnimationFixes.DescribeSetFix(BodyAnimationFixId.AssignDefaultContent),
                "Character-scoped fixes belong to the setup service, not the set-asset fixer.");
            Assert.IsNull(BodyAnimationFixes.DescribeSetFix(BodyAnimationFixId.AddMovement));
        }

        /// <summary>
        ///     The set inspector's evaluation must produce the same findings as the character path,
        ///     so an authoring problem reads identically from the asset and from the character.
        /// </summary>
        [Test]
        public void EvaluateSetAsset_ProducesTheSameContentFindings()
        {
            var set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(set);

            var issues = new List<string>();
            BodyAnimationTroubleshooter.EvaluateSetAsset(set, issues, _results);

            // An empty set has no talk/listen/think content, so those findings must be present —
            // exactly what the character-scoped path reports for the same set.
            Assert.IsTrue(_results.Any(f => f.Id == BodyAnimationFindingIds.NoTalk));
            Assert.IsTrue(_results.Any(f => f.Id == BodyAnimationFindingIds.NoListen));
            Assert.IsTrue(_results.Any(f => f.Id == BodyAnimationFindingIds.NoThink));
        }
    }
}
