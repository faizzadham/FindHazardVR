using System.Collections;
using System.Collections.Generic;
using Convai.Runtime.Animation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Embodiment
{
    public sealed class EmbodimentTickSchedulerTests
    {
        private GameObject _host;
        private EmbodimentTickScheduler _scheduler;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject(nameof(EmbodimentTickSchedulerTests));
            _scheduler = _host.AddComponent<EmbodimentTickScheduler>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
        }

        [UnityTest]
        public IEnumerator Update_TicksPhasesInCognitionExpressionFinalizeOrder()
        {
            var order = new List<string>();
            var cognition = new Recorder(EmbodimentTickPhase.Cognition, "cog", order);
            var expression = new Recorder(EmbodimentTickPhase.Expression, "exp", order);
            var finalize = new Recorder(EmbodimentTickPhase.Finalize, "fin", order);

            _scheduler.Register(expression);
            _scheduler.Register(finalize);
            _scheduler.Register(cognition);

            yield return null;

            Assert.That(order, Is.EqualTo(new List<string> { "cog", "exp", "fin" }),
                "Scheduler must tick Cognition → Expression → Finalize per frame.");
        }

        [UnityTest]
        public IEnumerator Unregister_StopsTickingAfterNextFrame()
        {
            var order = new List<string>();
            var recorder = new Recorder(EmbodimentTickPhase.Expression, "t", order);

            _scheduler.Register(recorder);
            yield return null;
            Assert.That(order.Count, Is.EqualTo(1));

            _scheduler.Unregister(recorder);
            yield return null;

            Assert.That(order.Count, Is.EqualTo(1), "Unregistered tickable must not be ticked.");
        }

        [UnityTest]
        public IEnumerator RegistrationDuringTick_IsDeferredToNextFrame()
        {
            var order = new List<string>();
            var late = new Recorder(EmbodimentTickPhase.Cognition, "late", order);
            var registrar = new SelfRegisteringTickable(_scheduler, late);

            _scheduler.Register(registrar);
            yield return null;

            Assert.That(order, Does.Not.Contain("late"),
                "Mid-tick registrations must be deferred to the next frame.");

            yield return null;
            Assert.That(order, Contains.Item("late"));
        }

        private sealed class Recorder : IEmbodimentTickable
        {
            private readonly string _id;
            private readonly List<string> _sink;
            public Recorder(EmbodimentTickPhase phase, string id, List<string> sink)
            {
                Phase = phase;
                _id = id;
                _sink = sink;
            }

            public EmbodimentTickPhase Phase { get; }
            public void EmbodimentTick(float deltaTime) => _sink.Add(_id);
        }

        private sealed class SelfRegisteringTickable : IEmbodimentTickable
        {
            private readonly EmbodimentTickScheduler _scheduler;
            private readonly IEmbodimentTickable _target;
            private bool _registered;

            public SelfRegisteringTickable(EmbodimentTickScheduler scheduler, IEmbodimentTickable target)
            {
                _scheduler = scheduler;
                _target = target;
            }

            public EmbodimentTickPhase Phase => EmbodimentTickPhase.Cognition;

            public void EmbodimentTick(float deltaTime)
            {
                if (_registered) return;
                _scheduler.Register(_target);
                _registered = true;
            }
        }
    }
}
