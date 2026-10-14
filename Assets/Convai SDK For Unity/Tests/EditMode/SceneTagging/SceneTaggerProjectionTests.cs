using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Executors;
using Convai.Runtime.Actions;
using Convai.Runtime.Embodiment;
using Convai.Runtime.SceneMetadata;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerProjectionTests
    {
        private readonly List<GameObject> _objects = new();
        private GameObject Create(string name, Vector3 position = default)
        {
            var go = new GameObject(name); go.transform.position = position; _objects.Add(go); return go;
        }
        [TearDown] public void TearDown()
        {
            foreach (GameObject go in _objects) if (go) Object.DestroyImmediate(go);
            _objects.Clear();
        }
        [Test] public void DestroyedInteractionPointFallsBackToPhysicalObject()
        {
            GameObject target = Create("Target"), point = Create("Point");
            target.AddComponent<ConvaiActionTarget>().InteractionPoint = point.transform;
            Object.DestroyImmediate(point);
            Assert.That(ConvaiSceneObjectProjection.InteractionPoint(target), Is.SameAs(target.transform));
        }
        [Test] public void DisabledGroupComponentDissolvesTheProjection()
        {
            GameObject root = Create("Logical group"), member = Create("Exhibit", new Vector3(3, 0, 0));
            var group = root.AddComponent<ConvaiLogicalObjectGroup>();
            ConvaiObjectMetadata metadata = member.AddComponent<ConvaiObjectMetadata>();
            group.Members.Add(metadata);
            Assert.That(ConvaiSceneObjectProjection.Physical(root, Vector3.zero), Is.SameAs(member));

            group.enabled = false;
            Assert.That(ConvaiSceneObjectProjection.Physical(root, Vector3.zero), Is.SameAs(root),
                "A disabled group must stop redirecting the root to its members.");
            Assert.That(ConvaiSceneObjectProjection.Include(metadata), Is.True);
            member.SetActive(false);
            Assert.That(ConvaiSceneObjectProjection.Include(root.GetComponent<ConvaiObjectMetadata>()), Is.True,
                "With the group disabled the root is an ordinary object, not a group without members.");
        }
        [Test] public async Task OffMeshWalkResolvesPhysicalMemberBeforeLegacyLocomotion()
        {
            GameObject root = Create("Convai group: Exhibits"), near = Create("Nearby exhibit", new Vector3(12, 0, 4)),
                far = Create("Distant exhibit", new Vector3(50, 0, 4));
            var group = root.AddComponent<ConvaiLogicalObjectGroup>();
            group.Members.Add(far.AddComponent<ConvaiObjectMetadata>());
            group.Members.Add(near.AddComponent<ConvaiObjectMetadata>());
            GameObject point = Create("Authored point", near.transform.position + Vector3.forward);
            near.AddComponent<ConvaiActionTarget>().InteractionPoint = point.transform;
            GameObject character = Create("Off mesh character", new Vector3(10, 0, 4));
            character.AddComponent<ConvaiNavMeshLocomotion>();
            var executor = character.AddComponent<ConvaiWalkToActionExecutor>();
            Transform destination = ConvaiSceneObjectProjection.ResolveInteractionPoint(root, character.transform.position,
                root.transform, out GameObject physical);
            Assert.That(physical, Is.SameAs(near)); Assert.That(destination, Is.SameAs(point.transform));
            var target = ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition
                { Name = "Exhibits", GameObjectReference = root });
            var invocation = new ConvaiActionInvocation(new ConvaiActionCommand("Walk To"), null, target, null, 0, 0);
            ConvaiActionExecutionResult result = await executor.ExecuteAsync(invocation, CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed),
                "No NavMesh still fails through locomotion, after the group resolved to a physical exhibit.");
            Assert.That(result.Message, Does.Contain("'Exhibits'").And.Not.Contain(root.name),
                "Result text names the group as the character knows it, not its generated root.");
            near.SetActive(false); far.SetActive(false);
            Assert.That(ConvaiSceneObjectProjection.ResolveInteractionPoint(root, Vector3.zero, root.transform, out _), Is.Null);
        }

        private GameObject CreateGroup(params GameObject[] members)
        {
            GameObject root = Create("Logical group");
            var group = root.AddComponent<ConvaiLogicalObjectGroup>();
            foreach (GameObject member in members) group.Members.Add(member.AddComponent<ConvaiObjectMetadata>());
            return root;
        }
        private static ConvaiActionInvocation Invoke(string action, GameObject target) => new(new ConvaiActionCommand(action), null,
            ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition { Name = target.name, GameObjectReference = target }), null, 0, 0);
        [Test] public async Task TurnToFaceGroupFacesNearestPhysicalMemberNotGroupRoot()
        {
            GameObject member = Create("Exhibit", new Vector3(0, 0, 10));
            GameObject root = CreateGroup(member);
            GameObject character = Create("Character", new Vector3(0, 0, 5));
            character.AddComponent<EmbodimentContext>(); character.AddComponent<Animator>();
            character.AddComponent<ConvaiBodyAnimationController>();
            var executor = character.AddComponent<ConvaiTurnToFaceActionExecutor>();
            ConvaiActionExecutionResult result = await executor.ExecuteAsync(Invoke("Turn To Face", root), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(result.Message, Does.Contain("Already facing"),
                "The character already faces the exhibit; the group root at the origin is behind it.");
        }
        [Test] public async Task MeasureDistanceToGroupMeasuresNearestPhysicalMember()
        {
            GameObject near = Create("Near exhibit", new Vector3(3, 0, 4)), far = Create("Far exhibit", new Vector3(30, 0, 40));
            GameObject root = CreateGroup(far, near);
            var executor = Create("Character").AddComponent<ConvaiMeasureDistanceActionExecutor>();
            ConvaiActionExecutionResult result = await executor.ExecuteAsync(Invoke("Measure Distance", root), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(result.Answer, Does.Contain("5.0 metres"));
            near.SetActive(false); far.SetActive(false);
            result = await executor.ExecuteAsync(Invoke("Measure Distance", root), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled),
                "A group without active members must not fall back to measuring the player.");
        }
        [Test] public async Task MeasureDistanceToGroupNamesTheGroupNotItsRoot()
        {
            GameObject member = Create("Exhibit (3)", new Vector3(3, 0, 4));
            GameObject root = CreateGroup(member);
            root.name = "Convai group: Exhibits";
            root.GetComponent<ConvaiObjectMetadata>().ObjectName = "Exhibits";
            var actionTarget = root.AddComponent<ConvaiActionTarget>();
            actionTarget.MetadataSource = root.GetComponent<ConvaiObjectMetadata>();
            var invocation = new ConvaiActionInvocation(new ConvaiActionCommand("Measure Distance"), null,
                ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition
                    { Name = actionTarget.TargetName, GameObjectReference = root }), null, 0, 0);
            var executor = Create("Character").AddComponent<ConvaiMeasureDistanceActionExecutor>();
            ConvaiActionExecutionResult result = await executor.ExecuteAsync(invocation, CancellationToken.None);
            Assert.That(result.Answer, Does.StartWith("Exhibits is").And.Not.Contain("Convai group").And.Not.Contain(member.name));
        }
        [Test] public async Task ShowOrHideGroupSwitchesEveryMemberAndKeepsTheGroup()
        {
            GameObject first = Create("Exhibit", new Vector3(2, 0, 0)), second = Create("Exhibit (1)", new Vector3(4, 0, 0));
            GameObject root = CreateGroup(first, second);
            var group = root.GetComponent<ConvaiLogicalObjectGroup>();
            ConvaiObjectMetadata firstMetadata = first.GetComponent<ConvaiObjectMetadata>();
            var executor = Create("Character").AddComponent<ConvaiSetActiveActionExecutor>();
            ConvaiActionInvocation WithMode(string mode)
            {
                var command = new ConvaiActionCommand("Show Or Hide Object")
                {
                    Parameters = { ["mode"] = new ConvaiActionParameterValue { Type = ConvaiActionParameterType.String, StringValue = mode } }
                };
                return new ConvaiActionInvocation(command, null, Invoke("Show Or Hide Object", root).ResolvedTarget, null, 0, 0);
            }

            ConvaiActionExecutionResult result = await executor.ExecuteAsync(WithMode("hide"), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(first.activeSelf || second.activeSelf, Is.False, "Hiding a group hides every physical member.");
            Assert.That(root.activeSelf && group.isActiveAndEnabled, Is.True, "The group itself must stay intact.");
            Assert.That(group.Members, Is.EquivalentTo(new[] { firstMetadata, second.GetComponent<ConvaiObjectMetadata>() }));
            Assert.That(ConvaiSceneObjectProjection.Physical(root, Vector3.zero), Is.Null,
                "The root still resolves as a group with no active members, not as an ordinary object.");

            second.SetActive(true);
            result = await executor.ExecuteAsync(WithMode("show"), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(first.activeSelf && second.activeSelf, Is.True, "Showing a group shows every member, hidden ones included.");
            Assert.That(ConvaiSceneObjectProjection.Physical(root, Vector3.zero), Is.SameAs(first));
        }
        [Test] public async Task PlaySoundOnGroupFindsAudioSourceOnPhysicalMember()
        {
            GameObject member = Create("Speaker", new Vector3(2, 0, 0));
            member.AddComponent<AudioSource>();
            GameObject root = CreateGroup(member);
            var executor = Create("Character").AddComponent<ConvaiPlaySoundActionExecutor>();
            ConvaiActionExecutionResult result = await executor.ExecuteAsync(Invoke("Play Sound", root), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed),
                "The member's Audio Source is found, so the action reaches the missing-clip check.");
            Assert.That(result.FailureReason, Is.EqualTo(ConvaiActionFailureReason.InvalidState));
        }
    }
}
