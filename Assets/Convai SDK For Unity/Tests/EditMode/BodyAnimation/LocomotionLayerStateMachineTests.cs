using System;
using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Drives <see cref="LocomotionLayer" /> end-to-end through a stubbed
    ///     <see cref="ILocomotionDrive" /> — no NavMeshAgent, no scene, fully deterministic.
    /// </summary>
    public class LocomotionLayerStateMachineTests
    {
        // ------------------------------------------------------------------ stub drive

        private sealed class StubLocomotionDrive : ILocomotionDrive
        {
            public bool IsMoving { get; set; }
            public float Speed { get; set; }
            public float DesiredSpeed { get; set; }
            public float RemainingDistance { get; set; }
            public float SignedAngleToSteering { get; set; }
            public Vector3 Destination { get; set; }
            public bool InManagedMotion { get; private set; }
            public bool RotationDrivenExternally { get; set; }
            public bool PathPending { get; set; }

            public readonly List<bool> FreezeAgentCalls = new();
            public int BeginManagedMotionCount { get; private set; }
            public int EndManagedMotionCount { get; private set; }
            public float LastManagedSpeed { get; private set; }
            public int ReleaseGateCount { get; private set; }
            public int CompleteMoveFromAnimationCount { get; private set; }
            public int StopCount { get; private set; }
            public float ConfiguredWalkSpeed { get; private set; }
            public float ConfiguredJogSpeed { get; private set; }

            public event Action<bool> MoveEnded;

            public void RaiseMoveEnded(bool reachedDestination) => MoveEnded?.Invoke(reachedDestination);

            public void Stop() => StopCount++;

            public void FreezeAgent(bool frozen) => FreezeAgentCalls.Add(frozen);

            public void BeginManagedMotion()
            {
                BeginManagedMotionCount++;
                InManagedMotion = true;
            }

            public void SetManagedSpeed(float speed) => LastManagedSpeed = speed;

            public void EndManagedMotion()
            {
                EndManagedMotionCount++;
                InManagedMotion = false;
            }

            public void ReleaseAnimationStartGate() => ReleaseGateCount++;

            public void CompleteMoveFromAnimation() => CompleteMoveFromAnimationCount++;

            public void SetAnimationStartGate(bool enabled)
            {
                // Not exercised by these tests — the layer only calls this in Initialize/Teardown.
            }

            public void ConfigureSpeeds(float walkSpeed, float jogSpeed)
            {
                ConfiguredWalkSpeed = walkSpeed;
                ConfiguredJogSpeed = jogSpeed;
            }
        }

        // ------------------------------------------------------------------ builders

        private static AnimationClip MakeClip(List<Object> cleanup, string name, float length, bool looping)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0f, length, 0f));

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = looping;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            cleanup.Add(clip);
            return clip;
        }

        private static IdleEntry MakeIdle(
            List<Object> cleanup, string name, params (string label, float mult)[] affinities)
        {
            AnimationClip clip = MakeClip(cleanup, name, 1f, true);
            var list = new List<EmotionAffinity>();
            foreach ((string label, float mult) in affinities)
            {
                var affinity = new EmotionAffinity();
                affinity.Initialize(label, mult);
                list.Add(affinity);
            }

            var idle = new IdleEntry();
            idle.Initialize(clip, 1f, list);
            return idle;
        }

        private static ConvaiBodyAnimationSet MakeSet(List<Object> cleanup, List<IdleEntry> idles = null)
        {
            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);

            idles ??= new List<IdleEntry> { MakeIdle(cleanup, "idle_default") };
            set.InitializeContent("Test", idles, null, null, null);
            return set;
        }

        private static void AddWalk(List<Object> cleanup, ConvaiBodyAnimationSet set)
        {
            AnimationClip clip = MakeClip(cleanup, "walk", 1f, true);
            set.Locomotion.Walk.Initialize(clip);
            set.Locomotion.Walk.Metadata.SetAnalyzed(
                1.2f, 0f, 0f, new AnimationCurve(), new AnimationCurve(),
                new[] { 0.2f }, new[] { 0.7f });
        }

        /// <summary>Turn clips carry NO analyzed yaw — exercises the nominal-yaw-drive fallback.</summary>
        private static void AddTurns(List<Object> cleanup, ConvaiBodyAnimationSet set)
        {
            set.Locomotion.Turn90Left.Initialize(MakeClip(cleanup, "turn90L", 1f, false));
            set.Locomotion.Turn90Right.Initialize(MakeClip(cleanup, "turn90R", 1f, false));
            set.Locomotion.Turn180Left.Initialize(MakeClip(cleanup, "turn180L", 1f, false));
            set.Locomotion.Turn180Right.Initialize(MakeClip(cleanup, "turn180R", 1f, false));
        }

        private static void AddWalkStops(
            List<Object> cleanup, ConvaiBodyAnimationSet set, AnimationCurve distanceCurve)
        {
            AnimationClip left = MakeClip(cleanup, "walkStopLF", 1f, false);
            AnimationClip right = MakeClip(cleanup, "walkStopRF", 1f, false);

            set.Locomotion.WalkStopLeftPlant.Initialize(left);
            set.Locomotion.WalkStopLeftPlant.Metadata.SetAnalyzed(
                1.2f, 1f, 0f, distanceCurve, new AnimationCurve(), new[] { 0.1f }, new[] { 0.6f });

            set.Locomotion.WalkStopRightPlant.Initialize(right);
            set.Locomotion.WalkStopRightPlant.Metadata.SetAnalyzed(
                1.2f, 1f, 0f, distanceCurve, new AnimationCurve(), new[] { 0.1f }, new[] { 0.6f });
        }

        private static ConvaiBodyAnimationConfig CreateConfig(
            List<Object> cleanup,
            bool? plantedStopsWhileWalking = null,
            float? plantedStopMinTravel = null)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            cleanup.Add(config);

            // These tests exercise the authored turn and stop machinery, which the shipped config
            // and a hand-made one keep off until the clip content is there. Turn both on here so
            // the state machine under test can reach its turn and planted-stop states.
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_enableTurnInPlace").boolValue = true;
            serialized.FindProperty("_enablePlantedStops").boolValue = true;
            if (plantedStopsWhileWalking.HasValue)
                serialized.FindProperty("_plantedStopsWhileWalking").boolValue = plantedStopsWhileWalking.Value;
            if (plantedStopMinTravel.HasValue)
                serialized.FindProperty("_plantedStopMinTravel").floatValue = plantedStopMinTravel.Value;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return config;
        }

        private static (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform root) CreateRuntime(
            List<Object> cleanup,
            ConvaiBodyAnimationSet set,
            ConvaiBodyAnimationConfig config,
            int randomSeed = 1)
        {
            PlayableGraph graph = PlayableGraph.Create("LocomotionLayerStateMachineTests");
            var rootGo = new GameObject("root");
            cleanup.Add(rootGo);
            var stub = new StubLocomotionDrive();

            var runtime = new LayerRuntime
            {
                Graph = graph,
                Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                Set = set,
                Config = config,
                Trace = new AnimTrace("LocomotionLayerStateMachineTests"),
                RandomSeed = randomSeed,
                CharacterRoot = rootGo.transform,
                Locomotion = stub
            };

            var layer = new LocomotionLayer();
            layer.Initialize(runtime, LayerPorts.Locomotion);
            return (graph, layer, stub, rootGo.transform);
        }

        private static void Tick(LocomotionLayer layer, float dt, EmotionReading? emotion = null)
        {
            EmotionReading e = emotion ?? EmotionReading.Neutral;
            var context = new LayerTickContext(dt, DialogueState.Idle, in e, 0f, false, false);
            layer.Tick(in context);
        }

        private static void TeardownAll(LocomotionLayer layer, PlayableGraph graph, List<Object> cleanup)
        {
            layer?.Teardown();
            if (graph.IsValid()) graph.Destroy();
            foreach (Object obj in cleanup)
                Object.DestroyImmediate(obj);
            cleanup.Clear();
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void LeaveIdle_NoStartsNoTurns_EntersPlainMove()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = 0f;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;

                Tick(layer, 0.05f);
                Tick(layer, 0.05f);

                Assert.AreEqual("Move", layer.StateLabel);
                Assert.GreaterOrEqual(stub.ReleaseGateCount, 1);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void LeaveIdle_LargeAngle_TurnsInPlace_NominalDrive()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddTurns(cleanup, set);
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = -120f;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;

                Tick(layer, 0.05f);

                Assert.AreEqual("Turn:90L", layer.StateLabel);
                Assert.Contains(true, stub.FreezeAgentCalls);
                Assert.IsTrue(stub.RotationDrivenExternally);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void Turn_Completes_RotatesRoot_AndHandsOff()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddTurns(cleanup, set);
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform root) =
                CreateRuntime(cleanup, set, config);

            try
            {
                const float targetYaw = -120f;

                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = targetYaw;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;

                Tick(layer, 0.05f);
                Assert.AreEqual("Turn:90L", layer.StateLabel);

                int guard = 0;
                while (layer.StateLabel == "Turn:90L" && guard < 30)
                {
                    // Re-derive the remaining steering error from the root's ACTUAL rotation so
                    // far — a stale fixed angle would make the mid-turn re-aim guard think the
                    // turn is off course and re-select, which never completes.
                    stub.SignedAngleToSteering = Mathf.DeltaAngle(root.eulerAngles.y, targetYaw);
                    Tick(layer, 0.05f);
                    guard++;
                }

                Assert.Less(guard, 30, "turn never handed off within the tick budget");

                float residual = Mathf.Abs(Mathf.DeltaAngle(root.eulerAngles.y, targetYaw));
                // yawScale = |-120/-90| = 1.33, unclamped (within [0.6,1.4]) — the nominal drive
                // reaches full authored*scale rotation by the handoff normalized time, so the
                // residual should be near zero; 25° leaves generous room for step discretization.
                Assert.Less(residual, 25f, "root under-rotated the requested turn");

                Assert.IsFalse(stub.FreezeAgentCalls[^1]);
                Assert.IsFalse(stub.RotationDrivenExternally);
                Assert.AreEqual("Move", layer.StateLabel);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void Turn_SameClipReselection_DoesNotRestartForever()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddTurns(cleanup, set);
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = -110f;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;

                Tick(layer, 0.05f);
                Assert.AreEqual("Turn:90L", layer.StateLabel);

                // Reproduce a steering source that settles on a still-large residual while
                // the clip is active. Before the same-clip guard, TryEnterTurn restarted
                // Turn:90L every tick and kept the NavMeshAgent frozen indefinitely.
                stub.SignedAngleToSteering = -67f;
                int guard = 0;
                while (layer.StateLabel == "Turn:90L" && guard < 30)
                {
                    Tick(layer, 0.05f);
                    guard++;
                }

                Assert.Less(guard, 30, "same turn clip was restarted indefinitely");
                Assert.IsFalse(stub.FreezeAgentCalls[^1]);
                Assert.IsFalse(stub.RotationDrivenExternally);
                Assert.AreEqual("Move", layer.StateLabel);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void Turn_180Commit_DoesNotDowngradeTo90_WhenSteeringShrinks()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddTurns(cleanup, set);
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform root) =
                CreateRuntime(cleanup, set, config);

            try
            {
                const float targetYaw = -111f;

                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = -150f; // above Turn180MinAngle (135°) → Turn:180L
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;

                Tick(layer, 0.05f);
                Assert.AreEqual("Turn:180L", layer.StateLabel);

                // Steering settles to a still-large residual that would bucket to Turn:90L.
                // Before the downgrade guard, TickTurn crossfaded into a second turn clip.
                stub.SignedAngleToSteering = targetYaw;

                int guard = 0;
                while (layer.StateLabel.StartsWith("Turn:", StringComparison.Ordinal) && guard < 40)
                {
                    stub.SignedAngleToSteering = Mathf.DeltaAngle(root.eulerAngles.y, targetYaw);
                    Tick(layer, 0.05f);

                    // A clean handoff straight to Move (the normal end-of-turn handoff) is
                    // fine — what must never happen is a crossfade down to the 90° clip.
                    Assert.That(layer.StateLabel, Is.Not.EqualTo("Turn:90L").And.Not.EqualTo("Turn:90R"),
                        "committed 180° turn must not downgrade to 90° mid-flight");
                    guard++;
                }

                Assert.Less(guard, 40, "turn never handed off within the tick budget");

                float residual = Mathf.Abs(Mathf.DeltaAngle(root.eulerAngles.y, targetYaw));
                Assert.Less(residual, 25f, "root under-rotated the requested turn");
                Assert.AreEqual("Move", layer.StateLabel);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void Stop_DistanceMatched_CompletesMove()
        {
            var cleanup = new List<Object>();
            AnimationCurve stopCurve = new(
                new Keyframe(0f, 0f), new Keyframe(0.6f, 1f), new Keyframe(1f, 1f));

            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddWalkStops(cleanup, set, stopCurve);
            ConvaiBodyAnimationConfig config = CreateConfig(
                cleanup, plantedStopsWhileWalking: true, plantedStopMinTravel: 0.3f);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = 0f;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;
                stub.RemainingDistance = 5f;

                Tick(layer, 0.05f);
                Assert.AreEqual("Move", layer.StateLabel);

                // Cruise ~0.5s (> plantedStopMinTravel) with a long leg so no stop fires yet.
                for (int i = 0; i < 9; i++)
                    Tick(layer, 0.05f);
                Assert.AreEqual("Move", layer.StateLabel, "long leg — no stop should fire yet");

                stub.RemainingDistance = 0.95f;
                Tick(layer, 0.05f);
                StringAssert.StartsWith("WalkStop", layer.StateLabel);
                Assert.AreEqual(1, stub.BeginManagedMotionCount);

                Tick(layer, 0.05f);
                Assert.Greater(stub.LastManagedSpeed, 0f);

                ClipMotionMetadata stopMeta = set.Locomotion.WalkStopLeftPlant.Metadata;
                int guard = 0;
                while (layer.ActiveNormalizedTime < 0.6f && guard < 60)
                {
                    Tick(layer, 0.05f);
                    stub.RemainingDistance = Mathf.Max(0f, 1f - stopMeta.EvaluateDistance(layer.ActiveNormalizedTime));
                    guard++;
                }
                Assert.Less(guard, 60, "stop clip never reached the distance-covered norm");

                // The capsule parks: agent speed and remaining distance both hit zero.
                stub.Speed = 0f;
                stub.RemainingDistance = 0f;

                guard = 0;
                while (stub.CompleteMoveFromAnimationCount == 0 && guard < 20)
                {
                    Tick(layer, 0.05f);
                    guard++;
                }
                Assert.AreEqual(1, stub.CompleteMoveFromAnimationCount);

                stub.IsMoving = false;
                Tick(layer, 0.05f);
                Assert.AreEqual("Idle", layer.StateLabel);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void Stop_ArrivedEarly_HandsOffWithoutMarching()
        {
            var cleanup = new List<Object>();
            AnimationCurve stopCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddWalkStops(cleanup, set, stopCurve);
            ConvaiBodyAnimationConfig config = CreateConfig(
                cleanup, plantedStopsWhileWalking: true, plantedStopMinTravel: 0.3f);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = 0f;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;
                stub.RemainingDistance = 5f;

                Tick(layer, 0.05f);
                for (int i = 0; i < 9; i++)
                    Tick(layer, 0.05f);

                stub.RemainingDistance = 0.95f;
                Tick(layer, 0.05f);
                StringAssert.StartsWith("WalkStop", layer.StateLabel);

                for (int i = 0; i < 8; i++)
                    Tick(layer, 0.05f);

                Assert.Less(layer.ActiveNormalizedTime, 0.55f,
                    "test exercises the early-arrival path, not travel-done");

                // The capsule parks well before the clip's authored distance is covered.
                stub.RemainingDistance = 0f;
                stub.Speed = 0f;

                int guard = 0;
                while (stub.CompleteMoveFromAnimationCount == 0 && guard < 2)
                {
                    Tick(layer, 0.05f);
                    guard++;
                }

                Assert.AreEqual(1, stub.CompleteMoveFromAnimationCount,
                    "early arrival must hand off instead of marching the clip to its authored distance");

                stub.IsMoving = false;
                Tick(layer, 0.05f);
                Assert.AreEqual("Idle", layer.StateLabel);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void Arrival_Idle_RespectsEmotion()
        {
            foreach (int seed in new[] { 3, 17, 101 })
            {
                var cleanup = new List<Object>();
                IdleEntry idleA = MakeIdle(cleanup, "idleA_joy_excluded", ("joy", 0f));
                IdleEntry idleB = MakeIdle(cleanup, "idleB_plain");
                ConvaiBodyAnimationSet set = MakeSet(cleanup, new List<IdleEntry> { idleA, idleB });
                AddWalk(cleanup, set);
                ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
                (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                    CreateRuntime(cleanup, set, config, seed);

                try
                {
                    EmotionReading joy = new("joy", 1f, EmotionReading.EmptyScores, 0f, 0f);

                    stub.IsMoving = true;
                    stub.PathPending = false;
                    stub.SignedAngleToSteering = 0f;
                    stub.Speed = 1.2f;
                    stub.DesiredSpeed = 1.2f;

                    Tick(layer, 0.05f, joy);
                    Assert.AreEqual("Move", layer.StateLabel);

                    stub.IsMoving = false;
                    stub.Speed = 0f;
                    Tick(layer, 0.05f, joy);

                    Assert.AreEqual("Idle", layer.StateLabel);
                    Assert.AreEqual("idleB_plain", layer.ActiveClipName,
                        $"seed={seed}: joy affinity 0 must exclude idleA on arrival");
                }
                finally
                {
                    TeardownAll(layer, graph, cleanup);
                }
            }
        }

        [Test]
        public void RequestFacingTurn_FromIdle_TurnsAndCancels()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set);
            AddTurns(cleanup, set);
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = false;
                Tick(layer, 0.05f);
                Assert.AreEqual("Idle", layer.StateLabel);

                bool started = layer.RequestFacingTurn(90f, "test");

                Assert.IsTrue(started);
                Assert.AreEqual("Turn:90R", layer.StateLabel);
                Assert.Contains(true, stub.FreezeAgentCalls);

                layer.CancelFacingTurn("test");

                Assert.AreEqual("Idle", layer.StateLabel);
                Assert.IsFalse(stub.FreezeAgentCalls[^1]);
                Assert.IsFalse(stub.RotationDrivenExternally);
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }

        [Test]
        public void MoveCanceled_WalkMomentum_SettlesToIdle()
        {
            var cleanup = new List<Object>();
            ConvaiBodyAnimationSet set = MakeSet(cleanup);
            AddWalk(cleanup, set); // walk-only: jogThreshold == walkThreshold, no abrupt-stop momentum
            ConvaiBodyAnimationConfig config = CreateConfig(cleanup);
            (PlayableGraph graph, LocomotionLayer layer, StubLocomotionDrive stub, Transform _) =
                CreateRuntime(cleanup, set, config);

            try
            {
                stub.IsMoving = true;
                stub.PathPending = false;
                stub.SignedAngleToSteering = 0f;
                stub.Speed = 1.2f;
                stub.DesiredSpeed = 1.2f;

                Tick(layer, 0.05f);
                Assert.AreEqual("Move", layer.StateLabel);

                stub.IsMoving = false;
                stub.RaiseMoveEnded(false); // forced cancel, not a natural arrival

                Assert.AreEqual("Idle", layer.StateLabel, "no jog momentum — must settle plainly, no abrupt stop clip");
            }
            finally
            {
                TeardownAll(layer, graph, cleanup);
            }
        }
    }
}
