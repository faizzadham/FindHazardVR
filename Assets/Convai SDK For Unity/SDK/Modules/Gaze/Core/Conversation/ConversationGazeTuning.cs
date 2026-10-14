using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>
    ///     Everything <see cref="ConversationGazeDirector" /> is allowed to be opinionated about,
    ///     built once per tick by the wiring layer from the character's profile.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is no parameterless constructor (C# 9 forbids one on a struct), so a
    ///         <c>default</c> value is all zeros — an attention that decays instantly and a
    ///         character that never reacts. Always name what a caller cares about.
    ///     </para>
    /// </remarks>
    internal readonly struct ConversationGazeTuning
    {
        /// <summary>
        ///     The smallest gap that reads as two people reacting separately rather than as one
        ///     cue moving several heads, when nothing more specific is supplied. Kept as a default
        ///     for callers (mostly tests) that do not care about the profile's own value.
        /// </summary>
        internal const float DefaultMinSeparationSeconds = 0.35f;

        /// <summary>Engagement a listener commits to whoever it is attending, before the attention scale.</summary>
        public readonly float Engagement;

        /// <summary>How much of the look the head carries, before the eyes-only and glance scales.</summary>
        public readonly float HeadContribution;

        /// <summary>Whether attention may bring the body round, which is what makes a speaker behind the character reachable.</summary>
        public readonly bool AllowBodyTurn;

        /// <summary>Widest angle off the character's forward a speaker may sit at while the body may not turn.</summary>
        public readonly float MaxYawDegrees;

        /// <summary>Furthest a speaker may stand and still be somebody this character follows. Zero disables the limit.</summary>
        public readonly float MaxDistance;

        /// <summary>Median reaction latency to a speech onset, in seconds — half of them are shorter.</summary>
        public readonly float ReactionMedianSeconds;

        /// <summary>Spread of the reaction latency, as the standard deviation of its logarithm.</summary>
        public readonly float ReactionSigma;

        /// <summary>
        ///     Time constant of attention decay. Attention loses <c>1 − e⁻¹</c> of itself over
        ///     this long, so nothing in this director is a timeout: a listener drifts off a
        ///     conversation the way people do rather than at a moment the viewer can learn.
        /// </summary>
        public readonly float DecaySeconds;

        /// <summary>
        ///     Shortest gap enforced between two listeners' reactions to the same speech onset, so
        ///     the room never turns as one. Authored on the profile rather than fixed, so a denser
        ///     cast can be told to spread out further.
        /// </summary>
        public readonly float MinSeparationSeconds;

        /// <summary>How much natural aversion a listener runs while attending. Zero leaves the state's own aversion alone.</summary>
        public readonly float AversionStrength;

        /// <summary>
        ///     Whether the character ever looks away from the person it is attending to check
        ///     somebody else in the room: a listener checking the person being spoken to, and a
        ///     speaker checking the rest of its audience. One switch, because they are one
        ///     behaviour seen from the two ends of a turn.
        /// </summary>
        public readonly bool EnableAudienceChecks;

        /// <summary>Shortest wait between audience checks.</summary>
        public readonly float AudienceCheckIntervalMin;

        /// <summary>Longest wait between audience checks.</summary>
        public readonly float AudienceCheckIntervalMax;

        /// <summary>How long one audience check lasts before the look returns to the speaker.</summary>
        public readonly float AudienceCheckDuration;

        /// <summary>
        ///     How long somebody has to keep talking over the floor holder before the room hands
        ///     them the turn — the room's own rule, mirrored here because the eyes move before
        ///     the floor does and half of this is where they move.
        /// </summary>
        public readonly float InterruptionSeconds;

        /// <summary>
        ///     Whether the character exchanges glances with the people around it while nobody is
        ///     talking. Off leaves a quiet room to its ambient life.
        /// </summary>
        public readonly bool EnableSocialIdle;

        /// <summary>Shortest wait between two idle glances at somebody in the room.</summary>
        public readonly float SocialIdleIntervalMin;

        /// <summary>Longest wait between two idle glances at somebody in the room.</summary>
        public readonly float SocialIdleIntervalMax;

        /// <summary>How long one idle glance rests on the person it lands on.</summary>
        public readonly float SocialIdleDuration;

        /// <summary>
        ///     Engagement one idle glance carries. Stated rather than derived: the Idle policy row
        ///     commits to nothing at all, so a glance that inherited it would never land.
        /// </summary>
        public readonly float SocialIdleEngagement;

        /// <summary>
        ///     How far the head can stay turned before the character wants its feet — the
        ///     profile's own comfort angle, read here because a beat that has already decided
        ///     the body may not turn has to know how far it can reach without one.
        /// </summary>
        public readonly float HeadComfortYawDegrees;

        /// <summary>
        ///     How far the eyes may rest from centre. The other half of a body-less reach: the
        ///     head takes what it comfortably can and the eyes are allowed this much of the
        ///     rest, and anything past the two is a look this character cannot make politely.
        /// </summary>
        public readonly float EyeComfortDegrees;

        /// <summary>
        ///     Widest angle a look that will never recruit the body may be aimed at: as far as a
        ///     comfortable neck reaches, plus the band the eyes may rest in.
        /// </summary>
        /// <remarks>
        ///     A glance is defined by what it costs — eyes, a little neck, and no body. Past this
        ///     angle the same beat costs a head turn the character has already refused to support
        ///     with its chest and feet, so what actually happens is that the eyes are parked at
        ///     the corner of the socket for the length of the glance. Refusing the candidate is
        ///     the only honest answer: there is no such thing as a glance at somebody standing
        ///     there, and pretending otherwise is what produced the sideways stare.
        /// </remarks>
        public float GlanceReachDegrees => HeadComfortYawDegrees + EyeComfortDegrees;

        /// <summary>Creates a tuning. Everything is clamped to a value the director can survive.</summary>
        /// <param name="engagement">Engagement committed to an attended speaker.</param>
        /// <param name="headContribution">Head participation in the look.</param>
        /// <param name="allowBodyTurn">Whether attention may turn the body.</param>
        /// <param name="maxYawDegrees">Widest reachable angle while the body may not turn.</param>
        /// <param name="maxDistance">Attention distance limit; zero disables it.</param>
        /// <param name="reactionMedianSeconds">Median reaction latency.</param>
        /// <param name="reactionSigma">Log-normal spread of the reaction latency.</param>
        /// <param name="decaySeconds">Attention decay time constant.</param>
        /// <param name="minSeparationSeconds">Shortest gap enforced between two listeners' reactions to the same onset.</param>
        /// <param name="aversionStrength">Natural aversion while attending.</param>
        /// <param name="enableAudienceChecks">Whether audience checks run at all.</param>
        /// <param name="audienceCheckIntervalMin">Shortest wait between audience checks.</param>
        /// <param name="audienceCheckIntervalMax">Longest wait between audience checks.</param>
        /// <param name="audienceCheckDuration">Length of one audience check.</param>
        /// <param name="interruptionSeconds">How long a challenger must sustain to take the floor.</param>
        /// <param name="enableSocialIdle">Whether idle glances between people run at all.</param>
        /// <param name="socialIdleIntervalMin">Shortest wait between idle glances.</param>
        /// <param name="socialIdleIntervalMax">Longest wait between idle glances.</param>
        /// <param name="socialIdleDuration">Length of one idle glance.</param>
        /// <param name="socialIdleEngagement">Engagement one idle glance carries.</param>
        /// <param name="headComfortYawDegrees">Head yaw the neck holds without asking for the feet.</param>
        /// <param name="eyeComfortDegrees">How far the eyes may rest from centre.</param>
        public ConversationGazeTuning(
            float engagement = 0.6f,
            float headContribution = 0.7f,
            bool allowBodyTurn = false,
            float maxYawDegrees = 100f,
            float maxDistance = 10f,
            float reactionMedianSeconds = 0.28f,
            float reactionSigma = 0.35f,
            float decaySeconds = 6f,
            float minSeparationSeconds = DefaultMinSeparationSeconds,
            float aversionStrength = 0.2f,
            bool enableAudienceChecks = false,
            float audienceCheckIntervalMin = 9f,
            float audienceCheckIntervalMax = 22f,
            float audienceCheckDuration = 0.6f,
            float interruptionSeconds = 0.6f,
            bool enableSocialIdle = false,
            float socialIdleIntervalMin = 5f,
            float socialIdleIntervalMax = 12f,
            float socialIdleDuration = 1.4f,
            float socialIdleEngagement = 0.5f,
            float headComfortYawDegrees = 35f,
            float eyeComfortDegrees = 14f)
        {
            Engagement = Mathf.Clamp01(engagement);
            HeadContribution = Mathf.Clamp01(headContribution);
            AllowBodyTurn = allowBodyTurn;
            MaxYawDegrees = Mathf.Max(0f, maxYawDegrees);
            MaxDistance = Mathf.Max(0f, maxDistance);
            ReactionMedianSeconds = Mathf.Max(0f, reactionMedianSeconds);
            ReactionSigma = Mathf.Max(0f, reactionSigma);
            // A zero time constant would divide by nothing on the first decay step; a hundredth
            // of a second is still instantaneous to a viewer and is arithmetic that terminates.
            DecaySeconds = Mathf.Max(0.01f, decaySeconds);
            MinSeparationSeconds = Mathf.Max(0f, minSeparationSeconds);
            AversionStrength = Mathf.Clamp01(aversionStrength);
            EnableAudienceChecks = enableAudienceChecks;
            AudienceCheckIntervalMin = Mathf.Max(0f, audienceCheckIntervalMin);
            AudienceCheckIntervalMax = Mathf.Max(AudienceCheckIntervalMin, audienceCheckIntervalMax);
            AudienceCheckDuration = Mathf.Max(0f, audienceCheckDuration);
            InterruptionSeconds = Mathf.Max(0f, interruptionSeconds);
            EnableSocialIdle = enableSocialIdle;
            SocialIdleIntervalMin = Mathf.Max(0f, socialIdleIntervalMin);
            SocialIdleIntervalMax = Mathf.Max(SocialIdleIntervalMin, socialIdleIntervalMax);
            SocialIdleDuration = Mathf.Max(0f, socialIdleDuration);
            SocialIdleEngagement = Mathf.Clamp01(socialIdleEngagement);
            HeadComfortYawDegrees = Mathf.Max(0f, headComfortYawDegrees);
            EyeComfortDegrees = Mathf.Max(0f, eyeComfortDegrees);
        }
    }
}
