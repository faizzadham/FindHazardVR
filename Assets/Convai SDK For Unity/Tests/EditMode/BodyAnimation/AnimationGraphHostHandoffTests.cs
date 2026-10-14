using Convai.Modules.BodyAnimation.Core.Graph;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class AnimationGraphHostHandoffTests
    {
        [Test]
        public void RootHandoff_CompletesAndRetiredSubgraphCanBeDestroyed()
        {
            var root = new GameObject("RootHandoffTest");
            var animator = root.AddComponent<Animator>();
            AnimationGraphHost host = null;
            try
            {
                host = new AnimationGraphHost(animator, root.name, null);
                var first = AnimationMixerPlayable.Create(host.Graph, 0);
                var second = AnimationMixerPlayable.Create(host.Graph, 0);
                host.SetRoot(first);

                host.BeginRootHandoff(second, 0.2f);
                Assert.That(host.IsRootHandoffActive, Is.True);
                Assert.That(host.TickRootHandoff(0.1f), Is.False);
                Assert.That(host.TickRootHandoff(0.1f), Is.True);
                Assert.That(host.IsRootHandoffActive, Is.False);

                var retiring = host.TakeRetiringRoot();
                Assert.That(retiring.IsValid(), Is.True);
                Assert.DoesNotThrow(() => host.DestroyRetiredSubgraph(retiring));
            }
            finally
            {
                host?.Dispose();
                Object.DestroyImmediate(root);
            }
        }
    }
}
