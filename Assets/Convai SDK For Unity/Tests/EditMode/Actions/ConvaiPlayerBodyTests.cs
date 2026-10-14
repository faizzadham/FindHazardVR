using System;
using Convai.Runtime.Actions;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     EditMode coverage for <see cref="ConvaiPlayerBody" />, which answers "where is the player"
    ///     for every movement and attention behavior — and, since it became public, for Action
    ///     Behaviors written outside the package too.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiPlayerBodyTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null &&
                    gameObject.name.StartsWith("ConvaiPlayerBodyTests_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Resolve_ConvaiPlayerPresent_ResolvesToPlayerTransform()
        {
            GameObject playerGo = NewGameObject("Player_Present");
            Convai.Runtime.Components.ConvaiPlayer player =
                playerGo.AddComponent<Convai.Runtime.Components.ConvaiPlayer>();

            Assert.That(ConvaiPlayerBody.Resolve(), Is.EqualTo(player.transform));
        }

        /// <summary>
        ///     A first-person rig moves a capsule inside itself and leaves the prefab root at the
        ///     spawn point. Resolving to the root reports where the player <i>started</i>.
        /// </summary>
        /// <remarks>
        ///     Measured twice now. The SDK's own behaviors have always had this right; a hand-written
        ///     copy of the rule in this repository's demo left the step out, and the character who
        ///     led the visitor to the gallery then stood waiting for somebody who was already beside
        ///     her — for twelve seconds, and then went on ahead. Nothing failed, so nothing said so.
        /// </remarks>
        [Test]
        public void Resolve_ControllerOnAChildCapsule_ResolvesToTheBodyThatMoves()
        {
            GameObject root = NewGameObject("Rig_Root");
            root.AddComponent<Convai.Runtime.Components.ConvaiPlayer>();

            GameObject capsule = NewGameObject("Rig_Capsule");
            capsule.transform.SetParent(root.transform);
            CharacterController controller = capsule.AddComponent<CharacterController>();

            Transform resolved = ConvaiPlayerBody.Resolve();

            Assert.That(resolved, Is.EqualTo(controller.transform),
                "The capsule is what the controller displaces; the root never leaves the spawn point.");
            Assert.That(resolved, Is.Not.EqualTo(root.transform));
        }

        /// <summary>
        ///     The common wiring must not change: a player whose own object carries the controller
        ///     resolves to itself, because the search matches the object it starts from first.
        /// </summary>
        [Test]
        public void Resolve_ControllerOnThePlayerItself_StillResolvesToThePlayer()
        {
            GameObject playerGo = NewGameObject("Player_WithController");
            Convai.Runtime.Components.ConvaiPlayer player =
                playerGo.AddComponent<Convai.Runtime.Components.ConvaiPlayer>();
            playerGo.AddComponent<CharacterController>();

            Assert.That(ConvaiPlayerBody.Resolve(), Is.EqualTo(player.transform));
        }

        /// <summary>
        ///     A rig moved by physics rather than a controller is still the body that moves.
        /// </summary>
        [Test]
        public void Resolve_RigidbodyOnAChild_ResolvesToTheBodyThatMoves()
        {
            GameObject root = NewGameObject("Physics_Root");
            root.AddComponent<Convai.Runtime.Components.ConvaiPlayer>();

            GameObject body = NewGameObject("Physics_Body");
            body.transform.SetParent(root.transform);
            Rigidbody rigidbody = body.AddComponent<Rigidbody>();

            Assert.That(ConvaiPlayerBody.Resolve(), Is.EqualTo(rigidbody.transform));
        }

        /// <summary>
        ///     The floor-height form exists because cameras sit at head height, and a character that
        ///     measures to one stands slightly too far away in every first-person scene.
        /// </summary>
        [Test]
        public void TryResolveFloorPosition_AnswersAtTheHeightAsked()
        {
            GameObject playerGo = NewGameObject("Player_Floor");
            playerGo.AddComponent<Convai.Runtime.Components.ConvaiPlayer>();
            playerGo.transform.position = new Vector3(3f, 1.7f, -4f);

            Assert.That(ConvaiPlayerBody.TryResolveFloorPosition(0.25f, out Vector3 position), Is.True);
            Assert.That(position, Is.EqualTo(new Vector3(3f, 0.25f, -4f)));
        }

        private static GameObject NewGameObject(string suffix) => new($"ConvaiPlayerBodyTests_{suffix}");
    }
}
