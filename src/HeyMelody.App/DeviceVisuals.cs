using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Resources;
using Path = Avalonia.Controls.Shapes.Path;

namespace HeyMelodyWindows.App;

/// <summary>A bitmap resource description; loading is deferred until it is displayed.</summary>
public sealed record BitmapVisual(Uri Source);
public sealed record EncoFree4ModelVisual;
public sealed record GenericProductVisual;
public sealed record GenericDeviceIconVisual;

/// <summary>Built-in presentation data and its default template, independent of device identification.</summary>
public static class DeviceVisuals
{
    public static BitmapVisual X4Product { get; } = new(new Uri(
        "avares://HeyMelody/Assets/Devices/EncoX4/x4-pods-with-earphones.png"));
    public static BitmapVisual X4Icon { get; } = new(new Uri(
        "avares://HeyMelody/Assets/Devices/EncoX4/x4-both-earphones.png"));
    public static GenericProductVisual GenericProduct { get; } = new();
    public static GenericDeviceIconVisual GenericIcon { get; } = new();
    public static IDataTemplate DefaultTemplate { get; } = new DeviceVisualTemplate();

    private static readonly object BitmapLock = new();
    private static readonly Dictionary<Uri, Bitmap> Bitmaps = new();
    private static readonly Lazy<BundledResourceStore> Bundled = new(() => new());

    private static Bitmap GetBitmap(Uri source)
    {
        lock (BitmapLock)
        {
            if (Bitmaps.TryGetValue(source, out var bitmap)) return bitmap;
            using var stream = source.IsFile ? File.OpenRead(source.LocalPath) : AssetLoader.Open(source);
            bitmap = new Bitmap(stream);
            Bitmaps.Add(source, bitmap);
            return bitmap;
        }
    }

    internal static Control CreateAncGlyph(string source, string key, string brushKey)
    {
        var name = key switch { "off" => "melody_ui_reduction_noise_close", "transparency" => "melody_ui_reduction_noise_transparent",
            "adaptive" or "smart" => "melody_ui_reduction_noise_adaptive", "weak" => "melody_ui_reduction_noise_weak", _ => "melody_ui_reduction_noise_strong" };
        var selectedSource = source == "WirelessEarphones" ? ResourceSource.WirelessEarphones : ResourceSource.HeyMelody;
        try
        {
            var asset = Bundled.Value.FindNamedAsset(selectedSource, name);
            if (asset is not null)
            {
                var glyph = new Border { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    OpacityMask = new ImageBrush(GetBitmap(new Uri(Bundled.Value.FullPath(asset)))) { Stretch = Stretch.Uniform } };
                glyph.Bind(Border.BackgroundProperty, new DynamicResourceExtension(brushKey));
                return glyph;
            }
        }
        catch (IOException) { }
        var fallback = new FASymbolIcon { Symbol = key switch { "off" => FASymbol.Cancel, "transparency" => FASymbol.Volume, "adaptive" or "smart" => FASymbol.Sync, _ => FASymbol.Audio }, FontSize = 24 };
        fallback.Bind(FAIconElement.ForegroundProperty, new DynamicResourceExtension(brushKey));
        return fallback;
    }

    /// <summary>Releases shared bitmaps after all application windows have closed.</summary>
    public static void DisposeResources()
    {
        lock (BitmapLock)
        {
            foreach (var bitmap in Bitmaps.Values) bitmap.Dispose();
            Bitmaps.Clear();
        }
    }

    private sealed class DeviceVisualTemplate : IDataTemplate
    {
        public bool Match(object? data) => data is BitmapVisual or FileBitmapVisual or DeviceModelVisual or LottieVisual or EncoFree4ModelVisual or GenericProductVisual or GenericDeviceIconVisual;

        public Control? Build(object? data) => data switch
        {
            BitmapVisual image => new Image { Source = GetBitmap(image.Source), Stretch = Stretch.Uniform },
            FileBitmapVisual image => new ArtworkBitmapView(image),
            DeviceModelVisual model => new DeviceModelView(model),
            LottieVisual animation => new DeviceLottieView(animation),
            EncoFree4ModelVisual => new EncoFree4ModelView(),
            GenericProductVisual => CreateGenericProduct(),
            GenericDeviceIconVisual => CreateGenericIcon(),
            _ => null
        };

        private static Control CreateGenericProduct()
        {
            var icon = new FASymbolIcon { Symbol = FASymbol.Audio, FontSize = 96 };
            icon.Bind(FAIconElement.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
            var shell = new Border
            {
                Width = 220,
                Height = 220,
                CornerRadius = new CornerRadius(110),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = icon
            };
            shell.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ControlFillColorDefaultBrush"));
            return new Viewbox { Stretch = Stretch.Uniform, MaxWidth = 220, MaxHeight = 220, Child = shell };
        }

        private static Control CreateGenericIcon()
        {
            var outline = new Path
            {
                Data = Geometry.Parse("M4,14 L4,11 A8,8 0 0 1 20,11 L20,14 M4,13 L7,13 L7,21 L4,21 Q2,21 2,19 L2,15 Q2,13 4,13 M20,13 L17,13 L17,21 L20,21 Q22,21 22,19 L22,15 Q22,13 20,13"),
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };
            outline.Bind(Shape.StrokeProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush"));
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(outline);
            return new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
        }
    }
}
