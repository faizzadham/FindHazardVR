using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyAnimation;
using Convai.Modules.BodyAnimation.Core;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Core.Lifecycle;
using Convai.Modules.BodyAnimation.Core.Policy;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     coverage for <see cref="LayerStackBuilder" />: the shared layer-stack
    ///     construction used by both <c>ConvaiBodyAnimationController.BuildRuntime</c> and
    ///     <c>TryBeginSetHandoff</c>. The point of this suite is the fix itself — before the
    ///     shared builder existed the
    ///     handoff path skipped re-creating the social-spacing policy; routing both callers
    ///     through one builder makes that impossible by construction, which is asserted directly
    ///     below rather than only implied by "both callers use the same method".
    /// </summary>
    public sealed class LayerStackBuilderTests
    {
        private static ConvaiBodyAnimationConfig CreateConfig() => ConvaiBodyAnimationConfig.CreateDefault();

        private static ConvaiBodyAnimationSet CreateSet(List<Object> cleanup)
        {
            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);
            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, null, null, mask);
            return set;
        }

        private static object GetPrivateField(object instance, string name) =>
            instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

        [Test]
        public void Build_PopulatesLayersInPortOrder_AndTheResultBundle()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("LayerStackBuilderTest");
            var root = new GameObject("LayerStackBuilderTestRoot");
            var layers = new List<IAnimationLayer>();
            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);
                var trace = new Convai.Modules.BodyAnimation.Core.Diagnostics.AnimTrace("LayerStackBuilderTest");
                var socialSpacing = new SocialSpacingRunner();

                var args = new LayerStackBuilder.Args(
                    graph, set, config, trace, randomSeed: 42, motionScale: 1f,
                    characterRoot: root.transform, animator: null, locomotion: null,
                    onStateChanged: null, onActionEvent: null, onGestureResolved: null,
                    socialSpacing: socialSpacing);

                LayerStackBuilder.Result result = LayerStackBuilder.Build(in args, layers);

                Assert.AreEqual(4, layers.Count);
                Assert.AreSame(result.LocomotionLayer, layers[LayerPorts.Locomotion]);
                Assert.AreSame(result.TalkLayer, layers[LayerPorts.Talk]);
                Assert.AreSame(result.ActionLayer, layers[LayerPorts.Action]);
                Assert.AreSame(result.PointingLayer, layers[LayerPorts.Pointing]);

                Assert.IsNotNull(result.Mixer);
                Assert.IsNotNull(result.LayerRuntime);
                Assert.AreSame(set, result.LayerRuntime.Set);
                Assert.AreSame(config, result.LayerRuntime.Config);
                Assert.AreSame(trace, result.LayerRuntime.Trace);
                Assert.AreEqual(42, result.LayerRuntime.RandomSeed);
                Assert.AreEqual(1f, result.LayerRuntime.MotionScale);
                Assert.AreSame(root.transform, result.LayerRuntime.CharacterRoot);
                Assert.AreSame(result.Mixer, result.LayerRuntime.Mixer);

                Assert.IsNotNull(result.GesturePerformer);
                Assert.IsNotNull(result.ReferentialDirector);
                Assert.IsNotNull(result.AmbientDirector);
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Build_AlwaysRebuildsSocialSpacing()
        {
            // This is the regression test for finding before the shared builder existed,
            // the set-swap handoff path skipped re-creating the social-spacing policy, so a
            // handed-off character silently lost social spacing. Routing both build paths
            // through LayerStackBuilder means every call rebuilds it — asserted here by reading
            // SocialSpacingRunner's own private policy field, which must be non-null after Build.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("LayerStackBuilderSocialSpacingTest");
            var root = new GameObject("LayerStackBuilderSocialSpacingTestRoot");
            var layers = new List<IAnimationLayer>();
            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);
                var trace = new Convai.Modules.BodyAnimation.Core.Diagnostics.AnimTrace("LayerStackBuilderSocialSpacingTest");
                var socialSpacing = new SocialSpacingRunner();

                Assert.IsNull(GetPrivateField(socialSpacing, "_policy"), "sanity: no policy before any build.");

                var args = new LayerStackBuilder.Args(
                    graph, set, config, trace, randomSeed: 1, motionScale: 1f,
                    characterRoot: root.transform, animator: null, locomotion: null,
                    onStateChanged: null, onActionEvent: null, onGestureResolved: null,
                    socialSpacing: socialSpacing);

                LayerStackBuilder.Build(in args, layers);

                Assert.IsNotNull(GetPrivateField(socialSpacing, "_policy"),
                    "LayerStackBuilder.Build must (re)create the social-spacing policy on every call — " +
                    "this is what removes the divergence between the build and handoff paths.");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Build_WiresActionEventDelegate_ToTheActionLayer()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("LayerStackBuilderActionEventTest");
            var root = new GameObject("LayerStackBuilderActionEventTestRoot");
            var layers = new List<IAnimationLayer>();
            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);
                var trace = new Convai.Modules.BodyAnimation.Core.Diagnostics.AnimTrace("LayerStackBuilderActionEventTest");

                int calls = 0;
                void OnActionEvent(BodyAnimationActionEvent evt) => calls++;

                var args = new LayerStackBuilder.Args(
                    graph, set, config, trace, randomSeed: 1, motionScale: 1f,
                    characterRoot: root.transform, animator: null, locomotion: null,
                    onStateChanged: null, onActionEvent: OnActionEvent, onGestureResolved: null,
                    socialSpacing: new SocialSpacingRunner());

                LayerStackBuilder.Result result = LayerStackBuilder.Build(in args, layers);
                result.ActionLayer.LifecycleChanged?.Invoke(default);

                Assert.AreEqual(1, calls, "the ActionLayer's LifecycleChanged must forward to onActionEvent.");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Build_WiresGestureResolvedDelegate_ToTheReferentialDirector()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("LayerStackBuilderGestureResolvedTest");
            var root = new GameObject("LayerStackBuilderGestureResolvedTestRoot");
            var layers = new List<IAnimationLayer>();
            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);
                var trace = new Convai.Modules.BodyAnimation.Core.Diagnostics.AnimTrace("LayerStackBuilderGestureResolvedTest");

                int calls = 0;
                void OnGestureResolved(GestureCueKind kind, bool authoredPlayed) => calls++;

                var args = new LayerStackBuilder.Args(
                    graph, set, config, trace, randomSeed: 1, motionScale: 1f,
                    characterRoot: root.transform, animator: null, locomotion: null,
                    onStateChanged: null, onActionEvent: null, onGestureResolved: OnGestureResolved,
                    socialSpacing: new SocialSpacingRunner());

                LayerStackBuilder.Result result = LayerStackBuilder.Build(in args, layers);

                FieldInfo eventField = typeof(ReferentialGestureDirector).GetField(
                    "GestureResolved", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(eventField, "ReferentialGestureDirector.GestureResolved must be a standard field-backed event.");
                var handler = (System.Action<GestureCueKind, bool>)eventField.GetValue(result.ReferentialDirector);
                handler?.Invoke(GestureCueKind.Affirmative, false);

                Assert.AreEqual(1, calls, "GestureResolved must forward to onGestureResolved.");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Build_ReusesTheProvidedLayerList_InsteadOfAllocatingANewOne()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("LayerStackBuilderListReuseTest");
            var root = new GameObject("LayerStackBuilderListReuseTestRoot");
            var layers = new List<IAnimationLayer>(LayerPorts.Count);
            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);
                var trace = new Convai.Modules.BodyAnimation.Core.Diagnostics.AnimTrace("LayerStackBuilderListReuseTest");

                var args = new LayerStackBuilder.Args(
                    graph, set, config, trace, randomSeed: 1, motionScale: 1f,
                    characterRoot: root.transform, animator: null, locomotion: null,
                    onStateChanged: null, onActionEvent: null, onGestureResolved: null,
                    socialSpacing: new SocialSpacingRunner());

                List<IAnimationLayer> returnedList = layers;
                LayerStackBuilder.Build(in args, layers);

                Assert.AreSame(returnedList, layers, "Build must populate the caller's list, not replace the reference.");
                Assert.AreEqual(4, layers.Count);
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }
    }
}
