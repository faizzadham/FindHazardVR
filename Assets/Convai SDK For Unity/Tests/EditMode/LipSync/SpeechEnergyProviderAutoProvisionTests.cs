using System;
using System.Reflection;
using Convai.Modules.LipSync;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Runtime.Components;

namespace Convai.Tests.EditMode.LipSync
{
    /// <summary>
    ///     The parts of the speech-energy path that hold in Edit Mode: the bridge refuses to
    ///     provision an adapter for a character with no <see cref="ConvaiLipSyncComponent" />,
    ///     <see cref="EmbodimentContext.EnsureSpeechEnergyProvider" /> degrades to <c>null</c>
    ///     rather than growing components while the editor is merely rendering an inspector, the
    ///     adapter samples the underlying signal once per <see cref="IEmbodimentTickable" /> tick,
    ///     and the steady-state ensure/read path allocates nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="EmbodimentContext.EnsureSpeechEnergyProvider" /> only invokes
    ///         <c>EmbodimentLipSyncBridge.TryRegisterSpeechEnergyAdapter</c> when
    ///         <see cref="UnityEngine.Application.isPlaying" /> is <c>true</c> — a deliberate guard
    ///         against <c>AddComponent</c> churn in edit mode, asserted here rather than worked
    ///         around.
    ///     </para>
    ///     <para>
    ///         Everything this fixture cannot honestly assert lives in the Play Mode fixture
    ///         <c>SpeechEnergyProviderLifecycleTests</c>: registering, withdrawing and
    ///         auto-provisioning are all claims about <c>OnEnable</c>/<c>OnDisable</c>, and Unity
    ///         does not run those in Edit Mode for a component without <c>[ExecuteAlways]</c> —
    ///         which this adapter deliberately does not have. Asserting them here reported the
    ///         adapter as broken while the shipped behaviour was correct.
    ///     </para>
    /// </remarks>
    public sealed class SpeechEnergyProviderAutoProvisionTests
    {
        // ── EmbodimentLipSyncBridge: auto-provision presence/absence ──────────

        [Test]
        public void Bridge_NoLipSyncComponentPresent_ReturnsFalseAndRegistersNothing()
        {
            GameObject root = new("SpeechEnergy_NoLipSync");
            root.AddComponent<ConvaiCharacter>();
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();

                bool created = EmbodimentLipSyncBridge.TryRegisterSpeechEnergyAdapter(ctx);

                Assert.IsFalse(created, "No ConvaiLipSyncComponent in the hierarchy — nothing should be created.");
                Assert.IsNull(ctx.SpeechEnergyProvider, "Absent LipSync must degrade to a null provider, not throw.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        // Auto-provisioning when a LipSync component IS present is a Play Mode claim: the bridge
        // creates the adapter, and it is the adapter's own OnEnable that registers it. See
        // SpeechEnergyProviderLifecycleTests.

        // ── EmbodimentContext.EnsureSpeechEnergyProvider: gated-path graceful degrade ──

        [Test]
        public void EnsureSpeechEnergyProvider_NoProviderRegisteredAndNotPlaying_ReturnsNullGracefully()
        {
            GameObject root = new("SpeechEnergy_EnsureNoPlaying");
            root.AddComponent<ConvaiCharacter>();
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                root.AddComponent<ConvaiLipSyncComponent>();

                // Application.isPlaying is false in EditMode, so Ensure must not attempt the
                // bridge here — this is the exact production guard against edit-mode
                // AddComponent churn; the outcome is still a graceful null, not an exception.
                ISpeechEnergyProvider provider = ctx.EnsureSpeechEnergyProvider();

                Assert.IsNull(provider);
                Assert.AreEqual(0, root.GetComponents<ConvaiLipSyncSpeechEnergyAdapter>().Length,
                    "The gated path must not have created an adapter while not playing.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void EnsureSpeechEnergyProvider_AlreadyRegistered_ReturnsSameInstanceWithoutRescan()
        {
            GameObject root = new("SpeechEnergy_EnsureAlreadyRegistered");
            root.AddComponent<ConvaiCharacter>();
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                var fake = new FakeConfigurableSpeechEnergyProvider();
                ctx.Provide<ISpeechEnergyProvider>(fake);

                ISpeechEnergyProvider first = ctx.EnsureSpeechEnergyProvider();
                ISpeechEnergyProvider second = ctx.EnsureSpeechEnergyProvider();

                Assert.AreSame(fake, first);
                Assert.AreSame(first, second);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void EnsureSpeechEnergyProvider_SteadyState_AllocatesNoGarbage()
        {
            const int warmupIterations = 500;
            const int measuredIterations = 1000;

            GameObject root = new("SpeechEnergy_EnsureZeroAlloc");
            root.AddComponent<ConvaiCharacter>();
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                ctx.Provide<ISpeechEnergyProvider>(new FakeConfigurableSpeechEnergyProvider());

                // Warm up so any cold-path first-touch cost (JIT, delegate/log setup) settles
                // before the measured window, matching this codebase's zero-alloc gate
                // convention (see GesticulationDirectorZeroAllocTests).
                for (int i = 0; i < warmupIterations; i++)
                    ctx.EnsureSpeechEnergyProvider();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long allocationCount = AllocationCounter.Measure(() =>
                {
                    for (int i = 0; i < measuredIterations; i++)
                        ctx.EnsureSpeechEnergyProvider();
                });

                Assert.That(allocationCount, Is.EqualTo(0L),
                    $"EnsureSpeechEnergyProvider's fast (already-registered) path must not allocate; " +
                    $"measured {allocationCount} allocation events over {measuredIterations} calls.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── ConvaiLipSyncSpeechEnergyAdapter: tick-driven sampling + scheduler lifecycle ──

        [Test]
        public void Adapter_EmbodimentTick_SamplesTheUnderlyingProviderEachCall()
        {
            GameObject root = new("SpeechEnergy_AdapterTicks");
            root.AddComponent<ConvaiCharacter>();
            try
            {
                root.AddComponent<ConvaiLipSyncComponent>();
                var adapter = root.AddComponent<ConvaiLipSyncSpeechEnergyAdapter>();

                var tickable = (IEmbodimentTickable)adapter;
                Assert.AreEqual(EmbodimentTickPhase.Cognition, tickable.Phase,
                    "The adapter must sample in the Cognition phase alongside its consumers. " +
                    "When auto-provisioned mid-tick, the scheduler appends this tickable after " +
                    "the consumer that triggered provisioning, so the very first read is one " +
                    "Cognition tick behind — acceptable and consistent with other lazily " +
                    "provisioned sources, negligible against the ~80ms RMS window.");

                // The RMS window is a fixed-size ring, so its fill count proves one push per tick
                // only up to capacity, after which it saturates by design. Ticking well past
                // capacity and expecting the fill to keep climbing is how this test used to read
                // the ring's correct behaviour as a missed sample.
                const string oncePerTick =
                    "Each EmbodimentTick call must forward to Sample exactly once — the window " +
                    "fill count is the direct, deterministic proof of that, independent of " +
                    "whether the underlying LipSync stream is actually playing audio.";

                // The first tick is also what builds the provider: Unity does not run the
                // adapter's Awake in Edit Mode, and Sample provisions lazily.
                tickable.EmbodimentTick(1f / 60f);
                Assert.AreEqual(1, GetSpeechEnergyWindowCount(adapter), oncePerTick);

                int capacity = GetSpeechEnergyWindowCapacity(adapter);
                Assume.That(capacity, Is.GreaterThan(1), "A one-slot window could not tell these cases apart.");

                for (int i = 2; i <= capacity; i++)
                {
                    tickable.EmbodimentTick(1f / 60f);
                    Assert.AreEqual(i, GetSpeechEnergyWindowCount(adapter), oncePerTick);
                }

                for (int i = 0; i < 15; i++)
                    tickable.EmbodimentTick(1f / 60f);

                Assert.AreEqual(capacity, GetSpeechEnergyWindowCount(adapter),
                    "Once full, the window keeps the most recent capacity samples and stops growing.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        // Registering with the scheduler, withdrawing on disable, and surviving enable/disable
        // cycles are Play Mode claims — Unity never runs OnEnable/OnDisable for this adapter in
        // Edit Mode. See SpeechEnergyProviderLifecycleTests.

        // ── helpers ─────────────────────────────────────────────────────────────

        private static int GetSpeechEnergyWindowCapacity(ConvaiLipSyncSpeechEnergyAdapter adapter) =>
            GetSpeechEnergyWindow(adapter).Capacity;

        private static int GetSpeechEnergyWindowCount(ConvaiLipSyncSpeechEnergyAdapter adapter) =>
            GetSpeechEnergyWindow(adapter).Count;

        private static SpeechEnergyWindow GetSpeechEnergyWindow(ConvaiLipSyncSpeechEnergyAdapter adapter)
        {
            FieldInfo providerField = typeof(ConvaiLipSyncSpeechEnergyAdapter)
                .GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(providerField, "Adapter must expose a private _provider field.");

            var provider = providerField.GetValue(adapter) as LipSyncSpeechEnergyProvider;
            Assert.NotNull(provider, "Adapter must have constructed its LipSyncSpeechEnergyProvider.");

            FieldInfo windowField = typeof(LipSyncSpeechEnergyProvider)
                .GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(windowField, "Provider must expose a private _window field.");

            object boxedWindow = windowField.GetValue(provider);
            return (SpeechEnergyWindow)boxedWindow;
        }

        private sealed class FakeConfigurableSpeechEnergyProvider : IConfigurableSpeechEnergyProvider
        {
            public float Current { get; private set; }
            public void Configure(float windowSeconds) { }
            public void Sample(float deltaTime) { }
        }
    }
}
