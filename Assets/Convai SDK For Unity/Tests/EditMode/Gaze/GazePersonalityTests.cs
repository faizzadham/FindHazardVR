using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The three personality dials, and specifically the property that makes them safe: they
    ///     are <b>derived, not stored</b>.
    /// </summary>
    /// <remarks>
    ///     A stored slider is the obvious implementation and the wrong one. It overwrites
    ///     hand-authored fields the first time the inspector redraws, and it lies about the profile
    ///     the moment anyone edits one of those fields directly. These tests pin the alternative:
    ///     hand-editing a state row moves the dial, and moving the dial to where it already sits
    ///     changes nothing.
    /// </remarks>
    /// <remarks>
    ///     Internal rather than public because <see cref="GazePersonalityDial" /> appears in the
    ///     parameterised test signatures and is editor tooling, not API. Mirrors
    ///     <c>BodyAnimationConfigLabelTests</c> and the rest of the Body Animation editor suites.
    /// </remarks>
    internal sealed class GazePersonalityTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_profile != null) Object.DestroyImmediate(_profile);
        }

        [Test]
        public void FreshProfile_ReadsEveryDialInRange()
        {
            foreach (GazePersonalityDial dial in System.Enum.GetValues(typeof(GazePersonalityDial)))
            {
                float value = GazePersonality.Read(_profile, dial);
                Assert.That(value, Is.InRange(0f, 1f), $"{dial} read outside 0..1.");
            }
        }

        [Test]
        public void NullProfile_ReadsTheMidpoint_AndApplyIsSafe()
        {
            Assert.AreEqual(0.5f, GazePersonality.Read(null, GazePersonalityDial.EyeContact));
            Assert.DoesNotThrow(() => GazePersonality.Apply(null, GazePersonalityDial.EyeContact, 1f));
        }

        [TestCase(GazePersonalityDial.EyeContact)]
        [TestCase(GazePersonalityDial.Liveliness)]
        [TestCase(GazePersonalityDial.HeadMovement)]
        public void ApplyThenRead_RoundTrips(GazePersonalityDial dial)
        {
            GazePersonality.Apply(_profile, dial, 0.8f);
            Assert.That(GazePersonality.Read(_profile, dial), Is.EqualTo(0.8f).Within(0.06f),
                $"{dial} must read back approximately what was written, or the slider jumps under " +
                "the user's cursor.");

            GazePersonality.Apply(_profile, dial, 0.2f);
            Assert.That(GazePersonality.Read(_profile, dial), Is.EqualTo(0.2f).Within(0.06f));
        }

        [TestCase(GazePersonalityDial.EyeContact)]
        [TestCase(GazePersonalityDial.Liveliness)]
        [TestCase(GazePersonalityDial.HeadMovement)]
        public void ApplyingTheValueItAlreadyReads_IsIdempotent(GazePersonalityDial dial)
        {
            float before = GazePersonality.Read(_profile, dial);
            GazePersonality.Apply(_profile, dial, before);
            float afterOnce = GazePersonality.Read(_profile, dial);
            GazePersonality.Apply(_profile, dial, afterOnce);
            float afterTwice = GazePersonality.Read(_profile, dial);

            Assert.That(afterTwice, Is.EqualTo(afterOnce).Within(0.001f),
                $"{dial} must converge — a dial that drifts on every repaint would rewrite the asset forever.");
        }

        [TestCase(GazePersonalityDial.EyeContact)]
        [TestCase(GazePersonalityDial.Liveliness)]
        [TestCase(GazePersonalityDial.HeadMovement)]
        public void ExtremeValues_StaySurvivableByOnValidate(GazePersonalityDial dial)
        {
            foreach (float extreme in new[] { 0f, 1f })
            {
                GazePersonality.Apply(_profile, dial, extreme);

                // OnValidate is what the engine runs on any inspector edit. An applied dial that
                // could not survive it would silently snap back and read as a broken control.
                InvokeOnValidate(_profile);

                Assert.That(GazePersonality.Read(_profile, dial), Is.InRange(0f, 1f));
                foreach (GazeStatePolicy policy in _profile.StatePolicies)
                {
                    Assert.That(policy.Engagement, Is.InRange(0f, 1f));
                    Assert.That(policy.AversionStrength, Is.InRange(0f, 1f));
                    Assert.That(policy.HeadContribution, Is.InRange(0f, 1f));
                    Assert.That(policy.FixationLiveliness, Is.InRange(0f, 2f));
                }
            }
        }

        [Test]
        public void EyeContactDial_MovesEngagementInTheRightDirection()
        {
            GazePersonality.Apply(_profile, GazePersonalityDial.EyeContact, 1f);
            float high = AverageConversationalEngagement();

            GazePersonality.Apply(_profile, GazePersonalityDial.EyeContact, 0f);
            float low = AverageConversationalEngagement();

            Assert.That(high, Is.GreaterThan(low),
                "Pushing 'eye contact' up must make the character hold contact more, not less.");
        }

        [Test]
        public void LivelinessDial_ShortensTheGapsBetweenEyeMovements()
        {
            GazePersonality.Apply(_profile, GazePersonalityDial.Liveliness, 1f);
            float lively = _profile.MicroSaccadeIntervalMean;

            GazePersonality.Apply(_profile, GazePersonalityDial.Liveliness, 0f);
            float calm = _profile.MicroSaccadeIntervalMean;

            Assert.That(lively, Is.LessThan(calm),
                "A livelier character moves its eyes more often — the interval is inverted relative " +
                "to the dial, and getting that backwards is an easy mistake to ship.");
        }

        [Test]
        public void HeadMovementDial_MovesHeadContributionInTheRightDirection()
        {
            GazePersonality.Apply(_profile, GazePersonalityDial.HeadMovement, 1f);
            float high = AverageConversationalHeadContribution();

            GazePersonality.Apply(_profile, GazePersonalityDial.HeadMovement, 0f);
            float low = AverageConversationalHeadContribution();

            Assert.That(high, Is.GreaterThan(low));
        }

        [Test]
        public void HandEditingAStateRow_MovesTheDial_AndIsNotOverwritten()
        {
            GazePersonality.Apply(_profile, GazePersonalityDial.EyeContact, 0.5f);
            float before = GazePersonality.Read(_profile, GazePersonalityDial.EyeContact);

            // The user opens the state table and drops Listening engagement to the floor.
            var serialized = new SerializedObject(_profile);
            SerializedProperty policies = GazeProfileSerializedPaths.Find(serialized, "statePolicies");
            for (int i = 0; i < policies.arraySize; i++)
            {
                SerializedProperty element = policies.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("State").enumValueIndex != (int)DialogueState.Listening) continue;
                element.FindPropertyRelative("Engagement").floatValue = 0f;
            }
            serialized.ApplyModifiedProperties();

            float after = GazePersonality.Read(_profile, GazePersonalityDial.EyeContact);
            Assert.That(after, Is.LessThan(before),
                "A hand edit must be visible in the dial. A stored slider would still show the old " +
                "value and then overwrite the edit on the next change.");

            // And crucially: merely reading did not write the row back.
            foreach (GazeStatePolicy policy in _profile.StatePolicies)
                if (policy.State == DialogueState.Listening)
                    Assert.AreEqual(0f, policy.Engagement, 0.0001f,
                        "Reading a dial must never write to the profile.");
        }

        [Test]
        public void ActiveArchetype_IsDefaultOnAFreshProfile()
        {
            GazeProfileArchetypes.GazeArchetype active = GazePersonality.ActiveArchetype(_profile);
            Assert.IsNotNull(active, "A fresh profile must light up a personality pill.");
            Assert.AreEqual("Default", active.Name);
        }

        [Test]
        public void ActiveArchetype_IsNullOnceCustomised()
        {
            GazePersonality.Apply(_profile, GazePersonalityDial.EyeContact, 0.13f);
            Assert.IsNull(GazePersonality.ActiveArchetype(_profile),
                "A customised profile must not claim to still be a preset.");
            Assert.IsTrue(GazePersonality.IsCustomized(_profile),
                "Once the pills go quiet the inspector must report Custom, not an empty selection.");
        }

        /// <summary>
        ///     Retuning a value the archetype authors must clear the pill, even when the state
        ///     table is untouched.
        /// </summary>
        /// <remarks>
        ///     Applying an archetype writes the state table and nine feel values; recognising one
        ///     compared the table alone. So an author who slowed the blink rate kept a lit pill and
        ///     a caption describing a preset the profile had left — and since the picker ignores a
        ///     click on the pill already lit, there was no way back to those nine values.
        /// </remarks>
        [Test]
        public void ActiveArchetype_IsNullOnceAFeelValueIsRetuned()
        {
            Assert.IsNotNull(GazePersonality.ActiveArchetype(_profile), "Sanity: a fresh profile lights a pill.");

            var serialized = new SerializedObject(_profile);
            serialized.Update();
            GazeProfileSerializedPaths.Find(serialized, "blinkIntervalMean").floatValue =
                _profile.BlinkIntervalMean + 2f;
            serialized.ApplyModifiedProperties();

            Assert.IsNull(GazePersonality.ActiveArchetype(_profile),
                "The state table is untouched, but this profile no longer blinks the way the preset " +
                "says it does, so it must not claim to still be that preset.");
        }

        [Test]
        public void ApplyingAnArchetype_MakesItTheActiveOne()
        {
            foreach (GazeProfileArchetypes.GazeArchetype archetype in GazeProfileArchetypes.All)
            {
                var serialized = new SerializedObject(_profile);
                serialized.Update();
                GazeProfileArchetypes.Apply(serialized, archetype);
                serialized.ApplyModifiedProperties();

                GazeProfileArchetypes.GazeArchetype active = GazePersonality.ActiveArchetype(_profile);
                Assert.IsNotNull(active, $"{archetype.Name} did not light up after being applied.");
                Assert.AreEqual(archetype.Name, active.Name);
            }
        }

        // ------------------------------------------------------------------ helpers

        private float AverageConversationalEngagement()
        {
            float total = 0f;
            int counted = 0;
            foreach (GazeStatePolicy policy in _profile.StatePolicies)
            {
                if (policy.State == DialogueState.Idle) continue;
                total += policy.Engagement;
                counted++;
            }
            return counted == 0 ? 0f : total / counted;
        }

        private float AverageConversationalHeadContribution()
        {
            float total = 0f;
            int counted = 0;
            foreach (GazeStatePolicy policy in _profile.StatePolicies)
            {
                if (policy.State == DialogueState.Idle) continue;
                total += policy.HeadContribution;
                counted++;
            }
            return counted == 0 ? 0f : total / counted;
        }

        private static void InvokeOnValidate(ConvaiGazeProfile profile) =>
            typeof(ConvaiGazeProfile)
                .GetMethod("OnValidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(profile, null);
    }
}
