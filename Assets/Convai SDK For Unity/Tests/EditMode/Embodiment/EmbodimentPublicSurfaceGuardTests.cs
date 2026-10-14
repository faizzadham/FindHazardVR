using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Convai.Modules.Embodiment.Components;
using Convai.Modules.ConversationFlow.Components;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Embodiment
{
    public sealed class EmbodimentPublicSurfaceGuardTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath,
            "..",
            "Packages",
            "com.convai.convai-sdk-for-unity"));

        [Test]
        public void GazeFocusContract_PublicSurface_IsDeliberateAndStable()
        {
            Assert.AreEqual(0, (int)GazeEyeContactMode.Natural);
            Assert.AreEqual(1, (int)GazeEyeContactMode.ConversationLock);
            Assert.AreEqual(2, (int)GazeEyeContactMode.AlwaysLock);
            Assert.AreEqual(3, (int)GazeEyeContactMode.SpeakingFocus);
            Assert.AreEqual(0, (int)GazeFocusFidelity.Social);
            Assert.AreEqual(1, (int)GazeFocusFidelity.Exact);
            // Ordinals are pinned because they are what a scene file stores. Note that Off being 0
            // does NOT make it the upgrade default: the field is on a MonoBehaviour, whose C#
            // initializer runs before serialized values are applied, so a scene authored before
            // conversation attention existed comes up on the shipped default (Anyone) — which is
            // the ratified decision, not an accident of ordering.
            Assert.AreEqual(0, (int)GazeSpeakerAttention.Off);
            Assert.AreEqual(1, (int)GazeSpeakerAttention.Player);
            Assert.AreEqual(2, (int)GazeSpeakerAttention.Characters);
            Assert.AreEqual(3, (int)GazeSpeakerAttention.Anyone);

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

        [Test]
        [Category("Architecture")]
        public void EmbodimentFeatureSurface_HasNoPublicReleaseCleanupTerms()
        {
            string[] roots =
            {
                "SDK/Domain/Embodiment",
                "SDK/Modules/Embodiment",
                "SDK/Modules/Emotion",
                "SDK/Modules/Gaze",
                "SDK/Modules/BodyAnimation",
                "SDK/Modules/BodyLanguage",
                "SDK/Runtime/Embodiment",
                "SDK/Runtime/Animation",
                "SDK/Editor/Embodiment"
            };

            string[] forbidden =
            {
                "[Obsolete",
                "FormerlySerializedAs",
                "deprecated",
                "obsolete",
                "legacy",
                "Animation Rigging Gaze Bridge",
                "FacialBlendshapeLayerKind"
            };

            var violations = new List<string>();
            foreach (string root in roots)
            {
                string absoluteRoot = Path.Combine(PackageRoot, root.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(Directory.Exists(absoluteRoot), $"Embodiment feature root not found: {root}");

                foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                {
                    string source = File.ReadAllText(file);
                    foreach (string token in forbidden)
                    {
                        if (source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                            violations.Add($"{ToPackagePath(file)} contains '{token}'");
                    }
                }
            }

            Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations.Take(30)));
        }

        [Test]
        public void EmotionController_RequiresCharacterScopedEvents()
        {
            GameObject root = new("EmotionFilterTestCharacter");
            ConvaiCharacter character = root.AddComponent<ConvaiCharacter>();
            ConvaiEmotionController controller = root.AddComponent<ConvaiEmotionController>();

            try
            {
                SetPrivateField(character, "_characterId", "char-a");
                SetPrivateField(controller, "_character", character);

                Assert.IsTrue((bool)InvokePrivateWithResult(controller, "MatchesCharacter", "char-a"));
                Assert.IsFalse((bool)InvokePrivateWithResult(controller, "MatchesCharacter", "char-b"));
                Assert.IsFalse((bool)InvokePrivateWithResult(controller, "MatchesCharacter", ""));
                Assert.IsFalse((bool)InvokePrivateWithResult(controller, "MatchesCharacter", (object)null));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }


        [Test]
        public void EmbodimentPresetAssembly_DoesNotReferenceConcreteEmbodimentModules()
        {
            string asmdefPath = Path.Combine(
                PackageRoot,
                "SDK",
                "Modules",
                "Embodiment",
                "Convai.Modules.Embodiment.asmdef");
            string json = File.ReadAllText(asmdefPath);

            Assert.IsFalse(json.Contains("Convai.Modules.ConversationFlow"));
            Assert.IsFalse(json.Contains("Convai.Modules.Attention"));
            Assert.IsFalse(json.Contains("Convai.Modules.Emotion"));
            Assert.IsFalse(json.Contains("Convai.Modules.Gaze"));
        }

        [Test]
        public void BodyAnimationAssembly_DoesNotReferenceConcreteRuntimeModules()
        {
            string asmdefPath = Path.Combine(
                PackageRoot,
                "SDK",
                "Modules",
                "BodyAnimation",
                "Convai.Modules.BodyAnimation.asmdef");
            string json = File.ReadAllText(asmdefPath);

            Assert.IsFalse(json.Contains("Convai.Modules.ConversationFlow"));
            Assert.IsFalse(json.Contains("Convai.Modules.LipSync"));
        }

        [Test]
        public void AddComponentMenus_ExposeOnlyPublicEmbodimentModules()
        {
            AssertAddComponentMenu<ConvaiEmotionController>("Convai/Embodiment/Emotion");
            // The gaze components were re-pathed 2026-07-28: ten flat, undifferentiated siblings
            // under Convai/Embodiment/ meant a user browsing Add Component could not tell which of
            // the ten they needed. The controller stays alongside the other module entry points;
            // everything optional moved under Convai/Gaze/, with the rarely-added ones one level
            // deeper. No type or namespace changed, so nothing in code or an existing scene broke.
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
            AssertAddComponentMenu<ConvaiBodyAnimationController>("Convai/Embodiment/Body Animation");
            AssertAddComponentMenu<ConvaiBodyLanguageController>("Convai/Embodiment/Body Language");
            AssertAddComponentMenu<ConvaiNavMeshLocomotion>("Convai/Embodiment/NavMesh Locomotion");
            AssertAddComponentMenu<ConvaiEmbodimentPresetBinding>("Convai/Embodiment/Preset");
            AssertAddComponentMenu<ConvaiConversationFlowController>("Convai/Embodiment/Conversation Flow");
            AssertAddComponentMenu<StandardRigBinding>("Convai/Embodiment/Character Rig");

            AssertAddComponentMenu<EmbodimentContext>(string.Empty);
        }

        [Test]
        public void CoreAndLipSync_DoNotReferenceEmbodimentCompositionRoot()
        {
            AssertFileDoesNotContain(
                "SDK/Runtime/Components/ConvaiCharacter.cs",
                "EmbodimentContext",
                "ConvaiCharacter must not know the optional embodiment composition root.");
            AssertFileDoesNotContain(
                "SDK/Modules/LipSync/Components/ConvaiLipSyncComponent.cs",
                "EmbodimentContext",
                "ConvaiLipSyncComponent must not register embodiment providers directly.");
            AssertFileDoesNotContain(
                "SDK/Modules/LipSync/Components/ConvaiLipSyncComponent.cs",
                "Convai.Runtime.Embodiment",
                "ConvaiLipSyncComponent must stay embodiment-assembly agnostic.");
            AssertFileDoesNotContain(
                "SDK/Modules/LipSync/Components/LipSyncRuntimeController.cs",
                "EmbodimentContext",
                "LipSync runtime playback must not resolve embodiment context.");
            AssertFileDoesNotContain(
                "SDK/Modules/LipSync/Components/LipSyncRuntimeController.cs",
                "IEmotionMouthWeightProvider",
                "Emotion fade blending belongs to optional embodiment integration, not lipsync playback.");
            AssertFileDoesNotContain(
                "SDK/Modules/LipSync/Sinks/SkinnedMeshBlendshapeSink.cs",
                "GetOrCreate",
                "SkinnedMeshBlendshapeSink must not create FacialBlendshapeCompositorHost; use LipSyncBlendshapeOutputSinkFactory.");
        }

        [Test]
        public void ConversationOnlyContext_DoesNotEagerCreateRigCompositorOrConductor()
        {
            GameObject root = new("ConversationOnlyLazyContextTest");

            try
            {
                root.AddComponent<ConvaiCharacter>();
                ConvaiConversationFlowController flow = root.AddComponent<ConvaiConversationFlowController>();
                Assert.IsTrue(EmbodimentContext.TryResolve(flow, out EmbodimentContext context));
                Assert.NotNull(context);

                Assert.IsNull(root.GetComponentInChildren<StandardRigBinding>(true));
                Assert.IsNull(root.GetComponentInChildren<FacialBlendshapeCompositorHost>(true));
                Assert.IsNull(root.GetComponentInChildren<AnimatorConductor>(true));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static string ToPackagePath(string path) =>
            Path.GetRelativePath(PackageRoot, path).Replace('\\', '/');

        private static object InvokePrivateWithResult(object target, string methodName, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, $"Missing method {methodName}.");
            return method.Invoke(target, args);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static void AssertAddComponentMenu<T>(string expected)
        {
            AddComponentMenu attribute = typeof(T).GetCustomAttribute<AddComponentMenu>();
            Assert.NotNull(attribute, $"{typeof(T).Name} must declare AddComponentMenu.");
            Assert.AreEqual(expected, attribute.componentMenu);
        }

        private static void AssertFileDoesNotContain(string packageRelativePath, string token, string message)
        {
            string path = Path.Combine(PackageRoot, packageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"Expected source file to exist: {packageRelativePath}");
            string source = File.ReadAllText(path);
            StringAssert.DoesNotContain(token, source, message);
        }
    }
}
