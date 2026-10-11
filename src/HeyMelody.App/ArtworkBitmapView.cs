using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace HeyMelodyWindows.App;

/// <summary>Downloaded product artwork, decoded off the UI thread so a device switch never stalls a frame.</summary>
internal sealed class ArtworkBitmapView(FileBitmapVisual visual) : UserControl
{
    private Bitmap? _bitmap;
    private IDisposable? _lease;
    private int _generation;

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var generation = ++_generation;
        try
        {
            _lease = visual.File.Acquire();
            // A 1440 px PNG takes tens of milliseconds to decode; the view stays empty for that moment.
            var bitmap = await Task.Run(() =>
            {
                using var stream = visual.File.Open();
                return new Bitmap(stream);
            });
            if (generation != _generation) { bitmap.Dispose(); return; }
            _bitmap = bitmap;
            Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }
        catch (Exception)
        {
            if (generation != _generation) return;
            Release();
            Content = DeviceVisuals.DefaultTemplate.Build(visual.Fallback);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ++_generation;
        Content = null;
        Release();
        base.OnDetachedFromVisualTree(e);
    }

    private void Release()
    {
        _bitmap?.Dispose(); _bitmap = null;
        _lease?.Dispose(); _lease = null;
    }
}
