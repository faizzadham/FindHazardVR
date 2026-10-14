using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.EventSystem;
using Convai.Domain.Identity;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Modules;
using Convai.Runtime.Core.Providers;
using Convai.Runtime.Room;

namespace Convai.Runtime.Core
{
    /// <summary>
    ///     The main runtime orchestrator for the Convai SDK.
    ///     Owns all services and manages module lifecycle.
    /// </summary>
    /// <remarks>
    ///     Created via <see cref="ConvaiRuntimeBuilder.Build" />, started with <see cref="StartAsync" />,
    ///     and disposed through <see cref="DisposeAsync" />.
    /// </remarks>
    public sealed class ConvaiRuntime : IConvaiRuntime, IAsyncDisposable
    {
        private readonly ILogger _logger;
        private readonly List<IConvaiModule> _modules;
        private readonly SemaphoreSlim _moduleLifecycleGate = new(1, 1);
        private readonly object _stateLock = new();
        private readonly object _statePublicationLock = new();
        private readonly Queue<RuntimeStateChanged> _statePublicationQueue = new();
        private Task _disposeTask;
        private bool _disposeRequested;
        private bool _isDrainingStatePublications;
        private RuntimePauseReason _lastPauseReason;
        private ModuleContext _moduleContext;
        private RuntimeState _state;
        private Task<Unit> _stopTask;

        /// <summary>
        ///     Internal constructor - use <see cref="ConvaiRuntimeBuilder" /> to create instances.
        /// </summary>
        internal ConvaiRuntime(
            IRoomRuntime room,
            IEventHub events,
            IAgentRegistry agents,
            IReadOnlyList<IConvaiModule> modules,
            ILogger logger = null,
            ITransportProvider transport = null,
            IConversationProvider conversation = null,
            ConvaiBootstrapConfigSnapshot config = null,
            IRuntimePreferences runtimePreferences = null,
            IFeatureVariantProvider featureVariants = null,
            ITelemetryProvider telemetry = null,
            IEndUserIdentityProvider endUserIdentityProvider = null,
            IEndUserMetadataProvider endUserMetadataProvider = null)
        {
            Room = room ?? throw new ArgumentNullException(nameof(room));
            Events = events ?? throw new ArgumentNullException(nameof(events));
            Agents = agents ?? throw new ArgumentNullException(nameof(agents));
            _modules = new List<IConvaiModule>(modules ?? Array.Empty<IConvaiModule>());
            _logger = logger.WithTag(nameof(ConvaiRuntime), LogCategory.Bootstrap);
            _state = RuntimeState.Created;

            Transport = transport;
            Conversation = conversation;
            Config = config;
            RuntimePreferences = runtimePreferences;
            FeatureVariants = featureVariants;
            Telemetry = telemetry;
            EndUserIdentityProvider = endUserIdentityProvider;
            EndUserMetadataProvider = endUserMetadataProvider;
        }

        #region ModuleContext Implementation

        private sealed class ModuleContext : IModuleContext
        {
            private readonly Dictionary<Type, object> _services = new();

            public ModuleContext(ConvaiRuntime runtime)
            {
                Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            }

            #region Runtime Reference

            public ConvaiRuntime Runtime { get; }

            #endregion

            #region Typed Properties (Preferred)

            /// <inheritdoc />
            public IEventHub Events => Runtime.Events;

            /// <inheritdoc />
            public IAgentRegistry Agents => Runtime.Agents;

            /// <inheritdoc />
            public ITransportProvider Transport => Runtime.Transport;

            /// <inheritdoc />
            public IRuntimePreferences Preferences => Runtime.RuntimePreferences;

            /// <inheritdoc />
            public ILogger Logger => Runtime._logger;

            /// <inheritdoc />
            public IConvaiRoomAudioService RoomAudio
            {
                get
                {
                    TryGetModuleService(out IConvaiRoomAudioService service);
                    return service;
                }
            }

            /// <inheritdoc />
            public ICredentialProvider Credentials
            {
                get
                {
                    TryGetModuleService(out ICredentialProvider service);
                    return service;
                }
            }

            #endregion

            #region Generic Service Access

            public bool TryGetModuleService<TService>(out TService service) where TService : class
            {
                if (_services.TryGetValue(typeof(TService), out object obj))
                {
                    service = (TService)obj;
                    return true;
                }

                service = default;
                return false;
            }

            public void ProvideModuleService<TService>(TService instance) where TService : class
            {
                if (instance == null)
                    throw new ArgumentNullException(nameof(instance));

                _services[typeof(TService)] = instance;
            }

            #endregion
        }

        #endregion

        #region Typed Dependencies (NO Service Locator)

        /// <summary>
        ///     Current runtime state.
        /// </summary>
        public RuntimeState State
        {
            get
            {
                lock (_stateLock)
                    return _state;
            }
        }

        /// <summary>
        ///     Room runtime for connection, audio, and ownership management.
        /// </summary>
        public IRoomRuntime Room { get; }

        /// <summary>
        ///     Event hub for decoupled communication.
        /// </summary>
        public IEventHub Events { get; }

        /// <summary>
        ///     Agent registry for character and player management.
        /// </summary>
        public IAgentRegistry Agents { get; }

        /// <summary>
        ///     Registered modules.
        /// </summary>
        public IReadOnlyList<IConvaiModule> Modules => _modules.AsReadOnly();

        #endregion

        #region Providers (RC-2: All builder fields consumed)

        /// <summary>
        ///     Transport provider for platform-specific communication.
        /// </summary>
        public ITransportProvider Transport { get; }

        /// <summary>
        ///     Optional custom standalone conversation provider; null when none is supplied.
        /// </summary>
        public IConversationProvider Conversation { get; }

        /// <summary>
        ///     Bootstrap configuration snapshot (immutable).
        /// </summary>
        public ConvaiBootstrapConfigSnapshot Config { get; }

        /// <summary>
        ///     Mutable runtime preferences.
        /// </summary>
        public IRuntimePreferences RuntimePreferences { get; }

        /// <summary>
        ///     Feature variant provider for A/B testing.
        /// </summary>
        public IFeatureVariantProvider FeatureVariants { get; }

        /// <summary>
        ///     Telemetry provider for analytics.
        /// </summary>
        public ITelemetryProvider Telemetry { get; }

        /// <summary>
        ///     Optional end-user identity provider configured for this runtime.
        /// </summary>
        public IEndUserIdentityProvider EndUserIdentityProvider { get; }

        /// <summary>
        ///     Optional end-user metadata provider configured for this runtime.
        /// </summary>
        public IEndUserMetadataProvider EndUserMetadataProvider { get; }

        #endregion

        #region Lifecycle Operations

        /// <summary>
        ///     Starts the runtime and all registered modules.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task that completes when startup is finished.</returns>
        public IConvaiOperation<Unit> StartAsync(CancellationToken ct = default) =>
            ConvaiOperation<Unit>.FromTask(StartAsyncCore(ct));

        private async Task<Unit> StartAsyncCore(CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_moduleLifecycleGate, ct);
            try
            {
                if (!TryQueueStateTransition(
                        RuntimeState.Created,
                        RuntimeState.Starting,
                        RuntimePauseReason.None,
                        requireNotDisposing: true,
                        out bool shouldDrainStarting))
                {
                    throw new InvalidOperationException(
                        $"Cannot start runtime in state {State}. Runtime must be in Created state.");
                }

                DrainStatePublicationQueueIfOwner(shouldDrainStarting);
                _logger?.Info("Starting...");

                try
                {
                    IModuleContext context = GetOrCreateModuleContext();

                    // Register module services in dependency order
                    foreach (IConvaiModule module in _modules)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!StartupOwnsLifecycleState())
                            return Unit.Value;

                        if (_logger?.IsEnabled(LogLevel.Debug) == true)
                            _logger?.Debug($"Registering module services: {module.ModuleId}");
                        await module.RegisterAsync(context, ct);

                        if (!StartupOwnsLifecycleState())
                            return Unit.Value;
                    }

                    // Start modules in dependency order
                    foreach (IConvaiModule module in _modules)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!StartupOwnsLifecycleState())
                            return Unit.Value;

                        if (_logger?.IsEnabled(LogLevel.Debug) == true)
                            _logger?.Debug($"Starting module: {module.ModuleId}");
                        await module.StartAsync(context, ct);

                        if (!StartupOwnsLifecycleState())
                            return Unit.Value;
                    }

                    if (!TryQueueStateTransition(
                            RuntimeState.Starting,
                            RuntimeState.Running,
                            RuntimePauseReason.None,
                            requireNotDisposing: true,
                            out bool shouldDrainRunning))
                    {
                        if (_logger?.IsEnabled(LogLevel.Debug) == true)
                            _logger?.Debug(
                            $"Startup completed after the runtime entered {State}; preserving the newer lifecycle state.");
                        return Unit.Value;
                    }

                    DrainStatePublicationQueueIfOwner(shouldDrainRunning);
                    _logger?.Info("Started successfully");
                    return Unit.Value;
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Error) == true)
                        _logger?.Error($"Start failed: {ex.Message}");
                    if (TryQueueStateTransition(
                            RuntimeState.Starting,
                            RuntimeState.Stopped,
                            RuntimePauseReason.None,
                            requireNotDisposing: false,
                            out bool shouldDrainStopped))
                        DrainStatePublicationQueueIfOwner(shouldDrainStopped);
                    throw;
                }
            }
            finally
            {
                _moduleLifecycleGate.Release();
            }
        }

        private bool StartupOwnsLifecycleState()
        {
            lock (_stateLock)
                return !_disposeRequested && _state == RuntimeState.Starting;
        }

        /// <summary>
        ///     Pauses the runtime and all modules.
        /// </summary>
        public IConvaiOperation<Unit> PauseAsync(RuntimePauseReason reason, CancellationToken ct = default) =>
            ConvaiOperation<Unit>.FromTask(PauseAsyncCore(reason, ct));

        private async Task<Unit> PauseAsyncCore(RuntimePauseReason reason, CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_moduleLifecycleGate, ct);
            try
            {
                if (!TryQueueStateTransition(
                        RuntimeState.Running,
                        RuntimeState.Pausing,
                        reason,
                        requireNotDisposing: true,
                        out bool shouldDrainPausing,
                        stateMutation: () => _lastPauseReason = reason))
                {
                    throw new InvalidOperationException(
                        $"Cannot pause runtime in state {State}. Runtime must be Running.");
                }

                DrainStatePublicationQueueIfOwner(shouldDrainPausing);
                if (_logger?.IsEnabled(LogLevel.Info) == true)
                    _logger?.Info($"Pausing (reason: {reason})...");

                try
                {
                    // Pause modules in reverse order
                    for (int i = _modules.Count - 1; i >= 0; i--)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!LifecycleOperationOwnsState(RuntimeState.Pausing)) return Unit.Value;
                        await _modules[i].PauseAsync(reason, ct);
                        if (!LifecycleOperationOwnsState(RuntimeState.Pausing)) return Unit.Value;
                    }

                    if (!TryQueueStateTransition(
                            RuntimeState.Pausing,
                            RuntimeState.Paused,
                            reason,
                            requireNotDisposing: true,
                            out bool shouldDrainPaused))
                        return Unit.Value;

                    DrainStatePublicationQueueIfOwner(shouldDrainPaused);
                    _logger?.Info("Paused");
                    return Unit.Value;
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Error) == true)
                        _logger?.Error($"Pause failed: {ex.Message}");
                    throw;
                }
            }
            finally
            {
                _moduleLifecycleGate.Release();
            }
        }

        /// <summary>
        ///     Resumes the runtime from pause.
        /// </summary>
        public IConvaiOperation<Unit> ResumeAsync(CancellationToken ct = default) =>
            ConvaiOperation<Unit>.FromTask(ResumeAsyncCore(ct));

        private async Task<Unit> ResumeAsyncCore(CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_moduleLifecycleGate, ct);
            try
            {
                if (!TryQueueStateTransition(
                        RuntimeState.Paused,
                        RuntimeState.Resuming,
                        RuntimePauseReason.None,
                        requireNotDisposing: true,
                        out bool shouldDrainResuming))
                {
                    throw new InvalidOperationException(
                        $"Cannot resume runtime in state {State}. Runtime must be Paused.");
                }

                DrainStatePublicationQueueIfOwner(shouldDrainResuming);
                _logger?.Info("Resuming...");

                try
                {
                    // Resume modules in original order
                    foreach (IConvaiModule module in _modules)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!LifecycleOperationOwnsState(RuntimeState.Resuming)) return Unit.Value;
                        await module.ResumeAsync(ct);
                        if (!LifecycleOperationOwnsState(RuntimeState.Resuming)) return Unit.Value;
                    }

                    if (!TryQueueStateTransition(
                            RuntimeState.Resuming,
                            RuntimeState.Running,
                        RuntimePauseReason.None,
                        requireNotDisposing: true,
                        out bool shouldDrainRunning,
                        stateMutation: () => _lastPauseReason = RuntimePauseReason.None))
                        return Unit.Value;

                    DrainStatePublicationQueueIfOwner(shouldDrainRunning);
                    _logger?.Info("Resumed");
                    return Unit.Value;
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Error) == true)
                        _logger?.Error($"Resume failed: {ex.Message}");
                    throw;
                }
            }
            finally
            {
                _moduleLifecycleGate.Release();
            }
        }

        /// <summary>
        ///     Stops the runtime and all modules.
        /// </summary>
        public IConvaiOperation<Unit> StopAsync(CancellationToken ct = default)
        {
            Task<Unit> stopTask = GetOrStartStopTask();
            Task<Unit> callerTask = ct.CanBeCanceled
                ? AwaitWithCallerCancellationAsync(stopTask, ct)
                : stopTask;
            return ConvaiOperation<Unit>.FromTask(callerTask);
        }

        private Task<Unit> GetOrStartStopTask()
        {
            TaskCompletionSource<Unit> completion;
            bool completesSynchronously = false;
            bool shouldDrainInitialTransition = false;
            lock (_statePublicationLock)
            {
                lock (_stateLock)
                {
                    if (_stopTask != null) return _stopTask;
                    if (_state is RuntimeState.Stopped or RuntimeState.Disposed)
                        return Task.FromResult(Unit.Value);

                    completion = new TaskCompletionSource<Unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopTask = completion.Task;

                    if (_state == RuntimeState.Created)
                    {
                        _state = RuntimeState.Stopped;
                        _statePublicationQueue.Enqueue(new RuntimeStateChanged(
                            RuntimeState.Created,
                            RuntimeState.Stopped,
                            RuntimePauseReason.None));
                        shouldDrainInitialTransition = ClaimStatePublicationDrain();
                        completesSynchronously = true;
                    }
                    else
                    {
                        RuntimeState previousState = _state;
                        _state = RuntimeState.Stopping;
                        _statePublicationQueue.Enqueue(new RuntimeStateChanged(
                            previousState,
                            RuntimeState.Stopping,
                            RuntimePauseReason.None));
                        shouldDrainInitialTransition = ClaimStatePublicationDrain();
                    }
                }
            }

            if (completesSynchronously)
            {
                try
                {
                    DrainStatePublicationQueueIfOwner(shouldDrainInitialTransition);
                    completion.TrySetResult(Unit.Value);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
            else
                _ = CompleteStopOperationAsync(completion, shouldDrainInitialTransition);

            return completion.Task;
        }

        private async Task CompleteStopOperationAsync(
            TaskCompletionSource<Unit> completion,
            bool shouldDrainStopping)
        {
            try
            {
                completion.TrySetResult(await StopAsyncCore(shouldDrainStopping));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task<Unit> StopAsyncCore(bool shouldDrainStopping)
        {
            ExceptionDispatchInfo firstFailure = null;
            CaptureStatePublicationFailure(shouldDrainStopping, ref firstFailure);
            _logger?.Info("Stopping...");

            await ConvaiTimers.WaitAsync(_moduleLifecycleGate);
            try
            {
                try
                {
                    List<IConvaiModule> modulesToStop;
                    lock (_stateLock)
                        modulesToStop = new List<IConvaiModule>(_modules);

                    // Stop modules in reverse order
                    for (int i = modulesToStop.Count - 1; i >= 0; i--)
                    {
                        IConvaiModule module = modulesToStop[i];
                        try
                        {
                            await module.StopAsync(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            if (_logger?.IsEnabled(LogLevel.Warning) == true)
                                _logger?.Warning($"Error stopping module {module.ModuleId}: {ex.Message}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (_logger?.IsEnabled(LogLevel.Error) == true)
                        _logger?.Error($"Stop failed: {exception.Message}");
                    firstFailure ??= ExceptionDispatchInfo.Capture(exception);
                }

                if (TryQueueStateTransition(
                        RuntimeState.Stopping,
                        RuntimeState.Stopped,
                        RuntimePauseReason.None,
                        requireNotDisposing: false,
                        out bool shouldDrainStopped))
                    CaptureStatePublicationFailure(shouldDrainStopped, ref firstFailure);

                _logger?.Info("Stopped");
            }
            finally
            {
                _moduleLifecycleGate.Release();
            }

            firstFailure?.Throw();
            return Unit.Value;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource<bool> completion;
            Task disposeTask;
            lock (_stateLock)
            {
                if (_disposeTask != null) return new ValueTask(_disposeTask);

                completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                _disposeRequested = true;
                disposeTask = _disposeTask;
            }

            _ = CompleteDisposeOperationAsync(completion);
            return new ValueTask(disposeTask);
        }

        private async Task CompleteDisposeOperationAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await DisposeAsyncCore();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task DisposeAsyncCore()
        {
            Task<Unit> stopTask;
            RuntimeState state;
            lock (_stateLock)
            {
                state = _state;
                stopTask = _stopTask;
            }

            // A previously requested stop can have assigned Stopped before its publication and
            // completion have drained. Disposal joins that canonical cleanup rather than racing it.
            if (state != RuntimeState.Created && state != RuntimeState.Disposed)
            {
                stopTask ??= GetOrStartStopTask();
                try
                {
                    await stopTask;
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Warning) == true)
                        _logger?.Warning($"Error during stop in dispose: {ex.Message}");
                }
            }

            // Dispose room runtime if disposable
            Room?.Shutdown();

            if (!TryQueueTerminalDisposedTransition(out bool shouldDrainDisposed))
                return;

            DrainStatePublicationQueueIfOwner(shouldDrainDisposed);
            _logger?.Debug("Disposed");
        }

        #endregion

        #region Internal Helpers

        private static async Task<Unit> AwaitWithCallerCancellationAsync(
            Task<Unit> operation,
            CancellationToken ct)
        {
            if (operation.IsCompleted)
                return await operation;

            var cancellationCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancellationCompletion.TrySetResult(true)))
            {
                Task completedTask = await ConvaiTimers.WhenAny(operation, cancellationCompletion.Task);
                if (completedTask != operation)
                    ct.ThrowIfCancellationRequested();
            }

            return await operation;
        }

        private bool TryQueueStateTransition(
            RuntimeState expected,
            RuntimeState current,
            RuntimePauseReason reason,
            bool requireNotDisposing,
            out bool shouldDrain,
            Action stateMutation = null)
        {
            shouldDrain = false;
            lock (_statePublicationLock)
            {
                lock (_stateLock)
                {
                    if (_state != expected || requireNotDisposing && _disposeRequested)
                        return false;

                    stateMutation?.Invoke();
                    _state = current;
                    _statePublicationQueue.Enqueue(new RuntimeStateChanged(expected, current, reason));
                    shouldDrain = ClaimStatePublicationDrain();
                }
            }

            return true;
        }

        private bool TryQueueTerminalDisposedTransition(out bool shouldDrain)
        {
            shouldDrain = false;
            lock (_statePublicationLock)
            {
                lock (_stateLock)
                {
                    if (_state == RuntimeState.Disposed)
                        return false;

                    RuntimeState previous = _state;
                    _state = RuntimeState.Disposed;
                    _statePublicationQueue.Enqueue(new RuntimeStateChanged(
                        previous,
                        RuntimeState.Disposed,
                        RuntimePauseReason.None));
                    shouldDrain = ClaimStatePublicationDrain();
                }
            }

            return true;
        }

        /// <summary>Requires <see cref="_statePublicationLock" /> to be held.</summary>
        private bool ClaimStatePublicationDrain()
        {
            if (_isDrainingStatePublications) return false;

            _isDrainingStatePublications = true;
            return true;
        }

        private void DrainStatePublicationQueueIfOwner(bool shouldDrain)
        {
            if (shouldDrain)
                DrainStatePublicationQueue();
        }

        private void CaptureStatePublicationFailure(
            bool shouldDrain,
            ref ExceptionDispatchInfo firstFailure)
        {
            if (!shouldDrain) return;

            try
            {
                DrainStatePublicationQueue();
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        private void DrainStatePublicationQueue()
        {
            ExceptionDispatchInfo firstPublicationFailure = null;
            try
            {
                while (true)
                {
                    RuntimeStateChanged stateChange;
                    lock (_statePublicationLock)
                    {
                        if (_statePublicationQueue.Count == 0)
                        {
                            _isDrainingStatePublications = false;
                            break;
                        }

                        stateChange = _statePublicationQueue.Dequeue();
                    }

                    try
                    {
                        Events?.Publish(stateChange);
                    }
                    catch (Exception exception)
                    {
                        // A custom event hub may allow a subscriber exception to escape. Continue
                        // draining transitions that were queued re-entrantly before surfacing the
                        // first failure to the publication boundary.
                        firstPublicationFailure ??= ExceptionDispatchInfo.Capture(exception);
                    }
                }
            }
            finally
            {
                lock (_statePublicationLock)
                {
                    if (_isDrainingStatePublications)
                    {
                        // Only an unexpected internal drain failure reaches this path. Do not
                        // replay possibly partially delivered transitions; restore ownership so
                        // the next transition can start a fresh drain.
                        _statePublicationQueue.Clear();
                        _isDrainingStatePublications = false;
                    }
                }
            }

            firstPublicationFailure?.Throw();
        }

        private bool LifecycleOperationOwnsState(RuntimeState state)
        {
            lock (_stateLock)
                return !_disposeRequested && _state == state;
        }

        private IModuleContext GetOrCreateModuleContext()
        {
            _moduleContext ??= new ModuleContext(this);
            return _moduleContext;
        }

        /// <summary>
        ///     Pre-registers a service into the shared module context.
        ///     Called by the composition root (ConvaiManager) to make container-owned services
        ///     available to modules before <see cref="StartAsync" /> runs.
        /// </summary>
        /// <typeparam name="TService">Service type to register.</typeparam>
        /// <param name="instance">Service instance.</param>
        internal void RegisterModuleService<TService>(TService instance) where TService : class
        {
            if (instance == null) return;
            GetOrCreateModuleContext().ProvideModuleService(instance);
        }

        /// <summary>
        ///     Adds late-discovered modules to the runtime before it starts.
        ///     Called by <see cref="ConvaiManager" /> in Start() after all Awake() calls
        ///     have completed, ensuring modules that self-register at default execution order
        ///     are captured before <see cref="StartAsync" /> runs.
        /// </summary>
        /// <param name="modules">Modules to add. Duplicates (by ModuleId) are skipped.</param>
        /// <exception cref="InvalidOperationException">If runtime is not in Created state.</exception>
        internal void AddModules(IReadOnlyList<IConvaiModule> modules)
        {
            if (modules == null || modules.Count == 0) return;

            lock (_stateLock)
            {
                if (_disposeRequested || _state != RuntimeState.Created)
                {
                    throw new InvalidOperationException(
                        $"Cannot add modules in state {_state}. Runtime must be in Created state.");
                }

                var existingIds = new HashSet<string>();
                foreach (IConvaiModule existing in _modules)
                    existingIds.Add(existing.ModuleId);

                foreach (IConvaiModule module in modules)
                {
                    if (module != null && existingIds.Add(module.ModuleId))
                    {
                        _modules.Add(module);
                        if (_logger?.IsEnabled(LogLevel.Debug) == true)
                            _logger?.Debug($"Late-discovered module added: {module.ModuleId}");
                    }
                }
            }
        }

        /// <summary>
        ///     Adds and starts a module discovered after the runtime has already started.
        ///     Runtime-instantiated character features such as lip sync use this path.
        /// </summary>
        internal IConvaiOperation<Unit> AddAndStartModuleAsync(
            IConvaiModule module,
            CancellationToken ct = default) =>
            ConvaiOperation<Unit>.FromTask(AddAndStartModuleAsyncCore(module, ct));

        private async Task<Unit> AddAndStartModuleAsyncCore(
            IConvaiModule module,
            CancellationToken ct)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));

            await ConvaiTimers.WaitAsync(_moduleLifecycleGate, ct);
            bool added = false;
            try
            {
                lock (_stateLock)
                {
                    if (_disposeRequested || _state != RuntimeState.Running)
                    {
                        throw new InvalidOperationException(
                            $"Cannot dynamically start module '{module.ModuleId}' in state {_state}. " +
                            "Runtime must be running.");
                    }

                    if (_modules.Contains(module)) return Unit.Value;
                    _modules.Add(module);
                    added = true;
                }

                IModuleContext context = GetOrCreateModuleContext();
                ct.ThrowIfCancellationRequested();
                await module.RegisterAsync(context, ct);
                ct.ThrowIfCancellationRequested();
                await module.StartAsync(context, ct);
                ct.ThrowIfCancellationRequested();
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    _logger?.Debug($"Runtime module started dynamically: {module.ModuleId}");
                return Unit.Value;
            }
            catch
            {
                if (added)
                {
                    try
                    {
                        await module.StopAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        if (_logger?.IsEnabled(LogLevel.Warning) == true)
                            _logger?.Warning(
                            $"Error cleaning up dynamic module {module.ModuleId}: {ex.Message}");
                    }

                    lock (_stateLock)
                        _modules.Remove(module);
                }

                throw;
            }
            finally
            {
                _moduleLifecycleGate.Release();
            }
        }

        /// <summary>Stops and removes a module that was destroyed while the runtime is active.</summary>
        internal IConvaiOperation<Unit> StopAndRemoveModuleAsync(
            IConvaiModule module,
            CancellationToken ct = default)
        {
            Task<Unit> removalTask = StopAndRemoveModuleAsyncCore(module);
            Task<Unit> callerTask = ct.CanBeCanceled
                ? AwaitWithCallerCancellationAsync(removalTask, ct)
                : removalTask;
            return ConvaiOperation<Unit>.FromTask(callerTask);
        }

        private async Task<Unit> StopAndRemoveModuleAsyncCore(
            IConvaiModule module)
        {
            if (module == null) return Unit.Value;

            await ConvaiTimers.WaitAsync(_moduleLifecycleGate);
            try
            {
                bool shouldStop;
                lock (_stateLock)
                {
                    if (!_modules.Contains(module)) return Unit.Value;
                    if (_disposeRequested || _state is RuntimeState.Stopping or RuntimeState.Disposed)
                        return Unit.Value;

                    _modules.Remove(module);
                    shouldStop = _state is RuntimeState.Running or RuntimeState.Paused;
                }

                if (shouldStop)
                    await module.StopAsync(CancellationToken.None);

                return Unit.Value;
            }
            finally
            {
                _moduleLifecycleGate.Release();
            }
        }

        #endregion
    }
}
