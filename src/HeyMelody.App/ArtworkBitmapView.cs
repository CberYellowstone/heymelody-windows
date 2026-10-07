using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace HeyMelodyWindows.App;

internal sealed class ArtworkBitmapView(FileBitmapVisual visual) : UserControl
{
    private Bitmap? _bitmap;
    private IDisposable? _lease;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            _lease = visual.File.Acquire();
            using var stream = visual.File.Open();
            _bitmap = new Bitmap(stream);
            Content = new Image { Source = _bitmap, Stretch = Stretch.Uniform };
        }
        catch
        {
            _bitmap?.Dispose(); _bitmap = null;
            _lease?.Dispose(); _lease = null;
            Content = DeviceVisuals.DefaultTemplate.Build(visual.Fallback);
        }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Content = null;
        _bitmap?.Dispose(); _bitmap = null;
        _lease?.Dispose(); _lease = null;
        base.OnDetachedFromVisualTree(e);
    }
}
