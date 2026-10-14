using System.Reflection;
using Convai.Editor.AI;
using Convai.Modules.Narrative.Editor.AI;
using Convai.Runtime;
using Convai.Runtime.Components;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiMcpIdRoundTripTests
    {
        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
        }

        [TearDown]
        public void TearDown()
        {
            _sceneFixture.End();
        }

        [Test]
        public void EntityIdRoundTrip_ResolvesSameCharacter()
        {
            ConvaiCharacter character = CreateCharacter();

            long id = ConvaiMcpEntityRef.ToToolId(character);
            bool resolved = ConvaiMcpEntityRef.TryResolve(id, out ConvaiCharacter value);

            Assert.That(resolved, Is.True);
            Assert.That(value, Is.SameAs(character));
        }

#if !UNITY_6000_6_OR_NEWER
        [Test]
        public void LegacyInstanceId_StillResolvesSameCharacter()
        {
            ConvaiCharacter character = CreateCharacter();
            long id = LegacyInstanceId(character);

            bool resolved = ConvaiMcpEntityRef.TryResolve(id, out ConvaiCharacter value);

            Assert.That(resolved, Is.True);
            Assert.That(value, Is.SameAs(character));
        }
#else
        // From 6000.6 Unity cannot turn a 32-bit instance ID back into an object, so a legacy id
        // must resolve to nothing rather than to whatever object shares its low bits.
        [Test]
        public void LegacyInstanceId_ResolvesToNothingFrom6000_6()
        {
            ConvaiCharacter character = CreateCharacter();
            long id = LegacyInstanceId(character);

            bool resolved = ConvaiMcpEntityRef.TryResolve(id, out ConvaiCharacter value);

            Assert.That(resolved, Is.False);
            Assert.That(value, Is.Null);
        }
#endif

        [Test]
        public void GameObjectEntityId_ResolvesCarriedCharacter()
        {
            ConvaiCharacter character = CreateCharacter();

            long id = ConvaiMcpEntityRef.ToToolId(character.gameObject);
            bool resolved = ConvaiMcpEntityRef.TryResolve(id, out ConvaiCharacter value);

            Assert.That(resolved, Is.True);
            Assert.That(value, Is.SameAs(character));
        }

        [Test]
        public void NullAndZero_ReturnNoObjectIdOrReference()
        {
            Assert.That(ConvaiMcpEntityRef.ToToolId(null), Is.Zero);
            Assert.That(ConvaiMcpEntityRef.Resolve(0), Is.Null);
        }

        [Test]
        public void SemanticResolvers_UseCanonicalCharacterErrors()
        {
            Assert.That(ConvaiMcpResolvers.TryCharacter(0, true, out _, out string missing), Is.False);
            Assert.That(missing, Is.EqualTo("No ConvaiCharacter exists in the active scene."));

            ConvaiCharacter first = CreateCharacter();
            Assert.That(ConvaiMcpResolvers.TryCharacter(0, true, out ConvaiCharacter resolved, out string success), Is.True);
            Assert.That(resolved, Is.SameAs(first));
            Assert.That(success, Is.Empty);

            CreateCharacter();
            Assert.That(ConvaiMcpResolvers.TryCharacter(0, true, out _, out string ambiguous), Is.False);
            Assert.That(ambiguous, Is.EqualTo("Multiple ConvaiCharacter components exist; provide characterInstanceId."));
            Assert.That(ConvaiMcpResolvers.TryCharacter(long.MaxValue, true, out _, out string invalid), Is.False);
            Assert.That(invalid, Is.EqualTo("characterInstanceId must identify ConvaiCharacter in the active scene."));
        }

        [Test]
        public void SemanticResolvers_PreserveManagerAndHostResolutionRules()
        {
            Assert.That(ConvaiMcpResolvers.TryManager(0, true, out _, out string missing), Is.False);
            Assert.That(missing, Is.EqualTo("No ConvaiManager exists in the active scene."));

            ConvaiManager manager = new GameObject("Convai Manager").AddComponent<ConvaiManager>();
            Assert.That(ConvaiMcpResolvers.TryManager(0, true, out ConvaiManager resolved, out string success), Is.True);
            Assert.That(resolved, Is.SameAs(manager));
            Assert.That(success, Is.Empty);

            ConvaiCharacter character = CreateCharacter();
            long componentId = ConvaiMcpEntityRef.ToToolId(character);
            Assert.That(ConvaiMcpResolvers.TryHost(componentId, null, false, out _, out _), Is.False);
            Assert.That(ConvaiMcpResolvers.TryHost(componentId, null, true, out GameObject host, out _), Is.True);
            Assert.That(host, Is.SameAs(character.gameObject));
        }

        [Test]
        public void EntityId_IsAcceptedByNarrativeAndFeatureResolvers()
        {
            ConvaiCharacter character = CreateCharacter();
            long id = ConvaiMcpEntityRef.ToToolId(character);

            JObject narrative = JObject.FromObject(ConvaiNarrativeMcpTools.Diagnose(
                new DiagnoseNarrativeRequest { CharacterInstanceId = id }));
            JObject features = JObject.FromObject(ConvaiMcpTools.DiagnoseActions(
                new ConvaiDiagnoseActionsRequest { CharacterInstanceId = id }));

            Assert.That(narrative.Value<bool>("success"), Is.True);
            Assert.That(features.Value<bool>("success"), Is.True);
        }

        [Test]
        public void ResolutionFailures_UseCanonicalCharacterCode()
        {
            const long invalidId = long.MaxValue;
            JObject actions = JObject.FromObject(ConvaiMcpTools.DiagnoseActions(
                new ConvaiDiagnoseActionsRequest { CharacterInstanceId = invalidId }));
            JObject narrative = JObject.FromObject(ConvaiNarrativeMcpTools.Diagnose(
                new DiagnoseNarrativeRequest { CharacterInstanceId = invalidId }));
            JObject conversation = JObject.FromObject(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest { CharacterInstanceId = invalidId }));

            Assert.That(actions["data"]?.Value<string>("code"), Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
            Assert.That(narrative["data"]?.Value<string>("code"), Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
            Assert.That(conversation["data"]?.Value<string>("code"), Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
        }

        // Object.GetInstanceID() is [Obsolete(error: true)] on Unity 6000.5, so the genuine legacy
        // id this test exercises is fetched reflectively — the point is to feed the fallback a real
        // historical instance ID, which no supported call syntax can still produce directly.
        private static long LegacyInstanceId(UnityEngine.Object value) =>
            (int)typeof(UnityEngine.Object)
                .GetMethod("GetInstanceID", BindingFlags.Public | BindingFlags.Instance)
                .Invoke(value, null);

        private static ConvaiCharacter CreateCharacter() =>
            new GameObject("Convai MCP ID Character").AddComponent<ConvaiCharacter>();

    }
}
