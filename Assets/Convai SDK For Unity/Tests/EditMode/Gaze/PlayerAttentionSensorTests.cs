using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     E8 player attention sensor: the distance-aware cone (with its close-range cap), the
    ///     asymmetric rise/fall smoothing, the hysteresis edge decision for context publishing,
    ///     and the gaze-ray source resolution order.
    /// </summary>
    public sealed class PlayerAttentionSensorTests
    {
        private sealed class FakeGazeRaySource : IPlayerGazeRaySource
        {
            private readonly Ray _ray;
            private readonly bool _available;

            public FakeGazeRaySource(Ray ray, bool available = true)
            {
                _ray = ray;
                _available = available;
            }

            public bool TryGetPlayerGazeRay(out Ray ray)
            {
                ray = _ray;
                return _available;
            }
        }

        [Test]
        public void ConeHalfAngle_WidensWhenNearAndCapsAtClose()
        {
            float far = PlayerAttentionMath.ConeHalfAngle(6f, 6f, 0.35f, 28f);
            float near = PlayerAttentionMath.ConeHalfAngle(1f, 6f, 0.35f, 28f);
            float pointBlank = PlayerAttentionMath.ConeHalfAngle(0.2f, 6f, 0.35f, 28f);

            Assert.That(far, Is.GreaterThan(6f).And.LessThan(near),
                "The cone must widen as the player gets closer (larger visual angle).");
            Assert.That(pointBlank, Is.EqualTo(28f).Within(1e-3f),
                "At point-blank range the cone must be capped so it does not swallow the whole view.");
        }

        [Test]
        public void IsLooking_TrueOnAxis_FalseOffAxis()
        {
            Vector3 head = new(0f, 1.6f, 0f);
            Vector3 origin = new(0f, 1.6f, 5f);

            var atHead = new Ray(origin, (head - origin).normalized);
            Assert.IsTrue(PlayerAttentionMath.IsLooking(atHead, head, 6f, 0.35f, 28f),
                "Aiming straight at the head must read as looking.");

            var away = new Ray(origin, Vector3.right);
            Assert.IsFalse(PlayerAttentionMath.IsLooking(away, head, 6f, 0.35f, 28f),
                "Aiming 90° away must not read as looking.");

            // ~4° off at 5 m — inside the base cone.
            var slightlyOff = new Ray(origin, Quaternion.AngleAxis(4f, Vector3.up) * (head - origin).normalized);
            Assert.IsTrue(PlayerAttentionMath.IsLooking(slightlyOff, head, 6f, 0.35f, 28f),
                "A small off-axis angle within the cone still reads as looking.");
        }

        [Test]
        public void Step_RisesFasterThanItFalls()
        {
            const float dt = 0.1f;
            float rising = PlayerAttentionMath.Step(0.5f, 1f, dt, 0.5f, 1.5f);
            float falling = PlayerAttentionMath.Step(0.5f, 0f, dt, 0.5f, 1.5f);

            Assert.That(rising - 0.5f, Is.GreaterThan(0.5f - falling),
                "Attention must build faster than it decays (rise τ < fall τ).");
        }

        [Test]
        public void ResolvePublish_UsesHysteresis()
        {
            bool looking = false;

            // Below the enter threshold: no crossing.
            Assert.IsFalse(PlayerAttentionMath.ResolvePublish(looking, 0.5f, 0.6f, 0.35f, out looking));
            Assert.IsFalse(looking);

            // Crosses to looking at the enter threshold.
            Assert.IsTrue(PlayerAttentionMath.ResolvePublish(looking, 0.65f, 0.6f, 0.35f, out looking));
            Assert.IsTrue(looking);

            // Dipping between the thresholds must NOT flip back (hysteresis).
            Assert.IsFalse(PlayerAttentionMath.ResolvePublish(looking, 0.5f, 0.6f, 0.35f, out looking));
            Assert.IsTrue(looking);

            // Only below the exit threshold does it return to away.
            Assert.IsTrue(PlayerAttentionMath.ResolvePublish(looking, 0.3f, 0.6f, 0.35f, out looking));
            Assert.IsFalse(looking);
        }

        [Test]
        public void TryResolveGazeRay_FollowsPriorityOrder()
        {
            var primary = new FakeGazeRaySource(new Ray(Vector3.one, Vector3.forward));
            var secondary = new FakeGazeRaySource(new Ray(Vector3.zero, Vector3.right));

            Assert.IsTrue(PlayerAttentionMath.TryResolveGazeRay(primary, secondary, null, out Ray ray));
            Assert.That(ray.origin, Is.EqualTo(Vector3.one), "An explicit source outranks the default.");

            // Primary unavailable (tracking lost) falls through to the secondary/default.
            var lost = new FakeGazeRaySource(default, available: false);
            Assert.IsTrue(PlayerAttentionMath.TryResolveGazeRay(lost, secondary, null, out ray));
            Assert.That(ray.direction, Is.EqualTo(Vector3.right), "A source that returns false is skipped.");

            GameObject cameraGo = new("AttentionCamera");
            try
            {
                cameraGo.transform.position = new Vector3(2f, 3f, 4f);
                cameraGo.transform.rotation = Quaternion.LookRotation(Vector3.left);
                Camera camera = cameraGo.AddComponent<Camera>();

                Assert.IsTrue(PlayerAttentionMath.TryResolveGazeRay(null, null, camera, out ray));
                Assert.That(ray.origin, Is.EqualTo(cameraGo.transform.position),
                    "With no source, the camera forward ray is the fallback.");
                Assert.That(Vector3.Distance(ray.direction, cameraGo.transform.forward), Is.LessThan(1e-4f),
                    "The fallback ray points along the camera's forward.");

                Assert.IsFalse(PlayerAttentionMath.TryResolveGazeRay(null, null, null, out _),
                    "With nothing to resolve, no ray is produced.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
            }
        }

        // ------------------------------------------------------------------ asking about other things

        [Test]
        public void TryGetPlayerGazeRay_PrefersTheSensorsOwnSource()
        {
            GameObject sensorGo = new("AttentionSensorHost");
            try
            {
                PlayerAttentionSensor sensor = sensorGo.AddComponent<PlayerAttentionSensor>();
                sensor.SetGazeRaySource(new FakeGazeRaySource(new Ray(new Vector3(1f, 2f, 3f), Vector3.forward)));

                Assert.IsTrue(sensor.TryGetPlayerGazeRay(out Ray ray));
                Assert.That(ray.origin, Is.EqualTo(new Vector3(1f, 2f, 3f)),
                    "An eye-tracking source must outrank the camera for the public reading too, or " +
                    "game code and the character disagree about where the player is looking.");
            }
            finally
            {
                Object.DestroyImmediate(sensorGo);
            }
        }

        [Test]
        public void IsPlayerLookingAt_AimsAtWhatTheSubjectDraws_NotItsPivot()
        {
            GameObject sensorGo = new("AttentionSensorHost");
            GameObject subject = new("SubjectWithFloorPivot");
            GameObject drawn = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                PlayerAttentionSensor sensor = sensorGo.AddComponent<PlayerAttentionSensor>();

                // A pivot on the floor with the body of the object well above it — the shape half
                // the props in any scene have.
                subject.transform.position = Vector3.zero;
                drawn.transform.SetParent(subject.transform, false);
                drawn.transform.localPosition = new Vector3(0f, 5f, 0f);

                var origin = new Vector3(0f, 5f, 10f);

                sensor.SetGazeRaySource(new FakeGazeRaySource(
                    new Ray(origin, (new Vector3(0f, 5f, 0f) - origin).normalized)));
                Assert.IsTrue(sensor.IsPlayerLookingAt(subject.transform),
                    "Aiming at the drawn volume must read as looking at the object.");

                sensor.SetGazeRaySource(new FakeGazeRaySource(
                    new Ray(origin, (Vector3.zero - origin).normalized)));
                Assert.IsFalse(sensor.IsPlayerLookingAt(subject.transform),
                    "Aiming at the pivot five metres below the object must not read as looking at it.");
            }
            finally
            {
                Object.DestroyImmediate(drawn);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(sensorGo);
            }
        }

        [Test]
        public void IsPlayerLookingAt_WidensWithTheSubjectsSize()
        {
            GameObject sensorGo = new("AttentionSensorHost");
            try
            {
                PlayerAttentionSensor sensor = sensorGo.AddComponent<PlayerAttentionSensor>();

                var origin = new Vector3(0f, 0f, 10f);
                var target = new Vector3(1.6f, 0f, 0f);
                sensor.SetGazeRaySource(new FakeGazeRaySource(new Ray(origin, (Vector3.zero - origin).normalized)));

                Assert.IsFalse(sensor.IsPlayerLookingAt(target, 0.1f),
                    "A small object well off the line of sight is not being looked at.");
                Assert.IsTrue(sensor.IsPlayerLookingAt(target, 3f),
                    "A large one covering the same offset is — which is why the cone is sized by " +
                    "the subject rather than fixed.");
            }
            finally
            {
                Object.DestroyImmediate(sensorGo);
            }
        }

        [Test]
        public void IsPlayerLookingAt_NullSubject_IsAnswerableRatherThanThrowing()
        {
            GameObject sensorGo = new("AttentionSensorHost");
            try
            {
                PlayerAttentionSensor sensor = sensorGo.AddComponent<PlayerAttentionSensor>();
                Assert.IsFalse(sensor.IsPlayerLookingAt(null));
            }
            finally
            {
                Object.DestroyImmediate(sensorGo);
            }
        }
    }
}
