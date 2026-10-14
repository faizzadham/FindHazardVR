using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using Convai.Tests.PlayMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     A multi-character sustained-load GC soak for the live
    ///     <see cref="ConvaiBodyLanguageController" /> — the piece the EditMode POCO zero-alloc
    ///     gate (<c>BodyLanguageZeroAllocTests</c>) cannot reach, since that test measures the
    ///     Cognition-path POCOs directly and never drives an actual component through its
    ///     <see cref="IEmbodimentTickable.EmbodimentTick" /> + <c>LateUpdate</c> actuation pair.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ten characters, each with the same minimal rig + <see cref="EmbodimentContext" /> +
    ///         <see cref="ConvaiBodyLanguageController" /> harness used by this folder's other
    ///         PlayMode tests (e.g. <c>ScriptedApiControllerGlueTests</c>), all held in
    ///         <see cref="DialogueState.Speaking" /> with a synthetic oscillating speech-energy
    ///         provider so posture, breath, gesticulation, and head-beat directors are all
    ///         exercised simultaneously — the busiest everyday steady state.
    ///     </para>
    ///     <para>
    ///         Rather than waiting 10 real minutes, the simulated-duration loop calls
    ///         <see cref="IEmbodimentTickable.EmbodimentTick" /> and <c>LateUpdate</c> directly,
    ///         36000 times per character (600 simulated seconds at a nominal <c>1/60s</c> step) —
    ///         the exact tick path the scheduler drives every real frame, just called directly so
    ///         the test finishes in a few real seconds. The explicit <c>Dt</c> genuinely advances
    ///         each <c>EmbodimentTick</c> call (so Cognition-side state — policy smoothing,
    ///         gesticulation/fidget/listening timers — really does cover ~10 minutes); note
    ///         <c>LateUpdate</c>'s posture/breath spring integration reads real
    ///         <see cref="UnityEngine.Time.deltaTime" /> instead (unaffected by this loop not
    ///         yielding a frame), so its per-call step is whatever the last real frame's delta
    ///         was, not a synthetic 1/60s — immaterial for this gate's actual purpose (GC
    ///         allocation is structural, not a function of the delta magnitude), but worth noting
    ///         precisely rather than implying frame-accurate simulated actuation. A 120-frame
    ///         warm-up (via <c>yield return null</c>, letting the scheduler/controller genuinely
    ///         initialize once) runs first and is excluded from the measured window, matching the
    ///         warm-up convention in <c>BodyLanguageZeroAllocTests</c>.
    ///     </para>
    /// </remarks>
    public sealed class BodyLanguageGcSoakTests
    {
        private const int CharacterCount = 10;
        private const int WarmupFrames = 120;
        private const float Dt = 1f / 60f;
        private const int SimulatedSeconds = 600; // 10 simulated minutes per character
        private const int MeasuredTicksPerCharacter = (int)(SimulatedSeconds / Dt); // 36000

        // BodyLanguageZeroAllocTests asserts exactly 0 bytes for a single-character POCO loop.
        // This gate drives 10 live components (MonoBehaviour overhead, reflection-free direct
        // calls aside) across 10x the tick count, so a small fixed noise budget absorbs incidental
        // GC bookkeeping without masking a real per-tick leak (36000 x 10 ticks of even a
        // single 24-byte allocation would be
        // ~8.6MB, far above this threshold).
        private const long NoiseThresholdBytes = 4096;

        // Every 300 ticks (~5 simulated seconds) a scripted reaction fires for one character in
        // the roster (staggered by character index via the `(tick + c)` modulo below) — over the
        // 36000-tick measured window that is 120 reaction envelopes per character, comfortably
        // exercising the full flinch/bounce lifecycle (attack/hold/decay, refractory, retrigger)
        // many times over.
        private const int ReactionTriggerEveryNTicks = 300;

        private readonly List<GameObject> _roots = new(CharacterCount);
        private readonly List<ConvaiBodyLanguageController> _controllers = new(CharacterCount);
        private readonly List<IEmbodimentTickable> _tickables = new(CharacterCount);
        private readonly List<Action> _lateUpdateCalls = new(CharacterCount);
        private readonly List<FakeConversationFlowSource> _flowSources = new(CharacterCount);
        private readonly List<FakeSpeechEnergyProvider> _speechProviders = new(CharacterCount);

        [SetUp]
        public void SetUp()
        {
            for (int c = 0; c < CharacterCount; c++)
                SpawnCharacter(c);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in _roots)
                if (root != null)
                    Object.DestroyImmediate(root);

            _roots.Clear();
            _controllers.Clear();
            _tickables.Clear();
            _lateUpdateCalls.Clear();
            _flowSources.Clear();
            _speechProviders.Clear();
        }

        [UnityTest]
        public IEnumerator TenCharacters_TenMinuteSimulatedSoak_NoSteadyStateAllocation()
        {
            using var measurement = new AllocationByteMeasurement();
            // Enable recording before the original warm-up so lazy profiler initialization settles too.
            for (int i = 0; i < WarmupFrames; i++)
                yield return null;

            Action batch = RunSoakBatch;
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            yield return measurement.Measure(batch);
            stopwatch.Stop();
            long first = measurement.Bytes;
            stopwatch.Start();
            yield return measurement.Measure(batch);
            stopwatch.Stop();
            long second = measurement.Bytes;
            long allocatedBytes = Math.Max(first, second);
            long totalTicks = (long)CharacterCount * MeasuredTicksPerCharacter;

            Debug.Log(
                $"[BodyLanguageGcSoakTests] Two batches of {CharacterCount} characters x {MeasuredTicksPerCharacter} ticks " +
                $"({SimulatedSeconds}s simulated per batch); allocated first={first}, second={second} bytes " +
                $"(ceiling {NoiseThresholdBytes} bytes per batch). " +
                $"Measurement wall-clock including profiler readback: {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");

            Assert.That(allocatedBytes, Is.LessThanOrEqualTo(NoiseThresholdBytes),
                $"Both 10-character, 10-simulated-minute batches must stay within {NoiseThresholdBytes} bytes " +
                $"per {totalTicks} ticks (first={first}, second={second}).");
        }

        private void RunSoakBatch()
        {
            for (int tick = 0; tick < MeasuredTicksPerCharacter; tick++)
            {
                for (int c = 0; c < CharacterCount; c++)
                {
                    _speechProviders[c].Sample(Dt);
                    _tickables[c].EmbodimentTick(Dt);
                    _lateUpdateCalls[c]();

                    // Periodically exercise the scripted reaction API: with
                    // no EmotionStateSource registered on this harness the autonomous
                    // spike-detection path never fires on its own, so TriggerReaction is called
                    // directly, alternating kinds, on a cadence short enough that every character
                    // plays many full flinch/bounce envelopes over the measured window.
                    if ((tick + c) % ReactionTriggerEveryNTicks == 0)
                        _controllers[c].TriggerReaction(
                            (tick / ReactionTriggerEveryNTicks) % 2 == 0
                                ? ReactionKind.SurpriseFlinch
                                : ReactionKind.AmusementBounce,
                            0.8f);
                }
            }
        }

        private void SpawnCharacter(int index)
        {
            var root = new GameObject($"GcSoakCharacter_{index}");
            _roots.Add(root);

            Transform spine = NewChild(root.transform, "Spine", new Vector3(0f, 1f, 0f));
            Transform chest = NewChild(spine, "Chest", new Vector3(0f, 0.15f, 0f));
            Transform upperChest = NewChild(chest, "UpperChest", new Vector3(0f, 0.15f, 0f));
            Transform neck = NewChild(upperChest, "Neck", new Vector3(0f, 0.1f, 0f));
            NewChild(neck, "Head", new Vector3(0f, 0.1f, 0f));

            root.AddComponent<Animator>();
            var context = root.AddComponent<EmbodimentContext>();

            var flowSource = new FakeConversationFlowSource(DialogueState.Speaking);
            context.Provide<IConversationFlowSource>(flowSource);
            _flowSources.Add(flowSource);

            var speechProvider = new FakeSpeechEnergyProvider(seed: index + 1);
            context.Provide<ISpeechEnergyProvider>(speechProvider);
            _speechProviders.Add(speechProvider);

            var profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(profile, "policyTransitionSeconds", 0f);
            SetPrivateField(profile, "headGestureNodMaxPitchDegrees", 8f);
            SetPrivateField(profile, "headGestureRefractorySeconds", 0f);
            SetPrivateField(profile, "headGestureRefractoryVarianceSeconds", 0f);

            // Force every optional-channel
            // toggle this gate is meant to exercise explicitly on, and shorten the stance
            // director's cadence so many full weight-shift cycles (not just the default ~20s-mean
            // one) land inside the measured window — defensive against a future default flip
            // silently narrowing this gate's coverage, not just relying on today's CreateDefault().
            SetPrivateField(profile, "enableBreathAdaptiveLayering", true);
            SetPrivateField(profile, "enableHandMicro", true);
            SetPrivateField(profile, "enableReactions", true);
            SetPrivateField(profile, "enableWeightShifts", true);
            SetPrivateField(profile, "weightShiftIntervalSeconds", 2f);
            SetPrivateField(profile, "weightShiftIntervalVarianceSeconds", 0.5f);
            SetPrivateField(profile, "weightShiftTransferSeconds", 0.8f);

            var controller = root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(controller, "profile", profile);
            controller.enabled = false;
            controller.enabled = true;

            _controllers.Add(controller);
            _tickables.Add(controller);

            // Bound once here (setup, not the measured loop) as a plain delegate rather than a
            // MethodInfo invoked per-tick: Delegate.CreateDelegate can bind to a private instance
            // method, and calling the resulting delegate is a direct call with no per-call
            // reflection marshaling/boxing — MethodInfo.Invoke would itself allocate and pollute
            // the measurement of the system under test.
            MethodInfo lateUpdate = typeof(ConvaiBodyLanguageController).GetMethod(
                "LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(lateUpdate, "ConvaiBodyLanguageController must expose a private LateUpdate method.");
            _lateUpdateCalls.Add((Action)Delegate.CreateDelegate(typeof(Action), controller, lateUpdate));
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

        /// <summary>Fixed-state conversation flow source: never transitions, no allocation.</summary>
        private sealed class FakeConversationFlowSource : IConversationFlowSource
        {
            private readonly DialogueStateReading _reading;

            public FakeConversationFlowSource(DialogueState state)
            {
                _reading = new DialogueStateReading(state, state, 0f, 0f, 1f);
            }

            public DialogueStateReading Current => _reading;

            public event System.Action<DialogueStateReading> Changed
            {
                add { }
                remove { }
            }
        }

        /// <summary>Deterministic oscillating speech energy so gesticulation/beat paths engage.</summary>
        private sealed class FakeSpeechEnergyProvider : ISpeechEnergyProvider
        {
            private readonly float _seedOffset;
            private float _time;

            public FakeSpeechEnergyProvider(int seed)
            {
                _seedOffset = seed * 0.37f;
            }

            public float Current { get; private set; }

            public void Sample(float deltaTime)
            {
                _time += deltaTime;
                Current = 0.5f + 0.5f * Mathf.Sin((_time + _seedOffset) * 3.1f);
            }
        }
    }
}
