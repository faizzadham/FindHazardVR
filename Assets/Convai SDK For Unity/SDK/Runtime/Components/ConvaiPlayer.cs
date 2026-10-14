using System;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Logging;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime.Logging;
using Convai.Runtime.Presentation.Services;
using Convai.Shared.Abstractions;
using UnityEngine;
using UnityEngine.Serialization;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Runtime.Components
{
    /// <summary>
    ///     Main player component that implements IConvaiPlayerAgent.
    ///     Provides player identity for the Convai conversation system.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This component is the player-side equivalent of <see cref="ConvaiCharacter" />.
    ///         It provides player identity (name, playerId, color) and text messaging capability.
    ///         Microphone input is managed separately by <see cref="Convai.Runtime.Adapters.Networking.ConvaiRoomManager" />.
    ///         Player behavior can be extended using <see cref="ConvaiPlayerBehaviorBase" />.
    ///     </para>
    ///     <para>
    ///         <b>Important - PlayerId vs Server Speaker ID:</b>
    ///     </para>
    ///     <para>
    ///         The <c>PlayerId</c> property on this component is a <b>local display identifier</b> used for
    ///         transcript UI attribution. It is NOT the same as the server-generated <c>speaker_id</c> used
    ///         for Long-Term Memory (LTM) and interaction tracking.
    ///     </para>
    ///     <para>
    ///         When the backend includes it for diagnostics, the server-generated speaker ID may be visible via
    ///         <see cref="Convai.Infrastructure.Networking.IConvaiRoomController.ResolvedSpeakerId" />
    ///         after connection is established.
    ///     </para>
    /// </remarks>
    [AddComponentMenu("Convai/Convai Player")]
    public class ConvaiPlayer : MonoBehaviour, IConvaiPlayerAgent,
        IInjectable<IConvaiPlayerDependencies>
    {
        #region Events

        /// <summary>Raised when a text message is sent.</summary>
        public event Action<string> OnTextMessageSent;

        #endregion

        #region Dependency Injection

        /// <inheritdoc cref="IInjectable{TDependencies}.InjectDependencies" />
        public void InjectDependencies(IConvaiPlayerDependencies dependencies)
        {
            _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

            // Register with player input service if available
            if (PlayerInputService != null)
                PlayerInputService.SetPlayer(this);

            if (Logger?.IsEnabled(LogLevel.Debug) == true)
                Logger?.Debug($"[{_playerName}] Dependencies injected (typed bundle)");
        }

        #endregion

        #region Dependencies

        /// <summary>
        ///     Typed dependency bundle (new pattern). When set, provides all dependencies.
        /// </summary>
        private IConvaiPlayerDependencies _deps;

        // Accessor properties for dependencies
        private IPlayerInputService PlayerInputService => _deps?.PlayerInputService;
        private ILogger Logger => _deps?.Logger;

        #endregion

        #region Serialized Fields

        // No [Header]: Unity draws one wherever the field is drawn, and these three are drawn by
        // ConvaiPlayerEditor inside a section already called Identity. "Player Configuration" was a
        // second, bolder caption saying the same thing one line lower down.
        [SerializeField]
        [Tooltip("Display name for the player, used in transcripts and debug logs.")]
        private string _playerName = "Player";

        [SerializeField]
        [FormerlySerializedAs("_speakerId")]
        [FormerlySerializedAs("_actorId")]
        [Tooltip("Optional local player identifier for transcript UI attribution. " +
                 "If empty, PlayerName is used. This is NOT the server-generated speaker ID used for Long-Term Memory.")]
        private string _playerId = "";

        [SerializeField] private Color _nameTagColor = Color.green;

        private string _runtimeDisplayName = string.Empty;
        private bool _hasRuntimeDisplayName;

        #endregion

        #region IConvaiPlayerAgent Implementation

        /// <summary>Display name for the player.</summary>
        public string PlayerName => _hasRuntimeDisplayName ? _runtimeDisplayName : _playerName;

        /// <summary>
        ///     Local player identifier for transcript UI attribution.
        /// </summary>
        /// <remarks>
        ///     This is a local display identifier, NOT the server-generated speaker_id used for LTM.
        ///     If the backend exposes a resolved speaker ID for diagnostics, it can be read from
        ///     <see cref="Convai.Infrastructure.Networking.ConvaiRoomController.ResolvedSpeakerId" />.
        /// </remarks>
        public string PlayerId => string.IsNullOrWhiteSpace(_playerId) ? PlayerName : _playerId.Trim();

        /// <summary>Name tag color for transcript display.</summary>
        public Color NameTagColor => _nameTagColor;

        /// <summary>
        ///     Sends a text message to the character the player is addressing.
        /// </summary>
        /// <remarks>
        ///     Refused when the character cannot hear it yet — see
        ///     <see cref="ConvaiManager.ConversationAvailability" />. Use
        ///     <see cref="TrySendTextMessage" /> when the caller wants to know rather than read the
        ///     Console.
        /// </remarks>
        /// <param name="message">The text message to send.</param>
        public void SendTextMessage(string message)
        {
            if (!TrySendTextMessage(message, out string reason))
                ConvaiLogger.Warning($"Message not sent: {reason}", LogCategory.SDK);
        }

        /// <summary>
        ///     Sends a text message, and says why when it cannot.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Text used to be dropped in four separate places — no injection, no player, no
        ///         subscriber, no room — each logging into the Console and returning nothing, so the
        ///         caller could not tell a sent message from a swallowed one. A fifth drop happened
        ///         on the service, when the addressed character had not been announced yet, and that
        ///         one logged nothing at all.
        ///     </para>
        ///     <para>
        ///         Gating here rather than only in the shipped chat UI is deliberate: most projects
        ///         build their own input, and a rule that lives in one prefab protects nobody else.
        ///     </para>
        /// </remarks>
        /// <param name="message">The text message to send.</param>
        /// <param name="reason">Why the message was refused, or <c>null</c> when it was sent.</param>
        /// <returns><c>true</c> when the message was handed to the room.</returns>
        public bool TrySendTextMessage(string message, out string reason)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                reason = "the message is empty.";
                return false;
            }

            if (OnTextMessageSent == null)
            {
                reason = "nothing is listening for player messages. Is ConvaiManager in the scene " +
                         "and active?";
                return false;
            }

            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager != null)
            {
                ConvaiConversationAvailability availability = manager.ConversationAvailability;
                if (!availability.CanAcceptPlayerInput())
                {
                    reason = DescribeUnavailability(availability, manager.AddressedCharacter);
                    return false;
                }
            }

            OnTextMessageSent.Invoke(message);
            reason = null;
            return true;
        }

        /// <summary>
        ///     Says why a message cannot be sent, in words a project can show a player.
        /// </summary>
        private static string DescribeUnavailability(
            ConvaiConversationAvailability availability,
            ConvaiCharacter addressed)
        {
            string who = addressed != null ? $"'{addressed.CharacterName}'" : "the character";
            return availability switch
            {
                ConvaiConversationAvailability.NoCharacter =>
                    "no character is being addressed. Add a Convai Character to the scene, or point " +
                    "the conversation at one with ConvaiManager.TalkTo.",
                ConvaiConversationAvailability.Offline =>
                    "the room is not connected. Start a conversation first.",
                ConvaiConversationAvailability.Connecting =>
                    "the room is still connecting.",
                ConvaiConversationAvailability.Preparing =>
                    $"{who} has not finished joining the conversation. It answers as soon as the " +
                    "service confirms it — a moment, usually.",
                ConvaiConversationAvailability.Unavailable =>
                    $"{who} is not available in this conversation.",
                _ => $"{who} cannot accept input right now ({availability})."
            };
        }

        #endregion

        #region Public Methods

        /// <summary>
        ///     Configures the player identity.
        /// </summary>
        /// <param name="playerName">Display name for the player.</param>
        /// <param name="playerId">Optional local player ID (defaults to playerName).</param>
        public void Configure(string playerName, string playerId = null)
        {
            _playerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
            _playerId = string.IsNullOrWhiteSpace(playerId) ? _playerName : playerId.Trim();
            _runtimeDisplayName = string.Empty;
            _hasRuntimeDisplayName = false;
        }

        /// <summary>
        ///     Updates the effective runtime display name used by transcripts and player identity.
        /// </summary>
        /// <param name="displayName">Display name override. Null/empty clears the runtime override.</param>
        public void SetRuntimeDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                _runtimeDisplayName = string.Empty;
                _hasRuntimeDisplayName = false;
                return;
            }

            _runtimeDisplayName = displayName.Trim();
            _hasRuntimeDisplayName = true;
        }

        #endregion
    }
}
