using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>Six-band filter illustration, composed with the music spectrum during either audition mode.</summary>
/// <remarks>
/// Colours come from the shared theme (the legend uses the same keys). Brushes, pens and labels are built once per
/// theme and size rather than on every frame, and the drawn spectrum eases between the backend's 20 Hz updates so the
/// curves move at the display rate; <see cref="LeftLevels"/> and the other properties stay the target values.
/// </remarks>
public sealed class HearingRadarChart : Control
{
    private static readonly int[] DisplayOrder = [0, 5, 4, 3, 2, 1];
    private static readonly string[] Labels = ["低频", "极高频", "高频", "中高频", "中频", "中低频"];
    // Time constant of the spectrum easing; a 50 ms update is mostly reached within its own interval.
    private const double EasingSeconds = .06;
    private UiHearingRadar? _radar;
    private IReadOnlyList<double> _spectrum = [];
    private IReadOnlyList<double> _left = [], _right = [];
    private IReadOnlyList<double> _enhancement = [];
    private bool _showEnhancement = true;
    private double[]? _shownSpectrum;
    private bool _easing;
    private TimeSpan? _lastFrame;
    private Palette? _palette;

    public HearingRadarChart()
    {
        Name = "HearingRadar"; Height = 280; MaxWidth = 400;
        AutomationProperties.SetName(this, "黄金听感雷达图");
        AutomationProperties.SetHelpText(this, "左耳绿色，右耳蓝色；增强前与增强后试听时，左右耳曲线随音乐频谱变化。增强后显示橙色音乐频谱。");
        ActualThemeVariantChanged += (_, _) => { _palette = null; InvalidateVisual(); };
    }

    public bool ShowEnhancement => _showEnhancement;
    public IReadOnlyList<double> EnhancementLevels => _enhancement;
    public IReadOnlyList<double> LeftLevels => _left;
    public IReadOnlyList<double> RightLevels => _right;

    public void SetResult(UiHearingRadar? radar)
    {
        if (ReferenceEquals(radar, _radar)) return;
        _radar = radar; UpdateEarSeries(); UpdateSummary(); InvalidateVisual();
    }

    public void SetPreview(string mode, bool playing, IReadOnlyList<double> spectrum)
    {
        _showEnhancement = !(playing && mode == "Original");
        _spectrum = playing && mode is ("Original" or "Enhanced") && spectrum.Count == 6
            && spectrum.All(value => double.IsFinite(value) && value is >= 0 and <= 1)
            ? Array.AsReadOnly(spectrum.ToArray()) : [];
        _enhancement = playing && mode == "Enhanced" ? _spectrum : [];
        UpdateEarSeries();
        if (_spectrum.Count != 6) { _shownSpectrum = null; _easing = false; }
        else
        {
            // The first frame of a new audition starts at its target; later updates ease towards theirs.
            _shownSpectrum ??= _spectrum.ToArray();
            StartEasing();
        }
        InvalidateVisual();
    }

    private void UpdateEarSeries()
    {
        _left = Compose(_radar?.Left, _spectrum);
        _right = Compose(_radar?.Right, _spectrum);
    }

    private static IReadOnlyList<double> Compose(IReadOnlyList<double>? ear, IReadOnlyList<double> spectrum)
    {
        if (ear is not { Count: 6 } || ear.Any(value => !double.IsFinite(value) || value is < .2 or > 1)) return [];
        if (spectrum.Count != 6) return ear;
        // OEM: max(.2, music + offset), offset = -abs(filterDb) / scale <= 0.
        // static = clamp(1 + offset, .2, 1). Since music <= 1, a saturated
        // static radius also saturates the dynamic radius, so this is equivalent.
        return Array.AsReadOnly(ear.Select((value, i) => Math.Clamp(spectrum[i] + value - 1, .2, 1)).ToArray());
    }

    /// <summary>The six values in words for screen readers, which cannot see the polygons.</summary>
    private void UpdateSummary()
    {
        string Describe(IReadOnlyList<double>? ear) => ear is { Count: 6 }
            ? string.Join("，", Enumerable.Range(0, 6).Select(axis => $"{Labels[axis]} {ear[DisplayOrder[axis]] * 100:0}%")) : "无数据";
        AutomationProperties.SetHelpText(this, $"左耳绿色，右耳蓝色。左耳：{Describe(_radar?.Left)}。右耳：{Describe(_radar?.Right)}。");
    }

    private void StartEasing()
    {
        if (_easing || TopLevel.GetTopLevel(this) is not { } top) return;
        _easing = true;
        _lastFrame = null;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        if (!_easing || _shownSpectrum is not { } shown || _spectrum.Count != 6) { _easing = false; return; }
        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, .1) : 0;
        _lastFrame = time;
        var step = 1 - Math.Exp(-dt / EasingSeconds);
        var moving = false;
        for (var i = 0; i < 6; i++)
        {
            shown[i] += (_spectrum[i] - shown[i]) * step;
            if (Math.Abs(_spectrum[i] - shown[i]) > .002) moving = true; else shown[i] = _spectrum[i];
        }
        InvalidateVisual();
        // Idle once settled; the next spectrum update starts the loop again.
        if (moving && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame); else _easing = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _easing = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _palette = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Max(0, Math.Min((Bounds.Width - 100) / 2, (Bounds.Height - 52) / 2));
        if (radius <= 0) return;
        var palette = _palette ??= CreatePalette(center, radius);
        context.DrawGeometry(null, palette.GridPen, palette.Grid);
        for (int axis = 0; axis < 6; axis++)
        {
            var text = palette.Labels[axis];
            var labelPoint = At(center, radius + 19, (-120 + axis * 60) * Math.PI / 180);
            context.DrawText(text, new Point(labelPoint.X - text.Width / 2, labelPoint.Y - text.Height / 2));
        }
        // Drawn curves follow the eased spectrum; the public level properties keep the exact targets.
        var spectrum = (IReadOnlyList<double>?)_shownSpectrum ?? _spectrum;
        var enhancement = _enhancement.Count == 6 ? spectrum : _enhancement;
        if (_showEnhancement) DrawSeries(context, center, radius, enhancement, palette.EnhancedFill, palette.EnhancedPen);
        var left = Compose(_radar?.Left, spectrum);
        var right = Compose(_radar?.Right, spectrum);
        if (left.Count == 6 && right.Count == 6)
        {
            DrawSeries(context, center, radius, left, palette.LeftFill, palette.LeftPen);
            DrawSeries(context, center, radius, right, palette.RightFill, palette.RightPen);
        }
    }

    private static Point At(Point center, double distance, double angle) => center + new Vector(Math.Cos(angle) * distance, Math.Sin(angle) * distance);

    private static void DrawSeries(DrawingContext context, Point center, double radius, IReadOnlyList<double> levels, IBrush fill, IPen pen)
    {
        // Missing levels draw the full outline, which marks the enhancement range on the static result.
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (int i = 0; i < 6; i++)
            {
                double value = levels.Count == 6 && double.IsFinite(levels[DisplayOrder[i]]) ? Math.Clamp(levels[DisplayOrder[i]], 0, 1) : 1;
                Point point = At(center, radius * value, (-120 + i * 60) * Math.PI / 180);
                if (i == 0) path.BeginFigure(point, true); else path.LineTo(point);
            }
            path.EndFigure(true);
        }
        context.DrawGeometry(fill, pen, geometry);
    }

    private Palette CreatePalette(Point center, double radius)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        Color left = Resource("HmChartLeftColor", dark ? "#65D68D" : "#13964A"), right = Resource("HmChartRightColor", dark ? "#72B5FF" : "#1471CE"),
            enhanced = Resource("HmChartEnhancedColor", dark ? "#FFC177" : "#C57416");
        var label = ResourceBrush("HmChartLabelBrush", dark ? "#D5D5D5" : "#606060");
        var grid = ResourceBrush("HmChartGridBrush", dark ? "#35FFFFFF" : "#19000000");
        var gridGeometry = new GeometryGroup();
        for (int ring = 1; ring <= 4; ring++)
            gridGeometry.Children.Add(new EllipseGeometry(new Rect(center.X - radius * ring / 4, center.Y - radius * ring / 4, radius * ring / 2, radius * ring / 2)));
        for (int axis = 0; axis < 6; axis++)
            gridGeometry.Children.Add(new LineGeometry(center, At(center, radius, (-120 + axis * 60) * Math.PI / 180)));
        var labels = Labels.Select(text => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, label)).ToArray();
        return new Palette(gridGeometry, new ImmutablePen(grid, 1), labels,
            new ImmutableSolidColorBrush(left, .12), new ImmutablePen(new ImmutableSolidColorBrush(left), 1.7),
            new ImmutableSolidColorBrush(right, .10), new ImmutablePen(new ImmutableSolidColorBrush(right), 1.7),
            new ImmutableSolidColorBrush(enhanced, .07), new ImmutablePen(new ImmutableSolidColorBrush(enhanced), 1.7));

        Color Resource(string key, string fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Color.Parse(fallback);
        IImmutableSolidColorBrush ResourceBrush(string key, string fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush
                ? new ImmutableSolidColorBrush(brush.Color, brush.Opacity) : new ImmutableSolidColorBrush(Color.Parse(fallback));
    }

    private sealed record Palette(Geometry Grid, IPen GridPen, FormattedText[] Labels,
        IBrush LeftFill, IPen LeftPen, IBrush RightFill, IPen RightPen, IBrush EnhancedFill, IPen EnhancedPen);
}
