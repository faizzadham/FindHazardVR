using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Models;
using Convai.Runtime.Presentation.Views.Transcript;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Presentation
{
    public class ChatTranscriptUITests
    {
        private readonly List<GameObject> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
        }

        [Test]
        public void TurnCorrectionReusesExistingBubble()
        {
            ChatTranscriptUI ui = Track(new GameObject("ChatTranscriptUI")).AddComponent<ChatTranscriptUI>();
            RectTransform container = Track(new GameObject("ChatContainer", typeof(RectTransform)))
                .GetComponent<RectTransform>();
            GameObject prefab = Track(new GameObject("PlayerMessagePrefab"));
            prefab.SetActive(false);
            SetField(ui, "chatContainer", container);
            SetField(ui, "playerMessagePrefab", prefab);
            SetField(ui, "characterMessagePrefab", prefab);

            InvokeDisplayTurn(ui, CreateTurn("turn-1", "helo", 1));
            InvokeDisplayTurn(ui, CreateTurn("turn-1", "hello", 2));

            Assert.AreEqual(1, container.childCount);
        }

        [Test]
        public void StartLifecycleRequestsFadeIn()
        {
            MethodInfo start = typeof(ChatTranscriptUI).GetMethod(
                "Start",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo startFadeIn = typeof(ChatTranscriptUI).GetMethod(
                "StartFadeIn",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(start);
            Assert.IsNotNull(startFadeIn, "Chat transcript startup must keep an explicit fade-in seam.");
            Assert.That(
                CallsMethod(start, startFadeIn),
                Is.True,
                "ChatTranscriptUI.Start must request its fade-in animation.");
        }

        [Test]
        public void MissingPlayerInputKeepsDependencyResolutionRetryable()
        {
            ChatTranscriptUI ui = Track(new GameObject("ChatTranscriptUI")).AddComponent<ChatTranscriptUI>();

            ui.Inject(null, null);

            Assert.That(
                GetField<bool>(ui, "_isInjected"),
                Is.False,
                "An early UI enable must not permanently disable text submission before player input is ready.");
        }

        [Test]
        public void UpdateRetriesDependencyResolution()
        {
            MethodInfo update = typeof(ChatTranscriptUI).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo tryResolveDependencies = typeof(ChatTranscriptUI).GetMethod(
                "TryResolveDependencies",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(update);
            Assert.IsNotNull(tryResolveDependencies);
            Assert.That(
                CallsMethod(update, tryResolveDependencies),
                Is.True,
                "ChatTranscriptUI must retry dependency resolution after an early enable races manager initialization.");
        }

        // The clear path lives in Convai.Tests.PlayMode.Presentation.ChatTranscriptUIPlayModeTests:
        // it removes rows with Object.Destroy, which Unity refuses outside Play mode, so an EditMode
        // test could assert the bookkeeping but never the removal it is named after.

        private static TranscriptTurn CreateTurn(string id, string text, int revision)
        {
            DateTime now = DateTime.UtcNow;
            return new TranscriptTurn(
                id,
                id,
                string.Empty,
                1,
                revision,
                new TranscriptSpeaker(TranscriptSpeakerType.Player, "player-1", "You"),
                TranscriptTurnState.Committed,
                TranscriptTextSource.ProcessedFinal,
                text,
                string.Empty,
                text,
                now,
                now,
                now,
                false,
                Array.Empty<TranscriptSegment>());
        }

        private GameObject Track(GameObject gameObject)
        {
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void InvokeDisplayTurn(ChatTranscriptUI ui, TranscriptTurn turn)
        {
            MethodInfo method = typeof(ChatTranscriptUI).GetMethod(
                "DisplayTurn",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(ui, new object[] { turn });
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (T)field.GetValue(target);
        }

        private static bool CallsMethod(MethodInfo caller, MethodInfo expectedCallee)
        {
            byte[] il = caller.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;

            byte[] token = BitConverter.GetBytes(expectedCallee.MetadataToken);
            for (int i = 0; i <= il.Length - token.Length - 1; i++)
            {
                bool isCall = il[i] == 0x28 || il[i] == 0x6F;
                if (!isCall) continue;

                bool tokenMatches = true;
                for (int tokenIndex = 0; tokenIndex < token.Length; tokenIndex++)
                {
                    if (il[i + 1 + tokenIndex] == token[tokenIndex]) continue;
                    tokenMatches = false;
                    break;
                }

                if (tokenMatches) return true;
            }

            return false;
        }
    }
}
