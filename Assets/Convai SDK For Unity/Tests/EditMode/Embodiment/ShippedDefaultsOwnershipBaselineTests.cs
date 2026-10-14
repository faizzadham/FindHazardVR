using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.BodyLanguage.Editor;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Editor.Ownership;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     The regression red-line for shipped-asset ownership: a character must never be handed a
    ///     package asset it cannot edit.
    /// </summary>
    /// <remarks>
    ///     Two kinds of assertion live here and they are labelled apart on purpose:
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Invariant_</b> — must hold before and after the plan. These are what made the
    ///             fix possible at all: every module already runs on code-defined defaults, so the
    ///             package-resident tuning assets the setup services used to hand out were never
    ///             load-bearing. If one of these ever goes red, the design's premise is gone and the
    ///             design has to be revisited, not the test.
    ///         </item>
    ///         <item>
    ///             <b>Outcome_</b> — what setup does now. These replaced the <c>Baseline_</c> tests
    ///             that pinned the old behaviour: those were written to be inverted, and were
    ///             inverted in the same commit that changed what they described.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Setup now writes real assets into the project, so this fixture deletes what it creates.
    ///         A test suite that leaves a trail of <c>BA_Setup_BodyAnimation.asset</c> behind is a
    ///         test suite people stop running.
    ///     </para>
    /// </remarks>
    public class ShippedDefaultsOwnershipBaselineTests
    {
        private readonly List<Object> _created = new();
        private readonly List<string> _createdAssetPaths = new();

        [TearDown]
        public void TearDown()
        {
            // Characters before assets: a live component still pointing at an asset keeps it loaded,
            // and the delete then quietly does nothing.
            for (int i = 0; i < _created.Count; i++)
                if (_created[i] != null)
                    Object.DestroyImmediate(_created[i]);
            _created.Clear();

            for (int i = 0; i < _createdAssetPaths.Count; i++)
                AssetDatabase.DeleteAsset(_createdAssetPaths[i]);
            _createdAssetPaths.Clear();
        }

        /// <summary>
        ///     A bare GameObject carrying <typeparamref name="T" />, tracked for cleanup.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         These tests deliberately add a controller to a GameObject that is <em>not</em> a
        ///         Convai character, because that is the shortest way to ask a setup service what it
        ///         assigns. Each controller answers with a one-shot error and goes inert — the
        ///         documented, correct degradation. Failing the test on it would be failing it for
        ///         behaving properly, so the errors are ignored from here: the helper that provokes
        ///         them is the one that declares them.
        ///     </para>
        ///     <para>
        ///         Set here rather than in a <c>[SetUp]</c>, which is where it used to live and where
        ///         it silently did nothing. The test framework runs every setup and teardown method
        ///         inside a <c>LogScope</c> of its own, and that scope is disposed the moment the
        ///         method returns — while a fresh scope starts with the flag back at false. So a
        ///         <c>[SetUp]</c> can only ever set it on a scope the test body never sees. The
        ///         matching reset in <c>[TearDown]</c> is gone for the same reason: each test body
        ///         gets its own scope, so nothing leaks into the next test to reset.
        ///     </para>
        /// </remarks>
        private T NewCharacter<T>(string name) where T : Component
        {
            LogAssert.ignoreFailingMessages = true;

            var go = new GameObject(name);
            _created.Add(go);
            return go.AddComponent<T>();
        }

        /// <summary>Remembers an asset setup created, so TearDown can remove it.</summary>
        private T TrackAsset<T>(T asset) where T : Object
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path) && !_createdAssetPaths.Contains(path))
                _createdAssetPaths.Add(path);
            return asset;
        }

        private static bool IsProjectAsset(Object asset) => ConvaiAssetOwnership.IsProjectAsset(asset);

        private static bool IsPackageResident(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith("Packages/", System.StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ invariants

        [Test]
        public void Invariant_EveryTunableModuleHasACodeDefinedDefault()
        {
            // The whole plan rests on this: a character with no asset assigned is a working
            // character. If any of these returned null, "assign nothing at setup" would be a
            // regression rather than a fix.
            Assert.That(ConvaiBodyAnimationConfig.CreateDefault(), Is.Not.Null, "Body Animation config");
            Assert.That(ConvaiBodyAnimationProfile.CreateDefault(), Is.Not.Null, "Body Animation profile");
            Assert.That(ConvaiGazeProfile.CreateDefault(), Is.Not.Null, "Gaze profile");
            Assert.That(ConvaiBodyLanguageProfile.CreateDefault(), Is.Not.Null, "Body Language profile");
            Assert.That(ConvaiEmotionProfile.CreateDefault(), Is.Not.Null, "Emotion profile");
            Assert.That(ConvaiConversationFlowProfile.CreateDefault(), Is.Not.Null, "Conversation Flow profile");
        }

        [Test]
        public void Invariant_AFreshControllerResolvesNoAssetAtAll()
        {
            // Nothing is assigned by merely adding the component — every assignment in this
            // codebase is a deliberate setup step, which is what makes those steps fixable.
            Assert.That(
                BodyAnimationSetupService.ResolveAssignedConfig(NewCharacter<ConvaiBodyAnimationController>("BA")),
                Is.Null);
            Assert.That(
                GazeSetupService.ResolveAssignedProfile(NewCharacter<ConvaiGazeController>("Gaze")),
                Is.Null);
            Assert.That(
                BodyLanguageSetupService.ResolveAssignedProfile(NewCharacter<ConvaiBodyLanguageController>("BL")),
                Is.Null);
        }

        [Test]
        public void Invariant_APackageAssetIsNeverAnEditableProjectAsset()
        {
            ConvaiBodyAnimationProfile shipped = BodyAnimationSetupService.TryLoadDefaultProfile();
            if (shipped == null || !IsPackageResident(shipped))
                Assert.Ignore("No package-resident default profile in this project; nothing to assert.");

            Assert.That(ConvaiAssetOwnership.IsProjectAsset(shipped), Is.False);
        }

        // ------------------------------------------------------------------ outcomes

        [Test]
        public void Outcome_BodyAnimationSetupGivesTheCharacterItsOwnProfile()
        {
            var controller = NewCharacter<ConvaiBodyAnimationController>("BA_Setup");
            if (!BodyAnimationSetupService.ApplyFix(controller, BodyAnimationFixId.AssignDefaultContent))
                Assert.Ignore("No Body Animation content is available in this project.");

            ConvaiBodyAnimationSet set = BodyAnimationSetupService.ResolveAssignedSet(controller);
            Assert.That(set, Is.Not.Null, "setup assigned no animation content");

            var serialized = new SerializedObject(controller);
            var profile = serialized.FindProperty("profile").objectReferenceValue as ConvaiBodyAnimationProfile;
            Assert.That(profile, Is.Not.Null, "setup assigned no profile");
            TrackAsset(profile);

            Assert.That(
                IsProjectAsset(profile), Is.True,
                "the character's profile must live in the project, not in the package");

            // The animation set is content — clips are consumed, not tuned — so it rightly stays in
            // the package and is referenced. Only the tuning half had to move.
            Assert.That(
                BodyAnimationSetupService.ResolveAssignedConfig(controller), Is.Null,
                "no config asset should exist until the user actually tunes something");
        }

        [Test]
        public void Outcome_GazeSetupGivesTheCharacterItsOwnProfile()
        {
            var controller = NewCharacter<ConvaiGazeController>("Gaze_Setup");
            if (!GazeSetupService.ApplyFix(controller, GazeFixId.AssignDefaultProfile))
                Assert.Ignore("Gaze setup did not run in this project.");

            ConvaiGazeProfile profile = TrackAsset(GazeSetupService.ResolveAssignedProfile(controller));
            Assert.That(profile, Is.Not.Null);
            Assert.That(IsProjectAsset(profile), Is.True);
        }

        [Test]
        public void Outcome_BodyLanguageSetupGivesTheCharacterItsOwnProfile()
        {
            var controller = NewCharacter<ConvaiBodyLanguageController>("BL_Setup");
            if (!BodyLanguageSetupService.ApplyFix(controller, BodyLanguageFixId.AssignDefaultProfile))
                Assert.Ignore("Body Language setup did not run in this project.");

            ConvaiBodyLanguageProfile profile =
                TrackAsset(BodyLanguageSetupService.ResolveAssignedProfile(controller));
            Assert.That(profile, Is.Not.Null);
            Assert.That(IsProjectAsset(profile), Is.True);
        }

        /// <summary>
        ///     Sharing and SDK-ownership stay separate verdicts. Collapsing them is what made the
        ///     original notice tell a user their own shared asset was untouchable, and it is the one
        ///     distinction the whole vocabulary rests on.
        /// </summary>
        [Test]
        public void Invariant_SharingAndSdkOwnershipAreDifferentVerdicts()
        {
            ConvaiBodyAnimationProfile shipped = BodyAnimationSetupService.TryLoadDefaultProfile();
            if (shipped == null || shipped.Config == null || !IsPackageResident(shipped.Config))
                Assert.Ignore("No package-resident shipped config in this project.");

            ConvaiAssetOwnership sdkOwned = BodyAnimationPersonality.OwnershipOf(shipped.Config);

            Assert.That(sdkOwned.Kind, Is.EqualTo(ConvaiAssetOwnershipKind.SdkOwned));
            Assert.That(sdkOwned.RequiresProjectCopy, Is.True, "an SDK asset always needs a copy");
            Assert.That(sdkOwned.IsWritable, Is.False, "an SDK asset is never writable");
            Assert.That(
                sdkOwned.EditingAffectsOthers, Is.True,
                "an unattended caller must still take consent before touching an SDK asset");
        }
    }
}
