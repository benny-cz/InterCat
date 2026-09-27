using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace InterCat.Ui.Tests;

/// <summary>Reads what a headless window actually drew, so a colour is asserted where the user sees it.</summary>
internal static class RenderedPixels
{
    /// <summary>
    /// One pixel of a rendered frame, whatever byte order the frame keeps. A point outside the frame is a control that is
    /// not on screen, and is refused as such: read unchecked it once crashed the test host rather than failing a test.
    /// </summary>
    public static Color At(WriteableBitmap frame, Point point)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        int x = (int)point.X;
        int y = (int)point.Y;
        if (x < 0 || y < 0 || x >= buffer.Size.Width || y >= buffer.Size.Height)
        {
            throw new Xunit.Sdk.XunitException(
                $"({point.X:F0}, {point.Y:F0}) lies outside the {buffer.Size.Width}×{buffer.Size.Height} frame: the control "
                + "sampled is not on screen.");
        }

        int value = Marshal.ReadInt32(buffer.Address, (y * buffer.RowBytes) + (x * 4));
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
