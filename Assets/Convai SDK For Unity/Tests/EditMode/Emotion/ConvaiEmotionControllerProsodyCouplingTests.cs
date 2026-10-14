using System.Reflection;
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
using Convai.Domain.Embodiment.Interfaces;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Controller-level tests for prosody-coupled expression:
    ///     <see cref="ConvaiEmotionProfile.ProsodyCoupling" /> at <c>0</c> must hold the gain at
    ///     exactly <c>1</c> across a scripted speaking timeline; a positive coupling must make high
    ///     speech energy read measurably above low energy while speaking; NaN energy must sanitize
    ///     rather than poison the gain; and the gain must settle back to <c>1</c> once speech ends.
    /// </summary>
    /// <remarks>
    ///     Asserts on the controller's smoothed <c>_prosodyGain</c> directly rather than on a
    ///     composed blendshape weight. The gain used to be observable only through the retired
    ///     slot-list output binding; routing it through a resolvable rig instead would make these
    ///     tests depend on rig-convention detection, which is a different unit's contract. The
    ///     matching "the gain actually reaches the face" assertion lives in
    ///     <see cref="EmotionExpressionPlannerProsodyTests" />, which covers the consumer side.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiEmotionControllerProsodyCouplingTests
    {
        private const string CharacterId = "prosody-coupling-char";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;
        private FakeSpeechEnergyProvider _energyProvider;

        [SetUp]
        public void SetUp()
        {

            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerProsodyCouplingTests));
            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");

            _rig.Root.AddComponent<FacialBlendshapeCompositorHost>();

            _energyProvider = new FakeSpeechEnergyProvider();
            _rig.Context.Provide<ISpeechEnergyProvider>(_energyProvider);

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

        private static ConvaiEmotionProfile CreateProfile(float prosodyCoupling)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profile, "prosodyCoupling", prosodyCoupling);
            return profile;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private void SetSpeaking(bool speaking) =>
            _rig.EventHub.Publish(CharacterSpeechStateChanged.Create(CharacterId, speaking));

        /// <summary>The controller's smoothed global prosody gain for the frame just ticked.</summary>
        private float ReadProsodyGain()
        {
            FieldInfo field = typeof(ConvaiEmotionController)
                .GetField("_prosodyGain", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "Missing field _prosodyGain.");
            return (float)field.GetValue(_harness.Controller);
        }

        [Test]
        public void CouplingZero_ScriptedSpeakingTimeline_GainAlwaysStaysAtOne()
        {
            ConvaiEmotionProfile profile = CreateProfile(prosodyCoupling: 0f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.LockEmotion("joy", 1f);

                // Idle, speaking with varying energy, back to idle. With coupling 0 the gain must
                // stay exactly 1 at every step, so the feature is a provable no-op when unused.
                _harness.Tick(1f / 60f);
                Assert.That(ReadProsodyGain(), Is.EqualTo(1f).Within(0.0001f));

                SetSpeaking(true);
                _energyProvider.Current = 0.1f;
                for (int i = 0; i < 30; i++) _harness.Tick(1f / 60f);
                Assert.That(ReadProsodyGain(), Is.EqualTo(1f).Within(0.0001f));

                _energyProvider.Current = 1f;
                for (int i = 0; i < 30; i++) _harness.Tick(1f / 60f);
                Assert.That(ReadProsodyGain(), Is.EqualTo(1f).Within(0.0001f));

                SetSpeaking(false);
                for (int i = 0; i < 30; i++) _harness.Tick(1f / 60f);
                Assert.That(ReadProsodyGain(), Is.EqualTo(1f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CouplingActive_Speaking_HighEnergy_GainMeasurablyAboveLowEnergy()
        {
            ConvaiEmotionProfile profile = CreateProfile(prosodyCoupling: 1f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.LockEmotion("joy", 1f);
                SetSpeaking(true);

                _energyProvider.Current = 0f;
                for (int i = 0; i < 120; i++) _harness.Tick(1f / 60f);
                float lowEnergyGain = ReadProsodyGain();

                _energyProvider.Current = 1f;
                for (int i = 0; i < 120; i++) _harness.Tick(1f / 60f);
                float highEnergyGain = ReadProsodyGain();

                Assert.That(highEnergyGain, Is.GreaterThan(lowEnergyGain),
                    "Full coupling + high speech energy must read measurably above the low-energy case.");
                Assert.That(lowEnergyGain, Is.EqualTo(0.85f).Within(0.005f));
                Assert.That(highEnergyGain, Is.EqualTo(1.15f).Within(0.005f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CouplingActive_Speaking_NaNEnergy_SanitizesToZero_MatchesZeroEnergyBaseline()
        {
            // EmbodimentTick sanitizes NaN/negative speech energy to 0 BEFORE calling
            // ComputeProsodyGainTarget (Mathf.Clamp01(NaN) propagates NaN, so this pre-helper
            // sanitization is the real safety net). A NaN-reporting provider must therefore
            // produce a finite, sane gain identical to the explicit zero-energy case.
            ConvaiEmotionProfile profile = CreateProfile(prosodyCoupling: 1f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.LockEmotion("joy", 1f);
                SetSpeaking(true);

                _energyProvider.Current = 0f;
                for (int i = 0; i < 120; i++) _harness.Tick(1f / 60f);
                float zeroEnergyGain = ReadProsodyGain();

                _energyProvider.Current = float.NaN;
                for (int i = 0; i < 120; i++) _harness.Tick(1f / 60f);
                float nanEnergyGain = ReadProsodyGain();

                Assert.That(float.IsNaN(nanEnergyGain), Is.False,
                    "NaN speech energy must never poison the composed gain.");
                Assert.That(float.IsInfinity(nanEnergyGain), Is.False);
                Assert.That(nanEnergyGain, Is.EqualTo(zeroEnergyGain).Within(0.005f),
                    "NaN energy sanitizes to 0 before ComputeProsodyGainTarget, so it must read identically to the explicit zero-energy baseline.");
                Assert.That(nanEnergyGain, Is.EqualTo(0.85f).Within(0.005f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CouplingActive_SpeechEnds_GainReturnsToNonSpeakingBaseline()
        {
            ConvaiEmotionProfile profile = CreateProfile(prosodyCoupling: 1f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.LockEmotion("joy", 1f);

                SetSpeaking(true);
                _energyProvider.Current = 1f;
                for (int i = 0; i < 120; i++) _harness.Tick(1f / 60f);
                Assert.That(ReadProsodyGain(), Is.EqualTo(1.15f).Within(0.005f));

                SetSpeaking(false);
                for (int i = 0; i < 120; i++) _harness.Tick(1f / 60f);

                Assert.That(ReadProsodyGain(), Is.EqualTo(1f).Within(0.001f),
                    "Once speech ends, the gain must settle back to 1 (the non-speaking baseline).");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
