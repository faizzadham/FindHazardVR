using System.Collections.Generic;
using Convai.Domain.Embodiment.Modules;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.Embodiment.Presets;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Preset routing tests: a <see cref="ConvaiEmbodimentPreset" /> with a body
    ///     language slot resolves the profile by <see cref="ModuleIds.BodyLanguage" /> through
    ///     the generic <c>profileSlots</c> list — no schema change required.
    /// </summary>
    public sealed class BodyLanguagePresetRoutingTests
    {
        [Test]
        public void Preset_WithBodyLanguageSlot_RoutesProfileByModuleId()
        {
            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(ModuleIds.BodyLanguage, profile)
                });

                Assert.IsTrue(preset.TryGetProfile(ModuleIds.BodyLanguage, out ScriptableObject resolved),
                    "The preset must resolve a slot keyed by the body language module id.");
                Assert.That(resolved, Is.SameAs(profile));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(preset);
            }
        }

        [Test]
        public void Preset_ModuleIdLookup_IsCaseInsensitive()
        {
            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(ModuleIds.BodyLanguage, profile)
                });

                Assert.IsTrue(preset.TryGetProfile("CONVAI.BODY-LANGUAGE", out ScriptableObject resolved));
                Assert.That(resolved, Is.SameAs(profile));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(preset);
            }
        }

        [Test]
        public void Preset_WithoutBodyLanguageSlot_ReturnsFalse()
        {
            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                Assert.IsFalse(preset.TryGetProfile(ModuleIds.BodyLanguage, out ScriptableObject resolved));
                Assert.IsNull(resolved);
            }
            finally
            {
                Object.DestroyImmediate(preset);
            }
        }
    }
}
