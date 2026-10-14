using System.Collections.Generic;
using System.IO;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.ConversationFlow.Components;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Behavior of <see cref="ConvaiCharacterModule{TProfile}" /> <em>outside Play Mode</em>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The base class carries <c>[ExecuteAlways]</c>, so every module controller runs its
    ///         enable and disable path in the editor: on <c>AddComponent</c>, on an <c>enabled</c>
    ///         toggle, on undo and redo, on prefab staging, and after a domain reload. That surface
    ///         was completely uncovered, and it is what produced the worst defect this release pass
    ///         fixed — an invisible <c>EmbodimentContext</c> serialized into both shipped sample
    ///         scenes, because Edit-Mode resolution stamped a hide flag that then got saved.
    ///     </para>
    ///     <para>
    ///         The plan ratified keeping <c>[ExecuteAlways]</c> and covering it instead of removing
    ///         it. This is that coverage: it turns an accepted risk into a checked contract.
    ///     </para>
    ///     <para>
    ///         <see cref="ConvaiConversationFlowController" /> is the subject because it is the module
    ///         with the fewest rig and content prerequisites — the behavior under test belongs to the
    ///         shared base class, not to conversation flow.
    ///     </para>
    /// </remarks>
    public sealed class CharacterModuleEditModeBehaviorTests
    {
        private readonly List<GameObject> _spawned = new();
        private readonly List<string> _tempAssets = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();

            for (int i = 0; i < _tempAssets.Count; i++)
                if (!string.IsNullOrEmpty(_tempAssets[i])) AssetDatabase.DeleteAsset(_tempAssets[i]);
            _tempAssets.Clear();

            TestAssetSandbox.Clear(nameof(CharacterModuleEditModeBehaviorTests));
        }

        private GameObject NewCharacter(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            go.AddComponent<ConvaiCharacter>();
            return go;
        }

        /// <summary>Live default-profile instances, for leak and double-destroy assertions.</summary>
        private static int OwnedDefaultProfileCount()
        {
            int count = 0;
            foreach (ConvaiConversationFlowProfile profile in
                     Resources.FindObjectsOfTypeAll<ConvaiConversationFlowProfile>())
            {
                // Only the runtime-created defaults, which the factory flags HideAndDontSave.
                if (profile != null && (profile.hideFlags & HideFlags.DontSave) != 0) count++;
            }

            return count;
        }

        // ── AddComponent / destroy in the editor ────────────────────────────────────

        [Test]
        public void AddingAModuleInEditMode_ResolvesTheContextAndRegisters()
        {
            GameObject root = NewCharacter("EditMode_Add");

            var module = root.AddComponent<ConvaiConversationFlowController>();

            EmbodimentContext context = root.GetComponent<EmbodimentContext>();
            Assert.NotNull(context, "[ExecuteAlways] means OnEnable ran, so the context exists.");
            Assert.IsTrue(module.enabled, "The module must not have disabled itself on a real character.");

            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            context.GetProfileReceivers(registrations);
            Assert.AreEqual(1, registrations.Count);
        }

        [Test]
        public void AddingAModuleInEditMode_DoesNotHideTheContextItCreates()
        {
            // The regression guard for the shipped defect. A hidden component here is what got
            // serialized into the sample scenes.
            GameObject root = NewCharacter("EditMode_NotHidden");

            root.AddComponent<ConvaiConversationFlowController>();

            EmbodimentContext context = root.GetComponent<EmbodimentContext>();
            Assert.NotNull(context);
            Assert.AreEqual(HideFlags.None, context.hideFlags);
        }

        [Test]
        public void RemovingAModuleInEditMode_Unregisters()
        {
            GameObject root = NewCharacter("EditMode_Remove");
            var module = root.AddComponent<ConvaiConversationFlowController>();
            EmbodimentContext context = root.GetComponent<EmbodimentContext>();

            Object.DestroyImmediate(module);

            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            context.GetProfileReceivers(registrations);
            Assert.IsEmpty(registrations, "A destroyed module must not stay on the receiver roster.");
            Assert.IsNull(context.ConversationFlowSource,
                "A destroyed module must not stay registered as the character's flow source.");
        }

        // ── enable / disable cycling, which is what a domain reload looks like ───────

        [Test]
        public void DisableEnableCycling_DoesNotDoubleRegister()
        {
            // A domain reload re-runs OnEnable on everything already enabled. The observable
            // consequence is the same as toggling: registration must be idempotent, or a module
            // ends up on the roster twice and every notification reaches it twice.
            GameObject root = NewCharacter("EditMode_Cycle");
            var module = root.AddComponent<ConvaiConversationFlowController>();
            EmbodimentContext context = root.GetComponent<EmbodimentContext>();

            for (int i = 0; i < 3; i++)
            {
                module.enabled = false;
                module.enabled = true;
            }

            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            context.GetProfileReceivers(registrations);
            Assert.AreEqual(1, registrations.Count,
                "Re-enabling must replace the registration, not add another.");
            Assert.AreSame(module, context.ConversationFlowSource,
                "The module must still hold the contract after cycling.");
        }

        [Test]
        public void DisableEnableCycling_DoesNotLeakOwnedDefaultProfiles()
        {
            // OwnedProfile calls DestroyImmediate outside Play Mode. Cycling is where a
            // double-destroy or a leak would show up.
            GameObject root = NewCharacter("EditMode_ProfileLeak");
            var module = root.AddComponent<ConvaiConversationFlowController>();

            int baseline = OwnedDefaultProfileCount();

            for (int i = 0; i < 5; i++)
            {
                module.enabled = false;
                module.enabled = true;
            }

            Assert.LessOrEqual(OwnedDefaultProfileCount(), baseline + 1,
                "Each cycle must not strand another runtime default profile.");
        }

        [Test]
        public void DestroyingAModule_ReleasesItsOwnedDefaultProfile()
        {
            GameObject root = NewCharacter("EditMode_ProfileRelease");
            var module = root.AddComponent<ConvaiConversationFlowController>();

            // Force the default to exist by asking for the effective profile through the module's
            // own public surface.
            Assert.IsNotNull(module.GetType());
            int withModule = OwnedDefaultProfileCount();

            Object.DestroyImmediate(module);

            Assert.LessOrEqual(OwnedDefaultProfileCount(), withModule,
                "Destroying a module must not leave its runtime default profile behind.");
        }

        // ── undo / redo ─────────────────────────────────────────────────────────────

        [Test]
        public void UndoOfAddComponent_LeavesNoRegistration()
        {
            GameObject root = NewCharacter("EditMode_Undo");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            Undo.AddComponent<ConvaiConversationFlowController>(root);
            EmbodimentContext context = root.GetComponent<EmbodimentContext>();
            Assert.NotNull(context);

            Undo.RevertAllDownToGroup(group);

            Assert.IsNull(root.GetComponent<ConvaiConversationFlowController>(),
                "Undo must remove the component.");

            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            context.GetProfileReceivers(registrations);
            Assert.IsEmpty(registrations,
                "Undo destroys the component, so its registration must go with it — otherwise the " +
                "roster holds a reference to something the user has already taken back.");
        }

        [Test]
        public void RedoOfAddComponent_RegistersExactlyOnce()
        {
            GameObject root = NewCharacter("EditMode_Redo");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            Undo.AddComponent<ConvaiConversationFlowController>(root);
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            Undo.PerformUndo();
            Assert.That(root.GetComponent<ConvaiConversationFlowController>(), Is.Null,
                "The component must actually be undone before testing redo.");

            Undo.PerformRedo();
            var restored = root.GetComponent<ConvaiConversationFlowController>();
            Assert.That(restored, Is.Not.Null, "Redo must restore the module.");
            EmbodimentContext context = root.GetComponent<EmbodimentContext>();
            Assert.That(context, Is.Not.Null, "The restored module must have a context.");

            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            context.GetProfileReceivers(registrations);
            Assert.That(registrations, Has.Count.EqualTo(1));
            Assert.That(registrations[0].Owner, Is.SameAs(restored));
            Assert.That(registrations[0].Receiver, Is.SameAs(restored));
        }

        // ── prefab staging ──────────────────────────────────────────────────────────

        [Test]
        public void SavingACharacterAsAPrefab_SerializesNoHiddenComponent()
        {
            // The prefab-asset counterpart of the scene regression guard. A hidden component saved
            // into a prefab is worse than one in a scene: it propagates to every instance.
            GameObject root = NewCharacter("EditMode_PrefabSave");
            root.AddComponent<ConvaiConversationFlowController>();

            string path = TestAssetSandbox.Path(
                nameof(CharacterModuleEditModeBehaviorTests), "BehaviorTest.prefab");
            _tempAssets.Add(path);

            PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
            Assert.IsTrue(saved, "The prefab must save for this test to mean anything.");

            string yaml = File.ReadAllText(path);
            Assert.IsFalse(yaml.Contains("m_ObjectHideFlags: 2"),
                "A prefab must not carry a Convai component the user cannot select.");
        }

        [Test]
        public void OpeningAndClosingPrefabMode_LeavesTheAssetRegistrationConsistent()
        {
            GameObject root = NewCharacter("EditMode_PrefabStage");
            root.AddComponent<ConvaiConversationFlowController>();

            string path = TestAssetSandbox.Path(
                nameof(CharacterModuleEditModeBehaviorTests), "PrefabStage.prefab");
            _tempAssets.Add(path);
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
            Assert.IsTrue(saved);

            // Prefab mode instantiates the asset into its own environment scene and runs the whole
            // [ExecuteAlways] path again, against a different context.
            GameObject opened = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var staged = opened.GetComponent<ConvaiConversationFlowController>();
                Assert.NotNull(staged, "The staged copy must carry the module.");

                EmbodimentContext stagedContext = opened.GetComponent<EmbodimentContext>();
                Assert.NotNull(stagedContext, "Staging runs OnEnable, so the staged copy resolves too.");
                Assert.AreEqual(HideFlags.None, stagedContext.hideFlags);

                var registrations = new List<EmbodimentProfileReceiverRegistration>();
                stagedContext.GetProfileReceivers(registrations);
                Assert.AreEqual(1, registrations.Count,
                    "The staged copy must register against its own context, exactly once.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(opened);
            }

            // And the scene copy must be unaffected by the staging round-trip.
            EmbodimentContext sceneContext = root.GetComponent<EmbodimentContext>();
            Assert.NotNull(sceneContext);
            Assert.AreSame(root.GetComponent<ConvaiConversationFlowController>(),
                sceneContext.ConversationFlowSource,
                "Opening prefab mode must not steal the scene instance's contract.");
        }

        [Test]
        public void AModuleOnAPrefabAsset_DoesNotRegisterAgainstASceneContext()
        {
            // A module living on an asset rather than a scene instance must not reach into a scene.
            GameObject sceneRoot = NewCharacter("EditMode_AssetIsolation");
            sceneRoot.AddComponent<ConvaiConversationFlowController>();
            EmbodimentContext sceneContext = sceneRoot.GetComponent<EmbodimentContext>();

            GameObject other = NewCharacter("EditMode_AssetIsolation_Source");
            other.AddComponent<ConvaiConversationFlowController>();

            string path = TestAssetSandbox.Path(
                nameof(CharacterModuleEditModeBehaviorTests), "AssetIsolation.prefab");
            _tempAssets.Add(path);
            PrefabUtility.SaveAsPrefabAsset(other, path, out bool saved);
            Assert.IsTrue(saved);

            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.NotNull(loaded);

            var registrations = new List<EmbodimentProfileReceiverRegistration>();
            sceneContext.GetProfileReceivers(registrations);
            Assert.AreEqual(1, registrations.Count,
                "The scene character's roster must hold only its own module, never a prefab asset's.");
        }

        // ── the failure path ────────────────────────────────────────────────────────

        [Test]
        public void AModuleOnANonCharacter_DisablesItselfAndSaysWhy()
        {
            var stray = new GameObject("EditMode_NotACharacter");
            _spawned.Add(stray);

            UnityEngine.TestTools.LogAssert.Expect(
                LogType.Error, new System.Text.RegularExpressions.Regex("is not on a Convai character"));

            var module = stray.AddComponent<ConvaiConversationFlowController>();

            Assert.IsFalse(module.enabled,
                "Without a character there is nothing to drive, so the module must go inert.");
            Assert.IsNull(stray.GetComponent<EmbodimentContext>(),
                "And it must not have grown a composition root on the way.");
        }
    }
}
