using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Editor.Inspectors;
using Convai.Api.Transport;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    public sealed class ConvaiCharacterVersionDropdownStateTests
    {
        private const string CharacterId = "00000000-0000-4000-8000-000000000001";
        [Test]
        public void InitialState_HasNoVersionsOrPendingLoad()
        {
            using var state = new ConvaiCharacterVersionDropdownState();

            Assert.That(state.Versions, Is.Empty);
            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.HasLoaded, Is.False);
            Assert.That(state.Error, Is.Null.Or.Empty);
            Assert.That(state.Availability, Is.Null);
            Assert.That(state.CurrentReference, Is.Null);
        }

        [Test]
        public async Task SetContextAsync_PublishesCompletedVersionsAndRepaints()
        {
            using var fetch = new ControlledFetch();
            int repaints = 0;
            using var state = new ConvaiCharacterVersionDropdownState(() => repaints++);
            var context = new object();

            Task load = state.SetContextAsync(context, fetch.FetchAsync);

            Assert.That(state.IsCurrentContext(context), Is.True);
            Assert.That(state.IsLoading, Is.True);
            Assert.That(state.HasLoaded, Is.False);
            Assert.That(fetch.Requests.Count, Is.EqualTo(1));
            Assert.That(fetch.Requests[0].Token.CanBeCanceled, Is.True);
            int repaintsBeforeCompletion = repaints;
            fetch.Requests[0].Complete("2.0", "1.2.3");
            await Completed(load);

            CollectionAssert.AreEqual(new[] { "2.0", "1.2.3" }, state.Versions.Select(item => item.Version));
            Assert.That(state.Availability.CharacterId, Is.EqualTo(CharacterId));
            Assert.That(state.CurrentReference, Is.EqualTo(CharacterId));
            Assert.That(state.Versions[0].Reference, Is.EqualTo(CharacterId + "-api-ref-0"));
            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.HasLoaded, Is.True);
            Assert.That(state.Error, Is.Null.Or.Empty);
            Assert.That(repaints, Is.GreaterThan(repaintsBeforeCompletion));
        }

        [Test]
        public async Task EqualValueContexts_DoNotRefetchWhileLoadingOrAfterCompletion()
        {
            using var fetch = new ControlledFetch();
            using var state = new ConvaiCharacterVersionDropdownState();
            object context = ("character", "credential-scope", "environment");
            object equalContext = ("character", "credential-scope", "environment");

            Task first = state.SetContextAsync(context, fetch.FetchAsync);
            int generation = state.Generation;
            Task duplicate = state.SetContextAsync(equalContext, fetch.FetchAsync);

            Assert.That(fetch.Requests.Count, Is.EqualTo(1));
            Assert.That(state.Generation, Is.EqualTo(generation));
            Assert.That(state.IsCurrentContext(equalContext), Is.True);
            fetch.Requests[0].Complete("1.0");
            await Completed(Task.WhenAll(first, duplicate));
            await Completed(state.SetContextAsync(equalContext, fetch.FetchAsync));

            Assert.That(fetch.Requests.Count, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { "1.0" }, state.Versions.Select(item => item.Version));
        }

        [Test]
        public async Task RefreshAsync_LoadsAgainButDeduplicatesConcurrentRefreshes()
        {
            using var fetch = new ControlledFetch();
            using var state = new ConvaiCharacterVersionDropdownState();
            Task initial = state.SetContextAsync("scope", fetch.FetchAsync);
            fetch.Requests[0].Complete("1.0");
            await Completed(initial);

            Task refresh = state.RefreshAsync();
            Task duplicate = state.RefreshAsync();

            Assert.That(fetch.Requests.Count, Is.EqualTo(2));
            Assert.That(state.IsLoading, Is.True);
            fetch.Requests[1].Complete("2.0", "1.0");
            await Completed(Task.WhenAll(refresh, duplicate));

            CollectionAssert.AreEqual(new[] { "2.0", "1.0" }, state.Versions.Select(item => item.Version));
            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.HasLoaded, Is.True);
        }

        [Test]
        public async Task FetchFailure_DoesNotRetryOnRepeatedContext_ButExplicitRefreshRecovers()
        {
            using var fetch = new ControlledFetch();
            using var state = new ConvaiCharacterVersionDropdownState();
            Task load = state.SetContextAsync("scope", fetch.FetchAsync);
            fetch.Requests[0].Fail(new InvalidOperationException("Version list unavailable."));
            await Completed(load);

            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.Versions, Is.Empty);
            Assert.That(state.Error, Does.Contain("Cannot load released versions"));
            Assert.That(state.Error, Does.Not.Contain("Version list unavailable"),
                "Arbitrary backend exception details must not leak into inspector feedback.");
            await Completed(state.SetContextAsync("scope", fetch.FetchAsync));
            Assert.That(fetch.Requests.Count, Is.EqualTo(1));

            Task refresh = state.RefreshAsync();
            fetch.Requests[1].Complete("1.0");
            await Completed(refresh);

            CollectionAssert.AreEqual(new[] { "1.0" }, state.Versions.Select(item => item.Version));
            Assert.That(state.Error, Is.Null.Or.Empty);
        }

        [Test]
        public async Task UnavailableContext_DoesNotFetchOrRefresh_AndNextContextCanLoad()
        {
            using var fetch = new ControlledFetch();
            using var state = new ConvaiCharacterVersionDropdownState();
            await Completed(state.SetContextAsync("unavailable", fetch.FetchAsync, "Configure credentials first."));
            await Completed(state.RefreshAsync());

            Assert.That(fetch.Requests, Is.Empty);
            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.Versions, Is.Empty);
            Assert.That(state.Error, Does.Contain("Configure credentials"));

            Task load = state.SetContextAsync("available", fetch.FetchAsync);
            Assert.That(fetch.Requests.Count, Is.EqualTo(1));
            fetch.Requests[0].Complete("1.0");
            await Completed(load);

            Assert.That(state.Error, Is.Null.Or.Empty);
            CollectionAssert.AreEqual(new[] { "1.0" }, state.Versions.Select(item => item.Version));
        }

        [TestCase(400, "reference is invalid")]
        [TestCase(401, "authentication")]
        [TestCase(403, "entitlement")]
        [TestCase(404, "not visible")]
        public async Task ApiFailures_ShowDistinctActionableFeedback_WithoutRawResponseDetails(int status, string expected)
        {
            using var fetch = new ControlledFetch();
            using var state = new ConvaiCharacterVersionDropdownState();
            Task load = state.SetContextAsync("scope", fetch.FetchAsync);
            fetch.Requests[0].Fail(new ConvaiApiException("raw-backend-detail", ConvaiApiErrorCategory.Contract,
                statusCode: status, responseBody: "raw-response-body"));
            await Completed(load);

            Assert.That(state.Error, Does.Contain(expected));
            Assert.That(state.Error, Does.Not.Contain("raw-backend-detail"));
            Assert.That(state.Error, Does.Not.Contain("raw-response-body"));
            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.Versions, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ContextSwitch_CancelsPriorLoad_ClearsVersions_AndIgnoresStaleCompletion(bool staleFailure)
        {
            using var fetch = new ControlledFetch();
            using var state = new ConvaiCharacterVersionDropdownState();
            Task initial = state.SetContextAsync("old-scope", fetch.FetchAsync);
            fetch.Requests[0].Complete("1.0");
            await Completed(initial);
            Task oldRefresh = state.RefreshAsync();
            PendingRequest oldRequest = fetch.Requests[1];
            int previousGeneration = state.Generation;

            Task currentLoad = state.SetContextAsync("new-scope", fetch.FetchAsync);

            Assert.That(oldRequest.Token.IsCancellationRequested, Is.True);
            Assert.That(state.Generation, Is.GreaterThan(previousGeneration));
            Assert.That(state.IsCurrentContext("old-scope"), Is.False);
            Assert.That(state.IsCurrentContext("new-scope"), Is.True);
            Assert.That(state.Versions, Is.Empty);
            Assert.That(state.CurrentReference, Is.Null);
            Assert.That(state.HasLoaded, Is.False);
            Assert.That(state.IsLoading, Is.True);
            fetch.Requests[2].Complete("2.0");
            await Completed(currentLoad);

            // The old backend deliberately ignores cancellation and finishes after the new one.
            if (staleFailure) oldRequest.Fail(new InvalidOperationException("Stale backend failure."));
            else oldRequest.Complete("0.1");
            await Completed(oldRefresh);

            CollectionAssert.AreEqual(new[] { "2.0" }, state.Versions.Select(item => item.Version));
            Assert.That(state.CurrentReference, Is.EqualTo(CharacterId));
            Assert.That(state.IsLoading, Is.False);
            Assert.That(state.HasLoaded, Is.True);
            Assert.That(state.Error, Is.Null.Or.Empty);
        }

        [Test]
        public async Task ContextSwitch_CooperativeCancellationDoesNotSurfaceAsNewContextError()
        {
            using var fetch = new ControlledFetch(cancelWithToken: true);
            using var state = new ConvaiCharacterVersionDropdownState();
            Task oldLoad = state.SetContextAsync("old-scope", fetch.FetchAsync);
            Task currentLoad = state.SetContextAsync("new-scope", fetch.FetchAsync);
            fetch.Requests[1].Complete("2.0");
            await Completed(Task.WhenAll(oldLoad, currentLoad));

            Assert.That(fetch.Requests[0].Token.IsCancellationRequested, Is.True);
            CollectionAssert.AreEqual(new[] { "2.0" }, state.Versions.Select(item => item.Version));
            Assert.That(state.Error, Is.Null.Or.Empty);
        }

        [Test]
        public async Task Dispose_CancelsLoad_AndIgnoresLateResultWithoutRepainting()
        {
            using var fetch = new ControlledFetch();
            int repaints = 0;
            using var state = new ConvaiCharacterVersionDropdownState(() => repaints++);
            Task load = state.SetContextAsync("scope", fetch.FetchAsync);

            state.Dispose();

            Assert.That(fetch.Requests[0].Token.IsCancellationRequested, Is.True);
            int repaintsAfterDisposal = repaints;
            bool hasLoadedAfterDisposal = state.HasLoaded;
            bool isLoadingAfterDisposal = state.IsLoading;
            string errorAfterDisposal = state.Error;
            fetch.Requests[0].Complete("1.0");
            await Completed(load);

            Assert.That(state.Versions, Is.Empty);
            Assert.That(state.CurrentReference, Is.Null);
            Assert.That(state.HasLoaded, Is.EqualTo(hasLoadedAfterDisposal));
            Assert.That(state.IsLoading, Is.EqualTo(isLoadingAfterDisposal));
            Assert.That(state.Error, Is.EqualTo(errorAfterDisposal));
            Assert.That(repaints, Is.EqualTo(repaintsAfterDisposal));
        }

        private static Task Completed(Task task) =>
            AsyncTestDeadline.WithinAsync(task, "The controlled version-dropdown operation must complete.");

        private sealed class ControlledFetch : IDisposable
        {
            private readonly bool _cancelWithToken;
            public readonly List<PendingRequest> Requests = new();

            public ControlledFetch(bool cancelWithToken = false) => _cancelWithToken = cancelWithToken;

            public Task<CharacterVersionAvailability> FetchAsync(CancellationToken token)
            {
                var request = new PendingRequest(token, _cancelWithToken);
                Requests.Add(request);
                return request.Task;
            }

            public void Dispose()
            {
                foreach (PendingRequest request in Requests) request.Dispose();
            }
        }

        private sealed class PendingRequest : IDisposable
        {
            private readonly TaskCompletionSource<CharacterVersionAvailability> _source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            public PendingRequest(CancellationToken token, bool cancelWithToken)
            {
                Token = token;
                if (cancelWithToken) _registration = token.Register(() => _source.TrySetCanceled());
            }

            public CancellationToken Token { get; }
            public Task<CharacterVersionAvailability> Task => _source.Task;
            public void Complete(params string[] versions) => _source.TrySetResult(
                new CharacterVersionAvailability(CharacterId, CharacterId, versions.Select((version, index) =>
                    new CharacterReleasedVersion(CharacterId + "-api-ref-" + index, version, index == 0)).ToArray()));
            public void Fail(Exception error) => _source.TrySetException(error);

            public void Dispose()
            {
                _registration.Dispose();
                _source.TrySetCanceled();
            }
        }
    }
}
