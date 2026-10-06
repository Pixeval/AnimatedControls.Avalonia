using System;
using Avalonia;

namespace AnimatedControls.Avalonia;

/// <summary>
/// Reports an initialized source's size change before the image requests a new layout.
/// </summary>
public sealed class SourceSizeChangedEventArgs(Size oldSize, Size newSize) : EventArgs
{
    public Size OldSize { get; } = oldSize;

    public Size NewSize { get; } = newSize;
}
