using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AnimatedControls.Avalonia;

internal class AnimatedBitmapSimpleImpl : AnimatedBitmapBase
{
    public AnimatedBitmapSimpleImpl(IReadOnlyCollection<Bitmap> bitmaps, IReadOnlyCollection<int> delays)
        : base(true)
    {
        ArgumentNullException.ThrowIfNull(bitmaps);
        ArgumentNullException.ThrowIfNull(delays);
        if (bitmaps.Count is var bitmapCount && delays.Count != bitmapCount)
            throw new ArgumentException($"{nameof(delays)} inconsistent count with {nameof(bitmaps)}");
        if ((IReadOnlyList<Bitmap>) [.. bitmaps] is not [var first, ..] bitmapsCopy)
            throw new ArgumentException($"Invalid {nameof(bitmaps)}.Count");
        Size = first.Size;
        Frames = bitmapsCopy;
        Delays = [.. delays];
        FrameCount = bitmapCount;
    }

    public override Size Size { get; }

    public override int FrameCount { get; }

    public override IReadOnlyList<Bitmap> Frames { get; }

    public override IReadOnlyList<int> Delays { get; }

    protected override void InitCore(CancellationToken cancellationToken)
    {
    }

    protected override void DisposeCore()
    {
        foreach (var bitmap in Frames)
            bitmap.Dispose();
    }
}
