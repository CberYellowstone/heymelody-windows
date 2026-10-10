using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>Six-band filter illustration, with a separate music-source enhancement overlay.</summary>
public sealed class HearingRadarChart : Control
{
    private static readonly int[] DisplayOrder = [0, 5, 4, 3, 2, 1];
    private static readonly string[] Labels = ["低频", "极高频", "高频", "中高频", "中频", "中低频"];
    private UiHearingRadar? _radar;
    private IReadOnlyList<double> _enhancement = [];
    private bool _showEnhancement = true;

    public HearingRadarChart()
    {
        Name = "HearingRadar"; Height = 280; MaxWidth = 400;
        AutomationProperties.SetName(this, "黄金听感雷达图");
        AutomationProperties.SetHelpText(this, "左耳绿色，右耳蓝色，橙色为增强区间示意；增强试听时随音乐频谱变化。");
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public bool ShowEnhancement => _showEnhancement;
    public IReadOnlyList<double> EnhancementLevels => _enhancement;
    public void SetResult(UiHearingRadar? radar) { _radar = radar; InvalidateVisual(); }
    public void SetPreview(string mode, bool playing, IReadOnlyList<double> spectrum)
    {
        _showEnhancement = !(playing && mode == "Original");
        _enhancement = playing && mode == "Enhanced" && spectrum.Count == 6 ? spectrum : [];
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        Color left = Color.Parse(dark ? "#65D68D" : "#13964A"), right = Color.Parse(dark ? "#72B5FF" : "#1471CE"),
            enhanced = Color.Parse(dark ? "#FFC177" : "#C57416");
        IBrush label = new SolidColorBrush(Color.Parse(dark ? "#D5D5D5" : "#606060"));
        var gridPen = new Pen(new SolidColorBrush(Color.Parse(dark ? "#35FFFFFF" : "#19000000")), 1);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Max(0, Math.Min((Bounds.Width - 100) / 2, (Bounds.Height - 52) / 2));
        if (radius <= 0) return;
        for (int ring = 1; ring <= 4; ring++) context.DrawEllipse(null, gridPen, center, radius * ring / 4, radius * ring / 4);
        for (int axis = 0; axis < 6; axis++)
        {
            double angle = (-120 + axis * 60) * Math.PI / 180;
            var edge = At(radius, angle);
            context.DrawLine(gridPen, center, edge);
            var text = new FormattedText(Labels[axis], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, label);
            var labelPoint = At(radius + 19, angle);
            context.DrawText(text, new Point(labelPoint.X - text.Width / 2, labelPoint.Y - text.Height / 2));
        }
        if (_showEnhancement) DrawSeries(_enhancement, enhanced, .07);
        if (_radar is { Left.Count: 6, Right.Count: 6 })
        {
            DrawSeries(_radar.Left, left, .12);
            DrawSeries(_radar.Right, right, .10);
        }

        Point At(double distance, double angle) => center + new Vector(Math.Cos(angle) * distance, Math.Sin(angle) * distance);
        void DrawSeries(IReadOnlyList<double> levels, Color color, double alpha)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                for (int i = 0; i < 6; i++)
                {
                    double value = levels.Count == 6 && double.IsFinite(levels[DisplayOrder[i]]) ? Math.Clamp(levels[DisplayOrder[i]], 0, 1) : 1;
                    Point point = At(radius * value, (-120 + i * 60) * Math.PI / 180);
                    if (i == 0) path.BeginFigure(point, true); else path.LineTo(point);
                }
                path.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(color, alpha), new Pen(new SolidColorBrush(color), 1.7), geometry);
        }
    }
}
