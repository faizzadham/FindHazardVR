using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Runtime safety net: every getter must be safe even when
    ///     <see cref="ConvaiBodyAnimationConfig.ValidateForRuntime" /> was never called (tests,
    ///     direct <c>CreateInstance</c> use), and <c>ValidateForRuntime</c> itself must report
    ///     what it corrected instead of failing silently.
    /// </summary>
    internal class BodyAnimationConfigValidationTests
    {
        private ConvaiBodyAnimationConfig _config;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_config != null)
                Object.DestroyImmediate(_config);
        }

        private static void SetPrivateField(Object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"expected private field '{fieldName}' on {target.GetType()}");
            field.SetValue(target, value);
        }

        [Test]
        public void ValidateForRuntime_JogSpeedBelowWalkSpeed_IsCorrectedAndReported()
        {
            SetPrivateField(_config, "_walkSpeed", 1.2f);
            SetPrivateField(_config, "_jogSpeed", 0.8f);

            BodyAnimationConfigCorrections corrections = _config.ValidateForRuntime();

            Assert.IsTrue(corrections.HasCorrections);
            Assert.AreEqual(1.2f, _config.JogSpeed, 1e-4f);
            Assert.IsTrue(corrections.Descriptions.Exists(d => d.Contains("Jog Speed") && d.Contains("Walk Speed")));
        }

        [Test]
        public void ValidateForRuntime_Turn180BelowTurnInPlacePlusFive_IsCorrected()
        {
            // Turn 180 Min Angle has two independent floors: its own [Min(90)] bound and the
            // relational rule that it stay at least 5° above Turn In Place Min Angle. The
            // turn-in-place angle is deliberately high enough that the relational rule is the
            // binding one — at a low turn-in-place angle the absolute floor of 90 wins and this
            // test would pass without ever exercising the relationship it is named after.
            SetPrivateField(_config, "_turnInPlaceMinAngle", 100f);
            SetPrivateField(_config, "_turn180MinAngle", 95f); // above the 90 floor, below 100 + 5

            BodyAnimationConfigCorrections corrections = _config.ValidateForRuntime();

            Assert.IsTrue(corrections.HasCorrections);
            Assert.AreEqual(105f, _config.Turn180MinAngle, 1e-4f);
            Assert.IsTrue(corrections.Descriptions.Exists(d => d.Contains("Turn 180 Min Angle")));
        }

        /// <summary>The other floor: below <c>[Min(90)]</c> the absolute bound is what corrects it.</summary>
        [Test]
        public void ValidateForRuntime_Turn180BelowItsAbsoluteFloor_IsRaisedToNinety()
        {
            SetPrivateField(_config, "_turnInPlaceMinAngle", 60f);
            SetPrivateField(_config, "_turn180MinAngle", 62f);

            BodyAnimationConfigCorrections corrections = _config.ValidateForRuntime();

            Assert.IsTrue(corrections.HasCorrections);
            Assert.AreEqual(90f, _config.Turn180MinAngle, 1e-4f);
        }

        [Test]
        public void BlendCurve_NullSerializedCurve_GetterReturnsUsableZeroToOneCurve()
        {
            SetPrivateField(_config, "_blendCurve", null);

            AnimationCurve curve = _config.BlendCurve;

            Assert.IsNotNull(curve);
            Assert.GreaterOrEqual(curve.length, 2);
            Assert.AreEqual(0f, curve.Evaluate(0f), 1e-4f);
            Assert.AreEqual(1f, curve.Evaluate(1f), 1e-4f);
        }

        [Test]
        public void BlendCurve_ZeroKeyCurve_GetterReturnsUsableZeroToOneCurve()
        {
            SetPrivateField(_config, "_blendCurve", new AnimationCurve());

            AnimationCurve curve = _config.BlendCurve;

            Assert.GreaterOrEqual(curve.length, 2);
            Assert.AreEqual(0f, curve.Evaluate(0f), 1e-4f);
            Assert.AreEqual(1f, curve.Evaluate(1f), 1e-4f);
        }

        [Test]
        public void BlendCurve_OneKeyCurve_GetterReturnsUsableZeroToOneCurve()
        {
            var oneKey = new AnimationCurve();
            oneKey.AddKey(0f, 0f);
            SetPrivateField(_config, "_blendCurve", oneKey);

            AnimationCurve curve = _config.BlendCurve;

            Assert.GreaterOrEqual(curve.length, 2);
            Assert.AreEqual(0f, curve.Evaluate(0f), 1e-4f);
            Assert.AreEqual(1f, curve.Evaluate(1f), 1e-4f);
        }

        [Test]
        public void MotionHandoffNormalizedTime_OutOfRange_ClampedByGetter_WithoutValidateForRuntime()
        {
            SetPrivateField(_config, "_motionHandoffNormalizedTime", 5f); // far outside [0.5, 0.98]

            // ValidateForRuntime is deliberately never called here — the getter alone must be safe.
            float value = _config.MotionHandoffNormalizedTime;

            Assert.LessOrEqual(value, 0.98f);
            Assert.GreaterOrEqual(value, 0.5f);
        }

        private static object GetPrivateField(Object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"expected private field '{fieldName}' on {target.GetType()}");
            return field.GetValue(target);
        }

        /// <summary>
        ///     <see cref="ConvaiBodyAnimationConfig.ValidateForRuntime" /> must be read-only.
        ///     ScriptableObject field writes made during Play Mode survive exiting it, so a
        ///     mutating runtime validation would silently rewrite the customer's config asset as
        ///     a side effect of pressing Play. The getters clamp, so nothing needs repairing.
        /// </summary>
        [Test]
        public void ValidateForRuntime_DoesNotMutateTheAsset()
        {
            SetPrivateField(_config, "_walkSpeed", 1.2f);
            SetPrivateField(_config, "_jogSpeed", 0.8f);
            SetPrivateField(_config, "_motionHandoffNormalizedTime", 5f);
            SetPrivateField(_config, "_blendCurve", new AnimationCurve());

            BodyAnimationConfigCorrections corrections = _config.ValidateForRuntime();
            Assert.IsTrue(corrections.HasCorrections, "the fixture is deliberately out of range");

            Assert.AreEqual(0.8f, (float)GetPrivateField(_config, "_jogSpeed"), 1e-4f);
            Assert.AreEqual(5f, (float)GetPrivateField(_config, "_motionHandoffNormalizedTime"), 1e-4f);
            Assert.AreEqual(0, ((AnimationCurve)GetPrivateField(_config, "_blendCurve")).length);

            // …while every getter still reports a safe value.
            Assert.AreEqual(1.2f, _config.JogSpeed, 1e-4f);
            Assert.LessOrEqual(_config.MotionHandoffNormalizedTime, 0.98f);
            Assert.GreaterOrEqual(_config.BlendCurve.length, 2);
        }

        [Test]
        public void ValidateForRuntime_FullyValidConfig_ReportsZeroCorrections()
        {
            BodyAnimationConfigCorrections corrections = _config.ValidateForRuntime();

            Assert.IsFalse(corrections.HasCorrections);
            Assert.AreEqual(0, corrections.Descriptions.Count);
        }
    }
}
