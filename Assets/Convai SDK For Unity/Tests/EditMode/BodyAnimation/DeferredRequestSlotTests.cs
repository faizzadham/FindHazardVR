using Convai.Modules.BodyAnimation.Core.Lifecycle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="DeferredRequestSlot" />: the payload storage, the
    ///     <see cref="DeferredRequestSlot.Clear" /> semantics (mirrors the earlier
    ///     <c>ClearDeferredRequest</c> exactly — only reference-typed fields that could otherwise
    ///     leak a stale <see cref="Object" /> are nulled), the pure expiry check, and the
    ///     description text used by both the deferred-call log line and the expiry warning.
    /// </summary>
    /// <remarks>
    ///     The identity triplet (kind/name/queued-at) is intentionally NOT part of this class —
    ///     see its own class remarks — so it is not covered here; it is covered by
    ///     <c>BodyAnimationLifecycleTests</c> against the live controller field.
    /// </remarks>
    public sealed class DeferredRequestSlotTests
    {
        [Test]
        public void Clear_NullsTargetAnchorAndAnchorOptions()
        {
            var root = new GameObject("DeferredRequestSlotTestAnchor");
            try
            {
                var slot = new DeferredRequestSlot
                {
                    Target = root.transform,
                    Anchor = root.transform,
                    AnchorOptions = null
                };

                slot.Clear();

                Assert.IsNull(slot.Target);
                Assert.IsNull(slot.Anchor);
                Assert.IsNull(slot.AnchorOptions);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Clear_LeavesValueTypePayloadFieldsUntouched()
        {
            // Mirrors the original ClearDeferredRequest exactly: the value-type payload is left
            // stale because it is only ever read when the kind that owns it is queued again,
            // which always overwrites it first — clearing it would be pure waste, not a fix.
            var slot = new DeferredRequestSlot
            {
                Position = new Vector3(1f, 2f, 3f),
                HoldSeconds = 5f
            };

            slot.Clear();

            Assert.AreEqual(new Vector3(1f, 2f, 3f), slot.Position);
            Assert.AreEqual(5f, slot.HoldSeconds);
        }

        [Test]
        public void HasExpired_WithinTimeout_IsFalse()
        {
            Assert.IsFalse(DeferredRequestSlot.HasExpired(queuedAt: 10f, now: 11f, timeoutSeconds: 2f));
        }

        [Test]
        public void HasExpired_ExactlyAtTimeout_IsFalse()
        {
            // Original expiry check was strictly ">", not ">=" — preserved exactly.
            Assert.IsFalse(DeferredRequestSlot.HasExpired(queuedAt: 10f, now: 12f, timeoutSeconds: 2f));
        }

        [Test]
        public void HasExpired_PastTimeout_IsTrue()
        {
            Assert.IsTrue(DeferredRequestSlot.HasExpired(queuedAt: 10f, now: 12.01f, timeoutSeconds: 2f));
        }

        [TestCase((int)DeferredRequestSlot.Kind.PlayAction, "wave", "PlayAction('wave')")]
        [TestCase((int)DeferredRequestSlot.Kind.PlayActionAt, "sit", "PlayActionAt('sit')")]
        [TestCase((int)DeferredRequestSlot.Kind.PointAtPosition, null, "PointAt(...)")]
        [TestCase((int)DeferredRequestSlot.Kind.PointAtTarget, null, "PointAt(...)")]
        [TestCase((int)DeferredRequestSlot.Kind.PointAtTargetOptions, null, "PointAt(...)")]
        [TestCase((int)DeferredRequestSlot.Kind.None, null, "A body animation request")]
        public void Describe_MatchesTheOriginalWording(int kindValue, string name, string expected)
        {
            var kind = (DeferredRequestSlot.Kind)kindValue;
            Assert.AreEqual(expected, DeferredRequestSlot.Describe(kind, name));
        }
    }
}
