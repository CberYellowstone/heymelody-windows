using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

public enum AppIcon
{
    Settings, Refresh, Copy, Charging, NoiseControl, Equalizer, Sound, Earbuds, Gestures,
    Hearing, FitTest, Info, History, Battery, BatteryLeft, BatteryRight, BatteryCase
}

/// <summary>Resolves interface icons from system or FluentAvalonia fonts, bundled HeyMelody device shapes and independent fallbacks.</summary>
public static class AppIcons
{
    private const string SegoeFluentName = "Segoe Fluent Icons", BundledSymbolsName = "Symbols";
    private static readonly FontFamily SegoeFluent = new(SegoeFluentName);
    // FluentAvalonia's bundled symbol font maps the same code points, so Windows 10 and previews keep the glyphs.
    private static readonly FontFamily BundledSymbols = new("avares://FluentAvalonia/Fonts#Symbols");
    private static readonly Dictionary<(string Family, int CodePoint), bool> Coverage = [];
    // Original 24-unit outline for platforms without the system battery glyph.
    private const string BatteryOutline = "M2,6 H20 V18 H2 Z M4,8 V16 H18 V8 Z M20,10 H23 V14 H20 Z";

    // Exact pathData from HeyMelody 17.6.5 battery hint vectors; see docs/research/battery-device-icons.md.
    // Android alpha/color are recorded as provenance; the desktop keeps its native foreground and opacity.
    private static readonly string[] BatteryLeftPaths =
    [
        "M5.406,1.676C3.908,3.173 4.347,5.684 5.923,7.258C6.216,7.551 6.541,7.805 6.886,8.014C6.441,6.762 6.517,5.322 7.239,4.072C7.818,3.069 8.715,2.362 9.725,1.999C10.005,1.899 10.286,1.837 10.565,1.81C8.991,0.556 6.775,0.309 5.406,1.676ZM10.507,7.84C10.111,7.611 10.139,6.822 10.57,6.076C11.001,5.33 11.672,4.911 12.068,5.14C12.465,5.369 12.437,6.159 12.006,6.904C11.575,7.65 10.904,8.069 10.507,7.84ZM10.161,3.216C11.281,2.814 12.489,3.41 12.962,4.442C13.406,5.41 13.435,6.532 12.883,7.487C12.406,8.313 11.6,8.827 10.704,9.014C9.639,9.236 8.505,8.668 8.117,7.614C7.777,6.692 7.83,5.633 8.358,4.719C8.778,3.993 9.426,3.48 10.161,3.216Z",
        "M8.692,9.955C7.761,9.855 6.846,9.51 6.055,9.001V13.027C6.055,14.071 6.901,14.917 7.945,14.917C8.989,14.917 9.835,14.071 9.835,13.027V10.293C9.441,10.235 9.055,10.122 8.692,9.955Z",
    ];
    private static readonly string[] BatteryRightPaths =
    [
        "M12.368,1.673C13.865,3.17 13.427,5.681 11.851,7.255C11.562,7.544 11.242,7.795 10.902,8.002C11.344,6.753 11.266,5.317 10.546,4.07C9.967,3.067 9.071,2.36 8.061,1.998C7.778,1.896 7.493,1.834 7.211,1.808C8.784,0.553 11,0.306 12.368,1.673ZM7.287,7.838C7.683,7.609 7.655,6.819 7.225,6.074C6.794,5.328 6.123,4.909 5.727,5.138C5.331,5.367 5.359,6.156 5.79,6.902C6.22,7.648 6.891,8.067 7.287,7.838ZM7.625,3.214C6.506,2.812 5.298,3.408 4.825,4.439C4.381,5.407 4.352,6.53 4.904,7.485C5.381,8.311 6.187,8.825 7.083,9.012C8.147,9.234 9.28,8.666 9.668,7.612C10.008,6.69 9.955,5.631 9.427,4.717C9.008,3.991 8.359,3.478 7.625,3.214Z",
        "M9.086,9.956C10.02,9.857 10.94,9.511 11.734,9V13.028C11.734,14.071 10.888,14.917 9.845,14.917C8.801,14.917 7.956,14.071 7.956,13.028V10.29C8.344,10.233 8.727,10.12 9.086,9.956Z",
    ];
    private static readonly string[] BatteryCasePaths =
    [
        "M7.976,1.523H7.992H11.039H11.055H11.056C11.394,1.523 11.58,1.523 11.739,1.532C14.304,1.666 16.444,3.377 17.212,5.716H16.795C16.795,5.716 16.795,5.716 16.795,5.716H13.267C13.165,5.279 12.772,4.954 12.303,4.954H6.665C6.197,4.954 5.804,5.279 5.701,5.716H2.208C2.208,5.716 2.208,5.716 2.208,5.716H1.819C2.587,3.377 4.727,1.666 7.292,1.532C7.451,1.523 7.637,1.523 7.976,1.523H7.976ZM16.676,6.706H17.447C17.477,6.901 17.497,7.099 17.507,7.3C17.516,7.459 17.516,7.645 17.516,7.984V8V8.015C17.516,8.354 17.516,8.54 17.507,8.7C17.344,11.815 14.855,14.304 11.739,14.467C11.58,14.476 11.394,14.476 11.055,14.476H11.039H7.992H7.976C7.637,14.476 7.451,14.476 7.292,14.467C4.176,14.304 1.687,11.815 1.524,8.7C1.516,8.54 1.516,8.354 1.516,8.016V8.015V8V7.984V7.984C1.516,7.645 1.516,7.459 1.524,7.3C1.534,7.099 1.555,6.901 1.584,6.706H2.356C2.356,6.706 2.356,6.707 2.356,6.707H5.787C5.952,7.023 6.283,7.24 6.665,7.24H12.303C12.685,7.24 13.017,7.023 13.182,6.707H16.676C16.676,6.707 16.676,6.706 16.676,6.706ZM9.501,10.287C9.922,10.287 10.263,9.946 10.263,9.525C10.263,9.104 9.922,8.763 9.501,8.763C9.081,8.763 8.74,9.104 8.74,9.525C8.74,9.946 9.081,10.287 9.501,10.287Z",
    ];

    private abstract record Candidate;
    private sealed record Glyph(int CodePoint) : Candidate;
    private sealed record Vector(string Data, double Viewport) : Candidate;
    private sealed record ClippedVector(string[] Paths, Rect Clip) : Candidate;

    private static Candidate[] Candidates(AppIcon icon) => icon switch
    {
        AppIcon.Settings => [new Glyph(0xE713)],
        AppIcon.Refresh => [new Glyph(0xE72C)],
        AppIcon.Copy => [new Glyph(0xE8C8)],
        AppIcon.Charging => [new Glyph(0xE945)],
        AppIcon.NoiseControl => [new Glyph(0xF61F)],
        AppIcon.Equalizer => [new Glyph(0xE9E9)],
        AppIcon.Sound => [new Glyph(0xE995)],
        AppIcon.Earbuds => [new Glyph(0xF4C0)],
        AppIcon.Gestures => [new Glyph(0xE815)],
        AppIcon.Hearing => [new Glyph(0xF270)],
        AppIcon.FitTest => [new Glyph(0xF27C)],
        AppIcon.Info => [new Glyph(0xE946)],
        AppIcon.History => [new Glyph(0xE81C)],
        AppIcon.Battery => [new Glyph(0xE850), new Vector(BatteryOutline, 24)],
        AppIcon.BatteryLeft => [new ClippedVector(BatteryLeftPaths, new Rect(0, 0, 19, 16))],
        AppIcon.BatteryRight => [new ClippedVector(BatteryRightPaths, new Rect(0, 0, 19, 16))],
        AppIcon.BatteryCase => [new ClippedVector(BatteryCasePaths, new Rect(1.5, 0, 16, 16))],
        _ => throw new ArgumentOutOfRangeException(nameof(icon))
    };

    /// <summary>Creates a new icon source; icon sources are never shared between controls.</summary>
    public static FAIconSource Source(AppIcon icon, double size = 20)
    {
        foreach (var candidate in Candidates(icon))
        {
            switch (candidate)
            {
                case Glyph glyph when Covers(SegoeFluent, SegoeFluentName, glyph.CodePoint):
                    return Font(SegoeFluent, glyph.CodePoint, size);
                case Glyph glyph when Covers(BundledSymbols, BundledSymbolsName, glyph.CodePoint):
                    return Font(BundledSymbols, glyph.CodePoint, size);
                case Vector vector:
                    // Keep the fallback's padding at the requested icon size.
                    var data = Geometry.Parse(vector.Data);
                    data.Transform = new ScaleTransform(size / vector.Viewport, size / vector.Viewport);
                    return new FAPathIconSource { Data = data, Stretch = Stretch.None };
                case ClippedVector vector:
                    Geometry? merged = null;
                    foreach (var pathData in vector.Paths)
                    {
                        var path = PathGeometry.Parse(pathData);
                        path.FillRule = FillRule.EvenOdd;
                        merged = merged is null ? path : new CombinedGeometry(GeometryCombineMode.Union, merged, path);
                    }
                    var clipped = new CombinedGeometry(GeometryCombineMode.Intersect, merged!, new RectangleGeometry(vector.Clip));
                    // Keep the original 19 x 16 viewport, uniformly centred in the square icon slot.
                    var scale = Math.Min(size / 19, size / 16);
                    clipped.Transform = new MatrixTransform(new Matrix(scale, 0, 0, scale,
                        (size - 19 * scale) / 2, (size - 16 * scale) / 2));
                    return new FAPathIconSource { Data = clipped, Stretch = Stretch.None };
            }
        }
        return new FASymbolIconSource { Symbol = icon is AppIcon.NoiseControl or AppIcon.Hearing or AppIcon.FitTest or AppIcon.Earbuds
            ? FASymbol.Audio : FASymbol.Help, FontSize = size };
    }

    public static FAIconSourceElement Element(AppIcon icon, double size = 16) =>
        new() { IconSource = Source(icon, size), Width = size, Height = size };

    private static FAFontIconSource Font(FontFamily family, int codePoint, double size) =>
        new() { FontFamily = family, Glyph = char.ConvertFromUtf32(codePoint), FontSize = size };

    private static bool Covers(FontFamily family, string familyName, int codePoint)
    {
        if (Coverage.TryGetValue((familyName, codePoint), out var covered)) return covered;
        // A missing family resolves to the default text font, so compare the family that was found.
        covered = FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var typeface)
            && string.Equals(typeface.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
            && typeface.CharacterToGlyphMap.ContainsGlyph(codePoint);
        Coverage[(familyName, codePoint)] = covered;
        return covered;
    }
}

/// <summary>XAML access to <see cref="AppIcons"/>, for example <c>{local:AppIcon Equalizer}</c>.</summary>
public sealed class AppIconExtension : MarkupExtension
{
    public AppIconExtension() { }
    public AppIconExtension(AppIcon icon) => Icon = icon;
    public AppIcon Icon { get; set; }
    public double Size { get; set; } = 20;
    public override object ProvideValue(IServiceProvider serviceProvider) => AppIcons.Source(Icon, Size);
}
