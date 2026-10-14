using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Bounded waits for tests that await SDK operations.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A test runs on Unity's main thread — the editor loop in EditMode, the player loop in
    ///         Play Mode — and the test framework blocks that thread until the test's task completes.
    ///         A wait with no deadline therefore has two outcomes, not one: it passes, or it takes the
    ///         whole editor down with it. The second is
    ///         not a test result — the run produces no report, the console stays empty, and the only
    ///         way out is to kill the process, which is how a stalled SDK path costs an editor session
    ///         instead of a red line.
    ///     </para>
    ///     <para>
    ///         Every wait here carries a deadline, so a path that never completes fails the test and
    ///         names itself. Waiting is expressed as <c>Task.WhenAny</c> against a delay rather than
    ///         <c>Assert.ThrowsAsync</c> or <c>Task.Wait</c>, because those block until the awaited
    ///         task completes and have no way to give up.
    ///     </para>
    ///     <para>
    ///         The deadline is a stall detector, not a performance budget: it is set far above any
    ///         legitimate duration so that tripping it means "this never finishes", never "this
    ///         machine was busy".
    ///     </para>
    /// </remarks>
    public static class AsyncTestDeadline
    {
        /// <summary>Long enough that only a stalled path trips it.</summary>
        public const int DefaultMilliseconds = 5000;

        /// <summary>
        ///     Awaits <paramref name="task" /> and fails the test if it has not completed within
        ///     <paramref name="milliseconds" />.
        /// </summary>
        /// <param name="task">The operation under test.</param>
        /// <param name="because">What the completion would have proven; reported when the wait trips.</param>
        /// <param name="milliseconds">The stall deadline.</param>
        public static async Task WithinAsync(Task task, string because,
            int milliseconds = DefaultMilliseconds)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            await ReachedCompletionAsync(task, because, milliseconds);
            await task;
        }

        /// <summary>
        ///     Awaits <paramref name="task" />, requiring it to end in <typeparamref name="TException" />
        ///     within <paramref name="milliseconds" />.
        /// </summary>
        /// <remarks>
        ///     Matching is by assignability, deliberately. Awaiting a task that was cancelled through
        ///     its own token surfaces <see cref="TaskCanceledException" />, so an exact-type
        ///     expectation of <see cref="OperationCanceledException" /> — what
        ///     <c>Assert.ThrowsAsync</c> enforces — fails on correct behaviour.
        /// </remarks>
        /// <returns>The exception that ended the task, for further assertions.</returns>
        public static async Task<TException> ThrowsWithinAsync<TException>(Task task, string because,
            int milliseconds = DefaultMilliseconds)
            where TException : Exception
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            await ReachedCompletionAsync(task, because, milliseconds);

            try
            {
                await task;
            }
            catch (TException expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                Assert.Fail($"{because}\nExpected an exception assignable to " +
                            $"{typeof(TException).Name} but the operation threw {other.GetType().Name}: " +
                            $"{other.Message}");
            }

            Assert.Fail($"{because}\nExpected an exception assignable to {typeof(TException).Name} " +
                        "but the operation completed successfully.");
            return null;
        }

        private static async Task ReachedCompletionAsync(Task task, string because, int milliseconds)
        {
            if (task.IsCompleted) return;

            Task reached = await Task.WhenAny(task, Task.Delay(milliseconds));
            if (reached == task) return;

            Assert.Fail($"{because}\nThe operation had not completed after {milliseconds} ms, so it " +
                        "is treated as stalled. An unbounded wait here would have hung the editor " +
                        "instead of failing.");
        }
    }
}
