using Convai.Modules.ConversationFlow.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.ConversationFlow
{
    /// <summary>
    ///     Unit tests for <see cref="ConvaiConversationFlowDriverRegistry" />.
    ///     Each test resets the static registry in [SetUp] to guarantee isolation.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiConversationFlowDriverRegistryTests
    {
        private EmbodimentTestRig _rig;

        [SetUp]
        public void SetUp()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _rig = EmbodimentTestRig.Create(nameof(ConvaiConversationFlowDriverRegistryTests));
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _rig.Dispose();
        }

        // ── helpers ───────────────────────────────────────────────────────────────────

        private ConvaiConversationFlowController CreateController()
            => _rig.AddComponent<ConvaiConversationFlowController>();

        // ── tests ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Register_Null_IsNoOpAndDoesNotIncreaseCount()
        {
            // Arrange — count starts at 0 after Reset
            int before = ConvaiConversationFlowDriverRegistry.ActiveCount;

            // Act
            ConvaiConversationFlowDriverRegistry.Register(null);

            // Assert
            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(before),
                "Registering null must be a no-op.");
        }

        [Test]
        public void Register_SameDriverTwice_IsIdempotent()
        {
            // Arrange
            ConvaiConversationFlowController controller = CreateController();
            ConvaiConversationFlowDriverRegistry.Reset();

            // Act
            ConvaiConversationFlowDriverRegistry.Register(controller);
            ConvaiConversationFlowDriverRegistry.Register(controller);

            // Assert
            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(1),
                "Duplicate registration must not add the driver a second time.");
        }

        [Test]
        public void Unregister_RegisteredDriver_RemovesFromActiveSet()
        {
            // Arrange
            ConvaiConversationFlowController controller = CreateController();
            ConvaiConversationFlowDriverRegistry.Reset();
            ConvaiConversationFlowDriverRegistry.Register(controller);
            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(1));

            // Act
            ConvaiConversationFlowDriverRegistry.Unregister(controller);

            // Assert
            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(0),
                "Unregistering the driver must decrement the active count.");
        }

        [Test]
        public void Unregister_UnknownDriver_IsNoOp()
        {
            ConvaiConversationFlowController controller = CreateController();
            ConvaiConversationFlowDriverRegistry.Reset();

            ConvaiConversationFlowDriverRegistry.Unregister(controller);

            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.Zero);
        }

        [Test]
        public void ScopeChanged_FiredOnce_WhenDriverRegisters()
        {
            // Arrange
            ConvaiConversationFlowController controller = CreateController();
            ConvaiConversationFlowDriverRegistry.Reset();
            int fireCount = 0;
            void OnScopeChanged() => fireCount++;
            ConvaiConversationFlowDriverRegistry.ScopeChanged += OnScopeChanged;

            try
            {
                // Act
                ConvaiConversationFlowDriverRegistry.Register(controller);

                // Assert
                Assert.That(fireCount, Is.EqualTo(1),
                    "ScopeChanged must fire exactly once when a driver registers.");
            }
            finally
            {
                ConvaiConversationFlowDriverRegistry.ScopeChanged -= OnScopeChanged;
            }
        }

        [Test]
        public void ScopeChanged_FiredOnce_WhenDriverUnregisters()
        {
            // Arrange
            ConvaiConversationFlowController controller = CreateController();
            ConvaiConversationFlowDriverRegistry.Reset();
            ConvaiConversationFlowDriverRegistry.Register(controller);

            int fireCount = 0;
            void OnScopeChanged() => fireCount++;
            ConvaiConversationFlowDriverRegistry.ScopeChanged += OnScopeChanged;

            try
            {
                // Act
                ConvaiConversationFlowDriverRegistry.Unregister(controller);

                // Assert
                Assert.That(fireCount, Is.EqualTo(1),
                    "ScopeChanged must fire exactly once when a driver unregisters.");
            }
            finally
            {
                ConvaiConversationFlowDriverRegistry.ScopeChanged -= OnScopeChanged;
            }
        }
    }
}
