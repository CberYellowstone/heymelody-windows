using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.Calibration.UI;

internal static class HearingResultUiAssertions
{
    public static void LegendIsCentered(Control view)
    {
        foreach (var (name, resource) in new[] { ("HearingLegendLeft", "HmChartLeftBrush"),
            ("HearingLegendRight", "HmChartRightBrush"), ("HearingLegendEnhanced", "HmChartEnhancedBrush") })
        {
            var row = Named<Control>(view, name);
            var dot = Named<Shape>(row, name + "Dot");
            var label = Named<TextBlock>(row, name + "Label");
            var dotCenter = dot.TranslatePoint(new Point(dot.Bounds.Width / 2, dot.Bounds.Height / 2), row);
            var labelCenter = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), row);
            if (dotCenter is null || labelCenter is null || dot.Bounds.Width <= 0 || dot.Bounds.Height <= 0 || label.Bounds.Height <= 0
                || Math.Abs(dotCenter.Value.Y - labelCenter.Value.Y) > .5)
                throw new InvalidOperationException("The radar legend dot and label must share their vertical center: " + name);
            if (dot.Fill is not ISolidColorBrush fill || fill.Opacity <= 0 || fill.Color.A == 0 || dot.Opacity <= 0
                || !dot.IsEffectivelyVisible || dot.FindResource(dot.ActualThemeVariant, resource) is not ISolidColorBrush expected
                || fill.Color != expected.Color)
                throw new InvalidOperationException("The radar legend dot must visibly paint the current theme's chart color: " + name);
        }
    }

    public static (Button Button, RadarTeachingTip Tip) HelpControls(Control view)
    {
        var button = Named<Button>(view, "HearingRadarHelp");
        var tip = Named<RadarTeachingTip>(view, "HearingRadarHelpTip");
        if (!ReferenceEquals(tip.Target, button) || tip.Title != "关于听感雷达图"
            || string.IsNullOrWhiteSpace(tip.Subtitle) || !tip.IsLightDismissEnabled
            || tip.PlacementConstraintAdjustment != PopupPositionerConstraintAdjustment.All || tip.Placement != PlacementMode.Top)
            throw new InvalidOperationException("Radar help must use a dismissible teaching tip anchored above its question button.");
        return (button, tip);
    }

    public static void DescriptionUsesAccent(Control view)
    {
        var description = Named<TextBlock>(view, "HearingResultDescription");
        var runs = description.Inlines?.OfType<Run>().ToArray() ?? [];
        string[] keywords = ["低频", "力量感", "底鼓更澎湃有力"];
        var emphasized = runs.Where(run => keywords.Contains(run.Text)).ToArray();
        if (emphasized.Length != 3 || !emphasized.Select(run => run.Text).SequenceEqual(keywords)
            || !string.Concat(runs.Select(run => run.Text)).Contains("经过耳道结构、听力特征、心理声学、耳机声学系统等多维建模分析"))
            throw new InvalidOperationException("The low-frequency result must retain the OEM description and its three emphasis phrases.");
        if (description.FindResource(description.ActualThemeVariant, "AccentTextFillColorPrimaryBrush") is not ISolidColorBrush accent
            || emphasized.Any(run => !run.IsSet(TextElement.ForegroundProperty)
                || run.Foreground is not ISolidColorBrush foreground || foreground.Color != accent.Color)
            || runs.Except(emphasized).Any(run => run.IsSet(TextElement.ForegroundProperty)))
            throw new InvalidOperationException("Only the three emphasis phrases should set the current theme's accent text foreground.");
    }

    public static InputElement PopupFocus(Window window, Button help) =>
        window.FocusManager?.GetFocusedElement() is InputElement focused && !ReferenceEquals(focused, help)
            ? focused : throw new InvalidOperationException("Opening radar help must focus its popup controls for keyboard interaction.");

    public static bool HelpIsDismissed(Control view, RadarTeachingTip tip) => !tip.IsOpen;

    public static void QuickDismissal(Control view, TestService service)
    {
        var inputs = service.Inputs.Count;
        var cancels = service.Cancels;
        var (help, tip) = HelpControls(view);
        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        if (!tip.IsOpen) throw new InvalidOperationException("Radar help must open on request.");
        var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        view.RaiseEvent(escape); Dispatcher.UIThread.RunJobs();
        if (!escape.Handled || !HelpIsDismissed(view, tip))
            throw new InvalidOperationException("Escape immediately after opening must dismiss radar help.");
        (help, tip) = HelpControls(view);
        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        if (!tip.IsOpen) throw new InvalidOperationException("Radar help must reopen after an immediate dismissal.");
        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        if (!HelpIsDismissed(view, tip))
            throw new InvalidOperationException("A second click immediately after opening must dismiss radar help.");
        if (service.Inputs.Count != inputs || service.Cancels != cancels)
            throw new InvalidOperationException("Radar help dismissal must not change the calibration operation.");
    }

    private static T Named<T>(Control view, string name) where T : Control =>
        view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
