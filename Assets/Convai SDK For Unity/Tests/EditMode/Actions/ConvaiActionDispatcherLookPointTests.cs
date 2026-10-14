using System;
using System.Collections.Generic;
using Convai.Runtime.Actions;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     EditMode coverage for <see cref="ConvaiActionDispatcher.TryResolveLookPoint" /> — the rule
    ///     that decides where a performance reactor (gaze, nod, mood) aims for a step's target.
    /// </summary>
    /// <remarks>
    ///     This pins the fix for a defect where the dispatcher reported
    ///     <c>InteractionPoint.position</c> (a place to walk to, on the floor by definition) as the
    ///     look point instead of the target's drawn volume. A character acting on a prop whose pivot
    ///     sits on the floor ducked its head down about 58 degrees exactly as it arrived.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionDispatcherLookPointTests
    {
        private List<Renderer> _scratch;

        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

        [SetUp]
        public void SetUp() => _scratch = new List<Renderer>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null &&
                    gameObject.name.StartsWith("ConvaiActionDispatcherLookPointTests_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        /// <summary>
        ///     The headline case: a target whose renderer sits well above its pivot must be aimed at
        ///     via its renderer bounds, not the pivot and not the authored interaction point. This is
        ///     the whole defect in one assertion — the resolved Y must clear the pivot's Y.
        /// </summary>
        [Test]
        public void TryResolveLookPoint_RendererAbovePivot_ResolvesToRendererBoundsCenterNotPivotOrInteractionPoint()
        {
            GameObject target = NewGameObject("Target_Box");
            target.transform.position = new Vector3(0f, 0f, 0f);

            GameObject rendererChild = NewGameObject("Target_Box_Renderer");
            rendererChild.transform.SetParent(target.transform);
            rendererChild.transform.position = new Vector3(0f, 1f, 0f);
            MeshRenderer renderer = rendererChild.AddComponent<MeshRenderer>();
            MeshFilter filter = rendererChild.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildTallBoxMesh(2f);

            GameObject interactionPointGo = NewGameObject("Target_InteractionPoint");
            interactionPointGo.transform.position = new Vector3(1.5f, 0f, 0f);

            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                target, interactionPointGo.transform, _scratch, out Vector3 lookPoint);

            Assert.That(resolved, Is.True);
            Assert.That(lookPoint, Is.EqualTo(renderer.bounds.center));
            Assert.That(lookPoint.y, Is.GreaterThan(target.transform.position.y + 0.5f),
                "The resolved point must clear the pivot's height — aiming at the pivot is the defect.");
            Assert.That(lookPoint, Is.Not.EqualTo(interactionPointGo.transform.position));
            Assert.That(lookPoint, Is.Not.EqualTo(target.transform.position));
        }

        /// <summary>
        ///     Multiple child renderers must encapsulate — the resolved point is the centre of the
        ///     combined bounds, not just the first renderer found.
        /// </summary>
        [Test]
        public void TryResolveLookPoint_MultipleChildRenderers_ResolvesToEncapsulatedBoundsCenter()
        {
            GameObject target = NewGameObject("Target_Multi");
            target.transform.position = Vector3.zero;

            GameObject childA = NewGameObject("Target_Multi_A");
            childA.transform.SetParent(target.transform);
            childA.transform.position = new Vector3(-2f, 0f, 0f);
            MeshRenderer rendererA = childA.AddComponent<MeshRenderer>();
            childA.AddComponent<MeshFilter>().sharedMesh = BuildUnitCubeMesh();

            GameObject childB = NewGameObject("Target_Multi_B");
            childB.transform.SetParent(target.transform);
            childB.transform.position = new Vector3(2f, 0f, 0f);
            MeshRenderer rendererB = childB.AddComponent<MeshRenderer>();
            childB.AddComponent<MeshFilter>().sharedMesh = BuildUnitCubeMesh();

            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                target, null, _scratch, out Vector3 lookPoint);

            Bounds expected = rendererA.bounds;
            expected.Encapsulate(rendererB.bounds);

            Assert.That(resolved, Is.True);
            Assert.That(lookPoint, Is.EqualTo(expected.center));
            Assert.That(lookPoint, Is.Not.EqualTo(rendererA.bounds.center),
                "Must not stop at the first renderer found.");
        }

        /// <summary>No renderer anywhere in the target's hierarchy: falls back to its own transform.</summary>
        [Test]
        public void TryResolveLookPoint_NoRendererOnTarget_FallsBackToTargetTransformPosition()
        {
            GameObject target = NewGameObject("Target_NoRenderer");
            target.transform.position = new Vector3(5f, 0.5f, -1f);

            GameObject interactionPointGo = NewGameObject("Target_NoRenderer_InteractionPoint");
            interactionPointGo.transform.position = new Vector3(6f, 0f, -1f);

            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                target, interactionPointGo.transform, _scratch, out Vector3 lookPoint);

            Assert.That(resolved, Is.True);
            Assert.That(lookPoint, Is.EqualTo(target.transform.position));
            Assert.That(lookPoint, Is.Not.EqualTo(interactionPointGo.transform.position));
        }

        /// <summary>No target object at all: falls back to the interaction point and returns true.</summary>
        [Test]
        public void TryResolveLookPoint_NoTargetObject_FallsBackToInteractionPoint()
        {
            GameObject interactionPointGo = NewGameObject("Target_BarePoint_InteractionPoint");
            interactionPointGo.transform.position = new Vector3(3f, 1f, 2f);

            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                null, interactionPointGo.transform, _scratch, out Vector3 lookPoint);

            Assert.That(resolved, Is.True);
            Assert.That(lookPoint, Is.EqualTo(interactionPointGo.transform.position));
        }

        /// <summary>
        ///     Neither a target object nor an interaction point: nothing to aim at, so the call must
        ///     fail rather than hand back a stale or arbitrary point for the caller to act on.
        /// </summary>
        [Test]
        public void TryResolveLookPoint_NeitherTargetNorInteractionPoint_ReturnsFalse()
        {
            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                null, null, _scratch, out Vector3 lookPoint);

            Assert.That(resolved, Is.False);
            Assert.That(lookPoint, Is.EqualTo(default(Vector3)),
                "The false path resolves to the default point; a caller must gate on the bool, " +
                "not on the value looking non-trivial.");
        }

        /// <summary>
        ///     The scratch list is caller-owned and reused across calls (once per dispatched step
        ///     forever). A successful bounds resolution must leave it empty or it leaks unbounded.
        /// </summary>
        [Test]
        public void TryResolveLookPoint_SuccessfulBoundsResolution_LeavesScratchListEmpty()
        {
            GameObject target = NewGameObject("Target_ScratchCheck");
            GameObject child = NewGameObject("Target_ScratchCheck_Renderer");
            child.transform.SetParent(target.transform);
            child.AddComponent<MeshRenderer>();
            child.AddComponent<MeshFilter>().sharedMesh = BuildUnitCubeMesh();

            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                target, null, _scratch, out _);

            Assert.That(resolved, Is.True);
            Assert.That(_scratch, Is.Empty);
        }

        /// <summary>
        ///     Documents Unity's actual behavior for the <c>GetComponentsInChildren</c> overload the
        ///     helper uses (no <c>includeInactive</c> argument, so it defaults to false): a renderer
        ///     on an inactive child must be excluded from the resolved bounds.
        /// </summary>
        [Test]
        public void TryResolveLookPoint_InactiveChildRenderer_IsExcludedFromResolvedBounds()
        {
            GameObject target = NewGameObject("Target_InactiveChild");
            target.transform.position = Vector3.zero;

            GameObject activeChild = NewGameObject("Target_InactiveChild_Active");
            activeChild.transform.SetParent(target.transform);
            activeChild.transform.position = new Vector3(0f, 0f, 0f);
            MeshRenderer activeRenderer = activeChild.AddComponent<MeshRenderer>();
            activeChild.AddComponent<MeshFilter>().sharedMesh = BuildUnitCubeMesh();

            GameObject inactiveChild = NewGameObject("Target_InactiveChild_Inactive");
            inactiveChild.transform.SetParent(target.transform);
            inactiveChild.transform.position = new Vector3(10f, 10f, 10f);
            inactiveChild.AddComponent<MeshRenderer>();
            inactiveChild.AddComponent<MeshFilter>().sharedMesh = BuildUnitCubeMesh();
            inactiveChild.SetActive(false);

            bool resolved = ConvaiActionDispatcher.TryResolveLookPoint(
                target, null, _scratch, out Vector3 lookPoint);

            Assert.That(resolved, Is.True);
            Assert.That(lookPoint, Is.EqualTo(activeRenderer.bounds.center),
                "GetComponentsInChildren without an includeInactive argument defaults to false; " +
                "if this fails, Unity's actual behavior for this overload differs from that assumption.");
        }

        private static GameObject NewGameObject(string suffix) =>
            new($"ConvaiActionDispatcherLookPointTests_{suffix}");

        private static Mesh BuildUnitCubeMesh()
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(temp);
            return mesh;
        }

        /// <summary>A box mesh <paramref name="height" /> tall, based at local Y = 0 (pivot at its base).</summary>
        private static Mesh BuildTallBoxMesh(float height)
        {
            Mesh mesh = BuildUnitCubeMesh();
            Vector3[] vertices = mesh.vertices;
            Mesh scaled = UnityEngine.Object.Instantiate(mesh);
            Vector3[] scaledVertices = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                scaledVertices[i] = new Vector3(v.x, (v.y + 0.5f) * height, v.z);
            }

            scaled.vertices = scaledVertices;
            scaled.RecalculateBounds();
            return scaled;
        }
    }
}
