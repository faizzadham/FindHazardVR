using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Infrastructure.Networking.Native;
using LiveKit.Internal.FFI;
using LiveKit.Internal.Threading;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class LiveKitInstructionExtensionsTests
    {
        private SynchronizationContext _previousContext;
        private SynchronizationContext _previousFfiContext;
        private QueuedSynchronizationContext _context;

        private sealed class QueuedSynchronizationContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback callback, object state)> _callbacks = new();

            public override void Post(SendOrPostCallback callback, object state) =>
                _callbacks.Enqueue((callback, state));

            public void Drain()
            {
                while (_callbacks.Count > 0)
                {
                    var work = _callbacks.Dequeue();
                    work.callback(work.state);
                }
            }
        }

        private sealed class TestInstruction : YieldInstruction
        {
            public string PublishedSid { get; private set; } = string.Empty;
            public bool PublicationAdded { get; private set; }

            public void Complete(bool error = false)
            {
                IsError = error;
                IsDone = true;
            }

            public void CompletePublication(Action afterSignal = null)
            {
                IsDone = true;
                afterSignal?.Invoke();
                PublishedSid = "TR_published";
                PublicationAdded = true;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            _previousFfiContext = FfiClient.Instance._context;
            _context = new QueuedSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_context);
            FfiClient.Instance._context = _context;
        }

        [TearDown]
        public void TearDown()
        {
            FfiClient.Instance._context = _previousFfiContext;
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public void AsTask_WhenAlreadyDone_StillQueuesCompletion()
        {
            var instruction = new TestInstruction();
            instruction.Complete();

            Task task = instruction.AsTask(CancellationToken.None);

            Assert.That(task.IsCompleted, Is.False);
            _context.Drain();
            Assert.That(task.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public void AsTask_CompletesWhenInstructionCompletes_AndExposesError()
        {
            var instruction = new TestInstruction();
            Task task = instruction.AsTask(CancellationToken.None);
            Assert.That(task.IsCompleted, Is.False);

            instruction.Complete(error: true);

            Assert.That(task.IsCompleted, Is.False);
            _context.Drain();
            Assert.That(task.IsCompletedSuccessfully, Is.True);
            Assert.That(instruction.IsError, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AsTask_PublicationStateIsReadyBeforeConsumerResumes(bool attachAfterSignal)
        {
            var instruction = new TestInstruction();
            Task consumer = null;
            int mainThread = Environment.CurrentManagedThreadId;
            int consumerThread = -1;
            string observedSid = null;
            bool observedPublication = false;

            async Task Consume()
            {
                await instruction.AsTask(CancellationToken.None);
                consumerThread = Environment.CurrentManagedThreadId;
                observedSid = instruction.PublishedSid;
                observedPublication = instruction.PublicationAdded;
            }

            if (attachAfterSignal)
                instruction.CompletePublication(() => consumer = Consume());
            else
            {
                consumer = Consume();
                instruction.CompletePublication();
            }

            Assert.That(consumer.IsCompleted, Is.False,
                "Publication consumers must wait until the native callback has returned.");
            _context.Drain();

            Assert.That(consumer.IsCompletedSuccessfully, Is.True);
            Assert.That(observedSid, Is.EqualTo("TR_published"));
            Assert.That(observedPublication, Is.True);
            Assert.That(consumerThread, Is.EqualTo(mainThread));
        }

        [Test]
        public void AsTask_UsesCallbackContextWhenCallerHasNone()
        {
            var instruction = new TestInstruction();
            SynchronizationContext.SetSynchronizationContext(null);
            Task task = instruction.AsTask(CancellationToken.None);
            SynchronizationContext.SetSynchronizationContext(_context);

            instruction.Complete();
            Assert.That(task.IsCompleted, Is.False);
            _context.Drain();
            Assert.That(task.IsCompletedSuccessfully, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AsTask_WithoutSerialMainThreadContext_Faults(bool useBaseContext)
        {
            SynchronizationContext.SetSynchronizationContext(useBaseContext ? new SynchronizationContext() : null);
            FfiClient.Instance._context = SynchronizationContext.Current;
            var instruction = new TestInstruction();
            Task task = instruction.AsTask(CancellationToken.None);

            instruction.Complete();

            Assert.That(task.IsFaulted, Is.True);
            Assert.That(task.Exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void AsTask_CancellationWhileCompletionIsQueued_Wins()
        {
            var instruction = new TestInstruction();
            using var cts = new CancellationTokenSource();
            Task task = instruction.AsTask(cts.Token);
            instruction.Complete();

            cts.Cancel();
            Assert.That(task.IsCanceled, Is.True);
            _context.Drain();
            Assert.That(task.IsCanceled, Is.True);
        }

        [Test]
        public void AsTask_WhenTokenCancelled_CancelsWithoutWaitingForNative()
        {
            var instruction = new TestInstruction();
            using var cts = new CancellationTokenSource();
            Task task = instruction.AsTask(cts.Token);

            cts.Cancel();

            Assert.That(task.IsCanceled, Is.True);
            Assert.That(instruction.IsDone, Is.False);
        }

        [Test]
        public void AsTask_WhenTokenAlreadyCancelled_ReturnsCanceledTask()
        {
            var instruction = new TestInstruction();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Task task = instruction.AsTask(cts.Token);

            Assert.That(task.IsCanceled, Is.True);
        }

        [Test]
        public void AsTask_NullInstruction_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((YieldInstruction)null).AsTask(CancellationToken.None));
        }
    }
}
