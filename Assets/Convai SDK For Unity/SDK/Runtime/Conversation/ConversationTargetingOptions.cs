using System;
using UnityEngine;

namespace Convai.Runtime.Conversation
{
    /// <summary>
    ///     How the SDK decides which character the player is talking to when a room holds more than
    ///     one.
    /// </summary>
    public enum ConversationTargetingMode
    {
        /// <summary>
        ///     The character the player is looking at. Scored from the view direction, so it needs no
        ///     colliders, no layers, and no input wiring — a mouse, a gamepad, a touch screen and a
        ///     head-mounted display all drive it the same way.
        /// </summary>
        LookAt = 0,

        /// <summary>
        ///     The nearest character, regardless of where the player is looking. Suited to top-down
        ///     and third-person games where the camera is not the player's gaze.
        /// </summary>
        Proximity = 1,

        /// <summary>
        ///     Nothing changes the target except the game. Use with
        ///     <see cref="Components.ConvaiManager.TalkTo" /> for scripted or menu-driven conversations.
        /// </summary>
        Manual = 2
    }

    /// <summary>
    ///     Tuning for automatic conversation targeting. Every value has a working default; a scene
    ///     that changes none of them still behaves correctly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The numbers are separated by what they do, because they answer different questions and
    ///         are tuned at different times. <see cref="MaxDistance" /> and <see cref="MaxAngle" />
    ///         decide who is <i>eligible</i> — the shape of the space a character has to be in before
    ///         the player counts as addressing them. <see cref="SwitchMargin" /> and
    ///         <see cref="SwitchDelaySeconds" /> decide how <i>willingly</i> an eligible challenger
    ///         takes the conversation over, and exist entirely to stop the target flickering.
    ///     </para>
    ///     <para>
    ///         Without the second pair, two characters standing near each other trade the conversation
    ///         back and forth on sub-degree camera movement, and sweeping the view across a room hands
    ///         the conversation to everyone it passes. Both are the kind of fault that is obvious in
    ///         play and invisible in code.
    ///     </para>
    /// </remarks>
    [Serializable]
    public sealed class ConversationTargetingOptions
    {
        [SerializeField]
        [Tooltip("How the character the player is talking to is chosen.")]
        private ConversationTargetingMode _mode = ConversationTargetingMode.LookAt;

        [SerializeField]
        [Min(0f)]
        [Tooltip("How far away a character can be and still be addressed, in metres.")]
        private float _maxDistance = 30f;

        [SerializeField]
        [Range(1f, 180f)]
        [Tooltip("How far from the centre of view a character can be and still be addressed, in degrees.")]
        private float _maxAngle = 35f;

        [SerializeField]
        [Range(0f, 90f)]
        [Tooltip("How much better a different character must look before the conversation moves to them, in degrees.")]
        private float _switchMargin = 10f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("How long a different character must stay the best choice before the conversation moves to them.")]
        private float _switchDelaySeconds = 0.2f;

        /// <summary>How the character the player is talking to is chosen.</summary>
        public ConversationTargetingMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        /// <summary>How far away a character can be and still be addressed, in metres.</summary>
        /// <remarks>
        ///     Deliberately generous. Under <see cref="ConversationTargetingMode.LookAt" /> this is a
        ///     guard against addressing somebody in the next room, not the rule that decides who is
        ///     being spoken to — looking across a hall at somebody and speaking to them is ordinary,
        ///     and a modest range turns that into a conversation stuck on whoever was closest last.
        ///     The failure is quiet, because the target holding is also what correct behaviour looks
        ///     like from outside.
        /// </remarks>
        public float MaxDistance
        {
            get => _maxDistance;
            set => _maxDistance = Mathf.Max(0f, value);
        }

        /// <summary>
        ///     How far from the centre of view a character can be and still be addressed, in degrees.
        ///     Measured from the view direction, so 35 means a 70-degree cone.
        /// </summary>
        public float MaxAngle
        {
            get => _maxAngle;
            set => _maxAngle = Mathf.Clamp(value, 1f, 180f);
        }

        /// <summary>
        ///     How much better a different character must score before the conversation moves to them.
        ///     Zero lets the target change on the smallest camera movement.
        /// </summary>
        /// <remarks>
        ///     Measured in degrees, so it applies to <see cref="ConversationTargetingMode.LookAt" />
        ///     only. Under <see cref="ConversationTargetingMode.Proximity" /> two characters the same
        ///     distance away are separated by <see cref="SwitchDelaySeconds" /> instead — a margin in
        ///     degrees would mean nothing to a rule measured in metres, and reusing the number would
        ///     make one setting quietly change meaning with the mode.
        /// </remarks>
        public float SwitchMargin
        {
            get => _switchMargin;
            set => _switchMargin = Mathf.Clamp(value, 0f, 90f);
        }

        /// <summary>
        ///     How long a different character must stay the best choice before the conversation moves
        ///     to them. Zero switches the moment the view passes over somebody.
        /// </summary>
        public float SwitchDelaySeconds
        {
            get => _switchDelaySeconds;
            set => _switchDelaySeconds = Mathf.Max(0f, value);
        }

        /// <summary>Clamps every value into its supported range.</summary>
        public void Validate()
        {
            _maxDistance = Mathf.Max(0f, _maxDistance);
            _maxAngle = Mathf.Clamp(_maxAngle, 1f, 180f);
            _switchMargin = Mathf.Clamp(_switchMargin, 0f, 90f);
            _switchDelaySeconds = Mathf.Max(0f, _switchDelaySeconds);
        }

        /// <summary>The shipped defaults.</summary>
        public static ConversationTargetingOptions CreateDefault() => new();
    }
}
