using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AnimatedControls.Avalonia;

internal class MultiAnimatedBitmap(IReadOnlyCollection<BitmapOrStream> frameSources, IReadOnlyCollection<int> delays, bool disposeStream) : AnimatedBitmapBase
{
    private List<BitmapOrStream?>? _frameSources =
        (frameSources ?? throw new ArgumentNullException(nameof(frameSources))).Count < 1
            ? throw new ArgumentException($"Invalid {nameof(frameSources)}.Count")
            : [..frameSources];

    private readonly IReadOnlyCollection<int> _sourceDelays =
        delays is not null ? [..delays] : throw new ArgumentNullException(nameof(delays));

    private Size _size;
    private int _frameCount;
    private IReadOnlyList<Bitmap>? _frames;
    private IReadOnlyList<int> _delays = [];

    public override Size Size => _size;
    
    public override int FrameCount => _frameCount;

    public override IReadOnlyList<Bitmap> Frames => _frames ?? throw new InvalidOperationException();

    public override IReadOnlyList<int> Delays => _delays;

    protected override void InitCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var frameSources = _frameSources
            ?? throw new InvalidOperationException($"{nameof(MultiAnimatedBitmap)} has no readable frame streams.");

        var delays = new int[frameSources.Count];
        var frames = new Bitmap[frameSources.Count];

        try
        {
            for (var index = 0; index < frameSources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                delays[index] = _sourceDelays.ElementAtOrDefault(index) is var delay && delay > 0 ? delay : 100;
                var frameSource = frameSources[index];
                try
                {
                    frames[index] = frameSource switch
                    {
                        Bitmap bitmap => bitmap,
                        Stream stream => new Bitmap(stream),
                        null => throw new InvalidOperationException($"{nameof(MultiAnimatedBitmap)} has an unavailable frame stream.")
                    };
                }
                finally
                {
                    if (disposeStream && frameSource is Stream stream)
                        stream.Dispose();
                    frameSources[index] = null;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            _frameSources = null;
        }
        catch
        {
            DisposeFrames(frames);
            throw;
        }

        _size = frames[0].Size;
        _frameCount = delays.Length;
        _delays = delays;
        _frames = frames;
    }

    protected override void DisposeCore()
    {
        if (_frameSources is not null && disposeStream)
            foreach (var frameStream in _frameSources)
                frameStream?.Dispose();
        _frameSources = null;

        if (_frames is not null)
            DisposeFrames(_frames);

        _size = default;
        _frameCount = 0;
        _delays = [];
        _frames = null;
    }

    private static void DisposeFrames(IEnumerable<Bitmap> frames)
    {
        foreach (var frame in frames)
            frame?.Dispose();
    }
}

public union BitmapOrStream(Bitmap, Stream) : IDisposable
{
    public void Dispose()
    {
        switch(this)
        {
            case Bitmap bitmap:
                bitmap.Dispose();
                break;
            case Stream stream:
                stream.Dispose();
                break;
            default:
                break;
        }
    }
}
