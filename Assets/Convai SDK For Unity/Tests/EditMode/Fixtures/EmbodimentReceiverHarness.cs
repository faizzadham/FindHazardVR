using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using UnityEngine;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Drives a <typeparamref name="TController" /> through its MonoBehaviour lifecycle
    ///     without pumping real Unity frames. Awake and OnEnable fire automatically when the
    ///     component is added to the rig's root <see cref="GameObject" /> because
    ///     <see cref="ConvaiCharacterModule{TProfile}" /> carries <c>[ExecuteAlways]</c>,
    ///     which causes Unity to honour the full lifecycle even in EditMode.
    ///     Use <see cref="Enable" /> / <see cref="Disable" /> to drive OnEnable / OnDisable,
    ///     and <see cref="Tick" /> to invoke <see cref="IEmbodimentTickable.EmbodimentTick" />
    ///     directly. No private reflection — relies on public interfaces only.
    /// </summary>
    public sealed class EmbodimentReceiverHarness<TController, TProfile>
        where TController : ConvaiCharacterModule<TProfile>, IEmbodimentTickable
        where TProfile : ScriptableObject
    {
        public TController Controller { get; }

        public EmbodimentReceiverHarness(EmbodimentTestRig rig)
        {
            Controller = rig.AddComponent<TController>();
        }

        public void Enable() => Controller.enabled = true;

        public void Disable() => Controller.enabled = false;

        public string ModuleId => ((IEmbodimentProfileReceiver)Controller).ModuleId;

        public EmbodimentTickPhase Phase => ((IEmbodimentTickable)Controller).Phase;

        public bool ApplyProfile(TProfile newProfile)
            => ((IEmbodimentProfileReceiver)Controller).ApplyProfile(newProfile);

        public bool ApplyProfile(ScriptableObject candidate)
            => ((IEmbodimentProfileReceiver)Controller).ApplyProfile(candidate);

        public void Tick(float dt)
            => ((IEmbodimentTickable)Controller).EmbodimentTick(dt);
    }
}
