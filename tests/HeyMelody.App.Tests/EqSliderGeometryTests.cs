using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

/// <summary>The ten vertical EQ sliders must keep their thumbs (including the hover-scaled ellipse) inside every clipping ancestor.</summary>
internal static class EqSliderGeometryTests
{
    private const double HoverScale = 1.167;

    internal static int Run(HeadlessUnitTestSession session)
    {
        session.Dispatch<bool>(async () => { await GeometryAsync(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS EQ vertical slider thumbs stay centred and unclipped in compact and wide layouts");
        return 1;
    }

    private static async Task GeometryAsync()
    {
        foreach (var width in new[] { 640d, 960d, 1280d })
        {
            await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
            await vm.InitializeAsync(); await vm.AddDemoDeviceAsync();
            var view = new MainView { DataContext = vm };
            var window = new Window { Content = view, Width = width, Height = 900, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show(); await MainUiTestLayout.ExpandSettingsAsync(view);
                var panel = view.FindControl<FASettingsExpander>("EqualizerPanel")!;
                var editor = view.FindControl<ItemsControl>("BandEditor")!;
                var sliders = editor.GetVisualDescendants().OfType<Slider>().ToArray();
                Check(sliders.Length == 10, "All ten EQ sliders are realized.");
                foreach (var slider in sliders)
                {
                    var thumb = slider.GetVisualDescendants().OfType<Track>().Single().Thumb!;
                    var box = Rect(thumb, slider);
                    var scaled = new Rect(box.Center.X - box.Width * HoverScale / 2, box.Center.Y - box.Height * HoverScale / 2,
                        box.Width * HoverScale, box.Height * HoverScale);
                    Console.WriteLine($"EQ-GEOMETRY window={width} editor={editor.Bounds.Width:0.#} slider={slider.Bounds.Width:0.#}"
                        + $" thumb={box.Left:0.#}..{box.Right:0.#} scaled={scaled.Left:0.#}..{scaled.Right:0.#}");
                    Check(Math.Abs(box.Center.X - slider.Bounds.Width / 2) <= 1, $"The thumb is centred in its slider ({box}, slider width {slider.Bounds.Width}).");
                    Check(Inside(scaled, new Rect(slider.Bounds.Size)), $"The hover-scaled thumb {scaled} stays inside the slider {slider.Bounds.Size}.");
                    for (Visual? up = slider; up is not null && !ReferenceEquals(up, panel); up = up.GetVisualParent())
                        if (up is Control control && control != slider)
                            Check(Inside(Rect(slider, control, scaled), new Rect(control.Bounds.Size)),
                                $"The scaled thumb stays inside {control.GetType().Name} {control.Bounds.Size}.");
                }
            }
            finally { window.Close(); }
        }
    }

    private static Rect Rect(Control child, Control ancestor) => Rect(child, ancestor, new Rect(child.Bounds.Size));
    private static Rect Rect(Control child, Control ancestor, Rect local)
    {
        var at = child.TranslatePoint(local.TopLeft, ancestor)!.Value;
        return new Rect(at, local.Size);
    }
    private static bool Inside(Rect inner, Rect outer) => inner.Left >= outer.Left - .01 && inner.Right <= outer.Right + .01
        && inner.Top >= outer.Top - .01 && inner.Bottom <= outer.Bottom + .01;
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
