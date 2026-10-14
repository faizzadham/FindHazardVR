using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.Emotion.Components;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Actions Showcase P0b coverage: <see cref="MoodCommandHandlerAdapter" /> is auto-added
    ///     alongside <see cref="ConvaiEmotionController" /> (via <c>[RequireComponent]</c>) and
    ///     follows the same context registration lifecycle as
    ///     <see cref="ICharacterReorientationHandler" />.
    /// </summary>
    [TestFixture]
    public sealed class MoodCommandHandlerAdapterTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("MoodCommandHandlerAdapterTestCharacter");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void AddingEmotionController_AutoAddsAdapter_AndRegistersHandler()
        {
            _root.AddComponent<ConvaiCharacter>();
            var controller = _root.AddComponent<ConvaiEmotionController>();

            var adapter = _root.GetComponent<MoodCommandHandlerAdapter>();
            Assert.IsNotNull(adapter,
                "MoodCommandHandlerAdapter must be auto-added alongside ConvaiEmotionController.");

            Assert.IsTrue(EmbodimentContext.TryResolve(controller, out EmbodimentContext context));
            Assert.AreSame(adapter, context.MoodCommandHandler,
                "The adapter must register itself as the character's mood command handler.");
        }

        [Test]
        public void DisablingAdapter_ClearsMoodCommandHandlerSlot()
        {
            _root.AddComponent<ConvaiCharacter>();
            _root.AddComponent<ConvaiEmotionController>();
            var adapter = _root.GetComponent<MoodCommandHandlerAdapter>();
            Assert.IsTrue(EmbodimentContext.TryResolve(adapter, out EmbodimentContext context));
            Assert.AreSame(adapter, context.MoodCommandHandler);

            adapter.enabled = false;

            Assert.IsNull(context.MoodCommandHandler,
                "Disabling the adapter must clear the mood command handler slot.");
        }

        [Test]
        public void RequestMood_ForwardsToController_AndReturnsTrue()
        {
            _root.AddComponent<ConvaiCharacter>();
            _root.AddComponent<ConvaiEmotionController>();
            var adapter = _root.GetComponent<MoodCommandHandlerAdapter>();

            bool accepted = ((IMoodCommandHandler)adapter).RequestMood("Happy", 0.8f, 1f);

            Assert.IsTrue(accepted);
        }
    }
}
