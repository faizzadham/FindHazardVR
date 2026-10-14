using System;
using System.Reflection;
using Convai.Runtime.Vision.Debug;
using Convai.Runtime.Vision.Sources;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public class VisionDebugPreviewTests
    {
        private GameObject _root;
        private FakeFrameSource _source;
        private VisionDebugPreview _preview;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("VisionDebugPreviewTests");
            _source = _root.AddComponent<FakeFrameSource>();
            _preview = _root.AddComponent<VisionDebugPreview>();
            SetPrivateField(_preview, "_frameSourceComponent", _source);
            SetPrivateField(_preview, "_hasExplicitFrameSourceAssignment", true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root);
        }

        [Test]
        public void OnEnable_WhenNoRawImageAssigned_CreatesVisibleOverlayPreview()
        {
            InvokePrivate(_preview, "OnEnable");

            RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");

            Assert.That(previewImage, Is.Not.Null);
            Assert.That(previewImage.gameObject.activeInHierarchy, Is.True);
            Assert.That(previewImage.GetComponentInParent<Canvas>(), Is.Not.Null);
        }

        [Test]
        public void FrameReady_WhenOverlayPreviewCreated_AssignsCurrentRenderTexture()
        {
            var texture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            _source.CurrentRenderTexture = texture;

            try
            {
                InvokePrivate(_preview, "OnEnable");
                _source.RaiseFrameReady();

                RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");

                Assert.That(previewImage.texture, Is.SameAs(texture));
            }
            finally
            {
                texture.Release();
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void OnEnable_WhenNoAssignedSource_UsesFrameSourceOnSameGameObjectBeforeSceneSource()
        {
            SetPrivateField(_preview, "_frameSourceComponent", null);
            SetPrivateField(_preview, "_hasExplicitFrameSourceAssignment", false);

            GameObject sceneSourceObject = new("UnrelatedCameraSource");
            sceneSourceObject.AddComponent<Camera>();
            sceneSourceObject.AddComponent<CameraVisionFrameSource>();

            var texture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            _source.CurrentRenderTexture = texture;

            try
            {
                InvokePrivate(_preview, "OnEnable");
                _source.RaiseFrameReady();

                RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");

                Assert.That(previewImage.texture, Is.SameAs(texture));
            }
            finally
            {
                texture.Release();
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(sceneSourceObject);
            }
        }

        [Test]
        public void Update_WhenAssignedSourceIdle_UsesSceneFrameSourceWithTexture()
        {
            _source.IsCapturing = false;

            GameObject activeSourceObject = new("ActiveVisionSource");
            var activeSource = activeSourceObject.AddComponent<FakeFrameSource>();
            activeSource.IsCapturing = false;

            var texture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            activeSource.CurrentRenderTexture = texture;

            try
            {
                InvokePrivate(_preview, "OnEnable");
                SetPrivateField(_preview, "_nextActiveSourceSearchTime", -1f);

                InvokePrivate(_preview, "Update");

                RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");
                MonoBehaviour assignedSource = GetPrivateField<MonoBehaviour>(_preview, "_frameSourceComponent");
                IVisionFrameSource runtimeSource = GetPrivateField<IVisionFrameSource>(_preview, "_frameSource");

                Assert.That(previewImage.texture, Is.SameAs(texture));
                Assert.That(assignedSource, Is.SameAs(_source));
                Assert.That(runtimeSource, Is.SameAs(activeSource));
            }
            finally
            {
                texture.Release();
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(activeSourceObject);
            }
        }

        [Test]
        public void Update_WhenMultipleSceneSourcesHaveTextures_PrefersCapturingSource()
        {
            _source.IsCapturing = false;

            GameObject idleSourceObject = new("IdleVisionSource");
            var idleSource = idleSourceObject.AddComponent<FakeFrameSource>();
            idleSource.IsCapturing = false;

            GameObject capturingSourceObject = new("CapturingVisionSource");
            var capturingSource = capturingSourceObject.AddComponent<FakeFrameSource>();
            capturingSource.IsCapturing = true;

            var idleTexture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            var capturingTexture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            idleTexture.Create();
            capturingTexture.Create();
            idleSource.CurrentRenderTexture = idleTexture;
            capturingSource.CurrentRenderTexture = capturingTexture;

            try
            {
                InvokePrivate(_preview, "OnEnable");
                SetPrivateField(_preview, "_nextActiveSourceSearchTime", -1f);

                InvokePrivate(_preview, "Update");

                RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");
                MonoBehaviour assignedSource = GetPrivateField<MonoBehaviour>(_preview, "_frameSourceComponent");
                IVisionFrameSource runtimeSource = GetPrivateField<IVisionFrameSource>(_preview, "_frameSource");

                Assert.That(previewImage.texture, Is.SameAs(capturingTexture));
                Assert.That(assignedSource, Is.SameAs(_source));
                Assert.That(runtimeSource, Is.SameAs(capturingSource));
            }
            finally
            {
                idleTexture.Release();
                capturingTexture.Release();
                Object.DestroyImmediate(idleTexture);
                Object.DestroyImmediate(capturingTexture);
                Object.DestroyImmediate(idleSourceObject);
                Object.DestroyImmediate(capturingSourceObject);
            }
        }

        [Test]
        public void Update_WhenRuntimeSourceMissing_UsesSceneFrameSourceWithTexture()
        {
            SetPrivateField(_preview, "_frameSourceComponent", _source);
            SetPrivateField(_preview, "_frameSource", null);

            GameObject activeSourceObject = new("ActiveVisionSource");
            var activeSource = activeSourceObject.AddComponent<FakeFrameSource>();

            var texture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            activeSource.CurrentRenderTexture = texture;

            try
            {
                InvokePrivate(_preview, "OnEnable");
                SetPrivateField(_preview, "_frameSource", null);
                SetPrivateField(_preview, "_nextActiveSourceSearchTime", -1f);

                InvokePrivate(_preview, "Update");

                RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");
                MonoBehaviour assignedSource = GetPrivateField<MonoBehaviour>(_preview, "_frameSourceComponent");
                IVisionFrameSource runtimeSource = GetPrivateField<IVisionFrameSource>(_preview, "_frameSource");

                Assert.That(previewImage.texture, Is.SameAs(texture));
                Assert.That(assignedSource, Is.SameAs(_source));
                Assert.That(runtimeSource, Is.SameAs(activeSource));
            }
            finally
            {
                texture.Release();
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(activeSourceObject);
            }
        }

        [Test]
        public void GetFrameSourceName_IncludesSourceIdAndGameObjectPath()
        {
            GameObject childObject = new("ChildSource");
            childObject.transform.SetParent(_root.transform, false);
            var childSource = childObject.AddComponent<FakeFrameSource>();

            string label = (string)typeof(VisionDebugPreview)
                .GetMethod("GetFrameSourceName", BindingFlags.NonPublic | BindingFlags.Static)
                ?.Invoke(null, new object[] { childSource });

            Assert.That(label, Is.EqualTo("VisionDebugPreviewTests/ChildSource [fake] (FakeFrameSource)"));
        }

        [Test]
        public void Update_WhenAssignedSourceBecomesReady_RestoresAssignedSourceAfterFallback()
        {
            _source.IsCapturing = false;

            GameObject activeSourceObject = new("ActiveVisionSource");
            var activeSource = activeSourceObject.AddComponent<FakeFrameSource>();

            var fallbackTexture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            var assignedTexture = new RenderTexture(64, 32, 24, RenderTextureFormat.ARGB32);
            fallbackTexture.Create();
            assignedTexture.Create();
            activeSource.CurrentRenderTexture = fallbackTexture;

            try
            {
                InvokePrivate(_preview, "OnEnable");
                SetPrivateField(_preview, "_nextActiveSourceSearchTime", -1f);

                InvokePrivate(_preview, "Update");

                _source.CurrentRenderTexture = assignedTexture;
                SetPrivateField(_preview, "_nextActiveSourceSearchTime", -1f);
                InvokePrivate(_preview, "Update");

                RawImage previewImage = GetPrivateField<RawImage>(_preview, "_previewImage");
                MonoBehaviour assignedSource = GetPrivateField<MonoBehaviour>(_preview, "_frameSourceComponent");
                IVisionFrameSource runtimeSource = GetPrivateField<IVisionFrameSource>(_preview, "_frameSource");

                Assert.That(previewImage.texture, Is.SameAs(assignedTexture));
                Assert.That(assignedSource, Is.SameAs(_source));
                Assert.That(runtimeSource, Is.SameAs(_source));
            }
            finally
            {
                fallbackTexture.Release();
                assignedTexture.Release();
                Object.DestroyImmediate(fallbackTexture);
                Object.DestroyImmediate(assignedTexture);
                Object.DestroyImmediate(activeSourceObject);
            }
        }

        private static T GetPrivateField<T>(VisionDebugPreview preview, string fieldName) where T : class =>
            typeof(VisionDebugPreview)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(preview) as T;

        private static void SetPrivateField(VisionDebugPreview preview, string fieldName, object value) =>
            typeof(VisionDebugPreview)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(preview, value);

        private static void InvokePrivate(VisionDebugPreview preview, string methodName) =>
            typeof(VisionDebugPreview)
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(preview, Array.Empty<object>());

        private sealed class FakeFrameSource : MonoBehaviour, IVisionFrameSource
        {
            public bool IsCapturing { get; set; } = true;
            public long FrameCount { get; private set; }
            public (int Width, int Height) FrameDimensions => (64, 32);
            public float TargetFrameRate => 10;
            public string SourceId => "fake";
            public RenderTexture CurrentRenderTexture { get; set; }
            public bool IsFrameReady => CurrentRenderTexture != null;
            public event Action FrameReady;
            public void StartCapture() { }
            public void StopCapture() { }

            public void RaiseFrameReady()
            {
                FrameCount++;
                FrameReady?.Invoke();
            }
        }
    }
}
