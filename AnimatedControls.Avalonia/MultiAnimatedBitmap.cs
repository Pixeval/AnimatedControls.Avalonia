using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AnimatedControls.Avalonia;

internal class MultiAnimatedBitmap(IReadOnlyCollection<Stream> frameStreams, IReadOnlyCollection<int> delays, bool disposeStream) : AnimatedBitmapBase
{
    private List<Stream?>? _frameStreams =
        (frameStreams ?? throw new ArgumentNullException(nameof(frameStreams))).Count < 1
            ? throw new ArgumentException($"Invalid {nameof(frameStreams)}.Count")
            : [..frameStreams];

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

        var frameStreams = _frameStreams
            ?? throw new InvalidOperationException($"{nameof(MultiAnimatedBitmap)} has no readable frame streams.");

        var delays = new int[frameStreams.Count];
        var frames = new Bitmap[frameStreams.Count];

        try
        {
            for (var index = 0; index < frameStreams.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                delays[index] = _sourceDelays.ElementAtOrDefault(index) is var delay && delay > 0 ? delay : 100;
                var frameStream = frameStreams[index];
                try
                {
                    if (frameStream is null)
                        throw new InvalidOperationException($"{nameof(MultiAnimatedBitmap)} has an unavailable frame stream.");

                    frames[index] = new Bitmap(frameStream);
                }
                finally
                {
                    if (disposeStream)
                        frameStream?.Dispose();
                    frameStreams[index] = null;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            _frameStreams = null;
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
        if (_frameStreams is not null && disposeStream)
            foreach (var frameStream in _frameStreams)
                frameStream?.Dispose();
        _frameStreams = null;

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
