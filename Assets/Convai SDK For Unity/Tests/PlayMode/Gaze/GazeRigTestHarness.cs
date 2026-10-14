using System;
using System.Collections;
using System.Reflection;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.Gaze
{
    /// <summary>
    ///     Shared procedural rig for the Gaze PlayMode suites. Before this existed the module had
    ///     389 EditMode tests covering every policy, director, and piece of solver math — and
    ///     exactly one PlayMode test, so nothing exercised
    ///     <c>GazeChainCalibration.Bind</c> → <c>HeadTorsoSolver</c> → <c>EyeSolver</c> → bone
    ///     writes against a real hierarchy. Every rig-shaped assumption (the +Z-forward
    ///     requirement, the eye-backend fallback chain, rebinding mid-play) was verified only by
    ///     hand.
    /// </summary>
    /// <remarks>
    ///     Built in code rather than from a shipped asset, exactly like
    ///     <see cref="HeadGestureCompositionTests" />: a plain (non-Humanoid) Animator, so
    ///     <c>StandardRigBinding</c> resolves through its name-based fallback tables and the test
    ///     needs no avatar asset, no scene, and no package content.
    /// </remarks>
    internal sealed class GazeRigTestHarness : IDisposable
    {
        public GameObject Root { get; private set; }
        public Transform Spine { get; private set; }
        public Transform Chest { get; private set; }
        public Transform UpperChest { get; private set; }
        public Transform Neck { get; private set; }
        public Transform Head { get; private set; }
        public Transform LeftEye { get; private set; }
        public Transform RightEye { get; private set; }
        public ConvaiGazeController Gaze { get; private set; }
        public ConvaiGazeProfile Profile { get; private set; }

        /// <param name="withEyeBones">Author LeftEye/RightEye bones under the head.</param>
        /// <param name="headForwardLocalRotation">
        ///     Local rotation applied to the head bone at build time. Identity means the head's
        ///     local +Z is the character's visual forward — the convention the module documents and
        ///     requires. A non-identity value is how a mis-oriented import is simulated.
        /// </param>
        public static GazeRigTestHarness Build(
            bool withEyeBones = true, Quaternion? headForwardLocalRotation = null)
        {
            var harness = new GazeRigTestHarness();
            harness.Root = new GameObject("GazeRigTestCharacter");

            harness.Spine = NewChild(harness.Root.transform, "Spine", new Vector3(0f, 1f, 0f));
            harness.Chest = NewChild(harness.Spine, "Chest", new Vector3(0f, 0.15f, 0f));
            harness.UpperChest = NewChild(harness.Chest, "UpperChest", new Vector3(0f, 0.15f, 0f));
            harness.Neck = NewChild(harness.UpperChest, "Neck", new Vector3(0f, 0.1f, 0f));
            harness.Head = NewChild(harness.Neck, "Head", new Vector3(0f, 0.1f, 0f));
            if (headForwardLocalRotation.HasValue)
                harness.Head.localRotation = headForwardLocalRotation.Value;

            if (withEyeBones)
            {
                harness.LeftEye = NewChild(harness.Head, "LeftEye", new Vector3(-0.03f, 0.05f, 0.08f));
                harness.RightEye = NewChild(harness.Head, "RightEye", new Vector3(0.03f, 0.05f, 0.08f));
            }

            harness.Root.AddComponent<Animator>();
            harness.Root.AddComponent<EmbodimentContext>();

            harness.Profile = ConvaiGazeProfile.CreateDefault();
            // A short head shift so the aim converges within a few seconds of wall clock;
            // ambient life and nods off so an assertion reads the aim and nothing else. This pins
            // wiring and geometry, never feel — the authored timings are tested in the
            // deterministic EditMode director suites.
            SetPrivateField(harness.Profile, "headOnsetSeconds", 0f);
            SetPrivateField(harness.Profile, "headTurnBaseSeconds", 0.05f);
            SetPrivateField(harness.Profile, "headTurnSecondsPerDegree", 0.001f);
            SetPrivateField(harness.Profile, "maxHeadAngularSpeed", 720f);
            SetPrivateField(harness.Profile, "enableAmbientExploration", false);
            SetPrivateField(harness.Profile, "enableListeningNods", false);
            SetPrivateField(harness.Profile, "enableCuriosityGlances", false);
            SetPrivateField(harness.Profile, "enableBlink", false);

            harness.Gaze = harness.Root.AddComponent<ConvaiGazeController>();
            SetPrivateField(harness.Gaze, "profile", harness.Profile);
            // The controller is auto-provisioned a player anchor on enable; these suites drive it
            // with explicit scripted requests instead, so the anchor is irrelevant either way.
            harness.Gaze.enabled = false;
            harness.Gaze.enabled = true;

            return harness;
        }

        public void Dispose()
        {
            if (Root != null) Object.DestroyImmediate(Root);
            if (Profile != null) Object.DestroyImmediate(Profile);
        }

        public static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        /// <summary>
        ///     Writes a serialized field by name, looking one level into nested settings blocks
        ///     when the target keeps its fields grouped (the Gaze Profile does). Which block owns
        ///     a setting is the asset's business, not a test's.
        /// </summary>
        public static void SetPrivateField(object target, string fieldName, object value)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

            FieldInfo field = target.GetType().GetField(fieldName, Flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            foreach (FieldInfo block in target.GetType().GetFields(Flags))
            {
                FieldInfo nested = block.FieldType.GetField(fieldName, Flags);
                if (nested == null) continue;

                object blockValue = block.GetValue(target);
                if (blockValue == null) continue;

                nested.SetValue(blockValue, value);
                return;
            }

            Assert.Fail($"Missing field {fieldName} on {target.GetType().Name}.");
        }

        /// <summary>
        ///     Runs frames for up to <paramref name="realSeconds" /> of wall-clock time. A fixed
        ///     frame COUNT is only valid under an assumed per-frame delta, and this headless runner's
        ///     frames carry far less simulated time than an interactive editor frame.
        /// </summary>
        public static IEnumerator RunForRealSeconds(float realSeconds, Action perFrame = null)
        {
            float deadline = Time.realtimeSinceStartup + realSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                perFrame?.Invoke();
            }
        }
    }
}
