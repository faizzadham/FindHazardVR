using System.Collections.Generic;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Composition;
using Convai.Runtime.Room;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers the change detection behind "enable a character GameObject during play and it is
    ///     in the conversation".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ownership refresh used to compare only who is <i>owned</i>, and enabling or disabling
    ///         an owned character changes who can be in the room without changing ownership at all —
    ///         so the room manager was never told, and the sample's promise quietly did nothing.
    ///     </para>
    ///     <para>
    ///         These assert the decision the refresh actually makes rather than a helper it happens
    ///         to call, so removing the connectable-roster comparison from
    ///         <see cref="OwnershipChangeDetector" /> fails them. A test that only exercised the
    ///         comparison helper would pass with the call site reverted, which is the same as not
    ///         testing the fix.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class RuntimeRosterActivityTests
    {
        private readonly List<GameObject> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
                if (_createdObjects[i] != null)
                    Object.DestroyImmediate(_createdObjects[i]);
            _createdObjects.Clear();
        }

        private ConvaiCharacter CreateCharacter(string name)
        {
            var characterObject = new GameObject(name);
            _createdObjects.Add(characterObject);
            return characterObject.AddComponent<ConvaiCharacter>();
        }

        /// <summary>
        ///     Runs the refresh's decision with ownership held identical, so only the connectable
        ///     roster can account for the answer.
        /// </summary>
        private static bool OwnershipOnlyChanged(
            IReadOnlyList<ConvaiCharacter> owned,
            IReadOnlyList<IConvaiCharacterAgent> connectableBefore,
            IReadOnlyList<IConvaiCharacterAgent> connectableAfter) =>
            OwnershipChangeDetector.HasChanged(
                playerUnchanged: true,
                conversationTargetUnchanged: true,
                selectionModeUnchanged: true,
                previousOwned: owned,
                currentOwned: owned,
                previousIncluded: System.Array.Empty<ConvaiCharacter>(),
                currentIncluded: System.Array.Empty<ConvaiCharacter>(),
                previousConnectable: connectableBefore,
                currentConnectable: connectableAfter);

        [Test]
        public void EnablingAnOwnedCharacterIsAChangeTheRoomManagerIsToldAbout()
        {
            ConvaiCharacter sofia = CreateCharacter("Sofia");
            ConvaiCharacter james = CreateCharacter("James");
            james.gameObject.SetActive(false);
            ConvaiCharacter[] owned = { sofia, james };

            List<IConvaiCharacterAgent> before = ConvaiRuntimeHost.CaptureConnectableCharacters(owned);
            james.gameObject.SetActive(true);
            List<IConvaiCharacterAgent> after = ConvaiRuntimeHost.CaptureConnectableCharacters(owned);

            Assert.That(
                OwnershipOnlyChanged(owned, before, after),
                Is.True,
                "Ownership is identical here — if enabling a character does not register as a "
                + "change, the room manager is never notified and the character never joins.");
        }

        [Test]
        public void DisablingAnOwnedCharacterIsAChangeTheRoomManagerIsToldAbout()
        {
            ConvaiCharacter sofia = CreateCharacter("Sofia");
            ConvaiCharacter james = CreateCharacter("James");
            ConvaiCharacter[] owned = { sofia, james };

            List<IConvaiCharacterAgent> before = ConvaiRuntimeHost.CaptureConnectableCharacters(owned);
            james.gameObject.SetActive(false);
            List<IConvaiCharacterAgent> after = ConvaiRuntimeHost.CaptureConnectableCharacters(owned);

            Assert.That(
                OwnershipOnlyChanged(owned, before, after),
                Is.True,
                "A character that disappears must be able to leave the room without a reconnect.");
        }

        [Test]
        public void AnUntouchedSceneIsNotAChange()
        {
            ConvaiCharacter sofia = CreateCharacter("Sofia");
            ConvaiCharacter[] owned = { sofia };

            List<IConvaiCharacterAgent> before = ConvaiRuntimeHost.CaptureConnectableCharacters(owned);
            List<IConvaiCharacterAgent> after = ConvaiRuntimeHost.CaptureConnectableCharacters(owned);

            Assert.That(
                OwnershipOnlyChanged(owned, before, after),
                Is.False,
                "Every ordinary refresh runs this; answering \"changed\" would rebuild the room "
                + "composition for nothing.");
        }

        [Test]
        public void AnEmptyRosterAndANullRosterAreNotAChange()
        {
            // The first refresh of a session compares against an empty last-observed list; a null
            // must not read as a phantom change and recompose the room during startup.
            Assert.That(
                OwnershipChangeDetector.HaveSameReferences(null, System.Array.Empty<object>()),
                Is.True);
        }

        [Test]
        public void OwnershipChangesAreStillDetectedOnTheirOwn()
        {
            ConvaiCharacter sofia = CreateCharacter("Sofia");
            ConvaiCharacter james = CreateCharacter("James");
            var connectable = new List<IConvaiCharacterAgent> { sofia };

            Assert.That(
                OwnershipChangeDetector.HasChanged(
                    playerUnchanged: true,
                    conversationTargetUnchanged: true,
                    selectionModeUnchanged: true,
                    previousOwned: new[] { sofia },
                    currentOwned: new[] { sofia, james },
                    previousIncluded: System.Array.Empty<ConvaiCharacter>(),
                    currentIncluded: System.Array.Empty<ConvaiCharacter>(),
                    previousConnectable: connectable,
                    currentConnectable: connectable),
                Is.True,
                "Adding the connectable-roster comparison must not cost the ownership comparison "
                + "that was already there.");
        }

        [Test]
        public void AChangedPlayerOrStartingCharacterIsStillAChange()
        {
            ConvaiCharacter sofia = CreateCharacter("Sofia");
            ConvaiCharacter[] owned = { sofia };
            var connectable = new List<IConvaiCharacterAgent> { sofia };

            Assert.That(
                OwnershipChangeDetector.HasChanged(
                    playerUnchanged: false,
                    conversationTargetUnchanged: true,
                    selectionModeUnchanged: true,
                    owned, owned,
                    System.Array.Empty<ConvaiCharacter>(), System.Array.Empty<ConvaiCharacter>(),
                    connectable, connectable),
                Is.True);

            Assert.That(
                OwnershipChangeDetector.HasChanged(
                    playerUnchanged: true,
                    conversationTargetUnchanged: false,
                    selectionModeUnchanged: true,
                    owned, owned,
                    System.Array.Empty<ConvaiCharacter>(), System.Array.Empty<ConvaiCharacter>(),
                    connectable, connectable),
                Is.True);

            Assert.That(
                OwnershipChangeDetector.HasChanged(
                    playerUnchanged: true,
                    conversationTargetUnchanged: true,
                    selectionModeUnchanged: false,
                    owned, owned,
                    System.Array.Empty<ConvaiCharacter>(), System.Array.Empty<ConvaiCharacter>(),
                    connectable, connectable),
                Is.True);
        }
    }
}
