using System.Diagnostics;
using System.IO;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Measures the emotion tick against the embodiment budget of 0.05 ms per character, and
    ///     writes the measurement out so a release pass has a number rather than an assurance.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately generous in what it asserts. A wall-clock measurement inside the editor
    ///         competes with domain reloads, the asset pipeline and whatever else the machine is
    ///         doing, so a tight assertion here would be a flaky test rather than a guard. The
    ///         threshold is set an order of magnitude above the budget: it exists to catch a change
    ///         that makes the tick structurally expensive — a per-frame allocation, an
    ///         accidental scene scan, a rebuilt lookup — not to police microseconds.
    ///     </para>
    ///     <para>
    ///         The precise, non-flaky guard for this path is
    ///         <c>EmotionReleaseFixTests.EmbodimentTick_DoesNotAllocate</c>. This test complements
    ///         it with a recorded cost.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class EmotionTickBudgetTests
    {
        private const string CharacterId = "budget-char";

        /// <summary>The embodiment budget this module is held to.</summary>
        private const double BudgetMilliseconds = 0.05d;

        /// <summary>
        ///     What the test actually fails on. Ten times the budget, for the reasons in the class
        ///     remarks.
        /// </summary>
        private const double FailThresholdMilliseconds = BudgetMilliseconds * 10d;

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(EmotionTickBudgetTests));
            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");
            _rig.Root.AddComponent<FacialBlendshapeCompositorHost>();
            _harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
        }

        [TearDown]
        public void TearDown()
        {
            // A log this fixture did not expect fails the test that produced it. The pin held
            // LogAssert.ignoreFailingMessages for the whole fixture instead, under which these
            // tests could not fail for a logging reason at all.
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        [Test]
        public void EmotionTick_StaysWithinAnOrderOfMagnitudeOfTheBudget()
        {
            const int warmupTicks = 120;
            const int measuredTicks = 2000;

            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);

                // A live character, not an idle one: an active emotion exercises the score copy,
                // the blend path, the expression planner and the micro-expression layer.
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
                for (int i = 0; i < warmupTicks; i++) _harness.Tick(1f / 60f);

                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < measuredTicks; i++) _harness.Tick(1f / 60f);
                stopwatch.Stop();

                double perTick = stopwatch.Elapsed.TotalMilliseconds / measuredTicks;
                Record(perTick);

                Assert.That(perTick, Is.LessThan(FailThresholdMilliseconds),
                    $"The emotion tick costs {perTick:0.0000} ms per character, against a budget of " +
                    $"{BudgetMilliseconds:0.00} ms. Something structural got more expensive.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     Writes the measurement next to the other test reports, so a release pass can cite a
        ///     number instead of repeating the run.
        /// </summary>
        private static void Record(double perTickMilliseconds)
        {
            string line =
                $"Emotion tick: {perTickMilliseconds:0.0000} ms/character " +
                $"(budget {BudgetMilliseconds:0.00} ms, editor wall-clock)";

            UnityEngine.Debug.Log("[EmotionTickBudget] " + line);

            try
            {
                Directory.CreateDirectory("TestReport");
                File.WriteAllText(Path.Combine("TestReport", "EmotionTickBudget.txt"), line);
            }
            catch (IOException)
            {
                // Reporting is a convenience; a locked or read-only report folder must never fail
                // the measurement itself.
            }
        }
    }
}
