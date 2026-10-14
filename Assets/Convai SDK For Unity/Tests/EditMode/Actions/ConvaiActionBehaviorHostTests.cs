using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers the seam that lets an Action Behavior sit either on the Convai Character or on a
    ///     child object that holds the character's behaviors:
    ///     <see cref="ConvaiActionExecutorBase" />'s character transform.
    /// </summary>
    /// <remarks>
    ///     Both layouts are supported and neither is a migration target, so the contract worth
    ///     pinning is that a behavior resolves the <em>character's</em> world-space transform from
    ///     wherever it lives — including from a child that has been moved off the origin, which is
    ///     the case that shipped broken in Return To Start.
    /// </remarks>
    [TestFixture]
    public class ConvaiActionBehaviorHostTests
    {
        private const string NamePrefix = "ConvaiActionBehaviorHostTests_";

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith(NamePrefix, StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BehaviorOnTheCharacter_ResolvesTheCharacterTransform()
        {
            GameObject character = NewCharacter(new Vector3(3f, 0f, 7f));
            var behavior = character.AddComponent<ProbeActionBehavior>();

            Assert.That(behavior.ObservedCharacterTransform, Is.SameAs(character.transform));
            Assert.That(behavior.ObservedCharacterTransform.position, Is.EqualTo(new Vector3(3f, 0f, 7f)));
        }

        [Test]
        public void BehaviorOnAChildHost_ResolvesTheCharacterTransform_NotItsOwn()
        {
            GameObject character = NewCharacter(new Vector3(3f, 0f, 7f));
            var host = new GameObject($"{NamePrefix}Action Behaviors");
            host.transform.SetParent(character.transform, false);

            var behavior = host.AddComponent<ProbeActionBehavior>();

            Assert.That(behavior.ObservedCharacterTransform, Is.SameAs(character.transform));
        }

        /// <summary>
        ///     The regression that motivated the seam: a host nudged off the origin must not shift
        ///     where the behavior thinks the character is. Before the fix this returned the host's
        ///     position, and Return To Start walked the character to it.
        /// </summary>
        [Test]
        public void BehaviorOnAnOffsetChildHost_StillReportsTheCharacterPosition()
        {
            GameObject character = NewCharacter(new Vector3(3f, 0f, 7f));
            var host = new GameObject($"{NamePrefix}Action Behaviors");
            host.transform.SetParent(character.transform, false);
            host.transform.localPosition = new Vector3(5f, 0f, 5f);
            host.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

            var behavior = host.AddComponent<ProbeActionBehavior>();

            Assert.That(behavior.ObservedCharacterTransform.position, Is.EqualTo(new Vector3(3f, 0f, 7f)));
            Assert.That(behavior.ObservedCharacterTransform.rotation, Is.EqualTo(character.transform.rotation));
            Assert.That(behavior.ObservedCharacterTransform.position, Is.Not.EqualTo(host.transform.position));
        }

        /// <summary>
        ///     Behaviors are routinely constructed on a bare GameObject in tests and in user
        ///     experiments. Falling back to the component's own transform keeps that working rather
        ///     than returning null and throwing somewhere far away.
        /// </summary>
        [Test]
        public void BehaviorWithNoCharacterAbove_FallsBackToItsOwnTransform()
        {
            var lonely = new GameObject($"{NamePrefix}Lonely");
            var behavior = lonely.AddComponent<ProbeActionBehavior>();

            Assert.That(behavior.ObservedCharacterTransform, Is.SameAs(lonely.transform));
        }

        private static GameObject NewCharacter(Vector3 position)
        {
            var character = new GameObject($"{NamePrefix}Character");
            character.transform.position = position;
            character.AddComponent<ConvaiCharacter>();
            return character;
        }

        /// <summary>
        ///     Minimal behavior that does nothing but expose the protected character transform, so the
        ///     seam can be asserted without dragging a real behavior's peers into the test.
        /// </summary>
        private sealed class ProbeActionBehavior : ConvaiActionExecutorBase
        {
            public Transform ObservedCharacterTransform => CharacterTransform;

            public override Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken) =>
                Task.FromResult(ConvaiActionExecutionResult.Unhandled("Test probe."));
        }
    }
}
