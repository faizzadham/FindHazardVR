using Convai.Domain.Logging;
using Convai.Modules.BodyAnimation.Components;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using UnityEngine;

namespace Convai.Sample.Behaviors
{
    /// <summary>
    ///     Recipe sample for a "social approach": when the Character becomes ready to converse,
    ///     it walks up to the player (stopping at a proxemics-friendly distance) and turns to
    ///     face them. Composed entirely from public API — <see cref="ConvaiNavMeshLocomotion.MoveTo" />
    ///     and <see cref="ConvaiBodyAnimationController.FaceTowards" /> — so it ships as sample
    ///     policy rather than a module capability.
    /// </summary>
    /// <remarks>
    ///     Place this alongside a <see cref="ConvaiCharacterBehaviorBase" />-hosting Character that
    ///     also carries <see cref="ConvaiBodyAnimationController" /> and
    ///     <see cref="ConvaiNavMeshLocomotion" />. If either is missing, or no
    ///     <see cref="ConvaiPlayer" /> can be found, the behaviour logs once and stays inert.
    /// </remarks>
    [AddComponentMenu("Convai/Samples/Approach On Conversation")]
    public sealed class ConvaiApproachOnConversation : ConvaiCharacterBehaviorBase
    {
        [SerializeField]
        [Tooltip("Master switch — disable to stop approaching on conversation start without removing the component.")]
        private bool _approachOnConversationStart = true;

        [SerializeField]
        [Tooltip("When enabled, the Character only turns to face the player and never walks over.")]
        private bool _faceOnlyNoWalk;

        [SerializeField, Min(0f)]
        [Tooltip("Stop this far (meters) from the player instead of walking into them.")]
        private float _stoppingDistance = 1.8f;

        [SerializeField]
        [Tooltip("Body animation controller driving this character. When empty, resolved from this hierarchy.")]
        private ConvaiBodyAnimationController _bodyAnimation;

        [SerializeField]
        [Tooltip("NavMesh locomotion used to walk toward the player. When empty, resolved from this hierarchy.")]
        private ConvaiNavMeshLocomotion _locomotion;

        [SerializeField]
        [Tooltip("Player to approach. When empty, resolved from the first ConvaiPlayer found in the scene.")]
        private ConvaiPlayer _player;

        private bool _approaching;
        private bool _loggedInert;

        /// <inheritdoc />
        public override void OnCharacterReady(IConvaiCharacterAgent agent)
        {
            if (!_approachOnConversationStart) return;
            if (!ResolveDependencies()) return;

            Vector3 toPlayer = _player.transform.position - transform.position;
            toPlayer.y = 0f;
            float distance = toPlayer.magnitude;

            if (_faceOnlyNoWalk || _locomotion == null || distance <= _stoppingDistance)
            {
                FacePlayer();
                return;
            }

            Vector3 destination = _player.transform.position - toPlayer.normalized * _stoppingDistance;
            _approaching = _locomotion.MoveTo(destination);
            if (_approaching)
                _locomotion.MoveEnded += HandleMoveEnded;
            else
                FacePlayer();
        }

        /// <inheritdoc />
        public override void OnCharacterShutdown(IConvaiCharacterAgent agent) => CancelApproach();

        private void OnDisable() => CancelApproach();

        private void HandleMoveEnded(bool arrived)
        {
            if (_locomotion != null)
                _locomotion.MoveEnded -= HandleMoveEnded;
            _approaching = false;

            if (arrived)
                FacePlayer();
        }

        /// <summary>Cancels an in-flight approach cleanly (e.g. the conversation ends mid-walk).</summary>
        private void CancelApproach()
        {
            if (!_approaching) return;

            if (_locomotion != null)
            {
                _locomotion.MoveEnded -= HandleMoveEnded;
                _locomotion.Stop();
            }

            _approaching = false;
        }

        private void FacePlayer()
        {
            if (_bodyAnimation == null || _player == null) return;

            Vector3 direction = _player.transform.position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f) return;

            _bodyAnimation.FaceTowards(direction, "ApproachOnConversation");
        }

        private bool ResolveDependencies()
        {
            if (_bodyAnimation == null)
            {
                _bodyAnimation = GetComponentInParent<ConvaiBodyAnimationController>(true)
                                  ?? GetComponentInChildren<ConvaiBodyAnimationController>(true);
            }

            if (_locomotion == null)
            {
                _locomotion = GetComponentInParent<ConvaiNavMeshLocomotion>(true)
                              ?? GetComponentInChildren<ConvaiNavMeshLocomotion>(true);
            }

            if (_player == null)
                _player = FindAnyObjectByType<ConvaiPlayer>();

            if (_bodyAnimation != null && _player != null) return true;

            if (!_loggedInert)
            {
                _loggedInert = true;
                ConvaiLogger.Warning(
                    $"[ConvaiApproachOnConversation] '{name}' missing " +
                    $"{(_bodyAnimation == null ? "ConvaiBodyAnimationController" : "a ConvaiPlayer in the scene")} " +
                    "— staying inert.",
                    LogCategory.Animation);
            }

            return false;
        }
    }
}
