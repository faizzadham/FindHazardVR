using System.Collections.Generic;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Canary ScriptableObject used by <see cref="EmbodimentReceiverTestsBase{TController,TProfile}" />
    ///     to verify that <see cref="IEmbodimentProfileReceiver.ApplyProfile" /> rejects profiles
    ///     of a wrong type. Must live outside the generic class so that Unity's reflection-based
    ///     <c>ScriptableObject.CreateInstance</c> can resolve the concrete type without ambiguity.
    /// </summary>
    internal sealed class EmbodimentTestSentinelProfile : ScriptableObject { }

    /// <summary>
    ///     Abstract base that provides the shared invariant tests for every
    ///     <see cref="ConvaiCharacterModule{TProfile}" /> controller. Subclass once per
    ///     module and supply <see cref="ExpectedModuleId" />, <see cref="ExpectedPhase" />, and
    ///     <see cref="CreateValidProfile" />; NUnit discovers the shared tests automatically.
    ///     Eliminates ~30 lines of boilerplate per controller.
    /// </summary>
    public abstract class EmbodimentReceiverTestsBase<TController, TProfile>
        where TController : ConvaiCharacterModule<TProfile>, IEmbodimentTickable
        where TProfile : ScriptableObject
    {

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<TController, TProfile> _harness;

        protected TController Controller => _harness.Controller;
        protected EmbodimentContext Context => _rig.Context;

        /// <summary>Expected value of <see cref="IEmbodimentProfileReceiver.ModuleId" />.</summary>
        protected abstract string ExpectedModuleId { get; }

        /// <summary>Expected tick phase for this controller.</summary>
        protected abstract EmbodimentTickPhase ExpectedPhase { get; }

        /// <summary>
        ///     Creates a valid <typeparamref name="TProfile" /> instance for profile-apply tests.
        ///     Caller is responsible for calling <c>Object.DestroyImmediate</c> on the result.
        /// </summary>
        protected abstract TProfile CreateValidProfile();

        [SetUp]
        public void BaseSetUp()
        {
            _rig = EmbodimentTestRig.Create(GetType().Name);
            _harness = new EmbodimentReceiverHarness<TController, TProfile>(_rig);
        }

        [TearDown]
        public void BaseTearDown()
        {
            _rig.Dispose();
        }

        [Test]
        public void ModuleId_EqualsExpected()
        {
            // Arrange / Act — module ID is a compile-time constant read from the controller.
            string moduleId = _harness.ModuleId;

            // Assert
            Assert.That(moduleId, Is.EqualTo(ExpectedModuleId));
        }

        [Test]
        public void Phase_EqualsExpected()
        {
            EmbodimentTickPhase phase = _harness.Phase;

            Assert.That(phase, Is.EqualTo(ExpectedPhase));
        }

        [Test]
        public void OnEnable_RegistersControllerAsProfileReceiver()
        {
            // Arrange — controller was enabled by AddComponent in BaseSetUp.

            // Assert
            Assert.That(IsRegisteredAsReceiver(), Is.True,
                "Controller should be registered as a profile receiver after OnEnable.");
        }

        [Test]
        public void OnDisable_UnregistersControllerAsProfileReceiver()
        {
            // Arrange
            _harness.Disable();

            // Assert
            Assert.That(IsRegisteredAsReceiver(), Is.False,
                "Controller should be unregistered after OnDisable.");
        }

        [Test]
        public void ApplyProfile_ValidType_ReturnsTrue()
        {
            // Arrange
            TProfile profile = CreateValidProfile();
            try
            {
                // Act
                bool result = _harness.ApplyProfile(profile);

                // Assert
                Assert.That(result, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ApplyProfile_NullProfile_ReturnsTrue()
        {
            // Act + Assert — null clears the authored override; must always succeed.
            Assert.That(_harness.ApplyProfile((ScriptableObject)null), Is.True);
        }

        [Test]
        public void ApplyProfile_WrongType_ReturnsFalse()
        {
            // Arrange — EmbodimentTestSentinelProfile is a non-generic, non-nested ScriptableObject
            // so Unity's CreateInstance can resolve it unambiguously via reflection.
            EmbodimentTestSentinelProfile wrong =
                ScriptableObject.CreateInstance<EmbodimentTestSentinelProfile>();
            try
            {
                // Act + Assert
                Assert.That(
                    ((IEmbodimentProfileReceiver)Controller).ApplyProfile(wrong),
                    Is.False);
            }
            finally
            {
                Object.DestroyImmediate(wrong);
            }
        }

        [Test]
        public void ApplyProfile_SameInstanceTwice_IsIdempotent()
        {
            // Arrange
            TProfile profile = CreateValidProfile();
            try
            {
                _harness.ApplyProfile(profile);

                Assert.That(_harness.ApplyProfile(profile), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private bool IsRegisteredAsReceiver()
        {
            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            Context.GetProfileReceivers(registrations);
            IEmbodimentProfileReceiver receiver = (IEmbodimentProfileReceiver)Controller;
            for (int i = 0; i < registrations.Count; i++)
                if (registrations[i].Matches(receiver)) return true;
            return false;
        }
    }
}
