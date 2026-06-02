using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AnimatedControls.Avalonia;

internal abstract class AnimatedBitmapBase(bool isInitialized = false) : IAnimatedBitmap
{
    private readonly object _lifecycleGate = new();
    private AnimatedBitmapLifecycleState _state = isInitialized
        ? AnimatedBitmapLifecycleState.Initialized
        : AnimatedBitmapLifecycleState.Created;
    private CancellationTokenSource? _disposeCancellationTokenSource;
    private TaskCompletionSource? _initializationCompletion;
    private int _initializingThreadId;

    public bool IsInitialized
    {
        get
        {
            lock (_lifecycleGate)
                return _state is AnimatedBitmapLifecycleState.Initialized;
        }
        set
        {
            lock (_lifecycleGate)
            {
                _state = value switch
                {
                    true when _state is AnimatedBitmapLifecycleState.Created => AnimatedBitmapLifecycleState.Initialized,
                    false when _state is AnimatedBitmapLifecycleState.Initialized => AnimatedBitmapLifecycleState.Created,
                    _ => _state
                };
            }
        }
    }

    public bool IsFailed
    {
        get
        {
            lock (_lifecycleGate)
                return _state is AnimatedBitmapLifecycleState.Failed;
        }
    }

    public bool IsCancellable
    {
        get
        {
            lock (_lifecycleGate)
                return field;
        }
        set
        {
            lock (_lifecycleGate)
                field = value;
        }
    }

    public abstract Size Size { get; }

    public abstract int FrameCount { get; }

    public abstract IReadOnlyList<Bitmap> Frames { get; }

    public abstract IReadOnlyList<int> Delays { get; }

    public event EventHandler? Initialized;

    public event EventHandler<AnimatedBitmapFailedEventArgs>? Failed;

    public void Init()
    {
        Task? pendingInitializationTask;
        CancellationTokenSource? disposeCancellationTokenSource = null;

        lock (_lifecycleGate)
        {
            if (_state is AnimatedBitmapLifecycleState.Initialized
                or AnimatedBitmapLifecycleState.Failed
                or AnimatedBitmapLifecycleState.Disposed)
                return;

            if (_state is AnimatedBitmapLifecycleState.Initializing)
            {
                if (_initializingThreadId == Environment.CurrentManagedThreadId)
                    return;

                pendingInitializationTask = _initializationCompletion?.Task;
            }
            else
            {
                _state = AnimatedBitmapLifecycleState.Initializing;
                _initializingThreadId = Environment.CurrentManagedThreadId;
                disposeCancellationTokenSource = new CancellationTokenSource();
                _disposeCancellationTokenSource = disposeCancellationTokenSource;
                _initializationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingInitializationTask = null;
            }
        }

        if (pendingInitializationTask is not null)
        {
            pendingInitializationTask.GetAwaiter().GetResult();
            return;
        }

        RunInitialization(disposeCancellationTokenSource!);
    }

    public void Dispose()
    {
        var disposeImmediately = false;

        lock (_lifecycleGate)
        {
            switch (_state)
            {
                case AnimatedBitmapLifecycleState.Disposed:
                    return;
                case AnimatedBitmapLifecycleState.Initializing:
                    // Init owns decoder resources while it is running; dispose asks it to stop and lets that thread clean up.
                    _state = AnimatedBitmapLifecycleState.Disposed;
                    _disposeCancellationTokenSource?.Cancel();
                    break;
                default:
                    _state = AnimatedBitmapLifecycleState.Disposed;
                    disposeImmediately = true;
                    break;
            }
        }

        if (!disposeImmediately)
        {
            GC.SuppressFinalize(this);
            return;
        }

        try
        {
            DisposeCore();
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    protected abstract void InitCore(CancellationToken cancellationToken);

    protected abstract void DisposeCore();

    private void RunInitialization(CancellationTokenSource disposeCancellationTokenSource)
    {
        EventHandler? initialized = null;
        EventHandler<AnimatedBitmapFailedEventArgs>? failed = null;
        AnimatedBitmapFailedEventArgs? failedEventArgs = null;
        var disposeAfterInitialization = false;

        try
        {
            InitCore(disposeCancellationTokenSource.Token);

            lock (_lifecycleGate)
            {
                if (_state is AnimatedBitmapLifecycleState.Initializing && !disposeCancellationTokenSource.IsCancellationRequested)
                {
                    _state = AnimatedBitmapLifecycleState.Initialized;
                    initialized = Initialized;
                }
                else
                {
                    _state = AnimatedBitmapLifecycleState.Disposed;
                    disposeAfterInitialization = true;
                }
            }
        }
        catch (OperationCanceledException) when (disposeCancellationTokenSource.IsCancellationRequested)
        {
            _ = DisposeCoreSafely();

            lock (_lifecycleGate)
                _state = AnimatedBitmapLifecycleState.Disposed;
        }
        catch (Exception e)
        {
            var cleanupException = DisposeCoreSafely();

            lock (_lifecycleGate)
            {
                if (_state is not AnimatedBitmapLifecycleState.Disposed && !disposeCancellationTokenSource.IsCancellationRequested)
                {
                    _state = AnimatedBitmapLifecycleState.Failed;
                    failed = Failed;
                    failedEventArgs = new AnimatedBitmapFailedEventArgs(cleanupException is null
                        ? e
                        : new AggregateException(e, cleanupException));
                }
            }
        }
        finally
        {
            if (disposeAfterInitialization)
                _ = DisposeCoreSafely();

            CompleteInitialization(disposeCancellationTokenSource);
        }

        initialized?.Invoke(this, EventArgs.Empty);
        failed?.Invoke(this, failedEventArgs!);
    }

    private void CompleteInitialization(CancellationTokenSource disposeCancellationTokenSource)
    {
        TaskCompletionSource? initializationCompletion;

        lock (_lifecycleGate)
        {
            _initializingThreadId = 0;
            if (ReferenceEquals(_disposeCancellationTokenSource, disposeCancellationTokenSource))
                _disposeCancellationTokenSource = null;

            initializationCompletion = _initializationCompletion;
            _initializationCompletion = null;
        }

        disposeCancellationTokenSource.Dispose();
        _ = initializationCompletion?.TrySetResult();
    }

    private Exception? DisposeCoreSafely()
    {
        try
        {
            DisposeCore();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private enum AnimatedBitmapLifecycleState
    {
        Created,
        Initializing,
        Initialized,
        Failed,
        Disposed
    }
}
