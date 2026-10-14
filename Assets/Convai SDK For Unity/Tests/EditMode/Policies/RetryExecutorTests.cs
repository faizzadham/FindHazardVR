using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Policies;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Policies
{
    /// <summary>
    ///     Minimal unit tests for RetryExecutor - 5 key scenarios only.
    /// </summary>
    [TestFixture]
    public class RetryExecutorTests
    {
        private RetryExecutor _executor;

        [SetUp]
        public void SetUp()
        {
            // Use a zero-delay policy in unit tests so EditMode never blocks on real backoff waits.
            _executor = new RetryExecutor(new ImmediateRetryPolicy());
        }

        [Test]
        public async Task ExecuteAsync_SucceedsOnFirstAttempt_ReturnsResult()
        {
            int result = await _executor.ExecuteAsync(
                (_, _) => Task.FromResult(42),
                CancellationToken.None);

            Assert.AreEqual(42, result);
        }

        [Test]
        public async Task ExecuteAsync_FailsThenSucceeds_RetriesAndReturns()
        {
            int attemptCount = 0;

            int result = await _executor.ExecuteAsync(
                (attempt, _) =>
                {
                    attemptCount++;
                    if (attempt < 2)
                        throw new TimeoutException("Simulated timeout");
                    return Task.FromResult(99);
                },
                CancellationToken.None);

            Assert.AreEqual(99, result);
            Assert.AreEqual(3, attemptCount);
        }

        [Test]
        public async Task ExecuteAsync_ExhaustsRetries_ThrowsLastException()
        {
            TimeoutException ex = await CaptureAsync<TimeoutException>(() =>
                _executor.ExecuteAsync<int>(
                    (attempt, _) => throw new TimeoutException($"Attempt {attempt}"),
                    CancellationToken.None));

            Assert.That(ex.Message, Does.Contain("Attempt 3"));
        }

        [Test]
        public async Task ExecuteAsync_NonTransientError_DoesNotRetry()
        {
            int attemptCount = 0;

            await CaptureAsync<ArgumentException>(() =>
                _executor.ExecuteAsync<int>(
                    (_, _) =>
                    {
                        attemptCount++;
                        throw new ArgumentException("Bad argument");
                    },
                    CancellationToken.None));

            Assert.AreEqual(1, attemptCount);
        }

        [Test]
        public async Task ExecuteAsync_Cancelled_ThrowsOperationCanceled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await CaptureAsync<OperationCanceledException>(() =>
                _executor.ExecuteAsync(
                    (_, _) => Task.FromResult(1),
                    cts.Token));
        }

        private static async Task<TException> CaptureAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException exception)
            {
                return exception;
            }

            Assert.Fail($"Expected exception of type {typeof(TException).Name}.");
            return null;
        }

        private sealed class ImmediateRetryPolicy : IRetryPolicy
        {
            public int MaxAttempts => 4;

            public TimeSpan GetDelay(int attempt) => TimeSpan.Zero;

            public bool ShouldRetry(Exception exception, int attempt)
            {
                if (attempt < 0 || attempt >= MaxAttempts - 1)
                    return false;

            return exception is TimeoutException;
            }
        }
    }
}
