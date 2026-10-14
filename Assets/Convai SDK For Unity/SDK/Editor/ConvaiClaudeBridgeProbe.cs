using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Convai.Editor
{
    /// <summary>Where a Claude MCP server entry was registered. Lower values win on the same name.</summary>
    internal enum ConvaiClaudeConfigScope
    {
        /// <summary><c>~/.claude.json</c> → <c>projects[&lt;project path&gt;].mcpServers</c>.</summary>
        ProjectLocal,

        /// <summary><c>&lt;project&gt;/.mcp.json</c> → <c>mcpServers</c>.</summary>
        ProjectShared,

        /// <summary><c>~/.claude.json</c> → top-level <c>mcpServers</c>.</summary>
        User
    }

    internal enum ConvaiClaudeBridgeState
    {
        /// <summary>An Assistant relay entry is registered. This is configuration evidence, not a connection.</summary>
        Detected,

        NotConfigured,

        /// <summary>A configuration file exists but could not be read or parsed.</summary>
        UnableToInspect
    }

    internal readonly struct ConvaiClaudeBridgeStatus
    {
        internal ConvaiClaudeBridgeStatus(
            ConvaiClaudeBridgeState state,
            string detail,
            ConvaiClaudeConfigScope? scope,
            string serverName)
        {
            State = state;
            Detail = detail;
            Scope = scope;
            ServerName = serverName;
        }

        internal ConvaiClaudeBridgeState State { get; }
        internal string Detail { get; }
        internal ConvaiClaudeConfigScope? Scope { get; }
        internal string ServerName { get; }
    }

    /// <summary>
    ///     Read-only check for a Claude registration of the Assistant MCP server that hosts the Convai
    ///     tools. It reports configuration evidence only: a detected entry does not mean Claude is
    ///     connected or that the tools are usable. Convai never writes <c>~/.claude.json</c> or
    ///     <c>.mcp.json</c>, never executes a configured command, and never logs raw configuration,
    ///     secrets, or environment values.
    /// </summary>
    internal static class ConvaiClaudeBridgeProbe
    {
        internal const string UserConfigFileName = ".claude.json";
        internal const string ProjectSharedConfigFileName = ".mcp.json";
        internal const string RelayRelativeRoot = ".unity/relay";

        /// <summary>The relay executables Unity ships. The entry key is never the matcher.</summary>
        internal static readonly string[] RelayFileNames =
        {
            "relay_mac_arm64",
            "relay_mac_x64",
            "relay_win.exe",
            "relay_linux"
        };

        private static readonly string[] RequiredArguments = { "--mcp" };

        internal static ConvaiClaudeBridgeStatus Inspect(string projectRoot)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                return new ConvaiClaudeBridgeStatus(
                    ConvaiClaudeBridgeState.UnableToInspect,
                    "Cannot determine the user's home directory, so Claude's MCP configuration cannot be read.",
                    null,
                    null);

            string userConfigPath = Path.Combine(home, UserConfigFileName);
            string projectConfigPath = Path.Combine(projectRoot, ProjectSharedConfigFileName);

            if (!TryRead(userConfigPath, out string userConfig, out string readError) ||
                !TryRead(projectConfigPath, out string projectConfig, out readError))
                return new ConvaiClaudeBridgeStatus(ConvaiClaudeBridgeState.UnableToInspect, readError, null, null);

            return Evaluate(userConfig, projectConfig, projectRoot, Path.Combine(home, ".unity", "relay"));
        }

        /// <summary>
        ///     Resolves all three locations into one effective server set and looks for an Assistant
        ///     relay entry. Same-name entries follow local &gt; project &gt; user precedence, so an
        ///     overridden lower-scope entry is never reported as effective.
        /// </summary>
        internal static ConvaiClaudeBridgeStatus Evaluate(
            string userConfigJson,
            string projectConfigJson,
            string projectRoot,
            string relayRoot)
        {
            var effective = new Dictionary<string, (ConvaiClaudeConfigScope Scope, JObject Entry)>(StringComparer.Ordinal);
            try
            {
                JObject user = Parse(userConfigJson);
                JObject project = Parse(projectConfigJson);

                Merge(effective, ReadServers(user?["mcpServers"]), ConvaiClaudeConfigScope.User);
                Merge(effective, ReadServers(project?["mcpServers"]), ConvaiClaudeConfigScope.ProjectShared);
                Merge(
                    effective,
                    ReadServers(FindProjectSection(user, projectRoot)?["mcpServers"]),
                    ConvaiClaudeConfigScope.ProjectLocal);
            }
            catch (JsonException exception)
            {
                return new ConvaiClaudeBridgeStatus(
                    ConvaiClaudeBridgeState.UnableToInspect,
                    $"Claude MCP configuration could not be parsed{DescribeParseLocation(exception)}. " +
                    $"Check the JSON syntax in ~/{UserConfigFileName} and {ProjectSharedConfigFileName}.",
                    null,
                    null);
            }

            foreach (KeyValuePair<string, (ConvaiClaudeConfigScope Scope, JObject Entry)> server in
                     effective.OrderBy(entry => entry.Value.Scope).ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!IsAssistantRelay(
                        ReadCommand(server.Value.Entry),
                        ReadArguments(server.Value.Entry?["args"]),
                        relayRoot))
                    continue;

                return new ConvaiClaudeBridgeStatus(
                    ConvaiClaudeBridgeState.Detected,
                    $"Assistant MCP server '{server.Key}' is registered in {Describe(server.Value.Scope)}. " +
                    "Configuration evidence only; it does not prove Claude is connected or that the tools are usable.",
                    server.Value.Scope,
                    server.Key);
            }

            bool pipelineOnly = effective.Values.Any(entry => IsPipelineOnly(
                ReadCommand(entry.Entry),
                ReadArguments(entry.Entry?["args"])));
            return new ConvaiClaudeBridgeStatus(
                ConvaiClaudeBridgeState.NotConfigured,
                pipelineOnly
                    ? "Only a Unity CLI/Pipeline entry is registered. That is a separate bridge and does not supply the Assistant-registered Convai tools."
                    : "No Assistant MCP server registration found in the user, project-local, or project-shared Claude configuration.",
                null,
                null);
        }

        /// <summary>
        ///     Accepts an entry only when the command resolves inside the user's relay directory, its
        ///     filename is a known relay executable, and its arguments are exactly <c>["--mcp"]</c>.
        ///     The check is on directory boundaries and the filename, never an arbitrary substring.
        ///     A path containing a <c>..</c> segment is rejected outright rather than resolved. A
        ///     lexical prefix test cannot see that <c>~/.unity/relay/../../other/relay_linux</c> starts
        ///     inside the relay root and then leaves it, and Claude would run the escaped command while
        ///     this reported the bridge as detected.
        /// </summary>
        internal static bool IsAssistantRelay(string command, IReadOnlyList<string> arguments, string relayRoot)
        {
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(relayRoot)) return false;
            if (arguments == null || !arguments.SequenceEqual(RequiredArguments, StringComparer.Ordinal)) return false;

            string normalized = NormalizeCommandPath(command);
            if (HasTraversalSegment(normalized)) return false;
            string root = NormalizeCommandPath(relayRoot).TrimEnd('/');
            if (normalized.Length <= root.Length + 1) return false;
            if (!normalized.StartsWith(root, PathComparison) || normalized[root.Length] != '/') return false;

            string fileName = normalized.Substring(normalized.LastIndexOf('/') + 1);
            return RelayFileNames.Contains(fileName, PathComparer);
        }

        /// <summary>True when any path segment is <c>..</c>, which could walk out of the relay root.</summary>
        private static bool HasTraversalSegment(string normalizedPath) =>
            normalizedPath.Split('/').Any(segment => segment == "..");

        /// <summary>A <c>unity</c> executable with <c>mcp</c> arguments is the Pipeline bridge, not this one.</summary>
        internal static bool IsPipelineOnly(string command, IReadOnlyList<string> arguments)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            string normalized = NormalizeCommandPath(command);
            string fileName = normalized.Substring(normalized.LastIndexOf('/') + 1);
            return (string.Equals(fileName, "unity", PathComparison) ||
                    string.Equals(fileName, "unity.exe", PathComparison)) &&
                   arguments != null &&
                   arguments.Contains("mcp", StringComparer.Ordinal);
        }

        internal static string Describe(ConvaiClaudeConfigScope scope) => scope switch
        {
            ConvaiClaudeConfigScope.ProjectLocal => $"~/{UserConfigFileName} (this project)",
            ConvaiClaudeConfigScope.ProjectShared => $"{ProjectSharedConfigFileName} (project-shared)",
            ConvaiClaudeConfigScope.User => $"~/{UserConfigFileName} (user scope)",
            _ => scope.ToString()
        };

        private static StringComparison PathComparison => ConvaiSkillSourceResolver.PathComparison;

        private static StringComparer PathComparer => ConvaiSkillSourceResolver.PathComparer;

        private static string NormalizeCommandPath(string value)
        {
            string normalized = value.Trim().Trim('"').Replace('\\', '/');
            if (normalized.StartsWith("~/", StringComparison.Ordinal))
                normalized = ConvaiSkillSourceResolver.Normalize(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        normalized.Substring(2)));
            // Collapse repeated separators without destroying a leading UNC prefix.
            string prefix = normalized.StartsWith("//", StringComparison.Ordinal) ? "//" : string.Empty;
            string body = normalized.Substring(prefix.Length);
            while (body.Contains("//", StringComparison.Ordinal)) body = body.Replace("//", "/");
            return prefix + body;
        }

        private static bool TryRead(string path, out string content, out string error)
        {
            content = null;
            error = string.Empty;
            if (!File.Exists(path)) return true;
            try
            {
                content = File.ReadAllText(path);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The message names the file, never its contents.
                error = $"'{ConvaiSkillSourceResolver.Normalize(path)}' could not be read: {exception.Message}";
                return false;
            }
        }

        private static JObject Parse(string json) =>
            string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);

        /// <summary>Finds this project's section by path, tolerating separator and trailing-slash differences.</summary>
        private static JObject FindProjectSection(JObject userConfig, string projectRoot)
        {
            if (userConfig?["projects"] is not JObject projects || string.IsNullOrWhiteSpace(projectRoot)) return null;
            string wanted = NormalizeCommandPath(projectRoot).TrimEnd('/');
            foreach (KeyValuePair<string, JToken> project in projects)
                if (string.Equals(NormalizeCommandPath(project.Key).TrimEnd('/'), wanted, PathComparison))
                    return project.Value as JObject;
            return null;
        }

        private static IEnumerable<KeyValuePair<string, JObject>> ReadServers(JToken servers)
        {
            if (servers is not JObject map) yield break;
            foreach (KeyValuePair<string, JToken> entry in map)
                if (entry.Value is JObject definition)
                    yield return new KeyValuePair<string, JObject>(entry.Key, definition);
        }

        private static void Merge(
            IDictionary<string, (ConvaiClaudeConfigScope Scope, JObject Entry)> effective,
            IEnumerable<KeyValuePair<string, JObject>> servers,
            ConvaiClaudeConfigScope scope)
        {
            foreach (KeyValuePair<string, JObject> server in servers)
                effective[server.Key] = (scope, server.Value);
        }

        /// <summary>
        ///     Reads <c>command</c> only when it really is a string. A malformed definition — an
        ///     array or object where a string belongs — must not throw: one unrelated server's bad
        ///     entry would otherwise escape <see cref="Evaluate" /> and interrupt the setup UI's
        ///     status refresh, hiding a perfectly valid Unity registration. Non-string values are
        ///     skipped, the same way <see cref="ReadArguments" /> drops non-string arguments.
        /// </summary>
        private static string ReadCommand(JObject entry) =>
            entry?["command"] is { Type: JTokenType.String } command ? command.Value<string>() : null;

        /// <summary>
        ///     Newtonsoft's parse message quotes the offending token, which can be a configuration
        ///     value, so only the location is reported. <see cref="JsonReaderException.Path" /> is
        ///     left out for the same reason: it names configuration keys.
        /// </summary>
        private static string DescribeParseLocation(JsonException exception) =>
            exception is JsonReaderException reader && reader.LineNumber > 0
                ? $" at line {reader.LineNumber}, position {reader.LinePosition}"
                : string.Empty;

        private static IReadOnlyList<string> ReadArguments(JToken args) =>
            args is JArray array
                ? array.Select(value => value.Type == JTokenType.String ? value.Value<string>() : null).ToArray()
                : Array.Empty<string>();
    }
}
