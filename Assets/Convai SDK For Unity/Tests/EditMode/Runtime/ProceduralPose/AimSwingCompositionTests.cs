using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     The enforcement mechanism for the SDK's single aim-delta construction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two composition bugs lived in this math for as long as it existed, and they
    ///         partially cancelled each other, which is why neither was visible in review:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 The aim delta was built as <c>Y·AngleAxis(-pitch, Y·right)</c>, which is
    ///                 <c>Y²·X·Y⁻¹</c>, not the intended <c>Y·X</c> — mis-aiming by 3.7° at
    ///                 yaw 40°/pitch -15° and 14.8° at yaw 55°/pitch -32°.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Distributing that delta across a two-bone chain by scaling the yaw/pitch
    ///                 pair twice does not recompose to the whole, because yaw and pitch do not
    ///                 commute — leaving a parasitic roll of 6.3° at yaw 40°/pitch -15° and
    ///                 16.6° at the clamp corner. On the head chain that is a visible sideways
    ///                 head tilt, appearing only when yaw and pitch are both non-zero.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Fixing the split alone would have made aim error worse (0.55° → 3.69°), because
    ///         the split was masking the operand-order bug. Both properties are therefore
    ///         asserted here over the same sweep, so neither can regress alone.
    ///     </para>
    /// </remarks>
    public sealed class AimSwingCompositionTests
    {
        /// <summary>Head-chain clamp corners from the shipped profile: ±55° yaw, ±32° pitch.</summary>
        private const float MaxYaw = 55f;
        private const float MaxPitch = 32f;
        private const float Step = 5f;

        /// <summary>
        ///     Generous next to the measured 0.000000° of the corrected construction, tight
        ///     enough that either original bug fails by two orders of magnitude.
        /// </summary>
        private const float ToleranceDegrees = 0.01f;

        private static readonly Vector3 Right = Vector3.right;
        private static readonly Vector3 Up = Vector3.up;
        private static readonly Vector3 Forward = Vector3.forward;

        /// <summary>
        ///     The aim swing must be the exact inverse of yaw/pitch angle extraction: rotating
        ///     the frame's forward by the swing must land on the direction those same angles
        ///     describe.
        /// </summary>
        [Test]
        public void AimSwing_PointsForwardAtTheRequestedYawAndPitch()
        {
            ForEachAngle((yaw, pitch) =>
            {
                Vector3 achieved = ProceduralPoseMath.AimSwing(Right, Up, yaw, pitch) * Forward;
                Vector3 requested = DirectionFromYawPitch(yaw, pitch);

                Assert.That(Vector3.Angle(achieved, requested), Is.LessThan(ToleranceDegrees),
                    $"aim error at yaw {yaw}, pitch {pitch}");
            });
        }

        /// <summary>
        ///     A split must recompose to the whole exactly — same forward, and no roll about it.
        ///     Asserted at several shares because the shipped chain splits at 0.35 (neck/head)
        ///     and 0.45 (chest/upper-chest), and a future rung may pick another.
        /// </summary>
        [Test]
        public void SplitAimSwing_RecomposesWithoutAimErrorOrRoll()
        {
            foreach (float share in new[] { 0f, 0.35f, 0.45f, 0.5f, 0.65f, 1f })
            {
                ForEachAngle((yaw, pitch) =>
                {
                    Quaternion swing = ProceduralPoseMath.AimSwing(Right, Up, yaw, pitch);
                    ProceduralPoseMath.SplitAimSwing(swing, share,
                        out Quaternion first, out Quaternion second);

                    // Application order: `first` goes on the ancestor bone, `second` on the
                    // descendant, which has already inherited the ancestor's world rotation.
                    Quaternion composed = second * first;

                    Vector3 composedForward = composed * Forward;
                    Assert.That(Vector3.Angle(composedForward, swing * Forward),
                        Is.LessThan(ToleranceDegrees),
                        $"split aim error at yaw {yaw}, pitch {pitch}, share {share}");

                    Assert.That(RollBetween(composed, swing, composedForward),
                        Is.LessThan(ToleranceDegrees),
                        $"parasitic roll at yaw {yaw}, pitch {pitch}, share {share}");
                });
            }
        }

        /// <summary>
        ///     The regression guard proper: the naive split — building each part from a scaled
        ///     yaw/pitch pair — must be measurably wrong, so this suite is proven to be able to
        ///     detect the defect it exists to prevent rather than passing vacuously.
        /// </summary>
        [Test]
        public void NaiveScaledSplit_ProducesTheRollThisSuiteGuardsAgainst()
        {
            const float share = 0.35f;
            const float yaw = 40f;
            const float pitch = -15f;

            Quaternion swing = ProceduralPoseMath.AimSwing(Right, Up, yaw, pitch);
            Quaternion naiveFirst = ProceduralPoseMath.AimSwing(Right, Up, yaw * share, pitch * share);
            Quaternion naiveSecond = ProceduralPoseMath.AimSwing(
                Right, Up, yaw * (1f - share), pitch * (1f - share));
            Quaternion naive = naiveSecond * naiveFirst;

            float roll = RollBetween(naive, swing, naive * Forward);

            Assert.That(roll, Is.GreaterThan(1f),
                "the naive scaled split must still roll — otherwise these tests prove nothing");
        }

        /// <summary>
        ///     A pure yaw or a pure pitch has nothing to not-commute with, so the naive split is
        ///     correct there. This pins down that the defect is specifically the two-axis case,
        ///     which is why it only ever showed up while turning toward an off-axis target.
        /// </summary>
        [Test]
        public void SingleAxisSwings_AreUnaffectedByHowTheyAreSplit()
        {
            const float share = 0.35f;

            foreach ((float yaw, float pitch) in new[] { (40f, 0f), (0f, -15f) })
            {
                Quaternion swing = ProceduralPoseMath.AimSwing(Right, Up, yaw, pitch);
                Quaternion naive =
                    ProceduralPoseMath.AimSwing(Right, Up, yaw * (1f - share), pitch * (1f - share)) *
                    ProceduralPoseMath.AimSwing(Right, Up, yaw * share, pitch * share);

                Assert.That(Quaternion.Angle(naive, swing), Is.LessThan(ToleranceDegrees),
                    $"single-axis split diverged at yaw {yaw}, pitch {pitch}");
            }
        }

        /// <summary>A zero aim must produce no rotation at all, not a near-identity.</summary>
        [Test]
        public void ZeroAim_IsExactlyIdentity()
        {
            Assert.That(ProceduralPoseMath.AimSwing(Right, Up, 0f, 0f), Is.EqualTo(Quaternion.identity));
        }

        private static void ForEachAngle(System.Action<float, float> assertion)
        {
            for (float yaw = -MaxYaw; yaw <= MaxYaw; yaw += Step)
            for (float pitch = -MaxPitch; pitch <= MaxPitch; pitch += Step)
                assertion(yaw, pitch);
        }

        /// <summary>Yaw/pitch → direction, matching the solver's own angle convention.</summary>
        private static Vector3 DirectionFromYawPitch(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(
                Mathf.Sin(yaw) * cosPitch,
                Mathf.Sin(pitch),
                Mathf.Cos(yaw) * cosPitch);
        }

        /// <summary>
        ///     Twist between two rotations about a shared forward axis: the component a viewer
        ///     reads as a head tilt. Measured on the up vectors projected perpendicular to
        ///     <paramref name="axis" />, so a difference in aim direction cannot masquerade as
        ///     roll (aim is asserted separately).
        /// </summary>
        private static float RollBetween(Quaternion a, Quaternion b, Vector3 axis)
        {
            Vector3 upA = Vector3.ProjectOnPlane(a * Up, axis);
            Vector3 upB = Vector3.ProjectOnPlane(b * Up, axis);
            if (upA.sqrMagnitude < 1e-8f || upB.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.Angle(upA, upB);
        }
    }
}
