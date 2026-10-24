using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using PathShape = Avalonia.Controls.Shapes.Path;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>An anchored, light-dismiss teaching tip with a title, close button and pointing tail.</summary>
/// <remarks>Uses Avalonia's popup lifetime directly so closing during layout or animation is synchronous.</remarks>
public sealed class RadarTeachingTip : Popup
{
    private const string BackgroundBrush = "SolidBackgroundFillColorBaseBrush";
    private const string BorderBrush = "SurfaceStrokeColorDefaultBrush";
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 14, LineHeight = 22,
        MaxWidth = 296, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _surface = new() { RowDefinitions = new("8,Auto,8"), Margin = new Thickness(12), MaxWidth = 360 };
    private readonly PathShape _topTail = Tail("M 0,8 L 8,0 L 16,8");
    private readonly PathShape _bottomTail = Tail("M 0,0 L 8,8 L 16,0");
    private readonly TranslateTransform _topPosition = new(0, 1), _bottomPosition = new(0, -1);
    private readonly Button _close;

    public Control? Target { get => PlacementTarget; set => PlacementTarget = value; }
    public string Title
    {
        get => _title.Text ?? "";
        set { _title.Text = value; AutomationProperties.SetName(_surface, value); }
    }
    public string Subtitle { get => _subtitle.Text ?? ""; set => _subtitle.Text = value; }

    public RadarTeachingTip()
    {
        Placement = PlacementMode.Top;
        PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.All;
        IsLightDismissEnabled = true;
        ShouldUseOverlayLayer = true;
        WindowManagerAddShadowHint = false;
        _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush"));
        _subtitle.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
        var closeGlyph = new PathShape { Width = 10, Height = 10, Stretch = Stretch.Fill,
            StrokeThickness = 1.2, Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10") };
        closeGlyph.Bind(PathShape.StrokeProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
        _close = new Button { Name = "HearingRadarHelpClose", Content = closeGlyph, Width = 28, Height = 28,
            Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        _close.Classes.Add("subtle");
        AutomationProperties.SetName(_close, "关闭听感雷达图帮助");
        _close.Click += (_, _) => Dismiss(restoreFocus: true);
        var text = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 32, 0), Children = { _title, _subtitle } };
        var body = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 20, Color = Color.FromArgb(48, 0, 0, 0) }),
            Child = new Grid { Children = { text, _close } } };
        body.Bind(Border.BackgroundProperty, new DynamicResourceExtension(BackgroundBrush));
        body.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(BorderBrush));
        _topTail.RenderTransform = _topPosition;
        _bottomTail.RenderTransform = _bottomPosition;
        _topTail.IsVisible = false;
        Grid.SetRow(body, 1);
        Grid.SetRow(_bottomTail, 2);
        _surface.Children.Add(body);
        _surface.Children.Add(_topTail);
        _surface.Children.Add(_bottomTail);
        Child = _surface;
        _surface.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Dismiss(restoreFocus: true);
        }, RoutingStrategies.Tunnel);
        // Focusing an overlay control must not scroll the underlying wizard through its logical parent.
        _surface.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        _surface.LayoutUpdated += (_, _) => UpdateTail();
        Opened += (_, _) => { UpdateTail(); _close.Focus(NavigationMethod.Tab); };
        DetachedFromVisualTree += (_, _) => Dismiss();
    }

    /// <summary>Closes without moving focus unless the keyboard or close button explicitly requests it.</summary>
    public void Dismiss(bool restoreFocus = false)
    {
        IsOpen = false;
        if (restoreFocus && Target is { IsVisible: true } target && TopLevel.GetTopLevel(target) is not null)
            target.Focus(NavigationMethod.Tab);
    }

    private void UpdateTail()
    {
        if (!IsOpen || Target is not { } target || TopLevel.GetTopLevel(target) is null || TopLevel.GetTopLevel(_surface) is null) return;
        var point = _surface.PointToClient(target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2)));
        var belowTarget = point.Y < _surface.Bounds.Height / 2;
        _topTail.IsVisible = belowTarget;
        _bottomTail.IsVisible = !belowTarget;
        var center = Math.Clamp(point.X, 28, Math.Max(28, _surface.Bounds.Width - 28));
        _topPosition.X = _bottomPosition.X = center - _surface.Bounds.Width / 2;
    }

    private static PathShape Tail(string geometry)
    {
        var tail = new PathShape { Width = 16, Height = 8, HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Fill, StrokeThickness = 1, Data = Geometry.Parse(geometry), IsHitTestVisible = false };
        tail.Bind(PathShape.FillProperty, new DynamicResourceExtension(BackgroundBrush));
        tail.Bind(PathShape.StrokeProperty, new DynamicResourceExtension(BorderBrush));
        return tail;
    }
}
