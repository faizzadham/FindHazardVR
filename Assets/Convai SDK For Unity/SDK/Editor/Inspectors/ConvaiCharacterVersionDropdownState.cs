using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;

namespace Convai.Editor.Inspectors
{
    /// <summary>Inspector-owned release list; never changes the saved character selection.</summary>
    internal sealed class ConvaiCharacterVersionDropdownState : IDisposable
    {
        private readonly Action _repaint;
        private object _context;
        private bool _hasContext;
        private bool _disposed;
        private Func<CancellationToken, Task<CharacterVersionAvailability>> _fetch;
        private CancellationTokenSource _pending;
        private Task _loadTask = Task.CompletedTask;

        internal ConvaiCharacterVersionDropdownState(Action repaint = null) => _repaint = repaint;

        internal CharacterVersionAvailability Availability { get; private set; }
        internal string CurrentReference => Availability?.CurrentReference;
        internal IReadOnlyList<CharacterReleasedVersion> Versions =>
            Availability?.Versions ?? Array.Empty<CharacterReleasedVersion>();
        internal bool IsLoading { get; private set; }
        internal bool HasLoaded { get; private set; }
        internal string Error { get; private set; }
        internal int Generation { get; private set; }
        internal bool CanRefresh => !_disposed && _fetch != null;

        internal bool IsCurrentContext(object context) =>
            !_disposed && _hasContext && Equals(_context, context);

        internal Task SetContextAsync(object context,
            Func<CancellationToken, Task<CharacterVersionAvailability>> fetch, string unavailableReason = null)
        {
            if (_disposed) return Task.CompletedTask;
            if (IsCurrentContext(context)) return _loadTask;

            CancelPending();
            _context = context;
            _hasContext = true;
            _fetch = string.IsNullOrEmpty(unavailableReason) ? fetch : null;
            Availability = null;
            HasLoaded = false;
            Error = unavailableReason;
            return _fetch == null ? _loadTask = Task.CompletedTask : RefreshAsync();
        }

        internal Task RefreshAsync()
        {
            if (_disposed || _fetch == null) return Task.CompletedTask;
            if (IsLoading) return _loadTask;

            Generation++;
            Availability = null;
            HasLoaded = false;
            Error = null;
            IsLoading = true;
            _pending = new CancellationTokenSource();
            _loadTask = LoadAsync(_fetch, _pending, Generation);
            return _loadTask;
        }

        private async Task LoadAsync(Func<CancellationToken, Task<CharacterVersionAvailability>> fetch,
            CancellationTokenSource cancellation, int generation)
        {
            try
            {
                CharacterVersionAvailability availability = await fetch(cancellation.Token);
                if (_disposed || generation != Generation) return;
                if (availability == null) throw new InvalidOperationException("Missing version availability.");
                Availability = availability;
                HasLoaded = true;
            }
            catch (Exception error)
            {
                if (_disposed || generation != Generation) return;
                Error = GetErrorMessage(error);
            }
            finally
            {
                cancellation.Dispose();
                if (!_disposed && generation == Generation)
                {
                    _pending = null;
                    IsLoading = false;
                    _repaint?.Invoke();
                }
            }
        }

        private static string GetErrorMessage(Exception error)
        {
            // Do not show raw response bodies, credential-bearing URLs, or arbitrary exception messages.
            if (error is ConvaiApiException apiError)
            {
                if (apiError.StatusCode == 400)
                    return "Cannot load released versions. The character reference is invalid; check the Character ID, then refresh.";
                if (apiError.StatusCode == 401)
                    return "Cannot load released versions. Check the API key and authentication, then refresh.";
                if (apiError.StatusCode == 403)
                    return "Cannot load released versions. This account does not have the required versioning entitlement.";
                if (apiError.StatusCode == 404)
                    return "Cannot load released versions. The character was not found or is not visible in this workspace.";
                if (apiError.StatusCode == 429)
                    return "Version requests are rate limited. Wait before refreshing.";
                if (apiError.StatusCode.HasValue)
                    return $"Cannot load released versions (HTTP {apiError.StatusCode.Value}). Refresh to retry.";
            }

            return error is OperationCanceledException
                ? "Loading released versions was cancelled. Refresh to retry."
                : "Cannot load released versions. Check the connection and API configuration, then refresh.";
        }

        private void CancelPending()
        {
            Generation++;
            _pending?.Cancel();
            _pending = null;
            IsLoading = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelPending();
            _fetch = null;
            _context = null;
            Availability = null;
        }
    }
}
