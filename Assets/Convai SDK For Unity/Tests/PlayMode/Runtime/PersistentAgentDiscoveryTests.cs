using System.Collections;
using System.Linq;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Composition;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Runtime
{
    /// <summary>
    ///     Ownership resolution must see agents that persist across scene loads.
    /// </summary>
    /// <remarks>
    ///     A player rig commonly calls <c>DontDestroyOnLoad</c> from <c>Awake</c> — Unity's own URP
    ///     sample player does. The scene it moves to is never returned by <c>SceneManager</c>, so
    ///     enumerating loaded scenes alone loses the player and the room refuses to start, blaming a
    ///     missing component the scene visibly has. These cases run in Play Mode because
    ///     <c>DontDestroyOnLoad</c> has no meaning outside it.
    /// </remarks>
    public sealed class PersistentAgentDiscoveryTests
    {
        private GameObject _persistentObject;
        private GameObject _sceneObject;

        [TearDown]
        public void TearDown()
        {
            if (_persistentObject != null) Object.DestroyImmediate(_persistentObject);
            if (_sceneObject != null) Object.DestroyImmediate(_sceneObject);
            _persistentObject = null;
            _sceneObject = null;
        }

        [UnityTest]
        public IEnumerator GetLiveSceneObjects_FindsAPlayerThatMovedToDontDestroyOnLoad()
        {
            _persistentObject = new GameObject("Persistent Player");
            ConvaiPlayer player = _persistentObject.AddComponent<ConvaiPlayer>();
            Object.DontDestroyOnLoad(_persistentObject);
            yield return null;

            Assert.That(
                _persistentObject.scene.name,
                Is.EqualTo("DontDestroyOnLoad"),
                "The fixture must actually leave its scene, or this proves nothing.");
            Assert.That(ConvaiRuntimeHost.GetLiveSceneObjects<ConvaiPlayer>(), Does.Contain(player));
        }

        [UnityTest]
        public IEnumerator GetLiveSceneObjects_ReportsPersistentAndSceneAgentsTogetherWithoutDuplicates()
        {
            _persistentObject = new GameObject("Persistent Player");
            ConvaiPlayer persistentPlayer = _persistentObject.AddComponent<ConvaiPlayer>();
            Object.DontDestroyOnLoad(_persistentObject);

            _sceneObject = new GameObject("Scene Player");
            ConvaiPlayer scenePlayer = _sceneObject.AddComponent<ConvaiPlayer>();
            yield return null;

            ConvaiPlayer[] found = ConvaiRuntimeHost.GetLiveSceneObjects<ConvaiPlayer>();

            Assert.That(found, Does.Contain(persistentPlayer));
            Assert.That(found, Does.Contain(scenePlayer));
            Assert.That(found.Count(candidate => candidate == persistentPlayer), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator GetLiveSceneObjects_FindsACharacterThatMovedToDontDestroyOnLoad()
        {
            // Held inactive so ConvaiCharacter.Awake does not run its setup validation against a
            // scene with no manager. Discovery includes inactive components either way, which is the
            // behaviour under test — the scene a component lives in, not whether it is enabled.
            _persistentObject = new GameObject("Persistent Character");
            _persistentObject.SetActive(false);
            ConvaiCharacter character = _persistentObject.AddComponent<ConvaiCharacter>();
            Object.DontDestroyOnLoad(_persistentObject);
            yield return null;

            Assert.That(ConvaiRuntimeHost.GetLiveSceneObjects<ConvaiCharacter>(), Does.Contain(character));
        }
    }
}
