using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.Embodiment.Semantics;
using Convai.Editor.AI;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.BodyLanguage.Editor;
using Convai.Modules.BodyLanguage.Editor.AI;
using Convai.Modules.Gaze.Components;
using Convai.Runtime.Components;
using Newtonsoft.Json.Linq;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     Contract and behaviour coverage for the Convai Body Language MCP tools.
    /// </summary>
    /// <remarks>
    ///     The load-bearing test here is
    ///     <see cref="DiagnoseReportsExactlyWhatTheSetupServiceReports" />: it is what keeps the
    ///     assistant-facing surface a projection of <c>BodyLanguageSetupService</c> rather than a
    ///     second opinion about the same character. Adding a check to the MCP layer alone fails it.
    ///     <see cref="ToolsNeverCreateOrModifyAnAsset" /> is what keeps the "never authors a
    ///     personality" promise from decaying into a comment.
    /// </remarks>
    public sealed class ConvaiBodyLanguageMcpToolTests
    {
        private static readonly string _tempFolder =
            $"{TestAssetSandbox.Root}/{nameof(ConvaiBodyLanguageMcpToolTests)}";

        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;
        private readonly List<string> _createdAssetPaths = new();

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
        }

        [TearDown]
        public void TearDown()
        {
            if (_createdAssetPaths.Count > 0)
            {
                TestAssetSandbox.Clear(nameof(ConvaiBodyLanguageMcpToolTests));
                _createdAssetPaths.Clear();
                AssetDatabase.Refresh();
            }

            _sceneFixture.End();
        }

        // ------------------------------------------------------------------ contract

        [Test]
        public void CatalogCarriesEveryBodyLanguageTool()
        {
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.ConfigureBodyLanguage"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.DiagnoseBodyLanguage"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.InspectBodyLanguagePersonalities"));
        }

        [Test]
        public void SchemasAreClosed()
        {
            JObject configure = JObject.FromObject(ConvaiBodyLanguageMcpTools.ConfigureSchema());
            Assert.That(configure.Value<bool>("additionalProperties"), Is.False);
            Assert.That(configure["required"]?.Values<string>(),
                Is.EquivalentTo(new[] { "characterInstanceId" }));

            JObject diagnose = JObject.FromObject(ConvaiBodyLanguageMcpTools.DiagnoseSchema());
            Assert.That(diagnose.Value<bool>("additionalProperties"), Is.False);

            JObject personalities =
                JObject.FromObject(ConvaiBodyLanguageMcpTools.InspectPersonalitiesSchema());
            Assert.That(personalities.Value<bool>("additionalProperties"), Is.False);
        }

        [Test]
        public void TheTuningFieldDeclaresNoDefaultSoOmittingItLeavesThePersonalityAlone()
        {
            JToken properties = JObject.FromObject(ConvaiBodyLanguageMcpTools.ConfigureSchema())["properties"];

            Assert.That(properties?["personalityAssetPath"], Is.Not.Null);
            Assert.That(properties["personalityAssetPath"]["default"], Is.Null,
                "personalityAssetPath declares a default, which tells an assistant that omitting it " +
                "asks for that value. Omitting it must leave the character's own personality alone.");
        }

        [Test]
        public void EveryResponseCarriesTheStandardEnvelope()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Envelope");

            foreach (object response in new[]
                     {
                         ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiBodyLanguageMcpTools.InspectPersonalities(
                             new InspectBodyLanguagePersonalitiesRequest())
                     })
            {
                JObject json = Json(response);
                Assert.That(json["success"], Is.Not.Null);
                Assert.That(json["message"], Is.Not.Null);
                Assert.That(json["data"], Is.Not.Null);
            }
        }

        // ------------------------------------------------------------------ configure

        [Test]
        public void ConfigurePreviewChangesNothing()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Preview");

            JObject response = Json(ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(response["data"]?["changes"]?.Values<string>(), Is.Not.Empty);
            Assert.That(character.GetComponent<ConvaiBodyLanguageController>(), Is.Null,
                "A preview added the Body Language component.");
        }

        [Test]
        public void ConfigureApplyAddsBodyLanguageAndIsUndoneInOneStep()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Applied");

            JObject response = Json(ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(character.GetComponent<ConvaiBodyLanguageController>(), Is.Not.Null);
            Assert.That(response["data"]?.Value<bool>("complete"), Is.True);

            Undo.PerformUndo();
            Assert.That(character.GetComponent<ConvaiBodyLanguageController>(), Is.Null,
                "The configure step did not collapse into a single undo group.");
        }

        [Test]
        public void ConfigureOnARigThatCannotHostTheModuleReportsAndChangesNothing()
        {
            // No Animator at all — the module would go inert with one logged error.
            ConvaiCharacter character = CreateCharacter("NoRig");

            JObject response = Json(ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(character.GetComponent<ConvaiBodyLanguageController>(), Is.Null,
                "A character that cannot host the module was given an inert component anyway.");
            Assert.That(response["data"]?.Value<string>("advice"), Does.Contain("Humanoid"));
            Assert.That(response["data"]?["nextSteps"]?.Values<string>(), Is.Not.Empty);
        }

        [Test]
        public void ConfigureRejectsAPersonalityPathThatDoesNotExistAndNamesTheMenuPath()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("BadPath");

            JObject response = Json(ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PersonalityAssetPath = "Assets/Nowhere/Missing.asset",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response.Value<string>("message"), Does.Contain("Assets → Create"));
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("INVALID_PERSONALITY"));
            Assert.That(character.GetComponent<ConvaiBodyLanguageController>(), Is.Null);
        }

        [Test]
        public void ConfigureRefusesToGuessBetweenANamedPersonalityAndTheDefaultOne()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Ambiguous");
            ConvaiBodyLanguageProfile personality = CreatePersonalityAsset("Both", _ => { });

            JObject response = Json(ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PersonalityAssetPath = AssetDatabase.GetAssetPath(personality),
                AssignDefaultPersonality = true,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("PERSONALITY_AMBIGUOUS"));
            Assert.That(character.GetComponent<ConvaiBodyLanguageController>(), Is.Null);
        }

        [Test]
        public void ConfigureAssignsAnExistingPersonalityAndSaysSo()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Personality");
            ConvaiBodyLanguageProfile personality = CreatePersonalityAsset("Calm", _ => { });

            JObject response = Json(ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PersonalityAssetPath = AssetDatabase.GetAssetPath(personality),
                DryRun = false
            }));

            var controller = character.GetComponent<ConvaiBodyLanguageController>();
            Assert.That(controller, Is.Not.Null);
            Assert.That(BodyLanguageSetupService.ResolveAssignedProfile(controller), Is.EqualTo(personality));
            Assert.That(response["data"]?["personality"]?.Value<string>("profileName"), Is.EqualTo("Calm"));
            Assert.That(response["data"]?["personality"]?.Value<bool>("usingSdkDefaults"), Is.False);
        }

        [Test]
        public void ToolsNeverCreateOrModifyAnAsset()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("NoAssets");
            ConvaiBodyLanguageProfile personality = CreatePersonalityAsset("Untouched",
                profile => SetSwitch(profile, "enableAmbientSway", false));
            string personalityPath = AssetDatabase.GetAssetPath(personality);

            string[] before = AssetDatabase.FindAssets("t:ConvaiBodyLanguageProfile")
                .OrderBy(guid => guid).ToArray();

            ConvaiBodyLanguageMcpTools.Configure(new ConfigureBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PersonalityAssetPath = personalityPath,
                DryRun = false
            });
            ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            });
            ConvaiBodyLanguageMcpTools.InspectPersonalities(new InspectBodyLanguagePersonalitiesRequest());

            string[] after = AssetDatabase.FindAssets("t:ConvaiBodyLanguageProfile")
                .OrderBy(guid => guid).ToArray();
            Assert.That(after, Is.EqualTo(before),
                "A Body Language tool created or destroyed an asset. These tools assign a " +
                "personality; they never author one.");
            Assert.That(
                AssetDatabase.LoadAssetAtPath<ConvaiBodyLanguageProfile>(personalityPath).EnableAmbientSway,
                Is.False,
                "A Body Language tool wrote to a personality asset.");
        }

        // ------------------------------------------------------------------ diagnose

        [Test]
        public void DiagnoseOnACharacterWithoutBodyLanguageSaysWhatToDoNext()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Bare");

            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("present"), Is.False);
            Assert.That(response["data"]?.Value<string>("readiness"), Is.EqualTo("NotInstalled"));

            string[] steps = response["data"]?["nextSteps"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            Assert.That(steps, Is.Not.Empty);
            Assert.That(string.Join(" ", steps), Does.Contain("Add Component → Convai → Embodiment → Body Language"),
                "A beginner is told to add the component without being told where it lives.");
        }

        [Test]
        public void DiagnoseWithNoCharacterInTheSceneReportsRatherThanThrows()
        {
            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest()));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"),
                Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
        }

        [Test]
        public void DiagnoseIsReadOnly()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("ReadOnly");
            var controller = Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);
            ConvaiBodyLanguageProfile before = BodyLanguageSetupService.ResolveAssignedProfile(controller);
            int componentsBefore = character.GetComponents<Component>().Length;
            bool dirtyBefore = TestScene.isDirty;

            ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            });

            Assert.That(BodyLanguageSetupService.ResolveAssignedProfile(controller), Is.EqualTo(before));
            Assert.That(character.GetComponents<Component>().Length, Is.EqualTo(componentsBefore));
            Assert.That(TestScene.isDirty, Is.EqualTo(dirtyBefore), "Diagnose dirtied the scene.");
        }

        [Test]
        public void DiagnoseReportsExactlyWhatTheSetupServiceReports()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Projection");
            var controller = Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);

            BodyLanguagePreflight preflight = BodyLanguageSetupService.Inspect(controller);
            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            JToken[] reported = response["data"]?["checks"]?.ToArray() ?? Array.Empty<JToken>();
            Assert.That(reported.Length, Is.EqualTo(preflight.Checks.Count),
                "The diagnose tool reports a different number of rows than the setup service produces.");

            for (int i = 0; i < preflight.Checks.Count; i++)
            {
                Assert.That(reported[i].Value<string>("label"), Is.EqualTo(preflight.Checks[i].Label));
                Assert.That(reported[i].Value<string>("detail"), Is.EqualTo(preflight.Checks[i].Detail));
                Assert.That(reported[i].Value<string>("state"),
                    Is.EqualTo(preflight.Checks[i].State.ToString()));
            }
        }

        [Test]
        public void DiagnoseOnAnUnusableRigNamesTheBlockerAndTheFix()
        {
            ConvaiCharacter character = CreateCharacter("NoAnimator");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);

            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response["data"]?.Value<string>("readiness"), Is.EqualTo("Blocked"));
            Assert.That(response["data"]?.Value<bool>("isWorking"), Is.False);

            string reasons = string.Join(" ",
                response["data"]?["whyItMightNotMove"]?.Values<string>() ?? Array.Empty<string>());
            Assert.That(reasons, Does.Contain("Humanoid"),
                "The blocker states the fault without naming the rig setting that fixes it.");
        }

        [Test]
        public void DiagnoseNamesTheMasterSwitchThatStopsTheCharacterSwaying()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("NoSway");
            var controller = Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);
            AssignPersonality(controller,
                CreatePersonalityAsset("Still", profile => SetSwitch(profile, "enableAmbientSway", false)));

            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            string reasons = string.Join(" ",
                response["data"]?["whyItMightNotMove"]?.Values<string>() ?? Array.Empty<string>());
            Assert.That(reasons, Does.Contain("Sway On The Spot"),
                "The answer to \"why isn't it swaying?\" does not name the switch, by the label the " +
                "personality inspector shows.");
            Assert.That(reasons, Does.Contain("never sway"));
        }

        [Test]
        public void DiagnoseNamesSubtleExpressivenessAsAReasonMotionLooksSmall()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Subtle");
            var controller = Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);
            AssignPersonality(controller, CreatePersonalityAsset("Reserved", profile =>
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("expressivenessPreset").enumValueIndex =
                    (int)ExpressivenessPreset.Subtle;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }));

            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            string reasons = string.Join(" ",
                response["data"]?["whyItMightNotMove"]?.Values<string>() ?? Array.Empty<string>());
            Assert.That(reasons, Does.Contain("Subtle"));
        }

        [Test]
        public void DiagnoseAnswersOnACharacterWithNoPersonalityAssigned()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Defaults");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);

            JObject response = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            JToken personality = response["data"]?["personality"];
            Assert.That(personality?.Value<bool>("usingSdkDefaults"), Is.True);
            Assert.That(personality?["expressiveness"]?.Value<string>("preset"), Is.Not.Empty,
                "A character with no personality cannot be asked what its expressiveness is, so the " +
                "one configuration a first-time user is most likely to be in is unanswerable.");
            Assert.That(personality?["settings"]?.Children().Count(), Is.GreaterThan(0));
            Assert.That(response["data"]?.Value<bool>("isWorking"), Is.True,
                "A character with no personality is working, not unfinished.");
        }

        [Test]
        public void DiagnoseSaysBodyLanguageMovesTheHeadItselfWhenGazeIsAbsent()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("NoGaze");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);

            JToken coordination = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }))["data"]?["coordination"];

            Assert.That(coordination?.Value<bool>("gazePresent"), Is.False);
            Assert.That(coordination?.Value<string>("headGestures"),
                Does.Contain("Body Language moves the head and neck itself"));
            Assert.That(coordination?.Value<string>("gestureSuppression"),
                Does.Contain("always reads None"),
                "Without Body Animation nothing can duck this character, and a user cannot be " +
                "expected to know that.");
        }

        [Test]
        public void DiagnoseSaysGazeComposesTheHeadGesturesWhenGazeIsPresent()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("WithGaze");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JToken coordination = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }))["data"]?["coordination"];

            Assert.That(coordination?.Value<bool>("gazePresent"), Is.True);
            Assert.That(coordination?.Value<string>("headGestures"), Does.Contain("Gaze"));
            Assert.That(coordination?.Value<string>("summary"), Does.Contain("Gaze"));
        }

        [Test]
        public void CoordinationSpeaksTheDocumentationsVocabularyAndNotTheInternalOne()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Vocabulary");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);

            string coordination = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
                { CharacterInstanceId = Id(character.gameObject) }))["data"]?["coordination"]?.ToString();

            foreach (string jargon in new[]
                     { "compositor", "arbiter", "arbitration", "solver", "director", "write guard" })
            {
                Assert.That(coordination, Does.Not.Contain(jargon).IgnoreCase,
                    $"The coordination block leaks the internal term '{jargon}' to a user.");
            }
        }

        [Test]
        public void SuggestedRepairArgumentsAddressTheCharacterNotTheComponent()
        {
            ConvaiCharacter character = CreateCharacter("Suggestion");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);
            long characterId = Id(character.gameObject);

            JToken[] issues = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
                { CharacterInstanceId = characterId }))["data"]?["issues"]?.ToArray() ?? Array.Empty<JToken>();

            Assert.That(issues, Is.Not.Empty, "A character with no Animator reported no issue at all.");
            foreach (JToken issue in issues)
            {
                Assert.That(issue["suggestedArguments"]?.Value<long>("characterInstanceId"),
                    Is.EqualTo(characterId),
                    "An assistant following this suggestion would pass the component id to a tool " +
                    "that takes a character id, and get INVALID_CHARACTER.");
            }
        }

        // ------------------------------------------------------------------ personalities

        [Test]
        public void PersonalitiesListsTheProjectsProfilesAndWhoUsesThem()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("User");
            var controller = Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);
            ConvaiBodyLanguageProfile personality = CreatePersonalityAsset("Listed",
                profile => SetSwitch(profile, "enableHandMicro", false));
            AssignPersonality(controller, personality);

            JObject response = Json(ConvaiBodyLanguageMcpTools.InspectPersonalities(
                new InspectBodyLanguagePersonalitiesRequest { FolderPaths = new[] { _tempFolder } }));

            JToken listed = response["data"]?["personalities"]?
                .FirstOrDefault(entry => entry.Value<string>("name") == "Listed");

            Assert.That(listed, Is.Not.Null);
            Assert.That(listed.Value<int>("usedByCharacterCount"), Is.EqualTo(1));
            Assert.That(listed["usedByCharacters"]?.Values<string>(), Does.Contain("User"));
            Assert.That(listed.Value<string>("headline"), Does.Contain("Move Hands While Idle"));
        }

        [Test]
        public void PersonalitiesNamesTheMenuPathWhenTheProjectHasNone()
        {
            JObject response = Json(ConvaiBodyLanguageMcpTools.InspectPersonalities(
                new InspectBodyLanguagePersonalitiesRequest { FolderPaths = new[] { _tempFolder + "Empty" } }));

            Assert.That(response["data"]?.Value<int>("count"), Is.EqualTo(0));
            Assert.That(response["data"]?.Value<string>("createProfileMenuPath"),
                Does.Contain("Assets → Create"));
        }

        // ------------------------------------------------------------------ integration

        [Test]
        public void SceneSurveyAgreesWithDiagnose()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Survey");
            Undo.AddComponent<ConvaiBodyLanguageController>(character.gameObject);

            JObject diagnose = Json(ConvaiBodyLanguageMcpTools.Diagnose(new DiagnoseBodyLanguageRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            ConvaiModuleSurveyResult survey = ConvaiModuleSurveyRegistry.All
                .Single(surveyor => surveyor.ModuleId == "convai.body-language")
                .Survey(character.gameObject);

            Assert.That(survey.IsPresent, Is.EqualTo(diagnose["data"]?.Value<bool>("present")));
            Assert.That(survey.IsFunctional, Is.EqualTo(diagnose["data"]?.Value<bool>("isWorking")));
            Assert.That(survey.Summary, Is.EqualTo(diagnose["data"]?.Value<string>("summary")));
        }

        [Test]
        public void GuidanceForTheModuleNamesAllThreeToolsAndItsDocumentation()
        {
            JObject response = Json(ConvaiMcpTools.GetGuidance(new ConvaiGuidanceRequest
            {
                Topic = ConvaiGuidanceTopic.BodyLanguage
            }));

            string[] tools = response["data"]?["ConvaiTools"]?.Values<string>().ToArray()
                             ?? Array.Empty<string>();
            Assert.That(tools, Does.Contain("Convai.ConfigureBodyLanguage"));
            Assert.That(tools, Does.Contain("Convai.DiagnoseBodyLanguage"));
            Assert.That(tools, Does.Contain("Convai.InspectBodyLanguagePersonalities"));
            Assert.That(string.Join(" ", response["data"]?["Documentation"]?.Values<string>()
                                         ?? Array.Empty<string>()),
                Does.Contain("BODY-LANGUAGE.md"));
        }

        // ------------------------------------------------------------------ helpers

        private ConvaiCharacter CreateCharacter(string name)
        {
            var host = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(host, "test");
            SceneManager.MoveGameObjectToScene(host, TestScene);
            return Undo.AddComponent<ConvaiCharacter>(host);
        }

        private ConvaiCharacter CreateHumanoidCharacter(string name)
        {
            ConvaiCharacter character = CreateCharacter(name);
            Avatar avatar = HumanoidRigFixture.BuildAvatar(character.gameObject, TestScene);
            Assert.That(avatar, Is.Not.Null, "This editor refused to build the test Humanoid avatar.");

            Animator animator = Undo.AddComponent<Animator>(character.gameObject);
            animator.avatar = avatar;
            return character;
        }

        private ConvaiBodyLanguageProfile CreatePersonalityAsset(
            string name, Action<ConvaiBodyLanguageProfile> configure)
        {
            TestAssetSandbox.Ensure(nameof(ConvaiBodyLanguageMcpToolTests));

            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();

            // CreateDefault marks the instance HideAndDontSave, because its first purpose is the
            // in-memory fallback a character with no personality reads. An object carrying that
            // flag cannot be written to disk: AssetDatabase.CreateAsset leaves a file that loads
            // back empty, so every assertion downstream reads a nameless profile with no switches
            // and blames the tool. The SDK's own authoring path clears it for exactly this reason —
            // see ConvaiCopyOnWrite.CreateAndAssign.
            profile.hideFlags = HideFlags.None;

            string path = $"{_tempFolder}/{name}.asset";
            AssetDatabase.CreateAsset(profile, path);
            _createdAssetPaths.Add(path);

            configure(profile);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<ConvaiBodyLanguageProfile>(path);
        }

        private static void AssignPersonality(
            ConvaiBodyLanguageController controller, ConvaiBodyLanguageProfile personality)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("profile").objectReferenceValue = personality;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetSwitch(ConvaiBodyLanguageProfile profile, string field, bool value)
        {
            var serialized = new SerializedObject(profile);
            SerializedProperty property = serialized.FindProperty(field);
            Assert.That(property, Is.Not.Null, $"ConvaiBodyLanguageProfile no longer serialises {field}.");
            property.boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static long Id(GameObject value) => ConvaiMcpEntityRef.ToToolId(value);

        private static JObject Json(object response) => JObject.FromObject(response);
    }
}
