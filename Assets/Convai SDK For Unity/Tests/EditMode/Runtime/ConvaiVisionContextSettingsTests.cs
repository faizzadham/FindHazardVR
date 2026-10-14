using System.Collections.Generic;
using System.Reflection;
using Convai.RestAPI.Services;
using Convai.Runtime;
using Convai.Runtime.Vision.Context;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class ConvaiVisionContextSettingsTests
    {
        [Test]
        public void Defaults_MatchBackendDefaults_FiveFramesSingleHorizon()
        {
            RoomVisionInputConfig config = ConvaiVisionInputSettings.CreateDefault().ToRoomVisionInputConfig();

            Assert.IsTrue(config.Enabled);
            Assert.AreEqual(1f, config.SampleIntervalSecs);
            Assert.AreEqual(5, config.FramesPerTurn);
            Assert.IsNull(config.BufferFrames, "Unset buffer must be omitted so the backend defaults it to frames_per_turn.");
            Assert.IsNull(config.SamplingWindows, "Defaults must be single-horizon: no sampling windows on the wire.");
            Assert.AreEqual(10f, config.StalenessSeconds);
            Assert.IsNull(config.MaxResolution, "Unset resolution must be omitted so the provider-aware default applies.");
            Assert.IsTrue(config.ReplacePreviousVisionContext);
        }

        [Test]
        public void SamplingWindows_TrimGreedilyIntoFramesPerTurnBudget()
        {
            // Backend rule: sum(windows.count) <= frames_per_turn, else the connect request is
            // rejected. The converter must trim rather than forward an invalid config.
            var settings = CreateSettings(framesPerTurn: 8, windows: new List<ConvaiVisionSamplingWindowSettings>
            {
                ConvaiVisionSamplingWindowSettings.Create(6, 300),
                ConvaiVisionSamplingWindowSettings.Create(6, 1000)
            });

            RoomVisionInputConfig config = settings.ToRoomVisionInputConfig();

            Assert.IsNotNull(config.SamplingWindows);
            Assert.AreEqual(2, config.SamplingWindows.Count);
            Assert.AreEqual(6, config.SamplingWindows[0].Count);
            Assert.AreEqual(300, config.SamplingWindows[0].IntervalMs);
            Assert.AreEqual(2, config.SamplingWindows[1].Count, "Second window must be trimmed into the remaining budget.");
            Assert.AreEqual(1000, config.SamplingWindows[1].IntervalMs);
        }

        [Test]
        public void SamplingWindows_BeyondExhaustedBudget_AreDropped()
        {
            var settings = CreateSettings(framesPerTurn: 4, windows: new List<ConvaiVisionSamplingWindowSettings>
            {
                ConvaiVisionSamplingWindowSettings.Create(4, 300),
                ConvaiVisionSamplingWindowSettings.Create(6, 1000)
            });

            RoomVisionInputConfig config = settings.ToRoomVisionInputConfig();

            Assert.IsNotNull(config.SamplingWindows);
            Assert.AreEqual(1, config.SamplingWindows.Count);
            Assert.AreEqual(4, config.SamplingWindows[0].Count);
        }

        [Test]
        public void SamplingWindows_ZeroIntervalEntries_AreSkipped_NotClampedToOneMillisecond()
        {
            // The backend samples at the fastest window's interval, so a half-configured window
            // (count set, interval left at the default 0) must be dropped — clamping 0 up to the
            // 1 ms minimum would request maximal capture load the user never intended.
            var settings = CreateSettings(framesPerTurn: 5, windows: new List<ConvaiVisionSamplingWindowSettings>
            {
                ConvaiVisionSamplingWindowSettings.Create(5, 0),
                ConvaiVisionSamplingWindowSettings.Create(3, 1000)
            });

            RoomVisionInputConfig config = settings.ToRoomVisionInputConfig();

            Assert.IsNotNull(config.SamplingWindows);
            Assert.AreEqual(1, config.SamplingWindows.Count);
            Assert.AreEqual(3, config.SamplingWindows[0].Count);
            Assert.AreEqual(1000, config.SamplingWindows[0].IntervalMs);
        }

        [Test]
        public void SamplingWindows_ZeroCountEntries_AreSkipped()
        {
            var settings = CreateSettings(framesPerTurn: 5, windows: new List<ConvaiVisionSamplingWindowSettings>
            {
                ConvaiVisionSamplingWindowSettings.Create(0, 300),
                ConvaiVisionSamplingWindowSettings.Create(3, 1000)
            });

            RoomVisionInputConfig config = settings.ToRoomVisionInputConfig();

            Assert.IsNotNull(config.SamplingWindows);
            Assert.AreEqual(1, config.SamplingWindows.Count);
            Assert.AreEqual(3, config.SamplingWindows[0].Count);
        }

        [Test]
        public void BufferFrames_ClampsToAtLeastFramesPerTurn()
        {
            // Backend rule: buffer_frames >= frames_per_turn, else 422.
            var settings = CreateSettings(framesPerTurn: 10, bufferFrames: 3);

            RoomVisionInputConfig config = settings.ToRoomVisionInputConfig();

            Assert.AreEqual(10, config.BufferFrames);
        }

        [Test]
        public void OutOfRangeScalars_ClampIntoBackendValidatedRanges()
        {
            var settings = CreateSettings(framesPerTurn: 99, sampleIntervalSeconds: 500f, stalenessSeconds: 999f,
                maxResolution: 9999, bufferFrames: 500);

            RoomVisionInputConfig config = settings.ToRoomVisionInputConfig();

            Assert.AreEqual(20, config.FramesPerTurn);
            Assert.AreEqual(60f, config.SampleIntervalSecs);
            Assert.AreEqual(120f, config.StalenessSeconds);
            Assert.AreEqual(2048, config.MaxResolution);
            Assert.AreEqual(120, config.BufferFrames);
        }

        [Test]
        public void RespondModes_NeverIncludeUserInputLanes()
        {
            RoomRespondModesConfig config = ConvaiVisionRespondModeSettings.CreateDefault().ToRoomRespondModesConfig();

            Assert.IsNull(config.Text, "text is a user-input floor lane and must never be sent.");
            Assert.IsNull(config.Audio, "audio is a user-input floor lane and must never be sent.");
            Assert.AreEqual("silent", config.Vision);
            Assert.AreEqual("auto", config.ContextUpdate);
            Assert.AreEqual("must_respond", config.Trigger);
            Assert.AreEqual("silent", config.SceneMetadata);
        }

        [Test]
        public void RespondMode_WireStringsRoundTrip()
        {
            Assert.IsTrue(ConvaiRespondModeExtensions.TryParseWireString(" MUST_RESPOND ", out ConvaiRespondMode mode));
            Assert.AreEqual(ConvaiRespondMode.MustRespond, mode);
            Assert.IsTrue(ConvaiRespondModeExtensions.TryParseWireString("auto", out mode));
            Assert.AreEqual(ConvaiRespondMode.Auto, mode);
            Assert.IsTrue(ConvaiRespondModeExtensions.TryParseWireString("silent", out mode));
            Assert.AreEqual(ConvaiRespondMode.Silent, mode);
            Assert.IsFalse(ConvaiRespondModeExtensions.TryParseWireString("shout", out _));
            Assert.IsFalse(ConvaiRespondModeExtensions.TryParseWireString(null, out _));
        }

        private static ConvaiVisionInputSettings CreateSettings(
            int framesPerTurn,
            List<ConvaiVisionSamplingWindowSettings> windows = null,
            float sampleIntervalSeconds = 1f,
            float stalenessSeconds = 10f,
            int maxResolution = 0,
            int bufferFrames = 0)
        {
            var settings = ConvaiVisionInputSettings.CreateDefault();
            SetField(settings, "_framesPerTurn", framesPerTurn);
            SetField(settings, "_sampleIntervalSeconds", sampleIntervalSeconds);
            SetField(settings, "_stalenessSeconds", stalenessSeconds);
            SetField(settings, "_maxResolution", maxResolution);
            SetField(settings, "_bufferFrames", bufferFrames);
            if (windows != null)
                SetField(settings, "_samplingWindows", windows);
            return settings;
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}.");
            field.SetValue(target, value);
        }
    }
}
