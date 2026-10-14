using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Core.Gestures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     T2: a scripted request (the future <c>Nod</c>/<c>PulseGesture</c> public
    ///     API's underlying <see cref="HeadGestureDirector.TryRequest(HeadGestureKind, float, out int)" />
    ///     / <see cref="GesticulationDirector.TryEmitCue" /> calls) wins over a concurrently
    ///     driven automatic co-speech beat, and clearing the override (the surgical
    ///     <see cref="HeadGestureDirector.CancelRequest" /> that <c>ClearScriptedOverrides</c>
    ///     uses — NOT <c>Reset</c>) hands control back to the automatic directors WITHOUT
    ///     disturbing an autonomous program. POCO-only, mirroring
    ///     <c>HeadGestureDirectorTests</c>/<c>GesticulationDirectorTests</c> in this folder.
    ///     Also covers the request-id sentinel/transition contract (Fix 3).
    /// </summary>
    public sealed class ScriptedOverrideArbitrationTests
    {
        [Test]
        public void ScriptedNod_OccupiesActiveSlot_AutoBeatIsQueuedBehindIt_NeverPreempts()
        {
            var director = new HeadGestureDirector();
            director.Seed(1);

            bool scriptedAccepted = director.TryRequest(HeadGestureKind.Nod, 1f, out int scriptedId);
            Assert.IsTrue(scriptedAccepted);
            Assert.That(director.ActiveRequestId, Is.EqualTo(scriptedId),
                "The scripted request must own the active slot immediately.");

            // An automatic co-speech beat arriving while the scripted Nod is active must NOT
            // preempt it — arbitration is a single active/pending queue, so the beat
            // either queues behind it or is dropped (beats never queue — see
            // HeadGestureDirector.TryRequestBeat remarks), but it never steals the active slot.
            bool beatAccepted = director.TryRequestBeat(HeadGestureKind.Nod, 0.5f);

            Assert.IsFalse(beatAccepted, "A beat must never preempt an active scripted request.");
            Assert.That(director.ActiveRequestId, Is.EqualTo(scriptedId),
                "The scripted request must still own the active slot after the beat attempt.");
            Assert.That(director.ActiveKind, Is.EqualTo(HeadGestureKind.Nod));
        }

        [Test]
        public void CancelRequest_ClearsMatchingScriptedProgram_AutomaticResumesImmediately()
        {
            // Mirrors ConvaiBodyLanguageController.ClearScriptedOverrides(): CancelRequest(id) —
            // NOT Reset() — clears only the scripted program by id.
            var director = new HeadGestureDirector();
            director.Seed(1);

            director.TryRequest(HeadGestureKind.Nod, 1f, out int scriptedId);
            Assert.IsTrue(director.IsPlaying);

            bool cancelled = director.CancelRequest(scriptedId);

            Assert.IsTrue(cancelled, "The matching scripted program must be cancelled.");
            Assert.IsFalse(director.IsPlaying, "Cancel must stop the scripted program immediately.");
            Assert.IsTrue(director.HasRequestEnded(scriptedId), "The cancelled request must report ended.");

            // Cancel arms no refractory, so an automatic beat can claim the slot on the spot.
            bool beatAcceptedAfterCancel = director.TryRequestBeat(HeadGestureKind.Nod, 0.4f);
            Assert.IsTrue(beatAcceptedAfterCancel, "Directors must resume immediately after the override clears.");
        }

        [Test]
        public void CancelRequest_LeavesAutonomousActiveProgramUntouched()
        {
            // FIX 2 regression: an autonomous program (here a co-speech beat) is active, and a
            // scripted request sits pending behind it. Cancelling the SCRIPTED id must not
            // disturb the autonomous active program — it keeps playing.
            var director = new HeadGestureDirector();
            director.Seed(1);

            bool autoAccepted = director.TryRequestBeat(HeadGestureKind.Nod, 0.5f); // autonomous, active
            Assert.IsTrue(autoAccepted);
            Assert.IsTrue(director.IsPlaying);
            int autonomousActiveId = director.ActiveRequestId;

            bool scriptedQueued = director.TryRequest(HeadGestureKind.Tilt, 1f, out int scriptedId); // pending
            Assert.IsTrue(scriptedQueued);
            Assert.That(director.PendingRequestId, Is.EqualTo(scriptedId));

            bool cancelled = director.CancelRequest(scriptedId);

            Assert.IsTrue(cancelled, "The pending scripted request must be cancelled.");
            Assert.IsTrue(director.IsPlaying, "The autonomous active beat must keep playing.");
            Assert.That(director.ActiveRequestId, Is.EqualTo(autonomousActiveId),
                "The autonomous program's id must be unchanged after the scripted cancel.");
            Assert.IsFalse(director.HasPending, "The cancelled scripted request must no longer be pending.");
            Assert.IsTrue(director.HasRequestEnded(scriptedId), "The cancelled scripted request must report ended.");
            Assert.IsFalse(director.HasRequestEnded(autonomousActiveId),
                "The autonomous program must NOT be reported ended by the scripted cancel.");
        }

        [Test]
        public void CancelRequest_NonMatchingId_IsNoOp()
        {
            var director = new HeadGestureDirector();
            director.Seed(1);

            director.TryRequest(HeadGestureKind.Nod, 1f, out int activeId);

            Assert.IsFalse(director.CancelRequest(activeId + 999), "An unknown id must be a no-op.");
            Assert.IsFalse(director.CancelRequest(0), "The 0 sentinel must be a no-op.");
            Assert.IsTrue(director.IsPlaying, "A no-op cancel must not stop the active program.");
            Assert.That(director.ActiveRequestId, Is.EqualTo(activeId));
        }

        [Test]
        public void ScriptedCue_TakesPerformerSlot_RefusalStillReportsAcceptedFalse()
        {
            // TryEmitCue itself has no separate "scripted vs automatic" caller distinction — the
            // scripted-priority contract for PulseGesture is "call it whenever you like, same as
            // the fast channel would" (see GesticulationDirector.TryEmitCue's own documented
            // refusal-fallback semantics, already covered in GesticulationDirectorTests). This
            // test locks in that a cue emitted through the semantic channel while suppressed is
            // refused deterministically, which is the behavior PulseGesture relies on to report
            // "refused/substituted" via an already-completed handle.
            var director = new GesticulationDirector();
            director.Seed(1);

            bool accepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), null, GestureSuppression.FullBody,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(accepted, "FullBody suppression must refuse the scripted cue deterministically.");
        }

        [Test]
        public void RequestId_PendingThenActiveThenEnded_TransitionsCorrectly()
        {
            // FIX 3 regression: a valid id must read "not ended" continuously from pending,
            // through the pending→active carry-forward, until the slot actually clears — never
            // mistaken as ended while tracked, and correctly ended once idle.
            const float dt = 1f / 60f;
            var director = new HeadGestureDirector();
            director.Seed(1);

            director.TryRequest(HeadGestureKind.Nod, 1f, out int firstId);   // active
            director.TryRequest(HeadGestureKind.Shake, 1f, out int queuedId); // pending

            Assert.That(queuedId, Is.GreaterThanOrEqualTo(1), "Valid ids are >= 1.");
            Assert.That(director.PendingRequestId, Is.EqualTo(queuedId));
            Assert.IsFalse(director.HasRequestEnded(queuedId), "While pending, the id must NOT be ended.");

            // Drive the first (active) program to completion + refractory so the pending Shake
            // promotes to active; the queued id must stay "not ended" across that carry-forward.
            for (int i = 0; i < 400; i++)
            {
                director.Tick(dt, 15f, 20f, 10f, refractorySeconds: 0.2f, refractoryVarianceSeconds: 0f);
                if (director.ActiveRequestId == queuedId) break;
                Assert.IsFalse(director.HasRequestEnded(queuedId),
                    "The queued id must never read ended while it is still pending or active.");
            }

            Assert.That(director.ActiveRequestId, Is.EqualTo(queuedId),
                "The pending Shake must have carried forward to the active slot with the same id.");
            Assert.IsFalse(director.HasRequestEnded(queuedId), "While active, the id must NOT be ended.");

            // Run the Shake to completion; once idle, the id must finally report ended.
            for (int i = 0; i < 400 && !director.HasRequestEnded(queuedId); i++)
                director.Tick(dt, 15f, 20f, 10f, refractorySeconds: 0.2f, refractoryVarianceSeconds: 0f);

            Assert.IsTrue(director.HasRequestEnded(queuedId), "Once the slot clears, the id must report ended.");
        }

        [Test]
        public void HasRequestEnded_ValidActiveId_IsNeverEndedWhileTracked()
        {
            var director = new HeadGestureDirector();
            director.Seed(1);

            director.TryRequest(HeadGestureKind.Tilt, 1f, out int id);

            Assert.That(id, Is.GreaterThanOrEqualTo(1));
            Assert.IsFalse(director.HasRequestEnded(id), "A tracked active id must not read ended.");
            Assert.IsTrue(director.HasRequestEnded(0), "The 0 sentinel always reads ended.");
        }
    }
}
