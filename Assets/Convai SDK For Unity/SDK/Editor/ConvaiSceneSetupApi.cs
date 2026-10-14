using System.Collections.Generic;
using Convai.Editor.Diagnostics;
using Convai.Editor.Utilities;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Compatibility;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using UnityEditor;
using UnityEngine;

namespace Convai.Editor
{
    /// <summary>
    ///     Structured scene setup and validation for editor automation (MCP, tests) without modal dialogs.
    /// </summary>
    public static class ConvaiSceneSetupApi
    {
        public sealed class ValidationReport
        {
            public List<string> Errors { get; } = new();
            public List<string> Warnings { get; } = new();
            public List<string> NextSteps { get; } = new();
            public bool IsSuccess => Errors.Count == 0;
        }

        public sealed class BootstrapResult
        {
            public bool AddedManager;
            public bool AddedRoomManager;
            public string ManagerObjectName;
            public List<string> ActionsTaken { get; } = new();
        }

        public static ValidationReport ValidateCurrentScene()
        {
            var report = new ValidationReport();

            ConvaiManager[] managers = ConvaiObjectFind.All<ConvaiManager>(FindObjectsInactive.Include);
            if (managers.Length == 0)
                report.Errors.Add("Missing ConvaiManager");

            ConvaiRoomManager[] roomManagers = ConvaiObjectFind.All<ConvaiRoomManager>(FindObjectsInactive.Include);
            if (roomManagers.Length == 0)
                report.Errors.Add("Missing ConvaiRoomManager");

            ConvaiSettings settings = ConvaiSettings.Instance;
            if (settings == null || !settings.HasApiKey)
                report.Warnings.Add("API key not configured (Edit > Project Settings > Convai SDK)");

            // A project condition rather than a scene one, but it belongs in the same report: it is
            // the difference between Convai's UI rendering and it throwing on scene open, and a
            // first-time user has no way to guess it from the symptom.
            if (!ConvaiTextMeshProEssentials.AreImported)
            {
                report.Errors.Add(
                    "TextMesh Pro Essential Resources are not imported. Convai's UI prefabs and " +
                    "fonts reference them, and a scene containing Convai UI throws on open without them.");
                report.NextSteps.Add(ConvaiTextMeshProEssentials.ImportInstruction);
            }

            ConvaiCharacter[] characters = ConvaiObjectFind.All<ConvaiCharacter>(FindObjectsInactive.Include);
            if (characters.Length == 0)
                report.Errors.Add("No ConvaiCharacter components found in scene");
            else
            {
                foreach (ConvaiCharacter character in characters)
                {
                    if (string.IsNullOrWhiteSpace(character.CharacterId))
                        report.Errors.Add(
                            $"ConvaiCharacter on '{character.gameObject.name}' has no Character ID");
                }
            }

            AddMultiCharacterFindings(report, characters, managers.Length > 0 ? managers[0] : null);

            ConvaiPlayer[] players = ConvaiObjectFind.All<ConvaiPlayer>(FindObjectsInactive.Include);
            if (players.Length == 0)
                report.Errors.Add("No ConvaiPlayer component found in scene");

            foreach (ConvaiRoomManager room in roomManagers)
            {
                if (room.EffectiveConnectionType != ConvaiConnectionType.Video)
                    continue;

                bool hasPublisher = false;
                bool hasFrameSource = false;
                foreach (UnityEngine.Component c in room.GetComponentsInChildren<UnityEngine.Component>(true))
                {
                    if (c is IVisionPublisher)
                        hasPublisher = true;
                    if (c is IVisionFrameSource)
                        hasFrameSource = true;
                }

                if (!hasPublisher || !hasFrameSource)
                    report.Warnings.Add(
                        $"Video mode: ConvaiRoomManager on '{room.gameObject.name}' should have a vision publisher and frame source under its GameObject hierarchy (see Vision module README).");
            }

            if (report.Errors.Count > 0)
            {
                foreach (string issue in report.Errors)
                {
                    if (issue.Contains("ConvaiManager") &&
                        !report.NextSteps.Contains("Run Convai.BootstrapScene or GameObject > Convai > Setup Required Components"))
                        report.NextSteps.Add("Run Convai.BootstrapScene or GameObject > Convai > Setup Required Components");
                    if (issue.Contains("ConvaiCharacter"))
                        report.NextSteps.Add("Add ConvaiCharacter to NPC objects and set Character ID");
                    if (issue.Contains("ConvaiPlayer"))
                        report.NextSteps.Add("Add ConvaiPlayer to an explicit player GameObject");
                }
            }

            return report;
        }

        /// <summary>
        ///     The two things a scene with more than one character can be wrong about before it ever
        ///     runs.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Validation used to say nothing at all about rooms holding several characters, so
        ///         both of these were first met in Play Mode: one as a room that connects and then
        ///         answers two characters through one of them, the other as a character that is
        ///         enabled on cue and never joins. Neither reads as the thing it is.
        ///     </para>
        ///     <para>
        ///         Both are measurable here and neither guesses. What is deliberately <i>not</i>
        ///         claimed is whether the Convai account has multi-character access — nothing in the
        ///         editor can see that, and a verdict this has not measured would be worse than
        ///         silence.
        ///     </para>
        /// </remarks>
        private static void AddMultiCharacterFindings(
            ValidationReport report,
            IReadOnlyList<ConvaiCharacter> characters,
            ConvaiManager manager)
        {
            if (characters == null || characters.Count < 2) return;

            // Asked of the same rule the Character inspector and Convai.DiagnoseConversation use, so
            // the three cannot disagree about what counts as a duplicate.
            var conflicts = new List<ConvaiCharacterIdConflict>();
            ConvaiCharacterIdConflicts.Collect(characters, conflicts);
            foreach (ConvaiCharacterIdConflict conflict in conflicts)
            {
                var names = new List<string>(conflict.Characters.Count);
                foreach (ConvaiCharacter character in conflict.Characters)
                    names.Add(character.gameObject.name);

                // Two characters "both" share an ID and three or more "all" do. Worth the branch:
                // this line is read by somebody who has just been told their scene is broken, and a
                // sentence that does not parse is one more thing for them to doubt.
                string subject = names.Count == 2
                    ? $"{names[0]} and {names[1]} both use"
                    : $"{string.Join(", ", names.GetRange(0, names.Count - 1))} and " +
                      $"{names[names.Count - 1]} all use";

                report.Errors.Add(
                    $"{subject} Character ID {conflict.CharacterId}. " +
                    "The SDK routes ownership, participants and audio by Character ID, so characters " +
                    "sharing one collide instead of each being answered separately.");
                report.NextSteps.Add(
                    "Give each Convai Character its own Character ID from the Convai dashboard.");
            }

            // Only the characters this manager would actually send. A scene that deliberately left
            // one out of Characters Joining the Room has already answered this question.
            IReadOnlyList<ConvaiCharacter> considered =
                manager != null && manager.UsesCharacterConnectionSelection
                    ? manager.CharactersToConnect
                    : characters;
            if (considered == null || considered.Count < 2) return;

            var inactive = new List<string>();
            int active = 0;
            foreach (ConvaiCharacter character in considered)
            {
                if (character == null) continue;
                if (character.isActiveAndEnabled) active++;
                else inactive.Add(character.gameObject.name);
            }

            if (active != 1 || inactive.Count == 0) return;

            report.Warnings.Add(
                $"Only one of this scene's {considered.Count} characters is active, so the room opens " +
                $"for that character alone and keeps no roster. {string.Join(", ", inactive)} cannot " +
                "join it after it connects — a character enabled during play waits for the next " +
                "connection instead.");
            report.NextSteps.Add(
                "Enable every character before the room connects, or turn off Auto Connect on the " +
                "characters and connect once they are all active.");
        }

        public static BootstrapResult BootstrapScene()
        {
            var result = new BootstrapResult();
            Undo.SetCurrentGroupName("Convai MCP Bootstrap");
            int undoGroup = Undo.GetCurrentGroup();

            ConvaiManager[] managers = ConvaiObjectFind.All<ConvaiManager>(FindObjectsInactive.Include);
            GameObject managerGo;
            if (managers.Length == 0)
            {
                managerGo = new GameObject("[Convai Manager]");
                Undo.RegisterCreatedObjectUndo(managerGo, "Create ConvaiManager");
                Undo.AddComponent<ConvaiManager>(managerGo);
                result.AddedManager = true;
                result.ActionsTaken.Add("Created GameObject with ConvaiManager");
            }
            else
            {
                managerGo = managers[0].gameObject;
            }

            result.ManagerObjectName = managerGo != null ? managerGo.name : null;

            if (managerGo != null && managerGo.GetComponent<ConvaiRoomManager>() == null)
            {
                Undo.AddComponent<ConvaiRoomManager>(managerGo);
                result.AddedRoomManager = true;
                result.ActionsTaken.Add("Added ConvaiRoomManager to manager object");
            }

            Undo.CollapseUndoOperations(undoGroup);
            return result;
        }

        /// <summary>
        ///     Returns the path to the com.convai.convai-sdk-for-unity package root (POSIX, Unity-style).
        /// </summary>
        public static bool TryGetConvaiSdkPackageRoot(out string packageRoot)
        {
            packageRoot = null;
            string[] guids = AssetDatabase.FindAssets("ConvaiSceneSetupApi t:MonoScript");
            if (guids == null || guids.Length == 0)
                return false;

            string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            if (string.IsNullOrEmpty(scriptPath))
                return false;

            // .../Packages/com.convai.convai-sdk-for-unity/SDK/Editor/ConvaiSceneSetupApi.cs
            string editorDir = System.IO.Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(editorDir))
                return false;

            string sdkDir = System.IO.Path.GetDirectoryName(editorDir)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(sdkDir))
                return false;

            packageRoot = System.IO.Path.GetDirectoryName(sdkDir)?.Replace('\\', '/');
            return !string.IsNullOrEmpty(packageRoot);
        }
    }
}
