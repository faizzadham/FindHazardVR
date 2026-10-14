using System.Collections.Generic;
using System.Reflection;
using Convai.Runtime.Actions;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using UnityEngine;

namespace Convai.Tests.PlayMode.Actions
{
    /// <summary>
    ///     Builds the scene a Convai character actually needs, so PlayMode tests run against a
    ///     supported setup instead of muting the SDK's complaint about an unsupported one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists rather than <c>LogAssert.ignoreFailingMessages</c>.</b> A bare
    ///         <c>ConvaiCharacter</c> with no <c>ConvaiManager</c> logs a setup error, and the quick
    ///         way past it is to tell the test framework to ignore failing log messages. That works
    ///         and it costs more than it looks: with it on, <em>every</em> unexpected error stops
    ///         failing tests, so a suite written to prove something does not throw quietly stops
    ///         proving anything. Building the scene properly keeps Unity's log assertion available as
    ///         a real signal.
    ///     </para>
    ///     <para>
    ///         What the editor's <c>GameObject &gt; Convai &gt; Setup Required Components</c> does is
    ///         exactly this: one object carrying <see cref="ConvaiManager" /> and
    ///         <see cref="ConvaiRoomManager" />. That command lives in the editor assembly, which the
    ///         PlayMode test assembly cannot reference, so it is mirrored here — deliberately as the
    ///         same two components, so the fixture stays a supported setup rather than a private one.
    ///     </para>
    /// </remarks>
    internal sealed class ConvaiActionPlayModeScene
    {
        /// <summary>
        ///     A real character id, so nothing in the pipeline treats this character as unconfigured.
        /// </summary>
        /// <remarks>
        ///     Nothing here connects to Convai — no test in this folder enters a conversation — but a
        ///     character with a blank id is a different object from a character with one, and a
        ///     fixture that differs from a real scene in a way nobody wrote down is how a passing
        ///     suite stops meaning anything.
        /// </remarks>
        internal const string CharacterId = "51a5cb20-01e7-11f1-a307-42010a7be027";

        private readonly List<GameObject> _spawned = new();

        /// <summary>The manager object, created on first use.</summary>
        private GameObject _manager;

        /// <summary>
        ///     Ensures the scene carries a manager, and returns it.
        /// </summary>
        internal GameObject EnsureManager()
        {
            if (_manager != null)
                return _manager;

            _manager = Track(new GameObject("[Convai Manager]"));
            _manager.AddComponent<ConvaiManager>();

            // A conversation needs somebody to have it with, and the Terminal scene carries one
            // alongside the manager and the character.
            Track(new GameObject("[Convai Player]")).AddComponent<ConvaiPlayer>();

            // The room manager is *not* added here. `ConvaiManager.Awake` adds one itself, and
            // `Awake` has already run by the line above — adding a component to an active object
            // runs it immediately. So the manager the scene has is the manager already present.
            //
            // Adding a second one was this fixture's own defect, and it did more than fail to
            // silence anything: `ConvaiRoomManager` carries no `[DisallowMultipleComponent]`, and
            // its `Awake` responds to a duplicate instance by destroying its GameObject. The extra
            // component therefore tore down the whole `[Convai Manager]` object at the end of the
            // frame, while the manager's own copy — still enabled — went on to run `Start` and log
            // the error the disabling was meant to prevent.
            ConvaiRoomManager room = _manager.GetComponent<ConvaiRoomManager>();
            if (room == null)
                throw new System.InvalidOperationException(
                    "ConvaiManager no longer adds a ConvaiRoomManager during Awake. This fixture "
                    + "relies on that to be the scene a character actually runs in; add one here "
                    + "and say why the manager stopped doing it.");

            // Switched off, and that is the whole trick.
            //
            // `ConvaiRoomManager.Start` validates a whole session before it connects — a player,
            // owned characters, credentials, a resolved conversation target — and reports the first
            // thing missing. None of that is optional and `ConnectOnStart` does not skip it: the
            // flag decides whether to connect *after* validating, so turning it off changes nothing
            // here. A scene with no credentials and no backend can never satisfy that chain, and
            // filling it in one error at a time is chasing a conversation no test in this folder
            // wants to have.
            //
            // Disabling before the first frame means `Start` never runs, and disabling a component
            // is an ordinary thing a project does, not a test-only escape: nothing is faked,
            // nothing is muted, and an unexpected error from any other source still fails the test.
            // The alternative was expecting the error message, which passes by recognising a string
            // and would go on passing if the message changed meaning.
            room.enabled = false;

            return _manager;
        }

        /// <summary>
        ///     Builds a character with Convai Actions, on a supported scene.
        /// </summary>
        /// <param name="authoredObjects">Scene Knowledge entries the character starts with.</param>
        /// <remarks>
        ///     The object is created inactive and activated last, so <c>Awake</c> runs once the
        ///     manager exists and the character id is set — the order a real scene loads in, and the
        ///     order that keeps the setup diagnostics quiet because there is nothing to diagnose.
        /// </remarks>
        internal ConvaiCharacter Character(params ConvaiActionObjectDefinition[] authoredObjects)
        {
            EnsureManager();

            GameObject host = Track(new GameObject("convai-character"));
            host.SetActive(false);

            ConvaiActionConfigSource source = host.AddComponent<ConvaiActionConfigSource>();
            if (authoredObjects != null && authoredObjects.Length > 0)
                source.ReplaceObjects(new List<ConvaiActionObjectDefinition>(authoredObjects));

            ConvaiCharacter character = host.AddComponent<ConvaiCharacter>();
            SetCharacterId(character, CharacterId);

            host.SetActive(true);
            return character;
        }

        /// <summary>
        ///     Builds a dispatcher on its own character, the way the component is used in a scene.
        /// </summary>
        internal ConvaiActionDispatcher Dispatcher(string name)
        {
            ConvaiCharacter character = Character();
            character.gameObject.name = name;
            return character.gameObject.AddComponent<ConvaiActionDispatcher>();
        }

        /// <summary>Tracks an object for teardown.</summary>
        internal GameObject Track(GameObject go)
        {
            _spawned.Add(go);
            return go;
        }

        /// <summary>Creates a tracked, positioned plain object.</summary>
        internal GameObject At(string name, Vector3 position)
        {
            GameObject go = Track(new GameObject(name));
            go.transform.position = position;
            return go;
        }

        /// <summary>
        ///     Destroys everything this fixture created, including the manager.
        /// </summary>
        /// <remarks>
        ///     <see cref="ConvaiManager" /> marks itself <c>DontDestroyOnLoad</c>, so leaving one
        ///     behind would leak into the next test — and the second manager would then be the
        ///     duplicate the SDK correctly complains about, turning this fixture into the problem it
        ///     was written to remove.
        /// </remarks>
        internal void Dispose()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
            _manager = null;
        }

        /// <summary>
        ///     Sets the serialized character id, which has no setter because nothing at runtime is
        ///     meant to change it.
        /// </summary>
        /// <remarks>
        ///     Reflection here rather than a new internal setter on the component: adding API to the
        ///     shipped type so a test can reach it would put a hole in the product to serve the
        ///     fixture. The field is serialized, so a scene sets it the same way — through
        ///     serialization — and this is the test's equivalent of that.
        /// </remarks>
        private static void SetCharacterId(ConvaiCharacter character, string characterId) =>
            SetSerializedField(character, "_characterId", characterId);

        /// <summary>
        ///     Writes a serialized field the way a scene would, for settings the SDK deliberately
        ///     exposes only to the inspector.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Reflection rather than new internal setters on the shipped components: adding API
        ///         so a test can reach it would put a hole in the product to serve the fixture. These
        ///         fields are serialized, so a real scene sets them through serialization, and this
        ///         is the test's equivalent of that — not a back door into behaviour a project
        ///         cannot also configure.
        ///     </para>
        ///     <para>
        ///         Auto-property backing fields are named <c>&lt;Name&gt;k__BackingField</c>, which
        ///         is a compiler detail and therefore worth failing loudly about rather than silently
        ///         skipping: a rename here turns a configured fixture into an unconfigured one, and
        ///         the tests would go on passing while testing something else.
        ///     </para>
        /// </remarks>
        private static void SetSerializedField(Component component, string fieldName, object value)
        {
            FieldInfo field = component.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

            // System.MissingFieldException is qualified rather than reached through a
            // `using System;`, which would make the bare `Object.DestroyImmediate` in Dispose
            // ambiguous between System.Object and UnityEngine.Object.
            if (field == null)
                throw new System.MissingFieldException(
                    $"{component.GetType().Name}.{fieldName} is gone or renamed. The PlayMode "
                    + "fixture sets it the way serialization does; update the name here rather than "
                    + "adding a setter to the shipped component.");

            field.SetValue(component, value);
        }
    }
}
