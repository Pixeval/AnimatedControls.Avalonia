using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace AnimatedControls.Avalonia;

internal class SingleAnimatedBitmap(Stream stream, bool disposeStream) : AnimatedBitmapBase
{
    private Stream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));
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

        if (_stream is null)
            throw new InvalidOperationException($"{nameof(SingleAnimatedBitmap)} has no readable source stream.");

        if (_stream.CanSeek)
            _stream.Position = 0;

        using var skCodec = SKCodec.Create(_stream) ?? throw new InvalidOperationException($"Unable to create {nameof(SKCodec)} from the provided stream.");

        var imageInfo = skCodec.Info;
        var targetInfo = new SKImageInfo(imageInfo.Width, imageInfo.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameCount = Math.Max(skCodec.FrameCount, 1);
        var delays = new int[frameCount];
        var frames = new Bitmap[frameCount];
        var frameInfos = skCodec.FrameInfo;

        try
        {
            for (var index = 0; index < frameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frameInfo = frameInfos.Length > index ? frameInfos[index] : default;
                delays[index] = frameInfo.Duration > 0 ? frameInfo.Duration : 100;
                frames[index] = DecodeFrame(skCodec, targetInfo, index);

                cancellationToken.ThrowIfCancellationRequested();
            }

            DisposeStream();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            DisposeFrames(frames);
            throw;
        }

        _size = new Size(imageInfo.Width, imageInfo.Height);
        _frameCount = delays.Length;
        _delays = delays;
        _frames = frames;
    }

    protected override void DisposeCore()
    {
        DisposeStream();

        if (_frames is not null)
            DisposeFrames(_frames);

        _size = default;
        _frameCount = 0;
        _delays = [];
        _frames = null;
    }

    private void DisposeStream()
    {
        if (_stream is not null && disposeStream)
            _stream.Dispose();
        _stream = null;
    }

    private static Bitmap DecodeFrame(SKCodec codec, SKImageInfo imageInfo, int frameIndex)
    {
        using var bitmap = new SKBitmap(imageInfo);
        var options = new SKCodecOptions(frameIndex);
        var result = codec.GetPixels(imageInfo, bitmap.GetPixels(), options);
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidOperationException($"Failed to decode frame {frameIndex}: {result}.");

        return new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            bitmap.GetPixels(),
            new PixelSize(imageInfo.Width, imageInfo.Height),
            new Vector(96, 96),
            bitmap.RowBytes);
    }

    private static void DisposeFrames(IEnumerable<Bitmap> frames)
    {
        foreach (var frame in frames)
            frame?.Dispose();
    }
}
