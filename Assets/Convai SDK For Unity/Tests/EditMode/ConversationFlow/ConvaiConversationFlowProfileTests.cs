using System.Reflection;
using Convai.Modules.ConversationFlow.Core;
using Convai.Modules.ConversationFlow.Profiles;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.ConversationFlow
{
    /// <summary>
    ///     Covers <see cref="ConvaiConversationFlowProfile" />'s authoring contract: the values it
    ///     ships, and the one cross-field invariant its two <c>[Range]</c>s cannot express.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiConversationFlowProfileTests
    {
        private static void InvokeOnValidate(ConvaiConversationFlowProfile profile) =>
            typeof(ConvaiConversationFlowProfile)
                .GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(profile, null);

        private static void SetPrivateField(ConvaiConversationFlowProfile profile, string name, float value) =>
            typeof(ConvaiConversationFlowProfile)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(profile, value);

        [Test]
        public void CreateDefault_ProducesTheSameTimingsTheStateMachineDefaultsTo()
        {
            ConvaiConversationFlowProfile profile = ConvaiConversationFlowProfile.CreateDefault();
            try
            {
                Assert.That(profile.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave),
                    "The runtime-owned fallback must never be saved into the scene or the project.");

                ConversationFlowTimings authored = profile.ToTimings();
                ConversationFlowTimings expected = ConversationFlowTimings.Default;

                Assert.That(authored.TransitionDuration, Is.EqualTo(expected.TransitionDuration).Within(1e-4f));
                Assert.That(authored.ThinkingMinHold, Is.EqualTo(expected.ThinkingMinHold).Within(1e-4f));
                Assert.That(authored.ThinkingMaxHold, Is.EqualTo(expected.ThinkingMaxHold).Within(1e-4f));
                Assert.That(authored.AttendingGracePeriod, Is.EqualTo(expected.AttendingGracePeriod).Within(1e-4f));
                Assert.That(authored.SettlingDuration, Is.EqualTo(expected.SettlingDuration).Within(1e-4f));
                Assert.That(authored.IdleReturnDelay, Is.EqualTo(expected.IdleReturnDelay).Within(1e-4f));
                Assert.That(authored.InterruptedFreezeDuration,
                    Is.EqualTo(expected.InterruptedFreezeDuration).Within(1e-4f));
                Assert.That(authored.SpeakingBaseEnergy, Is.EqualTo(expected.SpeakingBaseEnergy).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_RaisesAThinkingMaximumThatFellBelowTheMinimum()
        {
            // Both values are individually legal: the minimum's range is 0..3 and the maximum's is
            // 0.5..10, so the Inspector accepts this pair without complaint. Only the pair is wrong.
            ConvaiConversationFlowProfile profile = ConvaiConversationFlowProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "thinkingMinHold", 3f);
                SetPrivateField(profile, "thinkingMaxHold", 0.5f);

                InvokeOnValidate(profile);

                ConversationFlowTimings timings = profile.ToTimings();
                Assert.That(timings.ThinkingMaxHold, Is.EqualTo(3f).Within(1e-4f));
                Assert.That(timings.ThinkingMaxHold, Is.GreaterThanOrEqualTo(timings.ThinkingMinHold));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_AgreesWithTheRuntimesOwnResolutionOfAnInvertedWindow()
        {
            // The point of repairing it on the asset is that the two never disagree. If
            // ConversationFlowTimings ever resolves an inverted window differently, this fails.
            ConvaiConversationFlowProfile profile = ConvaiConversationFlowProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "thinkingMinHold", 2.4f);
                SetPrivateField(profile, "thinkingMaxHold", 0.9f);

                ConversationFlowTimings beforeRepair = profile.ToTimings();
                InvokeOnValidate(profile);
                ConversationFlowTimings afterRepair = profile.ToTimings();

                Assert.That(afterRepair.ThinkingMaxHold,
                    Is.EqualTo(beforeRepair.ThinkingMaxHold).Within(1e-4f),
                    "Repairing the asset must not change what the character actually does.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_LeavesAValidThinkingWindowAlone()
        {
            ConvaiConversationFlowProfile profile = ConvaiConversationFlowProfile.CreateDefault();
            try
            {
                float minBefore = profile.ToTimings().ThinkingMinHold;
                float maxBefore = profile.ToTimings().ThinkingMaxHold;

                InvokeOnValidate(profile);

                Assert.That(profile.ToTimings().ThinkingMinHold, Is.EqualTo(minBefore).Within(1e-4f));
                Assert.That(profile.ToTimings().ThinkingMaxHold, Is.EqualTo(maxBefore).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
