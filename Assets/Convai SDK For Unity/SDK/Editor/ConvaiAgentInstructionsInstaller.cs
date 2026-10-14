using System;
using System.IO;
using System.Text;

namespace Convai.Editor
{
    internal enum ConvaiAgentClient
    {
        Codex,
        ClaudeCode,
        Cursor,
        Gemini,
        Copilot
    }

    internal static class ConvaiAgentInstructionsInstaller
    {
        internal const string BeginSentinel = "<!-- BEGIN CONVAI UNITY SDK -->";
        internal const string EndSentinel = "<!-- END CONVAI UNITY SDK -->";

        /// <summary>
        ///     The maintained Play Mode restriction. It must stay in step with rule 7 of the canonical
        ///     <c>SKILL.md</c>; <c>ConvaiAgentPolicyConsistencyTests</c> fails if either side relaxes.
        /// </summary>
        internal const string PlayModePolicy =
            "Never save scenes, change Play Mode, or bake a NavMesh unless explicitly requested.";

        private const string SkillPointerFallback =
            "the packaged Convai skill `convai-unity-sdk` (`AIAssistantSkills/convai-unity-sdk/SKILL.md` inside the installed Convai SDK)";

        /// <summary>The bridge that actually hosts the Convai tools, which a skill alone does not create.</summary>
        internal const string BridgeExplanation =
            "Convai tools are hosted by the Unity MCP Server from `com.unity.ai.assistant`. Configure the client in Project Settings > AI > Unity MCP Server. Unity CLI/Pipeline uses a separate bridge.";

        private const string ManagedInstructionsBody =
            "Use Unity MCP for generic Editor operations and Convai tools for SDK scene configuration and diagnosis; inspect targets and use returned instance IDs. Repository code and documentation work can use file and shell tools without an Editor inspection.\n" +
            "A setup or fix request authorizes its reversible changes: preview supported mutators, apply within scope, and verify. Resolve ambiguous targets before dependent mutations. Diagnosis-only requests do not authorize repairs.\n" +
            "User instructions take precedence over skill guidelines, subject to host permissions. If a skill blocks requested work, identify the file and exact rule, and complete independent authorized work.\n" +
            "Never request or expose API keys. Keep transcript text hidden unless the user explicitly asks for it, using the skill's opt-in tools. " +
            PlayModePolicy + "\n" +
            BridgeExplanation;

        /// <summary>
        ///     Builds the managed block. With a mirror installed the client discovers the skill by name,
        ///     so the block carries no filesystem pointer at all. Without one it names a physical
        ///     location, because Unity's logical <c>Packages/&lt;name&gt;</c> path is not a path an
        ///     external agent can open.
        /// </summary>
        internal static string BuildManagedInstructions(string skillPointer, bool skillMirrorInstalled = false)
        {
            string target = skillMirrorInstalled
                ? $"the installed `{ConvaiSkillSourceResolver.SkillName}` skill"
                : string.IsNullOrWhiteSpace(skillPointer)
                    ? SkillPointerFallback
                    : $"`{skillPointer.Trim()}`";
            return
                $"For Convai scene setup, configuration, or diagnosis, use {target}, then only references relevant to the task.\n" +
                ManagedInstructionsBody;
        }

        internal static string GetRelativePath(ConvaiAgentClient client) => client switch
        {
            ConvaiAgentClient.Codex => "AGENTS.md",
            ConvaiAgentClient.ClaudeCode => "CLAUDE.md",
            ConvaiAgentClient.Cursor => ".cursor/rules/convai-unity-sdk.mdc",
            ConvaiAgentClient.Gemini => "GEMINI.md",
            ConvaiAgentClient.Copilot => ".github/copilot-instructions.md",
            _ => throw new ArgumentOutOfRangeException(nameof(client), client, null)
        };

        internal static bool ContainsManagedBlock(string projectRoot, ConvaiAgentClient client)
        {
            string path = ResolvePath(projectRoot, client);
            if (!File.Exists(path)) return false;
            string content = ReadAllTextStrict(path);
            return content.Contains(BeginSentinel, StringComparison.Ordinal) &&
                   content.Contains(EndSentinel, StringComparison.Ordinal);
        }

        /// <summary>Use a verified client mirror before asking for the packaged skill's location.</summary>
        internal static void UpsertWithResolvedSkill(
            string projectRoot, ConvaiAgentClient client, Func<string> resolveSkillPointer)
        {
            bool hasMirror = (client is ConvaiAgentClient.ClaudeCode or ConvaiAgentClient.Codex) &&
                             ConvaiSkillMirror.Inspect(projectRoot, default, client).State == ConvaiSkillMirrorState.Installed;
            Upsert(projectRoot, client, hasMirror ? null : resolveSkillPointer(), hasMirror);
        }

        /// <param name="skillPointer">
        ///     Physical, agent-openable location of <c>SKILL.md</c>. When null the block names the skill
        ///     instead of a path, which is correct but slower for the agent to act on.
        /// </param>
        /// <param name="skillMirrorInstalled">
        ///     The client already has a project copy of the skill, so the block names the skill instead
        ///     of any path.
        /// </param>
        internal static void Upsert(
            string projectRoot,
            ConvaiAgentClient client,
            string skillPointer = null,
            bool skillMirrorInstalled = false)
        {
            string path = ResolvePath(projectRoot, client);
            string original = File.Exists(path) ? ReadAllTextStrict(path) : string.Empty;
            string lineEnding = DetectLineEnding(original);
            string withoutBlock = RemoveManagedBlock(original);
            string block = BeginSentinel + lineEnding +
                           BuildManagedInstructions(skillPointer, skillMirrorInstalled).Replace("\n", lineEnding) + lineEnding +
                           EndSentinel;

            string updated = AppendBlock(withoutBlock, block, lineEnding);
            WriteAtomic(path, updated);
        }

        internal static void Remove(string projectRoot, ConvaiAgentClient client)
        {
            string path = ResolvePath(projectRoot, client);
            if (!File.Exists(path)) return;
            string original = ReadAllTextStrict(path);
            string updated = RemoveManagedBlock(original);
            if (string.Equals(original, updated, StringComparison.Ordinal)) return;
            WriteAtomic(path, updated);
        }

        private static string ResolvePath(string projectRoot, ConvaiAgentClient client)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Project root is required.", nameof(projectRoot));
            return Path.Combine(projectRoot, GetRelativePath(client));
        }

        private static string ReadAllTextStrict(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Cannot read agent instruction file '{path}'.", exception);
            }
        }

        private static string RemoveManagedBlock(string content)
        {
            int begin = content.IndexOf(BeginSentinel, StringComparison.Ordinal);
            if (begin < 0) return content;
            int end = content.IndexOf(EndSentinel, begin, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidOperationException("Convai instruction block has a begin sentinel but no end sentinel.");

            end += EndSentinel.Length;
            string prefix = content.Substring(0, begin).TrimEnd('\r', '\n');
            string suffix = content.Substring(end).TrimStart('\r', '\n');
            if (prefix.Length == 0) return suffix;
            if (suffix.Length == 0) return prefix;
            string lineEnding = DetectLineEnding(content);
            return prefix + lineEnding + lineEnding + suffix;
        }

        private static string AppendBlock(string content, string block, string lineEnding)
        {
            if (string.IsNullOrEmpty(content)) return block + lineEnding;
            string trimmed = content.TrimEnd('\r', '\n');
            return trimmed + lineEnding + lineEnding + block + lineEnding;
        }

        private static string DetectLineEnding(string content) =>
            content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        private static void WriteAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Instruction path has no directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Cannot atomically write agent instruction file '{path}'.", exception);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
