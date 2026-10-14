using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps player code from resuming an <c>await</c> on a thread pool that a WebGL player does not run.
    /// </summary>
    /// <remarks>
    ///     A WebGL player never executes thread-pool work. A <c>ConfigureAwait(false)</c> continuation on a
    ///     task completed on the main thread is queued there and lost, which hung every WebGL microphone
    ///     publish with its locks held. Player code passes <c>ConvaiAwait.ResumeOnCapturedContext</c>, which
    ///     is <c>false</c> everywhere except a WebGL player; the REST and character API plugins, which cannot
    ///     reference it, pass <c>ApiAwait.ResumeOnCapturedContext</c>. Editor code, the native transport and the
    ///     HttpClient API transports never run in a WebGL player, so they are not scanned.
    /// </remarks>
    public sealed class WebGLContinuationGuardTests
    {
        private static readonly Regex _rawConfigureAwaitFalse =
            new(@"\.ConfigureAwait\(\s*false\s*\)", RegexOptions.Compiled);

        private static readonly string[] _scannedRoots =
            { "SDK", "Samples", "SamplesShared", "Plugins/ConvaiRestAPI", "Plugins/ConvaiApi" };

        [Test]
        [Category("Architecture")]
        public void PlayerCode_DoesNotResumeOnTheThreadPool()
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
                    if (IsExcluded(Path.GetRelativePath(packageRoot, file)))
                        continue;

                    scannedFiles++;
                    string[] lines = PackageFiles.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                            continue;
                        if (_rawConfigureAwaitFalse.IsMatch(lines[i]))
                            violations.Add($"{Path.GetRelativePath(packageRoot, file)}:{i + 1}");
                    }
                }
            }

            Assert.That(scannedFiles, Is.GreaterThan(0), "The guard examined no player source files.");
            Assert.IsEmpty(violations,
                "Player code must not use ConfigureAwait(false): a WebGL player never runs the thread " +
                "pool, so the continuation is lost and the awaiting operation never finishes. Use " +
                "ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext):\n" +
                string.Join(Environment.NewLine, violations));
        }

        private static bool IsExcluded(string relativePath)
        {
            string path = relativePath.Replace('\\', '/');
            return path.Contains("/Editor/", StringComparison.Ordinal)
                   || path.StartsWith("SDK/Infrastructure/Networking/Native/", StringComparison.Ordinal)
                   || (path.StartsWith("Plugins/", StringComparison.Ordinal)
                       && Path.GetFileName(path).StartsWith("HttpClient", StringComparison.Ordinal));
        }

        private static string PackageRoot
        {
            get
            {
                PackageInfo package = PackageInfo.FindForAssembly(typeof(WebGLContinuationGuardTests).Assembly);
                return package == null ? null : Path.GetFullPath(package.resolvedPath);
            }
        }
    }
}
