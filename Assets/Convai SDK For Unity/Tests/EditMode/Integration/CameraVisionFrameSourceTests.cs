using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.DomainEvents.Vision;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Runtime.Vision.Sources;
using Convai.Runtime.Vision.Sources.Internal;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Integration
{
    /// <summary>
    ///     Integration tests for CameraVisionFrameSource component.
    ///     Tests lifecycle, state management, and EventHub integration.
    /// </summary>
    /// <remarks>
    ///     Note: These tests run in EditMode and can create GameObjects with Camera
    ///     and CameraVisionFrameSource components. However, LateUpdate() doesn't run
    ///     automatically, so we test initialization, state changes, and event publishing.
    /// </remarks>
    [Category("Integration")]
    public class CameraVisionFrameSourceTests
    {
        private Camera _camera;
        private IEventHub _eventHub;
        private CameraVisionFrameSource _frameSource;
        private List<VisionCaptureStarted> _startedEvents;
        private List<VisionCaptureStopped> _stoppedEvents;
        private GameObject _testGameObject;

        [SetUp]
        public void SetUp()
        {
            _eventHub = new EventHub(new ImmediateScheduler());

            _testGameObject = new GameObject("TestCameraVisionFrameSource");
            _camera = _testGameObject.AddComponent<Camera>();
            _frameSource = _testGameObject.AddComponent<CameraVisionFrameSource>();

            _startedEvents = new List<VisionCaptureStarted>();
            _stoppedEvents = new List<VisionCaptureStopped>();

            _eventHub.Subscribe<VisionCaptureStarted>(e => _startedEvents.Add(e));
            _eventHub.Subscribe<VisionCaptureStopped>(e => _stoppedEvents.Add(e));

            SetPrivateFieldValue("_cameraCaptureMode", CameraVisionFrameSource.CameraCaptureMode.BuiltInHooks);
        }

        [TearDown]
        public void TearDown()
        {
            if (_testGameObject != null)
            {
                Object.DestroyImmediate(_testGameObject);
                _testGameObject = null;
            }

            _camera = null;
            _frameSource = null;
            _eventHub = null;
            _startedEvents = null;
            _stoppedEvents = null;
        }

        #region Test Infrastructure

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private object GetPrivateFieldValue(string fieldName) =>
            typeof(CameraVisionFrameSource)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(_frameSource);

        private void SetPrivateFieldValue(string fieldName, object value)
        {
            FieldInfo field = typeof(CameraVisionFrameSource)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_frameSource, value);
        }

        private object InvokePrivateMethod(string methodName, params object[] args) =>
            typeof(CameraVisionFrameSource)
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(_frameSource, args);

        #endregion

        #region Initialization Tests

        [Test]
        public void CameraVisionFrameSource_InitialState_IsNotCapturing()
        {
            Assert.IsFalse(_frameSource.IsCapturing);
            Assert.AreEqual(0, _frameSource.FrameCount);
        }

        [Test]
        public void CameraVisionFrameSource_StatusProvider_InitialState_IsIdle()
        {
            var statusProvider = _frameSource as IVisionFrameSourceStatusProvider;

            Assert.That(statusProvider, Is.Not.Null);
            Assert.That(statusProvider.State, Is.EqualTo(VisionSourceState.Idle));
            Assert.That(statusProvider.ErrorKind, Is.EqualTo(VisionSourceErrorKind.None));
            Assert.That(statusProvider.HasUsableFrame, Is.False);
        }

        [Test]
        public void CameraVisionFrameSource_HasTargetCamera_AfterOnEnable()
        {
            Assert.IsNotNull(_frameSource.TargetCamera);
            Assert.AreEqual(_camera, _frameSource.TargetCamera);
        }

        [Test]
        public void CameraVisionFrameSource_DoesNotAllocateRenderTextureBeforeCapture() =>
            Assert.IsNull(_frameSource.CurrentRenderTexture);

        [Test]
        public void CameraVisionFrameSource_EffectiveSettings_ArePositive()
        {
            Assert.Greater(_frameSource.EffectiveWidth, 0);
            Assert.Greater(_frameSource.EffectiveHeight, 0);
            Assert.Greater(_frameSource.EffectiveFps, 0);
        }

        [Test]
        public void ResolveCapturePresetSettings_BalancedPreset_UsesExpectedDefaults()
        {
            Type capturePresetType = typeof(CameraVisionFrameSource)
                .GetNestedType("CapturePreset", BindingFlags.NonPublic);
            SetPrivateFieldValue("_capturePreset", Enum.Parse(capturePresetType, "Balanced"));

            object result = InvokePrivateMethod("ResolveCapturePresetSettings");

            Assert.That(result.ToString(), Does.Contain("1280").And.Contain("720").And.Contain("15"));
        }

        [Test]
        public void ResolveCapturePresetSettings_CustomPreset_ClampsToMinimums()
        {
            Type capturePresetType = typeof(CameraVisionFrameSource)
                .GetNestedType("CapturePreset", BindingFlags.NonPublic);
            SetPrivateFieldValue("_capturePreset", Enum.Parse(capturePresetType, "Custom"));
            SetPrivateFieldValue("_captureWidth", 1);
            SetPrivateFieldValue("_captureHeight", 1);
            SetPrivateFieldValue("_targetFps", 0);

            object result = InvokePrivateMethod("ResolveCapturePresetSettings");

            Assert.That(result.ToString(), Does.Contain("320").And.Contain("240").And.Contain("1"));
        }

        #endregion

        #region StartCapture Tests

        [Test]
        public void StartCapture_SetsIsCapturingToTrue()
        {
            _frameSource.StartCapture();

            Assert.IsTrue(_frameSource.IsCapturing);
        }

        [Test]
        public void StartCapture_ResetsFrameCount()
        {
            _frameSource.StartCapture();

            Assert.AreEqual(0, _frameSource.FrameCount);
        }

        [Test]
        public void StartCapture_WhenAlreadyCapturing_DoesNothing()
        {
            _frameSource.StartCapture();
            _frameSource.StartCapture();

            Assert.IsTrue(_frameSource.IsCapturing);
        }

        [Test]
        public void StartCapture_BuiltInHooks_UsesBuiltInHooks_AndStopCapture_ClearsMode()
        {
            SetPrivateFieldValue("_cameraCaptureMode", CameraVisionFrameSource.CameraCaptureMode.BuiltInHooks);
            _frameSource.StartCapture();

            Assert.That(GetPrivateFieldValue("_activeCaptureMode").ToString(), Is.EqualTo("BuiltInHooks"));

            _frameSource.StopCapture();

            Assert.That(GetPrivateFieldValue("_activeCaptureMode").ToString(), Is.EqualTo("None"));
        }

        [Test]
        public void BuiltInBackend_AttachesAndDetachesCommandBuffer_ForHookCapture()
        {
            var backend = new BuiltInCameraCaptureBackend();
            var commandBuffer = new CommandBuffer();
            float now = 1f;
            int attachCount = 0;
            int detachCount = 0;
            int registerCount = 0;

            backend.Initialize(new CameraCaptureBackendContext
            {
                TargetCamera = _camera,
                ShouldProcessCamera = camera => camera == _camera,
                GetNextCaptureTime = () => 0f,
                GetCurrentTime = () => now,
                EnsureCaptureCommandBuffer = () => commandBuffer,
                AttachCaptureCommandBuffer = () => attachCount++,
                DetachCaptureCommandBuffer = () => detachCount++,
                RegisterHookCapture = () => registerCount++,
                StopCaptureWithError = (_, _) => { }
            });

            MethodInfo preRenderMethod = typeof(BuiltInCameraCaptureBackend)
                .GetMethod("HandlePreRender", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo postRenderMethod = typeof(BuiltInCameraCaptureBackend)
                .GetMethod("HandlePostRender", BindingFlags.NonPublic | BindingFlags.Instance);

            preRenderMethod.Invoke(backend, new object[] { _camera });
            postRenderMethod.Invoke(backend, new object[] { _camera });

            Assert.That(attachCount, Is.EqualTo(1));
            Assert.That(detachCount, Is.EqualTo(1));
            Assert.That(registerCount, Is.EqualTo(1));

            backend.Dispose();
            commandBuffer.Release();
        }

        [Test]
        public void StartCapture_InitializesFrameHealthGraceWindow()
        {
            _frameSource.StartCapture();

            float captureStartTime = (float)GetPrivateFieldValue("_captureStartTime");
            float nextProbeTime = (float)GetPrivateFieldValue("_nextFrameHealthProbeTime");

            Assert.That(nextProbeTime, Is.GreaterThan(captureStartTime));
        }

        [Test]
        public void RegisterCapturedFrame_WhenHooksAreHealthy_DoesNotWarnOrFallback()
        {
            RenderPipelineAsset originalAsset = GraphicsSettings.defaultRenderPipeline;

            try
            {
                GraphicsSettings.defaultRenderPipeline = null;
                _frameSource.StartCapture();

                MethodInfo registerCapturedFrameMethod = typeof(CameraVisionFrameSource)
                    .GetMethod("RegisterCapturedFrame", BindingFlags.NonPublic | BindingFlags.Instance);

                for (int i = 0; i < 16; i++)
                    registerCapturedFrameMethod.Invoke(_frameSource, null);

                Assert.That(_frameSource.FrameCount, Is.EqualTo(16));
                Assert.That((bool)GetPrivateFieldValue("_usesExplicitRenderPath"), Is.False);
                Assert.That((bool)GetPrivateFieldValue("_warnedForRepeatedInvalidContent"), Is.False);
                Assert.That(_frameSource.IsFrameReady, Is.True);
                Assert.That(((IVisionFrameSourceStatusProvider)_frameSource).State, Is.EqualTo(VisionSourceState.Ready));
            }
            finally
            {
                _frameSource.StopCapture();
                GraphicsSettings.defaultRenderPipeline = originalAsset;
            }
        }

        [Test]
        public void StartCapture_WithExplicitRenderCompatibility_OnSrp_UsesExplicitRenderPath()
        {
            RenderPipelineAsset originalAsset = GraphicsSettings.defaultRenderPipeline;

            try
            {
                GraphicsSettings.defaultRenderPipeline = ScriptableObject.CreateInstance<TestRenderPipelineAsset>();
                SetPrivateFieldValue("_cameraCaptureMode",
                    CameraVisionFrameSource.CameraCaptureMode.ExplicitRenderCompatibility);

                _frameSource.StartCapture();

                Assert.That((bool)GetPrivateFieldValue("_usesExplicitRenderPath"), Is.True);
                Assert.That(GetPrivateFieldValue("_captureBackend"), Is.Null);
                Assert.That(GetPrivateFieldValue("_activeCaptureMode").ToString(),
                    Is.EqualTo("ExplicitRenderCompatibility"));
            }
            finally
            {
                _frameSource.StopCapture();
                GraphicsSettings.defaultRenderPipeline = originalAsset;
            }
        }

        [Test]
        public void StartCapture_OnSrpAuto_UsesExplicitRenderCompatibilityPath()
        {
            RenderPipelineAsset originalAsset = GraphicsSettings.defaultRenderPipeline;

            try
            {
                GraphicsSettings.defaultRenderPipeline = ScriptableObject.CreateInstance<TestRenderPipelineAsset>();
                SetPrivateFieldValue("_cameraCaptureMode", CameraVisionFrameSource.CameraCaptureMode.Auto);

                _frameSource.StartCapture();

                Assert.That(_frameSource.IsCapturing, Is.True);
                Assert.That((bool)GetPrivateFieldValue("_usesExplicitRenderPath"), Is.True);
                Assert.That(GetPrivateFieldValue("_captureBackend"), Is.Null);
                Assert.That(GetPrivateFieldValue("_activeCaptureMode").ToString(),
                    Is.EqualTo("ExplicitRenderCompatibility"));
            }
            finally
            {
                _frameSource.StopCapture();
                GraphicsSettings.defaultRenderPipeline = originalAsset;
            }
        }

        [Test]
        public void StartCapture_OnExplicitSrpNative_FailsClosed()
        {
            RenderPipelineAsset originalAsset = GraphicsSettings.defaultRenderPipeline;

            try
            {
                GraphicsSettings.defaultRenderPipeline = ScriptableObject.CreateInstance<TestRenderPipelineAsset>();
                SetPrivateFieldValue("_cameraCaptureMode", CameraVisionFrameSource.CameraCaptureMode.SrpNative);
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("SRP-native capture backend not available"));

                _frameSource.StartCapture();

                var statusProvider = (IVisionFrameSourceStatusProvider)_frameSource;
                Assert.That(_frameSource.IsCapturing, Is.False);
                Assert.That((bool)GetPrivateFieldValue("_usesExplicitRenderPath"), Is.False);
                Assert.That(GetPrivateFieldValue("_captureBackend"), Is.Null);
                Assert.That(statusProvider.State, Is.EqualTo(VisionSourceState.Failed));
                Assert.That(statusProvider.ErrorKind, Is.EqualTo(VisionSourceErrorKind.UnsupportedPlatform));
            }
            finally
            {
                _frameSource.StopCapture();
                GraphicsSettings.defaultRenderPipeline = originalAsset;
            }
        }

        #endregion

        #region StopCapture Tests

        [Test]
        public void StopCapture_SetsIsCapturingToFalse()
        {
            _frameSource.StartCapture();
            _frameSource.StopCapture();

            Assert.IsFalse(_frameSource.IsCapturing);
        }

        [Test]
        public void StopCapture_WhenNotCapturing_DoesNothing()
        {
            _frameSource.StopCapture();

            Assert.IsFalse(_frameSource.IsCapturing);
        }

        #endregion

        #region IVisionFrameSource Interface Tests

        [Test]
        public void IVisionFrameSource_FrameDimensions_MatchEffectiveSettings()
        {
            IVisionFrameSource source = _frameSource;

            Assert.AreEqual(_frameSource.EffectiveWidth, source.FrameDimensions.Width);
            Assert.AreEqual(_frameSource.EffectiveHeight, source.FrameDimensions.Height);
        }

        [Test]
        public void IVisionFrameSource_TargetFrameRate_MatchesEffectiveFps()
        {
            IVisionFrameSource source = _frameSource;

            Assert.AreEqual(_frameSource.EffectiveFps, source.TargetFrameRate);
        }

        [Test]
        public void IVisionFrameSource_CurrentRenderTexture_IsNotNull()
        {
            IVisionFrameSource source = _frameSource;

            _frameSource.StartCapture();

            Assert.IsNotNull(source.CurrentRenderTexture);
        }

        #endregion

        #region RenderTexture Tests

        [Test]
        public void CurrentRenderTexture_HasCorrectDimensions()
        {
            _frameSource.StartCapture();
            RenderTexture rt = _frameSource.CurrentRenderTexture;

            Assert.AreEqual(_frameSource.EffectiveWidth, rt.width);
            Assert.AreEqual(_frameSource.EffectiveHeight, rt.height);
        }

        [Test]
        public void CurrentRenderTexture_IsCreated()
        {
            _frameSource.StartCapture();
            RenderTexture rt = _frameSource.CurrentRenderTexture;

            Assert.IsTrue(rt.IsCreated());
        }

        [Test]
        public void CurrentRenderTexture_HasCorrectFormat()
        {
            _frameSource.StartCapture();
            RenderTexture rt = _frameSource.CurrentRenderTexture;

            Assert.AreEqual(RenderTextureFormat.ARGB32, rt.format);
        }

        [Test]
        public void StartCapture_DiagnosticHealthProbe_IsDisabledByDefault()
        {
            FieldInfo probeEnabledField = typeof(CameraVisionFrameSource)
                .GetField("_enableDiagnosticFrameHealthProbe", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(probeEnabledField, Is.Not.Null);
            Assert.That((bool)probeEnabledField.GetValue(_frameSource), Is.False);

            _frameSource.StartCapture();

            Assert.That(GetPrivateFieldValue("_frameHealthProbeTexture"), Is.Null);
        }

        #endregion

        #region SessionErrorCodes (Vision) Tests

        [Test]
        public void SessionErrorCodes_VisionCameraLost_HasCorrectDotNotationFormat()
        {
            // Verify the new canonical dot.notation format
            Assert.AreEqual("vision.camera_lost", SessionErrorCodes.VisionCameraLost);
            // Verify category extraction works
            Assert.AreEqual("vision", SessionErrorCodes.GetCategory(SessionErrorCodes.VisionCameraLost));
        }

        [Test]
        public void SessionErrorCodes_GetDescription_ReturnsMeaningfulText()
        {
            string description = SessionErrorCodes.GetDescription(SessionErrorCodes.VisionCameraLost);

            Assert.IsFalse(string.IsNullOrEmpty(description));
            Assert.IsTrue(description.ToLower().Contains("camera"));
        }

        [Test]
        public void SessionErrorCodes_GetDescription_HandlesUnknownCode()
        {
            string description = SessionErrorCodes.GetDescription("UNKNOWN_CODE");

            Assert.IsTrue(description.Contains("UNKNOWN_CODE"));
        }

        [Test]
        public void SessionErrorCodes_IsRecoverable_IdentifiesTransientErrors()
        {
            // Recoverable errors
            Assert.IsTrue(SessionErrorCodes.IsRecoverable(SessionErrorCodes.ConnectionTimeout));
            Assert.IsTrue(SessionErrorCodes.IsRecoverable(SessionErrorCodes.VisionDeviceInitTimeout));

            // Non-recoverable errors
            Assert.IsFalse(SessionErrorCodes.IsRecoverable(SessionErrorCodes.VisionCameraLost));
            Assert.IsFalse(SessionErrorCodes.IsRecoverable(SessionErrorCodes.ConfigApiKeyMissing));
        }

        [Test]
        public void VisionCaptureStopped_SupportsErrorCode()
        {
            var evt = VisionCaptureStopped.Create(
                100,
                VisionCaptureStopReason.Error,
                errorMessage: "Camera lost",
                errorCode: SessionErrorCodes.VisionCameraLost);

            Assert.AreEqual(SessionErrorCodes.VisionCameraLost, evt.ErrorCode);
            Assert.IsTrue(evt.HasErrorCode);
            Assert.IsTrue(evt.IsError);
        }

        [Test]
        public void VisionCaptureStopped_WithoutErrorCode_HasErrorCodeIsFalse()
        {
            var evt = VisionCaptureStopped.Create(
                100);

            Assert.IsFalse(evt.HasErrorCode);
            Assert.IsNull(evt.ErrorCode);
        }

        #endregion

        #region Component Lifecycle Tests

        [Test]
        public void OnDisable_StopsCapture()
        {
            _frameSource.StartCapture();
            Assert.IsTrue(_frameSource.IsCapturing);

            MethodInfo onDisableMethod =
                typeof(CameraVisionFrameSource).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance);
            onDisableMethod.Invoke(_frameSource, null);

            Assert.IsFalse(_frameSource.IsCapturing);
        }

        [Test]
        public void OnDestroy_CleansUpRenderTargets()
        {
            _frameSource.StartCapture();
            RenderTexture rt = _frameSource.CurrentRenderTexture;
            Assert.IsNotNull(rt);

            Object.DestroyImmediate(_testGameObject);
            _testGameObject = null;
        }

        #endregion

        #region Test Render Pipeline

        private sealed class TestRenderPipelineAsset : RenderPipelineAsset
        {
            protected override RenderPipeline CreatePipeline() => new TestRenderPipeline();
        }

        private sealed class TestRenderPipeline : RenderPipeline
        {
            [Obsolete]
            protected override void Render(ScriptableRenderContext context, Camera[] cameras)
            {
            }
        }

        #endregion
    }
}
