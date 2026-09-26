using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace InterCat.Ui.Tests;

/// <summary>Reads what a headless window actually drew, so a colour is asserted where the user sees it.</summary>
internal static class RenderedPixels
{
    /// <summary>One pixel of a rendered frame, whatever byte order the frame keeps.</summary>
    public static Color At(WriteableBitmap frame, Point point)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        int value = Marshal.ReadInt32(buffer.Address, ((int)point.Y * buffer.RowBytes) + ((int)point.X * 4));
        byte first = (byte)value;
        byte second = (byte)(value >> 8);
        byte third = (byte)(value >> 16);
        byte alpha = (byte)(value >> 24);
        return buffer.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(alpha, first, second, third)
            : Color.FromArgb(alpha, third, second, first);
    }

    /// <summary>The colour of a solid brush a control was given.</summary>
    public static Color ColorOf(IBrush? brush) => Xunit.Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
