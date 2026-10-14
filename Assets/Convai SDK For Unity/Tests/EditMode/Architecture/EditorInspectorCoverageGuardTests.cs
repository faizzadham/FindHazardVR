#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards that every component and asset a customer can select in the Hierarchy or Project
    ///     window is either presented by a Convai inspector, or is on a recorded list of the ones that
    ///     deliberately are not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The design-system guards check the editors that exist. They cannot see a type with no
    ///         editor at all, and that was the larger gap: for a long stretch only 47 of 86 inspectable
    ///         types had one, so a user selecting a Convai Character saw a designed panel and a user
    ///         selecting the Convai Manager beneath it saw Unity's default grey one. Nothing was
    ///         broken; the product just looked half-finished at the exact moment a new user was forming
    ///         their first impression of it.
    ///     </para>
    ///     <para>
    ///         Not every type should have one. A sample presentation widget, an internal scheduler and
    ///         an abstract base a user derives from are all better served by Unity's default inspector
    ///         than by chrome that implies they are a configuration surface. The point of this test is
    ///         not to force coverage to 100% — it is to make "we chose not to cover this" a written
    ///         decision with a reason, so that it can never again be indistinguishable from an
    ///         oversight.
    ///     </para>
    ///     <para>
    ///         <b>Adding a type?</b> Either give it a <see cref="CustomEditor" /> deriving from
    ///         <c>ConvaiInspectorEditor</c>, or add it to <see cref="Exemptions" /> with a sentence
    ///         saying why it does not need one. A stale exemption — one naming a type that no longer
    ///         exists — also fails, so the list cannot rot quietly.
    ///     </para>
    /// </remarks>
    public class EditorInspectorCoverageGuardTests
    {
        /// <summary>Package runtime assemblies whose components and assets a user can select.</summary>
        private static readonly string[] RuntimeAssemblyNames =
        {
            "Convai.Runtime",
            "Convai.Runtime.UI",
            "Convai.Shared.Unity",
            "Convai.Modules.BodyAnimation",
            "Convai.Modules.BodyLanguage",
            "Convai.Modules.ConversationFlow",
            "Convai.Modules.Embodiment",
            "Convai.Modules.Emotion",
            "Convai.Modules.Gaze",
            "Convai.Modules.LipSync",
            "Convai.Modules.Narrative",
            "Convai.Modules.Vision"
        };

        /// <summary>Assemblies that may declare a <see cref="CustomEditor" /> for those types.</summary>
        private static readonly string[] EditorAssemblyNames =
        {
            "Convai.Editor",
            "Convai.Editor.AI",
            "Convai.Editor.Embodiment",
            "Convai.Modules.BodyAnimation.Editor",
            "Convai.Modules.BodyLanguage.Editor",
            "Convai.Modules.Emotion.Editor",
            "Convai.Modules.Gaze.Editor"
        };

        /// <summary>
        ///     Types that deliberately stay on Unity's default inspector, each with the reason.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Two kinds of thing live here and no third kind should be added without discussion:
        ///         presentation widgets that ship as sample UI rather than SDK configuration surface,
        ///         and internal plumbing a user never selects on purpose.
        ///     </para>
        ///     <para>
        ///         Abstract bases are deliberately <em>not</em> listed. They are excluded from the
        ///         population by <see cref="InspectableTypes" /> because Unity never inspects one
        ///         directly — the user's concrete subclass is what appears. Listing them here would be
        ///         a decision about something that was never in scope, and
        ///         <see cref="EveryExemption_NamesATypeThatStillExists" /> correctly reports such an
        ///         entry as stale.
        ///     </para>
        /// </remarks>
        private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
        {
            ["ConvaiAutoTagOwnership"] = "Internal generated-field ownership records; review and removal are owned by Scene Auto Tagger.",
            // Sample presentation widgets. These ship as the reference chat UI, not as SDK surface.
            // A Convai header on them would imply they are a supported configuration point.
            ["CanvasFader"] = "Sample UI helper — fades a canvas; not an SDK configuration surface.",
            ["ChatMessageBubble"] = "Sample chat UI — one message row in the reference transcript.",
            ["ChatTranscriptUI"] = "Sample chat UI — the reference transcript view.",
            ["SettingsPanel"] = "Sample settings UI shipped with the reference chat scene.",
            ["MicrophoneTestController"] = "Sample settings UI — drives the microphone test in that panel.",
            ["FeedbackHandler"] = "Sample transcript UI — the thumbs up/down affordance.",
            ["NotificationHandler"] = "Sample notification UI.",
            ["SONotificationGroup"] = "Sample notification content asset.",
            ["SONotification"] = "Sample notification content asset — one notification's text and icon.",
            ["SONotificationErrorMap"] = "Sample notification content asset — maps error codes to notifications.",
            ["UINotification"] = "Sample notification UI — one notification view.",
            ["UINotificationController"] = "Sample notification UI — spawns and retires notifications.",
            ["ConnectionStatusIndicator"] = "Sample transcript UI — tints an image by connection state.",
            ["PlayerSpeakingIndicator"] = "Sample transcript UI — tints an image while the player speaks.",
            ["ConvaiSettingsClickArea"] = "Sample UI hit target that opens the sample settings panel.",
            ["ConvaiIdleResetDriver"] = "Sample UI helper that returns the reference scene to idle.",

            // Internal plumbing. A user does not select these to configure anything; they are added
            // by the SDK or by a preset, and their fields are wiring rather than settings.
            ["UnityScheduler"] = "Internal — pumps SDK work onto the Unity main thread.",
            ["UnityConvaiAdapter"] = "Internal — adapts the engine-free core to Unity.",
            ["CompositorDialoguePhaseAdapter"] = "Internal — relays dialogue phase into the pose compositor.",
            ["CharacterBehaviorDispatcher"] = "Internal — routes events to the character's behaviours.",
            ["ConvaiLipSyncSpeechEnergyAdapter"] = "Internal — exposes lip-sync energy to other modules.",
            ["VisionDebugPreview"] = "Diagnostic overlay — a developer aid, not a shipped configuration surface."
        };

        private static IEnumerable<Type> TypesIn(IEnumerable<string> assemblyNames)
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            foreach (string name in assemblyNames)
            {
                Assembly assembly = all.FirstOrDefault(a => a.GetName().Name == name);
                if (assembly == null)
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in types)
                    yield return type;
            }
        }

        /// <summary>
        ///     Every public, concrete <see cref="MonoBehaviour" /> or <see cref="ScriptableObject" /> a
        ///     user can add or create — the population an inspector could present.
        /// </summary>
        /// <remarks>
        ///     Non-public types never reach a user's Add Component menu, and abstract ones are never
        ///     inspected directly; both are excluded here rather than exempted individually, because
        ///     they are not decisions — they are simply not in the population.
        /// </remarks>
        private static IEnumerable<Type> InspectableTypes() =>
            TypesIn(RuntimeAssemblyNames)
                .Where(t => t.IsPublic && !t.IsAbstract && !t.IsGenericTypeDefinition)
                .Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) || typeof(ScriptableObject).IsAssignableFrom(t))
                .Where(t => !typeof(UnityEditor.Editor).IsAssignableFrom(t))
                .Where(t => !typeof(EditorWindow).IsAssignableFrom(t));

        /// <summary>Maps each inspected type to the Convai inspector that presents it.</summary>
        private static HashSet<Type> TypesWithConvaiInspector()
        {
            var covered = new HashSet<Type>();

            foreach (Type editor in TypesIn(EditorAssemblyNames))
            {
                if (!typeof(UnityEditor.Editor).IsAssignableFrom(editor)) continue;
                if (!IsConvaiInspector(editor)) continue;

                var attribute = editor.GetCustomAttribute<CustomEditor>();
                if (attribute == null) continue;

                var inspected = (Type)typeof(CustomEditor)
                    .GetField("m_InspectedType", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(attribute);
                if (inspected == null) continue;

                // An editor registered for a base type covers everything derived from it — that is how
                // ConvaiActionExecutorInspector presents every shipped executor without one editor each.
                foreach (Type candidate in InspectableTypes())
                {
                    if (inspected.IsAssignableFrom(candidate))
                        covered.Add(candidate);
                }
            }

            return covered;
        }

        private static bool IsConvaiInspector(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (current.Name == "ConvaiInspectorEditor") return true;
            }

            return false;
        }

        [Test]
        public void EveryInspectableType_HasAConvaiInspector_OrARecordedReasonItDoesNot()
        {
            HashSet<Type> covered = TypesWithConvaiInspector();

            List<string> uncovered = InspectableTypes()
                .Where(t => !covered.Contains(t))
                .Where(t => !Exemptions.ContainsKey(t.Name))
                .Select(t => $"{t.Name}  ({t.Namespace})")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.IsEmpty(
                uncovered,
                "These types fall through to Unity's default inspector with no recorded decision behind " +
                "it. A user selecting one sees a plain grey panel next to Convai's designed ones.\n\n" +
                "Either give it a [CustomEditor] deriving from ConvaiInspectorEditor, or add it to " +
                $"{nameof(EditorInspectorCoverageGuardTests)}.{nameof(Exemptions)} with the reason:\n" +
                string.Join("\n", uncovered));
        }

        [Test]
        public void EveryExemption_NamesATypeThatStillExists()
        {
            HashSet<string> live = InspectableTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            List<string> stale = Exemptions.Keys
                .Where(name => !live.Contains(name))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.IsEmpty(
                stale,
                "These exemptions name types that no longer exist as public inspectable types. An " +
                "exemption list that outlives its subjects stops being a record of decisions and starts " +
                "hiding new gaps behind old names. Remove them:\n" + string.Join("\n", stale));
        }

        [Test]
        public void NoExemption_AlsoHasAConvaiInspector()
        {
            HashSet<Type> covered = TypesWithConvaiInspector();

            List<string> contradictory = covered
                .Where(t => Exemptions.ContainsKey(t.Name))
                .Select(t => t.Name)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.IsEmpty(
                contradictory,
                "These types have a Convai inspector and are also listed as deliberately not having " +
                "one. The exemption is now false — delete it:\n" + string.Join("\n", contradictory));
        }
    }
}
#endif
