using System;
using System.Reflection;
using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class BodyAnimationPublicReleaseDataTests
    {
        [Test]
        public void TalkEntry_SafeReleaseWindow_WrapsLoopPhase()
        {
            var entry = new TalkEntry();
            Set(entry, "_useSafeReleaseWindow", true);
            Set(entry, "_safeReleaseStart", 0.7f);
            Set(entry, "_safeReleaseEnd", 0.9f);

            Assert.That(entry.IsSafeReleaseTime(0.8f), Is.True);
            Assert.That(entry.IsSafeReleaseTime(1.8f), Is.True);
            Assert.That(entry.IsSafeReleaseTime(0.2f), Is.False);
        }

        [Test]
        public void MotionMetadata_NewAnalysis_WritesCurrentSchemaAndMarkers()
        {
            var metadata = new ClipMotionMetadata();
            metadata.SetAnalyzed(
                1.2f, 1f, 90f,
                AnimationCurve.Linear(0f, 0f, 1f, 1f),
                AnimationCurve.Linear(0f, 0f, 1f, 90f),
                new[] { 0.2f }, new[] { 0.7f },
                0.82f, 0.94f, MotionFootSide.Left, 0.8f, 0.9f);

            Assert.That(metadata.SchemaVersion, Is.EqualTo(ClipMotionMetadata.CurrentSchemaVersion));
            Assert.That(metadata.RecommendedHandoffNormalizedTime, Is.EqualTo(0.82f).Within(1e-5f));
            Assert.That(metadata.RecoveryNormalizedTime, Is.EqualTo(0.94f).Within(1e-5f));
            Assert.That(metadata.PrimaryPivotFoot, Is.EqualTo(MotionFootSide.Left));
            Assert.That(metadata.LoopClosureQuality, Is.EqualTo(0.9f).Within(1e-5f));
        }

        [Test]
        public void ProviderAdapter_MissingOptionalCapabilities_DegradesWithoutThrowing()
        {
            var source = new ReadOnlyLocomotionSource();
            var adapter = new LocomotionProviderAdapter(source);

            Assert.That(adapter.MoveTo(Vector3.one), Is.False);
            Assert.DoesNotThrow(() => adapter.BeginManagedMotion());
            Assert.DoesNotThrow(() => adapter.FreezeAgent(true));
            Assert.That(adapter.Speed, Is.EqualTo(1.25f));
        }

        private static void Set(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(target, value);
        }

        private sealed class ReadOnlyLocomotionSource : IConvaiLocomotionSource
        {
            public bool IsMoving => true;
            public bool PathPending => false;
            public float Speed => 1.25f;
            public float DesiredSpeed => 1.5f;
            public float RemainingDistance => 2f;
            public float SignedAngleToSteering => 10f;
            public Vector3 Destination => Vector3.one;
            public event Action<bool> MoveEnded { add { } remove { } }
        }
    }
}
