using System.Collections.Generic;
using System.IO;
using System.Linq;
using Convai.Domain.Embodiment.Semantics;
using Convai.Editor.Ownership;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     The promises Body Animation makes to the embodiment layer around it: that its assembly
    ///     stays independent of every peer module, that it spells the four temperaments the way the
    ///     rest of the SDK does, and that setting it up leaves the character owning its own profile
    ///     rather than editing an asset inside the package.
    /// </summary>
    /// <remarks>
    ///     These assertions used to live beside the embodiment layer's own tests, where they could
    ///     only run once every module had landed. They belong here instead: each one describes
    ///     something this module must keep true, so it should fail in this module's own suite.
    /// </remarks>
    [TestFixture]
    [Category("Architecture")]
    public sealed class BodyAnimationEmbodimentContractTests
    {
        // Fully qualified: this assembly's own namespace makes a bare `Application` resolve to
        // Convai.Tests.EditMode.Application rather than UnityEngine's.
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        private readonly List<Object> _created = new();
        private readonly List<string> _createdAssetPaths = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _created)
                if (o != null)
                    Object.DestroyImmediate(o);
            _created.Clear();

            foreach (string path in _createdAssetPaths)
                if (!string.IsNullOrEmpty(path))
                    AssetDatabase.DeleteAsset(path);
            _createdAssetPaths.Clear();
        }

        // ------------------------------------------------------------------ assembly independence

        /// <summary>
        ///     Body Animation must never reference another module's assembly. Peers meet it through
        ///     interfaces on the embodiment service registry, so the module has to build and run with
        ///     every one of them absent.
        /// </summary>
        [Test]
        public void BodyAnimationAssembly_DoesNotReferenceConcreteRuntimeModules()
        {
            string asmdefPath = Path.Combine(
                PackageRoot, "SDK", "Modules", "BodyAnimation", "Convai.Modules.BodyAnimation.asmdef");
            string json = File.ReadAllText(asmdefPath);

            Assert.IsFalse(json.Contains("Convai.Modules.ConversationFlow"));
            Assert.IsFalse(json.Contains("Convai.Modules.LipSync"));
            Assert.IsFalse(json.Contains("Convai.Modules.Gaze"));
            Assert.IsFalse(json.Contains("Convai.Modules.Emotion"));
            Assert.IsFalse(json.Contains("Convai.Modules.BodyLanguage"));
        }

        // ------------------------------------------------------------------ shared vocabulary

        /// <summary>
        ///     The demeanor picker shows the same four temperaments, in the same order, as every
        ///     other module that has one — a user comparing two inspectors on one character must not
        ///     see two different words for the same idea.
        /// </summary>
        [Test]
        public void BodyAnimation_PresentsEveryDemeanor_InTheSharedOrder()
        {
            CharacterDemeanor[] expected = CharacterDemeanors.Order.ToArray();

            CollectionAssert.AreEqual(
                expected,
                BodyAnimationPersonality.Archetypes.Select(a => a.Demeanor).ToArray(),
                "Body Animation does not present the four temperaments in the shared order.");

            CollectionAssert.AreEqual(
                expected.Select(CharacterDemeanors.DisplayName).ToArray(),
                BodyAnimationPersonality.Archetypes.Select(a => a.Name).ToArray(),
                "Body Animation spells a temperament differently from the shared vocabulary.");
        }

        /// <summary>
        ///     The names are shared; the descriptions are not, and must not be — "Reserved" means a
        ///     different thing to a walk cycle than it does to a face. This only checks the module
        ///     wrote one for each.
        /// </summary>
        [Test]
        public void BodyAnimation_DescribesEveryDemeanor_InItsOwnWording()
        {
            foreach (BodyAnimationArchetype archetype in BodyAnimationPersonality.Archetypes)
                Assert.IsNotEmpty(
                    archetype.Description,
                    $"Body Animation has no description for {archetype.Demeanor}.");
        }

        // ------------------------------------------------------------------ asset ownership

        /// <summary>
        ///     Setting the module up must leave the character pointing at a profile in the user's own
        ///     project. A shipped asset inside the package cannot be edited, so a setup that assigned
        ///     one would hand the user a character they cannot tune.
        /// </summary>
        [Test]
        public void Outcome_BodyAnimationSetupGivesTheCharacterItsOwnProfile()
        {
            ConvaiBodyAnimationController controller = NewCharacter();
            Assert.That(
                BodyAnimationSetupService.ApplyFix(controller, BodyAnimationFixId.AssignDefaultContent),
                Is.True,
                "setup could not assign the shipped animation content");

            ConvaiBodyAnimationSet set = BodyAnimationSetupService.ResolveAssignedSet(controller);
            Assert.That(set, Is.Not.Null, "setup assigned no animation content");

            var serialized = new SerializedObject(controller);
            var profile = serialized.FindProperty("profile").objectReferenceValue as ConvaiBodyAnimationProfile;
            Assert.That(profile, Is.Not.Null, "setup assigned no profile");
            TrackAsset(profile);

            Assert.That(
                ConvaiAssetOwnership.IsProjectAsset(profile), Is.True,
                "the character's profile must live in the project, not in the package");

            // The animation set is content — clips are consumed, not tuned — so it rightly stays in
            // the package and is referenced. Only the tuning half has to move.
            Assert.That(
                BodyAnimationSetupService.ResolveAssignedConfig(controller), Is.Null,
                "no config asset should exist until the user actually tunes something");
        }

        // ------------------------------------------------------------------ helpers

        private ConvaiBodyAnimationController NewCharacter()
        {
            var go = new GameObject("BodyAnimationContractTests");
            _created.Add(go);
            go.AddComponent<EmbodimentContext>();
            go.AddComponent<Animator>();
            return go.AddComponent<ConvaiBodyAnimationController>();
        }

        private T TrackAsset<T>(T asset) where T : Object
        {
            if (asset == null) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/"))
                _createdAssetPaths.Add(path);
            return asset;
        }
    }
}
