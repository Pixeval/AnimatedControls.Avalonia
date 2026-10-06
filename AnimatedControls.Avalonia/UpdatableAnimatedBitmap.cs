using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AnimatedControls.Avalonia;

/// <summary>
/// A stable image source updated by its caller, with a transparent preview over the current image.
/// </summary>
/// <param name="disposeSources">Whether this source owns completed and fallback images. Preview frames are always owned.</param>
public sealed class UpdatableAnimatedBitmap(bool disposeSources = true) : IAnimatedBitmap
{
    private readonly object _gate = new();
    private IAnimatedBitmap? _source;
    private IAnimatedBitmap? _fallback;
    private IAnimatedBitmap? _preview;
    private CancellationTokenSource? _pendingUpdate;
    private bool _disposed;
    private bool _failed;

    public event EventHandler? Changed;

    public event EventHandler? Initialized;

    public event EventHandler<AnimatedBitmapFailedEventArgs>? Failed;

    public bool IsInitialized
    {
        get
        {
            lock (_gate)
                return !_disposed && CurrentSource is { IsInitialized: true };
        }
    }

    bool IAnimatedBitmap.IsInitialized
    {
        get => IsInitialized;
        set => throw new NotSupportedException("Initialization is determined by the current image.");
    }

    public bool IsFailed
    {
        get
        {
            lock (_gate)
                return _failed && CurrentSource is null;
        }
    }

    public bool IsCancellable { get; set; }

    public Size Size
    {
        get
        {
            lock (_gate)
                return CurrentSource?.Size ?? default;
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_gate)
                return CurrentSource?.FrameCount ?? 0;
        }
    }

    public IReadOnlyList<Bitmap> Frames
    {
        get
        {
            lock (_gate)
                return CurrentSource?.Frames ?? [];
        }
    }

    public IReadOnlyList<int> Delays
    {
        get
        {
            lock (_gate)
                return CurrentSource?.Delays ?? [];
        }
    }

    private IAnimatedBitmap? CurrentSource => _source ?? _fallback ?? _preview;

    public void Init()
    {
        // Updates initialize their candidate off-thread before publishing it.
    }

    /// <summary>
    /// Uses an initialized image until a completed update is available. Does not supersede an in-flight update.
    /// </summary>
    public void SetFallback(IAnimatedBitmap? source)
    {
        if (source is { IsInitialized: false })
            throw new ArgumentException("The fallback must already be initialized.", nameof(source));

        bool wasInitialized;
        lock (_gate)
        {
            if (_disposed)
            {
                if (disposeSources)
                    source?.Dispose();
                return;
            }
            if (ReferenceEquals(_fallback, source))
                return;
            wasInitialized = IsInitialized;
            var previous = _fallback;
            _fallback = source;
            if (disposeSources && !ReferenceEquals(previous, _source) && !ReferenceEquals(previous, source))
                previous?.Dispose();
        }
        NotifyChanged(wasInitialized);
    }

    /// <summary>
    /// Takes ownership of a decoded preview. The caller controls when previews are pushed.
    /// Pass the token supplied to the update delegate to discard previews from superseded or completed updates.
    /// Without a token, this is an explicit standalone preview update (including clearing the preview).
    /// </summary>
    public void UpdatePreview(Bitmap? bitmap, CancellationToken updateToken = default)
    {
        bool wasInitialized;
        lock (_gate)
        {
            if (bitmap is not null && _preview?.Frames is [var frame] && ReferenceEquals(frame, bitmap))
                return;
            if (updateToken.CanBeCanceled
                && (updateToken.IsCancellationRequested || _pendingUpdate?.Token != updateToken))
            {
                // A late callback retains ownership until it is accepted or discarded here.
                bitmap?.Dispose();
                return;
            }
            if (_disposed)
            {
                bitmap?.Dispose();
                return;
            }
            wasInitialized = IsInitialized;
            var previous = _preview;
            _preview = bitmap is null ? null : IAnimatedBitmap.Load([bitmap], [0]);
            previous?.Dispose();
        }
        NotifyChanged(wasInitialized);
    }

    /// <summary>
    /// Loads and initializes a replacement while the old image stays visible. The latest update wins.
    /// An uncommitted candidate is always disposed; a committed candidate follows <c>disposeSources</c>.
    /// </summary>
    public async Task<IAnimatedBitmap?> UpdateAsync(
        Func<CancellationToken, Task<IAnimatedBitmap?>> loadSource,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(loadSource);
        CancellationTokenSource update;
        Task cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Run caller cancellation callbacks outside the rendering lock.
            cancellation = _pendingUpdate?.CancelAsync() ?? Task.CompletedTask;
            update = CancellationTokenSource.CreateLinkedTokenSource(token);
            _pendingUpdate = update;
        }

        IAnimatedBitmap? candidate = null;
        try
        {
            await cancellation.ConfigureAwait(false);
            update.Token.ThrowIfCancellationRequested();
            candidate = await loadSource(update.Token).ConfigureAwait(false);
            update.Token.ThrowIfCancellationRequested();
            if (candidate is null)
                return null;
            if (!candidate.IsInitialized)
                await Task.Run(candidate.Init, update.Token).ConfigureAwait(false);
            update.Token.ThrowIfCancellationRequested();
            if (!candidate.IsInitialized)
            {
                lock (_gate)
                    _failed = true;
                Failed?.Invoke(this, new(new InvalidOperationException("The replacement image could not be initialized.")));
                return null;
            }

            bool wasInitialized;
            var source = candidate;
            lock (_gate)
            {
                update.Token.ThrowIfCancellationRequested();
                wasInitialized = IsInitialized;
                var previous = _source;
                _source = source;
                candidate = null;
                _pendingUpdate = null;
                _failed = false;
                _preview?.Dispose();
                _preview = null;
                if (disposeSources && !ReferenceEquals(previous, source) && !ReferenceEquals(previous, _fallback))
                    previous?.Dispose();
            }
            NotifyChanged(wasInitialized);
            return source;
        }
        finally
        {
            lock (_gate)
            {
                if (!ReferenceEquals(candidate, _source) && !ReferenceEquals(candidate, _fallback))
                    candidate?.Dispose();
                if (ReferenceEquals(_pendingUpdate, update))
                    _pendingUpdate = null;
            }
            update.Dispose();
        }
    }

    private void NotifyChanged(bool wasInitialized)
    {
        if (!wasInitialized && IsInitialized)
            Initialized?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Hold this lease through rendering, so replacing an owned preview cannot dispose a frame being drawn.
    internal SourceLease AcquireSources() => new(_gate, this);

    internal readonly struct SourceLease : IDisposable
    {
        private readonly object _gate;

        internal SourceLease(object gate, UpdatableAnimatedBitmap owner)
        {
            _gate = gate;
            Monitor.Enter(gate);
            Source = owner._source ?? owner._fallback;
            Preview = owner._preview;
        }

        public IAnimatedBitmap? Source { get; }

        public IAnimatedBitmap? Preview { get; }

        public void Dispose() => Monitor.Exit(_gate);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _ = _pendingUpdate?.CancelAsync();
            _preview?.Dispose();
            if (disposeSources)
            {
                _source?.Dispose();
                if (!ReferenceEquals(_fallback, _source))
                    _fallback?.Dispose();
            }
            _source = null;
            _fallback = null;
            _preview = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        GC.SuppressFinalize(this);
    }
}
