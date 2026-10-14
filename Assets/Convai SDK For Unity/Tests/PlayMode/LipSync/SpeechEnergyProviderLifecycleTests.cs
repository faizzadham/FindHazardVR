using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.LipSync;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.LipSync
{
    /// <summary>
    ///     The three links that make <see cref="ISpeechEnergyProvider" /> flow for a character
    ///     carrying a <see cref="ConvaiLipSyncComponent" />: the adapter registers itself as the
    ///     character's provider and as a Cognition tickable when it is enabled, withdraws both when
    ///     it is disabled, and the bridge auto-provisions one when a LipSync component is present
    ///     but no adapter has been created yet.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These belong in Play Mode because every one of them is a claim about
    ///         <c>OnEnable</c>/<c>OnDisable</c>. Unity does not run those callbacks in Edit Mode
    ///         unless a component asks to with <c>[ExecuteAlways]</c>, and this adapter deliberately
    ///         does not: speech energy is a play-mode signal, and the whole provisioning path is
    ///         gated on <see cref="UnityEngine.Application.isPlaying" /> so that merely rendering an
    ///         inspector never grows components on the user's character.
    ///     </para>
    ///     <para>
    ///         They previously lived in an Edit Mode fixture, where the callbacks they assert on
    ///         simply never fired — so they reported the adapter as broken while the shipped
    ///         behaviour was correct. The zero-allocation and graceful-degradation cases that need
    ///         no lifecycle stay in Edit Mode, in
    ///         <c>SpeechEnergyProviderAutoProvisionTests</c>.
    ///     </para>
    /// </remarks>
    public sealed class SpeechEnergyProviderLifecycleTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        /// <summary>
        ///     Builds a character carrying a tick scheduler, a composition root and LipSync, with
        ///     the pieces in place before anything that registers against them is added.
        /// </summary>
        /// <remarks>
        ///     A bare test scene has no <c>ConvaiManager</c>, and <see cref="ConvaiCharacter" />
        ///     correctly says so the moment it wakes. That report is expected here rather than
        ///     silenced wholesale, so a genuine error raised by anything else in these tests still
        ///     fails them.
        /// </remarks>
        private void BuildCharacter(string name)
        {
            _root = new GameObject(name);

            LogAssert.Expect(LogType.Error, new Regex("Convai SDK Setup Error"));
            var character = _root.AddComponent<ConvaiCharacter>();
            GiveTheCharacterAnId(character);

            _root.AddComponent<EmbodimentTickScheduler>();
            _root.AddComponent<EmbodimentContext>();
            _root.AddComponent<ConvaiLipSyncComponent>();
        }

        /// <summary>
        ///     Sets the character's dashboard id, which is normally authored in the Inspector and
        ///     has no runtime setter.
        /// </summary>
        /// <remarks>
        ///     Without one, LipSync correctly refuses to start and disables itself, and the
        ///     fixture would be measuring a character the SDK has already declared unusable. This
        ///     reaches the serialized field directly; if it is ever renamed the assertion below
        ///     says so rather than the fixture quietly reverting to that state.
        /// </remarks>
        private static void GiveTheCharacterAnId(ConvaiCharacter character)
        {
            FieldInfo idField = typeof(ConvaiCharacter)
                .GetField("_characterId", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(idField, Is.Not.Null, "ConvaiCharacter must declare a serialized _characterId field.");

            idField.SetValue(character, "speech-energy-test-character");
        }

        private EmbodimentContext Context => _root.GetComponent<EmbodimentContext>();
        private EmbodimentTickScheduler Scheduler => _root.GetComponent<EmbodimentTickScheduler>();

        [UnityTest]
        public IEnumerator Adapter_OnEnable_RegistersAsProviderAndCognitionTickable()
        {
            BuildCharacter(nameof(Adapter_OnEnable_RegistersAsProviderAndCognitionTickable));
            yield return null;

            var adapter = _root.AddComponent<ConvaiLipSyncSpeechEnergyAdapter>();
            yield return null;

            Assert.That(Context.SpeechEnergyProvider, Is.SameAs(adapter),
                "Enabling the adapter must make it the character's speech energy provider.");
            Assert.That(CountRegistrations(adapter), Is.EqualTo(1),
                "The adapter must sample in the Cognition phase, so it registers as a tickable there.");
        }

        [UnityTest]
        public IEnumerator Adapter_OnDisable_WithdrawsTheProviderAndStopsTicking()
        {
            BuildCharacter(nameof(Adapter_OnDisable_WithdrawsTheProviderAndStopsTicking));
            yield return null;

            var adapter = _root.AddComponent<ConvaiLipSyncSpeechEnergyAdapter>();
            yield return null;

            Assume.That(Context.SpeechEnergyProvider, Is.SameAs(adapter));

            adapter.enabled = false;
            yield return null;

            Assert.That(Context.SpeechEnergyProvider, Is.Null,
                "Disabling must withdraw the provider rather than leave consumers reading a dead one.");
            Assert.That(CountRegistrations(adapter), Is.Zero,
                "Disabling must unregister the tickable so it never ticks again after teardown.");
        }

        [UnityTest]
        public IEnumerator Adapter_DisableThenReEnable_LeavesExactlyOneRegistration()
        {
            BuildCharacter(nameof(Adapter_DisableThenReEnable_LeavesExactlyOneRegistration));
            yield return null;

            var adapter = _root.AddComponent<ConvaiLipSyncSpeechEnergyAdapter>();
            yield return null;

            for (int cycle = 0; cycle < 2; cycle++)
            {
                adapter.enabled = false;
                yield return null;
                adapter.enabled = true;
                yield return null;
            }

            Assert.That(CountRegistrations(adapter), Is.EqualTo(1),
                "Repeated enable/disable cycles must never leave more than one live registration.");
            Assert.That(Context.SpeechEnergyProvider, Is.SameAs(adapter));
        }

        [UnityTest]
        public IEnumerator Bridge_LipSyncPresentAndNoAdapter_AutoProvisionsAndRegistersOne()
        {
            BuildCharacter(nameof(Bridge_LipSyncPresentAndNoAdapter_AutoProvisionsAndRegistersOne));
            yield return null;

            bool created = EmbodimentLipSyncBridge.TryRegisterSpeechEnergyAdapter(Context);
            yield return null;

            Assert.That(created, Is.True,
                "A ConvaiLipSyncComponent is present, so the adapter must be created and registered.");
            Assert.That(Context.SpeechEnergyProvider, Is.InstanceOf<ConvaiLipSyncSpeechEnergyAdapter>(),
                "The auto-provisioned adapter must be the registered provider.");
            Assert.That(_root.GetComponents<ConvaiLipSyncSpeechEnergyAdapter>().Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Bridge_CalledAgainAfterProvisioning_KeepsTheSameAdapter()
        {
            BuildCharacter(nameof(Bridge_CalledAgainAfterProvisioning_KeepsTheSameAdapter));
            yield return null;

            EmbodimentLipSyncBridge.TryRegisterSpeechEnergyAdapter(Context);
            yield return null;

            ISpeechEnergyProvider first = Context.SpeechEnergyProvider;
            Assume.That(first, Is.Not.Null);

            bool secondCallOk = EmbodimentLipSyncBridge.TryRegisterSpeechEnergyAdapter(Context);
            yield return null;

            Assert.That(secondCallOk, Is.True);
            Assert.That(Context.SpeechEnergyProvider, Is.SameAs(first),
                "Re-invoking the bridge must not replace the existing adapter.");
            Assert.That(_root.GetComponents<ConvaiLipSyncSpeechEnergyAdapter>().Length, Is.EqualTo(1),
                "Exactly one adapter component must exist after repeated bridge calls.");
        }

        /// <summary>
        ///     Asks the scheduler how many times <paramref name="tickable" /> appears in the
        ///     Cognition bucket. A count rather than a bool, so a double registration is visible
        ///     rather than indistinguishable from a healthy one.
        /// </summary>
        private int CountRegistrations(IEmbodimentTickable tickable)
        {
            var order = new List<IEmbodimentTickable>();
            Scheduler.GetPhaseOrder(EmbodimentTickPhase.Cognition, order);

            int count = 0;
            for (int i = 0; i < order.Count; i++)
                if (ReferenceEquals(order[i], tickable)) count++;

            return count;
        }
    }
}
