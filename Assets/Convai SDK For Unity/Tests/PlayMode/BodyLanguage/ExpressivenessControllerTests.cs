using System.Collections;
using System.Reflection;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Diagnostics;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     Live controller-glue coverage for the expressiveness dial: the
    ///     Natural default resolves to gain == 1 (the ==1 fast path), a runtime
    ///     <see cref="ConvaiBodyLanguageController.Expressiveness" /> override wins until the
    ///     next profile hot-swap, and a non-Natural value scales the reported amplitude gain to
    ///     <see cref="ExpressivenessCurves.AmplitudeGain" />'s own anchor. Modeled on this
    ///     folder's <c>ScriptedApiControllerGlueTests</c> harness (PlayMode, not EditMode — the
    ///     controller only ticks while <c>Application.isPlaying</c>).
    /// </summary>
    public sealed class ExpressivenessControllerTests
    {
        private GameObject _root;
        private ConvaiBodyLanguageController _controller;
        private ConvaiBodyLanguageProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("ExpressivenessControllerRoot");

            Transform spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            NewChild(spine, "Chest", new Vector3(0f, 0.15f, 0f));

            _root.AddComponent<Animator>();
            _root.AddComponent<EmbodimentContext>();

            _profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(_profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(_profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(_profile, "policyTransitionSeconds", 0f);

            _controller = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_controller, "profile", _profile);
            _controller.enabled = false;
            _controller.enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [UnityTest]
        public IEnumerator NaturalDefault_ResolvesGainOne_TheFastPath()
        {
            yield return null;

            BodyLanguageSnapshot snapshot = _controller.CaptureSnapshot();
            Assert.That(snapshot.Expressiveness, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(snapshot.AmplitudeGain, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator SubtleOverride_ScalesAmplitudeGain_ToAnchorValue()
        {
            yield return null;

            _controller.Expressiveness = 0.25f; // Subtle anchor
            yield return null;

            BodyLanguageSnapshot snapshot = _controller.CaptureSnapshot();
            Assert.That(snapshot.Expressiveness, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(snapshot.AmplitudeGain, Is.EqualTo(ExpressivenessCurves.AmplitudeGain(0.25f)).Within(1e-5f));
        }

        [UnityTest]
        public IEnumerator Override_WinsUntilProfileReApply()
        {
            yield return null;

            _controller.Expressiveness = 0.9f;
            yield return null;

            Assert.That(_controller.Expressiveness, Is.EqualTo(0.9f).Within(1e-5f));

            ((IEmbodimentProfileReceiver)_controller).ApplyProfile(_profile);
            yield return null;

            Assert.That(_controller.Expressiveness, Is.EqualTo(0.5f).Within(1e-5f),
                "A profile hot-swap must clear the runtime override back to the profile's own resolved value.");
        }

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on {target.GetType()}.");
            field.SetValue(target, value);
        }
    }
}
