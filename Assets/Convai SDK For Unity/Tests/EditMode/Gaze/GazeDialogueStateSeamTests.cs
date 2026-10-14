using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The dialogue-state seam, driven end to end through the stages the controller composes:
    ///     source → <see cref="DialogueStateDebounce" /> → state policy →
    ///     <see cref="GazePolicyEngine" /> → the actuator chain, with
    ///     <see cref="TurnTakingDirector" /> reading the same state alongside.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The input is the one measured in play mode: at every utterance end the state handed
    ///         to gaze alternates <see cref="DialogueState.Speaking" /> and
    ///         <see cref="DialogueState.Settling" /> on consecutive frames, for 0.2-0.3 s normally
    ///         and for seconds at a time during a long answer. Read raw, each of those edges is a
    ///         real instruction: the two rows of the state table differ in engagement (1 vs 0.6),
    ///         head participation (0.85 vs 0.6), aversion mode (None vs Natural) and whether the
    ///         body may turn — so the character re-plans its head twice a frame and flips its
    ///         aversion cadence on and off while it is supposed to be coming to rest.
    ///     </para>
    ///     <para>
    ///         The character is already looking at the person it is talking to and nothing about
    ///         the target changes for the whole run, so there is nothing legitimate for the head
    ///         to do. Every degree it moves during the flicker window is the defect.
    ///     </para>
    ///     <para>
    ///         <see cref="NegativeControl_ReadRaw_TheSameSquareWaveBreaksTheSameInvariants" /> is
    ///         not decoration: it runs the identical scenario with the seam bypassed and requires
    ///         the invariants to FAIL, so a green pass above is evidence that the seam works and
    ///         not that the scenario was too gentle to disturb anything.
    ///     </para>
    /// </remarks>
    public sealed class GazeDialogueStateSeamTests
    {
        private const float Dt = GazeShiftTraceHarness.FrameSeconds;

        /// <summary>Long enough for a 30 degree look to be fully settled before the flicker starts.</summary>
        private const float WarmupSeconds = 1.5f;

        /// <summary>Two seconds of alternation — an order of magnitude past the measured boundary.</summary>
        private const float FlickerSeconds = 2f;

        /// <summary>
        ///     Where the person being talked to is. Far enough round that the head owns most of the
        ///     look, so a change in its share of the shift is a change a viewer would see.
        /// </summary>
        private const float TargetYawDegrees = 30f;

        /// <summary>
        ///     The most the applied head pose may move in one frame while nothing about the look
        ///     has changed: 0.05 degrees, i.e. 3 deg/s at the harness's 60 Hz — a head that is not
        ///     moving.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Deliberately far tighter than the module's continuity bound. The obvious bound
        ///         for "the head jumped" is a few degrees per frame, and it does not bite here:
        ///         measured, the raw arm's worst step is 0.337 deg (20 deg/s), so a 3 deg bound
        ///         passes the defect with a 9x margin. That is not a flaw in the defect, it is the
        ///         blind spot <see cref="GazeSteadyLookTests" /> was written for — the actuator
        ///         lane turns a stepping goal into perfectly well-shaped small motion, so a
        ///         smoothness bound cannot see a decision that should never have been taken.
        ///     </para>
        ///     <para>
        ///         The claim being made here is not smoothness, it is stillness: the character is
        ///         holding a settled look at a target that never moves, so the correct amount of
        ///         head motion is none. Through the seam the measured step is below 1e-4 deg, so
        ///         this bound sits 500x above the passing case and 7x below the failing one.
        ///     </para>
        /// </remarks>
        private const float MaxAppliedHeadStepDegrees = 0.05f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        /// <summary>What one run of the square wave produced, measured over the flicker window only.</summary>
        private sealed class SeamRun
        {
            public GazeAversionMode FirstAversionMode;
            public int AversionModeChanges;
            public int TurnTakingSpeakingExits;
            public GazeAppliedHeadStep WorstHeadStep;

            public override string ToString() =>
                $"aversion mode changes={AversionModeChanges} (from {FirstAversionMode}), " +
                $"turn-taking Speaking exits={TurnTakingSpeakingExits}, {WorstHeadStep}";
        }

        private static GazeTargetDecision FullPlayerDecision(Vector3 point) => new()
        {
            Kind = GazeTargetKind.Player,
            SmoothedPoint = point,
            Commitment = 1f,
            GenerationId = 1,
            Name = "Player",
            Nature = GazeLookNature.Attention,
            TargetHasFace = true,
            ScriptedEngagementOverride = -1f,
            ScriptedHeadContributionOverride = -1f
        };

        /// <summary>
        ///     Runs the scenario. <paramref name="throughTheSeam" /> false reproduces the shipped
        ///     defect exactly: every stage acts on the raw state, as the controller used to.
        /// </summary>
        private SeamRun RunSquareWave(bool throughTheSeam)
        {
            var debounce = new DialogueStateDebounce();
            var policyEngine = new GazePolicyEngine();
            var turnTaking = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(9001u);
            var run = new SeamRun();

            using var harness = new GazeShiftTraceHarness(_profile);

            Vector3 origin = harness.EyeCenter;
            Vector3 target = origin +
                             Quaternion.AngleAxis(TargetYawDegrees, harness.Root.up) *
                             (harness.Root.forward * 2f);
            GazeTargetDecision decision = FullPlayerDecision(target);

            int warmupFrames = Mathf.CeilToInt(WarmupSeconds / Dt);
            int totalFrames = warmupFrames + Mathf.CeilToInt(FlickerSeconds / Dt);
            bool sampledFirst = false;

            for (int i = 0; i < totalFrames; i++)
            {
                bool flickering = i >= warmupFrames;
                DialogueState raw = flickering && i % 2 == 1
                    ? DialogueState.Settling
                    : DialogueState.Speaking;
                DialogueState state = throughTheSeam ? debounce.Tick(raw, Dt) : raw;

                GazeStatePolicy policy = _profile.GetStatePolicy(state);
                GazeDirective directive = policyEngine.Tick(in policy, in decision, _profile, Dt);
                turnTaking.Tick(
                    state, _profile, eyeContactLocked: false, finalTranscriptReceived: false,
                    finalTranscriptWordCount: 0, hasSpeechActivitySignal: false, speechActive: false,
                    speechEnergy: 0f, deltaTime: Dt, random: ref random);

                if (flickering)
                {
                    if (!sampledFirst)
                    {
                        run.FirstAversionMode = directive.AversionMode;
                        sampledFirst = true;
                    }
                    else if (directive.AversionMode != run.FirstAversionMode)
                    {
                        run.AversionModeChanges++;
                    }

                    // The director drops its suppression factor to 0 for as long as Speaking owns
                    // the aversion cadence and restores it to 1 the moment it sees anything else,
                    // so this counts exactly the Speaking exits it registered.
                    if (turnTaking.AversionSuppressionFactor > 0.5f) run.TurnTakingSpeakingExits++;
                }

                // The ladder is handed the settled strength and the head's share, which is what
                // the controller hands it — see ConvaiGazeController's shift block.
                harness.Step(target, directive.SettledEngagement, directive.HeadContribution);
            }

            run.WorstHeadStep = harness.LargestAppliedHeadStep(WarmupSeconds);
            return run;
        }

        [Test]
        public void SquareWaveThroughTheSeam_TheHeadTheAversionModeAndTheTurnAllHoldStill()
        {
            SeamRun run = RunSquareWave(throughTheSeam: true);

            Assert.That(run.AversionModeChanges, Is.Zero,
                $"The aversion cadence changed while nothing changed. {run}");
            Assert.That(run.TurnTakingSpeakingExits, Is.Zero,
                $"Turn-taking saw the character stop speaking — every one of those edges retires " +
                $"an utterance and re-plans the next. {run}");
            Assert.That(run.WorstHeadStep.Degrees, Is.LessThanOrEqualTo(MaxAppliedHeadStepDegrees),
                $"The head moved while the character was holding a settled look at a target that " +
                $"never moved. {run}");
        }

        [Test]
        public void NegativeControl_ReadRaw_TheSameSquareWaveBreaksTheSameInvariants()
        {
            SeamRun run = RunSquareWave(throughTheSeam: false);

            Assert.That(run.AversionModeChanges, Is.GreaterThan(0),
                $"The scenario no longer reproduces the defect it was written for, so the green " +
                $"case above proves nothing. {run}");
            Assert.That(run.TurnTakingSpeakingExits, Is.GreaterThan(0),
                $"Turn-taking should register a Speaking exit on every Settling frame here. {run}");
            Assert.That(run.WorstHeadStep.Degrees, Is.GreaterThan(MaxAppliedHeadStepDegrees),
                $"The raw arm no longer disturbs the head at all, so the stillness assertion " +
                $"above is measuring nothing. {run}");
        }
    }
}
