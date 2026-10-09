using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

/// <summary>Read the displayed Win32 compositor output. Reuses the existing native smoke approach.</summary>
internal static class NativeWindowCapture
{
    internal sealed record Screenshot(int Width, int Height, byte[] BgraPixels);

    public static Screenshot Save(IntPtr window, string path)
    {
        if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException("Native bounds unavailable.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096)
            throw new InvalidOperationException("Native capture bounds are outside the test limit.");
        var screen = GetDC(IntPtr.Zero);
        var memory = screen != IntPtr.Zero ? CreateCompatibleDC(screen) : IntPtr.Zero;
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }
        };
        var pointer = IntPtr.Zero;
        var dib = screen != IntPtr.Zero ? CreateDIBSection(screen, ref info, 0, out pointer, IntPtr.Zero, 0) : IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || dib == IntPtr.Zero || pointer == IntPtr.Zero)
                throw new InvalidOperationException("Native screenshot allocation failed.");
            previous = SelectObject(memory, dib);
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020))
                throw new InvalidOperationException("Native compositor capture failed.");
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(pointer, pixels, 0, pixels.Length);
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, pointer,
                new PixelSize(width, height), new Vector(96, 96), width * 4);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
            return new(width, height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static void PointerMessage(IntPtr window, uint message, int x, int y, bool leftHeld)
    {
        if (x is < 0 or > 32767 || y is < 0 or > 32767)
            throw new ArgumentOutOfRangeException(nameof(x));
        var packed = (y << 16) | (x & 0xffff);
        if (!PostMessage(window, message, leftHeld ? new IntPtr(1) : IntPtr.Zero, new IntPtr(packed)))
            throw new InvalidOperationException("Native test pointer message failed.");
    }

    public static (int X, int Y) ClientPoint(IntPtr window, PixelPoint screenPoint)
    {
        var point = new NativePoint { X = screenPoint.X, Y = screenPoint.Y };
        if (!ScreenToClient(window, ref point)) throw new InvalidOperationException("Native client origin unavailable.");
        return (point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
