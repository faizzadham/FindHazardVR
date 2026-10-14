using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps a stalled asynchronous path costing a red test rather than the whole editor session.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This guard exists because of a real failure. An EditMode test blocked the main thread
    ///         in <c>Assert.ThrowsAsync</c> waiting for an observer whose timeout was a ten
    ///         millisecond <c>Task.Delay</c>. The measurement that explains it:
    ///         <c>SynchronizationContext.Current</c> inside an EditMode test — synchronous or async —
    ///         is <c>UnityEngine.UnitySynchronizationContext</c>, and a continuation posted to it runs
    ///         only when the editor loop pumps. Blocking the main thread stops that pump, so the
    ///         continuation the block is waiting for can never arrive. The editor hung with zero CPU,
    ///         produced no report, named no test, and had to be killed.
    ///     </para>
    ///     <para>
    ///         An <c>await</c> in the test's own body is safe, because the test framework returns to
    ///         the editor loop between resumptions — which is why hundreds of tests await
    ///         <c>Task.Delay</c> without trouble. What is not safe is a <em>nested</em> blocking wait
    ///         on a task that is already in flight: whether it returns is decided by whoever completes
    ///         it, and if that completion is posted rather than inline, the wait never ends.
    ///     </para>
    ///     <para>
    ///         So the rule is narrow on purpose. Asserting on a call made right there
    ///         (<c>Assert.ThrowsAsync&lt;T&gt;(() =&gt; Api.RejectAsync(...))</c>) stays allowed — a
    ///         guard clause throws on the calling thread. Blocking on a task handed over from an
    ///         earlier line does not, and must go through
    ///         <see cref="Fixtures.AsyncTestDeadline" />, which waits with a deadline.
    ///     </para>
    /// </remarks>
    public sealed class TestBlockingWaitGuardTests
    {
        /// <summary>
        ///     <c>Assert.ThrowsAsync&lt;T&gt;(async () =&gt; await pending)</c> — a blocking wait on a
        ///     task some earlier line started. The awaited expression may be a member path
        ///     (<c>await observation.Completion</c>): that shape hung the editor once, unnoticed,
        ///     because the pattern only matched a bare identifier.
        /// </summary>
        private static readonly Regex _blocksOnInFlightTask = new(
            @"Assert\.(?:Throws|Catch)Async<[^>]+>\(\s*async\s*\(\)\s*=>\s*await\s+[A-Za-z_][\w.]*\s*\)",
            RegexOptions.Compiled);

        /// <summary>
        ///     Awaiting a task cancelled through its own token surfaces <c>TaskCanceledException</c>,
        ///     and <c>Assert.ThrowsAsync</c> matches the exact type — so this shape fails on correct
        ///     behaviour. Cancellation is asserted with <c>Assert.CatchAsync</c>, or with
        ///     <see cref="Fixtures.AsyncTestDeadline.ThrowsWithinAsync{TException}" />, both of which
        ///     match by assignability.
        /// </summary>
        private static readonly Regex _exactTypeCancellationAssertion = new(
            @"Assert\.ThrowsAsync<\s*(?:System\.)?(?:Operation|Task)Canceled(?:Exception)\s*>",
            RegexOptions.Compiled);

        /// <summary>
        ///     The package's own test sources — both suites, because Play Mode blocks the player loop
        ///     the same way EditMode blocks the editor's.
        /// </summary>
        /// <remarks>
        ///     Resolved through the package manager rather than assembled from
        ///     <c>Application.dataPath</c>: these tests ship, so in a consumer's project this
        ///     assembly lives under the package cache, not under <c>Packages/</c>.
        /// </remarks>
        private static string TestRoot
        {
            get
            {
                PackageInfo package = PackageInfo.FindForAssembly(
                    typeof(TestBlockingWaitGuardTests).Assembly);
                return package == null
                    ? null
                    : Path.GetFullPath(Path.Combine(package.resolvedPath, "Tests"));
            }
        }

        [Test]
        [Category("Architecture")]
        public void Tests_DoNotBlockOnAnInFlightTask()
        {
            IReadOnlyList<string> violations = Scan(_blocksOnInFlightTask);

            Assert.IsEmpty(violations,
                "A test must not block the main thread on a task that is already running. " +
                "Whether that wait ever returns depends on how the task completes: an inline " +
                "completion passes, a posted continuation needs the editor loop that the block is " +
                "holding, and then the editor hangs with no report and no named test. Await it with a " +
                "deadline instead — AsyncTestDeadline.ThrowsWithinAsync / WithinAsync:\n" +
                string.Join(Environment.NewLine, violations));
        }

        [Test]
        [Category("Architecture")]
        public void CancellationAssertions_MatchBySubtype()
        {
            IReadOnlyList<string> violations = Scan(_exactTypeCancellationAssertion);

            Assert.IsEmpty(violations,
                "Assert.ThrowsAsync matches the exception type exactly, and awaiting a task cancelled " +
                "through its own token yields TaskCanceledException — so expecting " +
                "OperationCanceledException exactly fails on correct behaviour, and expecting " +
                "TaskCanceledException exactly fails as soon as the path throws a plain cancellation. " +
                "Use Assert.CatchAsync or AsyncTestDeadline.ThrowsWithinAsync, which match by " +
                "assignability:\n" + string.Join(Environment.NewLine, violations));
        }

        private static IReadOnlyList<string> Scan(Regex pattern)
        {
            string root = TestRoot;
            if (root == null || !Directory.Exists(root))
            {
                // A consumer can install the package without its sources on disk in a readable
                // layout. There is nothing to scan, and a source rule has nothing to say about an
                // installation — failing here would report a defect that does not exist.
                Assert.Ignore("Package test sources are not available in this installation.");
            }

            var violations = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root, "*.cs",
                         SearchOption.AllDirectories))
            {
                // Through PackageFiles, because a path past the Windows limit would turn this
                // guard into a crash instead of a verdict — see that type's remarks.
                string source = PackageFiles.ReadAllText(file);
                foreach (Match match in pattern.Matches(source))
                {
                    int line = CountLines(source, match.Index);
                    violations.Add($"{Path.GetFileName(file)}:{line}");
                }
            }

            return violations;
        }

        private static int CountLines(string source, int index)
        {
            int line = 1;
            for (int i = 0; i < index; i++)
                if (source[i] == '\n')
                    line++;
            return line;
        }
    }
}
