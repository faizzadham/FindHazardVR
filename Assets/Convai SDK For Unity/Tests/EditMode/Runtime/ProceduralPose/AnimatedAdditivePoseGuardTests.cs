using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Unit-level behavior coverage for <see cref="AnimatedAdditivePoseGuard" /> (v2 plan
    ///     §3.1 port of the retired BodyLanguage-private chain write guard): dedup-on-record,
    ///     restore-on-identical, no-restore-on-external-write, and the fixed-capacity/null-bone
    ///     safety nets a shared single-writer guard depends on.
    /// </summary>
    public sealed class AnimatedAdditivePoseGuardTests
    {
        private GameObject _root;
        private Transform _spine;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Root");
            _spine = new GameObject("Spine").transform;
            _spine.SetParent(_root.transform, false);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void Record_SameBoneTwiceInOneFrame_KeepsFirstPreWrite_UpdatesPostWrite()
        {
            var guard = new AnimatedAdditivePoseGuard();
            Quaternion preA = _spine.localRotation;

            _spine.localRotation = Quaternion.Euler(5f, 0f, 0f);
            guard.Record(_spine, preA);

            // A second "writer" writes the same bone this frame with a different pre-write value.
            Quaternion preB = _spine.localRotation;
            Assert.That(preB, Is.Not.EqualTo(preA), "Sanity: the bone must have actually moved between writes.");
            _spine.localRotation = Quaternion.Euler(9f, 0f, 0f);
            guard.Record(_spine, preB);

            // Restoring must unwind to preA (the true underlying pose before ANY writer touched
            // the bone this frame), not preB (the intermediate composite).
            guard.RestoreStaleWrites();

            Assert.That(_spine.localRotation, Is.EqualTo(preA),
                "RestoreStaleWrites must unwind to the FIRST writer's pre-write value, not an intermediate one.");
        }

        [Test]
        public void RestoreStaleWrites_UntouchedBone_IsRestored()
        {
            var guard = new AnimatedAdditivePoseGuard();
            Quaternion pre = _spine.localRotation;

            _spine.localRotation = Quaternion.Euler(7f, 0f, 0f);
            guard.Record(_spine, pre);

            // Nothing re-posed the bone between the write and the restore — it must unwind.
            guard.RestoreStaleWrites();

            Assert.That(_spine.localRotation, Is.EqualTo(pre));
        }

        [Test]
        public void RestoreStaleWrites_ExternallyRePosedBone_IsLeftAlone()
        {
            var guard = new AnimatedAdditivePoseGuard();
            Quaternion pre = _spine.localRotation;

            _spine.localRotation = Quaternion.Euler(7f, 0f, 0f);
            guard.Record(_spine, pre);

            // Something else (an Animator, a clip) re-poses the bone before the next restore —
            // the guard must detect the mismatch and leave it untouched.
            var externalPose = Quaternion.Euler(30f, 15f, 0f);
            _spine.localRotation = externalPose;

            guard.RestoreStaleWrites();

            Assert.That(_spine.localRotation, Is.EqualTo(externalPose),
                "A bone re-posed by something else between writes must be left alone, not unwound.");
        }

        [Test]
        public void Record_NullBone_IsNoOp()
        {
            var guard = new AnimatedAdditivePoseGuard();

            Assert.DoesNotThrow(() => guard.Record(null, Quaternion.identity));
            Assert.DoesNotThrow(() => guard.RestoreStaleWrites());
        }

        [Test]
        public void Record_BeyondCapacity_IgnoresOverflowSafely()
        {
            // The guard's fixed capacity is 18 (the full 14-bone BodyPose set plus headroom —
            // see AnimatedAdditivePoseGuard's remarks). Recording beyond it must never throw,
            // and the overflow bones are simply never tracked (left exactly as last written,
            // not restored).
            const int capacity = 18;
            const int overflow = 3;
            var guard = new AnimatedAdditivePoseGuard();
            var bones = new Transform[capacity + overflow];
            var preWrites = new Quaternion[bones.Length];

            for (int i = 0; i < bones.Length; i++)
            {
                var go = new GameObject($"Bone{i}");
                go.transform.SetParent(_root.transform, false);
                bones[i] = go.transform;
                preWrites[i] = bones[i].localRotation;
                bones[i].localRotation = Quaternion.Euler(0f, i + 1f, 0f);
                guard.Record(bones[i], preWrites[i]);
            }

            Assert.DoesNotThrow(() => guard.RestoreStaleWrites());

            for (int i = 0; i < capacity; i++)
                Assert.That(bones[i].localRotation, Is.EqualTo(preWrites[i]),
                    $"Bone {i} is within capacity and must restore.");

            for (int i = capacity; i < bones.Length; i++)
                Assert.That(bones[i].localRotation, Is.Not.EqualTo(preWrites[i]),
                    $"Bone {i} is beyond capacity — never tracked, so it must be left untouched (not restored).");
        }
    }
}
