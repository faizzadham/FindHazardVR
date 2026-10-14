using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Editor;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The "Default" archetype exists so a fresh profile shows one pill already active and
    ///     "put it back the way it was" is one click. That only holds while the archetype and the
    ///     profile's own field initializers agree — and nothing but a test keeps two hand-authored
    ///     tables in step. This is that test.
    /// </summary>
    public sealed class GazeProfileDefaultParityTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_profile != null) Object.DestroyImmediate(_profile);
        }

        private static GazeProfileArchetypes.GazeArchetype DefaultArchetype
        {
            get
            {
                foreach (GazeProfileArchetypes.GazeArchetype archetype in GazeProfileArchetypes.All)
                    if (archetype.Name == "Default") return archetype;

                Assert.Fail("The 'Default' archetype is missing — a fresh profile would show no active pill.");
                return null;
            }
        }

        [Test]
        public void DefaultArchetype_IsFirstInTheToolbar()
        {
            Assert.AreEqual("Default", GazeProfileArchetypes.All[0].Name,
                "Default reads as the baseline the others deviate from, so it leads the row.");
        }

        [Test]
        public void DefaultArchetype_StateTable_MatchesTheProfileDefaults()
        {
            GazeProfileArchetypes.GazeArchetype archetype = DefaultArchetype;

            Assert.That(archetype.States.Length, Is.EqualTo(_profile.StatePolicies.Count),
                "The archetype must cover exactly the states the profile ships.");

            for (int i = 0; i < archetype.States.Length; i++)
            {
                GazeProfileArchetypes.StateRow row = archetype.States[i];
                GazeStatePolicy shipped = FindPolicy(row.State);

                Assert.AreEqual(shipped.Engagement, row.Engagement, 0.0001f, $"{row.State}.Engagement");
                Assert.AreEqual(shipped.AllowPlayerTarget, row.AllowPlayerTarget, $"{row.State}.AllowPlayerTarget");
                Assert.AreEqual(shipped.HeadContribution, row.HeadContribution, 0.0001f, $"{row.State}.HeadContribution");
                Assert.AreEqual(shipped.AllowBodyTurn, row.AllowBodyTurn, $"{row.State}.AllowBodyTurn");
                Assert.AreEqual(shipped.AversionMode, row.AversionMode, $"{row.State}.AversionMode");
                Assert.AreEqual(shipped.AversionStrength, row.AversionStrength, 0.0001f, $"{row.State}.AversionStrength");
                Assert.AreEqual(shipped.FixationLiveliness, row.FixationLiveliness, 0.0001f, $"{row.State}.FixationLiveliness");
            }
        }

        [Test]
        public void DefaultArchetype_FeelFields_MatchTheProfileDefaults()
        {
            GazeProfileArchetypes.GazeArchetype archetype = DefaultArchetype;

            Assert.AreEqual(_profile.AmbientYawRangeDegrees, archetype.AmbientYawRangeDegrees, 0.0001f);
            Assert.AreEqual(_profile.AmbientIntervalMin, archetype.AmbientIntervalMin, 0.0001f);
            Assert.AreEqual(_profile.AmbientIntervalMax, archetype.AmbientIntervalMax, 0.0001f);
            Assert.AreEqual(_profile.AmbientHeadFollow, archetype.AmbientHeadFollow, 0.0001f);
            Assert.AreEqual(_profile.AmbientRecenterBias, archetype.AmbientRecenterBias, 0.0001f);
            Assert.AreEqual(_profile.EnableCuriosityGlances, archetype.EnableCuriosityGlances);
            Assert.AreEqual(_profile.CuriosityGlanceIntervalMin, archetype.CuriosityGlanceIntervalMin, 0.0001f);
            Assert.AreEqual(_profile.CuriosityGlanceIntervalMax, archetype.CuriosityGlanceIntervalMax, 0.0001f);
            Assert.AreEqual(_profile.MicroSaccadeIntervalMean, archetype.MicroSaccadeIntervalMean, 0.0001f);
            Assert.AreEqual(_profile.FaceScanIntervalMean, archetype.FaceScanIntervalMean, 0.0001f);
            Assert.AreEqual(_profile.FaceScanRadiusDegrees, archetype.FaceScanRadiusDegrees, 0.0001f);
            Assert.AreEqual(_profile.BlinkIntervalMean, archetype.BlinkIntervalMean, 0.0001f);
            Assert.AreEqual(_profile.HeadEntryDegrees, archetype.HeadEntryDegrees, 0.0001f);
            Assert.AreEqual(_profile.EnableListeningNods, archetype.EnableListeningNods);
            Assert.AreEqual(_profile.NodPitchDegrees, archetype.NodPitchDegrees, 0.0001f);
            Assert.AreEqual(_profile.ListeningNodIntervalMin, archetype.ListeningNodIntervalMin, 0.0001f);
            Assert.AreEqual(_profile.ListeningNodIntervalMax, archetype.ListeningNodIntervalMax, 0.0001f);
            Assert.AreEqual(_profile.AcknowledgeNodProbability, archetype.AcknowledgeNodProbability, 0.0001f);
            Assert.AreEqual(_profile.PlanningBreakProbability, archetype.PlanningBreakProbability, 0.0001f);
        }

        /// <summary>
        ///     A state row's aversion mode and its strength must agree: neither may be authored in
        ///     a way that makes the other do nothing.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <c>AversionDirector.Tick</c> returns early when the mode is
        ///         <see cref="GazeAversionMode.None" /> or the strength rounds to zero, so the two
        ///         fields silently void each other. Six rows across four archetypes shipped with a
        ///         graded strength — 0.03, 0.05, 0.08, 0.1 — behind a mode of None, and the runtime
        ///         discarded every one of them.
        ///     </para>
        ///     <para>
        ///         They read as a missed mode rather than a leftover number: each of those four
        ///         archetypes also writes <c>None</c> with exactly <c>0</c> elsewhere — Stoic six
        ///         times, Confident five — so the author plainly knew how to say "no aversion here".
        ///         The visible cost was Confident, whose own description promises "minimal
        ///         aversion", holding unbroken eye contact through six of its eight states.
        ///     </para>
        /// </remarks>
        [Test]
        public void NoArchetypeRow_AuthorsAnAversionThatCannotRun()
        {
            var contradictions = new System.Collections.Generic.List<string>();

            foreach (GazeProfileArchetypes.GazeArchetype archetype in GazeProfileArchetypes.All)
            {
                foreach (GazeProfileArchetypes.StateRow row in archetype.States)
                {
                    bool modeOff = row.AversionMode == GazeAversionMode.None;
                    bool strengthOff = row.AversionStrength <= 0.001f;

                    if (modeOff && !strengthOff)
                        contradictions.Add(
                            $"{archetype.Name}/{row.State} sets a strength of {row.AversionStrength:0.###} " +
                            "behind a mode of None, so the runtime discards it. Give the row the mode " +
                            "its strength implies, or set the strength to 0.");

                    if (!modeOff && strengthOff)
                        contradictions.Add(
                            $"{archetype.Name}/{row.State} selects {row.AversionMode} at zero strength, " +
                            "so the mode never runs. Give the row a strength, or set the mode to None.");
                }
            }

            Assert.That(contradictions, Is.Empty,
                "An aversion mode and its strength void each other when they disagree, and nothing " +
                "in the Inspector says so:\n" + string.Join("\n", contradictions));
        }

        /// <summary>
        ///     The same agreement, on the profile assets Convai ships rather than on the archetype
        ///     table they were made from.
        /// </summary>
        /// <remarks>
        ///     A shipped profile is a hand-editable asset, so it can drift away from the table it
        ///     started as without anyone touching the table. Both are checked because a customer
        ///     opening Convai's own character is the one place this has to be right.
        /// </remarks>
        [Test]
        public void NoShippedProfileRow_AuthorsAnAversionThatCannotRun()
        {
            string[] paths =
            {
                "Packages/com.convai.convai-sdk-for-unity/SamplesShared/Profiles/Embodiment/Modules/Gaze/ConvaiSamplesShared_GazeProfile.asset",
                "Packages/com.convai.convai-sdk-for-unity/SamplesShared/Profiles/Character/Convai_Sofia_Gaze.asset"
            };

            var contradictions = new System.Collections.Generic.List<string>();

            foreach (string path in paths)
            {
                var profile = UnityEditor.AssetDatabase.LoadAssetAtPath<ConvaiGazeProfile>(path);
                Assert.That(profile, Is.Not.Null, $"Shipped gaze profile missing: {path}");

                foreach (GazeStatePolicy policy in profile.StatePolicies)
                {
                    bool modeOff = policy.AversionMode == GazeAversionMode.None;
                    bool strengthOff = policy.AversionStrength <= 0.001f;

                    if (modeOff && !strengthOff)
                        contradictions.Add(
                            $"{profile.name}/{policy.State}: strength {policy.AversionStrength:0.###} " +
                            "behind a mode of None — the runtime discards it.");

                    if (!modeOff && strengthOff)
                        contradictions.Add(
                            $"{profile.name}/{policy.State}: {policy.AversionMode} at zero strength — " +
                            "the mode never runs.");
                }
            }

            Assert.That(contradictions, Is.Empty, string.Join("\n", contradictions));
        }

        [Test]
        public void ShippedDefaults_KeepTheAliveCuesOn()
        {
            // Ratified 2026-07-28: both shipped off, which meant no project ever saw them.
            Assert.IsTrue(_profile.EnableCuriosityGlances,
                "An idle character glancing at the player is the cheapest 'it's alive' cue the module has.");
            Assert.IsTrue(_profile.EnableEmotionModulation,
                "Costs nothing without the Emotion module (neutral reading gives unit scales) and is " +
                "the only thing that makes an angry character's gaze read as angry.");

            // Deliberately still off: a real raycast cost and a real behavior change.
            Assert.IsFalse(_profile.PlayerLineOfSight,
                "Occlusion gating stays opt-in — it costs a raycast and changes tracking behavior.");
        }

        private GazeStatePolicy FindPolicy(DialogueState state)
        {
            foreach (GazeStatePolicy policy in _profile.StatePolicies)
                if (policy.State == state) return policy;

            Assert.Fail($"The shipped profile has no policy row for {state}.");
            return default;
        }
    }
}
