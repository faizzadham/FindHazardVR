using System.Collections.Generic;
using System.Reflection;
using Convai.Editor.Ownership;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Data;
using Convai.Editor.Embodiment.Inspectors;
using Convai.Modules.Gaze.Editor;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     What Convai Gaze promises the outside world: the enum values a saved scene depends on,
    ///     the Add Component paths a user navigates, the inspector that draws the profile, and the
    ///     guarantee that setting a character up never hands it an asset it does not own.
    /// </summary>
    /// <remarks>
    ///     These assertions used to live in the shared embodiment test files, which could only hold
    ///     them while every module happened to be present at once. They are statements about Gaze,
    ///     so they belong with Gaze, and they fail here the moment this module breaks its own
    ///     promises rather than when someone else's build does.
    /// </remarks>
    public sealed class GazeReleaseSurfaceTests
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

        // ------------------------------------------------------------------ public surface

        /// <summary>
        ///     Every one of these numbers is written into a user's scene file. Reordering an enum
        ///     silently repoints their setting at a different behaviour on the next open, which is
        ///     why the values are asserted rather than merely the names.
        /// </summary>
        [Test]
        public void GazeFocusContract_PublicSurface_IsDeliberateAndStable()
        {
            Assert.AreEqual(0, (int)GazeEyeContactMode.Natural);
            Assert.AreEqual(1, (int)GazeEyeContactMode.ConversationLock);
            Assert.AreEqual(2, (int)GazeEyeContactMode.AlwaysLock);
            Assert.AreEqual(3, (int)GazeEyeContactMode.SpeakingFocus);
            Assert.AreEqual(0, (int)GazeFocusFidelity.Social);
            Assert.AreEqual(1, (int)GazeFocusFidelity.Exact);
            Assert.AreEqual(0, (int)GazeAnchorAimMode.Auto);
            Assert.AreEqual(1, (int)GazeAnchorAimMode.ExactTransform);
            Assert.AreEqual(2, (int)GazeAnchorAimMode.LocalOffset);

            // Stepping Turn is 0 so a scene authored before this option existed keeps the animated
            // turn it already had, rather than silently switching to direct rotation on upgrade.
            Assert.AreEqual(0, (int)GazeBodyTurnStyle.SteppingTurn);
            Assert.AreEqual(1, (int)GazeBodyTurnStyle.SmoothRotation);

            Assert.NotNull(typeof(ConvaiGazeController).GetProperty(nameof(ConvaiGazeController.FocusFidelity)));
            Assert.NotNull(typeof(ConvaiGazeController).GetProperty(nameof(ConvaiGazeController.PlayerAnchorAimMode)));
            Assert.NotNull(typeof(ConvaiGazeController).GetProperty(nameof(ConvaiGazeController.PlayerAnchorAimOffset)));
            Assert.NotNull(typeof(ConvaiGazeController).GetProperty(
                nameof(ConvaiGazeController.AllowScriptedOverridesDuringExactFocus)));

            // How a scene decides where "the player" is, and where they are looking, is a rule game
            // code has to be able to share. Every one of these was internal once, and every caller
            // outside the package had to guess at the rule and then quietly disagree with the eyes.
            Assert.NotNull(typeof(ConvaiGazeController).GetMethod(
                nameof(ConvaiGazeController.TryGetPlayerAnchor)));
            Assert.NotNull(typeof(PlayerAttentionSensor).GetMethod(
                nameof(PlayerAttentionSensor.TryGetPlayerGazeRay)));
            Assert.NotNull(typeof(PlayerAttentionSensor).GetMethod(
                nameof(PlayerAttentionSensor.IsPlayerLookingAt), new[] { typeof(Transform) }));
            Assert.NotNull(typeof(PlayerAttentionSensor).GetMethod(
                nameof(PlayerAttentionSensor.IsPlayerLookingAt), new[] { typeof(Vector3), typeof(float) }));
        }

        /// <summary>
        ///     The controller sits with the other character features; everything optional sits under
        ///     Convai/Gaze, with the rarely-added parts one level deeper. Ten flat siblings under
        ///     Convai/Embodiment meant a user browsing Add Component could not tell which of the ten
        ///     they needed.
        /// </summary>
        [Test]
        public void GazeAddComponentMenus_LeadWithTheControllerAndGroupTheRest()
        {
            AssertAddComponentMenu<ConvaiGazeController>("Convai/Embodiment/Gaze");
            AssertAddComponentMenu<ConvaiGazeTarget>("Convai/Gaze/Target");
            AssertAddComponentMenu<PlayerAnchorTargetProvider>("Convai/Gaze/Advanced/Player Anchor");
            AssertAddComponentMenu<WorldObjectGazeTargetProvider>("Convai/Gaze/Advanced/World Object Target");
            AssertAddComponentMenu<CharacterGazeTargetProvider>("Convai/Gaze/Advanced/Character Target");
            AssertAddComponentMenu<PlayerAttentionSensor>("Convai/Gaze/Advanced/Player Attention Sensor");
            AssertAddComponentMenu<GazeReferentialGlances>("Convai/Gaze/Advanced/Referential Glances");
            AssertAddComponentMenu<GazeDynamicContextBridge>("Convai/Gaze/Advanced/Dynamic Context Bridge");
            AssertAddComponentMenu<GazeJointAttention>("Convai/Gaze/Advanced/Joint Attention");
            AssertAddComponentMenu<ConvaiEyePupilDriver>("Convai/Gaze/Advanced/Eye Pupil Driver");
        }

        // ------------------------------------------------------------------ inspector

        /// <summary>
        ///     The Gaze profile is drawn by a Convai inspector, not Unity's default one. Without
        ///     this the profile silently falls back to a flat list of serialized fields, which is
        ///     how a profile ends up looking unfinished without anybody changing it.
        /// </summary>
        [Test]
        public void GazeProfile_IsDrawnByAConvaiInspector()
        {
            var profile = ScriptableObject.CreateInstance<ConvaiGazeProfile>();
            UnityEditor.Editor editor = null;
            try
            {
                editor = UnityEditor.Editor.CreateEditor(profile);
                Assert.IsNotNull(editor, "No editor was created for the Gaze profile.");
                Assert.AreEqual(
                    typeof(ConvaiGazeProfileInspector), editor.GetType(),
                    $"The Gaze profile was drawn by {editor.GetType().FullName}.");
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     Section ids are persisted per user as foldout state, so renaming one silently
        ///     collapses a section the user had left open.
        /// </summary>
        [Test]
        public void GazeProfileInspector_ExposesStableSectionIds()
        {
            Assert.AreEqual("Personality", ConvaiGazeProfileInspector.SectionPersonality);
            Assert.AreEqual("WhoItLooksAt", ConvaiGazeProfileInspector.SectionWhoItLooksAt);
            Assert.AreEqual("HeadAndBody", ConvaiGazeProfileInspector.SectionHeadAndBody);
            Assert.AreEqual("EyesAndBlinking", ConvaiGazeProfileInspector.SectionEyesAndBlinking);
            Assert.AreEqual("Reactions", ConvaiGazeProfileInspector.SectionReactions);
            Assert.AreEqual("WhileWalking", ConvaiGazeProfileInspector.SectionWhileWalking);
            Assert.AreEqual("Advanced", ConvaiGazeProfileInspector.SectionAdvanced);
        }

        // ------------------------------------------------------------------ setup ownership

        /// <summary>
        ///     Setting a character up must give it a profile it owns. Handing out the one inside the
        ///     package looks identical until the user edits it, at which point they are either
        ///     blocked or silently tuning every other character in the project.
        /// </summary>
        [Test]
        public void GazeSetup_GivesTheCharacterItsOwnProfile()
        {
            var controller = NewCharacter<ConvaiGazeController>("Gaze_Setup");
            if (!GazeSetupService.ApplyFix(controller, GazeFixId.AssignDefaultProfile))
                Assert.Ignore("Gaze setup did not run in this project.");

            ConvaiGazeProfile profile = TrackAsset(GazeSetupService.ResolveAssignedProfile(controller));
            Assert.That(profile, Is.Not.Null);
            Assert.That(ConvaiAssetOwnership.IsProjectAsset(profile), Is.True);
        }

        [Test]
        public void GazeSetup_CreatesAProfileThatSurvivesBeingSaved()
        {
            var controller = NewCharacter<ConvaiGazeController>("Gaze_Flags");
            if (!GazeSetupService.ApplyFix(controller, GazeFixId.AssignDefaultProfile))
                Assert.Ignore("Gaze setup did not run in this project.");

            ConvaiGazeProfile profile = TrackAsset(GazeSetupService.ResolveAssignedProfile(controller));
            Assert.That(profile, Is.Not.Null);
            Assert.That(
                profile.hideFlags.HasFlag(HideFlags.DontSaveInEditor), Is.False,
                "an asset flagged DontSave is written and then silently discarded");
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertAddComponentMenu<T>(string expected)
        {
            AddComponentMenu attribute = typeof(T).GetCustomAttribute<AddComponentMenu>();
            Assert.NotNull(attribute, $"{typeof(T).Name} must declare AddComponentMenu.");
            Assert.AreEqual(expected, attribute.componentMenu);
        }

        private T NewCharacter<T>(string name) where T : Component
        {
            LogAssert.ignoreFailingMessages = true;

            var go = new GameObject(name);
            _created.Add(go);
            return go.AddComponent<T>();
        }

        private T TrackAsset<T>(T asset) where T : Object
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path) && !_createdAssetPaths.Contains(path))
                _createdAssetPaths.Add(path);
            return asset;
        }
    }
}
