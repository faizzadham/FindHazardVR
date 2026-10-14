using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Gestures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     T1: scripted-API handle lifecycle. Exercises
    ///     <see cref="HeadGestureHandle" /> against a bare <see cref="HeadGestureDirector" /> —
    ///     the same POCO-only style as the rest of this test folder (no MonoBehaviour) — plus
    ///     the handle's own idempotence guarantees, which need no director at all.
    /// </summary>
    public sealed class ScriptedApiHandleLifecycleTests
    {
        [Test]
        public void AcceptedRequest_HandleIsActive_AndCompletesWhenProgramEnds()
        {
            var director = new HeadGestureDirector();
            director.Seed(1);

            bool accepted = director.TryRequest(HeadGestureKind.Nod, 1f, out int requestId);
            Assert.IsTrue(accepted, "An idle director must accept the first request.");

            var handle = new HeadGestureHandle(null, requestId, HeadGestureKind.Nod);
            Assert.IsTrue(handle.IsActive);
            Assert.IsFalse(handle.Completion.IsCompleted);

            // Drive the program to completion (1.15s Nod duration as of ; see
            // HeadGestureDirector.DurationFor). 120 ticks at 1/60s = 2s, comfortably covering the
            // duration plus refractory.
            const float dt = 1f / 60f;
            for (int i = 0; i < 120 && !director.HasRequestEnded(requestId); i++)
                director.Tick(dt, 15f, 20f, 10f, refractorySeconds: 0.2f, refractoryVarianceSeconds: 0f);

            Assert.IsTrue(director.HasRequestEnded(requestId), "Sanity: the program must have ended within 2 seconds.");

            // The controller's ProcessHeadGestureHandles seam would call this on the tick that
            // observes HasRequestEnded — reproduced directly here since this is a POCO test.
            handle.MarkCompleted();

            Assert.IsTrue(handle.Completion.IsCompleted);
            Assert.IsFalse(handle.IsActive);
        }

        [Test]
        public void RefusedRequest_HandleIsAlreadyCompleted_AndInactive()
        {
            var director = new HeadGestureDirector();
            director.Seed(1);

            // Occupy both the active AND pending slots so a third request is refused outright.
            Assert.IsTrue(director.TryRequest(HeadGestureKind.Nod, 1f, out _));
            Assert.IsTrue(director.TryRequest(HeadGestureKind.Shake, 1f, out _));

            bool acceptedThird = director.TryRequest(HeadGestureKind.Tilt, 1f, out int thirdRequestId);
            Assert.IsFalse(acceptedThird, "Both slots occupied — a third request must be refused.");
            Assert.That(thirdRequestId, Is.EqualTo(0), "A refused request's id must be the 0 sentinel.");

            var handle = new HeadGestureHandle(null, thirdRequestId, HeadGestureKind.Tilt);
            if (!acceptedThird)
                handle.MarkCompleted();

            Assert.IsFalse(handle.IsActive);
            Assert.IsTrue(handle.Completion.IsCompleted);
        }

        [Test]
        public void DoubleRelease_NeverThrows_AndCompletionResolvesExactlyOnce()
        {
            var handle = new HeadGestureHandle(null, 1, HeadGestureKind.Nod);

            Assert.DoesNotThrow(() => handle.Release());
            Assert.DoesNotThrow(() => handle.Release());
            Assert.DoesNotThrow(() => handle.Release());

            // Release() with a null owner is a documented no-op (never throws), so completion
            // must be driven explicitly here to assert the "exactly once" contract on
            // MarkCompleted itself — TrySetResult must tolerate repeat calls without faulting.
            Assert.DoesNotThrow(() => handle.MarkCompleted());
            Assert.DoesNotThrow(() => handle.MarkCompleted());
            Assert.IsTrue(handle.Completion.IsCompleted);
            Assert.IsFalse(handle.Completion.IsFaulted);
        }

        [Test]
        public void GestureCueHandle_RefusedRequest_HandleIsAlreadyCompleted()
        {
            var handle = new GestureCueHandle(null, 0, GestureCueKind.None);
            handle.MarkCompleted();

            Assert.IsFalse(handle.IsActive);
            Assert.IsTrue(handle.Completion.IsCompleted);
            Assert.DoesNotThrow(() => handle.Release());
        }

        [Test]
        public void GestureCueHandle_DoubleMarkCompleted_NeverThrows()
        {
            var handle = new GestureCueHandle(null, 1, GestureCueKind.Affirmative);

            Assert.DoesNotThrow(() => handle.MarkCompleted());
            Assert.DoesNotThrow(() => handle.MarkCompleted());
            Assert.IsTrue(handle.Completion.IsCompleted);
        }
    }
}
