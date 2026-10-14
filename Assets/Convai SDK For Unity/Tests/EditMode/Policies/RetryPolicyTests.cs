using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Convai.Runtime.Core.Policies;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Policies
{
    [TestFixture]
    public sealed class RetryPolicyTests
    {
        private ExponentialBackoffPolicy _policy;

        private static IEnumerable<TestCaseData> ExceptionCases()
        {
            yield return Case(new TimeoutException(), true);
            yield return Case(new HttpRequestException(), true);
            yield return Case(new OperationCanceledException(), true);
            yield return Case(new TaskCanceledException(), true);
            yield return Case(new ArgumentException(), false);
            yield return Case(new InvalidOperationException(), false);
            yield return Case(new NullReferenceException(), false);
            yield return Case(null, false);
            yield return Case(new Exception("wrapper", new TimeoutException()), true);
            yield return Case(new AggregateException(new TimeoutException()), true);
            yield return Case(new AggregateException(new ArgumentException(), new TimeoutException()), true);
            yield return Case(new AggregateException(new ArgumentException(), new InvalidOperationException()), false);
            yield return Case(new Exception("outer", new Exception("inner", new TimeoutException())), true);
        }

        [SetUp]
        public void SetUp() => _policy = new ExponentialBackoffPolicy();

        [Test]
        public void Defaults_DefineFourAttemptExponentialBackoff()
        {
            Assert.That(_policy.MaxAttempts, Is.EqualTo(4));
            Assert.That(_policy, Is.InstanceOf<IRetryPolicy>());
        }

        [TestCase(-1, 0)]
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(3, 4)]
        [TestCase(10, 4)]
        public void GetDelay_ClampsExponentialSchedule(int attempt, int expectedSeconds) =>
            Assert.That(_policy.GetDelay(attempt), Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));

        [TestCaseSource(nameof(ExceptionCases))]
        public void ShouldRetry_ClassifiesExceptionGraph(Exception exception, bool expected) =>
            Assert.That(_policy.ShouldRetry(exception, 0), Is.EqualTo(expected));

        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        [TestCase(10, false)]
        public void ShouldRetry_EnforcesAttemptBounds(int attempt, bool expected) =>
            Assert.That(_policy.ShouldRetry(new TimeoutException(), attempt), Is.EqualTo(expected));

        private static TestCaseData Case(Exception exception, bool expected) => new(exception, expected);
    }
}
