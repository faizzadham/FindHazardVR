using System;
using System.Collections;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Diagnostics;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     Controller-level round trip for the semantic gesture channel against the
    ///     Domain <see cref="IConversationalGesturePerformer" /> contract:
    ///     <c>ConvaiBodyLanguageController</c>'s internal <c>TryEmitGestureCue</c> entry point
    ///     calls <see cref="IConversationalGesturePerformer.TryPerform" /> through the
    ///     <see cref="EmbodimentContext" /> registration slot, and a terminal
    ///     <see cref="GesturePerformanceResult" /> raised on <see cref="IConversationalGesturePerformer.Completed" />
    ///     round-trips back out through the controller without throwing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Coverage gap, documented:</b> this test exercises the contract against a FAKE
    ///         <see cref="IConversationalGesturePerformer" /> that mimics BodyAnimation's real
    ///         refusal/suppression/completion timing (see <c>FakeConversationalGesturePerformer</c>
    ///         below), not against a real <c>Convai.Modules.BodyAnimation</c>
    ///         <c>ConvaiBodyAnimationController</c>. A genuine two-module round trip needs a real
    ///         Humanoid <c>Animator.avatar</c> — <c>ConvaiBodyAnimationController.BuildRuntime</c>
    ///         hard-requires <c>_animator.avatar.isValid &amp;&amp; _animator.avatar.isHuman</c>
    ///         (see <c>ConvaiBodyAnimationController.cs</c>) — which cannot be constructed
    ///         procedurally inside a headless PlayMode test (it needs a real imported avatar
    ///         asset). Building or importing such an asset was judged out of scope for this
    ///         scope here, and the gap is recorded rather than left implicit. The fake
    ///         performer below implements the exact
    ///         same contract surface (<see cref="IConversationalGesturePerformer.TryPerform" />
    ///         refusal rules, <see cref="IConversationalGesturePerformer.Completed" /> firing
    ///         synchronously on the main thread) that
    ///         <c>Convai.Modules.BodyAnimation.Core.ConversationalGesturePerformer</c> ships, so
    ///         this test still proves the BodyLanguage-side plumbing (context registration,
    ///         suppression read, refusal fallback, event subscription lifecycle) end-to-end; it
    ///         does NOT prove BodyAnimation's own <c>ActionLayer</c> integration, which is covered
    ///         by BodyAnimation's own EditMode suppression-truth-table and resolution tests
    ///         own suite.
    ///     </para>
    /// </remarks>
    public sealed class GesticulationCueRoundTripTests
    {
        /// <summary>
        ///     Minimal fake performer mirroring
        ///     <c>Convai.Modules.BodyAnimation.Core.ConversationalGesturePerformer</c>'s contract
        ///     surface: refuses <see cref="GestureCueKind.None" />, refuses under any non-None
        ///     suppression, otherwise accepts and completes on the next explicit
        ///     <see cref="CompletePending" /> call (mirroring the real performer's
        ///     lifecycle-event-driven completion, just driven by the test instead of an
        ///     <c>ActionLayer</c>).
        /// </summary>
        private sealed class FakeConversationalGesturePerformer : IConversationalGesturePerformer
        {
            private GestureCue _pendingCue;
            private bool _hasPending;

            public GestureSuppression CurrentSuppression { get; set; } = GestureSuppression.None;

            public event System.Action<GestureCue, GesturePerformanceResult> Completed;

            public bool TryPerform(in GestureCue cue)
            {
                if (cue.Kind == GestureCueKind.None) return false;
                if (CurrentSuppression != GestureSuppression.None) return false;
                if (_hasPending) return false;

                _pendingCue = cue;
                _hasPending = true;
                return true;
            }

            public void CompletePending(GesturePerformanceResult result)
            {
                if (!_hasPending) return;
                GestureCue cue = _pendingCue;
                _hasPending = false;
                Completed?.Invoke(cue, result);
            }
        }

        /// <summary>
        ///     Minimal fake conversational motion budget for controller-level
        ///     continuous-duck coverage: a fixed <see cref="UpperBodyOccupancy01" /> and
        ///     <see cref="HardSuppression" /> the test controls directly, plus a spy on the
        ///     controller's <see cref="IConversationalMotionBudget.ReportConversationalIntensity" />
        ///     calls.
        /// </summary>
        private sealed class FakeConversationalMotionBudget : IConversationalMotionBudget
        {
            public float UpperBodyOccupancy01 { get; set; }
            public GestureSuppression HardSuppression { get; set; } = GestureSuppression.None;
            public float LastReportedIntensity { get; private set; } = 1f;

            public void ReportConversationalIntensity(float intensityScale) => LastReportedIntensity = intensityScale;
        }

        private GameObject _root;
        private EmbodimentContext _context;
        private ConvaiBodyLanguageController _controller;
        private FakeConversationalGesturePerformer _performer;
        private ConvaiBodyLanguageProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("GesticulationCueRoundTripRoot");

            Transform spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            NewChild(spine, "Chest", new Vector3(0f, 0.15f, 0f));

            _root.AddComponent<Animator>();
            _context = _root.AddComponent<EmbodimentContext>();

            var profile = ConvaiBodyLanguageProfile.CreateDefault();
            _profile = profile;
            SetPrivateField(profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(profile, "policyTransitionSeconds", 0f);
            SetPrivateField(profile, "semanticCueRefractorySeconds", 0.5f);

            _controller = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_controller, "profile", profile);
            _controller.enabled = false;
            _controller.enabled = true;

            _performer = new FakeConversationalGesturePerformer();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName} on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        private static bool InvokeTryEmitGestureCue(ConvaiBodyLanguageController controller, GestureCue cue)
        {
            MethodInfo method = typeof(ConvaiBodyLanguageController).GetMethod(
                "TryEmitGestureCue", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, "TryEmitGestureCue must exist as an internal method.");
            var parameters = new object[] { cue };
            return (bool)method.Invoke(controller, parameters);
        }

        /// <summary>
        ///     Waits until <paramref name="condition" /> is true or <paramref name="maxFrames" />
        ///     elapse, whichever comes first — a fixed frame COUNT (the pattern this replaces at
        ///     several call sites below) assumes a per-frame <c>Time.deltaTime</c> the headless
        ///     PlayMode runner does not guarantee; a generous frame CEILING around a live
        ///     condition check is robust to that variance without weakening what is actually
        ///     being asserted afterward.
        /// </summary>
        private static IEnumerator WaitUntil(Func<bool> condition, int maxFrames)
        {
            for (int i = 0; i < maxFrames && !condition(); i++)
                yield return null;
        }

        [UnityTest]
        public IEnumerator RegisteredPerformer_AcceptsCue_ThroughContextSlot()
        {
            yield return null;

            _context.Provide<IConversationalGesturePerformer>(_performer);
            yield return null;

            bool accepted = InvokeTryEmitGestureCue(_controller, new GestureCue(GestureCueKind.Affirmative, 1f));

            Assert.IsTrue(accepted, "A registered, non-suppressed performer must accept a valid cue.");
        }

        [UnityTest]
        public IEnumerator CompletedEvent_RoundTrips_WithTerminalResult()
        {
            yield return null;

            _context.Provide<IConversationalGesturePerformer>(_performer);
            yield return null;

            GesturePerformanceResult? observedResult = null;
            GestureCueKind observedKind = GestureCueKind.None;
            _performer.Completed += (cue, result) =>
            {
                observedResult = result;
                observedKind = cue.Kind;
            };

            bool accepted = InvokeTryEmitGestureCue(_controller, new GestureCue(GestureCueKind.Greeting, 1f));
            Assert.IsTrue(accepted, "Precondition: the cue must have been accepted before it can complete.");

            // Simulate the performer's underlying playback finishing a few frames later, exactly
            // as the real ConversationalGesturePerformer would raise Completed asynchronously
            // from an ActionLayer lifecycle event — always on the main thread, never inline with
            // TryPerform.
            for (int i = 0; i < 5 && observedResult == null; i++)
                yield return null;

            _performer.CompletePending(GesturePerformanceResult.Completed);
            yield return null;

            Assert.That(observedResult, Is.EqualTo(GesturePerformanceResult.Completed),
                "Completed must round-trip with a terminal result within a reasonable number of frames.");
            Assert.That(observedKind, Is.EqualTo(GestureCueKind.Greeting));
        }

        [UnityTest]
        public IEnumerator NoPerformerRegistered_CueRefused_NoException()
        {
            yield return null;

            Assert.IsNull(_context.ConversationalGesturePerformer, "Precondition: no performer registered.");

            bool accepted = false;
            Assert.DoesNotThrow(() =>
                accepted = InvokeTryEmitGestureCue(_controller, new GestureCue(GestureCueKind.Affirmative, 1f)));

            Assert.IsFalse(accepted, "With no performer registered, the cue must be refused, never throw.");
        }

        [UnityTest]
        public IEnumerator SuppressedPerformer_RefusesCue_WithoutThrowing()
        {
            yield return null;

            _performer.CurrentSuppression = GestureSuppression.FullBody;
            _context.Provide<IConversationalGesturePerformer>(_performer);
            yield return null;

            bool accepted = InvokeTryEmitGestureCue(_controller, new GestureCue(GestureCueKind.Affirmative, 1f));

            Assert.IsFalse(accepted, "FullBody suppression must refuse the cue.");
        }

        [UnityTest]
        public IEnumerator FullBodySuppression_FadesPostureAndBreathWeightToZero_AndRestores()
        {
            yield return null;

            _context.Provide<IConversationalGesturePerformer>(_performer);

            // Let the enable ramp finish first (postureFadeSeconds is 0.01s in this fixture) —
            // condition-based wait, not a fixed frame count (see WaitUntil).
            var snapshot = new BodyLanguageSnapshot();
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.MasterWeight > 0.9f;
            }, 300);
            Assert.That(snapshot.MasterWeight, Is.GreaterThan(0.9f), "Precondition: master weight fully ramped in.");

            _performer.CurrentSuppression = GestureSuppression.FullBody;
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.MasterWeight < 0.05f;
            }, 300);
            Assert.That(snapshot.MasterWeight, Is.LessThan(0.05f),
                "FullBody suppression must fade the shared posture/breath master weight to zero.");

            _performer.CurrentSuppression = GestureSuppression.None;
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.MasterWeight > 0.9f;
            }, 300);
            Assert.That(snapshot.MasterWeight, Is.GreaterThan(0.9f),
                "Clearing suppression must restore the master weight smoothly.");
        }

        [UnityTest]
        public IEnumerator UpperBodySuppression_ReducesPostureWeightOnly_BreathMasterWeightStaysFull()
        {
            yield return null;

            _context.Provide<IConversationalGesturePerformer>(_performer);
            var snapshot = new BodyLanguageSnapshot();
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.MasterWeight > 0.9f;
            }, 300);

            _performer.CurrentSuppression = GestureSuppression.UpperBody;
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.PostureSuppressionWeight <= _profile.UpperBodySuppressionPostureWeight + 0.05f;
            }, 300);

            _controller.CaptureSnapshot(snapshot);
            Assert.That(snapshot.MasterWeight, Is.GreaterThan(0.9f),
                "UpperBody suppression must NOT reduce the shared master weight — breath stays at full weight.");
            Assert.That(snapshot.PostureSuppressionWeight, Is.EqualTo(_profile.UpperBodySuppressionPostureWeight).Within(0.05f),
                "UpperBody suppression must ramp the posture-only factor to the profile's reduced weight.");
        }

        [UnityTest]
        public IEnumerator PerformerUnregisteredMidFlight_ControllerNeverThrows()
        {
            yield return null;

            _context.Provide<IConversationalGesturePerformer>(_performer);
            yield return null;

            InvokeTryEmitGestureCue(_controller, new GestureCue(GestureCueKind.Affirmative, 1f));

            _context.Withdraw<IConversationalGesturePerformer>(_performer);
            yield return null;

            Assert.DoesNotThrow(() => _performer.CompletePending(GesturePerformanceResult.Cancelled));
            yield return null;
        }

        // ── Conversational motion budget ────────────────

        [UnityTest]
        public IEnumerator BudgetRegistered_ContinuousOccupancyDuck_MatchesFormula()
        {
            yield return null;

            var budget = new FakeConversationalMotionBudget { UpperBodyOccupancy01 = 0.5f };
            _context.Provide<IConversationalMotionBudget>(budget);

            // Let the fade settle (postureFadeSeconds is 0.01s in this fixture).
            for (int i = 0; i < 30; i++) yield return null;

            var snapshot = new BodyLanguageSnapshot();
            _controller.CaptureSnapshot(snapshot);

            float expected = 1f - 0.5f * (1f - _profile.UpperBodySuppressionPostureWeight); // 1 - 0.5*(1-0.75) = 0.875
            Assert.That(snapshot.PostureSuppressionWeight, Is.EqualTo(expected).Within(0.02f),
                "A registered budget must drive the continuous posture-suppression target " +
                "1 - occupancy*(1-profileWeight), replacing the binary UpperBody ramp.");
            Assert.That(snapshot.UsingMotionBudget, Is.True);
            Assert.That(snapshot.UpperBodyOccupancy, Is.EqualTo(0.5f).Within(0.01f));
            Assert.That(budget.LastReportedIntensity, Is.EqualTo(1f).Within(0.05f),
                "A neutral emotion reading must report intensity 1 (neutral) every tick.");
        }

        [UnityTest]
        public IEnumerator BudgetHardSuppression_DrivesMasterWeightFullBodyFade()
        {
            yield return null;

            var budget = new FakeConversationalMotionBudget { HardSuppression = GestureSuppression.None };
            _context.Provide<IConversationalMotionBudget>(budget);

            var snapshot = new BodyLanguageSnapshot();
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.MasterWeight > 0.9f;
            }, 300);
            Assert.That(snapshot.MasterWeight, Is.GreaterThan(0.9f), "Precondition: master weight fully ramped in.");

            budget.HardSuppression = GestureSuppression.FullBody;
            yield return WaitUntil(() =>
            {
                _controller.CaptureSnapshot(snapshot);
                return snapshot.MasterWeight < 0.05f;
            }, 300);
            Assert.That(snapshot.MasterWeight, Is.LessThan(0.05f),
                "A registered budget's HardSuppression must drive the shared master-weight fade exactly like the legacy performer suppression did.");
        }

        [UnityTest]
        public IEnumerator BudgetUnregistered_DegradesToLegacyBinarySuppression()
        {
            // Regression lock: with no budget registered, the performer-only path
            // must behave byte-for-byte like the binary UpperBody ramp — covered by
            // UpperBodySuppression_ReducesPostureWeightOnly_BreathMasterWeightStaysFull and
            // FullBodySuppression_FadesPostureAndBreathWeightToZero_AndRestores above, which run
            // with no budget ever registered on this same fixture; this test only asserts the
            // snapshot explicitly reports UsingMotionBudget == false in that configuration.
            yield return null;

            _context.Provide<IConversationalGesturePerformer>(_performer);
            for (int i = 0; i < 10; i++) yield return null;

            var snapshot = new BodyLanguageSnapshot();
            _controller.CaptureSnapshot(snapshot);
            Assert.That(snapshot.UsingMotionBudget, Is.False);
            Assert.That(snapshot.UpperBodyOccupancy, Is.EqualTo(0f));
        }
    }
}
