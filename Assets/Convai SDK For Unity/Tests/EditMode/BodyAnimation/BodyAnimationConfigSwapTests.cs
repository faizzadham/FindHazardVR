using System.Reflection;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for the single-config guarantees: <see cref="ConvaiBodyAnimationController.SetConfig" />
    ///     before the runtime is built is adopted by <c>EffectiveConfig</c> (what the next
    ///     <c>BuildRuntime</c> reads); after the runtime is built it routes through the existing
    ///     <c>ApplyConfigInPlace</c>; and <c>EffectiveConfig</c> always prefers a runtime override
    ///     over the serialized/profile config, mirroring <c>SetAnimationSet</c>'s override.
    /// </summary>
    /// <remarks>
    ///     <c>BuildRuntime</c> is a no-op outside Play Mode
    ///     (<c>!UnityEngine.Application.isPlaying</c>), so the "after build" test fakes the exact
    ///     private state <c>ApplyConfigInPlace</c> reads via reflection rather than actually
    ///     building the graph — the same constraint every other controller-level EditMode test in
    ///     this suite works around.
    /// </remarks>
    public sealed class BodyAnimationConfigSwapTests
    {
        private static FieldInfo Field(string name) =>
            typeof(ConvaiBodyAnimationController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static PropertyInfo Property(string name) =>
            typeof(ConvaiBodyAnimationController).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        ///     A controller on a valid embodiment root. The <see cref="EmbodimentContext" /> goes on
        ///     first and is not optional: <c>ConvaiCharacterModule.OnEnable</c> resolves its context
        ///     the moment <c>AddComponent</c> returns, and without one it correctly logs a setup
        ///     error that the test framework counts as an unexpected failure.
        /// </summary>
        private static ConvaiBodyAnimationController CreateController(out GameObject root)
        {
            root = new GameObject("BodyAnimationConfigSwapTestCharacter");
            root.AddComponent<EmbodimentContext>();
            return root.AddComponent<ConvaiBodyAnimationController>();
        }

        private static ConvaiBodyAnimationConfig EffectiveConfigOf(ConvaiBodyAnimationController controller) =>
            (ConvaiBodyAnimationConfig)Property("EffectiveConfig").GetValue(controller);

        [Test]
        public void SetConfig_BeforeBuild_IsAdoptedByEffectiveConfig()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            ConvaiBodyAnimationConfig newConfig = ConvaiBodyAnimationConfig.CreateDefault();
            try
            {
                controller.SetConfig(newConfig);

                Assert.AreSame(newConfig, EffectiveConfigOf(controller),
                    "SetConfig before the runtime is built must be adopted by EffectiveConfig, " +
                    "which the next BuildRuntime call reads.");
                Assert.AreSame(newConfig, controller.Config);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(newConfig);
            }
        }

        [Test]
        public void SetConfig_Null_WarnsAndIsANoOp()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                ConvaiBodyAnimationConfig before = EffectiveConfigOf(controller);

                Assert.DoesNotThrow(() => controller.SetConfig(null));

                Assert.AreSame(before, EffectiveConfigOf(controller),
                    "A null SetConfig call must be refused, not clear/replace the effective config.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SetConfig_AfterBuild_RoutesThroughApplyConfigInPlace()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            ConvaiBodyAnimationConfig initialConfig = ConvaiBodyAnimationConfig.CreateDefault();
            ConvaiBodyAnimationConfig newConfig = ConvaiBodyAnimationConfig.CreateDefault();
            var trace = new AnimTrace("BodyAnimationConfigSwapTests") { Verbosity = AnimTraceVerbosity.State };

            var layerRuntime = new LayerRuntime
            {
                Config = initialConfig,
                Trace = trace,
                RandomSeed = 11,
                CharacterRoot = controller.transform
            };

            try
            {
                // Simulate "runtime already built" — the exact private state ApplyConfigInPlace
                // reads/writes — without going through BuildRuntime, which no-ops in Edit Mode.
                Field("_runtimeBuilt").SetValue(controller, true);
                Field("_builtConfig").SetValue(controller, initialConfig);
                Field("_trace").SetValue(controller, trace);
                Field("_layerRuntime").SetValue(controller, layerRuntime);

                controller.SetConfig(newConfig);

                Assert.AreSame(newConfig, Field("_builtConfig").GetValue(controller),
                    "SetConfig on a built runtime must route through ApplyConfigInPlace, which updates _builtConfig.");
                Assert.AreSame(newConfig, layerRuntime.Config,
                    "ApplyConfigInPlace must push the new config onto the live LayerRuntime every layer reads.");
            }
            finally
            {
                // Prevent OnDisable's TeardownRuntime from thinking there is a real graph/layer
                // stack to tear down — there isn't one in this faked-in state.
                Field("_runtimeBuilt").SetValue(controller, false);
                Field("_layerRuntime").SetValue(controller, null);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(initialConfig);
                Object.DestroyImmediate(newConfig);
            }
        }

        [Test]
        public void EffectiveConfig_PrefersRuntimeOverride_OverSerializedConfig()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            ConvaiBodyAnimationConfig serializedConfig = ConvaiBodyAnimationConfig.CreateDefault();
            ConvaiBodyAnimationConfig overrideConfig = ConvaiBodyAnimationConfig.CreateDefault();
            try
            {
                Field("_config").SetValue(controller, serializedConfig);
                Assert.AreSame(serializedConfig, EffectiveConfigOf(controller),
                    "Sanity check: with no override, EffectiveConfig must read the serialized _config.");

                controller.SetConfig(overrideConfig);

                Assert.AreSame(overrideConfig, EffectiveConfigOf(controller),
                    "A runtime SetConfig override must win over the serialized config, exactly as " +
                    "SetAnimationSet's override wins over the serialized animation set.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(serializedConfig);
                Object.DestroyImmediate(overrideConfig);
            }
        }
    }
}
