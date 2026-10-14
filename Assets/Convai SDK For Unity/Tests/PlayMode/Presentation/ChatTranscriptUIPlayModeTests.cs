using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Models;
using Convai.Runtime.Presentation.Views.Transcript;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.Presentation
{
    /// <summary>
    ///     Clearing the chat transcript, proven where the view actually lives.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ChatTranscriptUI" /> removes its rows with <see cref="Object.Destroy" />, the
    ///         correct call for a runtime view: it defers destruction to the end of the frame instead of
    ///         tearing objects out from under a layout pass that is still running. Unity refuses that call
    ///         outside Play mode, so an EditMode test of the clear path could only ever assert the
    ///         bookkeeping and had to swallow an error to do it — the rows it claimed were cleared were
    ///         still hanging in the container. Play mode is where the claim can be made in full.
    ///     </para>
    ///     <para>
    ///         The view is driven through its private display seam rather than through a live
    ///         <c>ConvaiManager</c> subscription: this suite is about what clearing does to the rendered
    ///         rows, and a real transport would add a second failure source to every assertion here.
    ///         The host stays inactive for the same reason — its <c>Update</c> loop and manager lookup
    ///         have nothing to do with the claim under test.
    ///     </para>
    /// </remarks>
    public sealed class ChatTranscriptUIPlayModeTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private RectTransform _container;
        private ChatTranscriptUI _ui;

        [SetUp]
        public void SetUp()
        {
            _container = Track(new GameObject("ChatContainer", typeof(RectTransform)))
                .GetComponent<RectTransform>();

            GameObject prefab = Track(new GameObject("PlayerMessagePrefab"));
            prefab.SetActive(false);

            GameObject host = Track(new GameObject(nameof(ChatTranscriptUIPlayModeTests)));
            host.SetActive(false);
            _ui = host.AddComponent<ChatTranscriptUI>();

            SetField(_ui, "chatContainer", _container);
            SetField(_ui, "playerMessagePrefab", prefab);
            SetField(_ui, "characterMessagePrefab", prefab);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _createdObjects.Clear();
        }

        [UnityTest]
        public IEnumerator ClearAll_RemovesTheRenderedRowsFromTheContainer()
        {
            DisplayTurn(CreateTurn("turn-1", "hello", 1));
            Assert.That(RenderedRowCount(), Is.EqualTo(1), "A committed turn must render a row to begin with.");

            _ui.ClearAll();
            yield return null;

            Assert.That(
                RenderedRowCount(),
                Is.Zero,
                "Clearing the transcript must destroy the rows it rendered, not just forget them.");
        }

        [UnityTest]
        public IEnumerator ClearAll_KeepsTheClearedTurnFromReappearingWhenItIsCorrected()
        {
            DisplayTurn(CreateTurn("turn-1", "helo", 1));
            _ui.ClearAll();
            yield return null;

            DisplayTurn(CreateTurn("turn-1", "hello", 2));

            Assert.That(
                RenderedRowCount(),
                Is.Zero,
                "A revision of a locally cleared turn must not bring that turn back on screen.");
            Assert.That(
                (Dictionary<string, GameObject>)GetField(_ui, "_messageRowsByTurnId"),
                Is.Empty,
                "A cleared turn must not be tracked as rendered.");
            CollectionAssert.Contains(
                (HashSet<string>)GetField(_ui, "_locallyHiddenTurnIds"),
                "turn-1",
                "Clearing is a local hide, so the cleared turn must stay on the hidden list.");
        }

        /// <summary>Rows currently parented to the container, excluding any pending destruction.</summary>
        private int RenderedRowCount()
        {
            int count = 0;
            foreach (Transform child in _container)
                if (child != null)
                    count++;
            return count;
        }

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

        private void DisplayTurn(TranscriptTurn turn)
        {
            MethodInfo method = typeof(ChatTranscriptUI).GetMethod(
                "DisplayTurn",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "ChatTranscriptUI must keep a single seam that renders one turn.");
            method.Invoke(_ui, new object[] { turn });
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Expected a serialized field named '{name}'.");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Expected a field named '{name}'.");
            return field.GetValue(target);
        }
    }
}
