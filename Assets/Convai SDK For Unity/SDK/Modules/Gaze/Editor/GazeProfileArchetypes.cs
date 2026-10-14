using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Data;
using UnityEditor;

namespace Convai.Modules.Gaze.Editor
{
    /// <summary>
    ///     Authored gaze "personalities" for the <see cref="ConvaiGazeProfile" /> inspector: one
    ///     click fills the whole per-state policy table plus the idle-life, blink-cadence, and
    ///     face-scan feel with a coherent character. Editor-only — no runtime surface. Every
    ///     value is inside the profile's own validation ranges by construction, so an applied
    ///     archetype always survives <c>OnValidate</c>.
    /// </summary>
    internal static class GazeProfileArchetypes
    {
        /// <summary>One per-state policy row of an archetype.</summary>
        internal readonly struct StateRow
        {
            public readonly DialogueState State;
            public readonly float Engagement;
            public readonly bool AllowPlayerTarget;
            public readonly float HeadContribution;
            public readonly bool AllowBodyTurn;
            public readonly GazeAversionMode AversionMode;
            public readonly float AversionStrength;
            public readonly float FixationLiveliness;

            public StateRow(
                DialogueState state, float engagement, bool allowPlayerTarget, float headContribution,
                bool allowBodyTurn, GazeAversionMode aversionMode, float aversionStrength, float fixationLiveliness)
            {
                State = state;
                Engagement = engagement;
                AllowPlayerTarget = allowPlayerTarget;
                HeadContribution = headContribution;
                AllowBodyTurn = allowBodyTurn;
                AversionMode = aversionMode;
                AversionStrength = aversionStrength;
                FixationLiveliness = fixationLiveliness;
            }
        }

        /// <summary>A complete authored personality: the state table plus every field that carries feel.</summary>
        /// <remarks>
        ///     <para>
        ///         "Complete" is load-bearing. An archetype that writes only some of the fields a
        ///         personality is made of leaves the rest of the previous one behind, so switching
        ///         from Shy to Stoic produced neither — it produced a Stoic with Shy's restless eyes
        ///         and Shy's nodding. Every field the three personality dials read
        ///         (<see cref="GazePersonality" />) is therefore authored here, on every archetype.
        ///     </para>
        /// </remarks>
        internal sealed class GazeArchetype
        {
            public string Name;
            public string Description;
            public StateRow[] States;

            // Idle life — what it does when nobody is talking to it.
            public float AmbientYawRangeDegrees;
            public float AmbientIntervalMin;
            public float AmbientIntervalMax;
            public float AmbientHeadFollow;
            public float AmbientRecenterBias;
            public bool EnableCuriosityGlances;
            public float CuriosityGlanceIntervalMin;
            public float CuriosityGlanceIntervalMax;

            // Eye liveliness — the "how busy are the eyes" dial reads all three of these.
            public float MicroSaccadeIntervalMean;
            public float FaceScanIntervalMean;
            public float FaceScanRadiusDegrees;
            public float BlinkIntervalMean;

            // Head involvement — the head dial reads the state table's contribution and this.
            public float HeadEntryDegrees;

            // Listening gestures — how much it visibly agrees with you while you talk.
            public bool EnableListeningNods;
            public float NodPitchDegrees;
            public float ListeningNodIntervalMin;
            public float ListeningNodIntervalMax;
            public float AcknowledgeNodProbability;

            // Turn taking.
            public float PlanningBreakProbability;

            // Conversation — how the character follows the room around it.
            public float ReactionMedianSeconds;
            public float ReactionSpread;
            public float AttentionDecaySeconds;
            public float ReactionMinSeparationSeconds;
        }

        /// <summary>
        ///     All shipped archetypes, in inspector-toolbar order. <b>Default</b> comes first and
        ///     is an exact mirror of <c>ConvaiGazeProfile</c>'s own field initializers and
        ///     <c>BuildDefaultStatePolicies()</c> — so a freshly created profile always shows one
        ///     pill already active, and "put it back the way it was" is one click rather than a
        ///     thing the user has to reconstruct by hand. <see cref="GazeProfileDefaultParityTests" />
        ///     fails if the two ever drift apart.
        /// </summary>
        internal static readonly GazeArchetype[] All =
        {
            new()
            {
                Name = "Default",
                Description = "The SDK's shipped tuning — a balanced, conversational baseline. Click to return a profile to "
                              + "it.",
                States = new[]
                {
                    new StateRow(DialogueState.Idle,         0f,    false, 0.35f, false, GazeAversionMode.None,       0f,    1f),
                    new StateRow(DialogueState.Attending,    0.9f,  true,  0.85f, true,  GazeAversionMode.Natural,    0.15f, 1f),
                    new StateRow(DialogueState.Listening,    0.95f, true,  0.85f, true,  GazeAversionMode.Natural,    0.08f, 1.1f),
                    new StateRow(DialogueState.Thinking,     0.7f,  true,  0.6f,  false, GazeAversionMode.Cognitive,  0.7f,  1.3f),
                    new StateRow(DialogueState.Speaking,     1f,    true,  0.85f, true,  GazeAversionMode.None,       0f,    1f),
                    new StateRow(DialogueState.Reacting,     1f,    true,  0.9f,  true,  GazeAversionMode.None,       0f,    1.2f),
                    new StateRow(DialogueState.Interrupted,  0.95f, true,  0.9f,  true,  GazeAversionMode.None,       0f,    1.1f),
                    new StateRow(DialogueState.Settling,     0.6f,  true,  0.6f,  false, GazeAversionMode.Natural,    0.25f, 0.9f),
                },
                AmbientYawRangeDegrees = 26f,
                AmbientIntervalMin = 1.7f,
                AmbientIntervalMax = 4.6f,
                AmbientHeadFollow = 0.35f,
                AmbientRecenterBias = 0.35f,
                EnableCuriosityGlances = true,
                CuriosityGlanceIntervalMin = 7f,
                CuriosityGlanceIntervalMax = 16f,
                MicroSaccadeIntervalMean = 1.5f,
                FaceScanIntervalMean = 2.1f,
                FaceScanRadiusDegrees = 2.2f,
                BlinkIntervalMean = 4.2f,
                HeadEntryDegrees = 12f,
                EnableListeningNods = true,
                NodPitchDegrees = 3f,
                ListeningNodIntervalMin = 3.5f,
                ListeningNodIntervalMax = 8f,
                AcknowledgeNodProbability = 0.7f,
                PlanningBreakProbability = 0.7f,
                ReactionMedianSeconds = 0.28f,
                ReactionSpread = 0.35f,
                AttentionDecaySeconds = 6f,
                ReactionMinSeparationSeconds = 0.35f,
            },
            new()
            {
                Name = "Confident",
                Description = "Unbroken eye contact and an economical head — it holds you without ever leaning in. Sparse "
                              + "blinks, quiet eyes, a still idle.",
                States = new[]
                {
                    new StateRow(DialogueState.Idle,         0f,    false, 0.3f,  false, GazeAversionMode.None,       0f,    0.9f),
                    new StateRow(DialogueState.Attending,    0.96f, true,  0.8f,  true,  GazeAversionMode.None,       0f,    0.9f),
                    new StateRow(DialogueState.Listening,    1f,    true,  0.8f,  true,  GazeAversionMode.Natural,    0.03f, 0.9f),
                    new StateRow(DialogueState.Thinking,     0.88f, true,  0.6f,  false, GazeAversionMode.Cognitive,  0.4f,  1f),
                    new StateRow(DialogueState.Speaking,     1f,    true,  0.8f,  true,  GazeAversionMode.None,       0f,    0.9f),
                    new StateRow(DialogueState.Reacting,     1f,    true,  0.85f, true,  GazeAversionMode.None,       0f,    1f),
                    new StateRow(DialogueState.Interrupted,  1f,    true,  0.8f,  true,  GazeAversionMode.None,       0f,    0.9f),
                    new StateRow(DialogueState.Settling,     0.75f, true,  0.55f, false, GazeAversionMode.Natural,    0.1f,  0.85f),
                },
                AmbientYawRangeDegrees = 16f,
                AmbientIntervalMin = 2.4f,
                AmbientIntervalMax = 5.6f,
                AmbientHeadFollow = 0.32f,
                AmbientRecenterBias = 0.5f,
                EnableCuriosityGlances = false,
                CuriosityGlanceIntervalMin = 14f,
                CuriosityGlanceIntervalMax = 30f,
                MicroSaccadeIntervalMean = 2.5f,
                FaceScanIntervalMean = 3.6f,
                FaceScanRadiusDegrees = 1.7f,
                BlinkIntervalMean = 6f,
                HeadEntryDegrees = 14f,
                EnableListeningNods = true,
                NodPitchDegrees = 3f,
                ListeningNodIntervalMin = 6f,
                ListeningNodIntervalMax = 13f,
                AcknowledgeNodProbability = 0.35f,
                PlanningBreakProbability = 0.35f,
                ReactionMedianSeconds = 0.36f,
                ReactionSpread = 0.3f,
                AttentionDecaySeconds = 8f,
                ReactionMinSeparationSeconds = 0.35f,
            },
            new()
            {
                Name = "Warm",
                Description = "Animated and close: the head joins every look early, it nods often while you talk, and the "
                              + "eyes rove your face.",
                States = new[]
                {
                    new StateRow(DialogueState.Idle,         0f,    false, 0.45f, false, GazeAversionMode.None,       0f,    1.15f),
                    new StateRow(DialogueState.Attending,    0.88f, true,  0.9f,  true,  GazeAversionMode.Natural,    0.2f,  1.25f),
                    new StateRow(DialogueState.Listening,    0.93f, true,  0.92f, true,  GazeAversionMode.Natural,    0.14f, 1.3f),
                    new StateRow(DialogueState.Thinking,     0.7f,  true,  0.68f, false, GazeAversionMode.Cognitive,  0.6f,  1.35f),
                    new StateRow(DialogueState.Speaking,     0.95f, true,  0.92f, true,  GazeAversionMode.Natural,    0.12f, 1.25f),
                    new StateRow(DialogueState.Reacting,     1f,    true,  0.95f, true,  GazeAversionMode.None,       0f,    1.35f),
                    new StateRow(DialogueState.Interrupted,  0.93f, true,  0.92f, true,  GazeAversionMode.None,       0f,    1.25f),
                    new StateRow(DialogueState.Settling,     0.6f,  true,  0.7f,  false, GazeAversionMode.Natural,    0.3f,  1.05f),
                },
                AmbientYawRangeDegrees = 24f,
                AmbientIntervalMin = 1.5f,
                AmbientIntervalMax = 4f,
                AmbientHeadFollow = 0.45f,
                AmbientRecenterBias = 0.32f,
                EnableCuriosityGlances = true,
                CuriosityGlanceIntervalMin = 6f,
                CuriosityGlanceIntervalMax = 13f,
                MicroSaccadeIntervalMean = 1.1f,
                FaceScanIntervalMean = 1.6f,
                FaceScanRadiusDegrees = 2.8f,
                BlinkIntervalMean = 3.4f,
                HeadEntryDegrees = 6f,
                EnableListeningNods = true,
                NodPitchDegrees = 5f,
                ListeningNodIntervalMin = 2.5f,
                ListeningNodIntervalMax = 5.5f,
                AcknowledgeNodProbability = 0.9f,
                PlanningBreakProbability = 0.65f,
                ReactionMedianSeconds = 0.22f,
                ReactionSpread = 0.4f,
                AttentionDecaySeconds = 5f,
                ReactionMinSeparationSeconds = 0.35f,
            },
            new()
            {
                Name = "Attentive",
                Description = "A listener first: still and locked on while you speak, acknowledging almost every turn with "
                              + "a nod, then giving the floor back and holding you more lightly while it talks.",
                States = new[]
                {
                    new StateRow(DialogueState.Idle,         0f,    false, 0.4f,  false, GazeAversionMode.None,       0f,    0.95f),
                    new StateRow(DialogueState.Attending,    0.95f, true,  0.88f, true,  GazeAversionMode.Natural,    0.05f, 1f),
                    new StateRow(DialogueState.Listening,    1f,    true,  0.95f, true,  GazeAversionMode.None,       0f,    1.05f),
                    new StateRow(DialogueState.Thinking,     0.78f, true,  0.6f,  false, GazeAversionMode.Cognitive,  0.45f, 1.1f),
                    new StateRow(DialogueState.Speaking,     0.88f, true,  0.78f, false, GazeAversionMode.Natural,    0.12f, 0.95f),
                    new StateRow(DialogueState.Reacting,     1f,    true,  0.9f,  true,  GazeAversionMode.None,       0f,    1.1f),
                    new StateRow(DialogueState.Interrupted,  0.98f, true,  0.88f, true,  GazeAversionMode.None,       0f,    1f),
                    new StateRow(DialogueState.Settling,     0.72f, true,  0.6f,  false, GazeAversionMode.Natural,    0.15f, 0.9f),
                },
                AmbientYawRangeDegrees = 20f,
                AmbientIntervalMin = 2.1f,
                AmbientIntervalMax = 5f,
                AmbientHeadFollow = 0.36f,
                AmbientRecenterBias = 0.45f,
                EnableCuriosityGlances = true,
                CuriosityGlanceIntervalMin = 11f,
                CuriosityGlanceIntervalMax = 24f,
                MicroSaccadeIntervalMean = 1.9f,
                FaceScanIntervalMean = 2.7f,
                FaceScanRadiusDegrees = 2f,
                BlinkIntervalMean = 5.3f,
                HeadEntryDegrees = 9f,
                EnableListeningNods = true,
                NodPitchDegrees = 4.5f,
                ListeningNodIntervalMin = 2.2f,
                ListeningNodIntervalMax = 5f,
                AcknowledgeNodProbability = 0.95f,
                PlanningBreakProbability = 0.45f,
                ReactionMedianSeconds = 0.2f,
                ReactionSpread = 0.3f,
                AttentionDecaySeconds = 7f,
                ReactionMinSeparationSeconds = 0.35f,
            },
            new()
            {
                Name = "Shy",
                Description = "Reserved and easily overwhelmed: low commitment, frequent breaks, never turns its body, and "
                              + "a restless wandering idle.",
                States = new[]
                {
                    new StateRow(DialogueState.Idle,         0f,    false, 0.28f, false, GazeAversionMode.None,       0f,    1.15f),
                    new StateRow(DialogueState.Attending,    0.55f, true,  0.55f, false, GazeAversionMode.Natural,    0.5f,  1.25f),
                    new StateRow(DialogueState.Listening,    0.66f, true,  0.6f,  false, GazeAversionMode.Natural,    0.45f, 1.35f),
                    new StateRow(DialogueState.Thinking,     0.45f, true,  0.5f,  false, GazeAversionMode.Cognitive,  0.8f,  1.4f),
                    new StateRow(DialogueState.Speaking,     0.74f, true,  0.62f, false, GazeAversionMode.Natural,    0.4f,  1.25f),
                    new StateRow(DialogueState.Reacting,     0.85f, true,  0.7f,  true,  GazeAversionMode.Natural,    0.12f, 1.35f),
                    new StateRow(DialogueState.Interrupted,  0.78f, true,  0.68f, false, GazeAversionMode.Natural,    0.3f,  1.25f),
                    new StateRow(DialogueState.Settling,     0.42f, true,  0.48f, false, GazeAversionMode.Natural,    0.55f, 1.05f),
                },
                AmbientYawRangeDegrees = 34f,
                AmbientIntervalMin = 1.2f,
                AmbientIntervalMax = 3.4f,
                AmbientHeadFollow = 0.28f,
                AmbientRecenterBias = 0.28f,
                EnableCuriosityGlances = false,
                CuriosityGlanceIntervalMin = 10f,
                CuriosityGlanceIntervalMax = 22f,
                MicroSaccadeIntervalMean = 1f,
                FaceScanIntervalMean = 1.5f,
                FaceScanRadiusDegrees = 2.5f,
                BlinkIntervalMean = 3.2f,
                HeadEntryDegrees = 22f,
                EnableListeningNods = true,
                NodPitchDegrees = 2.5f,
                ListeningNodIntervalMin = 5f,
                ListeningNodIntervalMax = 12f,
                AcknowledgeNodProbability = 0.25f,
                PlanningBreakProbability = 0.8f,
                ReactionMedianSeconds = 0.4f,
                ReactionSpread = 0.45f,
                AttentionDecaySeconds = 4f,
                ReactionMinSeparationSeconds = 0.35f,
            },
            new()
            {
                Name = "Stoic",
                Description = "The eyes do the work and the head barely moves. Sparse blinks, quiet eyes, no idle curiosity "
                              + "— present, and giving nothing away.",
                States = new[]
                {
                    new StateRow(DialogueState.Idle,         0f,    false, 0.18f, false, GazeAversionMode.None,       0f,    0.75f),
                    new StateRow(DialogueState.Attending,    0.9f,  true,  0.36f, false, GazeAversionMode.None,       0f,    0.7f),
                    new StateRow(DialogueState.Listening,    0.93f, true,  0.38f, false, GazeAversionMode.None,       0f,    0.75f),
                    new StateRow(DialogueState.Thinking,     0.76f, true,  0.3f,  false, GazeAversionMode.Cognitive,  0.35f, 0.8f),
                    new StateRow(DialogueState.Speaking,     1f,    true,  0.42f, false, GazeAversionMode.None,       0f,    0.7f),
                    new StateRow(DialogueState.Reacting,     0.95f, true,  0.48f, false, GazeAversionMode.None,       0f,    0.8f),
                    new StateRow(DialogueState.Interrupted,  0.93f, true,  0.42f, false, GazeAversionMode.None,       0f,    0.75f),
                    new StateRow(DialogueState.Settling,     0.66f, true,  0.3f,  false, GazeAversionMode.Natural,    0.06f, 0.7f),
                },
                AmbientYawRangeDegrees = 11f,
                AmbientIntervalMin = 3f,
                AmbientIntervalMax = 7f,
                AmbientHeadFollow = 0.14f,
                AmbientRecenterBias = 0.62f,
                EnableCuriosityGlances = false,
                CuriosityGlanceIntervalMin = 18f,
                CuriosityGlanceIntervalMax = 40f,
                MicroSaccadeIntervalMean = 2.6f,
                FaceScanIntervalMean = 3.6f,
                FaceScanRadiusDegrees = 1.4f,
                BlinkIntervalMean = 6.8f,
                HeadEntryDegrees = 30f,
                EnableListeningNods = false,
                NodPitchDegrees = 2f,
                ListeningNodIntervalMin = 8f,
                ListeningNodIntervalMax = 18f,
                AcknowledgeNodProbability = 0.1f,
                PlanningBreakProbability = 0.2f,
                ReactionMedianSeconds = 0.45f,
                ReactionSpread = 0.25f,
                AttentionDecaySeconds = 9f,
                ReactionMinSeparationSeconds = 0.35f,
            },
        };

        /// <summary>
        ///     Writes <paramref name="archetype" /> into the profile's serialized fields. The
        ///     caller owns the surrounding <see cref="SerializedObject.Update" /> /
        ///     <see cref="SerializedObject.ApplyModifiedProperties" /> (so the change is a single
        ///     undoable step).
        /// </summary>
        internal static void Apply(SerializedObject serializedObject, GazeArchetype archetype)
        {
            if (serializedObject == null || archetype == null) return;

            SerializedProperty policies = GazeProfileSerializedPaths.Find(serializedObject, "statePolicies");
            policies.ClearArray();
            for (int i = 0; i < archetype.States.Length; i++)
            {
                policies.InsertArrayElementAtIndex(i);
                SerializedProperty element = policies.GetArrayElementAtIndex(i);
                StateRow row = archetype.States[i];
                element.FindPropertyRelative("State").enumValueIndex = (int)row.State;
                element.FindPropertyRelative("Engagement").floatValue = row.Engagement;
                element.FindPropertyRelative("AllowPlayerTarget").boolValue = row.AllowPlayerTarget;
                element.FindPropertyRelative("HeadContribution").floatValue = row.HeadContribution;
                element.FindPropertyRelative("AllowBodyTurn").boolValue = row.AllowBodyTurn;
                element.FindPropertyRelative("AversionMode").enumValueIndex = (int)row.AversionMode;
                element.FindPropertyRelative("AversionStrength").floatValue = row.AversionStrength;
                element.FindPropertyRelative("FixationLiveliness").floatValue = row.FixationLiveliness;
            }

            GazeProfileSerializedPaths.Find(serializedObject, "ambientYawRangeDegrees").floatValue = archetype.AmbientYawRangeDegrees;
            GazeProfileSerializedPaths.Find(serializedObject, "ambientIntervalMin").floatValue = archetype.AmbientIntervalMin;
            GazeProfileSerializedPaths.Find(serializedObject, "ambientIntervalMax").floatValue = archetype.AmbientIntervalMax;
            GazeProfileSerializedPaths.Find(serializedObject, "ambientHeadFollow").floatValue = archetype.AmbientHeadFollow;
            GazeProfileSerializedPaths.Find(serializedObject, "ambientRecenterBias").floatValue = archetype.AmbientRecenterBias;
            GazeProfileSerializedPaths.Find(serializedObject, "enableCuriosityGlances").boolValue = archetype.EnableCuriosityGlances;
            GazeProfileSerializedPaths.Find(serializedObject, "curiosityGlanceIntervalMin").floatValue = archetype.CuriosityGlanceIntervalMin;
            GazeProfileSerializedPaths.Find(serializedObject, "curiosityGlanceIntervalMax").floatValue = archetype.CuriosityGlanceIntervalMax;
            GazeProfileSerializedPaths.Find(serializedObject, "microSaccadeIntervalMean").floatValue = archetype.MicroSaccadeIntervalMean;
            GazeProfileSerializedPaths.Find(serializedObject, "faceScanIntervalMean").floatValue = archetype.FaceScanIntervalMean;
            GazeProfileSerializedPaths.Find(serializedObject, "faceScanRadiusDegrees").floatValue = archetype.FaceScanRadiusDegrees;
            GazeProfileSerializedPaths.Find(serializedObject, "blinkIntervalMean").floatValue = archetype.BlinkIntervalMean;
            GazeProfileSerializedPaths.Find(serializedObject, "headEntryDegrees").floatValue = archetype.HeadEntryDegrees;
            GazeProfileSerializedPaths.Find(serializedObject, "enableListeningNods").boolValue = archetype.EnableListeningNods;
            GazeProfileSerializedPaths.Find(serializedObject, "nodPitchDegrees").floatValue = archetype.NodPitchDegrees;
            GazeProfileSerializedPaths.Find(serializedObject, "listeningNodIntervalMin").floatValue = archetype.ListeningNodIntervalMin;
            GazeProfileSerializedPaths.Find(serializedObject, "listeningNodIntervalMax").floatValue = archetype.ListeningNodIntervalMax;
            GazeProfileSerializedPaths.Find(serializedObject, "acknowledgeNodProbability").floatValue = archetype.AcknowledgeNodProbability;
            GazeProfileSerializedPaths.Find(serializedObject, "planningBreakProbability").floatValue = archetype.PlanningBreakProbability;
            GazeProfileSerializedPaths.Find(serializedObject, "reactionMedianSeconds").floatValue = archetype.ReactionMedianSeconds;
            GazeProfileSerializedPaths.Find(serializedObject, "reactionSpread").floatValue = archetype.ReactionSpread;
            GazeProfileSerializedPaths.Find(serializedObject, "attentionDecaySeconds").floatValue = archetype.AttentionDecaySeconds;
            GazeProfileSerializedPaths.Find(serializedObject, "reactionMinSeparationSeconds").floatValue = archetype.ReactionMinSeparationSeconds;
        }
    }
}
