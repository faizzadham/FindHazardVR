using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps player code off the timer and thread-pool primitives that never complete in a WebGL player.
    /// </summary>
    /// <remarks>
    ///     Measured in a Unity 6000.6 WebGL player over 20 seconds: <c>Task.Delay</c>, a timeout
    ///     <c>CancellationTokenSource</c>, <c>CancelAfter</c>, <c>System.Threading.Timer</c>, a contended
    ///     <c>SemaphoreSlim.WaitAsync</c>, a <c>ContinueWith</c> on the default scheduler, <c>Task.Run</c>,
    ///     <c>ThreadPool.QueueUserWorkItem</c>, and a <c>Task.WhenAny</c> over a task from a
    ///     <c>RunContinuationsAsynchronously</c> source never ran. Player code uses <c>ConvaiTimers</c>, which passes
    ///     through to those calls on native platforms. Forms measured as working stay allowed:
    ///     <c>Task.Delay(Timeout.Infinite, token)</c>, <c>WaitAsync(0)</c>, and a default-scheduler
    ///     <c>ContinueWith</c> with <c>ExecuteSynchronously</c>. Editor code and the native transport never run
    ///     in a WebGL player, so they are not scanned.
    /// </remarks>
    public sealed class WebGLTimerGuardTests
    {
        private static readonly string[] _scannedRoots =
            { "SDK", "Samples", "SamplesShared", "Plugins/ConvaiRestAPI", "Plugins/ConvaiApi" };

        private static readonly (Regex Pattern, string Use)[] _rules =
        {
            (new Regex(@"\bTask\.Delay\((?!\s*Timeout\.Infinite\b)|\bTask\.Delay\b(?!\s*\()", RegexOptions.Compiled),
                "ConvaiTimers.Delay"),
            (new Regex(@"(?<!ConvaiTimers)\.CancelAfter\(", RegexOptions.Compiled),
                "ConvaiTimers.CancelAfter"),
            (new Regex(@"\bnew\s+CancellationTokenSource\(\s*[^)\s]|\bCancellationTokenSource\s+\w+\s*=\s*new\s*\(\s*[^)\s]",
                    RegexOptions.Compiled),
                "ConvaiTimers.CreateTimeoutSource"),
            (new Regex(@"\bnew\s+(System\.Threading\.)?Timer\(|\bSystem\.Timers\.Timer\b", RegexOptions.Compiled),
                "ConvaiTimers.Delay with a cancellation token"),
            (new Regex(@"(?<!ConvaiTimers)\.WaitAsync\((?!\s*0\s*\))", RegexOptions.Compiled),
                "ConvaiTimers.WaitAsync(gate, token)"),
            (new Regex(@"\bTask\.Run\(|\bTask\.Factory\.StartNew\(", RegexOptions.Compiled),
                "the calling thread, or UnityScheduler"),
            (new Regex(@"\bTask\.WhenAny\(", RegexOptions.Compiled),
                "ConvaiTimers.WhenAny"),
            (new Regex(@"\bThreadPool\.(Unsafe)?QueueUserWorkItem\(", RegexOptions.Compiled),
                "a ConvaiTimers.UsePlayerLoop branch that stays on the main thread")
        };

        private static readonly Regex _defaultScheduler =
            new(@"\bTaskScheduler\.Default\b", RegexOptions.Compiled);

        // Files whose thread-pool call sits behind a ConvaiTimers.UsePlayerLoop branch.
        private static readonly HashSet<string> _threadPoolAllowed = new(StringComparer.Ordinal)
        {
            "SDK/Runtime/Bootstrap/UnityScheduler.cs",
            "SDK/Runtime/Logging/ConvaiLogger.cs"
        };

        private static readonly HashSet<string> _helperFiles = new(StringComparer.Ordinal)
        {
            "SDK/Runtime/Core/Async/ConvaiTimers.cs",
            "SDK/Runtime/Core/Async/PlayerLoopTimerQueue.cs"
        };

        [Test]
        [Category("Architecture")]
        public void PlayerCode_DoesNotWaitOnTimersOrTheThreadPool()
        {
            string packageRoot = PackageRoot;
            if (packageRoot == null || !Directory.Exists(Path.Combine(packageRoot, "SDK")))
                Assert.Ignore("Package sources are not available in this installation.");

            var violations = new List<string>();
            int scannedFiles = 0;
            foreach (string root in _scannedRoots)
            {
                string directory = Path.Combine(packageRoot, root);
                if (!Directory.Exists(directory))
                    continue;

                foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                    if (IsExcluded(relativePath))
                        continue;

                    scannedFiles++;
                    violations.AddRange(FindViolations(relativePath, PackageFiles.ReadAllLines(file)));
                }
            }

            Assert.That(scannedFiles, Is.GreaterThan(0), "The guard examined no player source files.");
            Assert.IsEmpty(violations,
                "Player code must not wait on a timer or the thread pool: a WebGL player runs neither, so the " +
                "wait never finishes. Use the ConvaiTimers helper named on each line:\n" +
                string.Join(Environment.NewLine, violations));
        }

        [Test]
        [Category("Architecture")]
        public void Guard_FlagsEachDeadPrimitive_AndAllowsTheFormsThatWork()
        {
            string[] dead =
            {
                "await Task.Delay(200, ct);",
                "cts.CancelAfter(10000);",
                "using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));",
                "_timer = new Timer(_ => Fire(), null, 200, Timeout.Infinite);",
                "_delay = delay ?? Task.Delay;",
                "CancellationTokenSource cts = new(15000);",
                "var timer = new System.Timers.Timer(200);",
                "await Task.WhenAny(work, Task.Delay(timeoutMs, token));",
                "Task completed = await Task.WhenAny(ready.Task, cancelled.Task);",
                "await _gate.WaitAsync(ct);",
                "await _gate.WaitAsync();",
                "_ = Task.Run(() => Work());",
                "ThreadPool.QueueUserWorkItem(_ => Work());",
                "    TaskContinuationOptions.OnlyOnFaulted,\n    TaskScheduler.Default);"
            };
            foreach (string source in dead)
                Assert.IsNotEmpty(FindViolations("SDK/Runtime/Sample.cs", source.Split('\n')), source);

            string[] allowed =
            {
                "await Task.Delay(Timeout.Infinite, token);",
                "await ConvaiTimers.Delay(200, ct);",
                "ConvaiTimers.CancelAfter(cts, 10000);",
                "using var cts = new CancellationTokenSource();",
                "using var cts = ConvaiTimers.CreateTimeoutSource(15000);",
                "if (!await _gate.WaitAsync(0)) return;",
                "await ConvaiTimers.WaitAsync(_gate, ct);",
                "Task completed = await ConvaiTimers.WhenAny(ready.Task, ConvaiTimers.Delay(200, ct));",
                "    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,\n    TaskScheduler.Default);",
                "// await Task.Delay(200);"
            };
            foreach (string source in allowed)
                Assert.IsEmpty(FindViolations("SDK/Runtime/Sample.cs", source.Split('\n')), source);
        }

        private static IEnumerable<string> FindViolations(string relativePath, string[] lines)
        {
            bool threadPoolAllowed = _threadPoolAllowed.Contains(relativePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;

                foreach ((Regex pattern, string use) in _rules)
                {
                    if (threadPoolAllowed && use.StartsWith("a ConvaiTimers.UsePlayerLoop", StringComparison.Ordinal))
                        continue;
                    if (pattern.IsMatch(line))
                        yield return $"{relativePath}:{i + 1} -> {use}";
                }

                if (_defaultScheduler.IsMatch(line) && !MentionsExecuteSynchronously(lines, i))
                    yield return $"{relativePath}:{i + 1} -> TaskContinuationOptions.ExecuteSynchronously";
            }
        }

        private static bool MentionsExecuteSynchronously(string[] lines, int index)
        {
            for (int i = Math.Max(0, index - 3); i <= index; i++)
            {
                if (lines[i].Contains("ExecuteSynchronously", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool IsExcluded(string relativePath) =>
            relativePath.Contains("/Editor/", StringComparison.Ordinal)
            || relativePath.StartsWith("SDK/Infrastructure/Networking/Native/", StringComparison.Ordinal)
            || (relativePath.StartsWith("Plugins/", StringComparison.Ordinal)
                && Path.GetFileName(relativePath).StartsWith("HttpClient", StringComparison.Ordinal))
            || _helperFiles.Contains(relativePath);

        private static string PackageRoot
        {
            get
            {
                PackageInfo package = PackageInfo.FindForAssembly(typeof(WebGLTimerGuardTests).Assembly);
                return package == null ? null : Path.GetFullPath(package.resolvedPath);
            }
        }
    }
}
