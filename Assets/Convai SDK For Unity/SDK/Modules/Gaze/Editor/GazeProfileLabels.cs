using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Convai.Modules.Gaze.Editor
{
    /// <summary>
    ///     Plain-English display labels for the Gaze Profile fields whose serialized names are
    ///     oculomotor research vocabulary. Unity derives a field's inspector label from its
    ///     identifier, which is fine for <c>playerMaxDistance</c> and useless for
    ///     <c>saccadeDurationPerDegree</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This closes a standing violation, not a nicety.</b> The project's naming rule
    ///         permits clinical jargon — Saccade, VOR, OMR, Oculomotor — <em>in internal and
    ///         tooling types only</em>. Every term renamed below was appearing on a customer-facing
    ///         ScriptableObject inspector, where a non-technical user was asked to reason about
    ///         "Saccade Duration Per Degree" and "Synthetic Interpupillary Distance".
    ///     </para>
    ///     <para>
    ///         Display only. Serialized names, public API and asset data are untouched, so this
    ///         changes nothing but what the user reads — which is the point. Modelled directly on
    ///         <c>BodyAnimationConfigLabels</c>, including the per-field <see cref="GUIContent" />
    ///         cache: inspectors redraw constantly and building a GUIContent per field per repaint
    ///         is the waste the editor style guide forbids.
    ///     </para>
    ///     <para>
    ///         Fields not listed here keep Unity's derived name, because it is already plain. A
    ///         field is renamed only where its identifier fails to describe what the setting does
    ///         to someone who has never read the module's source.
    ///     </para>
    /// </remarks>
    internal static class GazeProfileLabels
    {
        /// <summary>Serialized field name → the label a user should actually see.</summary>
        private static readonly Dictionary<string, string> Labels = new()
        {
            // ── Eyes: the densest cluster of research vocabulary in the whole SDK ──────────
            { "saccadeMinDurationSeconds", "Fastest Eye Movement" },
            { "saccadeDurationPerDegree", "Eye Time Per Degree" },
            { "saccadeDeadzoneDegrees", "Ignore Moves Smaller Than" },
            { "saccadeReactionSeconds", "Reaction Delay" },
            { "microSaccadeIntervalMean", "Average Eye Flick Gap" },
            { "microSaccadeIntervalJitter", "Eye Flick Timing Variation" },
            { "microSaccadeAmplitudeDegrees", "Eye Flick Size" },
            { "fixationDriftDegrees", "Eye Wander Amount" },
            { "fixationDriftFrequency", "Eye Wander Speed" },
            { "orbitRecenteringStrength", "Eyes Re-Centre With Head" },
            { "eyeSoftLimitFraction", "Eye Strain Starts At" },
            { "eyeTrackingSharpness", "Tracking Tightness" },
            { "catchUpErrorDegrees", "Catch Up When Behind By" },
            { "pursuitLeadSeconds", "Anticipate Moving Targets" },
            { "eyeActuationMode", "How The Eyes Are Driven" },
            { "eyeMaxYawDegrees", "Furthest The Eyes Turn" },
            { "eyeMaxPitchUpDegrees", "Furthest The Eyes Look Up" },
            { "eyeMaxPitchDownDegrees", "Furthest The Eyes Look Down" },

            // "Vergence" and "interpupillary" are optometry terms on a game-engine inspector.
            { "enableVergence", "Cross Eyes Up Close" },
            { "vergenceMinDistance", "Closest Focus Distance" },
            { "maxConvergenceDegrees", "Maximum Eye Crossing" },
            { "syntheticInterpupillaryDistance", "Eye Spacing (No Eye Bones)" },

            // ── Face scan ──────────────────────────────────────────────────────────────────
            { "enableFaceScan", "Looks Around Your Face" },
            { "faceScanIntervalMean", "Average Face Glance Gap" },
            { "faceScanIntervalJitter", "Face Glance Timing Variation" },
            { "faceScanRadiusDegrees", "Face Scan Spread" },
            { "enableListenerMouthBias", "Watches Mouth As You Talk" },
            { "listenerMouthBiasStrength", "Mouth Watching Strength" },

            // ── Head & torso: "recruitment", "share" and "sharpness" are solver words ──────
            { "headFollowSeconds", "Head Follow Time" },
            { "torsoFollowSeconds", "Chest Follow Time" },
            { "headStabilization", "Keep Head Level" },
            { "maxHeadAngularSpeed", "Head Speed Limit" },
            { "maxTorsoAngularSpeed", "Chest Speed Limit" },
            { "maxHeadYawDegrees", "Furthest The Head Turns" },
            { "maxHeadPitchDegrees", "Furthest The Head Tilts" },
            { "neckShare", "How Much The Neck Bends" },
            { "chainFollowThrough", "Follow Through" },
            { "enableTorsoRecruitment", "Chest Joins In On Big Looks" },
            { "maxTorsoYawDegrees", "Furthest The Chest Turns" },
            { "maxTorsoPitchDegrees", "Furthest The Chest Leans" },

            // ── Gaze shift ladder: "entry", "onset" and "residual" are solver words ───────
            { "headEntryDegrees", "Head Joins In Above" },
            { "torsoEntryDegrees", "Chest Joins In Above" },
            { "feetEntryDegrees", "Feet Join In Above" },
            { "headOnsetSeconds", "Head Starts After Eyes By" },
            { "torsoOnsetSeconds", "Chest Starts After Eyes By" },
            { "feetOnsetSeconds", "Feet Start After Eyes By" },
            { "headTurnBaseSeconds", "Head Turn Time" },
            { "headTurnSecondsPerDegree", "Added Time Per Degree" },
            { "torsoTurnBaseSeconds", "Chest Turn Time" },
            { "torsoTurnSecondsPerDegree", "Chest Added Time Per Degree" },
            { "movementSkew", "Movement Front-Loading" },
            { "shiftTriggerDegrees", "New Look Starts Above" },
            { "idleDriftTempoScale", "Idle Drift Slowdown" },
            { "eyeComfortDegrees", "Eye Comfort Range" },
            { "headComfortYawDegrees", "Neck Comfort Range" },

            // ── Targeting: "commitment", "interest", "relevance" are arbiter internals ─────
            { "playerMaxDistance", "Stops Noticing You Beyond" },
            { "playerFullRelevanceDistance", "Fully Interested Within" },
            { "playerLineOfSight", "Won't Look Through Walls" },
            { "playerObstructionMask", "What Counts As A Wall" },
            { "targetTeleportThreshold", "Treat Jumps As Cuts Above" },
            { "commitmentAcquireSeconds", "Time To Lock On" },
            { "commitmentReleaseSeconds", "Time To Let Go" },
            { "targetLossHoldSeconds", "Lingers After Losing Sight" },
            { "interestDecayPerSecond", "How Fast It Gets Bored" },
            { "interestRecoveryPerSecond", "How Fast Interest Returns" },
            { "maxContinuousHoldSeconds", "Longest Unbroken Look" },
            { "interestBreakThreshold", "Looks Away Below Interest" },
            { "enableTargetLossSearch", "Searches When It Loses You" },
            { "targetLossSearchMaxSeconds", "Gives Up Searching After" },
            { "enableLookAtActionTargets", "Looks At Action Targets" },

            // ── State policies ─────────────────────────────────────────────────────────────
            { "statePolicies", "Behaviour Per Conversation State" },
            { "policyBlendSpeed", "Mood Change Speed" },

            // ── Idle life ──────────────────────────────────────────────────────────────────
            { "enableAmbientExploration", "Looks Around When Idle" },
            { "ambientYawRangeDegrees", "Idle Look Range Sideways" },
            { "ambientPitchUpDegrees", "Idle Look Range Up" },
            { "ambientPitchDownDegrees", "Idle Look Range Down" },
            { "ambientIntervalMin", "Shortest Idle Look Gap" },
            { "ambientIntervalMax", "Longest Idle Look Gap" },
            { "ambientHeadFollow", "Head Follows Idle Looks" },
            { "ambientRecenterBias", "Prefers Straight Ahead" },
            { "enableCuriosityGlances", "Glances At You While Idle" },
            { "curiosityGlanceIntervalMin", "Shortest Glance Gap" },
            { "curiosityGlanceIntervalMax", "Longest Glance Gap" },
            { "curiosityGlanceDuration", "How Long A Glance Lasts" },
            { "curiosityRespondsToAttention", "Glances Back When Watched" },

            // ── While walking ──────────────────────────────────────────────────────────────
            { "enableTravelGaze", "Watches Where It Is Going" },
            { "enableDestinationGlances", "Glances At Its Destination" },
            { "travelPathPriority", "Path Priority" },
            { "pathLookAheadMinMeters", "Look-Ahead At Walking Pace" },
            { "pathLookAheadMaxMeters", "Look-Ahead At Full Pace" },
            { "travelEngageSeconds", "Look-Ahead Settle Time" },
            { "travelGlanceIntervalMin", "Shortest Place Glance Gap" },
            { "travelGlanceIntervalMax", "Longest Place Glance Gap" },
            { "companionGlanceIntervalMin", "Shortest Person Glance Gap" },
            { "companionGlanceIntervalMax", "Longest Person Glance Gap" },
            { "travelGlanceHoldSeconds", "How Long Each Look Lasts" },
            { "arrivalSettleEyeDropDegrees", "Eye Drop On Arrival" },
            { "arrivalSettleSeconds", "How Long The Settle Takes" },
            { "travelGlanceConversationScale", "Glances More While Talking" },
            { "arrivalApproachMeters", "Starts Settling This Far Out" },
            { "arrivalReleaseMeters", "Stops Watching Path Within" },
            { "travelHeadContributionScale", "Head Follows The Path" },

            // ── Blink & lids ───────────────────────────────────────────────────────────────
            { "enableBlink", "Blinks" },
            { "blinkIntervalMean", "Average Gap Between Blinks" },
            { "blinkIntervalJitter", "Blink Timing Variation" },
            { "blinkCloseSeconds", "Lids Close In" },
            { "blinkOpenSeconds", "Lids Open In" },
            { "blinkRefractorySeconds", "Minimum Gap Between Blinks" },
            { "gazeShiftBlinkThresholdDegrees", "Look-Away Blink Above" },
            { "gazeShiftBlinkProbability", "Look-Away Blink Chance" },
            { "enableEyelidFollow", "Eyelids Follow The Eyes" },
            { "eyelidFollowStrength", "How Much The Eyelids Follow" },
            { "enableBlinkClustering", "Blinks More At Pauses" },
            { "blinkClusterRateMultiplier", "How Much More It Blinks" },

            // ── Body turn ──────────────────────────────────────────────────────────────────
            { "enableBodyTurn", "Turns Body For Far Targets" },
            { "bodyTurnCompletionToleranceDegrees", "Counts The Turn Done Within" },
            { "bodyTurnHeadRelief", "Neck Relaxes During A Turn" },
            { "proceduralTurnSpeed", "Fallback Turn Speed" },

            // ── Conversational gestures & rhythm ───────────────────────────────────────────
            { "enableListeningNods", "Nods While Listening" },
            { "nodPitchDegrees", "How Deep A Nod Is" },
            { "nodDurationSeconds", "How Long A Nod Lasts" },
            { "listeningNodIntervalMin", "Shortest Gap Between Nods" },
            { "listeningNodIntervalMax", "Longest Gap Between Nods" },
            { "acknowledgeNodProbability", "Acknowledging Nod Chance" },
            { "enableInterruptionReaction", "Reacts When Interrupted" },
            { "interruptionReactionIntensity", "How Strong The Reaction Is" },
            { "enableTurnTakingGaze", "Conversational Eye Rhythm" },
            { "planningBreakProbability", "Thinking Look-Away Rate" },
            { "enableYieldBlink", "Blinks When Handing Over" },
            { "enableYieldHeadDip", "Head Dip When Handing Over" },

            // ── Emotion & proxemics ────────────────────────────────────────────────────────
            { "enableEmotionModulation", "Gaze Changes With Emotion" },
            { "emotionModifiers", "Per-Emotion Adjustments" },
            { "attentionEngagement", "How Closely It Listens" },
            { "attentionHeadContribution", "How Much The Head Turns" },
            { "attentionAllowBodyTurn", "Turns Body To The Speaker" },
            { "attentionMaxAngleDegrees", "Widest Angle It Will Turn" },
            { "attentionMaxDistance", "How Far Away It Still Notices" },
            { "reactionMedianSeconds", "Typical Pause Before Turning" },
            { "reactionSpread", "How Much Reactions Vary" },
            { "reactionMinSeparationSeconds", "Stagger Between Listeners" },
            { "attentionDecaySeconds", "Attention Fade Time" },
            { "attentionHoldSeconds", "Keeps The Floor For" },
            { "attentionInterruptionSeconds", "Time To Take The Floor" },
            { "attentionLookAway", "How Much Its Gaze Wanders" },
            { "enableAudienceChecks", "Glances At The Addressee" },
            { "audienceCheckIntervalMin", "Shortest Gap Between Them" },
            { "audienceCheckIntervalMax", "Longest Gap Between Them" },
            { "audienceCheckDuration", "How Long Each Lasts" },
            { "enableProxemicRegulation", "Softens When You Get Close" },
            { "proxemicCloseDistanceMeters", "\"Close\" Starts At" },
            { "proxemicIntensity", "How Much It Softens" },

            // ── Performance: "LOD" and "cognition Hz" are engine words ─────────────────────
            { "enableGazeLod", "Save Performance In Crowds" },
            { "lodFarDistance", "Crowd Distance" },
            { "lodFarCognitionHz", "Crowd Update Rate" },
            { "skipWhenInvisible", "Pause When Off-Screen" },

            // ── Diagnostics: "verbosity" and "firehose" are developer words ────────────────
            { "traceVerbosity", "Diagnostic Detail" },
            { "firehoseHz", "Per-Frame Report Rate" }
        };

        /// <summary>Cached <see cref="GUIContent" /> per field — never build one per repaint.</summary>
        private static readonly Dictionary<string, GUIContent> Cache = new();

        /// <summary>
        ///     The label to draw <paramref name="property" /> with: the plain-English override when
        ///     one exists, otherwise Unity's own derived name. The property's authored tooltip is
        ///     preserved either way — it carries the explanation the label cannot.
        /// </summary>
        internal static GUIContent For(SerializedProperty property)
        {
            if (property == null) return GUIContent.none;

            string fieldName = property.name;
            if (Cache.TryGetValue(fieldName, out GUIContent cached)) return cached;

            string label = Labels.TryGetValue(fieldName, out string plain) ? plain : property.displayName;
            var content = new GUIContent(label, property.tooltip);
            Cache[fieldName] = content;
            return content;
        }

        /// <summary>Whether this field carries a plain-English override (used by the naming guard test).</summary>
        internal static bool HasOverride(string fieldName) => Labels.ContainsKey(fieldName);

        /// <summary>Every field name this table renames, for the naming guard test.</summary>
        internal static IEnumerable<string> OverriddenFields => Labels.Keys;
    }
}
