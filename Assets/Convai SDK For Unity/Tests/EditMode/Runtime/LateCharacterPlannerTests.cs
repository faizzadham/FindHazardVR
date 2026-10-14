using Convai.Runtime.Core;
using Convai.Runtime.Room;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers the decision behind "a character spawned, enabled, disabled, or destroyed during
    ///     play is noticed".
    /// </summary>
    /// <remarks>
    ///     The dangerous direction is startup: at scene load every character's <c>OnEnable</c> runs
    ///     while the runtime is still <see cref="RuntimeState.Created" />, and acting then would
    ///     stack one ownership refresh per character on top of the startup sequence. These tests
    ///     pin both sides — startup stays quiet, and a live runtime acts.
    /// </remarks>
    [TestFixture]
    public sealed class LateCharacterPlannerTests
    {
        private static LateCharacterAction Plan(
            RuntimeState runtimeState,
            bool characterInjected = false,
            bool characterBecameEnabled = true,
            bool hasCharacter = true,
            bool hasRuntime = true,
            bool characterWasDestroyed = false) =>
            LateCharacterPlanner.Plan(
                hasCharacter,
                hasRuntime,
                runtimeState,
                characterInjected,
                characterBecameEnabled,
                characterWasDestroyed);

        [Test]
        public void SceneLoadStaysQuiet_EveryStartupCharacterEnablesWhileTheRuntimeIsCreated()
        {
            Assert.That(
                Plan(RuntimeState.Created),
                Is.EqualTo(LateCharacterAction.None),
                "Startup composition owns this moment; a per-character refresh here would race the "
                + "manager's own Start-time refresh.");
        }

        [Test]
        public void ShutdownStaysQuiet_ThereIsNoRoomLeftToTell()
        {
            Assert.That(Plan(RuntimeState.Stopping), Is.EqualTo(LateCharacterAction.None));
            Assert.That(Plan(RuntimeState.Stopped), Is.EqualTo(LateCharacterAction.None));
            Assert.That(Plan(RuntimeState.Disposed), Is.EqualTo(LateCharacterAction.None));
        }

        [Test]
        public void ASpawnedCharacterIsAdoptedAndOwnershipRefreshed()
        {
            Assert.That(
                Plan(RuntimeState.Running, characterInjected: false, characterBecameEnabled: true),
                Is.EqualTo(LateCharacterAction.AdoptAndRefresh),
                "A never-injected character enabling on a live runtime can only be a runtime "
                + "spawn — installers and explicit lists cannot name it, so it is adopted.");
        }

        [Test]
        public void AnAlreadyInjectedCharacterOnlyRefreshes_ItIsOwnedAlready()
        {
            Assert.That(
                Plan(RuntimeState.Running, characterInjected: true, characterBecameEnabled: true),
                Is.EqualTo(LateCharacterAction.Refresh));
        }

        [Test]
        public void ADisabledCharacterRefreshesActivity_WithoutReleasingItsSeat()
        {
            Assert.That(
                Plan(RuntimeState.Running, characterInjected: true, characterBecameEnabled: false),
                Is.EqualTo(LateCharacterAction.Refresh));
        }

        [Test]
        public void DisablingNeverAdopts_OwnershipIsAnEnableTimeDecision()
        {
            Assert.That(
                Plan(RuntimeState.Running, characterInjected: false, characterBecameEnabled: false),
                Is.EqualTo(LateCharacterAction.Refresh));
        }

        [Test]
        public void DestroyedCharacterRefreshesOwnership_AfterUnityMakesTheObjectCompareNull()
        {
            Assert.That(
                Plan(
                    RuntimeState.Running,
                    hasCharacter: false,
                    characterWasDestroyed: true),
                Is.EqualTo(LateCharacterAction.Refresh),
                "OnDisable retains the seat, so OnDestroy must refresh again even after Unity's "
                + "destroyed-object null semantics hide the component reference.");
        }

        [Test]
        public void ACharacterSpawnedDuringRuntimeStartupIsStillNoticed()
        {
            // Starting spans a frame or two after the manager's Start-time refresh already ran;
            // a spawn inside that window has no later refresh to fall back on.
            Assert.That(
                Plan(RuntimeState.Starting),
                Is.EqualTo(LateCharacterAction.AdoptAndRefresh));
        }

        [Test]
        public void APausedRuntimeStillObservesRosterChanges()
        {
            Assert.That(Plan(RuntimeState.Paused, characterInjected: true),
                Is.EqualTo(LateCharacterAction.Refresh));
            Assert.That(Plan(RuntimeState.Pausing, characterInjected: true),
                Is.EqualTo(LateCharacterAction.Refresh));
            Assert.That(Plan(RuntimeState.Resuming, characterInjected: true),
                Is.EqualTo(LateCharacterAction.Refresh));
        }

        [Test]
        public void NothingHappensWithoutACharacterOrARuntime()
        {
            Assert.That(
                Plan(RuntimeState.Running, hasCharacter: false),
                Is.EqualTo(LateCharacterAction.None));
            Assert.That(
                Plan(RuntimeState.Running, hasRuntime: false),
                Is.EqualTo(LateCharacterAction.None));
        }
    }
}
