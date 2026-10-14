using System.Collections.Generic;
using System.IO;
using System.Linq;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Profiles;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Guards that no serialized field on the Emotion component or its personality asset can be
    ///     edited by nobody.
    /// </summary>
    /// <remarks>
    ///     This is the permanent fix for a defect that shipped: five fields configuring the
    ///     post-action mood reaction were serialized on <see cref="ConvaiEmotionController" />,
    ///     drawn by no inspector, and caught by no fallback iterator — so the feature they
    ///     configured could not be used, changed, or discovered by anyone.
    /// </remarks>
    public sealed class EmotionInspectorReachabilityTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        /// <summary>
        ///     Reads a type's serialized field names off a throwaway instance. Component probes are
        ///     hosted on an <see cref="EmbodimentTestRig" /> rather than a bare
        ///     <see cref="GameObject" />, so an embodiment component finds the context it needs and
        ///     enables cleanly — on a bare object it would correctly report that it is not on a Convai
        ///     character, and every run of this fixture would log a setup error for a probe nobody
        ///     intends to use.
        /// </summary>
        private static List<string> SerializedFieldNames<T>() where T : Object
        {
            T instance;
            EmbodimentTestRig rig = null;
            if (typeof(T).IsSubclassOf(typeof(ScriptableObject)))
            {
                instance = ScriptableObject.CreateInstance(typeof(T)) as T;
            }
            else
            {
                rig = EmbodimentTestRig.Create("reachability-probe");
                instance = rig.Root.AddComponent(typeof(T)) as T;
            }

            try
            {
                var names = new List<string>();
                var serialized = new SerializedObject(instance);
                SerializedProperty iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyPath == "m_Script") continue;
                    names.Add(iterator.propertyPath);
                }
                return names;
            }
            finally
            {
                if (rig != null) rig.Dispose();
                else Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void EveryControllerField_IsDrawnByTheInspector()
        {
            string inspector = File.ReadAllText(Path.Combine(PackageRoot,
                "SDK", "Modules", "Emotion", "Editor", "ConvaiEmotionControllerEditor.cs"));

            // Fields the inspector deliberately never draws, each with a reason. Empty today: every
            // serialized field on the component is a user-facing setting.
            var intentionallyHidden = new HashSet<string>();

            List<string> unreachable = SerializedFieldNames<ConvaiEmotionController>()
                .Where(f => !intentionallyHidden.Contains(f))
                .Where(f => !inspector.Contains($"\"{f}\""))
                .ToList();

            Assert.That(unreachable, Is.Empty,
                "These fields are serialized on the Emotion component but no inspector draws them, " +
                "so nobody can edit the behaviour they configure: " + string.Join(", ", unreachable));
        }

        [Test]
        public void EveryProfileField_IsReachableThroughTheSectionTable()
        {
            var mapped = new HashSet<string>();
            EmotionConfigSections.CollectMappedFields(mapped);

            List<string> unreachable = SerializedFieldNames<ConvaiEmotionProfile>()
                .Where(f => !mapped.Contains(f))
                .ToList();

            Assert.That(unreachable, Is.Empty,
                "These fields are serialized on the personality asset but belong to no section, so " +
                "neither the inspector nor the Emotion editor window shows them: " +
                string.Join(", ", unreachable));
        }

        [Test]
        public void TheControllerInspector_LivesInItsOwnFile()
        {
            // It used to be one class among five in a 999-line shared file, which is how it ended up
            // with no fallback iterator and five invisible fields. It now sits in the module's own
            // editor assembly rather than the shared embodiment one, so the coupling that put it
            // there is gone too.
            string path = Path.Combine(PackageRoot,
                "SDK", "Modules", "Emotion", "Editor", "ConvaiEmotionControllerEditor.cs");

            Assert.That(File.Exists(path), Is.True,
                "The Emotion component's inspector must stay in its own file, like Body Animation's " +
                "and Gaze's.");
        }
    }
}
