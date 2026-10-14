using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Convai.Editor
{
    internal enum ConvaiInstallationState
    {
        Unknown,
        Checking,
        Detected,
        Installed,
        Disabled,
        NotInstalled,
        NotDetected
    }

    internal readonly struct ConvaiInstallationStatus
    {
        internal ConvaiInstallationStatus(ConvaiInstallationState state, string detail)
        {
            State = state;
            Detail = detail;
        }

        internal ConvaiInstallationState State { get; }
        internal string Detail { get; }
        internal bool IsReady => State is ConvaiInstallationState.Detected or ConvaiInstallationState.Installed;
        internal string Label => State switch
        {
            ConvaiInstallationState.Checking => "Checking…",
            ConvaiInstallationState.Detected => "Detected",
            ConvaiInstallationState.Installed => "Installed",
            ConvaiInstallationState.Disabled => "Disabled",
            ConvaiInstallationState.NotInstalled => "Not installed",
            ConvaiInstallationState.NotDetected => "Not detected",
            _ => "Unable to verify"
        };
    }

    internal readonly struct ConvaiCodexInstallationStatus
    {
        internal ConvaiCodexInstallationStatus(ConvaiInstallationStatus desktop, ConvaiInstallationStatus plugin)
        {
            Desktop = desktop;
            Plugin = plugin;
        }

        internal ConvaiInstallationStatus Desktop { get; }
        internal ConvaiInstallationStatus Plugin { get; }
    }

    /// <summary>Read-only local app detection and a bounded CLI plugin query, performed off the Editor thread.</summary>
    internal static class ConvaiCodexInstallationProbe
    {
        internal const string UnityPluginId = "plugins_6aa1c02597c081918e358d72f65bd772";
        private const int TimeoutMilliseconds = 10000;
        private static Task<ConvaiCodexInstallationStatus> _pending;
        private static ConvaiCodexInstallationStatus _status;
        private static bool _started;

        internal static bool IsPending => _pending != null;

        internal static ConvaiCodexInstallationStatus Status
        {
            get
            {
                if (_pending != null && _pending.IsCompleted)
                {
                    _status = _pending.Status == TaskStatus.RanToCompletion
                        ? _pending.Result
                        : new ConvaiCodexInstallationStatus(Unknown("Local app inspection failed."), Unknown("Plugin inspection failed."));
                    _pending = null;
                }
                return _status;
            }
        }

        /// <summary>Starts the first inspection; later results stay cached until an explicit <see cref="Refresh" />.</summary>
        internal static void EnsureStarted(string projectRoot)
        {
            if (!_started) Refresh(projectRoot);
        }

        internal static void Refresh(string projectRoot)
        {
            if (_pending != null && !_pending.IsCompleted) return;
            _started = true;
            var checking = new ConvaiInstallationStatus(ConvaiInstallationState.Checking, "Checking local installation. No configuration is changed.");
            _status = new ConvaiCodexInstallationStatus(checking, checking);
            _pending = Task.Run(() => Inspect(projectRoot));
        }

        private static ConvaiCodexInstallationStatus Inspect(string projectRoot)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                bool windows = Path.DirectorySeparatorChar == '\\';
                var bundledExecutables = new List<string>();
                ConvaiInstallationStatus desktop;
                if (windows)
                    desktop = InspectWindowsDesktop(bundledExecutables);
                else if (Directory.Exists("/Applications"))
                {
                    string[] apps = { "/Applications/ChatGPT.app", "/Applications/Codex.app",
                        Path.Combine(home, "Applications/ChatGPT.app"), Path.Combine(home, "Applications/Codex.app") };
                    string[] found = apps.Where(path => File.Exists(Path.Combine(path, "Contents/Info.plist")) &&
                        (File.Exists(Path.Combine(path, "Contents/MacOS/ChatGPT")) ||
                         File.Exists(Path.Combine(path, "Contents/MacOS/Codex")))).ToArray();
                    bundledExecutables.AddRange(found.Select(path => Path.Combine(path, "Contents/Resources/codex")));
                    desktop = found.Length > 0
                        ? new ConvaiInstallationStatus(ConvaiInstallationState.Detected, string.Join("\n", found))
                        : new ConvaiInstallationStatus(ConvaiInstallationState.NotDetected,
                            "No ChatGPT or Codex app found in /Applications or ~/Applications. A custom installation may still exist.");
                }
                else
                    desktop = Unknown("Automatic desktop-app detection is available for standard macOS installs and Windows URL registrations. Check the Plugins tab in the desktop app manually.");

                string executable = FindExecutable(bundledExecutables, home, windows);
                ConvaiInstallationStatus plugin = executable == null
                    ? Unknown("No native Codex CLI found. Check the Plugins tab in the desktop app; a missing CLI does not mean the plugin is missing.")
                    : QueryPlugin(executable, projectRoot);
                return new ConvaiCodexInstallationStatus(desktop, plugin);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new ConvaiCodexInstallationStatus(Unknown("Local installation locations could not be read."),
                    Unknown("Check Unity in the desktop app’s Plugins tab."));
            }
        }

        private static ConvaiInstallationStatus InspectWindowsDesktop(List<string> bundledExecutables) =>
            InspectWindowsDesktop(bundledExecutables, ReadWindowsRegisteredCommand);

        private static string ReadWindowsRegisteredCommand(string protocol)
        {
            using RegistryKey key = Registry.ClassesRoot.OpenSubKey(protocol + @"\shell\open\command");
            return key?.GetValue(null) as string;
        }

        internal static ConvaiInstallationStatus InspectWindowsDesktop(
            List<string> bundledExecutables, Func<string, string> readRegisteredCommand)
        {
            var detectedApps = new List<string>();
            foreach (string protocol in new[] { "codex", "chatgpt" })
            {
                string executable = ExtractRegisteredExecutable(readRegisteredCommand(protocol));
                if (executable == null || !File.Exists(executable)) continue;
                string directory = Path.GetDirectoryName(executable);
                bundledExecutables.Add(Path.Combine(directory, "resources", "codex.exe"));
                detectedApps.Add("Desktop app registered for " + protocol + " links: " + executable);
            }
            if (detectedApps.Count > 0)
                return new ConvaiInstallationStatus(ConvaiInstallationState.Detected, string.Join("\n", detectedApps));
            return new ConvaiInstallationStatus(ConvaiInstallationState.NotDetected,
                "No installed app was found through the Codex or ChatGPT URL registrations. An unregistered installation may still exist.");
        }

        internal static string ExtractRegisteredExecutable(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            if (!command.StartsWith("\"", StringComparison.Ordinal)) return null;
            int closingQuote = command.IndexOf('"', 1);
            if (closingQuote <= 1) return null;
            string path = command.Substring(1, closingQuote - 1);
            string name = Path.GetFileName(path);
            return name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        internal static string FindExecutable(IEnumerable<string> bundled, string home, bool windows)
        {
            // Prefer the desktop app's CLI: older standalone CLIs may not list remote plugins.
            var candidates = new List<string>(bundled);
            string name = windows ? "codex.exe" : "codex";
            if (!windows)
            {
                candidates.Add(Path.Combine(home, ".local/bin/codex"));
                candidates.Add("/opt/homebrew/bin/codex");
                candidates.Add("/usr/local/bin/codex");
            }
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                // Windows PATH entries are often quoted; entries with other invalid characters are skipped.
                string directory = entry.Trim().Trim('"');
                try
                {
                    if (directory.Length > 0 && Path.IsPathRooted(directory))
                        candidates.Add(Path.Combine(directory, name));
                }
                catch (ArgumentException) { }
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        private static ConvaiInstallationStatus QueryPlugin(string executable, string projectRoot)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "plugin list --json --available --marketplace openai-curated-remote",
                        WorkingDirectory = projectRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeoutMilliseconds))
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                    return Unknown("Codex plugin inspection timed out. Refresh to retry or check the desktop app’s Plugins tab.");
                }
                if (!Task.WaitAll(new Task[] { output, errors }, 1000) || process.ExitCode != 0)
                    return Unknown("Codex could not list remote plugins. Update the desktop app or check the plugin there. No configuration was changed.");
                // Never expose raw CLI output, which may include unrelated plugin/account details.
                return ParsePluginList(output.Result);
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or AggregateException)
            {
                return Unknown("Codex plugin inspection was unavailable. Check the Plugins tab in the desktop app.");
            }
        }

        internal static ConvaiInstallationStatus ParsePluginList(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                if (!(root["installed"] is JArray installed) || !(root["available"] is JArray available))
                    return Unknown("The Codex CLI returned an unsupported plugin-list format.");
                JObject[] matches = installed.Concat(available).OfType<JObject>()
                    .Where(plugin => plugin["source"] is JObject source &&
                                     source["source"]?.Type == JTokenType.String && source["id"]?.Type == JTokenType.String &&
                                     (string)source["source"] == "remote" && (string)source["id"] == UnityPluginId)
                    .ToArray();
                if (matches.Length != 1)
                    return Unknown("Unity was not uniquely reported by the remote plugin catalog. This does not prove it is uninstalled; check in the desktop app.");
                JObject match = matches[0];
                if (match["installed"]?.Type != JTokenType.Boolean)
                    return Unknown("The plugin's installation state was not reported.");
                if (!(bool)match["installed"])
                    return new ConvaiInstallationStatus(ConvaiInstallationState.NotInstalled, "The Codex remote plugin catalog reports Unity is not installed. Add Unity from the desktop app’s Plugins tab.");
                if (match["enabled"]?.Type != JTokenType.Boolean)
                    return Unknown("Unity is installed, but the CLI did not report whether it is enabled. Check in the desktop app.");
                return (bool)match["enabled"]
                    ? new ConvaiInstallationStatus(ConvaiInstallationState.Installed, "Unity is installed and enabled in the Codex plugin catalog. This does not verify the Unity MCP connection.")
                    : new ConvaiInstallationStatus(ConvaiInstallationState.Disabled, "Unity is installed but disabled. Enable Unity in the desktop app’s Plugins tab.");
            }
            catch (JsonException)
            {
                return Unknown("The Codex CLI returned an unsupported plugin-list format.");
            }
        }

        private static ConvaiInstallationStatus Unknown(string detail) => new(ConvaiInstallationState.Unknown, detail);
    }
}
