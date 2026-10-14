using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.EventSystem;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using Convai.Modules.LipSync.Profiles;
using Convai.Runtime.Core;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiLipSyncComponentTests
    {
        [SetUp]
        public void SetUp()
        {
            LipSyncProfileCatalog.ClearCachesForTests();
            LipSyncDefaultMappingResolver.ClearCachesForTests();
        }

        [TearDown]
        public void TearDown()
        {
            LipSyncProfileCatalog.ClearCachesForTests();
            LipSyncDefaultMappingResolver.ClearCachesForTests();
            for (int i = 0; i < _cleanup.Count; i++)
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);

            _cleanup.Clear();
        }

        private sealed class TestCharacterIdentitySource : MonoBehaviour, ICharacterIdentitySource
        {
            [SerializeField] private string _characterId = "char-1";
            public string CharacterId => _characterId;

            public void SetCharacterId(string characterId) => _characterId = characterId;
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private readonly List<Object> _cleanup = new();

        [Test]
        public void Awake_WhenCharacterIdentitySourceMissing_DisablesComponent()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(false);
            EnsureProfileCatalogContains("arkit", "arkit");

            InvokePrivateInstanceMethod(component, "Awake");

            Assert.IsFalse(component.enabled);
        }

        [Test]
        public void Awake_WhenCharacterIdentityIsEmpty_DisablesComponentWithExplicitError()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true, "   ");
            EnsureProfileCatalogContains("arkit", "arkit");

            InvokePrivateInstanceMethod(component, "Awake");

            Assert.IsFalse(component.enabled);
        }

        [Test]
        public void Awake_WithIdentitySourceAndValidProfile_LeavesComponentEnabled()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("arkit", "arkit");

            InvokePrivateInstanceMethod(component, "Awake");

            Assert.IsTrue(component.enabled);
        }

        [Test]
        public void ModuleId_IsUniqueForEachCharacterComponentInstance()
        {
            ConvaiLipSyncComponent first = CreateComponentGameObject(true, "shared-character");
            ConvaiLipSyncComponent second = CreateComponentGameObject(true, "shared-character");

            Assert.That(first.ModuleId, Does.StartWith("convai.lipsync:shared-character:"));
            Assert.That(second.ModuleId, Does.StartWith("convai.lipsync:shared-character:"));
            Assert.That(first.ModuleId, Is.Not.EqualTo(second.ModuleId));
        }

        [Test]
        public void Inject_BeforeAwake_DoesNotThrow_AndRemainsIdempotent()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("arkit", "arkit");
            var eventHub = new EventHub(new ImmediateScheduler());

            Assert.DoesNotThrow(() => component.Inject(eventHub));
            Assert.DoesNotThrow(() => InvokePrivateInstanceMethod(component, "Awake"));
            Assert.IsTrue(component.enabled);
        }

        [Test]
        public void Inject_WithNullEventHub_ThrowsArgumentNullException()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);

            Assert.Throws<ArgumentNullException>(() => component.Inject(null));
        }

        [Test]
        public void RuntimePause_Freezes_And_Resume_Restores_PresentationTicks()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);

            component.PauseAsync(RuntimePauseReason.ApplicationBackground).GetAwaiter().GetResult();
            Assert.IsTrue(component.IsPresentationPaused);

            component.ResumeAsync().GetAwaiter().GetResult();
            Assert.IsFalse(component.IsPresentationPaused);
        }

        [Test]
        public void Inject_WhenPackedDataArrivesForBoundCharacter_BuffersStreamWithoutTargetMeshes()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("metahuman", "mha");
            SetSerializedField(component, "_lockedProfileId", "metahuman");
            InvokePrivateInstanceMethod(component, "Awake");

            EventHub eventHub = new(new ImmediateScheduler());
            component.Inject(eventHub);

            Assert.AreEqual(0f, component.GetTotalStreamDuration(), 0.0001f);

            eventHub.Publish(LipSyncPackedDataReceived.Create("char-2", "participant-1",
                CreateChunk(LipSyncProfileId.MetaHuman, 4)));

            Assert.AreEqual(0f, component.GetTotalStreamDuration(), 0.0001f);

            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.MetaHuman, 4)));

            BlendshapeSnapshot snapshot = component.GetBlendshapeSnapshot();
            Assert.AreEqual(PlaybackState.Buffering, component.EngineState);
            Assert.Greater(component.GetTotalStreamDuration(), 0f);
            Assert.IsTrue(snapshot.IsValid);
            Assert.AreEqual(1, snapshot.Count);
        }

        [Test]
        public void TryGetLipSyncTransportOptions_WithValidProfile_ReturnsEnabledValidOptions()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("metahuman", "mha");
            SetSerializedField(component, "_lockedProfileId", "metahuman");
            InvokePrivateInstanceMethod(component, "Awake");
            component.Inject(new EventHub(new ImmediateScheduler()));

            bool success = component.TryGetLipSyncTransportOptions(out LipSyncTransportOptions options);

            Assert.IsTrue(success);
            Assert.IsTrue(options.IsValid);
            Assert.AreEqual("metahuman", options.ProfileId.Value);
        }

        [Test]
        public void EffectiveMapping_WhenAssignedMapProfileMismatches_UsesResolverFallbackMap()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("arkit", "arkit");
            SetSerializedField(component, "_lockedProfileId", "arkit");

            ConvaiLipSyncMapAsset mismatched = CreateMapAssetWithProfile("metahuman");
            ConvaiLipSyncMapAsset arkitDefault = CreateMapAssetWithProfile("arkit");
            SetSerializedField(component, "_mapping", mismatched);

            var registry = ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>();
            Track(registry);
            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 1;
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("_profileId").stringValue = "arkit";
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("_defaultMap").objectReferenceValue = arkitDefault;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            InvokePrivateInstanceMethod(component, "Awake");
            ConvaiLipSyncMapAsset effective = component.EffectiveMapping;

            Assert.AreSame(arkitDefault, effective);
        }

        [Test]
        public void EffectiveMapping_WhenMappingMissing_UsesProfileDefaultMappingFallback()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("arkit", "arkit");
            SetSerializedField(component, "_lockedProfileId", "arkit");
            SetSerializedField(component, "_mapping", (Object)null);

            ConvaiLipSyncMapAsset arkitDefault = CreateMapAssetWithProfile("arkit");
            var registry = ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>();
            Track(registry);
            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 1;
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("_profileId").stringValue = "arkit";
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("_defaultMap").objectReferenceValue = arkitDefault;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            ConvaiLipSyncMapAsset effective = component.EffectiveMapping;

            Assert.AreSame(arkitDefault, effective);
            Assert.IsNull(component.Mapping);
        }

        [Test]
        public void Awake_WithValidSetup_RuntimeControllerUsesDspTimePlaybackClock()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("arkit", "arkit");

            InvokePrivateInstanceMethod(component, "Awake");

            IPlaybackClock activeClock = GetActiveClockForTest(component);
            Assert.IsInstanceOf<DspTimePlaybackClock>(activeClock);
        }

        [Test]
        public void RuntimeConfig_WhenProfileChangesOnValidate_RefreshesTransportOptions()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains(
                ("metahuman", "mha"),
                ("arkit", "arkit"));
            SetSerializedField(component, "_lockedProfileId", "metahuman");
            InvokePrivateInstanceMethod(component, "Awake");

            bool initialSuccess = component.TryGetLipSyncTransportOptions(out LipSyncTransportOptions initialOptions);
            Assert.IsTrue(initialSuccess);
            Assert.AreEqual("metahuman", initialOptions.ProfileId.Value);

            SetSerializedField(component, "_lockedProfileId", "arkit");
            InvokePrivateInstanceMethod(component, "OnValidate");

            bool updatedSuccess = component.TryGetLipSyncTransportOptions(out LipSyncTransportOptions updatedOptions);
            Assert.IsTrue(updatedSuccess);
            Assert.AreEqual("arkit", updatedOptions.ProfileId.Value);
        }

        [Test]
        public async Task StopAsync_AfterBufferedStream_ResetsRuntimeState()
        {
            ConvaiLipSyncComponent component = CreateComponentGameObject(true);
            EnsureProfileCatalogContains("arkit", "arkit");
            InvokePrivateInstanceMethod(component, "Awake");
            EventHub eventHub = new(new ImmediateScheduler());
            component.Inject(eventHub);
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 4)));
            Assert.AreEqual(PlaybackState.Buffering, component.EngineState);

            await component.StopAsync();

            Assert.AreEqual(PlaybackState.Idle, component.EngineState);
            Assert.AreEqual(0f, component.GetTotalBufferedDuration(), 0.0001f);
        }

        private ConvaiLipSyncComponent CreateComponentGameObject(bool withIdentitySource,
            string identityCharacterId = "char-1")
        {
            GameObject go = new("lipsync-component-test");
            Track(go);
            if (withIdentitySource)
            {
                var identitySource = go.AddComponent<TestCharacterIdentitySource>();
                identitySource.SetCharacterId(identityCharacterId);
            }

            return go.AddComponent<ConvaiLipSyncComponent>();
        }

        private void EnsureProfileCatalogContains(string profileId, string format) =>
            EnsureProfileCatalogContains((profileId, format));

        private void EnsureProfileCatalogContains(params (string profileId, string format)[] profilesToRegister)
        {
            if (profilesToRegister == null || profilesToRegister.Length == 0) return;

            var registry = ScriptableObject.CreateInstance<ConvaiLipSyncProfileRegistry>();
            Track(registry);
            SerializedObject registrySerialized = new(registry);
            registrySerialized.FindProperty("_priority").intValue = 0;
            SerializedProperty profiles = registrySerialized.FindProperty("_profiles");
            profiles.arraySize = profilesToRegister.Length;
            for (int i = 0; i < profilesToRegister.Length; i++)
            {
                (string profileId, string format) descriptor = profilesToRegister[i];
                var profile = ScriptableObject.CreateInstance<ConvaiLipSyncProfile>();
                Track(profile);
                SerializedObject profileSerialized = new(profile);
                profileSerialized.FindProperty("_profileId").stringValue = descriptor.profileId;
                profileSerialized.FindProperty("_displayName").stringValue = descriptor.profileId;
                profileSerialized.FindProperty("_transportFormat").stringValue = descriptor.format;
                profileSerialized.ApplyModifiedPropertiesWithoutUndo();
                profiles.GetArrayElementAtIndex(i).objectReferenceValue = profile;
            }

            registrySerialized.ApplyModifiedPropertiesWithoutUndo();

            LipSyncProfileCatalog.SetRegistryOverridesForTests(registry, new ConvaiLipSyncProfileRegistry[0]);
        }

        private ConvaiLipSyncMapAsset CreateMapAssetWithProfile(string profileId)
        {
            var map = ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>();
            Track(map);
            SerializedObject serialized = new(map);
            serialized.FindProperty("_targetProfileId").stringValue = profileId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return map;
        }

        private static LipSyncPackedChunk CreateChunk(LipSyncProfileId profileId, int frameCount)
        {
            float[][] frames = new float[frameCount][];
            for (int i = 0; i < frameCount; i++) frames[i] = new[] { i / (float)Math.Max(1, frameCount) };

            return new LipSyncPackedChunk(profileId, 60f, new[] { "jawOpen" }, frames);
        }

        private static void SetSerializedField(Object target, string fieldName, string value)
        {
            SerializedObject serialized = new(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            Assert.IsNotNull(property, $"Serialized field '{fieldName}' was not found.");
            property.stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetSerializedField(Object target, string fieldName, Object value)
        {
            SerializedObject serialized = new(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            Assert.IsNotNull(property, $"Serialized field '{fieldName}' was not found.");
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private void InvokePrivateInstanceMethod(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(target.GetType().Name, methodName);

            method.Invoke(target, null);
        }

        private static IPlaybackClock GetActiveClockForTest(ConvaiLipSyncComponent component)
        {
            object runtimeController = GetPrivateField<object>(component, "_runtimeController");
            PropertyInfo currentClockProperty = runtimeController.GetType().GetProperty(
                "CurrentClock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (currentClockProperty == null)
                throw new MissingMemberException(runtimeController.GetType().Name, "CurrentClock");

            return (IPlaybackClock)currentClockProperty.GetValue(runtimeController);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(target.GetType().Name, fieldName);

            return (T)field.GetValue(target);
        }

        private void Track<T>(T obj) where T : Object
        {
            if (obj != null) _cleanup.Add(obj);
        }
    }
}
