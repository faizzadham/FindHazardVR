using System;
using System.Reflection;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Outputs;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Pins the shape of the Emotion module's public API, member by member.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Everything <c>public</c> on <see cref="ConvaiEmotionController" /> and
    ///         <see cref="ConvaiEmotionProfile" /> is customer API: a rename or a changed signature
    ///         breaks projects that already call it. These tests assert the exact shape, so such a
    ///         change has to be made here first and is therefore a decision someone wrote down,
    ///         rather than something noticed after release.
    ///     </para>
    ///     <para>
    ///         They deliberately check signatures rather than behaviour — parameter types, default
    ///         values, event handler types, return types. What the members <em>do</em> is covered by
    ///         the suites next to this file.
    ///     </para>
    /// </remarks>
    [TestFixture]
    [Category("Architecture")]
    public sealed class EmotionPublicSurfaceGuardTests
    {
        [Test]
        public void MoodApi_ExposesExactlySetMoodAndClearMood()
        {
            MethodInfo setMood = typeof(ConvaiEmotionController).GetMethod(
                "SetMood", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(setMood, "ConvaiEmotionController must expose a public SetMood method.");
            ParameterInfo[] setMoodParams = setMood.GetParameters();
            Assert.AreEqual(3, setMoodParams.Length);
            Assert.AreEqual(typeof(string), setMoodParams[0].ParameterType);
            Assert.AreEqual(typeof(float), setMoodParams[1].ParameterType);
            Assert.AreEqual(typeof(float), setMoodParams[2].ParameterType);
            Assert.IsTrue(setMoodParams[2].HasDefaultValue, "transitionSeconds must have a default value.");

            MethodInfo clearMood = typeof(ConvaiEmotionController).GetMethod(
                "ClearMood", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(clearMood, "ConvaiEmotionController must expose a public ClearMood method.");
            ParameterInfo[] clearMoodParams = clearMood.GetParameters();
            Assert.AreEqual(1, clearMoodParams.Length);
            Assert.AreEqual(typeof(float), clearMoodParams[0].ParameterType);
            Assert.IsTrue(clearMoodParams[0].HasDefaultValue, "transitionSeconds must have a default value.");
        }

        [Test]
        public void EmotionEventSurface_ExposesExactlyDominantAndMoodChanged()
        {
            // Both carry (label, intensity). A gameplay script subscribes to these to react to how
            // the character feels, so the handler type is as much API as the event name.
            EventInfo dominantEvent = typeof(ConvaiEmotionController).GetEvent(
                "DominantEmotionChanged", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(dominantEvent, "ConvaiEmotionController must expose a public DominantEmotionChanged event.");
            Assert.AreEqual(typeof(Action<string, float>), dominantEvent.EventHandlerType);

            EventInfo moodEvent = typeof(ConvaiEmotionController).GetEvent(
                "MoodChanged", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(moodEvent, "ConvaiEmotionController must expose a public MoodChanged event.");
            Assert.AreEqual(typeof(Action<string, float>), moodEvent.EventHandlerType);
        }

        [Test]
        public void Profile_MaterialPropertyBinding_ExposesAccessorAndFactory()
        {
            // The accessor reads what the profile was authored with; the factory hands back a
            // runtime copy the character can drive without writing back into the shared asset.
            PropertyInfo materialBindingProperty = typeof(ConvaiEmotionProfile)
                .GetProperty("MaterialBinding", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(materialBindingProperty, "ConvaiEmotionProfile must expose a public MaterialBinding property.");
            Assert.AreEqual(typeof(MaterialPropertyEmotionBinding), materialBindingProperty.PropertyType);

            MethodInfo createMaterialRuntimeBinding = typeof(ConvaiEmotionProfile)
                .GetMethod("CreateMaterialRuntimeBinding", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(createMaterialRuntimeBinding,
                "ConvaiEmotionProfile must expose a public CreateMaterialRuntimeBinding() method.");
            Assert.AreEqual(typeof(MaterialPropertyEmotionBinding), createMaterialRuntimeBinding.ReturnType);
            Assert.AreEqual(0, createMaterialRuntimeBinding.GetParameters().Length);
        }
    }
}
