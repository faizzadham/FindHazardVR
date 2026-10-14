using System.Collections.Generic;
using System.Linq;
using Convai.Modules.ConversationFlow.Components;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Cross-module profile ownership and enable/disable behavior for Emotion, ConversationFlow
    ///     and BodyAnimation. Actual BodyAnimation graph behavior belongs to its PlayMode tests.
    /// </summary>
    [TestFixture]
    public sealed class EmbodimentStackIntegrationTests
    {
        private EmbodimentTestRig _rig;

        [SetUp]
        public void SetUp()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _rig = EmbodimentTestRig.Create(nameof(EmbodimentStackIntegrationTests));
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        [Test]
        public void ThreeModules_ProfileRoutingAndLifecycle_KeepDistinctOwners()
        {
            var emotion = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            var flow = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
            var body = new EmbodimentReceiverHarness<ConvaiBodyAnimationController, ConvaiBodyAnimationProfile>(_rig);
            Component[] owners = { emotion.Controller, flow.Controller, body.Controller };
            ScriptableObject[] profiles =
            {
                ConvaiEmotionProfile.CreateDefault(),
                ConvaiConversationFlowProfile.CreateDefault(),
                ConvaiBodyAnimationProfile.CreateDefault()
            };
            var registrations = new List<EmbodimentProfileReceiverRegistration>();

            try
            {
                registrations.Clear();
                _rig.Context.GetProfileReceivers(registrations);
                Assert.That(registrations.Select(item => item.Owner), Is.EquivalentTo(owners));
                Assert.That(_rig.Context.EmotionStateSource, Is.SameAs(emotion.Controller));
                Assert.That(_rig.Context.EmotionMouthProvider, Is.SameAs(emotion.Controller));
                Assert.That(_rig.Context.ConversationFlowSource, Is.SameAs(flow.Controller));

                for (int i = 0; i < owners.Length; i++)
                {
                    IEmbodimentProfileReceiver receiver = registrations.Single(item => item.Owner == owners[i]).Receiver;
                    Assert.That(receiver.ApplyProfile(profiles[i]), Is.True);
                    for (int j = 0; j < profiles.Length; j++)
                    {
                        if (i == j) continue;
                        Assert.That(receiver.ApplyProfile(profiles[j]), Is.False,
                            "A module must reject another module's profile.");
                    }
                    using var serialized = new SerializedObject(owners[i]);
                    Assert.That(serialized.FindProperty("profile").objectReferenceValue, Is.SameAs(profiles[i]),
                        "Rejected profiles must not replace the owning module's assigned asset.");
                }

                body.Disable();
                emotion.Disable();
                flow.Disable();
                registrations.Clear();
                _rig.Context.GetProfileReceivers(registrations);
                Assert.That(registrations, Is.Empty);
                Assert.That(_rig.Context.EmotionStateSource, Is.Null);
                Assert.That(_rig.Context.EmotionMouthProvider, Is.Null);
                Assert.That(_rig.Context.ConversationFlowSource, Is.Null);

                flow.Enable();
                emotion.Enable();
                body.Enable();
                registrations.Clear();
                _rig.Context.GetProfileReceivers(registrations);
                Assert.That(registrations.Select(item => item.Owner), Is.EquivalentTo(owners),
                    "Re-enable must restore each of the three owners exactly once.");
                Assert.That(_rig.Context.EmotionStateSource, Is.SameAs(emotion.Controller));
                Assert.That(_rig.Context.EmotionMouthProvider, Is.SameAs(emotion.Controller));
                Assert.That(_rig.Context.ConversationFlowSource, Is.SameAs(flow.Controller));
            }
            finally
            {
                body.Disable();
                emotion.Disable();
                flow.Disable();
                foreach (ScriptableObject profile in profiles) Object.DestroyImmediate(profile);
            }
        }
    }
}
