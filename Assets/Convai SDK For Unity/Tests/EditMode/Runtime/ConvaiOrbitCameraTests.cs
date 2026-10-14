using System;
using System.IO;
using System.Reflection;
using Convai.Sample.Camera;
using Convai.Sample.Camera.Input;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class ConvaiOrbitCameraTests
    {
        private GameObject _cameraObject;
        private GameObject _targetObject;
        private GameObject _blockerObject;

        [TearDown]
        public void TearDown()
        {
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
            if (_targetObject != null) Object.DestroyImmediate(_targetObject);
            if (_blockerObject != null) Object.DestroyImmediate(_blockerObject);
        }

        [Test]
        public void SettingsValidate_ClampsUnsafeValues()
        {
            OrbitCameraSettings settings = OrbitCameraSettings.Default;
            settings.pitchMin = 80f;
            settings.pitchMax = -80f;
            settings.distanceMin = -5f;
            settings.distanceMax = -1f;
            settings.panClampX = -1f;
            settings.targetSmoothTime = -1f;
            settings.targetTeleportThreshold = -1f;
            settings.sensorSize = new Vector2(-1f, -1f);
            settings.focalLength = -1f;
            settings.lensSmoothTime = -1f;
            settings.screenComposition = new Vector2(2f, -2f);
            settings.idleDriftDelay = -1f;
            settings.idleYawDriftAmplitude = -1f;
            settings.idlePitchDriftAmplitude = -1f;
            settings.idleDistanceDriftAmplitude = -1f;
            settings.idleDriftFrequency = -1f;
            settings.idleDriftSecondaryFrequency = -1f;
            settings.orbitSensitivity = -1f;
            settings.zoomSensitivity = -1f;
            settings.zoomStepNear = -1f;
            settings.zoomStepFar = -2f;
            settings.panSensitivity = -1f;
            settings.collisionLayers = 0;
            settings.collisionRadius = -1f;
            settings.collisionSkin = -1f;
            settings.collisionMinimumDistance = -1f;
            settings.collisionSmoothTime = -1f;
            settings.rotationSmoothTime = -1f;
            settings.distanceSmoothTime = -1f;
            settings.panSmoothTime = -1f;

            settings.Validate();

            Assert.LessOrEqual(settings.pitchMin, settings.pitchMax);
            Assert.GreaterOrEqual(settings.distanceMin, 0.01f);
            Assert.GreaterOrEqual(settings.distanceMax, settings.distanceMin);
            Assert.GreaterOrEqual(settings.panClampX, 0f);
            Assert.GreaterOrEqual(settings.targetSmoothTime, 0f);
            Assert.GreaterOrEqual(settings.targetTeleportThreshold, 0f);
            Assert.GreaterOrEqual(settings.sensorSize.x, 1f);
            Assert.GreaterOrEqual(settings.sensorSize.y, 1f);
            Assert.GreaterOrEqual(settings.focalLength, 1f);
            Assert.GreaterOrEqual(settings.lensSmoothTime, 0f);
            Assert.That(settings.screenComposition.x, Is.InRange(-0.45f, 0.45f));
            Assert.That(settings.screenComposition.y, Is.InRange(-0.45f, 0.45f));
            Assert.GreaterOrEqual(settings.idleDriftDelay, 0f);
            Assert.GreaterOrEqual(settings.idleYawDriftAmplitude, 0f);
            Assert.GreaterOrEqual(settings.idlePitchDriftAmplitude, 0f);
            Assert.GreaterOrEqual(settings.idleDistanceDriftAmplitude, 0f);
            Assert.GreaterOrEqual(settings.idleDriftFrequency, 0.01f);
            Assert.GreaterOrEqual(settings.idleDriftSecondaryFrequency, 0.01f);
            Assert.GreaterOrEqual(settings.orbitSensitivity, 0f);
            Assert.GreaterOrEqual(settings.zoomSensitivity, 0f);
            Assert.GreaterOrEqual(settings.zoomStepNear, 0f);
            Assert.GreaterOrEqual(settings.zoomStepFar, settings.zoomStepNear);
            Assert.GreaterOrEqual(settings.panSensitivity, 0f);
            Assert.AreNotEqual(0, settings.collisionLayers.value);
            Assert.GreaterOrEqual(settings.collisionRadius, 0.01f);
            Assert.GreaterOrEqual(settings.collisionSkin, 0f);
            Assert.GreaterOrEqual(settings.collisionMinimumDistance, 0.01f);
            Assert.GreaterOrEqual(settings.collisionSmoothTime, 0f);
            Assert.GreaterOrEqual(settings.rotationSmoothTime, 0f);
            Assert.GreaterOrEqual(settings.distanceSmoothTime, 0f);
            Assert.GreaterOrEqual(settings.panSmoothTime, 0f);
        }

        [Test]
        public void ComposePose_ResolvesExpectedOrbitPosition()
        {
            OrbitMath.ComposePose(
                Vector3.zero,
                Vector3.zero,
                0f,
                0f,
                2f,
                Vector2.zero,
                out Vector3 position,
                out Quaternion rotation);

            AssertVectorApproximately(new Vector3(0f, 0f, -2f), position);
            AssertQuaternionApproximately(Quaternion.identity, rotation);
        }

        [Test]
        public void CollisionEnabled_PullsCameraInFrontOfOccluder()
        {
            ConvaiOrbitCamera camera = CreateCamera(CreateTestSettings(collisionEnabled: true));
            _blockerObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _blockerObject.name = "CameraCollisionBlocker";
            _blockerObject.transform.position = new Vector3(0f, 0f, -2f);
            _blockerObject.transform.localScale = Vector3.one;
            Physics.SyncTransforms();

            camera.SnapToTarget();

            Assert.Less(camera.transform.position.z, -0.2f);
            Assert.Greater(camera.transform.position.z, -5f);
        }

        [Test]
        public void CustomInputReader_DrivesOrbitPanAndZoom()
        {
            OrbitCameraSettings settings = CreateTestSettings(collisionEnabled: false);
            settings.orbitSensitivity = 1f;
            settings.panSensitivity = 0.1f;
            settings.zoomSensitivity = 1f;
            settings.zoomStepNear = 1f;
            settings.zoomStepFar = 1f;
            ConvaiOrbitCamera camera = CreateCamera(settings);
            var input = new TestCameraInputReader();
            camera.InputReader = input;

            input.OrbitActive = true;
            input.OrbitDelta = new Vector2(10f, -4f);
            InvokeLateUpdate(camera);
            Assert.Greater(camera.Yaw, 0f);
            Assert.Greater(camera.Pitch, 0f);

            input.ResetFrame();
            input.PanActive = true;
            input.OrbitDelta = new Vector2(3f, 2f);
            InvokeLateUpdate(camera);
            Assert.AreNotEqual(Vector2.zero, camera.PanOffset);

            float distanceBeforeZoom = camera.Distance;
            input.ResetFrame();
            input.ZoomDelta = 1f;
            InvokeLateUpdate(camera);
            Assert.Less(camera.Distance, distanceBeforeZoom);
        }

        [Test]
        public void InputBlocked_DoesNotDriveCameraState()
        {
            ConvaiOrbitCamera camera = CreateCamera(CreateTestSettings(collisionEnabled: false));
            var input = new TestCameraInputReader
            {
                OrbitActive = true,
                PanActive = true,
                OrbitDelta = new Vector2(20f, 20f),
                ZoomDelta = 1f,
                PointerOverUi = true
            };
            camera.InputReader = input;

            float yaw = camera.Yaw;
            float pitch = camera.Pitch;
            float distance = camera.Distance;
            Vector2 pan = camera.PanOffset;

            InvokeLateUpdate(camera);

            Assert.AreEqual(yaw, camera.Yaw);
            Assert.AreEqual(pitch, camera.Pitch);
            Assert.AreEqual(distance, camera.Distance);
            Assert.AreEqual(pan, camera.PanOffset);
        }

        [Test]
        public void InvalidTarget_HoldsLastKnownPivot()
        {
            ConvaiOrbitCamera camera = CreateCamera(CreateTestSettings(collisionEnabled: false));
            var target = new ToggleOrbitTarget(new Vector3(0f, 1f, 0f));
            camera.OrbitTarget = target;
            camera.ResetView();
            camera.SnapToTarget();
            Vector3 positionBeforeInvalidTarget = camera.transform.position;

            target.IsValid = false;
            InvokeLateUpdate(camera);

            AssertVectorApproximately(positionBeforeInvalidTarget, camera.transform.position);
        }

        [Test]
        public void StablePivot_IgnoresAnimatedChildTargetMotion()
        {
            OrbitCameraSettings settings = CreateTestSettings(collisionEnabled: false);
            settings.initialDistance = 2f;
            settings.distanceMax = 4f;
            settings.Validate();

            GameObject anchorObject = new GameObject("CharacterRoot");
            try
            {
                GameObject childTarget = new GameObject("AnimatedHeadTarget");
                childTarget.transform.SetParent(anchorObject.transform, false);
                childTarget.transform.localPosition = new Vector3(0f, 1.6f, 0f);

                _targetObject = anchorObject;
                _cameraObject = new GameObject("OrbitCamera");
                _cameraObject.AddComponent<UnityEngine.Camera>();
                ConvaiOrbitCamera camera = _cameraObject.AddComponent<ConvaiOrbitCamera>();
                camera.Settings = settings;
                camera.TargetTrackingMode = OrbitTargetTrackingMode.StablePivot;
                camera.TargetAnchor = anchorObject.transform;
                camera.SetTarget(childTarget.transform);
                camera.ResetView();
                camera.SnapToTarget();

                Vector3 positionBeforeChildAnimation = camera.transform.position;
                childTarget.transform.localPosition += new Vector3(0.25f, 0.1f, -0.2f);
                InvokeLateUpdate(camera);

                AssertVectorApproximately(positionBeforeChildAnimation, camera.transform.position);

                anchorObject.transform.position += Vector3.right;
                InvokeLateUpdate(camera);

                AssertVectorApproximately(positionBeforeChildAnimation + Vector3.right, camera.transform.position);
            }
            finally
            {
                if (anchorObject != null) Object.DestroyImmediate(anchorObject);
            }
        }

        [Test]
        public void TransformPosition_TracksAnimatedChildTargetMotion()
        {
            OrbitCameraSettings settings = CreateTestSettings(collisionEnabled: false);
            settings.initialDistance = 2f;
            settings.distanceMax = 4f;
            settings.Validate();

            GameObject anchorObject = new GameObject("CharacterRoot");
            try
            {
                GameObject childTarget = new GameObject("AnimatedHeadTarget");
                childTarget.transform.SetParent(anchorObject.transform, false);
                childTarget.transform.localPosition = new Vector3(0f, 1.6f, 0f);

                _targetObject = anchorObject;
                _cameraObject = new GameObject("OrbitCamera");
                _cameraObject.AddComponent<UnityEngine.Camera>();
                ConvaiOrbitCamera camera = _cameraObject.AddComponent<ConvaiOrbitCamera>();
                camera.Settings = settings;
                camera.TargetTrackingMode = OrbitTargetTrackingMode.TransformPosition;
                camera.SetTarget(childTarget.transform);
                camera.ResetView();
                camera.SnapToTarget();

                Vector3 positionBeforeChildAnimation = camera.transform.position;
                childTarget.transform.localPosition += Vector3.up * 0.2f;
                InvokeLateUpdate(camera);

                AssertVectorApproximately(positionBeforeChildAnimation + Vector3.up * 0.2f, camera.transform.position);
            }
            finally
            {
                if (anchorObject != null) Object.DestroyImmediate(anchorObject);
            }
        }

        [Test]
        public void CustomOrbitTarget_WithoutTransform_ResolvesPivot()
        {
            OrbitCameraSettings settings = CreateTestSettings(collisionEnabled: false);
            settings.initialYaw = 0f;
            settings.initialPitch = 0f;
            settings.initialDistance = 5f;
            settings.Validate();

            _cameraObject = new GameObject("OrbitCamera");
            _cameraObject.AddComponent<UnityEngine.Camera>();
            ConvaiOrbitCamera camera = _cameraObject.AddComponent<ConvaiOrbitCamera>();
            camera.Settings = settings;
            camera.OrbitTarget = new ToggleOrbitTarget(new Vector3(0f, 2f, 0f));
            camera.ResetView();
            camera.SnapToTarget();

            AssertVectorApproximately(new Vector3(0f, 2f, -5f), camera.transform.position);
        }

        [Test]
        public void LensControl_AppliesPhysicalCameraSettings()
        {
            OrbitCameraSettings settings = CreateTestSettings(collisionEnabled: false);
            settings.lensControlEnabled = true;
            settings.sensorSize = new Vector2(36f, 24f);
            settings.focalLength = 62f;
            settings.lensSmoothTime = 0f;

            ConvaiOrbitCamera orbitCamera = CreateCamera(settings);
            UnityEngine.Camera renderCamera = _cameraObject.GetComponent<UnityEngine.Camera>();

            InvokeLateUpdate(orbitCamera);

            Assert.IsTrue(renderCamera.usePhysicalProperties);
            AssertVector2Approximately(new Vector2(36f, 24f), renderCamera.sensorSize);
            Assert.That(renderCamera.focalLength, Is.EqualTo(62f).Within(0.001f));
        }

        [Test]
        public void CameraRuntime_HasNoRenderPipelineOrCinemachineDependency()
        {
            string cameraRoot = Path.Combine(
                "Packages",
                "com.convai.convai-sdk-for-unity",
                "SamplesShared",
                "Camera",
                "Runtime");

            foreach (string filePath in Directory.EnumerateFiles(cameraRoot, "*.cs", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(filePath);
                StringAssert.DoesNotContain("Cinemachine", content, filePath);
                StringAssert.DoesNotContain("Unity.RenderPipelines", content, filePath);
            }
        }



        private ConvaiOrbitCamera CreateCamera(OrbitCameraSettings settings)
        {
            _targetObject = new GameObject("OrbitTarget");
            _targetObject.transform.position = Vector3.zero;

            _cameraObject = new GameObject("OrbitCamera");
            _cameraObject.AddComponent<UnityEngine.Camera>();
            ConvaiOrbitCamera camera = _cameraObject.AddComponent<ConvaiOrbitCamera>();
            camera.Settings = settings;
            camera.SetTarget(_targetObject.transform);
            camera.ResetView();
            camera.SnapToTarget();
            return camera;
        }

        private static OrbitCameraSettings CreateTestSettings(bool collisionEnabled)
        {
            OrbitCameraSettings settings = OrbitCameraSettings.Default;
            settings.targetOffset = Vector3.zero;
            settings.initialYaw = 0f;
            settings.initialPitch = 0f;
            settings.initialDistance = 5f;
            settings.pitchMin = -80f;
            settings.pitchMax = 80f;
            settings.distanceMin = 0.2f;
            settings.distanceMax = 5f;
            settings.panClampX = 10f;
            settings.panClampY = 10f;
            settings.targetSmoothTime = 0f;
            settings.targetTeleportThreshold = 0f;
            settings.lensControlEnabled = false;
            settings.screenComposition = Vector2.zero;
            settings.idleDriftEnabled = false;
            settings.rotationSmoothTime = 0f;
            settings.distanceSmoothTime = 0f;
            settings.panSmoothTime = 0f;
            settings.collisionEnabled = collisionEnabled;
            settings.collisionLayers = Physics.DefaultRaycastLayers;
            settings.collisionRadius = 0.15f;
            settings.collisionSkin = 0.05f;
            settings.collisionMinimumDistance = 0.2f;
            settings.collisionSmoothTime = 0f;
            settings.Validate();
            return settings;
        }

        private static void InvokeLateUpdate(ConvaiOrbitCamera camera)
        {
            MethodInfo method = typeof(ConvaiOrbitCamera).GetMethod(
                "LateUpdate",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            method.Invoke(camera, Array.Empty<object>());
        }

        private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.001f));
        }

        private static void AssertQuaternionApproximately(Quaternion expected, Quaternion actual)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.001f));
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(0.001f));
        }

        private static void AssertVector2Approximately(Vector2 expected, Vector2 actual)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f));
        }

        private sealed class TestCameraInputReader : ICameraInputReader
        {
            public Vector2 OrbitDelta;
            public float ZoomDelta;
            public bool OrbitActive;
            public bool PanActive;
            public bool PointerOverUi;
            public bool TextInputFocused;

            public void ResetFrame()
            {
                OrbitDelta = Vector2.zero;
                ZoomDelta = 0f;
                OrbitActive = false;
                PanActive = false;
                PointerOverUi = false;
                TextInputFocused = false;
            }

            public void Sample() { }

            public Vector2 ReadOrbitDelta() => OrbitDelta;

            public float ReadZoomDelta() => ZoomDelta;

            public bool IsOrbitActive() => OrbitActive;

            public bool IsPanActive() => PanActive;

            public bool IsPointerOverUI() => PointerOverUi;

            public bool IsTextInputFocused() => TextInputFocused;
        }

        private sealed class ToggleOrbitTarget : IOrbitTarget
        {
            private readonly Vector3 _position;

            public ToggleOrbitTarget(Vector3 position)
            {
                _position = position;
                IsValid = true;
            }

            public bool IsValid { get; set; }

            public bool TryGetWorldPosition(out Vector3 position)
            {
                position = _position;
                return IsValid;
            }
        }
    }
}
