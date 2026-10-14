using System.Collections.Generic;
using Convai.Editor.AI;
using Convai.Runtime.Presentation.Views;
using Convai.Runtime.Presentation.Views.Transcript;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiTranscriptUiDiagnosticTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject createdObject in _createdObjects)
                if (createdObject != null)
                    Object.DestroyImmediate(createdObject);
            _createdObjects.Clear();

            _sceneFixture.End();
        }

        [Test]
        public void CountShippedTranscriptUis_CountsActiveAndInactiveTypedViewsOnly()
        {
            GameObject transcriptDisplayObject = CreateObject("Transcript Display");
            transcriptDisplayObject.SetActive(false);
            transcriptDisplayObject.AddComponent<ConvaiTranscriptDisplay>();

            GameObject chatTranscriptObject = CreateObject("Chat Transcript");
            chatTranscriptObject.AddComponent<ChatTranscriptUI>();

            GameObject plainBehaviourObject = CreateObject("Plain Behaviour");
            plainBehaviourObject.AddComponent<TranscriptUiPlainBehaviour>();

            Assert.That(ConvaiMcpTools.CountShippedTranscriptUis(TestScene), Is.EqualTo(2));
        }

        private GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _createdObjects.Add(gameObject);
            return gameObject;
        }
    }

    public sealed class TranscriptUiPlainBehaviour : MonoBehaviour
    {
    }
}
