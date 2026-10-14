using System;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Components;
using UnityEngine;

namespace Convai.Sample.Runtime
{
    /// <summary>
    ///     Sample component that instantiates a Convai character prefab and starts its conversation at runtime.
    /// </summary>
    /// <remarks>
    ///     Spawning is all it takes: the SDK notices a character that appears during play, injects
    ///     it, and — when the room is already connected — seats it in the live conversation without
    ///     a reconnect. The only thing left for this component is the case where no room is
    ///     connected yet, where it starts the conversation the same way a scene character would.
    /// </remarks>
    public sealed class RuntimeCharacterSpawner : MonoBehaviour
    {
        private const int InjectionTimeoutFrames = 120;

        [SerializeField] private ConvaiCharacter characterPrefab;
        [SerializeField] private Transform spawnPoint;

        private ConvaiCharacter _runtimeCharacter;
        private bool _isSpawning;

        /// <summary>
        ///     Assign this method to a UI Button OnClick event.
        /// </summary>
        public async void SpawnAndConnectCharacter()
        {
            if (_isSpawning) return;

            if (characterPrefab == null)
            {
                Debug.LogError(
                    $"[{nameof(RuntimeCharacterSpawner)}] Assign a ConvaiCharacter prefab before spawning.",
                    this);
                return;
            }

            _isSpawning = true;
            try
            {
                if (_runtimeCharacter == null)
                {
                    if (ConvaiManager.ActiveManager == null)
                    {
                        throw new InvalidOperationException(
                            "Cannot spawn a runtime character because no active ConvaiManager exists.");
                    }

                    Vector3 position = spawnPoint != null ? spawnPoint.position : transform.position;
                    Quaternion rotation = spawnPoint != null ? spawnPoint.rotation : transform.rotation;
                    _runtimeCharacter = Instantiate(characterPrefab, position, rotation);

                    // The manager notices the spawned character on its own and injects it a frame
                    // later; in a connected room it also joins the live roster (watch the Console
                    // for "'<name>' joined the room without reconnecting."). Waiting here is only
                    // about what this method does next.
                    await WaitForInjectionAsync(_runtimeCharacter);
                    await Task.Yield();
                }

                // In a connected room the character is already being seated, and its session state
                // mirrors the room — nothing to start. These branches only matter when no room is
                // connected yet, or the previous connection failed.
                switch (_runtimeCharacter.SessionState)
                {
                    case SessionState.Disconnected:
                        await _runtimeCharacter.StartConversationAsync();
                        break;

                    case SessionState.Error:
                        await _runtimeCharacter.ResetAndRetryAsync();
                        break;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                _isSpawning = false;
            }
        }

        private static async Task WaitForInjectionAsync(ConvaiCharacter character)
        {
            for (int frame = 0; frame < InjectionTimeoutFrames; frame++)
            {
                if (character != null && character.IsInjected) return;
                await Task.Yield();
            }

            throw new InvalidOperationException(
                "The runtime character was not injected. Ensure an active ConvaiManager is in the scene.");
        }
    }
}
