using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Executors;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Actions;
using Convai.Runtime.Embodiment;
using Convai.Runtime.SceneMetadata;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.SceneTagging
{
    public sealed class SceneTaggerMovementTests
    {
        private Scene _scene;
        private readonly List<Object> _assets = new();
        [UnitySetUp] public IEnumerator Setup() { _scene = SceneManager.CreateScene("Standing position fixture"); yield return null; }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_scene.IsValid()) yield return SceneManager.UnloadSceneAsync(_scene);
            foreach (Object asset in _assets) if (asset) Object.DestroyImmediate(asset);
            _assets.Clear();
        }
        private GameObject Cube(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            SceneManager.MoveGameObjectToScene(go, _scene); return go;
        }

        [UnityTest] public IEnumerator GroupGazeUsesPhysicalGeometryAndHonorsSharedGazeSettings()
        {
            GameObject physical = Cube("Physical chair"); physical.transform.position = new Vector3(3, 1, 2);
            var metadata = physical.AddComponent<ConvaiObjectMetadata>();
            metadata.ObjectName = "Chair"; metadata.ObjectDescription = "A physical chair."; metadata.GazeEnabled = true;
            var provider = physical.AddComponent<WorldObjectGazeTargetProvider>();
            GameObject logical = new GameObject("Logical chairs"); SceneManager.MoveGameObjectToScene(logical, _scene);
            var group = logical.AddComponent<ConvaiLogicalObjectGroup>();
            group.Metadata.ObjectName = "Chairs"; group.Metadata.ObjectDescription = "A group of chairs.";
            group.Metadata.GazeEnabled = true; group.Members.Add(metadata); group.NotifyMembershipChanged();
            yield return null;
            Assert.That(provider.TryGetCandidate(null, out var candidate), Is.True);
            Assert.That(candidate.Target, Is.SameAs(physical.transform));
            Assert.That(candidate.WorldPoint, Is.EqualTo(physical.GetComponent<Renderer>().bounds.center));
            Assert.That(candidate.DebugName, Is.EqualTo("Chairs"));
            group.Metadata.GazeEnabled = false;
            Assert.That(provider.TryGetCandidate(null, out _), Is.False);
            group.Metadata.GazeEnabled = true; group.Members.Clear(); group.NotifyMembershipChanged();
            Assert.That(provider.TryGetCandidate(null, out candidate), Is.True);
            Assert.That(candidate.DebugName, Is.EqualTo("Chair"));
        }

        [UnityTest] public IEnumerator MovementChoosesReachablePhysicalMemberAndExactStandingPoint()
        {
            var nav = UnityEngine.AI.NavMesh.GetSettingsByIndex(0);
            var sources = new List<UnityEngine.AI.NavMeshBuildSource> {
                new() { shape = UnityEngine.AI.NavMeshBuildSourceShape.Box, size = new Vector3(30, .2f, 30),
                    transform = Matrix4x4.TRS(new Vector3(0, -.1f, 0), Quaternion.identity, Vector3.one), area = 0 }
            };
            var data = UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(nav, sources, new Bounds(Vector3.zero, new Vector3(35, 5, 35)), Vector3.zero, Quaternion.identity);
            Assert.That(data, Is.Not.Null); _assets.Add(data);
            var instance = UnityEngine.AI.NavMesh.AddNavMeshData(data);
            try
            {
                GameObject actor = new GameObject("Fixture walker"); SceneManager.MoveGameObjectToScene(actor, _scene);
                var agent = actor.AddComponent<UnityEngine.AI.NavMeshAgent>(); agent.agentTypeID = nav.agentTypeID;
                yield return null;
                Assert.That(agent.Warp(new Vector3(0, 0, -5)), Is.True);
                var near = Cube("Near chair").AddComponent<ConvaiObjectMetadata>();
                var far = Cube("Far chair").AddComponent<ConvaiObjectMetadata>(); far.transform.position = new Vector3(8, 0, 0);
                near.gameObject.AddComponent<ConvaiObjectMovementPoints>().Points.Add(new ConvaiMovementPoint { Position = new Vector3(0, 0, -1) });
                far.gameObject.AddComponent<ConvaiObjectMovementPoints>().Points.Add(new ConvaiMovementPoint { Position = new Vector3(0, 0, -1) });
                GameObject logical = new GameObject("Chairs"); SceneManager.MoveGameObjectToScene(logical, _scene);
                var group = logical.AddComponent<ConvaiLogicalObjectGroup>(); group.Members.Add(far); group.Members.Add(near);
                Assert.That(ConvaiObjectMovementResolver.TryResolve(logical, agent, null, out var destination), Is.True);
                Assert.That(destination.PhysicalObject, Is.SameAs(near.gameObject));
                Assert.That(destination.IsExactStandingPoint, Is.True);
                Assert.That(Vector3.Distance(destination.Position, new Vector3(0, 0, -1)), Is.LessThan(.15f));
                actor.AddComponent<EmbodimentContext>(); // Offline composition for travel attention, without a room connection.
                var locomotion = actor.AddComponent<ConvaiNavMeshLocomotion>(); locomotion.ConfigureSpeeds(4, 6);
                var executor = actor.AddComponent<ConvaiWalkToActionExecutor>();
                var target = ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition { Name = "Chairs", GameObjectReference = logical });
                var invocation = new ConvaiActionInvocation(new ConvaiActionCommand("Walk To"), null, target, null, 0, 0);
                using var cancellation = new CancellationTokenSource();
                var walking = executor.ExecuteAsync(invocation, cancellation.Token);
                float deadline = Time.realtimeSinceStartup + 8;
                while (!walking.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (!walking.IsCompleted) { cancellation.Cancel(); Assert.Fail("Walk To did not reach the standing position."); }
                if (walking.IsFaulted) throw walking.Exception.GetBaseException();
                Assert.That(walking.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
                Assert.That(Vector3.Distance(actor.transform.position, destination.Position), Is.LessThan(.2f),
                    "An exact standing point must not receive the ordinary one-metre arrival offset.");
                instance.Remove();
                Assert.That(ConvaiObjectMovementResolver.TryResolve(logical, agent, null, out _), Is.False);
            }
            finally { if (instance.valid) instance.Remove(); }
        }

    }
}
