using Convai.Modules.Gaze.Components;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Actions Showcase P0b coverage: <see cref="GazeAttentionRequests" /> is auto-added
    ///     alongside <see cref="ConvaiGazeController" /> (via <c>[RequireComponent]</c>) and
    ///     follows the same context registration lifecycle as
    ///     <see cref="Convai.Domain.Embodiment.Interfaces.ICharacterReorientationHandler" />.
    /// </summary>
    [TestFixture]
    public sealed class GazeAttentionRequestsTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("GazeAttentionRequestsTestCharacter");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void AddingGazeController_AutoAddsAdapter_AndRegistersHandler()
        {
            _root.AddComponent<ConvaiCharacter>();
            var controller = _root.AddComponent<ConvaiGazeController>();

            var adapter = _root.GetComponent<GazeAttentionRequests>();
            Assert.IsNotNull(adapter,
                "GazeAttentionRequests must be auto-added alongside ConvaiGazeController.");

            Assert.IsTrue(EmbodimentContext.TryResolve(controller, out EmbodimentContext context));
            Assert.AreSame(adapter, context.GazeCommandHandler,
                "The adapter must register itself as the character's gaze command handler.");
        }

        [Test]
        public void DisablingAdapter_ClearsGazeCommandHandlerSlot()
        {
            _root.AddComponent<ConvaiCharacter>();
            _root.AddComponent<ConvaiGazeController>();
            var adapter = _root.GetComponent<GazeAttentionRequests>();
            Assert.IsTrue(EmbodimentContext.TryResolve(adapter, out EmbodimentContext context));
            Assert.AreSame(adapter, context.GazeCommandHandler);

            adapter.enabled = false;

            Assert.IsNull(context.GazeCommandHandler,
                "Disabling the adapter must clear the gaze command handler slot.");
        }

        [Test]
        public void RequestSustainedGaze_WorldPosition_ReturnsTrue()
        {
            _root.AddComponent<ConvaiCharacter>();
            _root.AddComponent<ConvaiGazeController>();
            var adapter = _root.GetComponent<GazeAttentionRequests>();

            bool accepted = ((Convai.Domain.Embodiment.Interfaces.IGazeCommandHandler)adapter)
                .RequestSustainedGaze(new Vector3(1f, 0f, 2f), 1f, priority: 5);

            Assert.IsTrue(accepted);
        }
    }
}
