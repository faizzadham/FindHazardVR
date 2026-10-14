#if UNITY_EDITOR
using Convai.Editor.UI;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Presentation
{
    /// <summary>
    ///     The section-header summary is the one place a collapsed personality section still
    ///     answers "what is this character on?". These tests pin the empty state, the named
    ///     asset, and the Custom suffix so identity can never go blank.
    /// </summary>
    public sealed class ConvaiEditorProfileFieldTests
    {
        [Test]
        public void Summarize_NoAsset_IsSdkDefaults()
        {
            Assert.AreEqual(
                ConvaiEditorProfileField.BuiltInDefaultsSummary,
                ConvaiEditorProfileField.Summarize(null));
        }

        [Test]
        public void Summarize_CustomizedWithNoAsset_StaysSdkDefaults()
        {
            Assert.AreEqual(
                ConvaiEditorProfileField.BuiltInDefaultsSummary,
                ConvaiEditorProfileField.Summarize(null, true),
                "Custom without an asset would invent a state the character is not in.");
        }

        [Test]
        public void Summarize_NamedAsset_IsTheAssetName()
        {
            var asset = new GameObject("Sofia_Emotion");
            try
            {
                Assert.AreEqual("Sofia_Emotion", ConvaiEditorProfileField.Summarize(asset));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Summarize_CustomizedAsset_KeepsTheNameAndSaysCustom()
        {
            var asset = new GameObject("Sofia_Emotion");
            try
            {
                Assert.AreEqual(
                    "Sofia_Emotion (" + ConvaiEditorProfileField.CustomLabel + ")",
                    ConvaiEditorProfileField.Summarize(asset, true));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CustomCaption_TellsANoviceWhatToDo()
        {
            StringAssert.Contains("Click", ConvaiEditorProfileField.CustomCaption);
            StringAssert.Contains("fine", ConvaiEditorProfileField.CustomCaption);
        }
    }
}
#endif
