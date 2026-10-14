using System.Collections;
using System.Reflection;
using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Runtime
{
    /// <summary>
    ///     A ConvaiCharacter that appears in the scene during play must be noticed, injected and
    ///     owned without anybody calling anything.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the promise the Multi-Character sample README makes — "enable a character
    ///         GameObject during play and it is in the conversation" — and the part of it that can
    ///         be proven without a backend: discovery, injection and ownership. Seating the
    ///         character in a live room builds on exactly this state, so a character that never
    ///         reaches <c>IsInjected</c> can never join anything.
    ///     </para>
    ///     <para>
    ///         These run in Play Mode because the whole defect lives in Unity lifecycle order:
    ///         <c>OnEnable</c> firing after the manager's startup composition already ran. Edit Mode
    ///         never runs <c>OnEnable</c>, so an Edit Mode test here would pass with or without the
    ///         fix.
    ///     </para>
    /// </remarks>
    public sealed class RuntimeSpawnedCharacterTests
    {
        /// <summary>A real character id, copied from the Actions PlayMode fixture's reasoning.</summary>
        private const string CharacterId = "51a5cb20-01e7-11f1-a307-42010a7be027";

        private const int RuntimeStartupTimeoutFrames = 120;
        private const int LateRegistrationTimeoutFrames = 10;

        private GameObject _managerObject;
        private GameObject _playerObject;
        private GameObject _characterObject;

        [TearDown]
        public void TearDown()
        {
            // The manager marks itself DontDestroyOnLoad; leaving one behind makes the next test's
            // manager the duplicate the SDK correctly complains about.
            if (_characterObject != null) Object.DestroyImmediate(_characterObject);
            if (_playerObject != null) Object.DestroyImmediate(_playerObject);
            if (_managerObject != null) Object.DestroyImmediate(_managerObject);
            _characterObject = null;
            _playerObject = null;
            _managerObject = null;
        }

        [UnityTest]
        public IEnumerator ACharacterSpawnedDuringPlayIsInjectedAndOwned()
        {
            ConvaiManager manager = null;
            yield return StartManagerScene(created => manager = created);

            ConvaiCharacter character = SpawnCharacter();

            int frame = 0;
            while (!character.IsInjected && frame++ < LateRegistrationTimeoutFrames)
                yield return null;

            Assert.That(
                character.IsInjected,
                Is.True,
                "A character spawned while the runtime is live must be injected without anybody "
                + "calling SetExplicitCharacters or RefreshReferences.");
            Assert.That(
                manager.Characters,
                Does.Contain(character),
                "The spawned character must be owned, or room composition can never seat it.");
        }

        [UnityTest]
        public IEnumerator ADisabledSpawnedCharacterStaysOwnedSoItCanRejoinLater()
        {
            ConvaiManager manager = null;
            yield return StartManagerScene(created => manager = created);

            ConvaiCharacter character = SpawnCharacter();
            int frame = 0;
            while (!character.IsInjected && frame++ < LateRegistrationTimeoutFrames)
                yield return null;
            Assert.That(character.IsInjected, Is.True, "Spawn must register before disable can be tested.");

            character.gameObject.SetActive(false);
            yield return null;
            yield return null;

            // Disabling gives up the character's seat in a live room, not its ownership — the
            // sample promises that enabling it again puts it back in the conversation.
            Assert.That(manager.Characters, Does.Contain(character));
        }

        /// <summary>
        ///     A character that starts the scene disabled and is enabled during play must be the
        ///     resolved conversation target before its own auto-connect runs.
        /// </summary>
        /// <remarks>
        ///     This is the case that failed in the editor. Ownership injects inactive characters at
        ///     startup, so enabling one runs <c>InitializeAfterInjection</c> immediately and starts
        ///     auto-connect — while the character is still absent from the connectable roster,
        ///     because that is decided by activity and nothing had re-read it. The connection then
        ///     composed a room with no conversation target and was refused with "connection
        ///     preparation failed", naming the character that had just appeared.
        /// </remarks>
        [UnityTest]
        public IEnumerator EnablingADisabledCharacterResolvesItAsTheTargetBeforeAutoConnectRuns()
        {
            ConvaiManager manager = null;
            yield return StartManagerScene(created => manager = created);

            _characterObject = new GameObject("disabled-character");
            _characterObject.SetActive(false);
            ConvaiCharacter character = _characterObject.AddComponent<ConvaiCharacter>();
            SetSerializedField(character, "_characterId", CharacterId);

            // Owned while disabled — ownership includes inactive characters — but not connectable,
            // so nothing resolves as the conversation target yet.
            yield return null;
            Assert.That(
                manager.ActiveConversationCharacter,
                Is.Null,
                "A disabled character must not be the conversation target; if it already is, this "
                + "test cannot show that enabling it is what resolves it.");

            _characterObject.SetActive(true);

            int frame = 0;
            while (manager.ActiveConversationCharacter != character &&
                   frame++ < LateRegistrationTimeoutFrames)
                yield return null;

            Assert.That(
                manager.ActiveConversationCharacter,
                Is.EqualTo(character),
                "Enabling a character must make it the resolved conversation target. Until it is, "
                + "composing a room finds no target and refuses the connection.");
            Assert.That(
                manager.HasPendingLateCharacterRefresh,
                Is.False,
                "The refresh must have settled — auto-connect waits on exactly this flag, so a flag "
                + "that never clears would hold every connection for its full wait.");
        }

        /// <summary>
        ///     A message the character cannot receive is refused with a reason, not swallowed.
        /// </summary>
        /// <remarks>
        ///     Text used to die in four separate places, each logging and returning nothing, so a
        ///     caller could not tell a sent message from a lost one — and the shipped chat field
        ///     stayed usable throughout, which is what made a player believe they were connected.
        /// </remarks>
        [UnityTest]
        public IEnumerator AMessageThatCannotBeDeliveredIsRefusedWithAReason()
        {
            ConvaiManager manager = null;
            yield return StartManagerScene(created => manager = created);

            ConvaiCharacter character = SpawnCharacter();
            int frame = 0;
            while (!character.IsInjected && frame++ < LateRegistrationTimeoutFrames)
                yield return null;

            // The room manager is disabled in this fixture, so nothing is connected — the state a
            // player is in before they press anything, and the one the old field lied about.
            Assert.That(
                manager.ConversationAvailability.CanAcceptPlayerInput(),
                Is.False,
                "Without a connected room nothing can receive a message; if this says otherwise "
                + "the gate is measuring the wrong thing.");

            ConvaiPlayer player = _playerObject.GetComponent<ConvaiPlayer>();
            bool sent = player.TrySendTextMessage("hey", out string reason);

            Assert.That(sent, Is.False, "The message must be refused rather than accepted and lost.");
            Assert.That(
                reason,
                Is.Not.Null.And.Not.Empty,
                "A refusal without a reason is the silent drop this replaced.");
        }

        private IEnumerator StartManagerScene(System.Action<ConvaiManager> managerReady)
        {
            _managerObject = new GameObject("[Convai Manager]");
            ConvaiManager manager = _managerObject.AddComponent<ConvaiManager>();

            // ConvaiManager.Awake added the room manager; disabling it before its Start keeps this
            // scene from validating a session no test here wants to have — the same trick, for the
            // same reasons, as the Actions PlayMode fixture.
            var room = _managerObject.GetComponent<Convai.Runtime.Adapters.Networking.ConvaiRoomManager>();
            Assert.That(room, Is.Not.Null, "ConvaiManager no longer adds a ConvaiRoomManager during Awake.");
            room.enabled = false;

            _playerObject = new GameObject("[Convai Player]");
            _playerObject.AddComponent<ConvaiPlayer>();

            int frame = 0;
            while (manager.ConvaiRuntime?.State != RuntimeState.Running &&
                   frame++ < RuntimeStartupTimeoutFrames)
                yield return null;

            Assert.That(
                manager.ConvaiRuntime?.State,
                Is.EqualTo(RuntimeState.Running),
                "The runtime never reached Running; the late-registration path under test only "
                + "exists on a live runtime.");
            managerReady(manager);
        }

        /// <summary>
        ///     A character that is disabled and enabled again must still have its voice.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The registration that tells the audio track manager which AudioSource belongs to
        ///         a character is torn down in <c>OnDisable</c>. Restoring it used to happen only as
        ///         a side effect of resolving dependencies for the first time, and that resolution
        ///         returns early once they are cached — so a second enable never put it back. The
        ///         character then rejoined its room, answered in text, and stayed silent, with lip
        ///         sync waiting for audio that was never going to play.
        ///     </para>
        ///     <para>
        ///         Play Mode because the whole defect is the <c>OnEnable</c>/<c>OnDisable</c> pair.
        ///         Edit Mode runs neither, so an Edit Mode test here would pass either way.
        ///     </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator ACharacterKeepsItsAudioSourceRegistrationAcrossADisable()
        {
            ConvaiManager manager = null;
            yield return StartManagerScene(created => manager = created);

            _characterObject = new GameObject("voiced-character");
            _characterObject.SetActive(false);
            ConvaiCharacter character = _characterObject.AddComponent<ConvaiCharacter>();
            SetSerializedField(character, "_characterId", CharacterId);
            _characterObject.AddComponent<AudioSource>();
            _characterObject.AddComponent<ConvaiAudioOutput>();
            _characterObject.SetActive(true);

            int frame = 0;
            while (!character.IsInjected && frame++ < LateRegistrationTimeoutFrames)
                yield return null;
            Assert.That(character.IsInjected, Is.True, "Injection must happen before this can mean anything.");

            Assert.That(
                manager.TryGetAgentRegistry(out IAgentRegistry registry),
                Is.True,
                "Without the registry there is nothing to assert against.");
            Assert.That(
                registry.TryGetAudioSource(CharacterId, out AudioSource _),
                Is.True,
                "The first enable must register the AudioSource, or the rest of this proves nothing.");

            _characterObject.SetActive(false);
            yield return null;
            Assert.That(
                registry.TryGetAudioSource(CharacterId, out AudioSource _),
                Is.False,
                "Disabling deliberately gives the registration up; if it did not, re-enabling could "
                + "not regress and this test would be vacuous.");

            _characterObject.SetActive(true);
            yield return null;

            Assert.That(
                registry.TryGetAudioSource(CharacterId, out AudioSource restored),
                Is.True,
                "Enabling a character again must put its AudioSource back, or the audio track "
                + "manager has nowhere to play its voice and the character is silent.");
            Assert.That(restored, Is.Not.Null);
        }

        private ConvaiCharacter SpawnCharacter()
        {
            // Created inactive and activated last, the order Instantiate gives a prefab: Awake and
            // OnEnable run only once the character id is in place.
            _characterObject = new GameObject("spawned-character");
            _characterObject.SetActive(false);
            ConvaiCharacter character = _characterObject.AddComponent<ConvaiCharacter>();
            SetSerializedField(character, "_characterId", CharacterId);
            _characterObject.SetActive(true);
            return character;
        }

        /// <summary>
        ///     Writes a serialized field the way a scene would; reflection rather than a test-only
        ///     setter on the shipped component (see the Actions PlayMode fixture for the full case).
        /// </summary>
        private static void SetSerializedField(Component component, string fieldName, object value)
        {
            FieldInfo field = component.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new System.MissingFieldException(
                    $"{component.GetType().Name}.{fieldName} is gone or renamed; update this fixture.");
            field.SetValue(component, value);
        }
    }
}
