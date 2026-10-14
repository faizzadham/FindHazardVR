using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Runtime.Components;
using Convai.Shared.Compatibility;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Editor.AI
{
    /// <summary>
    ///     Authoring for a room that holds more than one character: which characters join it, and how
    ///     the SDK decides which of them the player is talking to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="ConvaiConversationAuthoringService" /> because it answers a
    ///         different question. That one builds a working conversation; this one is only ever
    ///         reached once a scene has a second character, and every field it touches is inert until
    ///         then. Folding these onto <c>Convai.ConfigureRoom</c> would have put eleven settings
    ///         that do nothing in front of every first-time setup.
    ///     </para>
    ///     <para>
    ///         Every field is optional and omitting one leaves it exactly as the project authored it,
    ///         which is what makes this usable for tuning rather than only for creation. The two
    ///         object references are cleared by an explicit flag rather than by a sentinel id, because
    ///         "leave it alone" and "empty it" are different intentions and a magic number makes them
    ///         one field.
    ///     </para>
    /// </remarks>
    internal static class ConvaiMultiCharacterAuthoringService
    {
        internal static ConvaiAuthoringResult Configure(ConvaiConfigureConversationTargetingRequest request)
        {
            request ??= new ConvaiConfigureConversationTargetingRequest();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return ConvaiAuthoringResult.Failure(
                    "PLAY_MODE_ACTIVE",
                    "Scene authoring is not available in Play Mode. Exit Play Mode and try again.",
                    request.DryRun);

            if (!TryResolveManager(request.ManagerInstanceId, out ConvaiManager manager, out string error))
                return ConvaiAuthoringResult.Failure("MANAGER_NOT_FOUND", error, request.DryRun);

            var result = new ConvaiAuthoringResult
            {
                DryRun = request.DryRun,
                ManagerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject)
            };

            var serialized = new SerializedObject(manager);
            if (!TryPlan(serialized, request, result, out List<Action> writes))
                return result;

            if (!request.DryRun && result.Changes.Count > 0)
            {
                int group = Undo.GetCurrentGroup();
                try
                {
                    Undo.RecordObject(manager, "Configure Convai Conversation Targeting");
                    foreach (Action write in writes) write();
                    serialized.ApplyModifiedProperties();
                    EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                    Undo.SetCurrentGroupName("Configure Convai Conversation Targeting");
                    Undo.CollapseUndoOperations(group);
                }
                catch (Exception exception)
                {
                    Undo.RevertAllDownToGroup(group);
                    return ConvaiAuthoringResult.Failure("MUTATION_FAILED", exception.Message, false);
                }
            }

            AddContextWarnings(manager, result);
            result.Complete = result.FailureCode.Length == 0;
            return result;
        }

        /// <summary>
        ///     Works out every write and every refusal without performing any of them.
        /// </summary>
        /// <remarks>
        ///     Planned first and applied second so that a request with one bad value changes nothing
        ///     at all. Half-applying a tuning pass leaves a scene in a state neither the caller nor
        ///     the project author asked for, and nothing in the response would say which half landed.
        /// </remarks>
        private static bool TryPlan(
            SerializedObject serialized,
            ConvaiConfigureConversationTargetingRequest request,
            ConvaiAuthoringResult result,
            out List<Action> writes)
        {
            writes = new List<Action>();

            if (request.TargetingMode.HasValue)
                PlanEnum(
                    serialized.FindProperty("_conversationTargeting._mode"),
                    (int)request.TargetingMode.Value,
                    "Chosen By",
                    request.TargetingMode.Value.ToString(),
                    result,
                    writes);

            if (request.MaxDistance.HasValue)
            {
                if (request.MaxDistance.Value < 0f)
                    return Refuse(result, "INVALID_MAX_DISTANCE", "Range must be zero or more metres.");
                PlanFloat(
                    serialized.FindProperty("_conversationTargeting._maxDistance"),
                    request.MaxDistance.Value,
                    "Range",
                    "m",
                    result,
                    writes);
            }

            if (request.MaxAngle.HasValue)
            {
                if (request.MaxAngle.Value < 1f || request.MaxAngle.Value > 180f)
                    return Refuse(result, "INVALID_MAX_ANGLE", "Look Angle must be between 1 and 180 degrees.");
                PlanFloat(
                    serialized.FindProperty("_conversationTargeting._maxAngle"),
                    request.MaxAngle.Value,
                    "Look Angle",
                    "°",
                    result,
                    writes);
            }

            if (request.SwitchMargin.HasValue)
            {
                if (request.SwitchMargin.Value < 0f || request.SwitchMargin.Value > 90f)
                    return Refuse(result, "INVALID_SWITCH_MARGIN", "Switch Margin must be between 0 and 90 degrees.");
                PlanFloat(
                    serialized.FindProperty("_conversationTargeting._switchMargin"),
                    request.SwitchMargin.Value,
                    "Switch Margin",
                    "°",
                    result,
                    writes);
            }

            if (request.SwitchDelaySeconds.HasValue)
            {
                if (request.SwitchDelaySeconds.Value < 0f)
                    return Refuse(result, "INVALID_SWITCH_DELAY", "Switch Delay must be zero or more seconds.");
                PlanFloat(
                    serialized.FindProperty("_conversationTargeting._switchDelaySeconds"),
                    request.SwitchDelaySeconds.Value,
                    "Switch Delay",
                    "s",
                    result,
                    writes);
            }

            if (request.ClearViewCamera == true)
                PlanReference(
                    serialized.FindProperty("_conversationViewCamera"),
                    null,
                    "Player Camera",
                    "the main camera",
                    result,
                    writes);
            else if (request.ViewCameraInstanceId.HasValue)
            {
                if (!TryResolveComponent(request.ViewCameraInstanceId.Value, out Camera camera, out string cameraError))
                    return Refuse(result, "VIEW_CAMERA_NOT_FOUND", cameraError);
                PlanReference(
                    serialized.FindProperty("_conversationViewCamera"),
                    camera,
                    "Player Camera",
                    camera.gameObject.name,
                    result,
                    writes);
            }

            if (request.ClearInitialCharacter == true)
                PlanReference(
                    serialized.FindProperty("_explicitConversationTarget"),
                    null,
                    "Initial Character",
                    "whichever character the player is looking at",
                    result,
                    writes);
            else if (request.InitialCharacterInstanceId.HasValue)
            {
                if (!TryResolveComponent(
                        request.InitialCharacterInstanceId.Value,
                        out ConvaiCharacter initial,
                        out string initialError))
                    return Refuse(result, "INITIAL_CHARACTER_NOT_FOUND", initialError);
                PlanReference(
                    serialized.FindProperty("_explicitConversationTarget"),
                    initial,
                    "Initial Character",
                    initial.gameObject.name,
                    result,
                    writes);
            }

            return PlanRoomSelection(serialized, request, result, writes);
        }

        /// <summary>
        ///     Plans the room selection — which characters join the next connection.
        /// </summary>
        /// <remarks>
        ///     The selection is an exact set, not an addition, because that is what the component
        ///     stores and what the Inspector shows. A caller that means "and also this one" has to
        ///     read the current set first, and the diagnosis reports it for exactly that reason.
        /// </remarks>
        private static bool PlanRoomSelection(
            SerializedObject serialized,
            ConvaiConfigureConversationTargetingRequest request,
            ConvaiAuthoringResult result,
            List<Action> writes)
        {
            SerializedProperty usesSelection = serialized.FindProperty("_useCharacterConnectionSelection");
            SerializedProperty included = serialized.FindProperty("_includedCharacters");

            if (request.IncludeAllCharacters == true)
            {
                if (request.IncludedCharacterInstanceIds is { Length: > 0 })
                    return Refuse(
                        result,
                        "CONFLICTING_ROOM_SELECTION",
                        "includeAllCharacters and includedCharacterInstanceIds ask for different rooms. " +
                        "Send one of them.");

                if (usesSelection is { boolValue: true })
                {
                    result.Changes.Add("Characters Joining the Room: every active character (was an explicit selection)");
                    writes.Add(() =>
                    {
                        usesSelection.boolValue = false;
                        included?.ClearArray();
                    });
                }

                return true;
            }

            if (request.IncludedCharacterInstanceIds == null) return true;

            if (request.IncludedCharacterInstanceIds.Length == 0)
                return Refuse(
                    result,
                    "EMPTY_ROOM_SELECTION",
                    "A room needs at least one character. Send includeAllCharacters to go back to the default.");

            var characters = new List<ConvaiCharacter>(request.IncludedCharacterInstanceIds.Length);
            foreach (long instanceId in request.IncludedCharacterInstanceIds)
            {
                if (!TryResolveComponent(instanceId, out ConvaiCharacter character, out string characterError))
                    return Refuse(result, "SELECTED_CHARACTER_NOT_FOUND", characterError);
                if (!characters.Contains(character)) characters.Add(character);
            }

            result.Changes.Add(
                "Characters Joining the Room: " + string.Join(", ", characters.Select(character => character.gameObject.name)));
            writes.Add(() =>
            {
                usesSelection.boolValue = true;
                included.ClearArray();
                for (int i = 0; i < characters.Count; i++)
                {
                    included.InsertArrayElementAtIndex(i);
                    included.GetArrayElementAtIndex(i).objectReferenceValue = characters[i];
                }
            });

            return true;
        }

        /// <summary>
        ///     Says what this scene will actually do with the settings it now has.
        /// </summary>
        /// <remarks>
        ///     These are warnings rather than failures on purpose. Tuning targeting in a scene whose
        ///     second character has not been built yet is ordinary authoring order, and refusing it
        ///     would make the tool useless for the way scenes are actually assembled — but a caller
        ///     that thinks it just changed behaviour deserves to be told it has not, yet.
        /// </remarks>
        private static void AddContextWarnings(ConvaiManager manager, ConvaiAuthoringResult result)
        {
            ConvaiCharacter[] characters = ConvaiObjectFind.All<ConvaiCharacter>(true)
                .Where(character => character.gameObject.scene.IsValid())
                .ToArray();

            if (characters.Length < 2)
                result.Warnings.Add(
                    $"The loaded scenes hold {characters.Length} Convai Character(s). Conversation " +
                    "targeting only runs once a room can hold more than one, so these settings are " +
                    "stored but inert until a second character joins.");

            if (manager != null && manager.UsesCharacterConnectionSelection)
                result.Warnings.Add(
                    "This manager uses an explicit room selection, so a character added to the scene " +
                    "later stays out of the room until it is included as well.");

            result.Warnings.Add(
                "A room holding several characters is a Convai account feature. If the account this " +
                "API key belongs to does not have it, the room refuses to connect and the Console " +
                "names the reason.");
        }

        private static bool Refuse(ConvaiAuthoringResult result, string code, string message)
        {
            result.Success = false;
            result.FailureCode = code;
            result.FailureMessage = message;
            result.Changes.Clear();
            return false;
        }

        private static void PlanEnum(
            SerializedProperty property,
            int value,
            string label,
            string display,
            ConvaiAuthoringResult result,
            List<Action> writes)
        {
            if (property == null || property.enumValueIndex == value) return;
            result.Changes.Add($"{label}: {display}");
            writes.Add(() => property.enumValueIndex = value);
        }

        private static void PlanFloat(
            SerializedProperty property,
            float value,
            string label,
            string unit,
            ConvaiAuthoringResult result,
            List<Action> writes)
        {
            if (property == null || Mathf.Approximately(property.floatValue, value)) return;
            result.Changes.Add($"{label}: {value}{unit}");
            writes.Add(() => property.floatValue = value);
        }

        private static void PlanReference(
            SerializedProperty property,
            Object value,
            string label,
            string display,
            ConvaiAuthoringResult result,
            List<Action> writes)
        {
            if (property == null || property.objectReferenceValue == value) return;
            result.Changes.Add($"{label}: {display}");
            writes.Add(() => property.objectReferenceValue = value);
        }

        private static bool TryResolveManager(long instanceId, out ConvaiManager manager, out string error)
        {
            manager = null;
            error = string.Empty;

            if (instanceId != 0)
            {
                if (!TryResolveGameObject(instanceId, out GameObject target, out error))
                    return false;

                manager = target.GetComponent<ConvaiManager>();
                if (manager == null)
                {
                    error = $"'{target.name}' has no ConvaiManager. Run Convai.InspectScene for the right ID.";
                    return false;
                }

                return true;
            }

            ConvaiManager[] managers = ConvaiObjectFind.All<ConvaiManager>(true)
                .Where(candidate => candidate.gameObject.scene.IsValid())
                .ToArray();

            switch (managers.Length)
            {
                case 1:
                    manager = managers[0];
                    return true;
                case 0:
                    error = "The loaded scenes have no ConvaiManager. Run Convai.BootstrapScene first.";
                    return false;
                default:
                    error =
                        $"The loaded scenes have {managers.Length} ConvaiManager components. " +
                        "Send managerInstanceId to say which one.";
                    return false;
            }
        }

        private static bool TryResolveComponent<T>(long instanceId, out T component, out string error)
            where T : Component
        {
            component = null;
            if (!TryResolveGameObject(instanceId, out GameObject target, out error))
                return false;

            component = target.GetComponent<T>();
            if (component != null) return true;

            error = $"'{target.name}' has no {typeof(T).Name}. Run Convai.InspectScene for the right ID.";
            return false;
        }

        /// <summary>
        ///     Resolves one instance ID to a GameObject in any loaded scene.
        /// </summary>
        /// <remarks>
        ///     Any loaded scene, not only the active one. A room holds every active character in the
        ///     loaded scenes, so a roster assembled additively — the manager in one scene, characters
        ///     in another — is an ordinary multi-character setup rather than a mistake to refuse.
        /// </remarks>
        private static bool TryResolveGameObject(long instanceId, out GameObject gameObject, out string error)
        {
            gameObject = null;
            error = null;

            if (instanceId == 0)
            {
                error = "A non-zero instance ID is required.";
                return false;
            }

            ConvaiMcpEntityRef.TryResolve(instanceId, out gameObject);
            if (gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded)
                return true;

            gameObject = null;
            error =
                $"Instance ID {instanceId} does not name a GameObject in a loaded scene. " +
                "Run Convai.InspectScene for current IDs.";
            return false;
        }
    }
}
