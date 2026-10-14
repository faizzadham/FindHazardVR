using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Graph;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public class OneShotSlotTests
    {
        private PlayableGraph _graph;
        private OneShotSlot _slot;
        private readonly List<Object> _cleanup = new();
        private int _completedCount;

        [SetUp]
        public void SetUp()
        {
            _graph = PlayableGraph.Create("OneShotSlotTests");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            _slot = new OneShotSlot(_graph, AnimationCurve.Linear(0f, 0f, 1f, 1f));
            _slot.Completed += () => _completedCount++;
            _completedCount = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (_graph.IsValid()) _graph.Destroy();
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        private AnimationClip Clip(string name, float length = 1f)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve("", typeof(Transform), "localPosition.x",
                AnimationCurve.Constant(0f, length, 0f));
            _cleanup.Add(clip);
            return clip;
        }

        // Clip clocks are driven by the slot's own Tick — no graph evaluation needed.
        private void Step(float deltaTime) => _slot.Tick(deltaTime);

        [Test]
        public void PlayOnce_SingleClip_CompletesAfterClipLength()
        {
            var spec = OneShotSpec.For(Clip("main"));
            _slot.Play(in spec, 0f);
            Assert.AreEqual(OneShotSlot.SlotPhase.Main, _slot.Phase);

            for (int i = 0; i < 9; i++) Step(0.1f);
            Assert.AreEqual(OneShotSlot.SlotPhase.Main, _slot.Phase, "still playing at 0.9s");

            for (int i = 0; i < 4; i++) Step(0.1f);
            Assert.AreEqual(OneShotSlot.SlotPhase.Idle, _slot.Phase);
            Assert.AreEqual(1, _completedCount);
        }

        [Test]
        public void IntroMainOutro_ChainsInOrder()
        {
            var spec = OneShotSpec.For(Clip("main"));
            spec.Intro = Clip("intro", 0.5f);
            spec.Outro = Clip("outro", 0.5f);
            spec.ChainFadeSeconds = 0.05f;

            _slot.Play(in spec, 0f);
            Assert.AreEqual(OneShotSlot.SlotPhase.Intro, _slot.Phase);

            var seenPhases = new List<OneShotSlot.SlotPhase> { _slot.Phase };
            for (int i = 0; i < 40 && _slot.Phase != OneShotSlot.SlotPhase.Idle; i++)
            {
                Step(0.1f);
                if (seenPhases[^1] != _slot.Phase)
                    seenPhases.Add(_slot.Phase);
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    OneShotSlot.SlotPhase.Intro,
                    OneShotSlot.SlotPhase.Main,
                    OneShotSlot.SlotPhase.Outro,
                    OneShotSlot.SlotPhase.Idle
                },
                seenPhases);
            Assert.AreEqual(1, _completedCount);
        }

        [Test]
        public void Hold_RepeatsMainUntilStopRequested()
        {
            var spec = OneShotSpec.For(Clip("main"));
            spec.Loop = OneShotLoop.Hold;
            spec.ChainFadeSeconds = 0.05f;

            _slot.Play(in spec, 0f);

            // Well past two clip lengths: a Hold action must still be playing.
            for (int i = 0; i < 25; i++) Step(0.1f);
            Assert.AreEqual(OneShotSlot.SlotPhase.Main, _slot.Phase);
            Assert.GreaterOrEqual(_slot.CompletedLoops, 1, "main replayed at least once");
            Assert.AreEqual(0, _completedCount);

            _slot.RequestStop();
            for (int i = 0; i < 15 && _slot.Phase != OneShotSlot.SlotPhase.Idle; i++) Step(0.1f);

            Assert.AreEqual(OneShotSlot.SlotPhase.Idle, _slot.Phase);
            Assert.AreEqual(1, _completedCount);
        }

        [Test]
        public void LoopCount_PlaysMainRequestedTimes()
        {
            var spec = OneShotSpec.For(Clip("main"));
            spec.Loop = OneShotLoop.Count;
            spec.LoopCount = 2;
            spec.ChainFadeSeconds = 0.05f;

            _slot.Play(in spec, 0f);

            for (int i = 0; i < 40 && _slot.Phase != OneShotSlot.SlotPhase.Idle; i++) Step(0.1f);

            Assert.AreEqual(OneShotSlot.SlotPhase.Idle, _slot.Phase);
            Assert.AreEqual(2, _slot.CompletedLoops);
            Assert.AreEqual(1, _completedCount);
        }

        [Test]
        public void Play_ReplacesRunningChain()
        {
            var first = OneShotSpec.For(Clip("first"));
            first.Loop = OneShotLoop.Hold;
            _slot.Play(in first, 0f);
            Step(0.3f);

            var second = OneShotSpec.For(Clip("second"));
            _slot.Play(in second, 0.1f);

            Assert.AreEqual(OneShotSlot.SlotPhase.Main, _slot.Phase);
            Assert.AreEqual("second", _slot.ActiveClipName);
        }
    }
}
