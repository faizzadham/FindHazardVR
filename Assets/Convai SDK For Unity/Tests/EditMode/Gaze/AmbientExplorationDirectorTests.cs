using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class AmbientExplorationDirectorTests
    {
        private ConvaiGazeProfile _profile;
        private AmbientExplorationDirector _director;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new AmbientExplorationDirector();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        [Test]
        public void Tick_SameSeed_ProducesSameSequence()
        {
            var randomA = new DeterministicEmbodimentRandom(1234u);
            var randomB = new DeterministicEmbodimentRandom(1234u);
            var directorB = new AmbientExplorationDirector();

            for (int i = 0; i < 600; i++)
            {
                _director.Tick(_profile, 1f / 60f, true, ref randomA);
                directorB.Tick(_profile, 1f / 60f, true, ref randomB);
            }

            Assert.That(_director.CurrentAngles, Is.EqualTo(directorB.CurrentAngles),
                "Ambient exploration must be deterministic for a given seed.");
        }

        [Test]
        public void Tick_TargetsStayInsideConfiguredRanges()
        {
            var random = new DeterministicEmbodimentRandom(99u);

            for (int i = 0; i < 3000; i++)
            {
                _director.Tick(_profile, 0.1f, true, ref random);
                Vector2 angles = _director.CurrentAngles;
                Assert.That(Mathf.Abs(angles.x), Is.LessThanOrEqualTo(_profile.AmbientYawRangeDegrees + 0.001f));
                Assert.That(angles.y, Is.LessThanOrEqualTo(_profile.AmbientPitchUpDegrees + 0.001f));
                Assert.That(angles.y, Is.GreaterThanOrEqualTo(-_profile.AmbientPitchDownDegrees - 0.001f));
            }
        }

        [Test]
        public void Tick_BrieflyInactive_HoldsTheFixationSoAGlanceCanBeHandedBack()
        {
            var random = new DeterministicEmbodimentRandom(7u);
            Vector2 held = DriveToOffCentreFixation(ref random);

            // A glance's worth of interruption: the longest curiosity glance the profile allows
            // plus its release.
            for (int i = 0; i < 30; i++)
                _director.Tick(_profile, 0.1f, false, ref random);

            Assert.That(_director.CurrentAngles, Is.EqualTo(held),
                "A glance-length interruption must leave the fixation standing. The head solver " +
                "crossfades out of it while the glance runs and back onto it afterwards — " +
                "clearing it mid-glance is the frame-one drop to centre that crossfade exists to " +
                "remove, and it also makes the character return from every glance to a different " +
                "place than it was looking.");
        }

        [Test]
        public void Tick_InactiveBeyondTheResumeWindow_ClearsTheFixation()
        {
            var random = new DeterministicEmbodimentRandom(7u);
            DriveToOffCentreFixation(ref random);

            for (int i = 0; i < 120; i++) // 12 s: plainly a conversation, not a glance
                _director.Tick(_profile, 0.1f, false, ref random);

            Assert.That(_director.CurrentAngles, Is.EqualTo(Vector2.zero),
                "After a real engagement, the fixation the character was holding beforehand is " +
                "stale: idle life must start clean rather than freeze onto it.");
        }

        /// <summary>
        ///     Whether the director still holds a fixation must be answerable separately from what
        ///     that fixation is, because "no fixation" and "a fixation at dead centre" are the
        ///     same <see cref="Vector2.zero" />.
        /// </summary>
        /// <remarks>
        ///     The caller that needs the distinction is the head's hand-over from idle life. Handing
        ///     the head back to a fixation that still exists resumes idle life; handing it back to
        ///     the zero left behind when the resume window expired commands it to face front — and
        ///     mid-conversation that reads as the character briefly looking away from you.
        /// </remarks>
        [Test]
        public void HasResumableFixation_IsFalseOnceTheResumeWindowHasClearedTheAngles()
        {
            var random = new DeterministicEmbodimentRandom(4321u);

            Assert.IsFalse(_director.HasResumableFixation,
                "Idle life has never run, so there is nothing to resume.");

            DriveToOffCentreFixation(ref random);
            Assert.IsTrue(_director.HasResumableFixation);

            // A glance-length interruption: the fixation survives, and so does the claim to it.
            for (int i = 0; i < 20; i++) _director.Tick(_profile, 0.1f, false, ref random);
            Assert.IsTrue(_director.HasResumableFixation,
                "Inside the resume window the fixation is still there to be handed back.");
            Assert.That(_director.CurrentAngles, Is.Not.EqualTo(Vector2.zero));

            // Well past it: the angles are cleared, and the claim must go with them.
            for (int i = 0; i < 60; i++) _director.Tick(_profile, 0.1f, false, ref random);
            Assert.That(_director.CurrentAngles, Is.EqualTo(Vector2.zero));
            Assert.IsFalse(_director.HasResumableFixation,
                "The cleared angles are not a fixation at centre — they are the absence of one, " +
                "and a caller that cannot tell the difference will aim the head at rest-forward.");
        }

        /// <summary>
        ///     Runs idle life until it is holding a fixation that is not dead centre, and returns
        ///     it. The recentre bias means "tick a while" is not enough on its own — a run can end
        ///     on a recentring fixation, and a held zero proves nothing about holding.
        /// </summary>
        private Vector2 DriveToOffCentreFixation(ref DeterministicEmbodimentRandom random)
        {
            for (int i = 0; i < 6000; i++)
            {
                _director.Tick(_profile, 0.1f, true, ref random);
                if (_director.CurrentAngles != Vector2.zero) return _director.CurrentAngles;
            }

            Assert.Fail("Ambient exploration never produced an off-centre fixation.");
            return Vector2.zero;
        }
    }
}
