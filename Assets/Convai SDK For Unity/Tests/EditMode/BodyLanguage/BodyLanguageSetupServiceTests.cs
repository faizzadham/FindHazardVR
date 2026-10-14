using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.BodyLanguage.Editor;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Coverage for the edit-time preflight behind the Body Language inspector's
    ///     <b>This Character</b> card. The point of that card is that a rig which cannot drive the
    ///     module says so <em>before</em> Play, so these tests pin the two verdicts a user acts on:
    ///     a rig with no usable spine is blocked, and a rig that merely lacks optional bones is not.
    /// </summary>
    public sealed class BodyLanguageSetupServiceTests
    {
        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null)
                    Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        /// <summary>
        ///     Spawns the subject on an inactive GameObject. The preflight is a pure read and needs
        ///     no lifecycle, and staying inactive keeps the component's own <c>OnEnable</c> — which
        ///     correctly refuses to run outside a Convai character — out of these tests entirely.
        /// </summary>
        private ConvaiBodyLanguageController NewController(bool withAnimator)
        {
            var go = new GameObject("BodyLanguagePreflightSubject");
            _spawned.Add(go);
            go.SetActive(false);
            if (withAnimator) go.AddComponent<Animator>();
            return go.AddComponent<ConvaiBodyLanguageController>();
        }

        [Test]
        public void Inspect_NullController_ReturnsEmptyPreflightRatherThanThrowing()
        {
            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(null);

            Assert.NotNull(preflight.Checks, "A null target must still return a usable preflight.");
            Assert.That(preflight.Checks.Count, Is.Zero);
        }

        [Test]
        public void Inspect_WithNoAnimator_BlocksAndSaysWhy()
        {
            ConvaiBodyLanguageController controller = NewController(withAnimator: false);

            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(controller);

            Assert.IsFalse(preflight.IsFunctional,
                "Body Language layers motion onto an animated skeleton; with no Animator it cannot run.");
            Assert.IsTrue(preflight.TryGetBlocker(out BodyLanguageCheck blocker),
                "A non-functional rig must name the blocker rather than only failing the status chip.");
            Assert.That(blocker.Id, Is.EqualTo(BodyLanguageSetupService.CheckRig));
            Assert.That(blocker.Detail, Is.Not.Null.And.Not.Empty,
                "A blocker with no explanation is the failure this card exists to fix.");
        }

        [Test]
        public void Inspect_WithNonHumanoidAnimator_BlocksOnTheSpine()
        {
            // An Animator with no avatar is the shape a Generic or Legacy rig presents: the module
            // can resolve no spine from it, which is exactly the case a user previously only
            // discovered from a console line after pressing Play.
            ConvaiBodyLanguageController controller = NewController(withAnimator: true);

            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(controller);

            Assert.IsTrue(preflight.HasBlocker);
            Assert.IsTrue(preflight.TryGetBlocker(out BodyLanguageCheck blocker));
            Assert.That(blocker.Id, Is.EqualTo(BodyLanguageSetupService.CheckRig));
        }

        [Test]
        public void Inspect_WhenRigIsBlocked_DoesNotAlsoReportEveryOptionalBoneAsMissing()
        {
            ConvaiBodyLanguageController controller = NewController(withAnimator: false);

            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(controller);

            for (int i = 0; i < preflight.Checks.Count; i++)
            {
                Assert.That(preflight.Checks[i].Id, Is.Not.EqualTo(BodyLanguageSetupService.CheckShoulders),
                    "One unusable spine is one problem. Listing every dependent capability underneath " +
                    "it reads as five problems and buries the one that matters.");
                Assert.That(preflight.Checks[i].Id, Is.Not.EqualTo(BodyLanguageSetupService.CheckStance));
            }
        }

        [Test]
        public void Inspect_AlwaysReportsCharacterScopeAndPersonality()
        {
            ConvaiBodyLanguageController controller = NewController(withAnimator: false);

            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(controller);

            Assert.IsTrue(HasCheck(preflight, BodyLanguageSetupService.CheckCharacter),
                "Placement outside a Convai character is the most common setup mistake and must be " +
                "reported whatever the rig looks like.");
            Assert.IsTrue(HasCheck(preflight, BodyLanguageSetupService.CheckPersonality),
                "The profile row is meaningful even on a blocked rig — it is how a user assigns one.");
        }

        [Test]
        public void EveryCheck_CarriesAUserFacingLabelAndDetail()
        {
            ConvaiBodyLanguageController controller = NewController(withAnimator: false);

            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(controller);

            Assert.That(preflight.Checks.Count, Is.GreaterThan(0));
            for (int i = 0; i < preflight.Checks.Count; i++)
            {
                BodyLanguageCheck check = preflight.Checks[i];
                Assert.That(check.Label, Is.Not.Null.And.Not.Empty, $"Check '{check.Id}' has no label.");
                Assert.That(check.Detail, Is.Not.Null.And.Not.Empty, $"Check '{check.Id}' has no detail.");
                Assert.That(check.Label, Does.Not.Contain("."),
                    $"Check '{check.Id}' label reads as a sentence; it is a column heading.");
            }
        }

        [Test]
        public void DescribeFix_NamesTheOnlyRepairAndNothingElse()
        {
            Assert.That(BodyLanguageSetupService.DescribeFix(BodyLanguageFixId.AssignDefaultProfile),
                Is.Not.Null.And.Not.Empty);
            Assert.IsNull(BodyLanguageSetupService.DescribeFix(BodyLanguageFixId.None),
                "A repair this service cannot perform must not render a button that does nothing.");
        }

        [Test]
        public void ApplyFix_OnNullController_IsASafeNoOp()
        {
            Assert.IsFalse(BodyLanguageSetupService.ApplyFix(null, BodyLanguageFixId.AssignDefaultProfile));
        }

        [Test]
        public void ApplyFix_LeavesAnAlreadyAssignedProfileAlone()
        {
            ConvaiBodyLanguageController controller = NewController(withAnimator: true);
            ConvaiBodyLanguageProfile assigned = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(controller, "profile", assigned);

                Assert.IsFalse(
                    BodyLanguageSetupService.ApplyFix(controller, BodyLanguageFixId.AssignDefaultProfile),
                    "Assigning a default over a profile the user chose would be data loss, not a repair.");
                Assert.That(GetPrivateField(controller, "profile"), Is.SameAs(assigned));
            }
            finally
            {
                Object.DestroyImmediate(assigned);
            }
        }

        /// <summary>
        ///     The controller remembers the last co-speech gesture sequence it played so it never
        ///     plays one twice. Left uncleared across a disable, a request whose sequence happened
        ///     to match the stale value looked already-played and was dropped — reachable in the
        ///     samples, whose debug HUD toggles the whole layer off and on with a keypress.
        /// </summary>
        [Test]
        public void OnDisable_ClearsTheLastCoSpeechGestureSequence()
        {
            ConvaiBodyLanguageController controller = NewController(withAnimator: true);
            FieldInfo sequence = typeof(ConvaiBodyLanguageController).GetField(
                "_lastCoSpeechGestureSequence", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(sequence, "The co-speech de-duplication field must exist to be cleared.");

            sequence.SetValue(controller, 7);

            MethodInfo onDisable = typeof(ConvaiBodyLanguageController).GetMethod(
                "OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onDisable, "The controller must unwind its state in OnDisable.");
            onDisable.Invoke(controller, null);

            Assert.That(sequence.GetValue(controller), Is.EqualTo(0),
                "A stale sequence surviving a disable/enable cycle silently swallows the next " +
                "co-speech gesture that happens to match it. Zero is the 'nothing seen yet' " +
                "sentinel — a real request always carries a sequence above zero.");
        }

        private static bool HasCheck(BodyLanguagePreflight preflight, string id)
        {
            for (int i = 0; i < preflight.Checks.Count; i++)
                if (preflight.Checks[i].Id == id)
                    return true;
            return false;
        }

        private static FieldInfo ProfileField(object target)
        {
            for (System.Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) return field;
            }

            Assert.Fail("The controller must declare a serialized 'profile' field for the inspector to bind.");
            return null;
        }

        private static void SetPrivateField(object target, string _, object value) =>
            ProfileField(target).SetValue(target, value);

        private static object GetPrivateField(object target, string _) => ProfileField(target).GetValue(target);
    }
}
