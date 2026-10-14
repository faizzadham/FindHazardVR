using System;
using System.Collections.Generic;
using Convai.Modules.Gaze.Core;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The capability catalogue is what makes the module's optional half discoverable, so it
    ///     has to be true: every entry must name a real component, every name must be something a
    ///     person can read, and presence must reflect what is actually on the character.
    /// </summary>
    public sealed class GazeCapabilityModelTests
    {
        private GameObject _root;
        private readonly List<GazeCapabilityInfo> _results = new();

        [SetUp]
        public void SetUp() => _root = new GameObject("GazeCapabilityTestCharacter");

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [Test]
        public void EveryEnumValue_IsInTheCatalogue()
        {
            GazeCapabilities.Evaluate(_root.transform, _results);

            Assert.That(_results.Count, Is.EqualTo(GazeCapabilities.Count));
            Assert.That(_results.Count, Is.EqualTo(Enum.GetValues(typeof(GazeCapabilityId)).Length),
                "A capability id with no catalogue entry would be invisible to every setup surface.");
        }

        [Test]
        public void EveryEntry_ResolvesARealMonoBehaviourType()
        {
            GazeCapabilities.Evaluate(_root.transform, _results);

            foreach (GazeCapabilityInfo info in _results)
            {
                Assert.IsNotNull(info.ProviderType, $"{info.Id} has no provider type.");
                Assert.IsTrue(typeof(MonoBehaviour).IsAssignableFrom(info.ProviderType),
                    $"{info.Id}'s provider must be a MonoBehaviour the user can add.");
            }
        }

        [Test]
        public void EveryEntry_HasAPlainEnglishNameThatIsNotAClassName()
        {
            GazeCapabilities.Evaluate(_root.transform, _results);

            foreach (GazeCapabilityInfo info in _results)
            {
                Assert.IsNotEmpty(info.DisplayName, $"{info.Id} has no display name.");
                Assert.IsNotEmpty(info.Description, $"{info.Id} has no description.");

                // The whole point of the model: a user setting up a character never has to learn a
                // type name. A display name that IS the type name defeats it.
                Assert.AreNotEqual(info.ProviderType.Name, info.DisplayName,
                    $"{info.Id}'s display name is its class name — that is what this model exists to avoid.");
                Assert.IsFalse(info.DisplayName.Contains("Gaze"),
                    $"{info.Id}'s display name repeats the module name; it is already shown under Gaze.");

                // Deliberately no "must not contain Convai" rule. "Looks at other Convai characters"
                // is the product's own ratified term for the thing — the naming standard requires
                // "Convai Character", so banning the word here would fight it. What matters is that
                // the label is not a type name, which the assertion above already covers.
            }
        }

        [Test]
        public void DisplayNames_AreUnique()
        {
            GazeCapabilities.Evaluate(_root.transform, _results);

            var seen = new HashSet<string>();
            foreach (GazeCapabilityInfo info in _results)
                Assert.IsTrue(seen.Add(info.DisplayName),
                    $"Two capabilities share the label '{info.DisplayName}' — a checkbox list of " +
                    "those is unusable.");
        }

        [Test]
        public void BareCharacter_HasNoOptionalCapabilities()
        {
            GazeCapabilities.Evaluate(_root.transform, _results);

            foreach (GazeCapabilityInfo info in _results)
                Assert.IsFalse(info.IsPresent, $"{info.Id} must not be present on a bare character.");

            Assert.AreEqual("none", GazeCapabilities.DescribeActive(_root.transform));
        }

        [Test]
        public void AddingAProvider_FlipsItsCapabilityToPresent()
        {
            _root.AddComponent<PlayerAttentionSensor>();
            GazeCapabilities.Evaluate(_root.transform, _results);

            Assert.IsTrue(Find(GazeCapabilityId.PlayerAttention).IsPresent);
            Assert.IsFalse(Find(GazeCapabilityId.JointAttention).IsPresent,
                "Adding one provider must not report the others as present.");
        }

        [Test]
        public void ProviderOnAChild_Counts()
        {
            var child = new GameObject("Rig");
            child.transform.SetParent(_root.transform, false);
            child.AddComponent<GazeJointAttention>();

            GazeCapabilities.Evaluate(_root.transform, _results);
            Assert.IsTrue(Find(GazeCapabilityId.JointAttention).IsPresent,
                "Providers are commonly parented under the rig, not the character root.");
        }

        [Test]
        public void DisabledProvider_ReadsAsAbsent()
        {
            PlayerAttentionSensor sensor = _root.AddComponent<PlayerAttentionSensor>();
            sensor.enabled = false;

            GazeCapabilities.Evaluate(_root.transform, _results);
            Assert.IsFalse(Find(GazeCapabilityId.PlayerAttention).IsPresent,
                "A disabled component does nothing; reporting it as present would be a lie every " +
                "setup surface then repeats.");
        }

        [Test]
        public void NullRoot_IsSafeAndReportsNothingPresent()
        {
            GazeCapabilities.Evaluate(null, _results);

            Assert.That(_results.Count, Is.EqualTo(GazeCapabilities.Count),
                "The catalogue is still enumerable without a character — the editor draws it before " +
                "one is selected.");
            foreach (GazeCapabilityInfo info in _results)
                Assert.IsFalse(info.IsPresent);
        }

        [Test]
        public void StaticLookups_MatchTheEvaluatedEntries()
        {
            GazeCapabilities.Evaluate(_root.transform, _results);

            foreach (GazeCapabilityInfo info in _results)
            {
                Assert.AreEqual(info.DisplayName, GazeCapabilities.DisplayNameOf(info.Id));
                Assert.AreEqual(info.Description, GazeCapabilities.DescriptionOf(info.Id));
                Assert.AreEqual(info.ProviderType, GazeCapabilities.ProviderTypeOf(info.Id));
            }
        }

        private GazeCapabilityInfo Find(GazeCapabilityId id)
        {
            foreach (GazeCapabilityInfo info in _results)
                if (info.Id == id) return info;

            Assert.Fail($"Capability {id} is missing from the evaluated results.");
            return default;
        }
    }
}
