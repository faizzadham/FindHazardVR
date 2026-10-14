using System;
using System.Collections.Generic;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;
using UnityEditor;
using UnityEngine.UIElements;

namespace Convai.Editor.ConfigurationWindow.Components.Sections
{
    /// <summary>
    ///     AI coding setup inside the Convai configuration window. Reuses the
    ///     standalone setup window's health checks and repair operations.
    /// </summary>
    [UxmlElement]
    public partial class ConvaiAICodingSection : ConvaiBaseSection
    {
        /// <summary>Unique identifier for this section in navigation.</summary>
        public const string SECTION_NAME = "ai-coding";

        private const double RefreshIntervalSeconds = 0.5d;

        private readonly StatusRow _assistantStatus;
        private readonly List<ClientRow> _clientRows = new();
        private readonly Label _repairMessage;
        private readonly StatusRow _skillStatus;
        private readonly StatusRow _toolsStatus;
        private readonly StatusRow _unityStatus;
        private readonly Label _mirrorState;
        private readonly Button _mirrorAction;
        private readonly Button _mirrorRemove;
        private readonly StatusRow _claudeBridgeStatus;
        private readonly StatusRow _codexDesktopStatus;
        private readonly StatusRow _codexPluginStatus;
        private readonly Label _codexMirrorState;
        private readonly Button _codexMirrorAction;
        private readonly Button _codexMirrorRemove;
        private double _nextRefreshAt;

        /// <summary>Creates an AI coding section.</summary>
        public ConvaiAICodingSection()
        {
            AddToClassList("section-card");
            AddToClassList("ai-coding-section");
            Add(ConvaiVisualElementUtility.CreateLabel("section-header", "AI Coding", "header"));

            var intro = new Label(
                "Unity owns MCP client configuration. Convai adds SDK tools, a packaged skill, and optional managed project instructions.");
            intro.AddToClassList("ai-coding-intro");
            Add(intro);

            VisualElement healthCard = CreateCard("Setup Health", out VisualElement healthHeader);
            var refreshButton = new Button(RefreshAll)
            {
                text = "Refresh",
                tooltip = "Re-check setup health, client installations, project skills, and Claude MCP configuration."
            };
            refreshButton.AddToClassList("ai-coding-secondary-button");
            healthHeader.Add(refreshButton);

            _unityStatus = CreateStatusRow("Unity 6000+", null);
            _assistantStatus = CreateStatusRow("Unity AI Assistant", ConvaiAICodingSetupWindow.BeginAssistantRepair);
            _skillStatus = CreateStatusRow("Packaged Convai Skill", ConvaiAICodingSetupWindow.BeginSkillRepair);
            _toolsStatus = CreateStatusRow("Convai MCP Tools", ConvaiAICodingSetupWindow.BeginToolRegistrationRepair);
            healthCard.Add(_unityStatus.Root);
            healthCard.Add(_assistantStatus.Root);
            healthCard.Add(_skillStatus.Root);
            healthCard.Add(_toolsStatus.Root);
            _claudeBridgeStatus = CreateStatusRow("Claude MCP configuration", null);
            _codexDesktopStatus = CreateStatusRow("ChatGPT / Codex app", null);
            _codexPluginStatus = CreateStatusRow("Unity plugin (Codex)", null);
            healthCard.Add(_claudeBridgeStatus.Root);
            healthCard.Add(_codexDesktopStatus.Root);
            healthCard.Add(_codexPluginStatus.Root);

            _repairMessage = new Label();
            _repairMessage.AddToClassList("ai-coding-message");
            healthCard.Add(_repairMessage);

            var setupHelp = new Label(ConvaiAICodingSetupWindow.ClientSetupHelp);
            setupHelp.AddToClassList("helper-text");
            setupHelp.AddToClassList("ai-coding-card-help");
            healthCard.Add(setupHelp);

            var mcpActionRow = new VisualElement();
            mcpActionRow.AddToClassList("ai-coding-action-row");
            var mcpSettingsButton = new Button(ConvaiAICodingSetupWindow.OpenUnityMcpSettings)
            {
                text = "Open Unity MCP Server Settings",
                tooltip = "Open Unity's official MCP server settings. Convai does not duplicate Unity-owned client configuration."
            };
            mcpSettingsButton.AddToClassList("button");
            mcpSettingsButton.AddToClassList("ai-coding-primary-button");
            mcpActionRow.Add(mcpSettingsButton);
            healthCard.Add(mcpActionRow);
            Add(healthCard);
            Add(CreateSkillClientCard(
                ConvaiAgentClient.ClaudeCode, out _mirrorState, out _mirrorAction, out _mirrorRemove));
            Add(CreateSkillClientCard(
                ConvaiAgentClient.Codex, out _codexMirrorState, out _codexMirrorAction, out _codexMirrorRemove));

            VisualElement instructionsCard = CreateCard("Other AI Clients", out _);
            var instructionsHelp = new Label(
                "Install project instructions to help these clients find the packaged Convai skill. Writes only after an Install, Update, or Remove click. Existing non-Convai content stays intact.");
            instructionsHelp.AddToClassList("helper-text");
            instructionsHelp.AddToClassList("ai-coding-card-help");
            instructionsCard.Add(instructionsHelp);

            foreach (ConvaiAgentClient client in Enum.GetValues(typeof(ConvaiAgentClient)))
            {
                if (client is ConvaiAgentClient.ClaudeCode or ConvaiAgentClient.Codex) continue;
                ClientRow row = CreateClientRow(client);
                _clientRows.Add(row);
                instructionsCard.Add(row.Root);
            }

            Add(instructionsCard);
            RegisterCallback<DetachFromPanelEvent>(_ => StopPolling());
            Refresh();
        }

        protected override void OnSectionShown()
        {
            ConvaiAICodingSetupWindow.InvalidateClientStatus();
            Refresh();
            StartPolling();
        }

        protected override void OnSectionHidden() => StopPolling();

        private VisualElement CreateMirrorRow(
            ConvaiAgentClient client, out Label mirrorState, out Button mirrorAction, out Button mirrorRemove)
        {
            var mirrorRow = new VisualElement();
            mirrorRow.AddToClassList("ai-coding-client-row");
            var mirrorName = new Label("Project skill");
            mirrorName.AddToClassList("ai-coding-client-name");
            mirrorRow.Add(mirrorName);

            var mirrorPath = new Label($"{ConvaiSkillMirror.GetSkillsRelativeRoot(client)}/{ConvaiSkillSourceResolver.SkillName}");
            mirrorPath.AddToClassList("ai-coding-client-path");
            mirrorRow.Add(mirrorPath);

            mirrorState = new Label();
            mirrorState.AddToClassList("ai-coding-client-state");
            mirrorState.AddToClassList("ai-coding-client-state--wide");
            mirrorRow.Add(mirrorState);

            var mirrorActions = new VisualElement();
            mirrorActions.AddToClassList("ai-coding-client-actions");
            mirrorAction = new Button(() => RunMirrorAction(client)) { text = "Install" };
            mirrorAction.AddToClassList("ai-coding-secondary-button");
            mirrorActions.Add(mirrorAction);
            mirrorRemove = new Button(() => RunMirrorRemoval(client)) { text = "Remove" };
            mirrorRemove.AddToClassList("ai-coding-secondary-button");
            mirrorActions.Add(mirrorRemove);
            mirrorRow.Add(mirrorActions);
            return mirrorRow;
        }

        private VisualElement CreateSkillClientCard(
            ConvaiAgentClient client, out Label mirrorState, out Button mirrorAction, out Button mirrorRemove)
        {
            VisualElement card = CreateCard(ConvaiAICodingSetupWindow.GetClientLabel(client), out _);
            card.Add(CreateMirrorRow(client, out mirrorState, out mirrorAction, out mirrorRemove));
            ClientRow instructions = CreateClientRow(client, "Project instructions");
            _clientRows.Add(instructions);
            card.Add(instructions.Root);
            return card;
        }

        private static VisualElement CreateCard(string title, out VisualElement header)
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            card.AddToClassList("ai-coding-card");

            header = new VisualElement();
            header.AddToClassList("ai-coding-card-header");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("ai-coding-card-title");
            header.Add(titleLabel);
            card.Add(header);
            return card;
        }

        private static StatusRow CreateStatusRow(string name, Action repairAction)
        {
            var root = new VisualElement();
            root.AddToClassList("ai-coding-status-row");

            var dot = new VisualElement();
            dot.AddToClassList("ai-coding-status-dot");
            root.Add(dot);

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("ai-coding-status-name");
            root.Add(nameLabel);

            var detail = new Label();
            detail.AddToClassList("ai-coding-status-detail");
            root.Add(detail);

            var state = new Label();
            state.AddToClassList("ai-coding-status-state");
            root.Add(state);

            Button fixButton = null;
            if (repairAction != null)
            {
                fixButton = new Button(repairAction) { text = "Fix" };
                fixButton.AddToClassList("ai-coding-secondary-button");
                fixButton.AddToClassList("ai-coding-fix-button");
                root.Add(fixButton);
            }

            return new StatusRow(root, dot, detail, state, fixButton);
        }

        private ClientRow CreateClientRow(ConvaiAgentClient client, string displayName = null)
        {
            var root = new VisualElement();
            root.AddToClassList("ai-coding-client-row");

            var name = new Label(displayName ?? ConvaiAICodingSetupWindow.GetClientLabel(client));
            name.AddToClassList("ai-coding-client-name");
            root.Add(name);

            var path = new Label(ConvaiAgentInstructionsInstaller.GetRelativePath(client));
            path.AddToClassList("ai-coding-client-path");
            root.Add(path);

            var state = new Label();
            state.AddToClassList("ai-coding-client-state");
            root.Add(state);

            var actions = new VisualElement();
            actions.AddToClassList("ai-coding-client-actions");

            var installButton = new Button(() => RunInstructionAction(
                () => ConvaiAgentInstructionsInstaller.UpsertWithResolvedSkill(
                    ConvaiAICodingSetupWindow.GetProjectRoot(),
                    client,
                    ConvaiAICodingSetupWindow.ResolveSkillPointer)))
            {
                text = "Install"
            };
            installButton.AddToClassList("ai-coding-secondary-button");
            actions.Add(installButton);

            var removeButton = new Button(() => RunInstructionAction(
                () => ConvaiAgentInstructionsInstaller.Remove(ConvaiAICodingSetupWindow.GetProjectRoot(), client)))
            {
                text = "Remove"
            };
            removeButton.AddToClassList("ai-coding-secondary-button");
            actions.Add(removeButton);
            root.Add(actions);

            return new ClientRow(client, root, state, installButton, removeButton);
        }

        private void Refresh()
        {
            ConvaiAICodingSetupState state = ConvaiAICodingSetupWindow.GetSetupState();
            SetStatus(_unityStatus, state.UnityReady, state.UnityVersion, false, state.RepairRunning);
            SetStatus(
                _assistantStatus,
                state.AssistantReady,
                state.AssistantVersion ?? "Not installed",
                ConvaiAICodingSetupWindow.CanRepairAssistant(state.AssistantVersion),
                state.RepairRunning);
            SetStatus(
                _skillStatus,
                state.SkillReady,
                state.SkillDetail,
                !state.SkillReady,
                state.RepairRunning);

            string toolsDetail = state.AssistantReady
                ? state.ToolsReady
                    ? $"{state.ToolCount}/{ConvaiAICodingSetupWindow.ExpectedToolCount} registered"
                    : $"{state.ToolCount}/{ConvaiAICodingSetupWindow.ExpectedToolCount}; {state.ToolIssue}"
                : $"{state.ToolCount}/{ConvaiAICodingSetupWindow.ExpectedToolCount}; install Unity AI Assistant first";
            SetStatus(
                _toolsStatus,
                state.ToolsReady,
                toolsDetail,
                ConvaiAICodingSetupWindow.CanRepairToolRegistration(state.AssistantVersion, state.ToolsReady),
                state.RepairRunning);

            RefreshInstallationStatus(_codexDesktopStatus, state.CodexInstallation.Desktop, "ChatGPT / Codex desktop");
            RefreshInstallationStatus(_codexPluginStatus, state.CodexInstallation.Plugin, "Unity workflows for ChatGPT / Codex");
            RefreshClaudeRows(state);
            RefreshMirrorRow(state.CodexMirrorStatus.State, state.CodexMirrorStatus.Detail,
                _codexMirrorState, _codexMirrorAction, _codexMirrorRemove);
            RefreshRepairMessage(state);
            foreach (ClientRow row in _clientRows) RefreshClientRow(row);
        }

        /// <summary>Re-reads project mirrors, Claude MCP configuration, and client installations, which the poll deliberately does not.</summary>
        private void RefreshAll()
        {
            ConvaiAICodingSetupWindow.InvalidateClientStatus();
            ConvaiAICodingSetupWindow.RefreshInstallationProbe();
            Refresh();
        }

        private void RefreshClaudeRows(ConvaiAICodingSetupState state)
        {
            RefreshMirrorRow(state.MirrorState, state.MirrorDetail, _mirrorState, _mirrorAction, _mirrorRemove);

            bool detected = state.BridgeState == ConvaiClaudeBridgeState.Detected;
            SetStatus(_claudeBridgeStatus, detected, "~/.claude.json, .mcp.json", false, false);
            _claudeBridgeStatus.State.text = detected
                ? "Detected"
                : ConvaiAICodingSetupWindow.DescribeBridgeState(state.BridgeState);
            _claudeBridgeStatus.State.tooltip = state.BridgeDetail ?? string.Empty;
            _claudeBridgeStatus.Detail.tooltip = state.BridgeDetail ?? string.Empty;
        }

        private static void RefreshMirrorRow(
            ConvaiSkillMirrorState mirrorState, string mirrorDetail, Label label, Button action, Button remove)
        {
            bool installed = mirrorState != ConvaiSkillMirrorState.NotInstalled;
            bool healthy = mirrorState == ConvaiSkillMirrorState.Installed;
            label.text = ConvaiAICodingSetupWindow.DescribeMirrorState(mirrorState);
            label.tooltip = mirrorDetail ?? string.Empty;
            label.EnableInClassList("ai-coding-client-state--installed", healthy);
            label.EnableInClassList(
                "ai-coding-client-state--error",
                mirrorState is ConvaiSkillMirrorState.LocallyModified or ConvaiSkillMirrorState.Unmanaged);
            action.text = ConvaiAICodingSetupWindow.GetMirrorActionLabel(mirrorState);
            remove.SetEnabled(installed);
        }

        private void RunMirrorAction(ConvaiAgentClient client)
        {
            ConvaiAICodingSetupState state = ConvaiAICodingSetupWindow.GetSetupState();
            ConvaiSkillMirrorState mirrorState = client == ConvaiAgentClient.Codex ? state.CodexMirrorStatus.State : state.MirrorState;
            string detail = client == ConvaiAgentClient.Codex ? state.CodexMirrorStatus.Detail : state.MirrorDetail;
            RunInstructionAction(() => ConvaiAICodingSetupWindow.RunMirrorAction(mirrorState, detail, client));
        }

        private void RunMirrorRemoval(ConvaiAgentClient client)
        {
            ConvaiAICodingSetupState state = ConvaiAICodingSetupWindow.GetSetupState();
            ConvaiSkillMirrorState mirrorState = client == ConvaiAgentClient.Codex ? state.CodexMirrorStatus.State : state.MirrorState;
            string detail = client == ConvaiAgentClient.Codex ? state.CodexMirrorStatus.Detail : state.MirrorDetail;
            RunInstructionAction(() => ConvaiAICodingSetupWindow.RunMirrorRemoval(mirrorState, detail, client));
        }

        private static void RefreshInstallationStatus(StatusRow row, ConvaiInstallationStatus status, string detail)
        {
            SetStatus(row, status.IsReady, detail, false, false);
            row.State.text = status.Label;
            row.State.tooltip = status.Detail ?? string.Empty;
            row.Detail.tooltip = status.Detail ?? string.Empty;
        }

        private static void SetStatus(
            StatusRow row,
            bool ready,
            string detail,
            bool canRepair,
            bool repairRunning)
        {
            row.Dot.EnableInClassList("ai-coding-status-dot--ok", ready);
            row.Dot.EnableInClassList("ai-coding-status-dot--attention", !ready);
            row.Detail.text = detail ?? string.Empty;
            row.State.text = ready ? "Ready" : "Needs attention";
            row.State.EnableInClassList("ai-coding-status-state--ok", ready);
            row.State.EnableInClassList("ai-coding-status-state--attention", !ready);
            if (row.FixButton == null) return;

            row.FixButton.style.display = ready ? DisplayStyle.None : DisplayStyle.Flex;
            row.FixButton.SetEnabled(canRepair && !repairRunning);
        }

        private void RefreshRepairMessage(ConvaiAICodingSetupState state)
        {
            bool visible = !string.IsNullOrWhiteSpace(state.RepairMessage);
            _repairMessage.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;

            _repairMessage.text = state.RepairMessage;
            _repairMessage.EnableInClassList(
                "ai-coding-message--warning",
                state.RepairMessageType == MessageType.Warning);
            _repairMessage.EnableInClassList(
                "ai-coding-message--error",
                state.RepairMessageType == MessageType.Error);
        }

        private static void RefreshClientRow(ClientRow row)
        {
            bool installed;
            try
            {
                installed = ConvaiAgentInstructionsInstaller.ContainsManagedBlock(
                    ConvaiAICodingSetupWindow.GetProjectRoot(),
                    row.Client);
                row.State.tooltip = string.Empty;
                row.InstallButton.SetEnabled(true);
            }
            catch (InvalidOperationException exception)
            {
                installed = false;
                row.State.text = "Unreadable";
                row.State.tooltip = exception.Message;
                row.State.EnableInClassList("ai-coding-client-state--installed", false);
                row.State.EnableInClassList("ai-coding-client-state--error", true);
                row.InstallButton.SetEnabled(false);
                row.RemoveButton.SetEnabled(false);
                return;
            }

            row.State.text = installed ? "Installed" : "Not installed";
            row.State.EnableInClassList("ai-coding-client-state--installed", installed);
            row.State.EnableInClassList("ai-coding-client-state--error", false);
            row.InstallButton.text = installed ? "Update" : "Install";
            row.RemoveButton.SetEnabled(installed);
        }

        private void RunInstructionAction(Action action)
        {
            try
            {
                action();
                ConvaiAICodingSetupWindow.InvalidateClientStatus();
                AssetDatabase.Refresh();
                Refresh();
            }
            catch (Exception exception)
            {
                ConvaiLogger.Error($"AI Coding Setup failed: {exception.Message}", LogCategory.Editor);
                EditorUtility.DisplayDialog("Convai AI Coding Setup", exception.Message, "OK");
            }
        }

        private void StartPolling()
        {
            EditorApplication.update -= PollSetupState;
            _nextRefreshAt = 0d;
            EditorApplication.update += PollSetupState;
        }

        private void StopPolling() => EditorApplication.update -= PollSetupState;

        private void PollSetupState()
        {
            if (!IsSectionVisible || EditorApplication.timeSinceStartup < _nextRefreshAt) return;
            _nextRefreshAt = EditorApplication.timeSinceStartup + RefreshIntervalSeconds;
            Refresh();
        }

        private sealed class StatusRow
        {
            internal StatusRow(
                VisualElement root,
                VisualElement dot,
                Label detail,
                Label state,
                Button fixButton)
            {
                Root = root;
                Dot = dot;
                Detail = detail;
                State = state;
                FixButton = fixButton;
            }

            internal VisualElement Root { get; }
            internal VisualElement Dot { get; }
            internal Label Detail { get; }
            internal Label State { get; }
            internal Button FixButton { get; }
        }

        private sealed class ClientRow
        {
            internal ClientRow(
                ConvaiAgentClient client,
                VisualElement root,
                Label state,
                Button installButton,
                Button removeButton)
            {
                Client = client;
                Root = root;
                State = state;
                InstallButton = installButton;
                RemoveButton = removeButton;
            }

            internal ConvaiAgentClient Client { get; }
            internal VisualElement Root { get; }
            internal Label State { get; }
            internal Button InstallButton { get; }
            internal Button RemoveButton { get; }
        }
    }
}
