using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.Calibration.UI;

internal static class NativeResultDetailCapture
{
    public static async Task DescriptionAsync(Window window, Control view, string path)
    {
        var description = Named<TextBlock>(view, "HearingResultDescription");
        await RevealAsync(window, view, description, alignBottom: false);
        Save(window, path, ScreenBounds(description), margin: 8);
    }

    public static async Task LegendAsync(Window window, Control view, string path)
    {
        var legend = Named<Control>(view, "HearingRadarLegend");
        await RevealAsync(window, view, legend, alignBottom: true);
        Save(window, path, ScreenBounds(legend), margin: 8);
    }

    public static void Help(Window window, Control view, RadarTeachingTip tip, string path)
    {
        var legend = Named<Control>(view, "HearingRadarLegend");
        var popup = tip.Child as Control ?? throw new InvalidOperationException("Radar help must have visible popup content.");
        RequireAboveActionBar(view, legend);
        RequireAboveActionBar(view, popup);
        Save(window, path, ScreenBounds(legend).Union(ScreenBounds(popup)), margin: 12);
    }

    private static async Task RevealAsync(Window window, Control view, Control target, bool alignBottom)
    {
        var scroll = Named<ScrollViewer>(view, "PageScroll");
        var actionBar = Named<Border>(view, "ActionBar");
        target.BringIntoView(new Rect(0, 0, target.Bounds.Width, target.Bounds.Height + 84));
        Dispatcher.UIThread.RunJobs();
        double top = target.TranslatePoint(default, view)!.Value.Y;
        double desired = alignBottom
            ? actionBar.TranslatePoint(default, view)!.Value.Y - 20 - target.Bounds.Height
            : scroll.TranslatePoint(default, view)!.Value.Y + 12;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + top - desired));
        Dispatcher.UIThread.RunJobs();
        await NativeRenderingProbe.WaitForPixelsAsync(window);
        RequireAboveActionBar(view, target);
    }

    private static void RequireAboveActionBar(Control view, Control target)
    {
        var actionBar = Named<Border>(view, "ActionBar");
        var scroll = Named<ScrollViewer>(view, "PageScroll");
        var bounds = ScreenBounds(target);
        double top = ScreenBounds(scroll).Top;
        double bottom = ScreenBounds(actionBar).Top;
        if (bounds.Top < top || bounds.Bottom > bottom - 2)
            throw new InvalidOperationException($"Detail capture must show all of {target.Name ?? target.GetType().Name} above the action bar: {bounds.Top}..{bounds.Bottom}, viewport={top}..{bottom}.");
    }

    private static Rect ScreenBounds(Control control)
    {
        var start = control.PointToScreen(default);
        var end = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        return new Rect(start.X, start.Y, end.X - start.X, end.Y - start.Y);
    }

    // Full source frames stay local. Crops use actual control bounds, including the complete popup tail.
    private static void Save(Window window, string detailPath, Rect content, int margin)
    {
        string sourcePath = Path.Combine(Path.GetDirectoryName(detailPath)!, Path.GetFileNameWithoutExtension(detailPath) + "-source.png");
        var shot = NativeWindowCapture.Save(window.TryGetPlatformHandle()!.Handle, sourcePath);
        int padding = (int)Math.Ceiling(margin * window.RenderScaling);
        int contentLeft = (int)Math.Floor(content.Left - window.Position.X);
        int contentTop = (int)Math.Floor(content.Top - window.Position.Y);
        int contentRight = (int)Math.Ceiling(content.Right - window.Position.X);
        int contentBottom = (int)Math.Ceiling(content.Bottom - window.Position.Y);
        if (contentLeft < 0 || contentTop < 0 || contentRight > shot.Width || contentBottom > shot.Height)
            throw new InvalidOperationException($"Detail capture would clip its target: content={contentLeft},{contentTop}..{contentRight},{contentBottom}; frame={shot.Width}x{shot.Height}.");
        int left = Math.Max(0, contentLeft - padding);
        int top = Math.Max(0, contentTop - padding);
        int right = Math.Min(shot.Width, contentRight + padding);
        int bottom = Math.Min(shot.Height, contentBottom + padding);
        int width = right - left, height = bottom - top;
        var pixels = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(shot.BgraPixels, ((top + row) * shot.Width + left) * 4, pixels, row * width * 4, width * 4);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, pin.AddrOfPinnedObject(),
                new PixelSize(width, height), new Vector(96, 96), width * 4);
            using var file = new FileStream(detailPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
        }
        finally { pin.Free(); }
        File.WriteAllText(detailPath + ".bounds.json", JsonSerializer.Serialize(new
        {
            width, height, crop = new { left, top, right, bottom },
            content = new { content.X, content.Y, content.Width, content.Height },
            completeTarget = true, requestedMargin = padding,
            marginClippedAtFrameEdge = left != contentLeft - padding || top != contentTop - padding
                || right != contentRight + padding || bottom != contentBottom + padding,
            source = Path.GetFileName(sourcePath)
        }));
    }

    private static T Named<T>(Control view, string name) where T : Control =>
        view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
