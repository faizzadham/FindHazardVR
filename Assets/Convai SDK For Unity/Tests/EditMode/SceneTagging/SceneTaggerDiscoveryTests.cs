using System;
using System.Linq;
using Convai.Editor.SceneTagging;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerDiscoveryTests
    {
        private Scene _scene;
        private string _folder;
        [SetUp] public void SetUp()
        {
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string name = "SceneTaggerDiscoveryTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            _folder = "Assets/" + name;
        }
        [TearDown] public void TearDown()
        {
            if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
        }
        private static GameObject Cube(string name, Transform parent, Vector3 position)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name; cube.transform.SetParent(parent); cube.transform.localPosition = position;
            return cube;
        }
        private GameObject Instance(GameObject source)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, _folder + "/fixture.prefab");
            Object.DestroyImmediate(source);
            return (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
        }
        private static SceneTaggerRow[] Discover() => SceneTaggerDiscovery.Discover(new SceneTaggerSettings
            { ExcludeBasicShapes = false, Relevance = 0 }).ToArray();

        [Test] public void RendererOnContainerRootDoesNotEscapeItsPrefab()
        {
            var source = Cube("Room", null, Vector3.zero);
            Cube("Prop A", source.transform, Vector3.left * 3);
            Cube("Prop B", source.transform, Vector3.right * 3);
            GameObject instance = Instance(source);
            var outer = new GameObject("Outer scene parent"); instance.transform.SetParent(outer.transform);
            SceneTaggerRow[] rows = Discover();
            Assert.That(rows.Select(r => r.Target), Is.EquivalentTo(new[] { instance,
                instance.transform.GetChild(0).gameObject, instance.transform.GetChild(1).gameObject }));
            Assert.That(rows.SelectMany(r => r.Renderers).Distinct().Count(), Is.EqualTo(3));
            Assert.That(rows.Sum(r => r.Renderers.Length), Is.EqualTo(3), "Every renderer must have exactly one owner.");
        }
        [TestCase(4f, 2)]
        [TestCase(1f, 1)]
        public void ArbitraryPrefabNamesSplitSeparateObjectsButKeepTouchingParts(float separation, int expected)
        {
            var source = new GameObject("Set_042");
            Cube("Part A", source.transform, Vector3.zero);
            Cube("Part B", source.transform, Vector3.right * separation);
            GameObject instance = Instance(source);
            SceneTaggerRow[] rows = Discover();
            Assert.That(rows.Length, Is.EqualTo(expected));
            Assert.That(rows.Sum(r => r.Renderers.Length), Is.EqualTo(2));
            if (expected == 1) Assert.That(rows[0].Target, Is.SameAs(instance));
        }
        [TestCase("NavMesh", "navmesh")]
        [TestCase("PostProcess", "postprocess")]
        [TestCase("BlockingVolume", "blockingvolume")]
        [TestCase("SkyLight", "skylight")]
        [TestCase("RectLight", "rectlight")]
        [TestCase("SpotLights", "spotlight")]
        [TestCase("Walls", "wall")]
        public void StructuralCompoundsAndPluralsKeepTheirRelevanceToken(string name, string expected) =>
            Assert.That(SceneTaggerDiscovery.LabelTokens(name), Does.Contain(expected));
    }
}
