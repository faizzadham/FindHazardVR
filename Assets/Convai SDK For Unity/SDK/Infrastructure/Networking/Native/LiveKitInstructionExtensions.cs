using System;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Internal.Threading;

namespace Convai.Infrastructure.Networking.Native
{
    /// <summary>
    ///     Bridges LiveKit's awaitable <see cref="YieldInstruction" /> to <see cref="Task" /> so
    ///     transport code can await native operations without polling.
    /// </summary>
    /// <remarks>
    ///     Completion is queued on Unity's serial main-thread synchronization context after
    ///     LiveKit's callback returns. The publish callback signals IsDone before updating its
    ///     track SID and publication dictionary, so resuming inline would expose incomplete state.
    ///     A Unity main-thread context must be available on the callback or calling thread;
    ///     the base synchronization context cannot provide this ordering guarantee.
    ///     Cancellation completes the task but does not cancel the native request; callers
    ///     still inspect <see cref="YieldInstruction.IsError" /> after a successful wait.
    ///     An instruction supports one awaiter; do not call this twice on the same instance.
    /// </remarks>
    internal static class LiveKitInstructionExtensions
    {
        public static Task AsTask(this YieldInstruction instruction, CancellationToken ct)
        {
            if (instruction == null) throw new ArgumentNullException(nameof(instruction));
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

            SynchronizationContext callingContext = SynchronizationContext.Current;
            var completion = new TaskCompletionSource<bool>();
            CancellationTokenRegistration registration = ct.CanBeCanceled
                ? ct.Register(() => completion.TrySetCanceled(ct))
                : default;

            instruction.GetAwaiter().OnCompleted(() =>
            {
                // LiveKit marshals this callback to Unity even when AsTask was called off-thread.
                SynchronizationContext context = SynchronizationContext.Current ?? callingContext;
                if (context == null || context.GetType() == typeof(SynchronizationContext))
                {
                    registration.Dispose();
                    completion.TrySetException(new InvalidOperationException(
                        "LiveKit instruction completion requires Unity's main-thread synchronization context."));
                    return;
                }

                // Always Post, including already-done instructions: a reentrant caller can see
                // IsDone while the native callback is still updating publication state.
                context.Post(_ =>
                {
                    registration.Dispose();
                    completion.TrySetResult(true);
                }, null);
            });

            return completion.Task;
        }
    }
}
