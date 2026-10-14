using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="ConversationAnchorResolver" />: the resolution
    ///     ladder (explicit override → Camera.main → first enabled Game-view camera), safe
    ///     fallback when an explicit anchor is destroyed, and the once-only degradation log
    ///     that <see cref="ConversationAnchorResolver.Reset" /> re-arms.
    /// </summary>
    public sealed class ConversationAnchorResolverTests
    {
        private readonly List<Object> _cleanup = new();
        private readonly List<Camera> _suppressedCameras = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                if (obj != null) Object.DestroyImmediate(obj);
            _cleanup.Clear();

            foreach (Camera camera in _suppressedCameras)
                if (camera != null) camera.enabled = true;
            _suppressedCameras.Clear();
        }

        /// <summary>Disables every currently-enabled camera in the test process so the fallback
        /// ladder has nothing to resolve to; restored in <see cref="TearDown" />.</summary>
        private void SuppressAllCameras()
        {
            foreach (Camera camera in Camera.allCameras)
            {
                if (camera == null || !camera.enabled) continue;
                camera.enabled = false;
                _suppressedCameras.Add(camera);
            }
        }

        private GameObject NewGameObject(string name)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            return go;
        }

        private Camera NewCamera(string name, Vector3 position, bool mainTag = false)
        {
            GameObject go = NewGameObject(name);
            if (mainTag) go.tag = "MainCamera";
            go.transform.position = position;
            return go.AddComponent<Camera>();
        }

        [Test]
        public void ExplicitAnchor_WinsOverCamera()
        {
            SuppressAllCameras();
            Camera camera = NewCamera("MainCamera", new Vector3(5f, 0f, 0f), mainTag: true);
            Transform explicitAnchor = NewGameObject("ExplicitAnchor").transform;
            explicitAnchor.position = new Vector3(1f, 2f, 3f);

            var resolver = new ConversationAnchorResolver();
            resolver.SetExplicitAnchor(explicitAnchor);

            bool resolved = resolver.TryResolve(null, out Vector3 position);

            Assert.IsTrue(resolved);
            Assert.AreEqual(explicitAnchor.position, position);
            Assert.AreNotEqual(camera.transform.position, position);
        }

        [Test]
        public void NoExplicitAnchor_FallsThroughToCamera()
        {
            SuppressAllCameras();
            Camera camera = NewCamera("MainCamera", new Vector3(4f, 0f, 1f), mainTag: true);

            var resolver = new ConversationAnchorResolver();

            bool resolved = resolver.TryResolve(null, out Vector3 position);

            Assert.IsTrue(resolved);
            Assert.AreEqual(camera.transform.position, position);
        }

        [Test]
        public void DestroyedExplicitAnchor_FallsBackRatherThanThrowing()
        {
            SuppressAllCameras();
            Camera camera = NewCamera("MainCamera", new Vector3(2f, 0f, 0f), mainTag: true);
            Transform explicitAnchor = NewGameObject("ExplicitAnchor").transform;

            var resolver = new ConversationAnchorResolver();
            resolver.SetExplicitAnchor(explicitAnchor);

            // Take the GameObject reference BEFORE destroying it: reading `.gameObject` off a
            // destroyed Transform throws MissingReferenceException, so the cleanup-list removal
            // has to happen against a reference captured while the object was still alive.
            GameObject anchorObject = explicitAnchor.gameObject;
            Object.DestroyImmediate(anchorObject);
            _cleanup.Remove(anchorObject);

            bool resolved = resolver.TryResolve(null, out Vector3 position);

            Assert.IsTrue(resolved, "A destroyed explicit anchor must fall back to the camera ladder, not throw.");
            Assert.AreEqual(camera.transform.position, position);
        }

        [Test]
        public void NoResolvableAnchor_DegradationLogsExactlyOnce()
        {
            SuppressAllCameras();
            var trace = new AnimTrace("ConversationAnchorResolverTests") { Verbosity = AnimTraceVerbosity.Detail };

            var resolver = new ConversationAnchorResolver();

            Assert.IsFalse(resolver.TryResolve(trace, out _));
            Assert.IsFalse(resolver.TryResolve(trace, out _));
            Assert.IsFalse(resolver.TryResolve(trace, out _));

            Assert.AreEqual(1, trace.TotalRecorded, "The degradation must log exactly once, not once per failed resolve.");
        }

        [Test]
        public void Reset_ReArmsDegradationLatch()
        {
            SuppressAllCameras();
            var trace = new AnimTrace("ConversationAnchorResolverTests") { Verbosity = AnimTraceVerbosity.Detail };

            var resolver = new ConversationAnchorResolver();

            Assert.IsFalse(resolver.TryResolve(trace, out _));
            Assert.IsFalse(resolver.TryResolve(trace, out _));
            Assert.AreEqual(1, trace.TotalRecorded);

            resolver.Reset();

            Assert.IsFalse(resolver.TryResolve(trace, out _));
            Assert.AreEqual(2, trace.TotalRecorded, "Reset() must re-arm the log-once latch.");
        }
    }
}
