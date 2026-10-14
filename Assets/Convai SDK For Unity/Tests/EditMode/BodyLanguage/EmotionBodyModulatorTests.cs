using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="EmotionBodyModulator" />: score-weighted blend math, neutral ⇒
    ///     identity, and the valence/arousal fallback path for labels the profile's table
    ///     doesn't authors.
    /// </summary>
    public sealed class EmotionBodyModulatorTests
    {
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static EmotionReading Reading(params (string label, float score)[] scores)
        {
            var table = new Dictionary<string, float>();
            float best = 0f;
            string dominant = EmotionReading.NeutralLabel;
            foreach ((string label, float score) in scores)
            {
                table[label] = score;
                if (score > best)
                {
                    best = score;
                    dominant = label;
                }
            }
            return new EmotionReading(dominant, best, table, 0f, 0f);
        }

        private static ConvaiBodyLanguageProfile ProfileWithModulationEnabled(bool valenceArousalFallback = true)
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "enableEmotionModulation", true);
            SetPrivateField(profile, "valenceArousalFallback", valenceArousalFallback);
            return profile;
        }

        /// <summary>
        ///     Sets the gate explicitly rather than inheriting it from the shipped default. A test
        ///     of the disabled path must state that it disabled the thing — otherwise it silently
        ///     becomes a test of the enabled path the day the default changes, which is exactly
        ///     what happened when modulation was turned on by default.
        /// </summary>
        private static ConvaiBodyLanguageProfile ProfileWithModulationDisabled()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "enableEmotionModulation", false);
            return profile;
        }

        [Test]
        public void ModulationDisabled_ProducesIdentity()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationDisabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading joy = Reading(("joy", 1f));

                modulator.Tick(profile, in joy);

                Assert.That(modulator.OpennessBias, Is.EqualTo(0f));
                Assert.That(modulator.GestureIntensityScale, Is.EqualTo(1f));
                Assert.That(modulator.BreathRateScale, Is.EqualTo(1f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NeutralReading_ProducesIdentity()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading neutral = EmotionReading.Neutral;

                modulator.Tick(profile, in neutral);

                Assert.That(modulator.OpennessBias, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(modulator.LeanBias, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(modulator.GestureIntensityScale, Is.EqualTo(1f).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TwoLabelsFiftyFifty_ProducesAveragedBias()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading mixed = Reading(("joy", 0.5f), ("sadness", 0.5f));

                modulator.Tick(profile, in mixed);

                // Joy opens (+0.4), sadness closes (-0.5): a 50/50 blend must land near the
                // score-weighted average, not snap to either row.
                profile.TryGetEmotionModifier("joy", out BodyLanguageEmotionModifier joyMod);
                profile.TryGetEmotionModifier("sadness", out BodyLanguageEmotionModifier sadMod);
                float expectedOpenness = (joyMod.OpennessBias * 0.5f + sadMod.OpennessBias * 0.5f) / 1f;

                Assert.That(modulator.OpennessBias, Is.EqualTo(expectedOpenness).Within(0.02f),
                    "A 50/50 blend must average the two rows' openness bias.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WeakLoneEmotion_ProducesProportionallyWeakBias()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                profile.TryGetEmotionModifier("joy", out BodyLanguageEmotionModifier joyMod);

                // A lone joy at 0.2 confidence must move the body ~20% of joy's full openness
                // bias, not the full bias (an earlier path normalized the score
                // away and produced full magnitude for any lone emotion).
                EmotionReading weakJoy = Reading(("joy", 0.2f));
                modulator.Tick(profile, in weakJoy);

                Assert.That(modulator.OpennessBias, Is.EqualTo(joyMod.OpennessBias * 0.2f).Within(0.01f),
                    "A weak lone emotion must produce a proportionally weak posture bias.");

                // A saturated joy still produces the full bias.
                EmotionReading fullJoy = Reading(("joy", 1f));
                modulator.Tick(profile, in fullJoy);

                Assert.That(modulator.OpennessBias, Is.EqualTo(joyMod.OpennessBias).Within(0.01f),
                    "A saturated emotion must still produce the full posture bias.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WeakLoneEmotion_ScalesTheGestureAndBreathMultipliersTowardIdentity()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                profile.TryGetEmotionModifier("joy", out BodyLanguageEmotionModifier joyMod);

                EmotionReading weakJoy = Reading(("joy", 0.2f));
                modulator.Tick(profile, in weakJoy);

                // Scales lerp from identity (1) toward the authored value by the confidence, so a
                // 0.2 joy sits 20% of the way from 1 to joy's multiplier.
                float expectedGesture = 1f + (joyMod.GestureIntensityScale - 1f) * 0.2f;
                Assert.That(modulator.GestureIntensityScale, Is.EqualTo(expectedGesture).Within(0.01f),
                    "A weak emotion must scale gesture intensity only slightly away from 1.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TablePath_AgreesWithNoTablePath_ForASingleLabelAtTheSameScore()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                profile.TryGetEmotionModifier("joy", out BodyLanguageEmotionModifier joyMod);

                // Table path: full score table with one active label.
                var tableModulator = new EmotionBodyModulator();
                EmotionReading tableReading = Reading(("joy", 0.6f));
                tableModulator.Tick(profile, in tableReading);

                // No-table path: dominant label + score with an empty score table (e.g. a basic
                // emotion controller that publishes no full table) — this hits the Count == 0
                // branch, which already lerped by DominantScore. Both paths must now agree.
                var noTableModulator = new EmotionBodyModulator();
                var noTableReading = new EmotionReading("joy", 0.6f, EmotionReading.EmptyScores, 0f, 0f);
                noTableModulator.Tick(profile, in noTableReading);

                Assert.That(tableModulator.OpennessBias, Is.EqualTo(noTableModulator.OpennessBias).Within(1e-4f),
                    "The table blend and the no-table dominant-label path must agree for one label at the same score.");
                Assert.That(tableModulator.OpennessBias, Is.EqualTo(joyMod.OpennessBias * 0.6f).Within(0.01f),
                    "A single label at score 0.6 must produce 60% of its authored openness bias.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void UnknownLabel_FallsBackToValenceArousalDerivation_WhenEnabled()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled(valenceArousalFallback: true);
            try
            {
                var modulator = new EmotionBodyModulator();
                // "trust" is in the SDK's shipped taxonomy but not in the profile's hand-tuned
                // big-six table — it must derive from the valence/arousal table instead.
                EmotionReading trust = Reading(("trust", 1f));

                modulator.Tick(profile, in trust);

                Assert.That(modulator.OpennessBias, Is.Not.EqualTo(0f),
                    "Trust (positive valence) must derive a non-zero openness bias via the fallback.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void UnknownLabel_ProducesIdentity_WhenFallbackDisabled()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled(valenceArousalFallback: false);
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading trust = Reading(("trust", 1f));

                modulator.Tick(profile, in trust);

                Assert.That(modulator.OpennessBias, Is.EqualTo(0f).Within(1e-4f),
                    "With the fallback disabled, an unauthored label must contribute identity.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NewReading_ReblendsDespiteIdentityCache()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading joy = Reading(("joy", 1f));
                modulator.Tick(profile, in joy);
                float joyOpenness = modulator.OpennessBias;

                // Same reading instance again — the identity cache keeps the outputs stable.
                modulator.Tick(profile, in joy);
                Assert.That(modulator.OpennessBias, Is.EqualTo(joyOpenness));

                // A genuinely new reading (different table/content) must re-blend, not serve
                // the cached joy outputs.
                EmotionReading sadness = Reading(("sadness", 1f));
                modulator.Tick(profile, in sadness);
                Assert.That(modulator.OpennessBias, Is.Not.EqualTo(joyOpenness).Within(1e-4f),
                    "A changed reading must invalidate the identity cache and re-blend.");

                // And after a neutral gap the same emotion must blend again from scratch.
                EmotionReading neutral = EmotionReading.Neutral;
                modulator.Tick(profile, in neutral);
                modulator.Tick(profile, in joy);
                Assert.That(modulator.OpennessBias, Is.EqualTo(joyOpenness).Within(1e-4f),
                    "Re-entering the same emotion after neutral must restore its blend.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ──  Feature C: Arousal01 physiological coherence ────────

        [Test]
        public void NeutralReading_Arousal01IsNeutralOneHalf()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading neutral = EmotionReading.Neutral;

                modulator.Tick(profile, in neutral);

                Assert.That(modulator.Arousal01, Is.EqualTo(0.5f).Within(1e-4f),
                    "EmotionValenceArousalTable's signed 0 (neutral) must rescale to 0.5.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ModulationDisabled_Arousal01IsNeutralOneHalf()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationDisabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading joy = Reading(("joy", 1f));

                modulator.Tick(profile, in joy);

                Assert.That(modulator.Arousal01, Is.EqualTo(0.5f).Within(1e-4f),
                    "With emotion modulation disabled, arousal must degrade to neutral (0.5), same as every other output.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void HighArousalEmotion_ProducesArousal01AboveOneHalf()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                // Joy is marked high-arousal (+0.55) in EmotionValenceArousalTable, and is also
                // an AUTHORED big-six row — Arousal01 must still read from the VA table
                // regardless of the authored posture-bias row existing.
                EmotionReading joy = Reading(("joy", 1f));

                modulator.Tick(profile, in joy);

                Assert.That(modulator.Arousal01, Is.GreaterThan(0.5f),
                    "A high-arousal emotion (joy, +0.55 in the VA table) must produce Arousal01 above neutral.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void LowArousalEmotion_ProducesArousal01BelowOneHalf()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                // Trust is calm/low-arousal (-0.2) in EmotionValenceArousalTable.
                EmotionReading trust = Reading(("trust", 1f));

                modulator.Tick(profile, in trust);

                Assert.That(modulator.Arousal01, Is.LessThan(0.5f),
                    "A low-arousal emotion (trust, -0.2 in the VA table) must produce Arousal01 below neutral.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WeakLoneHighArousalEmotion_ProducesProportionallySmallArousalDeviation()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading weakJoy = Reading(("joy", 0.2f));
                EmotionReading fullJoy = Reading(("joy", 1f));

                modulator.Tick(profile, in weakJoy);
                float weakDeviation = modulator.Arousal01 - 0.5f;

                modulator.Tick(profile, in fullJoy);
                float fullDeviation = modulator.Arousal01 - 0.5f;

                Assert.That(weakDeviation, Is.GreaterThan(0f).And.LessThan(fullDeviation),
                    "A weaker score must produce a proportionally smaller arousal deviation from neutral, exactly like the posture biases.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Reset_RestoresArousal01ToNeutral()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading joy = Reading(("joy", 1f));
                modulator.Tick(profile, in joy);
                Assert.That(modulator.Arousal01, Is.Not.EqualTo(0.5f));

                modulator.Reset();

                Assert.That(modulator.Arousal01, Is.EqualTo(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Reset_RestoresIdentity()
        {
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            try
            {
                var modulator = new EmotionBodyModulator();
                EmotionReading joy = Reading(("joy", 1f));
                modulator.Tick(profile, in joy);
                Assert.That(modulator.OpennessBias, Is.Not.EqualTo(0f));

                modulator.Reset();

                Assert.That(modulator.OpennessBias, Is.EqualTo(0f));
                Assert.That(modulator.GestureIntensityScale, Is.EqualTo(1f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
