using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;

/// <summary>
/// Observes the application's own Skia compositor context without changing its backend options.
/// The one-pixel custom visual does not create a separate GL renderer or Bluetooth service.
/// </summary>
internal sealed class NativeRenderingProbe : Control
{
    public volatile bool Queried;
    public bool HasGpuContext { get; private set; }
    public string? Vendor { get; private set; }
    public string? Renderer { get; private set; }
    public string? Version { get; private set; }
    public string? Failure { get; private set; }
    public object Receipt => new { Queried, HasGpuContext, Vendor, Renderer, Version, Failure,
        source = "ISkiaSharpApiLeaseFeature within the application's actual compositor OnRender" };

    public static NativeRenderingProbe Attach(Window window)
    {
        var probe = new NativeRenderingProbe { Width = 1, Height = 1, IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var content = window.Content as Control ?? throw new InvalidOperationException("Window content must be a control.");
        window.Content = null;
        window.Content = new Grid { Children = { content, probe } };
        return probe;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ElementComposition.GetElementVisual(this) is not { } parent) return;
        var visual = parent.Compositor.CreateCustomVisual(new Handler(this));
        visual.Size = new Vector2(1, 1);
        ElementComposition.SetElementChildVisual(this, visual);
        visual.SendHandlerMessage("query");
    }

    private sealed class Handler(NativeRenderingProbe probe) : CompositionCustomVisualHandler
    {
        public override void OnMessage(object message) => Invalidate();
        public override void OnRender(ImmediateDrawingContext context)
        {
            if (probe.Queried || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            try
            {
                using var lease = feature.Lease();
                probe.HasGpuContext = lease.GrContext is not null;
                if (probe.HasGpuContext)
                {
                    probe.Vendor = Marshal.PtrToStringAnsi(glGetString(0x1F00));
                    probe.Renderer = Marshal.PtrToStringAnsi(glGetString(0x1F01));
                    probe.Version = Marshal.PtrToStringAnsi(glGetString(0x1F02));
                }
            }
            catch (Exception error) { probe.Failure = error.ToString(); }
            probe.Queried = true;
        }
        [DllImport("av_libglesv2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint glGetString(uint name);
    }

    public static object Capture(Window window, string path)
    {
        var shot = NativeWindowCapture.Save(window.TryGetPlatformHandle()!.Handle, path);
        var colors = new HashSet<uint>();
        for (int offset = 0; offset < shot.BgraPixels.Length; offset += 4 * 43)
            colors.Add(BitConverter.ToUInt32(shot.BgraPixels, offset) & 0x00ffffff);
        if (colors.Count < 30) throw new InvalidOperationException("Visible compositor screenshot has insufficient pixel variation.");
        return new { file = Path.GetFileName(path), shot.Width, shot.Height, sampledColors = colors.Count,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            source = "screen DC BitBlt of the visible window bounds; visual review is still required" };
    }

    public static async Task WaitForPixelsAsync(Window window)
    {
        // ContentDialog and theme transitions can outlast a few low-rate WARP frames.
        // First let their timers settle, then observe four top-level render callbacks.
        await Task.Delay(1200);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remaining = 4;
        void Frame(TimeSpan _)
        {
            if (--remaining == 0) completion.TrySetResult();
            else window.RequestAnimationFrame(Frame);
        }
        window.RequestAnimationFrame(Frame);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
    }
}
