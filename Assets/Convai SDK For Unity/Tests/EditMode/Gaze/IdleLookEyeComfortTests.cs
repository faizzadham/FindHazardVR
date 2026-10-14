using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Where an idle look leaves the eyes. Eyes can reach the corner of the socket, but they
    ///     do not rest there — a gaze held near the mechanical limit is the sideways stare, and
    ///     it is the head's job to prevent it by taking more of the look.
    /// </summary>
    public sealed class IdleLookEyeComfortTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_profile != null) Object.DestroyImmediate(_profile);
            _profile = null;
        }

        /// <summary>
        ///     What the eyes are left holding once the head has taken its share. The eye stage
        ///     aims at the residual, so this is the eccentricity the character actually sits at.
        /// </summary>
        private float EyeResidual(float ambientYaw)
        {
            Vector2 head = HeadTorsoSolver.AmbientHeadShare(_profile, new Vector2(ambientYaw, 0f));
            return Mathf.Abs(ambientYaw - head.x);
        }

        [Test]
        public void AcrossTheWholeIdleRange_TheEyesNeverRestNearTheirLimit()
        {
            float limit = _profile.EyeMaxYawDegrees;
            float range = _profile.AmbientYawRangeDegrees;

            for (float yaw = -range; yaw <= range; yaw += 1f)
            {
                float residual = EyeResidual(yaw);
                Assert.That(residual, Is.LessThan(limit * 0.75f),
                    $"An idle look {yaw:0}° off centre leaves the eyes {residual:0.0}° eccentric, " +
                    $"against a {limit:0}° range — that is the sideways stare.");
            }
        }

        [Test]
        public void AWideIdleLook_RecruitsTheHeadBeyondItsFollowFraction()
        {
            // Far enough out that the plain follow fraction would leave the eyes past comfort.
            const float wide = 40f;
            Vector2 head = HeadTorsoSolver.AmbientHeadShare(_profile, new Vector2(wide, 0f));

            Assert.That(head.x, Is.GreaterThan(wide * _profile.AmbientHeadFollow),
                "The head took only its follow fraction of a look wide enough to strain the eyes.");
        }

        [Test]
        public void ANarrowIdleLook_IsStillCarriedByTheEyes()
        {
            // Small looks are an eye movement. Recruiting the head for them would read as a
            // character scanning the room with its whole skull.
            const float narrow = 8f;
            Vector2 head = HeadTorsoSolver.AmbientHeadShare(_profile, new Vector2(narrow, 0f));

            Assert.That(head.x, Is.EqualTo(narrow * _profile.AmbientHeadFollow).Within(0.01f));
            Assert.That(EyeResidual(narrow), Is.GreaterThan(narrow * 0.5f));
        }

        [Test]
        public void TheHeadShare_KeepsTheSignOfTheLook()
        {
            Assert.That(HeadTorsoSolver.AmbientHeadShare(_profile, new Vector2(40f, 0f)).x,
                Is.GreaterThan(0f));
            Assert.That(HeadTorsoSolver.AmbientHeadShare(_profile, new Vector2(-40f, 0f)).x,
                Is.LessThan(0f));
        }
    }
}
