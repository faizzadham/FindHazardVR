using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Vision.Sources;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class WebcamVisionFrameSourceTests
    {
        private GameObject _gameObject;
        private WebcamVisionFrameSource _source;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("WebcamVisionFrameSourceTests");
            _source = _gameObject.AddComponent<WebcamVisionFrameSource>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void StopCapture_CancelsPendingStartupAndKeepsStoppedState()
        {
            using var startupCancellation = new CancellationTokenSource();
            SetPrivateField("_startupCancellation", startupCancellation);
            SetPrivateField("_isInitializing", true);

            _source.StopCapture();

            Assert.That(startupCancellation.IsCancellationRequested, Is.True);
            Assert.That(GetPrivateField("_startupCancellation"), Is.Null);
            Assert.That(GetPrivateField("_isInitializing"), Is.False);
            Assert.That(_source.State, Is.EqualTo(VisionSourceState.Stopped));
            Assert.That(_source.IsCapturing, Is.False);
        }

        [Test]
        public async Task StartupWait_ObservesCancellationBeforeReadingWebcamState()
        {
            using var startupCancellation = new CancellationTokenSource();
            startupCancellation.Cancel();

            MethodInfo waitForStartup = typeof(WebcamVisionFrameSource).GetMethod(
                "WaitForWebcamToStartAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(waitForStartup, Is.Not.Null);

            var wait = (Task)waitForStartup.Invoke(
                _source,
                new object[] { startupCancellation.Token });

            try
            {
                await wait;
                Assert.Fail("A cancelled webcam startup must not continue into texture access.");
            }
            catch (OperationCanceledException)
            {
                Assert.Pass();
            }
        }

        [Test]
        public async Task StartupWait_RechecksCancellationAfterCompletedPollDelay()
        {
            using var startupCancellation = new CancellationTokenSource();
            SetPrivateField("_startupCancellation", startupCancellation);
            SetPrivateField("_isInitializing", true);
            int readinessChecks = 0;

            var queuedContext = new QueuedSynchronizationContext();
            SynchronizationContext previousContext = SynchronizationContext.Current;
            Task wait;
            try
            {
                SynchronizationContext.SetSynchronizationContext(queuedContext);
                wait = WebcamVisionFrameSource.WaitForFirstFrameAsync(
                    () => { readinessChecks++; return false; }, startupCancellation.Token);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            for (int attempt = 0; attempt < 20 && queuedContext.PendingCount == 0; attempt++)
                await Task.Delay(25);

            Assert.That(queuedContext.PendingCount, Is.GreaterThan(0),
                "The completed delay should have queued the webcam polling continuation.");

            _source.StopCapture();
            queuedContext.RunAll();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(wait,
                "A cancellation seen after the poll delay must end the startup wait.");
            Assert.That(readinessChecks, Is.EqualTo(1),
                "Cancellation must be checked before the next hardware readiness read.");
            Assert.That(_source.State, Is.EqualTo(VisionSourceState.Stopped));
        }

        private object GetPrivateField(string fieldName)
        {
            FieldInfo field = typeof(WebcamVisionFrameSource).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(_source);
        }

        private void SetPrivateField(string fieldName, object value)
        {
            FieldInfo field = typeof(WebcamVisionFrameSource).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(_source, value);
        }

        private sealed class QueuedSynchronizationContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object State)> _callbacks = new();

            public int PendingCount
            {
                get
                {
                    lock (_callbacks)
                        return _callbacks.Count;
                }
            }

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (_callbacks)
                    _callbacks.Enqueue((callback, state));
            }

            public void RunAll()
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object State) work;
                    lock (_callbacks)
                    {
                        if (_callbacks.Count == 0)
                            return;

                        work = _callbacks.Dequeue();
                    }

                    work.Callback(work.State);
                }
            }
        }
    }
}
