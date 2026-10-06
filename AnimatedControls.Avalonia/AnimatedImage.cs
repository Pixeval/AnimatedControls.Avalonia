using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace AnimatedControls.Avalonia;

public class AnimatedImage : Control, ICustomHitTest
{
    private CompositionCustomVisual? _customVisual;
    private CancellationTokenSource? _cancellationTokenSource;
    private int _visualTreeVersion;
    private UpdatableAnimatedBitmap? _subscribedSource;
    private Size _sourceSize;

    public static readonly StyledProperty<IAnimatedBitmap?> SourceProperty = AvaloniaProperty.Register<AnimatedImage, IAnimatedBitmap?>(nameof(Source), defaultValue: null);

    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty = AvaloniaProperty.Register<AnimatedImage, StretchDirection>(nameof(StretchDirection), StretchDirection.Both);

    public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<AnimatedImage, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<AnimatedImage, bool>(nameof(IsPlaying), true);

    [Content]
    public IAnimatedBitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// Occurs on the UI thread before layout is invalidated, including updates within the same source.
    /// Subscribers can preserve their viewport; without a subscriber the image uses normal layout.
    /// </summary>
    public event EventHandler<SourceSizeChangedEventArgs>? SourceSizeChanged;

    /// <summary>Gets the layout scale from source coordinates to this control's bounds, before render transforms.</summary>
    public global::Avalonia.Vector SourceScale => Bounds is { Width: > 0, Height: > 0 } bounds
        && Source?.Size is { Width: > 0, Height: > 0 } size
        ? Stretch.CalculateScaling(bounds.Size, size, StretchDirection)
        : new(1, 1);

    public StretchDirection StretchDirection
    {
        get => GetValue(StretchDirectionProperty);
        set => SetValue(StretchDirectionProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    static AnimatedImage()
    {
        AffectsMeasure<AnimatedImage>(StretchProperty, StretchDirectionProperty);
        AffectsArrange<AnimatedImage>(StretchProperty, StretchDirectionProperty);
    }

    bool ICustomHitTest.HitTest(Point point) =>
        point is { X: >= 0, Y: >= 0 }
        && point.X <= Bounds.Width
        && point.Y <= Bounds.Height;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        switch (change.Property.Name)
        {
            case nameof(Source):
                UpdateSourceSubscription(VisualRoot is null ? null : change.NewValue as UpdatableAnimatedBitmap);
                OnSourcePropertyChanged(change.NewValue as IAnimatedBitmap);
                break;
            case nameof(IsPlaying):
                _customVisual?.SendHandlerMessage(IsPlaying
                    ? CustomVisualHandler.StartMessage
                    : CustomVisualHandler.StopMessage);
                break;
            case nameof(Stretch):
                _customVisual?.SendHandlerMessage(Stretch);
                Update();
                break;
            case nameof(StretchDirection):
                _customVisual?.SendHandlerMessage(StretchDirection);
                Update();
                break;
            case nameof(Bounds):
                Update();
                break;
        }

        base.OnPropertyChanged(change);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSourceSubscription(Source as UpdatableAnimatedBitmap);
        UpdateSourceSize();
        var version = Interlocked.Increment(ref _visualTreeVersion);
        _ = EnsureCustomVisualStateAsync(version);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UpdateSourceSubscription(null);
        _ = Interlocked.Increment(ref _visualTreeVersion);

        _customVisual?.SendHandlerMessage(CustomVisualHandler.StopMessage);

        ElementComposition.SetElementChildVisual(this, null);
        _customVisual = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        return Source is { IsInitialized: true }
            ? Stretch.CalculateSize(availableSize, Source.Size, StretchDirection)
            : default;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        return Source is { IsInitialized: true }
            ? Stretch.CalculateSize(finalSize, Source.Size, StretchDirection)
            : default;
    }

    private async void OnSourcePropertyChanged(IAnimatedBitmap? newValue)
    {
        UpdateSourceSize();
        var customVisual = _customVisual;
        if (customVisual is null)
            return;

        if (newValue is null)
            customVisual.SendHandlerMessage(CustomVisualHandler.ResetMessage);
        else
        {
            if (Source is { IsInitialized: false, IsFailed: false } source && source is not UpdatableAnimatedBitmap)
            {
                try
                {
                    await InitSourceAsync(source);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize animated image source: {ex}");
                    return;
                }
            }

            if (VisualRoot is null || !ReferenceEquals(_customVisual, customVisual))
                return;

            if (ReferenceEquals(Source, newValue) && (newValue.IsInitialized || newValue is UpdatableAnimatedBitmap))
                customVisual.SendHandlerMessage(newValue);
        }

        UpdateSourceSize();
        Update();
    }

    private void UpdateSourceSubscription(UpdatableAnimatedBitmap? source)
    {
        if (ReferenceEquals(_subscribedSource, source))
            return;
        _subscribedSource?.Changed -= SourceOnChanged;
        _subscribedSource = source;
        source?.Changed += SourceOnChanged;
    }

    private void SourceOnChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Source, sender) || VisualRoot is null)
                return;
            UpdateSourceSize();
            _customVisual?.SendHandlerMessage(Source!);
        });
    }

    private void UpdateSourceSize()
    {
        // Keep the old view while an ordinary replacement decodes; notify only when its size is usable.
        if (Source is { IsInitialized: false, IsFailed: false } and not UpdatableAnimatedBitmap)
            return;
        var size = Source is { IsInitialized: true } source ? source.Size : default;
        if (_sourceSize == size)
            return;
        var previous = _sourceSize;
        _sourceSize = size;
        SourceSizeChanged?.Invoke(this, new(previous, size));
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void Update()
    {
        if (_customVisual is null)
            return;

        _customVisual.Size = new Vector2((float) Bounds.Width, (float) Bounds.Height);
        _customVisual.Offset = Vector3.Zero;
    }

    private async Task EnsureCustomVisualStateAsync(int version)
    {
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor is null || version != _visualTreeVersion)
            return;

        // 不复用旧实例，避免“旧父级未解绑 + 新父级绑定”冲突
        var customVisual = compositor.CreateCustomVisual(new CustomVisualHandler());
        _customVisual = customVisual;
        ElementComposition.SetElementChildVisual(this, customVisual);
        customVisual.SendHandlerMessage(IsPlaying
            ? CustomVisualHandler.StartMessage
            : CustomVisualHandler.StopMessage);

        if (VisualRoot is null || version != _visualTreeVersion || !ReferenceEquals(_customVisual, customVisual))
            return;

        customVisual.SendHandlerMessage(Stretch);
        customVisual.SendHandlerMessage(StretchDirection);

        var source = Source;
        if (source is { IsInitialized: false, IsFailed: false } && source is not UpdatableAnimatedBitmap)
        {
            await InitSourceAsync(source);

            if (VisualRoot is null || version != _visualTreeVersion || !ReferenceEquals(_customVisual, customVisual))
                return;
        }

        if (ReferenceEquals(Source, source) && (Source is { IsInitialized: true } or UpdatableAnimatedBitmap))
            customVisual.SendHandlerMessage(Source);

        UpdateSourceSize();
        InvalidateArrange();
        InvalidateMeasure();
        Update();
    }

    private async Task InitSourceAsync(IAnimatedBitmap source)
    {
        if (_cancellationTokenSource is not null)
        {
            await _cancellationTokenSource.CancelAsync();
            _cancellationTokenSource.Dispose();
        }

        if (!source.IsCancellable)
        {
            await Task.Run(source.Init);
            return;
        }

        _cancellationTokenSource = new();

        try
        {
            await Task.Run(source.Init, _cancellationTokenSource.Token);
        }
        catch
        {
            // ignored
        }
    }

    ~AnimatedImage()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }

    private class CustomVisualHandler : CompositionCustomVisualHandler
    {
        private TimeSpan _animationElapsed;
        private TimeSpan? _lastServerTime;
        private IAnimatedBitmap? _currentInstance;
        private IAnimatedBitmap? _animationSource;
        private Stretch _stretch = Stretch.None;
        private StretchDirection _stretchDirection = StretchDirection.Both;
        private int _totalTime;
        private readonly List<int> _frameTimes = [];
        private bool _running;

        public static readonly object StopMessage = new();
        public static readonly object StartMessage = new();
        public static readonly object ResetMessage = new();

        public override void OnMessage(object message)
        {
            if (message == StartMessage)
            {
                _running = true;
                _lastServerTime = null;
                ScheduleAnimationFrameUpdate();
            }
            else if (message == StopMessage)
                _running = false;
            else if (message == ResetMessage)
            {
                Clear();
                Invalidate();
            }
            else switch (message)
            {
                case Stretch st:
                    _stretch = st;
                    break;
                case StretchDirection sd:
                    _stretchDirection = sd;
                    break;
                case IAnimatedBitmap instance when instance.IsInitialized || instance is UpdatableAnimatedBitmap:
                    _currentInstance = instance;
                    if (instance is UpdatableAnimatedBitmap updatable)
                    {
                        using var sources = updatable.AcquireSources();
                        SetAnimationSource(sources.Source);
                    }
                    else
                        SetAnimationSource(instance);
                    ScheduleAnimationFrameUpdate();
                    Invalidate();
                    break;
            }
            return;

            void Clear()
            {
                _currentInstance = null;
                SetAnimationSource(null);
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            if (!_running)
                return;
            Invalidate();
            ScheduleAnimationFrameUpdate();
        }

        private void ScheduleAnimationFrameUpdate()
        {
            if (_running && _animationSource is { FrameCount: > 1 })
                RegisterForNextAnimationFrameUpdate();
        }

        private void SetAnimationSource(IAnimatedBitmap? source)
        {
            if (ReferenceEquals(_animationSource, source))
                return;
            _animationSource = source;
            _animationElapsed = TimeSpan.Zero;
            _lastServerTime = null;
            _totalTime = 0;
            _frameTimes.Clear();
            if (source is null)
                return;
            if (source.Delays.Count != source.FrameCount)
                throw new ArgumentException($"{nameof(source.Delays)} inconsistent count with {nameof(source.FrameCount)}");
            foreach (var delay in source.Delays)
            {
                _frameTimes.Add(_totalTime);
                _totalTime += delay;
            }
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (_currentInstance is UpdatableAnimatedBitmap updatable)
            {
                using var sources = updatable.AcquireSources();
                SetAnimationSource(sources.Source);
                UpdateAnimationTime();
                if (sources.Source is { IsInitialized: true } source)
                    DrawFrame(drawingContext, source, true);
                if (sources.Preview is { IsInitialized: true } preview)
                    DrawFrame(drawingContext, preview, false);
            }
            else if (_currentInstance is { IsInitialized: true } source)
            {
                UpdateAnimationTime();
                DrawFrame(drawingContext, source, true);
            }
        }

        private void UpdateAnimationTime()
        {
            if (!_running)
                return;
            if (_lastServerTime.HasValue)
                _animationElapsed += CompositionNow - _lastServerTime.Value;
            _lastServerTime = CompositionNow;
        }

        private void DrawFrame(ImmediateDrawingContext drawingContext, IAnimatedBitmap source, bool animated)
        {
            var index = 0;
            if (animated && _totalTime > 0)
            {
                var ms = (int) _animationElapsed.TotalMilliseconds % _totalTime;
                index = _frameTimes.BinarySearch(ms);
                index = index < 0 ? ~index - 1 : index;
            }
            if (source.Frames.Count <= index || source.Size is not { Width: > 0, Height: > 0 })
                return;

            var viewPort = GetRenderBounds();
            var sourceRect = new Rect(source.Size);
            var scale = _stretch.CalculateScaling(viewPort.Size, source.Size, _stretchDirection);
            var destRect = viewPort.CenterRect(sourceRect * scale).Intersect(viewPort);
            sourceRect = sourceRect.CenterRect(destRect / scale);

            // Sample each original layer directly under the final composition transform; never flatten to a small bitmap.
            drawingContext.DrawBitmap(source.Frames[index], sourceRect, destRect);
        }
    }
}
