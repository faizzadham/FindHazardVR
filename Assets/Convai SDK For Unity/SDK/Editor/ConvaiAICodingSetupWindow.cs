using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Convai.Domain.Logging;
using Convai.Editor.UI;
using Convai.Runtime.Logging;
using UnityEditor;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using CompilationPipeline = UnityEditor.Compilation.CompilationPipeline;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageStatusCode = UnityEditor.PackageManager.StatusCode;

namespace Convai.Editor
{
    /// <summary>Read-only snapshot consumed by every AI coding setup surface.</summary>
    internal readonly struct ConvaiAICodingSetupState
    {
        internal ConvaiAICodingSetupState(
            bool unityReady,
            string unityVersion,
            bool assistantReady,
            string assistantVersion,
            bool skillReady,
            string skillDetail,
            bool toolsReady,
            int toolCount,
            string toolIssue,
            ConvaiSkillMirrorState mirrorState,
            string mirrorDetail,
            ConvaiClaudeBridgeState bridgeState,
            string bridgeDetail,
            bool repairRunning,
            string repairMessage,
            MessageType repairMessageType,
            ConvaiSkillMirrorStatus codexMirrorStatus = default,
            ConvaiCodexInstallationStatus codexInstallation = default)
        {
            UnityReady = unityReady;
            UnityVersion = unityVersion;
            AssistantReady = assistantReady;
            AssistantVersion = assistantVersion;
            SkillReady = skillReady;
            SkillDetail = skillDetail;
            ToolsReady = toolsReady;
            ToolCount = toolCount;
            ToolIssue = toolIssue;
            MirrorState = mirrorState;
            MirrorDetail = mirrorDetail;
            BridgeState = bridgeState;
            BridgeDetail = bridgeDetail;
            RepairRunning = repairRunning;
            RepairMessage = repairMessage;
            RepairMessageType = repairMessageType;
            CodexMirrorStatus = codexMirrorStatus;
            CodexInstallation = codexInstallation;
        }

        internal ConvaiSkillMirrorStatus CodexMirrorStatus { get; }
        internal ConvaiCodexInstallationStatus CodexInstallation { get; }

        internal bool UnityReady { get; }
        internal string UnityVersion { get; }
        internal bool AssistantReady { get; }
        internal string AssistantVersion { get; }
        internal bool SkillReady { get; }

        /// <summary>Where the skill actually resolved, or why it did not.</summary>
        internal string SkillDetail { get; }

        internal bool ToolsReady { get; }
        internal int ToolCount { get; }
        internal string ToolIssue { get; }

        /// <summary>Claude's project copy of the skill. Cached; recomputed on actions, not on repaint.</summary>
        internal ConvaiSkillMirrorState MirrorState { get; }

        internal string MirrorDetail { get; }

        /// <summary>Configuration evidence for the Assistant MCP host, never a connection check.</summary>
        internal ConvaiClaudeBridgeState BridgeState { get; }

        internal string BridgeDetail { get; }

        internal bool RepairRunning { get; }
        internal string RepairMessage { get; }
        internal MessageType RepairMessageType { get; }
    }

    internal sealed class ConvaiAICodingSetupWindow : EditorWindow
    {
        private const string AssistantPackageName = "com.unity.ai.assistant";
        private const string McpSettingsPath = "Project/AI/Unity MCP Server";
        private const string RegistryTypeName = "Unity.AI.MCP.Editor.ToolRegistry.McpToolRegistry";
        private const string BridgeTypeName = "Unity.AI.MCP.Editor.UnityMCPBridge";
        private const string RepairPendingKey = "Convai.AICodingSetup.RepairPending";
        private const string RepairMessageKey = "Convai.AICodingSetup.RepairMessage";
        private const string RepairMessageTypeKey = "Convai.AICodingSetup.RepairMessageType";
        private const string RepairStartedTicksKey = "Convai.AICodingSetup.RepairStartedTicks";
        private const string RepairGoalKey = "Convai.AICodingSetup.RepairGoal";
        private const double RepairTimeoutSeconds = 60d;

        #region Cached content

        private static readonly GUIContent HeroTitleContent = new("AI Coding Setup");

        private static readonly GUIContent HeroSubtitleContent = new(
            "Unity owns MCP client configuration. Convai adds SDK tools, a packaged skill, and optional project instructions.");

        private static readonly GUIContent ReadinessHeaderContent = new("Readiness");
        internal const string ClientSetupHelp =
            "Configure each client in Unity MCP Server settings; choose Codex for ChatGPT / Codex. " +
            "Install its project skill below, and optionally add project instructions. Verify the connection in your client.";
        private static readonly GUIContent ClientSetupHelpContent = new(ClientSetupHelp);
        private static readonly GUIContent ChatGptHeaderContent = new("ChatGPT / Codex");
        private static readonly GUIContent ClaudeHeaderContent = new("Claude Code");
        private static readonly GUIContent RefreshContent = new(
            "Refresh",
            "Re-check setup health, project skill mirrors, and Claude MCP configuration.");
        private static readonly GUIContent InstructionsHeaderContent = new("Other AI Clients");
        private static readonly GUIContent OpenMcpSettingsContent = new("Open Unity MCP Server Settings");
        private static readonly GUIContent RemoveContent = new("Remove");

        private const string RepairStatusTitle = "Setup Status";

        // Reused per draw: their text and tooltip are per-row.
        private static readonly GUIContent ScratchFixButton = new("Fix");
        private static readonly GUIContent ScratchClientAction = new();
        private static readonly GUIContent ScratchMirrorState = new();
        private static readonly GUIContent ScratchBridgeState = new();
        private static readonly GUIContent ScratchStatusDetail = new();

        #endregion

        internal static int ExpectedToolCount => ExpectedToolNames.Length;
        internal const string AssistantPackageIdentifier = "com.unity.ai.assistant@2.14.0-pre.1";
        internal static readonly string[] ExpectedToolNames = LoadExpectedToolNames();
        private static readonly Version MinimumAssistantVersion = new(2, 13, 0);
        private static readonly Version MaximumAssistantVersion = new(3, 0, 0);
        private static AddRequest _assistantInstallRequest;
        private static double _nextRegistryRefreshAt;

        private enum RepairGoal
        {
            FullSetup,
            PackagedSkill,
            ToolRegistration
        }

        private static void OpenWindow() =>
            ConfigurationWindow.ConvaiConfigurationWindowEditor.OpenAICodingWindow();

        private void OnFocus() => InvalidateClientStatus();

        private void OnEnable() => InvalidateClientStatus();

        private void OnInspectorUpdate()
        {
            if (ConvaiCodexInstallationProbe.IsPending) Repaint();
        }

        private void OnGUI()
        {
            ConvaiEditorTheme.EnsureStyles();
            ConvaiEditorTheme.Fill(new Rect(0f, 0f, position.width, position.height), ConvaiEditorTheme.WindowBg);

            DrawHeroBand();

            using (new EditorGUILayout.VerticalScope(ConvaiEditorStyles.PaneContent))
            {
                DrawBody();
            }
        }

        /// <summary>Convai hero band — the same opening language every Convai window uses.</summary>
        private void DrawHeroBand() =>
            ConvaiEditorTheme.WindowHero(position.width, HeroTitleContent, HeroSubtitleContent);

        private void DrawBody()
        {
            ConvaiAICodingSetupState state = GetSetupState();

            ConvaiEditorFrame.BeginCard();
            ConvaiEditorFrame.SectionHeader(ConvaiEditorGlyphs.Validation, ReadinessHeaderContent);

            DrawStatus(
                "Unity 6000+",
                state.UnityReady,
                state.UnityVersion,
                null,
                "Install or open this project with Unity 6000 or newer.");
            DrawStatus(
                "Unity AI Assistant",
                state.AssistantReady,
                state.AssistantVersion ?? "not installed",
                CanRepairAssistant(state.AssistantVersion) ? BeginAssistantRepair : null,
                $"Installs supported package {AssistantPackageIdentifier}.");
            DrawStatus(
                "Packaged Convai skill",
                state.SkillReady,
                state.SkillDetail,
                state.SkillReady ? null : BeginSkillRepair,
                "Refreshes package assets and recompiles Editor assemblies.");
            DrawStatus(
                "Convai tools",
                state.ToolsReady,
                state.AssistantReady
                    ? state.ToolsReady
                        ? $"{state.ToolCount}/{ExpectedToolCount} registered"
                        : $"{state.ToolCount}/{ExpectedToolCount}; {state.ToolIssue}"
                    : $"{state.ToolCount}/{ExpectedToolCount}; install Unity AI Assistant first",
                CanRepairToolRegistration(state.AssistantVersion, state.ToolsReady)
                    ? BeginToolRegistrationRepair
                    : null,
                state.AssistantReady
                    ? "Refreshes the Unity MCP registry, recompiles Editor assemblies, and reconnects the bridge."
                    : "Fix Unity AI Assistant first.");

            DrawBridgeRow(state);
            DrawInstallationStatus("ChatGPT / Codex app", state.CodexInstallation.Desktop);
            DrawInstallationStatus("Unity plugin (Codex)", state.CodexInstallation.Plugin);

            GUILayout.Space(6f);
            GUILayout.Label(ClientSetupHelpContent, ConvaiEditorStyles.MutedWrapped);
            int clicked = ConvaiEditorTheme.GhostButtonRow(OpenMcpSettingsContent, RefreshContent);
            if (clicked == 0)
                OpenUnityMcpSettings();

            if (clicked == 1)
                EditorApplication.delayCall += () =>
                {
                    RefreshClientStatus();
                    RefreshInstallationProbe();
                    Repaint();
                };

            ConvaiEditorFrame.EndCard();

            if (!string.IsNullOrWhiteSpace(state.RepairMessage))
            {
                switch (state.RepairMessageType)
                {
                    case MessageType.Error:
                        ConvaiEditorFrame.ErrorBox(RepairStatusTitle, state.RepairMessage);
                        break;
                    case MessageType.Warning:
                        ConvaiEditorFrame.WarningBox(RepairStatusTitle, state.RepairMessage);
                        break;
                    default:
                        ConvaiEditorFrame.InfoBox(RepairStatusTitle, state.RepairMessage);
                        break;
                }
            }

            DrawClaudeCard(state);
            DrawChatGptCard(state);

            using (ConvaiEditorFrame.Card())
            {
                ConvaiEditorFrame.SectionHeader(ConvaiEditorGlyphs.Content, InstructionsHeaderContent);
                GUILayout.Label(
                    "Install project instructions to help these clients find the packaged Convai skill. Nothing is written until you press a button, and existing non-Convai content stays intact.",
                    ConvaiEditorStyles.MutedWrapped);
                GUILayout.Space(4f);

                foreach (ConvaiAgentClient client in Enum.GetValues(typeof(ConvaiAgentClient)))
                    if (client is not (ConvaiAgentClient.ClaudeCode or ConvaiAgentClient.Codex))
                        DrawClient(client);
            }
        }

        /// <summary>Project skill and instruction installation for Claude Code.</summary>
        private void DrawClaudeCard(ConvaiAICodingSetupState state)
        {
            using (ConvaiEditorFrame.Card())
            {
                ConvaiEditorFrame.SectionHeader(ConvaiEditorGlyphs.Content, ClaudeHeaderContent);
                GUILayout.Space(4f);

                DrawMirrorRow(state.MirrorState, state.MirrorDetail, ConvaiAgentClient.ClaudeCode);
                // Claude's instruction file belongs with the rest of Claude, not in a list that
                // would then name Claude Code twice in the same window.
                DrawClient(ConvaiAgentClient.ClaudeCode, "Project instructions");
            }
        }

        private void DrawChatGptCard(ConvaiAICodingSetupState state)
        {
            using (ConvaiEditorFrame.Card())
            {
                ConvaiEditorFrame.SectionHeader(ConvaiEditorGlyphs.Content, ChatGptHeaderContent);
                GUILayout.Space(4f);
                DrawMirrorRow(state.CodexMirrorStatus.State, state.CodexMirrorStatus.Detail, ConvaiAgentClient.Codex);
                DrawClient(ConvaiAgentClient.Codex, "Project instructions");
            }
        }

        private static void DrawInstallationStatus(string label, ConvaiInstallationStatus status)
        {
            using (ConvaiEditorFrame.Panel(null, 4f))
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, ConvaiEditorStyles.CardTitle, GUILayout.Width(130f));
                ScratchBridgeState.text = status.Label;
                ScratchBridgeState.tooltip = status.Detail ?? string.Empty;
                GUILayout.Label(ScratchBridgeState, ConvaiEditorStyles.MicroLabel);
            }
        }

        private void DrawMirrorRow(ConvaiSkillMirrorState mirrorState, string mirrorDetail, ConvaiAgentClient client)
        {
            bool installed = mirrorState != ConvaiSkillMirrorState.NotInstalled;
            using (ConvaiEditorFrame.Panel(null, 4f))
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect dot = GUILayoutUtility.GetRect(12f, 18f, GUILayout.Width(12f));
                ConvaiEditorTheme.StatusDot(
                    new Vector2(dot.x + 5f, dot.y + (dot.height * 0.5f)),
                    mirrorState switch
                    {
                        ConvaiSkillMirrorState.Installed => ConvaiEditorTheme.StatusReady,
                        ConvaiSkillMirrorState.NotInstalled => ConvaiEditorTheme.StatusIdle,
                        _ => ConvaiEditorTheme.StatusWarn
                    });

                GUILayout.Label("Project skill", ConvaiEditorStyles.CardTitle, GUILayout.Width(130f));
                ScratchMirrorState.text = DescribeMirrorState(mirrorState);
                ScratchMirrorState.tooltip = mirrorDetail ?? string.Empty;
                GUILayout.Label(ScratchMirrorState, ConvaiEditorStyles.MicroLabel);

                ScratchClientAction.text = GetMirrorActionLabel(mirrorState);
                ScratchClientAction.tooltip = $"{ConvaiSkillMirror.GetSkillsRelativeRoot(client)}/{ConvaiSkillSourceResolver.SkillName}";
                if (InlineRowButton(ScratchClientAction))
                    RunInstructionAction(() => RunMirrorAction(mirrorState, mirrorDetail, client));

                using (new EditorGUI.DisabledScope(!installed))
                    if (InlineRowButton(RemoveContent))
                        RunInstructionAction(() => RunMirrorRemoval(mirrorState, mirrorDetail, client));
            }
        }

        private static void DrawBridgeRow(ConvaiAICodingSetupState state)
        {
            using (ConvaiEditorFrame.Panel(null, 4f))
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect dot = GUILayoutUtility.GetRect(12f, 18f, GUILayout.Width(12f));
                ConvaiEditorTheme.StatusDot(
                    new Vector2(dot.x + 5f, dot.y + (dot.height * 0.5f)),
                    state.BridgeState == ConvaiClaudeBridgeState.Detected
                        ? ConvaiEditorTheme.StatusReady
                        : ConvaiEditorTheme.StatusWarn);

                GUILayout.Label("Claude MCP config", ConvaiEditorStyles.CardTitle, GUILayout.Width(130f));
                ScratchBridgeState.text = DescribeBridgeState(state.BridgeState);
                ScratchBridgeState.tooltip = state.BridgeDetail ?? string.Empty;
                GUILayout.Label(ScratchBridgeState, ConvaiEditorStyles.MicroLabel);

            }
        }

        /// <summary>One readiness row: a status dot, what it is, where it stands, and its fix.</summary>
        private static void DrawStatus(
            string label,
            bool ready,
            string detail,
            Action fixAction,
            string fixTooltip)
        {
            using (ConvaiEditorFrame.Panel(null, 4f))
            {
                Rect row = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
                ConvaiEditorTheme.StatusDot(
                    new Vector2(row.x + 6f, row.y + (row.height * 0.5f)),
                    ready ? ConvaiEditorTheme.StatusReady : ConvaiEditorTheme.StatusWarn);

                float buttonWidth = ConvaiEditorTheme.GhostButtonWidth(ScratchFixButton);
                var button = new Rect(row.xMax - buttonWidth, row.y, buttonWidth, 18f);
                float textRight = ready ? row.xMax : button.x - 6f;

                GUI.Label(new Rect(row.x + 18f, row.y, 170f, row.height), label, ConvaiEditorStyles.CardTitle);
                // A long detail is cut off at the end of its column, so it carries itself as a tooltip.
                ScratchStatusDetail.text = ScratchStatusDetail.tooltip = detail ?? string.Empty;
                GUI.Label(
                    new Rect(row.x + 192f, row.y, Mathf.Max(40f, textRight - row.x - 198f), row.height),
                    ScratchStatusDetail, ConvaiEditorStyles.MicroLabel);

                if (ready) return;

                using (new EditorGUI.DisabledScope(fixAction == null || IsRepairRunning))
                {
                    ScratchFixButton.tooltip = fixTooltip;
                    if (ConvaiEditorControls.GhostButton(button, ScratchFixButton)) fixAction?.Invoke();
                }
            }
        }

        #region Client skill mirrors and Claude bridge

        private static ConvaiSkillMirrorStatus _mirrorStatus =
            new(ConvaiSkillMirrorState.NotInstalled, string.Empty, false, null);

        private static ConvaiSkillMirrorStatus _codexMirrorStatus =
            new(ConvaiSkillMirrorState.NotInstalled, string.Empty, false, null);

        private static ConvaiClaudeBridgeStatus _bridgeStatus =
            new(ConvaiClaudeBridgeState.NotConfigured, string.Empty, null, null);

        private static bool _clientStatusValid;

        /// <summary>
        ///     Recomputes the skill and bridge rows. Both read files — the mirror is hashed and Claude's
        ///     configuration is parsed — so they run after user actions and explicit refreshes, never
        ///     from a repaint or a status poll. The Codex CLI probe runs only once and on explicit refresh.
        /// </summary>
        internal static void RefreshClientStatus()
        {
            _clientStatusValid = true;
            string projectRoot = GetProjectRoot();
            ConvaiCodexInstallationProbe.EnsureStarted(projectRoot);
            _bridgeStatus = ConvaiClaudeBridgeProbe.Inspect(projectRoot);

            // Inspect the destination even when the source cannot be resolved. Reporting a mirror
            // that exists as Not installed would disable its Remove button exactly when the SDK is
            // missing or moved, stranding content the user can no longer clean up from here.
            bool resolved = ConvaiSkillSourceResolver.TryResolve(out ConvaiSkillSource source, out string error);
            _mirrorStatus = ConvaiSkillMirror.Inspect(projectRoot, source);
            _codexMirrorStatus = ConvaiSkillMirror.Inspect(projectRoot, source, ConvaiAgentClient.Codex);
            if (!resolved && _codexMirrorStatus.State == ConvaiSkillMirrorState.NotInstalled)
                _codexMirrorStatus = new ConvaiSkillMirrorStatus(ConvaiSkillMirrorState.NotInstalled, error, false, null);
            if (!resolved && _mirrorStatus.State == ConvaiSkillMirrorState.NotInstalled)
                _mirrorStatus = new ConvaiSkillMirrorStatus(ConvaiSkillMirrorState.NotInstalled, error, false, null);
        }

        internal static void InvalidateClientStatus() => _clientStatusValid = false;

        /// <summary>Re-runs the ChatGPT / Codex installation probe, which spawns the Codex CLI.</summary>
        internal static void RefreshInstallationProbe() => ConvaiCodexInstallationProbe.Refresh(GetProjectRoot());

        private static void EnsureClientStatus()
        {
            if (!_clientStatusValid) RefreshClientStatus();
        }

        /// <summary>Copies the canonical skill into this project for the selected client. Nothing else is written.</summary>
        internal static void InstallSkillMirror(bool replaceExisting, ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            try
            {
                if (!ConvaiSkillSourceResolver.TryResolve(out ConvaiSkillSource source, out string error))
                    throw new InvalidOperationException(error);
                ConvaiSkillMirror.Install(GetProjectRoot(), source, replaceExisting, client);
                ConvaiLogger.Info(
                    $"{GetClientLabel(client)} skill mirror synchronized to Convai SDK {source.SdkVersion}.",
                    LogCategory.Editor);
            }
            finally
            {
                InvalidateClientStatus();
            }
        }

        internal static void RemoveSkillMirror(bool force, ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            try
            {
                // Removal reads the destination and its stamp, never the source payload. An
                // unresolvable source only means staleness is unknown, which is not a reason to
                // refuse deleting a mirror the user asked to remove.
                ConvaiSkillSourceResolver.TryResolve(out ConvaiSkillSource source, out _);
                ConvaiSkillMirror.Remove(GetProjectRoot(), source, force, client);
            }
            finally
            {
                InvalidateClientStatus();
            }
        }

        internal static string DescribeMirrorState(ConvaiSkillMirrorState state) => state switch
        {
            ConvaiSkillMirrorState.NotInstalled => "Not installed",
            ConvaiSkillMirrorState.Installed => "Installed",
            ConvaiSkillMirrorState.Stale => "Stale",
            ConvaiSkillMirrorState.LocallyModified => "Locally modified",
            ConvaiSkillMirrorState.Unmanaged => "Unmanaged",
            _ => state.ToString()
        };

        /// <summary>The one action that moves this state forward. Destructive states ask first.</summary>
        internal static string GetMirrorActionLabel(ConvaiSkillMirrorState state) => state switch
        {
            ConvaiSkillMirrorState.NotInstalled => "Install",
            ConvaiSkillMirrorState.Stale => "Update",
            ConvaiSkillMirrorState.LocallyModified => "Replace",
            ConvaiSkillMirrorState.Unmanaged => "Replace",
            _ => "Reinstall"
        };

        internal static bool MirrorActionNeedsConfirmation(ConvaiSkillMirrorState state) =>
            state is ConvaiSkillMirrorState.LocallyModified or ConvaiSkillMirrorState.Unmanaged;

        internal static string DescribeBridgeState(ConvaiClaudeBridgeState state) => state switch
        {
            ConvaiClaudeBridgeState.Detected => "Configuration detected",
            ConvaiClaudeBridgeState.NotConfigured => "Not configured",
            _ => "Unable to inspect"
        };

        /// <summary>
        ///     Runs the mirror action for the current state, asking before it overwrites or deletes
        ///     content Convai did not install or that someone edited.
        /// </summary>
        internal static void RunMirrorAction(ConvaiSkillMirrorState state, string detail,
            ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            bool destructive = MirrorActionNeedsConfirmation(state);
            if (destructive && !EditorUtility.DisplayDialog(
                    $"Replace {GetClientLabel(client)} skill mirror",
                    $"{detail}\n\nReplace it with the packaged Convai skill?",
                    "Replace",
                    "Cancel"))
                return;

            InstallSkillMirror(destructive, client);
        }

        internal static void RunMirrorRemoval(ConvaiSkillMirrorState state, string detail,
            ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            bool destructive = MirrorActionNeedsConfirmation(state);
            if (destructive && !EditorUtility.DisplayDialog(
                    $"Remove {GetClientLabel(client)} skill mirror",
                    $"{detail}\n\nDelete it anyway?",
                    "Remove",
                    "Cancel"))
                return;

            RemoveSkillMirror(destructive, client);
        }

        #endregion

        internal static bool IsRepairRunning =>
            _assistantInstallRequest != null || SessionState.GetBool(RepairPendingKey, false);

        internal static ConvaiAICodingSetupState GetSetupState()
        {
            bool unityReady = IsUnityCompatible();
            string assistantVersion = FindAssistantVersion();
            bool assistantReady = IsAssistantCompatible(assistantVersion);
            bool skillReady = TryResolveSkillSource(out string skillDetail);
            bool toolsReady = HasExpectedRegisteredConvaiTools(out int toolCount, out string toolIssue);
            EnsureClientStatus();
            return new ConvaiAICodingSetupState(
                unityReady,
                UnityEngine.Application.unityVersion,
                assistantReady,
                assistantVersion,
                skillReady,
                skillDetail,
                toolsReady,
                toolCount,
                toolIssue,
                _mirrorStatus.State,
                _mirrorStatus.Detail,
                _bridgeStatus.State,
                _bridgeStatus.Detail,
                IsRepairRunning,
                SessionState.GetString(RepairMessageKey, string.Empty),
                GetRepairMessageType(),
                _codexMirrorStatus,
                ConvaiCodexInstallationProbe.Status);
        }

        internal static void OpenUnityMcpSettings() => SettingsService.OpenProjectSettings(McpSettingsPath);

        internal static bool CanRepairAssistant(string version) => !IsAssistantCompatible(version);

        internal static bool CanRepairToolRegistration(string assistantVersion, bool toolsReady) =>
            IsAssistantCompatible(assistantVersion) && !toolsReady;

        internal static void BeginAssistantRepair()
        {
            if (!CanStartRepair()) return;
            try
            {
                SetRepairStatus(
                    $"Installing {AssistantPackageIdentifier}. Unity may recompile and reload assemblies.",
                    MessageType.Info,
                    true,
                    true);
                SessionState.SetInt(RepairGoalKey, (int)RepairGoal.FullSetup);
                _assistantInstallRequest = PackageManagerClient.Add(AssistantPackageIdentifier);
                EditorApplication.update -= PollAssistantInstall;
                EditorApplication.update += PollAssistantInstall;
                RepaintOpenWindows();
            }
            catch (Exception exception)
            {
                FailRepair($"Could not start Unity AI Assistant installation: {exception.Message}");
            }
        }

        private static void PollAssistantInstall()
        {
            if (_assistantInstallRequest == null)
            {
                EditorApplication.update -= PollAssistantInstall;
                return;
            }

            if (!_assistantInstallRequest.IsCompleted)
            {
                RepaintOpenWindows();
                return;
            }

            AddRequest completed = _assistantInstallRequest;
            _assistantInstallRequest = null;
            EditorApplication.update -= PollAssistantInstall;
            if (completed.Status == PackageStatusCode.Success)
            {
                string installedVersion = completed.Result?.version ?? "unknown version";
                ConvaiLogger.Info(
                    $"Unity AI Assistant {installedVersion} installed. Refreshing Convai MCP tools.",
                    LogCategory.Editor);
                BeginRegistrationRepairInternal(
                    $"Unity AI Assistant {installedVersion} installed. Compiling and registering Convai tools.",
                    RepairGoal.FullSetup);
                return;
            }

            string error = completed.Error?.message ?? "Unity Package Manager returned an unknown error.";
            FailRepair($"Unity AI Assistant installation failed: {error}");
        }

        internal static void BeginSkillRepair()
        {
            if (!CanStartRepair()) return;
            BeginRegistrationRepairInternal(
                "Refreshing package assets and locating the packaged Convai skill.",
                RepairGoal.PackagedSkill);
        }

        internal static void BeginToolRegistrationRepair()
        {
            if (!CanStartRepair()) return;
            BeginRegistrationRepairInternal(
                "Refreshing package assets and registering Convai MCP tools.",
                RepairGoal.ToolRegistration);
        }

        private static void BeginRegistrationRepairInternal(string message, RepairGoal goal)
        {
            try
            {
                SessionState.SetInt(RepairGoalKey, (int)goal);
                SetRepairStatus(message, MessageType.Info, true, true);
                TryRefreshToolRegistry(out _);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                CompilationPipeline.RequestScriptCompilation();
                ScheduleRepairCompletionPoll();
            }
            catch (Exception exception)
            {
                FailRepair($"Could not refresh Convai MCP tools: {exception.Message}");
            }
        }

        private static bool CanStartRepair()
        {
            if (IsRepairRunning) return false;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SetRepairStatus(
                    "Exit Play Mode before repairing AI coding setup. Play Mode was not changed automatically.",
                    MessageType.Warning,
                    false,
                    false);
                return false;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                SetRepairStatus(
                    "Unity is compiling or updating packages. Wait for it to finish, then click Fix again.",
                    MessageType.Warning,
                    false,
                    false);
                return false;
            }

            return true;
        }

        private static void ScheduleRepairCompletionPoll()
        {
            _nextRegistryRefreshAt = 0d;
            EditorApplication.update -= PollRepairCompletion;
            EditorApplication.update += PollRepairCompletion;
            RepaintOpenWindows();
        }

        private static void PollRepairCompletion()
        {
            if (!SessionState.GetBool(RepairPendingKey, false))
            {
                EditorApplication.update -= PollRepairCompletion;
                return;
            }

            if (_assistantInstallRequest != null || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                RepaintOpenWindows();
                return;
            }

            string assistantVersion = FindAssistantVersion();
            var goal = (RepairGoal)SessionState.GetInt(RepairGoalKey, (int)RepairGoal.FullSetup);
            if (goal == RepairGoal.PackagedSkill)
            {
                if (HasPackagedSkill())
                    CompleteRepair("Packaged Convai skill ready.");
                else if (HasRepairTimedOut())
                    FailRepair("The packaged Convai skill is still missing. Reimport or update the Convai SDK package, then retry.");
                return;
            }

            if (!IsAssistantCompatible(assistantVersion))
            {
                if (HasRepairTimedOut())
                    FailRepair("Unity AI Assistant is still unavailable after the package refresh. Check Package Manager and Editor logs, then retry.");
                return;
            }

            if (goal == RepairGoal.FullSetup && !HasPackagedSkill())
            {
                if (HasRepairTimedOut())
                    FailRepair("The packaged Convai skill is still missing. Reimport or update the Convai SDK package, then retry.");
                return;
            }

            bool toolsReady = HasExpectedRegisteredConvaiTools(out int toolCount, out string toolIssue);
            string registryError = string.Empty;
            if (!toolsReady && EditorApplication.timeSinceStartup >= _nextRegistryRefreshAt)
            {
                TryRefreshToolRegistry(out registryError);
                _nextRegistryRefreshAt = EditorApplication.timeSinceStartup + 1d;
                toolsReady = HasExpectedRegisteredConvaiTools(out toolCount, out toolIssue);
            }

            if (toolsReady)
            {
                if (!TryRestartUnityMcpBridge(out bool restarted, out string bridgeError))
                {
                    FailRepair($"Convai tools registered, but Unity MCP bridge reconnect failed: {bridgeError}");
                    return;
                }

                string reconnect = restarted ? " Unity MCP bridge reconnected." : string.Empty;
                CompleteRepair($"AI coding setup ready. {toolCount}/{ExpectedToolCount} Convai tools registered.{reconnect}");
                return;
            }

            if (HasRepairTimedOut())
            {
                string suffix = string.IsNullOrWhiteSpace(registryError) ? string.Empty : $" {registryError}";
                FailRepair($"Unity AI Assistant loaded, but the Convai tool catalog is unhealthy: {toolIssue}.{suffix}");
            }
        }

        internal static bool TryRefreshToolRegistry(out string error)
        {
            Type registryType = FindUnityMcpType(RegistryTypeName);
            if (registryType == null)
            {
                error = "Unity MCP tool registry is not loaded.";
                return false;
            }

            MethodInfo refresh = registryType.GetMethod(
                "RefreshTools",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (refresh == null)
            {
                error = "Unity MCP tool registry has no compatible RefreshTools method.";
                return false;
            }

            try
            {
                refresh.Invoke(null, null);
                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException?.Message ?? exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        internal static bool HasExpectedRegisteredConvaiTools(out int toolCount, out string issue)
        {
            if (!TryGetRegisteredConvaiToolNames(out string[] names, out issue))
            {
                toolCount = 0;
                return false;
            }

            toolCount = names.Length;
            return HasExpectedToolNames(names, out issue);
        }

        internal static bool HasExpectedToolNames(IEnumerable<string> toolNames, out string issue)
        {
            var expected = new HashSet<string>(ExpectedToolNames, StringComparer.Ordinal);
            var actual = new HashSet<string>(
                (toolNames ?? Enumerable.Empty<string>()).Where(name =>
                    !string.IsNullOrWhiteSpace(name) && name.StartsWith("Convai_", StringComparison.Ordinal)),
                StringComparer.Ordinal);
            string[] missing = expected.Except(actual).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            string[] unexpected = actual.Except(expected).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (missing.Length == 0 && unexpected.Length == 0)
            {
                issue = string.Empty;
                return true;
            }

            var parts = new List<string>(2);
            if (missing.Length > 0) parts.Add($"missing {string.Join(", ", missing)}");
            if (unexpected.Length > 0) parts.Add($"unexpected {string.Join(", ", unexpected)}");
            issue = string.Join("; ", parts);
            return false;
        }

        private static string[] LoadExpectedToolNames()
        {
            Type catalogType = Type.GetType(
                "Convai.Editor.AI.ConvaiMcpToolCatalog, Convai.Editor.AI",
                throwOnError: false);
            FieldInfo allField = catalogType?.GetField(
                "All",
                BindingFlags.Static | BindingFlags.NonPublic);
            return allField?.GetValue(null) is IEnumerable<string> toolIds
                ? toolIds.Select(toolId => toolId.Replace('.', '_')).ToArray()
                : Array.Empty<string>();
        }

        internal static bool TryRestartUnityMcpBridge(out bool restarted, out string error)
        {
            restarted = false;
            Type bridgeType = FindUnityMcpType(BridgeTypeName);
            if (bridgeType == null)
            {
                error = "Unity MCP bridge is not loaded.";
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo isRunningProperty = bridgeType.GetProperty("IsRunning", flags);
            MethodInfo stop = bridgeType.GetMethod("Stop", flags, null, Type.EmptyTypes, null);
            MethodInfo start = bridgeType.GetMethod("Start", flags, null, Type.EmptyTypes, null);
            if (isRunningProperty == null || stop == null || start == null)
            {
                error = "Unity MCP bridge has no compatible lifecycle API.";
                return false;
            }

            try
            {
                if (!(isRunningProperty.GetValue(null) is bool isRunning) || !isRunning)
                {
                    error = string.Empty;
                    return true;
                }

                stop.Invoke(null, null);
                start.Invoke(null, null);
                restarted = true;
                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException?.Message ?? exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool HasRepairTimedOut()
        {
            string value = SessionState.GetString(RepairStartedTicksKey, string.Empty);
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) return false;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >=
                   TimeSpan.FromSeconds(RepairTimeoutSeconds);
        }

        private static void CompleteRepair(string message)
        {
            EditorApplication.update -= PollRepairCompletion;
            InvalidateClientStatus();
            SetRepairStatus(message, MessageType.Info, false, false);
            ConvaiLogger.Info(message, LogCategory.Editor);
        }

        private static void FailRepair(string message)
        {
            _assistantInstallRequest = null;
            EditorApplication.update -= PollAssistantInstall;
            EditorApplication.update -= PollRepairCompletion;
            SetRepairStatus(message, MessageType.Error, false, false);
            ConvaiLogger.Error(message, LogCategory.Editor);
        }

        private static void SetRepairStatus(
            string message,
            MessageType messageType,
            bool pending,
            bool resetStartedAt)
        {
            SessionState.SetString(RepairMessageKey, message ?? string.Empty);
            SessionState.SetInt(RepairMessageTypeKey, (int)messageType);
            SessionState.SetBool(RepairPendingKey, pending);
            if (resetStartedAt)
                SessionState.SetString(
                    RepairStartedTicksKey,
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            else if (!pending)
                SessionState.SetString(RepairStartedTicksKey, string.Empty);
            RepaintOpenWindows();
        }

        private static MessageType GetRepairMessageType()
        {
            int value = SessionState.GetInt(RepairMessageTypeKey, (int)MessageType.Info);
            return Enum.IsDefined(typeof(MessageType), value) ? (MessageType)value : MessageType.Info;
        }

        private static void RepaintOpenWindows()
        {
            foreach (ConvaiAICodingSetupWindow window in Resources.FindObjectsOfTypeAll<ConvaiAICodingSetupWindow>())
                window.Repaint();
        }

        private void DrawClient(ConvaiAgentClient client, string displayName = null)
        {
            string projectRoot = GetProjectRoot();
            bool installed;
            try { installed = ConvaiAgentInstructionsInstaller.ContainsManagedBlock(projectRoot, client); }
            catch (InvalidOperationException) { installed = false; }

            using (ConvaiEditorFrame.Panel(null, 4f))
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect dot = GUILayoutUtility.GetRect(12f, 18f, GUILayout.Width(12f));
                ConvaiEditorTheme.StatusDot(
                    new Vector2(dot.x + 5f, dot.y + (dot.height * 0.5f)),
                    installed ? ConvaiEditorTheme.StatusReady : ConvaiEditorTheme.StatusIdle);

                GUILayout.Label(
                    displayName ?? GetClientLabel(client), ConvaiEditorStyles.CardTitle, GUILayout.Width(130f));
                GUILayout.Label(
                    ConvaiAgentInstructionsInstaller.GetRelativePath(client), ConvaiEditorStyles.MicroLabel);

                ScratchClientAction.text = installed ? "Update" : "Install";
                if (InlineRowButton(ScratchClientAction))
                    RunInstructionAction(() =>
                        ConvaiAgentInstructionsInstaller.UpsertWithResolvedSkill(
                            projectRoot, client, ResolveSkillPointer));

                using (new EditorGUI.DisabledScope(!installed))
                    if (InlineRowButton(RemoveContent))
                        RunInstructionAction(() => ConvaiAgentInstructionsInstaller.Remove(projectRoot, client));
            }
        }

        /// <summary>
        ///     A ghost button sized to its label, at the 18-pixel height of the text row it sits in.
        /// </summary>
        private static bool InlineRowButton(GUIContent content)
        {
            float width = ConvaiEditorTheme.GhostButtonWidth(content);
            return ConvaiEditorControls.GhostButton(
                GUILayoutUtility.GetRect(width, 18f, GUILayout.Width(width)), content);
        }

        /// <summary>
        ///     Runs an install/remove action after the current IMGUI pass. It writes files, refreshes
        ///     the asset database and can raise a modal on failure — all of which discard the layout
        ///     state the enclosing scope is about to close when they happen mid-pass, leaving the
        ///     window throwing on every later repaint.
        /// </summary>
        private void RunInstructionAction(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                    InvalidateClientStatus();
                    AssetDatabase.Refresh();
                    Repaint();
                }
                catch (Exception exception)
                {
                    ConvaiLogger.Error($"AI Coding Setup failed: {exception.Message}", LogCategory.Editor);
                    EditorUtility.DisplayDialog("Convai AI Coding Setup", exception.Message, "OK");
                }
            };
        }

        internal static string GetClientLabel(ConvaiAgentClient client) => client switch
        {
            ConvaiAgentClient.Codex => "ChatGPT / Codex",
            ConvaiAgentClient.ClaudeCode => "Claude Code",
            ConvaiAgentClient.Copilot => "VS Code Copilot",
            _ => client.ToString()
        };

        private static bool IsUnityCompatible() =>
            Version.TryParse(UnityEngine.Application.unityVersion.Split('f', 'b', 'a', 'p')[0], out Version version) &&
            version.Major >= 6000;

        private static string FindAssistantVersion() => PackageInfo.GetAllRegisteredPackages()
            .FirstOrDefault(package => package.name == AssistantPackageName)?.version;

        internal static bool IsAssistantCompatible(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            int separator = value.IndexOf('-');
            string numeric = separator >= 0 ? value.Substring(0, separator) : value;
            string prerelease = separator >= 0 ? value.Substring(separator + 1) : string.Empty;
            if (!Version.TryParse(numeric, out Version version)) return false;
            if (version < MinimumAssistantVersion || version > MaximumAssistantVersion) return false;
            if (version == MaximumAssistantVersion) return prerelease.Length > 0;
            if (version > MinimumAssistantVersion) return true;
            if (prerelease.Length == 0) return true;
            const string prefix = "pre.";
            return prerelease.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(prerelease.Substring(prefix.Length), out int prereleaseNumber) &&
                   prereleaseNumber >= 2;
        }

        private static bool HasPackagedSkill() => TryResolveSkillSource(out _);

        /// <summary>
        ///     Resolves the physical skill source for every supported layout. A PackageCache install
        ///     resolves through Unity's own resolved path, never a hand-built cache suffix.
        /// </summary>
        private static bool TryResolveSkillSource(out string detail)
        {
            if (!ConvaiSkillSourceResolver.TryResolve(out ConvaiSkillSource source, out string error))
            {
                detail = error;
                return false;
            }

            string pointer = ConvaiSkillSourceResolver.ToInstructionPointer(GetProjectRoot(), source.SkillFilePath);
            detail = string.IsNullOrEmpty(source.Warning) ? pointer : $"{pointer} ({source.Warning})";
            return true;
        }

        /// <summary>
        ///     Resolves the instruction pointer at click time. It throws the resolver's own error so the
        ///     caller reports that operation's failure instead of writing a broken path.
        /// </summary>
        internal static string ResolveSkillPointer()
        {
            if (!ConvaiSkillSourceResolver.TryResolve(out ConvaiSkillSource source, out string error))
                throw new InvalidOperationException(error);
            return ConvaiSkillSourceResolver.ToInstructionPointer(GetProjectRoot(), source.SkillFilePath);
        }

        private static bool TryGetRegisteredConvaiToolNames(out string[] names, out string error)
        {
            names = Array.Empty<string>();
            Type registryType = FindUnityMcpType(RegistryTypeName);
            if (registryType == null)
            {
                error = "Unity MCP tool registry is not loaded";
                return false;
            }

            MethodInfo getTools = registryType.GetMethod(
                "GetAvailableTools",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(bool) },
                null);
            if (getTools == null)
            {
                error = "Unity MCP tool registry has no compatible GetAvailableTools method";
                return false;
            }

            try
            {
                object value = getTools.Invoke(null, new object[] { true });
                if (!(value is IEnumerable tools))
                {
                    error = "Unity MCP tool registry returned an invalid tool collection";
                    return false;
                }

                var registered = new List<string>();
                foreach (object tool in tools)
                {
                    if (tool == null) continue;
                    PropertyInfo nameProperty = tool.GetType().GetProperty(
                        "name",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (nameProperty?.GetValue(tool) is string name &&
                        name.StartsWith("Convai_", StringComparison.Ordinal))
                        registered.Add(name);
                }

                names = registered.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException?.Message ?? exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static Type FindUnityMcpType(string fullName) =>
            Type.GetType($"{fullName}, Unity.AI.MCP.Editor") ??
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);

        internal static string GetProjectRoot() =>
            System.IO.Directory.GetParent(UnityEngine.Application.dataPath)?.FullName ?? UnityEngine.Application.dataPath;

        [InitializeOnLoadMethod]
        private static void ScheduleOnboardingPrompt()
        {
            if (UnityEngine.Application.isBatchMode) return;
            EditorApplication.delayCall += ShowOnboardingPromptOnce;
            if (SessionState.GetBool(RepairPendingKey, false))
                EditorApplication.delayCall += ScheduleRepairCompletionPoll;
        }

        private static void ShowOnboardingPromptOnce()
        {
            string key = "Convai.AICodingSetup.Shown." + Hash128.Compute(UnityEngine.Application.dataPath);
            if (EditorPrefs.GetBool(key, false)) return;
            string assistantVersion = FindAssistantVersion();
            if (!IsUnityCompatible() || !IsAssistantCompatible(assistantVersion) || !HasPackagedSkill()) return;
            EditorPrefs.SetBool(key, true);
            if (EditorUtility.DisplayDialog(
                    "Convai AI Coding Setup",
                    "Unity AI Assistant detected. Open Convai AI Coding Setup to configure external agents and inspect MCP readiness? No files are written automatically.",
                    "Open Setup",
                    "Later"))
                OpenWindow();
        }
    }
}
