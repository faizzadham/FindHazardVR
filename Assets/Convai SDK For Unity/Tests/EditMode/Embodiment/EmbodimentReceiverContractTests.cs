using Convai.Domain.Embodiment.Modules;
using Convai.Modules.ConversationFlow.Components;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Animation;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;

// One fixture per embodiment receiver, each asserting the same contract: the module id it claims,
// the tick phase it runs in, and that its profile's CreateDefault produces something the receiver
// accepts. Only modules built on ConvaiCharacterModule<TProfile> belong here — Gaze, Body Animation
// and Body Language are not receivers of that shape and carry their own suites.
namespace Convai.Tests.EditMode.ConversationFlow
{
    [TestFixture]
    public sealed class ConvaiConversationFlowControllerInvariantsTests
        : EmbodimentReceiverTestsBase<ConvaiConversationFlowController, ConvaiConversationFlowProfile>
    {
        protected override string ExpectedModuleId => ModuleIds.ConversationFlow;
        protected override EmbodimentTickPhase ExpectedPhase => EmbodimentTickPhase.Cognition;

        protected override ConvaiConversationFlowProfile CreateValidProfile() =>
            ConvaiConversationFlowProfile.CreateDefault();
    }
}

namespace Convai.Tests.EditMode.Emotion
{
    [TestFixture]
    public sealed class ConvaiEmotionControllerInvariantsTests
        : EmbodimentReceiverTestsBase<ConvaiEmotionController, ConvaiEmotionProfile>
    {
        protected override string ExpectedModuleId => ModuleIds.Emotion;
        protected override EmbodimentTickPhase ExpectedPhase => EmbodimentTickPhase.Cognition;

        protected override ConvaiEmotionProfile CreateValidProfile() =>
            ConvaiEmotionProfile.CreateDefault();
    }
}
