using System.Collections.Generic;
using System.IO;
using Convai.Editor.Embodiment.Inspectors;
using Convai.Modules.Embodiment.Presets;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Emotion.Taxonomy;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Convai.Editor.Embodiment.Setup;
using Convai.Domain.Embodiment.Modules;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class EmbodimentProfileEditorTests
    {
        [Test]
        public void EmbodimentProfileAssets_CreateConvaiEmbodimentEditors()
        {
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<ConvaiEmbodimentPresetLibrary>());
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>());
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<ConvaiConversationFlowProfile>());
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<ConvaiEmotionProfile>());
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<EmotionTaxonomyAsset>());
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<ConvaiGazeProfile>());
            AssertUsesEmbodimentEditor(ScriptableObject.CreateInstance<ConvaiBodyAnimationProfile>());
        }

        [Test]
        public void EmbodimentProfileEditors_ExposeStableSectionIds()
        {
            Assert.AreEqual("Presets", ConvaiEmbodimentPresetLibraryInspector.SectionPresets);
            Assert.AreEqual("Identity", ConvaiEmbodimentPresetInspector.SectionIdentity);
            // The emotion profile's thirteen engineering-named sections became nine named after
            // what the user is trying to do, and they now live on the shared section table both
            // the inspector and the Emotion editor window render.
            Assert.AreEqual("Personality", EmotionConfigSections.Sections[0].Id);
            Assert.AreEqual("RestingMood", EmotionConfigSections.Sections[1].Id);
            // The gaze profile's eleven engineering-named sections became six named after what the
            // user is trying to do; "StatePolicies" folded into "Personality".
            Assert.AreEqual("Personality", ConvaiGazeProfileInspector.SectionPersonality);
            Assert.AreEqual("Content", BodyAnimationProfileInspector.SectionContent);
        }

        [Test]
        public void EmbodimentProfileEditors_DoNotUseAdHocEditorPrefs()
        {
            string root = Path.GetFullPath(Path.Combine(
                global::UnityEngine.Application.dataPath,
                "..",
                "Packages",
                "com.convai.convai-sdk-for-unity",
                "SDK",
                "Editor",
                "Embodiment",
                "Inspectors"));

            AssertNoEditorPrefs(Path.Combine(root, "EmbodimentProfileEditorBase.cs"));
            AssertNoEditorPrefs(Path.Combine(root, "EmbodimentProfileAssetEditors.cs"));
            AssertNoEditorPrefs(Path.Combine(root, "ConvaiEmbodimentPresetInspector.cs"));
            // Extracted out of EmbodimentProfileAssetEditors when the gaze profile inspector grew
            // its own labelling and personality surface — it must keep the same rule.
            AssertNoEditorPrefs(Path.Combine(root, "ConvaiGazeProfileInspector.cs"));
        }

        [Test]
        public void PresetTroubleshooter_DetectsDuplicateEmptyNullAndWrongType()
        {
            // Rewritten for the catalog-backed troubleshooter that replaced the hand-written
            // module map. That map was missing two entries, which is why the shipped sample preset
            // reported its own correct body-language slot as an unrecognized module.
            var flow = ScriptableObject.CreateInstance<ConvaiConversationFlowProfile>();
            var emotion = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new("convai.conversation-flow", flow),
                    new("convai.conversation-flow", flow),
                    new(string.Empty, flow),
                    new("convai.emotion", null),
                    new("convai.body-animation", emotion),
                });

                EmbodimentSetupReport report = EmbodimentPresetTroubleshooter.Evaluate(preset);

                Assert.IsTrue(HasFinding(report, "preset.slot-duplicate"), "duplicate feature not reported");
                Assert.IsTrue(HasFinding(report, "preset.slot-no-module"), "empty feature not reported");
                Assert.IsTrue(HasFinding(report, "preset.slot-no-profile"), "missing settings not reported");
                Assert.IsTrue(HasFinding(report, "preset.slot-wrong-type"), "wrong settings type not reported");
                Assert.IsTrue(report.HasBlocker, "these problems must block, not merely warn");
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(flow);
                Object.DestroyImmediate(emotion);
            }
        }

        [Test]
        public void PresetTroubleshooter_AcceptsEveryShippedModuleId()
        {
            // The regression guard for the drift that shipped: every id in ModuleIds must be a
            // feature the catalog knows, or a preset using it renders a false warning.
            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(ModuleIds.ConversationFlow, null),
                    new(ModuleIds.Emotion, null),
                    new(ModuleIds.Gaze, null),
                    new(ModuleIds.BodyAnimation, null),
                    new(ModuleIds.BodyLanguage, null),
                });

                EmbodimentSetupReport report = EmbodimentPresetTroubleshooter.Evaluate(preset);

                Assert.IsFalse(HasFinding(report, "preset.slot-unknown-module"),
                    "a shipped ModuleIds constant was not recognized by the catalog");
                Assert.IsFalse(report.HasBlocker);
            }
            finally
            {
                Object.DestroyImmediate(preset);
            }
        }

        private static bool HasFinding(EmbodimentSetupReport report, string id)
        {
            for (int i = 0; i < report.Findings.Count; i++)
                if (report.Findings[i].Id == id) return true;
            return false;
        }

        private static void AssertUsesEmbodimentEditor(ScriptableObject target)
        {
            UnityEditor.Editor editor = null;
            try
            {
                editor = UnityEditor.Editor.CreateEditor(target);
                Assert.IsNotNull(editor, $"No editor created for {target.GetType().Name}");
                Assert.AreEqual(
                    "Convai.Editor.Embodiment",
                    editor.GetType().Assembly.GetName().Name,
                    $"{target.GetType().Name} used {editor.GetType().FullName}");
            }
            finally
            {
                if (editor != null)
                    Object.DestroyImmediate(editor);
                if (target != null)
                    Object.DestroyImmediate(target);
            }
        }

        private static void AssertNoEditorPrefs(string path)
        {
            Assert.IsTrue(File.Exists(path), $"Expected {path}");
            string text = File.ReadAllText(path);
            StringAssert.DoesNotContain("EditorPrefs", text);
        }

    }
}
