using static Convai.Tests.EditMode.Fixtures.RigBindingTestAssets;
using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Runtime.Animation;
using Convai.Editor.Embodiment.Inspectors;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class StandardRigBindingTests
    {
        private GameObject _host;
        private GameObject _rigRoot;
        private Object _ownedAsset;
        private Object _ownedMesh;
        private Object _ownedMesh2;

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            if (_rigRoot != null) Object.DestroyImmediate(_rigRoot);
            if (_ownedAsset != null) Object.DestroyImmediate(_ownedAsset);
            if (_ownedMesh != null) Object.DestroyImmediate(_ownedMesh);
            if (_ownedMesh2 != null) Object.DestroyImmediate(_ownedMesh2);
        }

        [Test]
        public void Root_ReturnsAnimatorTransform_WhenRigLivesUnderWrapper()
        {
            _host = new GameObject("CharacterWrapper");
            _rigRoot = new GameObject("RigRoot");
            _rigRoot.transform.SetParent(_host.transform, false);
            _rigRoot.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _rigRoot.AddComponent<Animator>();

            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            binding.Rebuild();

            Assert.AreSame(_rigRoot.transform, binding.Root,
                "Animation systems should resolve the actual rig root, not the wrapper transform.");
        }

        [Test]
        public void Root_FallsBackToHostTransform_WhenAnimatorIsMissing()
        {
            _host = new GameObject("CharacterWrapper");

            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            binding.Rebuild();

            Assert.AreSame(_host.transform, binding.Root);
        }

        [Test]
        public void GazeAxisCalibration_IsDisabledByDefault_AndReturnsSerializedAxesWhenEnabled()
        {
            _host = new GameObject("CustomGazeRig");
            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();

            Assert.IsFalse(binding.TryGetGazeAxisCalibration(out _, out _, out _, out _),
                "Existing scenes must retain the exact legacy +Z/+Y path until calibration is explicitly enabled.");

            SetPrivateField(binding, "gazeAxisCalibrationEnabled", true);
            SetPrivateField(binding, "gazeRootForwardLocal", Vector3.left);
            SetPrivateField(binding, "gazeRootUpLocal", Vector3.up);
            SetPrivateField(binding, "leftEyeForwardLocal", Vector3.right);
            SetPrivateField(binding, "rightEyeForwardLocal", Vector3.back);

            Assert.IsTrue(binding.TryGetGazeAxisCalibration(out Vector3 rootForward, out Vector3 rootUp,
                out Vector3 leftEyeForward, out Vector3 rightEyeForward));
            Assert.AreEqual(Vector3.left, rootForward);
            Assert.AreEqual(Vector3.up, rootUp);
            Assert.AreEqual(Vector3.right, leftEyeForward);
            Assert.AreEqual(Vector3.back, rightEyeForward);
        }

        [Test]
        public void GazeAxisPreview_RefusesForeignAnimationModeOwnership()
        {
            _host = new GameObject("PreviewRig");
            StandardRigBinding binding = CreatePreviewBinding(_host);
            var inspector = (StandardRigBindingInspector)UnityEditor.Editor.CreateEditor(binding);
            try
            {
                AnimationMode.StartAnimationMode();
                InvokePrivate(inspector, "PreviewEyeDirection", binding, 15f, 0f);

                Assert.IsTrue(AnimationMode.InAnimationMode(), "Foreign preview ownership must remain intact.");
                Assert.IsFalse((bool)GetPrivateField(inspector, "_previewActive"),
                    "Gaze preview must not join or overwrite a foreign AnimationMode session.");
            }
            finally
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                Object.DestroyImmediate(inspector);
            }
        }

        [Test]
        public void GazeAxisPreview_OwnedSessionStopsAndRestoresEyePose()
        {
            _host = new GameObject("PreviewRig");
            StandardRigBinding binding = CreatePreviewBinding(_host);
            binding.TryGetBone(StandardBone.LeftEye, out Transform leftEye);
            Quaternion rest = leftEye.localRotation;
            var inspector = (StandardRigBindingInspector)UnityEditor.Editor.CreateEditor(binding);
            try
            {
                bool wasDirty = _host.scene.isDirty;
                InvokePrivate(inspector, "PreviewEyeDirection", binding, 15f, 0f);
                Assert.IsTrue(AnimationMode.InAnimationMode());
                Assert.IsTrue((bool)GetPrivateField(inspector, "_previewActive"));

                InvokePrivate(inspector, "RestorePreview");
                Assert.IsFalse(AnimationMode.InAnimationMode());
                Assert.IsFalse((bool)GetPrivateField(inspector, "_previewActive"));
                Assert.Less(Quaternion.Angle(rest, leftEye.localRotation), 0.001f);
                Assert.AreEqual(wasDirty, _host.scene.isDirty,
                    "Transient AnimationMode sampling must not leave the authoring scene dirty after cleanup.");
            }
            finally
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                Object.DestroyImmediate(inspector);
            }
        }

        private static StandardRigBinding CreatePreviewBinding(GameObject host)
        {
            Transform head = new GameObject("Head").transform;
            head.SetParent(host.transform, false);
            Transform left = new GameObject("LeftEye").transform;
            left.SetParent(head, false);
            Transform right = new GameObject("RightEye").transform;
            right.SetParent(head, false);
            StandardRigBinding binding = host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "headOverride", head);
            SetPrivateField(binding, "leftEyeOverride", left);
            SetPrivateField(binding, "rightEyeOverride", right);
            SetPrivateField(binding, "gazeAxisCalibrationEnabled", true);
            binding.Rebuild();
            return binding;
        }

        private static void InvokePrivate(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, args);

        private static object GetPrivateField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

        [Test]
        public void CustomConventionMap_ResolvesExplicitBlendshapeName()
        {
            _host = new GameObject("CustomRig");
            SkinnedMeshRenderer renderer = _host.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = CreateMeshWithBlendshape("CustomBlinkLeft");
            _ownedMesh = renderer.sharedMesh;

            var map = ScriptableObject.CreateInstance<CustomRigConventionMap>();
            _ownedAsset = map;
            SetPrivateField(map, "blendshapes", new List<CustomRigConventionMap.BlendshapeMapping>
            {
                new(StandardBlendshape.EyeBlinkLeft, "CustomBlinkLeft")
            });

            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "facialMeshes", new List<SkinnedMeshRenderer> { renderer });
            SetPrivateField(binding, "conventionOverride", RigConvention.Custom);
            SetPrivateField(binding, "customConventionMap", map);

            binding.Rebuild();

            Assert.IsTrue(binding.TryGetBlendshape(
                StandardBlendshape.EyeBlinkLeft,
                out SkinnedMeshRenderer resolvedMesh,
                out int resolvedIndex));
            Assert.AreSame(renderer, resolvedMesh);
            Assert.AreEqual(0, resolvedIndex);
        }

        [Test]
        public void Rebuild_AutoDetectsMeshes_WhenSerializedListContainsOnlyNulls()
        {
            _host = new GameObject("RigWithStaleMeshList");
            SkinnedMeshRenderer renderer = _host.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = CreateMeshWithBlendshape("JawOpen");
            _ownedMesh = renderer.sharedMesh;

            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "facialMeshes", new List<SkinnedMeshRenderer> { null, null });

            binding.Rebuild();

            Assert.AreEqual(1, binding.FacialMeshes.Count);
            Assert.AreSame(renderer, binding.FacialMeshes[0]);
        }

        [Test]
        public void Rebuild_PrioritizesFaceMesh_WhenAccessorySharesBlendshapeName()
        {
            _host = new GameObject("RigWithAccessoryBlendshapes");

            GameObject accessoryObject = new("AccessoryMesh");
            accessoryObject.transform.SetParent(_host.transform, false);
            SkinnedMeshRenderer accessory = accessoryObject.AddComponent<SkinnedMeshRenderer>();
            accessory.sharedMesh = CreateMeshWithBlendshapes("eyeBlinkLeft");
            _ownedMesh = accessory.sharedMesh;

            GameObject faceObject = new("CC_Base_Head");
            faceObject.transform.SetParent(_host.transform, false);
            SkinnedMeshRenderer face = faceObject.AddComponent<SkinnedMeshRenderer>();
            face.sharedMesh = CreateMeshWithBlendshapes("eyeBlinkLeft", "eyeBlinkRight", "jawOpen");
            _ownedMesh2 = face.sharedMesh;

            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "facialMeshes", new List<SkinnedMeshRenderer> { accessory, face });
            SetPrivateField(binding, "conventionOverride", RigConvention.ARKit);

            binding.Rebuild();

            Assert.AreSame(face, binding.FacialMeshes[0]);
            Assert.IsTrue(binding.TryGetBlendshape(
                StandardBlendshape.EyeBlinkLeft,
                out SkinnedMeshRenderer resolvedMesh,
                out _));
            Assert.AreSame(face, resolvedMesh);
        }

        [Test]
        public void ReallusionCC4Extended_ResolvesDedicatedEyelidFollowBlendshapes()
        {
            _host = new GameObject("CC4ExtendedRig");
            SkinnedMeshRenderer renderer = _host.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = CreateMeshWithBlendshapes(
                "Eye_Blink_L",
                "Eye_Blink_R",
                "Eyelash_Upper_Down_L",
                "Eyelash_Upper_Down_R",
                "Eyelash_Lower_Up_L",
                "Eyelash_Lower_Up_R");
            _ownedMesh = renderer.sharedMesh;

            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "facialMeshes", new List<SkinnedMeshRenderer> { renderer });
            SetPrivateField(binding, "conventionOverride", RigConvention.ReallusionCC4Extended);

            binding.Rebuild();

            Assert.IsTrue(binding.TryGetBlendshape(
                StandardBlendshape.EyeUpperLidDownLeft,
                out SkinnedMeshRenderer upperLidMesh,
                out int upperLidIndex));
            Assert.AreSame(renderer, upperLidMesh);
            Assert.AreEqual(2, upperLidIndex);

            Assert.IsTrue(binding.TryGetBlendshape(
                StandardBlendshape.EyeLowerLidUpRight,
                out SkinnedMeshRenderer lowerLidMesh,
                out int lowerLidIndex));
            Assert.AreSame(renderer, lowerLidMesh);
            Assert.AreEqual(5, lowerLidIndex);
        }

        [Test]
        public void ExplicitHeadOverride_WinsOverRecognizedNameFallback()
        {
            _host = new GameObject("GenericRig");
            Transform fallback = NewChild(_host.transform, "Head");
            Transform authored = NewChild(_host.transform, "CustomCranium");
            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "headOverride", authored);

            binding.Rebuild();

            Assert.IsTrue(binding.TryGetBone(StandardBone.Head, out Transform resolved));
            Assert.AreSame(authored, resolved);
            Assert.AreNotSame(fallback, resolved);
        }

        [Test]
        public void InvalidExternalOverride_IsIgnoredAndDoesNotPoisonFallbackCache()
        {
            _host = new GameObject("GenericRig");
            Transform fallback = NewChild(_host.transform, "Head");
            _rigRoot = new GameObject("OtherCharacter");
            Transform external = NewChild(_rigRoot.transform, "ExternalHead");
            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            SetPrivateField(binding, "headOverride", external);

            binding.Rebuild();

            Assert.IsTrue(binding.TryGetBone(StandardBone.Head, out Transform resolved));
            Assert.AreSame(fallback, resolved);
        }

        [Test]
        public void Rebuild_ReleasesCachedBoneAndUsesNewExplicitMapping()
        {
            _host = new GameObject("GenericRig");
            Transform fallback = NewChild(_host.transform, "Head");
            Transform authored = NewChild(_host.transform, "CustomCranium");
            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();
            binding.Rebuild();
            Assert.IsTrue(binding.TryGetBone(StandardBone.Head, out Transform first));
            Assert.AreSame(fallback, first);

            SetPrivateField(binding, "headOverride", authored);
            binding.Rebuild();

            Assert.IsTrue(binding.TryGetBone(StandardBone.Head, out Transform second));
            Assert.AreSame(authored, second);
        }

        [Test]
        public void DuplicateFallbackNames_ResolveFirstHierarchyEntryDeterministically()
        {
            _host = new GameObject("GenericRig");
            Transform first = NewChild(_host.transform, "Head");
            NewChild(_host.transform, "Head");
            StandardRigBinding binding = _host.AddComponent<StandardRigBinding>();

            binding.Rebuild();

            Assert.IsTrue(binding.TryGetBone(StandardBone.Head, out Transform resolved));
            Assert.AreSame(first, resolved);
        }

        private static Mesh CreateMeshWithBlendshape(string blendshapeName)
        {
            return CreateMeshWithBlendshapes(blendshapeName);
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            return child.transform;
        }

    }
}
