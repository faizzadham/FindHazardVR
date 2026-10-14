using System;
using System.Linq;
using System.Reflection;
using Convai.Editor.AI;
using Convai.Modules.Narrative;
using Convai.Modules.Narrative.Editor.AI;
using Convai.Runtime.Components;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiMcpAdvancedToolTests
    {
        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
            ConvaiMcpTools.TraceRuntimeEvents(new ConvaiRuntimeTraceRequest { Operation = ConvaiRuntimeTraceOperation.Clear });
        }

        [TearDown]
        public void TearDown()
        {
            _sceneFixture.End();
        }

        [Test]
        public void ConfigureNarrative_DryRunApplyAndRepeatPreserveEntries()
        {
            GameObject target = new("NPC");
            target.AddComponent<ConvaiCharacter>();
            var request = new ConfigureNarrativeRequest
            {
                CharacterInstanceId = ConvaiMcpEntityRef.ToToolId(target),
                Sections = new[] { new NarrativeSectionInput { SectionId = " intro ", SectionName = "Introduction" } },
                TemplateKeys = new[] { new NarrativeTemplateKeyInput { Key = " coin_count ", Value = "0" } },
                Triggers = new[] { new NarrativeTriggerInput { TriggerId = " celebrate ", TriggerName = "Celebrate", ActivationMode = TriggerActivationMode.Manual } },
                DryRun = true
            };

            JObject preview = Json(ConvaiNarrativeMcpTools.Configure(request));
            Assert.That(preview.Value<bool>("success"), Is.True);
            Assert.That(target.GetComponent<ConvaiNarrativeDesignManager>(), Is.Null);

            request.DryRun = false;
            JObject applied = Json(ConvaiNarrativeMcpTools.Configure(request));
            JObject repeated = Json(ConvaiNarrativeMcpTools.Configure(request));
            ConvaiNarrativeDesignManager manager = target.GetComponent<ConvaiNarrativeDesignManager>();
            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(manager, Is.Not.Null);
            Assert.That(manager.SectionConfigs.Single().SectionId, Is.EqualTo("intro"));
            Assert.That(manager.TemplateKeyConfigs.Single().Key, Is.EqualTo("coin_count"));
            Assert.That(target.GetComponents<ConvaiNarrativeDesignTrigger>(), Has.Length.EqualTo(1));
            Assert.That(repeated["data"]?["changes"]?.Count(), Is.EqualTo(0));
        }

        [Test]
        public void RuntimeTrace_IsBoundedAndTranscriptCaptureDefaultsOff()
        {
            Type traceType = typeof(ConvaiMcpTools).Assembly.GetType("Convai.Editor.AI.ConvaiRuntimeEventTrace");
            MethodInfo add = traceType?.GetMethod("Add", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(add, Is.Not.Null);
            for (int i = 0; i < 300; i++) add.Invoke(null, new object[] { "CharacterReady", "Session", null });

            JObject response = Json(ConvaiMcpTools.TraceRuntimeEvents(new ConvaiRuntimeTraceRequest
            {
                Operation = ConvaiRuntimeTraceOperation.Read,
                Limit = 256
            }));

            Assert.That(response["data"]?.Value<int>("capacity"), Is.EqualTo(256));
            Assert.That(response["data"]?.Value<int>("count"), Is.EqualTo(256));
            Assert.That(response["data"]?.Value<bool>("captureTranscripts"), Is.False);
            Assert.That(response.ToString(), Does.Not.Contain("transcriptText\": \"secret"));
        }

        [Test]
        public void RuntimeTrace_StartInEditModeRefusesWithoutChangingPlayMode()
        {
            bool wasPlaying = EditorApplication.isPlaying;

            JObject response = Json(ConvaiMcpTools.TraceRuntimeEvents(new ConvaiRuntimeTraceRequest
            {
                Operation = ConvaiRuntimeTraceOperation.Start
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("PLAY_MODE_REQUIRED"));
            Assert.That(EditorApplication.isPlaying, Is.EqualTo(wasPlaying));
        }

        private static JObject Json(object value) => JObject.FromObject(value);

    }
}
