using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Runtime.Facades;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor.Events;
using UnityEngine.Events;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiMcpToolContractTests
    {
        [Test]
        public void UnityEventUnwiredQueryHandlesNullEmptyAndPersistentListeners()
        {
            Assert.That(Convai.Editor.AI.ConvaiSceneQueries.IsUnityEventUnwired(null), Is.True);

            var emptyEvent = new UnityEvent();
            Assert.That(Convai.Editor.AI.ConvaiSceneQueries.IsUnityEventUnwired(emptyEvent), Is.True);

            var wiredEvent = new UnityEvent();
            UnityEventTools.AddPersistentListener(wiredEvent, PersistentListener);
            Assert.That(Convai.Editor.AI.ConvaiSceneQueries.IsUnityEventUnwired(wiredEvent), Is.False);
        }

        private static readonly string[] ExpectedToolNames = Convai.Editor.AI.ConvaiMcpToolCatalog.All
            .Select(toolId => toolId.Replace('.', '_'))
            .ToArray();

        private static readonly string[] ExpectedAssistantToolIds =
            Convai.Editor.AI.ConvaiMcpToolCatalog.All.ToArray();

        [Test]
        public void ToolCatalogMatchesMcpAttributesAndPackagedDocumentation()
        {
            Assembly[] toolAssemblies =
            {
                typeof(Convai.Editor.AI.ConvaiMcpTools).Assembly,
                typeof(Convai.Modules.LipSync.Editor.AI.ConvaiLipSyncMcpTools).Assembly,
                typeof(Convai.Modules.Narrative.Editor.AI.ConvaiNarrativeMcpTools).Assembly,
                typeof(Convai.Modules.Gaze.Editor.AI.ConvaiGazeMcpTools).Assembly,
                typeof(Convai.Modules.BodyAnimation.Editor.AI.ConvaiBodyAnimationMcpTools).Assembly,
                typeof(Convai.Modules.BodyLanguage.Editor.AI.ConvaiBodyLanguageMcpTools).Assembly,
                typeof(Convai.Modules.Emotion.Editor.AI.ConvaiEmotionMcpTools).Assembly,
                typeof(Convai.Editor.Embodiment.AI.ConvaiEmbodimentMcpTools).Assembly
            };
            string[] attributedToolIds = toolAssemblies
                .Distinct()
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .SelectMany(method => method.CustomAttributes)
                .Where(attribute => attribute.AttributeType.FullName ==
                                    "Unity.AI.MCP.Editor.ToolRegistry.McpToolAttribute")
                .Select(attribute => (string)attribute.ConstructorArguments[0].Value)
                .ToArray();

            Assert.That(
                attributedToolIds,
                Is.EquivalentTo(Convai.Editor.AI.ConvaiMcpToolCatalog.All),
                "MCP tool attributes drifted from ConvaiMcpToolCatalog.All.");

            string packageRoot = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(Convai.Editor.AI.ConvaiMcpTools).Assembly)?.resolvedPath;
            Assert.That(packageRoot, Is.Not.Null.And.Not.Empty);

            string[] documentationPaths =
            {
                "AIAssistantSkills/convai-unity-sdk/SKILL.md",
                "AIAssistantSkills/convai-unity-sdk/resources/foundation-tools.md"
            };
            foreach (string relativePath in documentationPaths)
            {
                string contents = File.ReadAllText(Path.Combine(packageRoot, relativePath));
                foreach (string toolId in Convai.Editor.AI.ConvaiMcpToolCatalog.All)
                    Assert.That(contents, Does.Contain(toolId), $"{relativePath} is missing {toolId}.");
            }
        }

        [Test]
        public void RegistryDiscoversFoundationToolsWithSchemas()
        {
            McpToolRegistry.RefreshTools();
            McpToolInfo[] tools = McpToolRegistry.GetAvailableTools(ignoreEnabledState: true);
            // Counted from the catalog rather than written here: a hand-kept number turns every
            // module that adds a tool into a failure in a file it has no reason to open, and the
            // catalog is the thing this test is actually asserting the registry agrees with.
            Assert.That(
                tools.Count(tool => tool.name.StartsWith("Convai_", StringComparison.Ordinal)),
                Is.EqualTo(ExpectedToolNames.Length));

            foreach (string expectedName in ExpectedToolNames)
            {
                McpToolInfo tool = tools.SingleOrDefault(candidate => candidate.name == expectedName);
                Assert.That(tool, Is.Not.Null, $"Missing MCP tool {expectedName}");
                Assert.That(tool.inputSchema, Is.Not.Null, $"Missing input schema for {expectedName}");
                Assert.That(tool.outputSchema, Is.Not.Null, $"Missing output schema for {expectedName}");
                Assert.That(tool.name.Length, Is.LessThanOrEqualTo(42));
            }

            McpToolInfo configureActions = tools.Single(tool => tool.name == "Convai_ConfigureActions");
            JObject actionSchema = JObject.FromObject(configureActions.inputSchema);
            Assert.That(actionSchema["properties"]?["definitions"]?["items"]?["properties"]?["name"], Is.Not.Null);
            Assert.That(actionSchema["properties"]?["definitions"]?["items"]?["properties"]?["parameters"]?["items"]?["properties"]?["type"], Is.Not.Null);
            Assert.That(actionSchema["properties"]?["objects"]?["items"]?["required"]?.Values<string>(), Does.Contain("gameObjectInstanceId"));

            JObject traceSchema = JObject.FromObject(tools.Single(tool => tool.name == "Convai_TraceRuntimeEvents").inputSchema);
            Assert.That(traceSchema["properties"]?["operation"]?["enum"]?.Values<string>(), Is.EquivalentTo(new[] { "Start", "Read", "Clear", "Stop" }));
            Assert.That(traceSchema["properties"]?["captureTranscripts"]?.Value<bool>("default"), Is.False);

            JObject targetSchema = JObject.FromObject(
                tools.Single(tool => tool.name == "Convai_SetConversationTarget").inputSchema);
            Assert.That(targetSchema["properties"]?["dryRun"]?.Value<bool>("default"), Is.True);
            Assert.That(targetSchema["properties"]?["timeoutSeconds"]?.Value<float>("maximum"), Is.EqualTo(60f));
            Assert.That(targetSchema["required"]?.Values<string>(), Does.Contain("characterInstanceId"));
            Assert.That(targetSchema["properties"]?["characterInstanceId"]?["default"], Is.Null);

            JObject rosterSchema = JObject.FromObject(
                tools.Single(tool => tool.name == "Convai_UpdateCharacterRoster").inputSchema);
            Assert.That(
                rosterSchema["properties"]?["operation"]?["enum"]?.Values<string>(),
                Is.EquivalentTo(new[] { "Add", "Remove" }));
            Assert.That(rosterSchema["properties"]?["dryRun"]?.Value<bool>("default"), Is.True);
            Assert.That(rosterSchema["properties"]?["timeoutSeconds"]?.Value<float>("maximum"), Is.EqualTo(15f));

            JObject setupRosterSchema = JObject.FromObject(
                tools.Single(tool => tool.name == "Convai_SetupMultiCharacterRoster").inputSchema);
            Assert.That(setupRosterSchema["required"]?.Values<string>(), Does.Contain("characterInstanceIds"));
            Assert.That(setupRosterSchema["properties"]?["characterInstanceIds"]?.Value<int>("minItems"), Is.EqualTo(2));
            Assert.That(setupRosterSchema["properties"]?["dryRun"]?.Value<bool>("default"), Is.True);

            JObject readySchema = JObject.FromObject(
                tools.Single(tool => tool.name == "Convai_WaitForCharacterReady").inputSchema);
            Assert.That(readySchema["required"]?.Values<string>(), Does.Contain("characterInstanceId"));
            Assert.That(readySchema["properties"]?["timeoutSeconds"]?.Value<float>("maximum"), Is.EqualTo(60f));

            McpToolInfo narrative = tools.Single(tool => tool.name == "Convai_ConfigureNarrative");
            Assert.That(narrative.description, Does.Contain("never contacts the backend").IgnoreCase);
        }

        [Test]
        public void AssistantWrappersExposeMatchingFoundationToolIds()
        {
            string[] toolIds = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try { return assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
                })
                .Where(type => type.Namespace != null && type.Namespace.StartsWith("Convai", StringComparison.Ordinal))
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .SelectMany(method => method.CustomAttributes)
                .Where(attribute =>
                    attribute.AttributeType.FullName ==
                    "Unity.AI.Assistant.FunctionCalling.AgentToolAttribute")
                .Select(attribute => (string)attribute.ConstructorArguments[1].Value)
                .OrderBy(id => id)
                .ToArray();

            Assert.That(toolIds, Is.EqualTo(ExpectedAssistantToolIds.OrderBy(id => id).ToArray()));
        }

        [Test]
        public void RuntimeTraceSubscribesOnlyToExistingConvaiEventsMembers()
        {
            string[] missingEventNames = Convai.Editor.AI.ConvaiRuntimeEventTrace.SubscribedEventNames
                .Where(eventName => typeof(ConvaiEvents).GetEvent(eventName) == null)
                .ToArray();

            Assert.That(
                missingEventNames,
                Is.Empty,
                $"Runtime trace references missing ConvaiEvents members: {string.Join(", ", missingEventNames)}");
        }

        [Test]
        public void PackageSkillUsesValidEditorVersionConstraint()
        {
            string packageRoot = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(Convai.Editor.AI.ConvaiMcpTools).Assembly)?.resolvedPath;
            Assert.That(packageRoot, Is.Not.Null.And.Not.Empty);

            string skill = File.ReadAllText(Path.Combine(
                packageRoot,
                "AIAssistantSkills/convai-unity-sdk/SKILL.md"));
            Match match = Regex.Match(
                skill,
                "^required_editor_version:\\s*[\\\"'](?<constraint>[^\\\"']+)[\\\"']\\s*$",
                RegexOptions.Multiline);

            Assert.That(match.Success, Is.True, "Missing quoted required_editor_version constraint.");
            Assert.That(
                match.Groups["constraint"].Value,
                Does.Match("^(>=?|<=?|\\^|~|==)?[0-9]+\\.[0-9]+\\.[0-9]+$"));

            string[] declaredTools = Regex.Matches(skill, "^  - (?<tool>Convai\\.[A-Za-z]+)\\s*$", RegexOptions.Multiline)
                .Cast<Match>()
                .Select(value => value.Groups["tool"].Value)
                .OrderBy(value => value)
                .ToArray();
            Assert.That(declaredTools, Is.EqualTo(ExpectedAssistantToolIds.OrderBy(value => value).ToArray()));
        }

        [Test]
        public void ProjectStatusReturnsCredentialStateWithoutApiKeyField()
        {
            object response = Convai.Editor.AI.ConvaiMcpTools.GetProjectStatus();
            object data = ReadProperty(response, "data");
            string[] propertyNames = data.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .ToArray();

            Assert.That((bool)ReadProperty(response, "success"), Is.True);
            Assert.That(propertyNames, Does.Contain("credentialsConfigured"));
            Assert.That(propertyNames, Does.Not.Contain("apiKey").IgnoreCase);
            // Raised deliberately, not to make this pass. Version 7 adds multi-character setup,
            // simulation, readiness, and event-driven state waits.
            Assert.That(Convert.ToInt32(ReadProperty(data, "toolContractVersion")), Is.EqualTo(7));
        }

        [Test]
        public void SharedResponseBuildersPreserveSerializedContracts()
        {
            Assert.That(Compact(Convai.Editor.AI.ConvaiMcpResponses.Success("ok", new { value = 1 })),
                Is.EqualTo("{\"success\":true,\"message\":\"ok\",\"data\":{\"value\":1}}"));
            Assert.That(Compact(Convai.Editor.AI.ConvaiMcpResponses.Failure("E", "bad")),
                Is.EqualTo("{\"success\":false,\"message\":\"bad\",\"data\":{\"code\":\"E\"}}"));
            Assert.That(Compact(Convai.Editor.AI.ConvaiMcpResponses.Failure("E", "bad", new { value = 1 })),
                Is.EqualTo("{\"success\":false,\"message\":\"bad\",\"data\":{\"code\":\"E\",\"details\":{\"value\":1}}}"));
            Assert.That(Compact(Convai.Editor.AI.ConvaiMcpResponses.Issue(
                    "I", "Warning", "message", "evidence", 7, true, "Convai.Fix", new { dryRun = true })),
                Is.EqualTo("{\"code\":\"I\",\"severity\":\"Warning\",\"message\":\"message\",\"evidence\":\"evidence\",\"affectedInstanceId\":7,\"autoFixable\":true,\"suggestedTool\":\"Convai.Fix\",\"suggestedArguments\":{\"dryRun\":true}}"));

            Assert.That(Compact(Convai.Editor.AI.ConvaiMcpTools.GetGuidanceOutputSchema()), Is.EqualTo(
                "{\"type\":\"object\",\"properties\":{\"success\":{\"type\":\"boolean\"},\"message\":{\"type\":\"string\"},\"data\":{\"type\":\"object\",\"additionalProperties\":true}},\"required\":[\"success\",\"message\",\"data\"]}"));
            Assert.That(Compact(Convai.Modules.Narrative.Editor.AI.ConvaiNarrativeMcpTools.ConfigureOutput()), Is.EqualTo(
                "{\"type\":\"object\",\"properties\":{\"success\":{\"type\":\"boolean\"},\"message\":{\"type\":\"string\"},\"data\":{\"type\":\"object\"}},\"required\":[\"success\",\"message\",\"data\"],\"additionalProperties\":true}"));
            Assert.That(Compact(Convai.Modules.Narrative.Editor.AI.ConvaiNarrativeMcpTools.DiagnoseSchema()), Is.EqualTo(
                "{\"type\":\"object\",\"properties\":{\"characterInstanceId\":{\"type\":\"integer\",\"default\":0},\"includeInactive\":{\"type\":\"boolean\",\"default\":true},\"includeContent\":{\"type\":\"boolean\",\"default\":false}},\"additionalProperties\":false}"));
            Assert.That(Compact(Convai.Modules.Narrative.Editor.AI.ConvaiNarrativeMcpTools.ConfigureSchema()), Is.EqualTo(
                "{\"type\":\"object\",\"properties\":{\"characterInstanceId\":{\"type\":\"integer\"},\"managerHostInstanceId\":{\"type\":\"integer\",\"default\":0},\"sections\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"sectionId\":{\"type\":\"string\"},\"sectionName\":{\"type\":\"string\"}},\"required\":[\"sectionId\"]}},\"templateKeys\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}},\"required\":[\"key\"]}},\"triggers\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"hostInstanceId\":{\"type\":\"integer\",\"default\":0},\"triggerId\":{\"type\":\"string\"},\"triggerName\":{\"type\":\"string\"},\"activationMode\":{\"type\":\"string\",\"enum\":[\"Collision\",\"Proximity\",\"Manual\",\"TimeBased\"]},\"proximityRadius\":{\"type\":\"number\",\"default\":3},\"timeDelay\":{\"type\":\"number\",\"default\":0},\"triggerOnce\":{\"type\":\"boolean\",\"default\":true},\"playerLayerMask\":{\"type\":\"integer\",\"default\":-1},\"playerTag\":{\"type\":\"string\",\"default\":\"Player\"},\"queueUntilReady\":{\"type\":\"boolean\",\"default\":true},\"maxWaitTime\":{\"type\":\"number\",\"default\":30}},\"required\":[\"triggerId\"]}},\"dryRun\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"characterInstanceId\"],\"additionalProperties\":false}"));
        }

        [Test]
        public void SetupGuidanceRequiresProactiveEndToEndDefaults()
        {
            object response = Convai.Editor.AI.ConvaiMcpTools.GetGuidance(
                new Convai.Editor.AI.ConvaiGuidanceRequest
                {
                    Topic = Convai.Editor.AI.ConvaiGuidanceTopic.Setup
                });
            JObject json = JObject.FromObject(response);
            string workflow = string.Join("\n", json["data"]?["Workflow"]?.Values<string>() ?? Array.Empty<string>());
            string[] tools = json["data"]?["ConvaiTools"]?.Values<string>().ToArray() ?? Array.Empty<string>();

            Assert.That(workflow, Does.Contain("Convai Player"));
            Assert.That(workflow, Does.Contain("Convai Character"));
            Assert.That(workflow, Does.Contain("HandsFree"));
            Assert.That(workflow, Does.Contain("Ask only"));
            Assert.That(workflow, Does.Contain("do not save"));
            Assert.That(tools, Is.EquivalentTo(ExpectedAssistantToolIds));
        }

        /// <summary>
        ///     Every topic in the enum answers with real guidance.
        /// </summary>
        /// <remarks>
        ///     A topic added to the enum and forgotten in the switch falls through to the Overview
        ///     answer, which reads as working guidance rather than as a missing case — the assistant
        ///     gets sensible-looking text about the wrong subject and has no way to tell.
        /// </remarks>
        [Test]
        public void EveryGuidanceTopicReturnsItsOwnAnswer()
        {
            string overview = Summary(Convai.Editor.AI.ConvaiGuidanceTopic.Overview);

            foreach (Convai.Editor.AI.ConvaiGuidanceTopic topic in
                     Enum.GetValues(typeof(Convai.Editor.AI.ConvaiGuidanceTopic))
                         .Cast<Convai.Editor.AI.ConvaiGuidanceTopic>())
            {
                string summary = Summary(topic);
                Assert.That(summary, Is.Not.Null.And.Not.Empty, $"{topic} has no guidance.");
                if (topic != Convai.Editor.AI.ConvaiGuidanceTopic.Overview)
                    Assert.That(
                        summary,
                        Is.Not.EqualTo(overview),
                        $"{topic} falls through to the Overview answer.");
            }
        }

        [Test]
        public void MultiCharacterGuidanceLeadsWithDiagnosisAndTheAccountGate()
        {
            object response = Convai.Editor.AI.ConvaiMcpTools.GetGuidance(
                new Convai.Editor.AI.ConvaiGuidanceRequest
                {
                    Topic = Convai.Editor.AI.ConvaiGuidanceTopic.MultiCharacter
                });
            JObject json = JObject.FromObject(response);
            string workflow = string.Join(" | ", json["data"]?["Workflow"]?.Values<string>() ?? Array.Empty<string>());
            string prerequisites = string.Join(
                " | ", json["data"]?["Prerequisites"]?.Values<string>() ?? Array.Empty<string>());
            string[] tools = json["data"]?["ConvaiTools"]?.Values<string>().ToArray() ?? Array.Empty<string>();

            Assert.That(workflow, Does.Contain("Convai.DiagnoseConversation"));
            Assert.That(
                prerequisites,
                Does.Contain("Multi-character access"),
                "The account gate is the one thing that stops a correct scene from working.");
            Assert.That(tools, Does.Contain("Convai.ConfigureConversationTargeting"));
        }

        private static string Summary(Convai.Editor.AI.ConvaiGuidanceTopic topic) =>
            JObject.FromObject(Convai.Editor.AI.ConvaiMcpTools.GetGuidance(
                    new Convai.Editor.AI.ConvaiGuidanceRequest { Topic = topic }))
                ["data"]?.Value<string>("Summary");

        [Test]
        public void BootstrapDryRunDoesNotMutateScene()
        {
            object before = Convai.Editor.AI.ConvaiMcpTools.InspectScene(
                new Convai.Editor.AI.ConvaiSceneInspectionRequest());
            object response = Convai.Editor.AI.ConvaiMcpTools.BootstrapScene(
                new Convai.Editor.AI.ConvaiBootstrapRequest { DryRun = true });
            object after = Convai.Editor.AI.ConvaiMcpTools.InspectScene(
                new Convai.Editor.AI.ConvaiSceneInspectionRequest());

            Assert.That((bool)ReadProperty(response, "success"), Is.True);
            Assert.That(ReadCounts(before), Is.EqualTo(ReadCounts(after)));
        }

        [Test]
        public void RegistryExecutionReturnsStructuredContent()
        {
            object result = McpToolRegistry.ExecuteToolAsync(
                    "Convai_GetProjectStatus",
                    new JObject())
                .GetAwaiter()
                .GetResult();

            JObject json = result as JObject;
            Assert.That(json, Is.Not.Null);
            Assert.That(json.Value<bool>("success"), Is.True);
            Assert.That(json["structuredContent"], Is.Not.Null);
            Assert.That(json["data"]?["credentialsConfigured"], Is.Not.Null);
            Assert.That(json.ToString(), Does.Not.Contain("apiKey").IgnoreCase);
        }

        private static string ReadCounts(object response)
        {
            object data = ReadProperty(response, "data");
            object counts = ReadProperty(data, "counts");
            return string.Join(
                ":",
                ReadProperty(counts, "managers"),
                ReadProperty(counts, "rooms"),
                ReadProperty(counts, "players"),
                ReadProperty(counts, "characters"));
        }

        private static string Compact(object value) => JsonConvert.SerializeObject(value, Formatting.None);

        private static object ReadProperty(object value, string propertyName)
        {
            Assert.That(value, Is.Not.Null);
            PropertyInfo property = value.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            Assert.That(property, Is.Not.Null, $"Missing property {propertyName} on {value.GetType().Name}");
            return property.GetValue(value);
        }

        private static void PersistentListener()
        {
        }
    }
}
