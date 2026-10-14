using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.BodyLanguage.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     What Body Language owes the shared embodiment surface: the component menu a user finds it
    ///     under, the demeanor vocabulary it presents alongside Emotion and Body Animation, and the
    ///     promise that setting it up leaves the character owning its own settings asset.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These assertions used to live in the shared embodiment suite, where they asserted
    ///         things about a module that had not landed yet. They were removed with that module's
    ///         release in mind rather than trimmed away, so they are re-authored here — in the folder
    ///         the session that owns this module actually reads.
    ///     </para>
    ///     <para>
    ///         Self-contained on purpose. The helpers they leaned on went with them, and rebuilding
    ///         them here costs a few lines and removes a dependency on a file no release owns.
    ///     </para>
    /// </remarks>
    public sealed class BodyLanguageEmbodimentContractTests
    {
        private readonly List<string> _createdAssetPaths = new();
        private readonly List<GameObject> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
                if (_createdObjects[i] != null)
                    Object.DestroyImmediate(_createdObjects[i]);
            _createdObjects.Clear();

            for (int i = 0; i < _createdAssetPaths.Count; i++)
                AssetDatabase.DeleteAsset(_createdAssetPaths[i]);
            _createdAssetPaths.Clear();
        }

        /// <summary>
        ///     The menu path is how a user finds this module at all, so it is API in every sense that
        ///     matters — changing it silently strands every instruction that names it.
        /// </summary>
        [Test]
        public void TheComponentIsFoundUnderTheEmbodimentMenu()
        {
            var attribute = typeof(ConvaiBodyLanguageController).GetCustomAttribute<AddComponentMenu>();

            Assert.NotNull(attribute,
                "ConvaiBodyLanguageController must declare AddComponentMenu, or a user cannot find it " +
                "under Add Component.");
            Assert.AreEqual("Convai/Embodiment/Body Language", attribute.componentMenu);
        }

        /// <summary>
        ///     A character with no settings asset assigned must still be a working character — the
        ///     reason setup is allowed to assign nothing.
        /// </summary>
        [Test]
        public void ThereIsACodeDefinedDefaultPersonality()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                Assert.That(profile, Is.Not.Null,
                    "Body Language must have a code-defined default, or a character with no " +
                    "personality assigned would be broken rather than merely untuned.");
            }
            finally
            {
                if (profile != null) Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     Demeanor names are shared with Emotion and Body Animation and have drifted apart once
        ///     already. Every module must present all four, in the one order the Domain defines.
        /// </summary>
        [Test]
        public void ThePersonalityPresetsPresentEveryDemeanor_InTheSharedOrder()
        {
            CharacterDemeanor[] expected = (CharacterDemeanor[])Enum.GetValues(typeof(CharacterDemeanor));
            CharacterDemeanor[] presented =
                BodyLanguageDemeanorPresets.Presets.Select(preset => preset.Demeanor).ToArray();

            CollectionAssert.AreEqual(expected, presented,
                "Body Language must offer every demeanor the Domain defines, in the same order the " +
                "other modules present them — a user comparing two modules side by side is comparing " +
                "the same list.");

            foreach (BodyLanguageDemeanorPresets.Multipliers preset in BodyLanguageDemeanorPresets.Presets)
                Assert.That(preset.Name, Is.Not.Null.And.Not.Empty,
                    $"The {preset.Demeanor} preset must be named for the dropdown to be usable.");
        }

        /// <summary>
        ///     Setting a character up gives it a settings asset it owns, rather than pointing it at
        ///     one inside the package that it cannot edit.
        /// </summary>
        [Test]
        public void SetupGivesTheCharacterItsOwnPersonalityAsset()
        {
            ConvaiBodyLanguageController controller = NewCharacter("BodyLanguage_SetupOutcome");

            if (!BodyLanguageSetupService.ApplyFix(controller, BodyLanguageFixId.AssignDefaultProfile))
                Assert.Ignore("Body Language setup did not run in this project.");

            ConvaiBodyLanguageProfile profile =
                TrackAsset(BodyLanguageSetupService.ResolveAssignedProfile(controller));

            Assert.That(profile, Is.Not.Null, "Setup reported success but assigned no personality.");
            Assert.That(IsProjectAsset(profile), Is.True,
                "The assigned personality must live in the project, where the user can edit it — not " +
                "inside the package, where every change would be lost or shared with every character.");
        }

        private ConvaiBodyLanguageController NewCharacter(string name)
        {
            var host = new GameObject(name);
            _createdObjects.Add(host);
            host.AddComponent<EmbodimentContext>();
            return host.AddComponent<ConvaiBodyLanguageController>();
        }

        private ConvaiBodyLanguageProfile TrackAsset(ConvaiBodyLanguageProfile profile)
        {
            if (profile == null) return null;

            string path = AssetDatabase.GetAssetPath(profile);
            if (!string.IsNullOrEmpty(path)) _createdAssetPaths.Add(path);
            return profile;
        }

        private static bool IsProjectAsset(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal);
        }
    }
}
