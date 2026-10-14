using System.Reflection;
using Convai.Runtime.Components;
using Convai.Runtime.Core;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiManagerWebGLTests
    {
        [TestCase(true, RuntimePlatform.WebGLPlayer, true, true, false, true,
            Description = "Arms when WebGL is connected and browser audio still needs activation.")]
        [TestCase(false, RuntimePlatform.WebGLPlayer, true, true, false, false,
            Description = "Feature toggle disables the WebGL scene-click flow.")]
        [TestCase(true, RuntimePlatform.WindowsPlayer, true, true, false, false,
            Description = "Non-WebGL platforms never arm the scene-click flow.")]
        [TestCase(true, RuntimePlatform.WebGLPlayer, false, true, false, false,
            Description = "Missing room manager state cannot arm the flow.")]
        [TestCase(true, RuntimePlatform.WebGLPlayer, true, true, true, false,
            Description = "Already-active audio playback should not arm the flow.")]
        public void CanArmWebGLVoiceStart_ReturnsExpectedValue(
            bool featureEnabled,
            RuntimePlatform platform,
            bool hasRoomManager,
            bool requiresUserGestureForAudio,
            bool isAudioPlaybackActive,
            bool expected)
        {
            MethodInfo method = typeof(ConvaiManager).GetMethod(
                "CanArmWebGLVoiceStart",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method, "Expected CanArmWebGLVoiceStart helper to exist.");

            object result = method.Invoke(null,
                new object[]
                {
                    featureEnabled, platform, hasRoomManager, true, requiresUserGestureForAudio,
                    isAudioPlaybackActive
                });

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        public void ShouldConsumeWebGLVoiceStartGesture_RespectsPointerAndUiState(
            bool pointerPressedThisFrame,
            bool pointerOverUi,
            bool expected)
        {
            MethodInfo method = typeof(ConvaiManager).GetMethod(
                "ShouldConsumeWebGLVoiceStartGesture",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method, "Expected ShouldConsumeWebGLVoiceStartGesture helper to exist.");

            object result = method.Invoke(null, new object[] { pointerPressedThisFrame, pointerOverUi });

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(RuntimeBackgroundPolicy.PauseTimeline, true, RuntimeBackgroundPolicy.MuteButCatchUp)]
        [TestCase(RuntimeBackgroundPolicy.PauseTimeline, false, RuntimeBackgroundPolicy.PauseTimeline)]
        [TestCase(RuntimeBackgroundPolicy.ContinueAudibly, true, RuntimeBackgroundPolicy.ContinueAudibly)]
        [TestCase(RuntimeBackgroundPolicy.MuteButCatchUp, true, RuntimeBackgroundPolicy.MuteButCatchUp)]
        public void ResolveEffectiveBackgroundPolicy_Uses_WebGL_TimelineFallback(
            RuntimeBackgroundPolicy requested,
            bool isWebGl,
            RuntimeBackgroundPolicy expected)
        {
            Assert.AreEqual(expected, ConvaiManager.ResolveEffectiveBackgroundPolicy(requested, isWebGl));
        }
    }
}
