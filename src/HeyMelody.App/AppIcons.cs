using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

public enum AppIcon
{
    Settings, Refresh, Copy, Charging, NoiseControl, Equalizer, Sound, Earbuds, Gestures,
    Hearing, FitTest, Info, History, Battery
}

/// <summary>Resolves interface icons from system or FluentAvalonia fonts with independent fallbacks.</summary>
public static class AppIcons
{
    private const string SegoeFluentName = "Segoe Fluent Icons", BundledSymbolsName = "Symbols";
    private static readonly FontFamily SegoeFluent = new(SegoeFluentName);
    // FluentAvalonia's bundled symbol font maps the same code points, so Windows 10 and previews keep the glyphs.
    private static readonly FontFamily BundledSymbols = new("avares://FluentAvalonia/Fonts#Symbols");
    private static readonly Dictionary<(string Family, int CodePoint), bool> Coverage = [];
    // Original 24-unit outline for platforms without the system battery glyph.
    private const string BatteryOutline = "M2,6 H20 V18 H2 Z M4,8 V16 H18 V8 Z M20,10 H23 V14 H20 Z";

    private abstract record Candidate;
    private sealed record Glyph(int CodePoint) : Candidate;
    private sealed record Vector(string Data, double Viewport) : Candidate;

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
