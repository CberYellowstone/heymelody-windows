using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Resources;

namespace HeyMelodyWindows.App;

/// <summary>A bitmap resource description; loading is deferred until it is displayed.</summary>
public sealed record BitmapVisual(Uri Source);
public sealed record EncoFree4ModelVisual;
public sealed record EncoFree4DynaudioModelVisual;
public sealed record GenericProductVisual;

/// <summary>Built-in presentation data and its default template, independent of device identification.</summary>
public static class DeviceVisuals
{
    public static BitmapVisual X4Product { get; } = new(new Uri(
        "avares://HeyMelody/Assets/Devices/EncoX4/x4-pods-with-earphones.png"));
    public static GenericProductVisual GenericProduct { get; } = new();
    public static IDataTemplate DefaultTemplate { get; } = new DeviceVisualTemplate();

    private static readonly object BitmapLock = new();
    private static readonly Dictionary<Uri, Bitmap> Bitmaps = new();
    private static readonly Lazy<BundledResourceStore> Bundled = new(() => new());

    /// <summary>Decodes the bundled default artwork on a worker thread so the first window shows it without a decode pause.</summary>
    internal static Task PrewarmBundledArtworkAsync() => Task.Run(() =>
    {
        try { GetBitmap(X4Product.Source); }
        catch (Exception) { }
    });

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

    // The bundled HeyMelody glyph index is fixed for the process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> AncGlyphPaths = new();

    internal static Control CreateAncGlyph(string key, string brushKey)
    {
        var name = key switch { "off" => "melody_ui_reduction_noise_close", "transparency" => "melody_ui_reduction_noise_transparent",
            "adaptive" or "smart" => "melody_ui_reduction_noise_adaptive", "weak" => "melody_ui_reduction_noise_weak", _ => "melody_ui_reduction_noise_strong" };
        try
        {
            var path = AncGlyphPaths.GetOrAdd(name, entry =>
                Bundled.Value.FindNamedAsset(entry) is { } found ? Bundled.Value.FullPath(found) : null);
            if (path is not null)
            {
                var glyph = new Border { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    OpacityMask = new ImageBrush(GetBitmap(new Uri(path))) { Stretch = Stretch.Uniform } };
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
        public bool Match(object? data) => data is BitmapVisual or FileBitmapVisual or DeviceModelVisual or LottieVisual or AnimatedWebpVisual or EncoFree4ModelVisual or EncoFree4DynaudioModelVisual or GenericProductVisual;

        public Control? Build(object? data) => data switch
        {
            BitmapVisual image => new Image { Source = GetBitmap(image.Source), Stretch = Stretch.Uniform },
            FileBitmapVisual image => new ArtworkBitmapView(image),
            DeviceModelVisual model => new DeviceModelView(model),
            LottieVisual animation => new DeviceLottieView(animation),
            AnimatedWebpVisual animation => new DeviceAnimatedWebpView(animation),
            EncoFree4ModelVisual => new EncoFree4ModelView(),
            EncoFree4DynaudioModelVisual => new EncoFree4DynaudioModelView(),
            GenericProductVisual => CreateGenericProduct(),
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
    }
}
