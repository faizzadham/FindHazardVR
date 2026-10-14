using System.Collections.Generic;
using System.Reflection;
using Convai.Editor.Inspectors;
using Convai.Modules.Narrative;
using Convai.RestAPI.Internal.Models;
using Convai.Runtime;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Editor
{
    public sealed class ConvaiNarrativeDesignTriggerEditorTests
    {
        private const string CharacterId = "00000000-0000-4000-8000-000000000001";
        private const string TriggerId = "saved-trigger";
        private const string TriggerName = "Saved Trigger";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private GameObject _gameObject;
        private ConvaiCharacter _character;
        private ConvaiNarrativeDesignTrigger _trigger;
        private UnityEditor.Editor _editor;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("Narrative Trigger Inspector Migration Test");
            _gameObject.SetActive(false);
            _character = _gameObject.AddComponent<ConvaiCharacter>();
            _trigger = _gameObject.AddComponent<ConvaiNarrativeDesignTrigger>();

            using (var serialized = new SerializedObject(_character))
            {
                serialized.FindProperty("_characterId").stringValue = CharacterId;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            using (var serialized = new SerializedObject(_trigger))
            {
                serialized.FindProperty("_characterComponent").objectReferenceValue = _character;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (_editor != null) Object.DestroyImmediate(_editor);
            if (_gameObject != null) Object.DestroyImmediate(_gameObject);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(CharacterId)]
        public void OpenInspector_UnknownOrMatchingCacheReference_PreservesSavedTrigger(string cachedReference)
        {
            CacheSelectedTrigger(cachedReference);
            List<TriggerData> cachedTriggers = _trigger.AvailableTriggers;
            string serializedBefore = EditorJsonUtility.ToJson(_trigger);
            int dirtyCountBefore = EditorUtility.GetDirtyCount(_trigger);

            OpenInspector();
            CheckCharacterChange();

            Assert.That(_trigger.AvailableTriggers, Is.SameAs(cachedTriggers));
            Assert.That(_trigger.AvailableTriggers.Count, Is.EqualTo(1));
            Assert.That(_trigger.SelectedTriggerIndex, Is.Zero);
            Assert.That(_trigger.TriggerId, Is.EqualTo(TriggerId));
            Assert.That(_trigger.TriggerName, Is.EqualTo(TriggerName));
            Assert.That(_trigger.LastFetchedCharacterReference, Is.EqualTo(cachedReference));
            Assert.That(EditorJsonUtility.ToJson(_trigger), Is.EqualTo(serializedBefore));
            Assert.That(EditorUtility.GetDirtyCount(_trigger), Is.EqualTo(dirtyCountBefore),
                "Opening an unchanged legacy trigger must not dirty it or discard its selection.");
        }

        [Test]
        public void OpenInspector_KnownDifferentCacheReference_ClearsStaleSelection()
        {
            CacheSelectedTrigger(CharacterId + "-1.0");

            OpenInspector();
            CheckCharacterChange();

            AssertSelectionCleared();
        }

        [Test]
        public void OpenInspector_UnknownCacheReference_LaterVersionChangeStillClearsStaleSelection()
        {
            CacheSelectedTrigger(null);
            OpenInspector();
            CheckCharacterChange();
            Assert.That(_trigger.TriggerId, Is.EqualTo(TriggerId));

            using (var serialized = new SerializedObject(_character))
            {
                serialized.FindProperty("_characterVersionMode").intValue = (int)ConvaiCharacterVersionMode.Specific;
                serialized.FindProperty("_characterVersion").stringValue = "1.0";
                serialized.FindProperty("_characterVersionReference").stringValue = CharacterId + "-1.0";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            CheckCharacterChange();

            AssertSelectionCleared();
        }

        private void CacheSelectedTrigger(string cachedReference)
        {
            _trigger.SetAvailableTriggers(new List<TriggerData>
            {
                new(TriggerId, TriggerName, "Saved message", "saved-section", CharacterId)
            }, cachedReference);
            _trigger.SelectTriggerByIndex(0);
        }

        private void OpenInspector()
        {
            // CreateEditor executes the real OnEnable path used when an older scene is inspected.
            _editor = UnityEditor.Editor.CreateEditor(_trigger);
            Assert.That(_editor, Is.TypeOf<ConvaiNarrativeDesignTriggerEditor>());
            // A real character change schedules a delayed refresh. Keep that callback offline;
            // teardown destroys its target before the next editor update as an additional guard.
            FieldInfo fetching = _editor.GetType().GetField("_isFetching", PrivateInstance);
            Assert.That(fetching, Is.Not.Null);
            fetching.SetValue(_editor, true);
        }

        private void CheckCharacterChange()
        {
            _editor.serializedObject.Update();
            MethodInfo method = _editor.GetType().GetMethod("CheckCharacterChange", PrivateInstance);
            Assert.That(method, Is.Not.Null);
            method.Invoke(_editor, null);
        }

        private void AssertSelectionCleared()
        {
            Assert.That(_trigger.AvailableTriggers, Is.Empty);
            Assert.That(_trigger.SelectedTriggerIndex, Is.EqualTo(-1));
            Assert.That(_trigger.TriggerId, Is.Null.Or.Empty);
            Assert.That(_trigger.TriggerName, Is.Null.Or.Empty);
            Assert.That(_trigger.LastFetchedCharacterReference, Is.Null.Or.Empty);
        }
    }
}
