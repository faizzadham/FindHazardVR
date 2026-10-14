using System;
using System.Linq;
using Convai.SampleCommon.UI.Debug;
using Convai.SampleCommon.UI.DynamicContext;
using Convai.SampleCommon.UI.Emotion;
using Convai.SampleCommon.UI.Vision;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Samples
{
    public sealed class SampleDebugPanelTests
    {
        [TestCase(typeof(DynamicContextDebugPanel), "ContextPanel", "Dynamic Context", false)]
        [TestCase(typeof(EmotionStateDebugPanel), "EmotionPanel", "Emotion Debug", false)]
        [TestCase(typeof(VisionContextDebugPanel), "VisionPanel", "Dynamic Vision Context", false)]
        [TestCase(typeof(DynamicContextDebugPanel), "ContextPanel", "Dynamic Context", true)]
        [TestCase(typeof(EmotionStateDebugPanel), "EmotionPanel", "Emotion Debug", true)]
        [TestCase(typeof(VisionContextDebugPanel), "VisionPanel", "Dynamic Vision Context", true)]
        public void PanelBuild_PreservesHostingLayoutAndVisibility(
            Type panelType, string hostedRootName, string heading, bool hosted)
        {
            var root = new GameObject("DebugPanelTest", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                var owner = new GameObject("Controller", typeof(RectTransform));
                owner.transform.SetParent(root.transform, false);
                var panel = (ISampleDebugPanel)owner.AddComponent(panelType);
                Transform panelRoot;
                CanvasScaler existingScaler = null;
                RectTransform content = null;
                if (hosted)
                {
                    root.AddComponent<Canvas>();
                    content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
                    content.SetParent(root.transform, false);
                    panel.ConfigureHosted(content);
                }
                else
                {
                    owner.AddComponent<Canvas>();
                    existingScaler = owner.AddComponent<CanvasScaler>();
                    existingScaler.referenceResolution = new Vector2(1280f, 720f);
                    existingScaler.matchWidthOrHeight = 0.75f;
                }

                panel.EnsureUiBuilt();
                Assert.That(panel.IsUiBuilt, Is.True);
                int objectCount = root.GetComponentsInChildren<Transform>(true).Length;
                panel.EnsureUiBuilt();
                Assert.That(root.GetComponentsInChildren<Transform>(true), Has.Length.EqualTo(objectCount),
                    "Building an existing panel must not duplicate its UI.");
                Assert.That(root.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == heading), Is.True);
                Assert.That(root.GetComponentsInChildren<Canvas>(true), Has.Length.EqualTo(1));

                if (hosted)
                {
                    panelRoot = content.Find(hostedRootName);
                    Assert.That(panelRoot, Is.Not.Null);
                    Assert.That(owner.GetComponent<Canvas>(), Is.Null,
                        "A hosted panel must use its host canvas rather than creating an overlay.");
                    Assert.That(panelRoot.GetComponent<LayoutElement>().preferredHeight, Is.GreaterThan(0f));
                    Assert.That(panelRoot.gameObject.activeSelf, Is.False);
                    panel.OnPanelShown();
                    Assert.That(panelRoot.gameObject.activeSelf, Is.True);
                    panel.OnPanelHidden();
                    Assert.That(panelRoot.gameObject.activeSelf, Is.False);
                }
                else
                {
                    Assert.That(owner.transform.Find("Panel"), Is.Not.Null);
                    Assert.That(owner.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                    Assert.That(owner.GetComponent<GraphicRaycaster>(), Is.Not.Null);
                    Assert.That(owner.GetComponent<CanvasScaler>(), Is.SameAs(existingScaler));
                    Assert.That(existingScaler.referenceResolution, Is.EqualTo(new Vector2(1280f, 720f)));
                    Assert.That(existingScaler.matchWidthOrHeight, Is.EqualTo(0.75f));
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
