using System.Collections.Generic;
using Convai.Runtime;
using Convai.Runtime.DynamicContext;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Runtime.NarrativeDesign;
using Convai.Shared.Actions;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Tests.EditMode.Mocks;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class ProtocolMessageSerializationTests
    {
        [Test]
        public void Serialize_RTVITriggerMessage_SavedTrigger_ContainsOnlyTriggerName()
        {
            var message = new RTVITriggerMessage(ConvaiNarrativeTriggerRequest.SavedTrigger(" wake_up "));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("trigger-message", obj["type"]?.ToString());
            Assert.AreEqual("rtvi-ai", obj["label"]?.ToString());
            Assert.IsFalse(string.IsNullOrEmpty(obj["id"]?.ToString()));
            Assert.AreEqual("wake_up", obj["data"]?["trigger_name"]?.ToString());
            Assert.IsNull(obj["data"]?["trigger_message"]);
        }

        [Test]
        public void Serialize_RTVITriggerMessage_InlineEvent_ContainsOnlyTriggerMessage()
        {
            var message = new RTVITriggerMessage(ConvaiNarrativeTriggerRequest.InlineEvent("Door opened"));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("trigger-message", obj["type"]?.ToString());
            Assert.IsNull(obj["data"]?["trigger_name"]);
            Assert.AreEqual("Door opened", obj["data"]?["trigger_message"]?.ToString());
        }

        [Test]
        public void Serialize_RTVITriggerMessage_ScriptedSpeech_WrapsSpeakTag()
        {
            var message = new RTVITriggerMessage(ConvaiNarrativeTriggerRequest.ScriptedSpeech("Welcome trainee"));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("trigger-message", obj["type"]?.ToString());
            Assert.IsNull(obj["data"]?["trigger_name"]);
            Assert.AreEqual("<speak>Welcome trainee</speak>", obj["data"]?["trigger_message"]?.ToString());
        }

        [Test]
        public void ConvaiNarrativeTriggerRequest_RejectsEmptyPayloads()
        {
            Assert.Throws<ArgumentException>(() => ConvaiNarrativeTriggerRequest.SavedTrigger(" "));
            Assert.Throws<ArgumentException>(() => ConvaiNarrativeTriggerRequest.InlineEvent(""));
            Assert.Throws<ArgumentException>(() => ConvaiNarrativeTriggerRequest.ScriptedSpeech(null));
        }

        [Test]
        public void Serialize_RTVIUpdateTemplateKeys_ContainsExpectedKeys()
        {
            var message = new RTVIUpdateTemplateKeys(
                new Dictionary<string, string> { { "foo", "bar" } });
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("update-template-keys", obj["type"]?.ToString());
            Assert.AreEqual("bar", obj["data"]?["template_keys"]?["foo"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateSceneMetadata_ContainsExpectedKeys()
        {
            var message = new RTVIUpdateSceneMetadata(
                new List<SceneMetadata> { new() { Name = "Town", Description = "Center square" } });
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("update-scene-metadata", obj["type"]?.ToString());
            Assert.AreEqual("Town", obj["data"]?[0]?["name"]?.ToString());
            Assert.AreEqual("Center square", obj["data"]?[0]?["description"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_ContainsExpectedKeys()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate("The player is in the town square."));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("context-update", obj["type"]?.ToString());
            Assert.AreEqual("rtvi-ai", obj["label"]?.ToString());
            Assert.IsFalse(string.IsNullOrEmpty(obj["id"]?.ToString()));
            var data = obj["data"];
            Assert.NotNull(data);
            Assert.AreEqual("The player is in the town square.", data["text"]?.ToString());
            Assert.AreEqual("append", data["mode"]?.ToString());
            Assert.AreEqual("auto", data["run_llm"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_ReplaceMode_SerializesCorrectly()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    "New full context.",
                    ConvaiContextUpdateMode.Replace,
                    ConvaiRespondMode.MustRespond));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("replace", obj["data"]?["mode"]?.ToString());
            Assert.AreEqual("true", obj["data"]?["run_llm"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_ResetMode_SerializesCorrectly()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    null,
                    ConvaiContextUpdateMode.Reset,
                    ConvaiRespondMode.Silent));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("reset", obj["data"]?["mode"]?.ToString());
            Assert.AreEqual("false", obj["data"]?["run_llm"]?.ToString());
            Assert.IsNull(obj["data"]?["text"]);
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_EmptyReplaceText_SerializesIntentionally()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    string.Empty,
                    ConvaiContextUpdateMode.Replace,
                    ConvaiRespondMode.Silent));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual(string.Empty, obj["data"]?["text"]?.ToString());
            Assert.AreEqual("replace", obj["data"]?["mode"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_WithAttentionObject_SerializesCorrectly()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    "Player moved closer to lever.",
                    currentAttentionObject: "lever"));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("lever", obj["data"]?["current_attention_object"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_ClearAttention_SerializesEmptyString()
        {
            var message = new RTVIUpdateDynamicContext(new ConvaiDynamicContextUpdate(
                null,
                ConvaiContextUpdateMode.Append,
                ConvaiRespondMode.Silent,
                currentAttentionObject: string.Empty));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual(string.Empty, obj["data"]?["current_attention_object"]?.ToString());
            Assert.AreEqual("false", obj["data"]?["run_llm"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_WithAttentionObjectDefinition_SerializesObject()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    null,
                    ConvaiContextUpdateMode.Append,
                    ConvaiRespondMode.Silent,
                    currentAttentionObject: new ConvaiActionObjectDefinition
                    {
                        Name = "lever",
                        Description = "A metal lever on the wall"
                    }));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("lever", obj["data"]?["current_attention_object"]?["name"]?.ToString());
            Assert.AreEqual(
                "A metal lever on the wall",
                obj["data"]?["current_attention_object"]?["description"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_WithActionConfig_SerializesCorrectly()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    null,
                    reaction: ConvaiRespondMode.Silent,
                    actionConfig: new ConvaiActionConfigPatch
                    {
                        Objects = new List<ConvaiActionObjectDefinition>
                        {
                            new()
                            {
                                Name = "lever",
                                Description = "A metal lever on the wall"
                            }
                        },
                        CurrentAttentionObject = "lever"
                    }));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("context-update", obj["type"]?.ToString());
            Assert.AreEqual("false", obj["data"]?["run_llm"]?.ToString());
            Assert.IsNull(obj["data"]?["mode"]);
            Assert.IsNull(obj["data"]?["text"]);
            Assert.IsNull(obj["data"]?["current_attention_object"]);
            Assert.IsNull(obj["data"]?["action_config"]?["actions"]);
            Assert.IsNull(obj["data"]?["action_config"]?["characters"]);
            Assert.AreEqual("lever", obj["data"]?["action_config"]?["objects"]?[0]?["name"]?.ToString());
            Assert.AreEqual(
                "A metal lever on the wall",
                obj["data"]?["action_config"]?["objects"]?[0]?["description"]?.ToString());
            Assert.AreEqual("lever", obj["data"]?["action_config"]?["current_attention_object"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_WithExplicitEmptyActions_SerializesEmptyArray()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    null,
                    reaction: ConvaiRespondMode.Silent,
                    actionConfig: new ConvaiActionConfigPatch
                    {
                        Actions = new List<string>()
                    }));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));
            var actions = (JArray)obj["data"]?["action_config"]?["actions"];

            Assert.IsNotNull(actions);
            Assert.AreEqual(0, actions.Count);
            Assert.IsNull(obj["data"]?["action_config"]?["objects"]);
            Assert.IsNull(obj["data"]?["action_config"]?["characters"]);
        }

        [Test]
        public void Serialize_RTVIUpdateDynamicContext_AdvancedFields_SerializesCorrectly()
        {
            var message = new RTVIUpdateDynamicContext(
                new ConvaiDynamicContextUpdate(
                    "Player health is 80",
                    ConvaiContextUpdateMode.Replace,
                    ConvaiRespondMode.MustRespond,
                    removeStatic: true,
                    currentAttentionObject: "health-pack",
                    updateId: "ctx-1"));
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("context-update", obj["type"]?.ToString());
            Assert.AreEqual("ctx-1", obj["data"]?["update_id"]?.ToString());
            Assert.AreEqual("Player health is 80", obj["data"]?["text"]?.ToString());
            Assert.AreEqual("replace", obj["data"]?["mode"]?.ToString());
            Assert.AreEqual("true", obj["data"]?["run_llm"]?.ToString());
            Assert.AreEqual(true, obj["data"]?["remove_static"]?.Value<bool>());
            Assert.AreEqual("health-pack", obj["data"]?["current_attention_object"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIResetIdleTimer_ContainsExpectedKeys()
        {
            var message = new RTVIResetIdleTimer();
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("reset-idle-timer", obj["type"]?.ToString());
            Assert.AreEqual("rtvi-ai", obj["label"]?.ToString());
            Assert.NotNull(obj["data"]);
        }

        [Test]
        public void Serialize_RTVITtsToggle_ContainsExpectedKeys()
        {
            var message = new RTVITtsToggle(true);
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("tts-toggle", obj["type"]?.ToString());
            Assert.AreEqual("True", obj["data"]?["enabled"]?.ToString());
        }

        [Test]
        public void Serialize_RTVISttToggle_ContainsExpectedKeys()
        {
            var message = new RTVISttToggle(true);
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("stt-toggle", obj["type"]?.ToString());
            Assert.AreEqual("True", obj["data"]?["muted"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIInterruptBot_ContainsExpectedKeys()
        {
            var message = new RTVIInterruptBot();
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("interrupt-bot", obj["type"]?.ToString());
            Assert.NotNull(obj["data"]);
        }

        [Test]
        public void Serialize_RTVIKillPipeline_ContainsExpectedKeys()
        {
            var message = new RTVIKillPipeline();
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("kill-pipeline", obj["type"]?.ToString());
            Assert.NotNull(obj["data"]);
        }

        [Test]
        public void Serialize_RTVIForceUserStoppedSpeaking_ContainsExpectedKeys()
        {
            var message = new RTVIForceUserStoppedSpeaking();
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("force-user-stopped-speaking", obj["type"]?.ToString());
            Assert.NotNull(obj["data"]);
        }

        [Test]
        public void MockRoomConnectionService_SendDynamicContext_RecordsCallsWithCorrectParameters()
        {
            var mock = new MockRoomConnectionService();
            mock.RaiseConnected();
            IConvaiDynamicContextTransport transport = mock;

            Assert.IsTrue(transport.SendDynamicContext(new ConvaiDynamicContextUpdate("First line of context.")));
            Assert.IsTrue(transport.SendDynamicContext(new ConvaiDynamicContextUpdate(
                "Replace everything.",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Silent)));
            Assert.IsTrue(transport.SendDynamicContext(new ConvaiDynamicContextUpdate(
                null,
                ConvaiContextUpdateMode.Reset)));

            Assert.AreEqual(3, mock.SentDynamicContextUpdates.Count);
            Assert.AreEqual("First line of context.", mock.SentDynamicContextUpdates[0].Text);
            Assert.AreEqual(ConvaiContextUpdateMode.Append, mock.SentDynamicContextUpdates[0].Mode);
            Assert.AreEqual(ConvaiRespondMode.Auto, mock.SentDynamicContextUpdates[0].Reaction);
            Assert.AreEqual("Replace everything.", mock.SentDynamicContextUpdates[1].Text);
            Assert.AreEqual(ConvaiContextUpdateMode.Replace, mock.SentDynamicContextUpdates[1].Mode);
            Assert.AreEqual(ConvaiRespondMode.Silent, mock.SentDynamicContextUpdates[1].Reaction);
            Assert.IsNull(mock.SentDynamicContextUpdates[2].Text);
            Assert.AreEqual(ConvaiContextUpdateMode.Reset, mock.SentDynamicContextUpdates[2].Mode);
            Assert.AreEqual(ConvaiRespondMode.Auto, mock.SentDynamicContextUpdates[2].Reaction);
        }

        [Test]
        public void Serialize_ActionConfigObjectReference_UsesNameAndDescriptionOnly()
        {
            var config = new ConvaiActionConfig
            {
                Actions = new List<string> { "Move To" },
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new()
                    {
                        Name = "cube",
                        Description = "A red cube",
                        GameObjectReference = new UnityEngine.GameObject("Cube")
                    }
                }
            };

            JObject obj = JObject.Parse(JsonConvert.SerializeObject(config));

            Assert.AreEqual("cube", obj["objects"]?[0]?["name"]?.ToString());
            Assert.AreEqual("A red cube", obj["objects"]?[0]?["description"]?.ToString());
            Assert.IsNull(obj["objects"]?[0]?["gameObjectReference"]);
        }

        [Test]
        public void Serialize_AnyOutboundMessage_ContainsEnvelopeKeys()
        {
            var message = new RTVIUserTextMessage("hello", "typed-1");
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("rtvi-ai", obj["label"]?.ToString());
            Assert.AreEqual("user_text_message", obj["type"]?.ToString());
            Assert.AreEqual("typed-1", obj["id"]?.ToString());
            Assert.NotNull(obj["data"]);
            Assert.AreEqual("typed-1", obj["data"]?["message_id"]?.ToString());
            Assert.AreEqual("hello", obj["data"]?["text"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIVisionStatus_ContainsUpdateId()
        {
            var message = new RTVIVisionStatus("vision-status-1");
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("vision-status", obj["type"]?.ToString());
            Assert.AreEqual("rtvi-ai", obj["label"]?.ToString());
            Assert.AreEqual("vision-status-1", obj["id"]?.ToString());
            Assert.AreEqual("vision-status-1", obj["data"]?["update_id"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIVisionTrigger_OmitsNullOptionalFields()
        {
            var request = new ConvaiVisionTriggerRequest("vision-trigger-1")
            {
                Text = "What can you see?",
                RespondMode = ConvaiRespondMode.MustRespond
            };

            var message = new RTVIVisionTrigger(request);
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("vision-trigger", obj["type"]?.ToString());
            Assert.AreEqual("vision-trigger-1", obj["id"]?.ToString());
            Assert.AreEqual("vision-trigger-1", obj["data"]?["update_id"]?.ToString());
            Assert.AreEqual("What can you see?", obj["data"]?["text"]?.ToString());
            Assert.AreEqual("must_respond", obj["data"]?["respond_mode"]?.ToString());
            Assert.IsNull(obj["data"]?["frame_indices"]);
            Assert.IsNull(obj["data"]?["frame_ids"]);
        }

        [Test]
        public void Serialize_RTVIVisionTrigger_IncludesFrameSelectors()
        {
            // The backend contract: frame_indices is exactly [start, end] (negative = relative to
            // newest), frame_ids are absolute PTS values in nanoseconds.
            var request = new ConvaiVisionTriggerRequest("vision-trigger-2")
            {
                FramePtsIds = new long[] { 171000000000, 175000000000 }
            };
            request.SetFrameWindow(-5, -1);

            var message = new RTVIVisionTrigger(request);
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            CollectionAssert.AreEqual(new[] { -5, -1 }, obj["data"]?["frame_indices"]?.ToObject<int[]>());
            CollectionAssert.AreEqual(new long[] { 171000000000, 175000000000 },
                obj["data"]?["frame_ids"]?.ToObject<long[]>());
        }

        [Test]
        public void Serialize_RTVIRespondModeUpdate_ContainsModalityAndMode()
        {
            var message = new RTVIRespondModeUpdate(
                ConvaiRespondModeLane.ContextUpdate, ConvaiRespondMode.MustRespond, "respond-mode-1");
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.AreEqual("respond-mode-update", obj["type"]?.ToString());
            Assert.AreEqual("respond-mode-1", obj["id"]?.ToString());
            Assert.AreEqual("context_update", obj["data"]?["modality"]?.ToString());
            Assert.AreEqual("must_respond", obj["data"]?["mode"]?.ToString());
            Assert.AreEqual("respond-mode-1", obj["data"]?["update_id"]?.ToString());
        }

        [Test]
        public void Serialize_RTVIRespondModeUpdate_LaneWireStringsMatchBackendModalities()
        {
            Assert.AreEqual("vision", ConvaiRespondModeLane.Vision.ToWireString());
            Assert.AreEqual("context_update", ConvaiRespondModeLane.ContextUpdate.ToWireString());
            Assert.AreEqual("trigger", ConvaiRespondModeLane.Trigger.ToWireString());
            Assert.AreEqual("scene_metadata", ConvaiRespondModeLane.SceneMetadata.ToWireString());
        }

        [Test]
        public void Serialize_RTVIVisionTrigger_FrameWindowIsAlwaysAStartEndPair()
        {
            var request = new ConvaiVisionTriggerRequest("vision-trigger-3");
            request.SetFrameWindow(0, 2);
            request.ClearFrameWindow();

            var message = new RTVIVisionTrigger(request);
            JObject obj = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.IsNull(obj["data"]?["frame_indices"],
                "A cleared frame window must fall back to the backend's default latest-frames selection.");
        }
    }
}
