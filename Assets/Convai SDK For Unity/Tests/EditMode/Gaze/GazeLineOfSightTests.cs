using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazeLineOfSightTests
    {
        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private GameObject _wall;

        // Far from the scene origin so colliders left by other edit-mode fixtures can never
        // intersect these rays.
        private static readonly Vector3 Origin = new(500f, 1.6f, 500f);
        private static readonly Vector3 Target = new(500f, 1.6f, 504f);

        [TearDown]
        public void TearDown()
        {
            if (_wall != null)
            {
                Object.DestroyImmediate(_wall);
                Physics.SyncTransforms();
            }
        }

        private GameObject CreateWallBetween()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = new Vector3(500f, 1.6f, 502f);
            wall.transform.localScale = new Vector3(4f, 4f, 0.2f);
            Physics.SyncTransforms();
            return wall;
        }

        [Test]
        public void Occluded_WhenWallBetweenObserverAndTarget()
        {
            _wall = CreateWallBetween();

            bool occluded = GazeLineOfSight.Occluded(
                Origin, Target, Physics.DefaultRaycastLayers, null, null, _hits);

            Assert.IsTrue(occluded, "A wall on the line of sight must occlude the target.");
        }

        [Test]
        public void NotOccluded_WhenPathIsClear()
        {
            Physics.SyncTransforms();

            bool occluded = GazeLineOfSight.Occluded(
                Origin, Target, Physics.DefaultRaycastLayers, null, null, _hits);

            Assert.IsFalse(occluded, "With nothing between, the target is visible (identical to no-LOS behavior).");
        }

        [Test]
        public void HitsUnderEitherExcludedRoot_AreIgnored()
        {
            _wall = CreateWallBetween();

            bool viaSelfRoot = GazeLineOfSight.Occluded(
                Origin, Target, Physics.DefaultRaycastLayers, _wall.transform, null, _hits);
            Assert.IsFalse(viaSelfRoot,
                "A collider under the first excluded root (the character itself) is not an obstruction.");

            bool viaAnchorRoot = GazeLineOfSight.Occluded(
                Origin, Target, Physics.DefaultRaycastLayers, null, _wall.transform, _hits);
            Assert.IsFalse(viaAnchorRoot,
                "A collider under the second excluded root (the anchor's hierarchy) is not an obstruction.");
        }

        [Test]
        public void HitsUnderExcludedRootChild_AreIgnored()
        {
            _wall = new GameObject("SelfRoot");
            var limb = GameObject.CreatePrimitive(PrimitiveType.Cube);
            limb.transform.SetParent(_wall.transform, false);
            limb.transform.position = new Vector3(500f, 1.6f, 502f);
            limb.transform.localScale = new Vector3(4f, 4f, 0.2f);
            Physics.SyncTransforms();

            bool occluded = GazeLineOfSight.Occluded(
                Origin, Target, Physics.DefaultRaycastLayers, _wall.transform, null, _hits);

            Assert.IsFalse(occluded,
                "Exclusion covers the whole hierarchy under the root, not just the root itself.");
        }

        [Test]
        public void Occluded_MaskWithoutWallLayer_SeesNothing()
        {
            _wall = CreateWallBetween();
            int emptyMask = 0;

            bool occluded = GazeLineOfSight.Occluded(Origin, Target, emptyMask, null, null, _hits);

            Assert.IsFalse(occluded, "An obstruction mask that excludes the wall's layer never occludes.");
        }

        [Test]
        public void Visibility_DecaysSmoothly_NotInOneStep()
        {
            const float dt = 1f / 60f;
            float visibility = 1f;

            // One ~0.1 s raycast interval must not have collapsed visibility to zero.
            for (float t = 0f; t < 0.1f; t += dt)
                visibility = GazeLineOfSight.StepVisibility(visibility, 0f, dt);
            Assert.That(visibility, Is.GreaterThan(0.4f), "Visibility eases down; it does not step.");

            // Over ~0.3 s total it decays most of the way toward occluded.
            for (float t = 0f; t < 0.2f; t += dt)
                visibility = GazeLineOfSight.StepVisibility(visibility, 0f, dt);
            Assert.That(visibility, Is.LessThan(0.2f), "Visibility fully decays over ~0.3 s.");
        }

        [Test]
        public void Visibility_RecoversToward1_WhenVisibleAgain()
        {
            const float dt = 1f / 60f;
            float visibility = 0f;

            for (float t = 0f; t < 0.5f; t += dt)
                visibility = GazeLineOfSight.StepVisibility(visibility, 1f, dt);

            Assert.That(visibility, Is.GreaterThan(0.9f), "Reappearing eases visibility back toward full.");
        }
    }
}
