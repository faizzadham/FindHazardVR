using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class BlinkDirectorTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private BlinkDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new BlinkDirector();
            _random = new DeterministicEmbodimentRandom(42u);
            _director.Reset(_profile, ref _random);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private int CountBlinks(float seconds)
        {
            int blinks = 0;
            bool wasClosed = false;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                _director.Tick(_profile, Dt, ref _random);
                bool closed = _director.Weight > 0.5f;
                if (closed && !wasClosed) blinks++;
                wasClosed = closed;
            }
            return blinks;
        }

        [Test]
        public void Tick_ProducesStatisticalBlinkCadence()
        {
            int blinks = CountBlinks(60f);

            // Mean 4.2 s ± 2.2 s jitter → roughly 9–30 blinks per minute.
            Assert.That(blinks, Is.InRange(6, 32),
                $"Expected a natural blink cadence, got {blinks} blinks in 60 s.");
        }

        [Test]
        public void Tick_WeightReturnsToZeroBetweenBlinks()
        {
            CountBlinks(20f);

            // Run until we are in a waiting window.
            for (int i = 0; i < 240 && _director.Weight > 0f; i++)
                _director.Tick(_profile, Dt, ref _random);

            Assert.That(_director.Weight, Is.EqualTo(0f), "Lids must fully reopen between blinks.");
        }

        [Test]
        public void ShiftBlink_FiresOnLargeSaccades_RespectsRefractory()
        {
            // Force a shift blink: probability is 0.55, so roll until one fires.
            bool fired = false;
            for (int i = 0; i < 20 && !fired; i++)
                fired = _director.TryTriggerShiftBlink(_profile, 30f, ref _random);
            Assert.IsTrue(fired, "A 30° saccade must be able to trigger a blink.");

            // While blinking / inside the refractory window a second trigger must fail.
            _director.Tick(_profile, Dt, ref _random);
            Assert.IsFalse(_director.TryTriggerShiftBlink(_profile, 30f, ref _random),
                "No blink stacking while a blink is in flight.");
        }

        [Test]
        public void ShiftBlink_IgnoresSmallSaccades()
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.IsFalse(_director.TryTriggerShiftBlink(_profile, 5f, ref _random),
                    "Saccades below the threshold must never blink.");
            }
        }

        [Test]
        public void RateScale_IncreasesBlinkFrequency()
        {
            int baseline = CountBlinks(40f);

            _director.Reset(_profile, ref _random);
            _director.RateScale = 2f;
            int scaled = CountBlinks(40f);

            Assert.That(scaled, Is.GreaterThan(baseline),
                "Doubling the rate scale must produce more blinks (emotion hook).");
        }

        [Test]
        public void DisabledBlink_ProducesNoWeight()
        {
            var serialized = new UnityEditor.SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "enableBlink").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            int blinks = CountBlinks(30f);

            Assert.That(blinks, Is.EqualTo(0));
            Assert.That(_director.Weight, Is.EqualTo(0f));
        }

        // ------------------------------------------------------------------ blink clustering

        private void SetProperty(string name, object value)
        {
            var serialized = new UnityEditor.SerializedObject(_profile);
            UnityEditor.SerializedProperty prop = GazeProfileSerializedPaths.Find(serialized, name);
            switch (value)
            {
                case bool b: prop.boolValue = b; break;
                case float f: prop.floatValue = f; break;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void ClusterCue_ElevatesBlinkLikelihood()
        {
            // Default profile: spontaneous interval floor is max(BlinkRefractorySeconds=0.6,
            // BlinkIntervalMean-Jitter=4.2-2.2=2.0) = 2.0s, always well above the 0.7s window —
            // so with NO cue (rate 1x) a spontaneous blink inside the window is impossible for
            // any seed (deterministic floor, not just improbable). At the max clamped cluster
            // multiplier (6x) the window covers up to 0.7*6=4.2 "countdown seconds", i.e. roughly
            // half the sampled [2.0, 6.4]s range — a strong, reliable signal across seeds.
            SetProperty("blinkClusterRateMultiplier", 6f);

            int cuedTotal = 0;
            int baselineTotal = 0;
            for (uint seed = 1; seed <= 20; seed++)
            {
                var random = new DeterministicEmbodimentRandom(seed);
                _director.Reset(_profile, ref random);
                baselineTotal += CountBlinksWith(ref random, 0.7f);

                random = new DeterministicEmbodimentRandom(seed);
                _director.Reset(_profile, ref random);
                _director.NotifyClusterCue();
                cuedTotal += CountBlinksWith(ref random, 0.7f);
            }

            Assert.That(baselineTotal, Is.EqualTo(0),
                "Sanity: the uncued spontaneous floor (2.0s) exceeds the 0.7s window, so baseline must never blink in it.");
            Assert.That(cuedTotal, Is.GreaterThan(baselineTotal),
                $"Cluster window must raise blink incidence across trials (baseline={baselineTotal}, cued={cuedTotal}).");
        }

        private int CountBlinksWith(ref DeterministicEmbodimentRandom random, float seconds)
        {
            int blinks = 0;
            bool wasClosed = false;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                _director.Tick(_profile, Dt, ref random);
                bool closed = _director.Weight > 0.5f;
                if (closed && !wasClosed) blinks++;
                wasClosed = closed;
            }
            return blinks;
        }

        [Test]
        public void ClusterCue_WindowExpires_RateReturnsToBaseline()
        {
            _director.NotifyClusterCue();
            Assert.IsTrue(GetClusterWindowActive(), "Window must be active immediately after the cue.");

            // Tick past the ~0.7s window.
            for (int i = 0; i < Mathf.CeilToInt(0.9f / Dt); i++)
                _director.Tick(_profile, Dt, ref _random);

            Assert.IsFalse(GetClusterWindowActive(), "Window must expire after ~0.7s.");
        }

        [Test]
        public void ClusterCue_DoesNotDoubleBlink_ImmediatelyAfterForcedBlink()
        {
            bool fired = _director.TryTriggerForcedBlink(_profile);
            Assert.IsTrue(fired, "Forced blink must fire on a fresh director.");

            _director.NotifyClusterCue();

            // Tick a single frame right after: refractory + in-flight phase must prevent any
            // second blink from starting on top of the one just forced, regardless of the
            // cluster window being active.
            float weightBefore = _director.Weight;
            _director.Tick(_profile, Dt, ref _random);
            Assert.That(_director.Weight, Is.GreaterThanOrEqualTo(weightBefore),
                "Lid weight must progress monotonically through the single forced blink, not restart.");
            Assert.IsFalse(_director.TryTriggerForcedBlink(_profile),
                "No stacked blink while the forced blink is still in flight / inside its refractory.");
        }

        [Test]
        public void ClusterCue_Disabled_ProducesNoElevation()
        {
            SetProperty("enableBlinkClustering", false);

            _director.NotifyClusterCue();
            for (int i = 0; i < 5; i++)
                _director.Tick(_profile, Dt, ref _random);

            Assert.IsFalse(GetClusterWindowActive(),
                "With clustering disabled the window must never be reported active.");
        }

        [Test]
        public void ClusterRateMultiplier_Clamped()
        {
            // Range attribute already clamps the Inspector slider, but SerializedProperty writes
            // bypass it; BlinkDirector.Tick must still clamp defensively (1..6) so a corrupted
            // asset cannot produce a pathological blink rate. The freshly-reset countdown floor
            // is 2.0s (see ClusterCue_ElevatesBlinkLikelihood): an unclamped 50x multiplier would
            // deplete it in 0.04s, but the clamped 6x cap needs >= 0.333s — so ticking only 0.1s
            // must never produce a blink if the clamp is actually applied.
            SetProperty("blinkClusterRateMultiplier", 50f);
            _director.NotifyClusterCue();

            for (int i = 0; i < 6; i++) // 0.1s at 60Hz
            {
                _director.Tick(_profile, Dt, ref _random);
                Assert.That(_director.Weight, Is.InRange(0f, 1f));
            }

            Assert.That(_director.Weight, Is.EqualTo(0f),
                "A 50x multiplier must be clamped to 6x — it must not blink within 0.1s from a fresh 2.0s-floor countdown.");
        }

        [Test]
        public void DelayedClusterCue_SchedulesAfterDelay()
        {
            _director.NotifyDelayedClusterCue(0.3f);
            Assert.IsFalse(GetClusterWindowActive(), "Window must not open before the delay elapses.");

            // Tick comfortably short of the delay (0.2s of 0.3s), with margin against float rounding.
            for (int i = 0; i < 12; i++)
                _director.Tick(_profile, Dt, ref _random);
            Assert.IsFalse(GetClusterWindowActive(), "Window must still be pending well before 300ms.");

            // Cross the delay boundary with margin (cumulative ~0.35s).
            for (int i = 0; i < 9; i++)
                _director.Tick(_profile, Dt, ref _random);
            Assert.IsTrue(GetClusterWindowActive(), "Window must open once the 300ms delay elapses.");
        }

        [Test]
        public void ForcedBlink_ClearsActiveClusterWindow()
        {
            _director.NotifyClusterCue();
            Assert.IsTrue(GetClusterWindowActive());

            bool fired = _director.TryTriggerForcedBlink(_profile);
            Assert.IsTrue(fired);
            Assert.IsFalse(GetClusterWindowActive(), "A forced blink must clear the now-redundant cluster window.");

            // Time-to-next-blink after the forced blink completes must match the uncued
            // baseline, not the spiked rate: with seed 42 the very next spontaneous blink after
            // this forced one must not land inside the 0.7s window that would only be possible
            // under elevated (cued) rate.
            for (int i = 0; i < Mathf.CeilToInt(0.3f / Dt); i++) // clear Closing/Opening phases
                _director.Tick(_profile, Dt, ref _random);

            int blinksInFormerWindow = CountBlinks(0.7f);
            Assert.That(blinksInFormerWindow, Is.EqualTo(0),
                "Rate must be back to baseline — no spike-rate blink inside the cleared window.");
        }

        private bool GetClusterWindowActive() => _director.ClusterWindowActive;
    }
}
