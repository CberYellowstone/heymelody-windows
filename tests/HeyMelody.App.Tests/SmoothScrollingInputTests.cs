using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Ui;

internal static class SmoothScrollingInputTests
{
    public static async Task<IReadOnlyList<object>> RunAsync()
    {
        var checks = new List<object>();
        bool animationsBefore = FAUISettings.AreAnimationsEnabled();
        try
        {
            FAUISettings.SetAnimationsEnabledAtAppLevel(true);
            await PrecisionBoundariesAsync(checks);
            await PrecisionNestedAsync(checks);
            await PrecisionMotionAsync(checks);
            await PrecisionDirectionsAsync(checks);
            await PrecisionFlingBoundsAsync(checks);
            await PrecisionCancellationAsync(checks);
            await HeaderTapAsync(checks, move: 0);
            await HeaderTapAsync(checks, move: 1);
            foreach (string title in new[] { "降噪", "均衡器", "音效" }) await HeaderDragAsync(checks, title);
            await FooterButtonAsync(checks);
            await FooterSliderAsync(checks);
            await NestedHeaderAsync(checks);
            await HeaderReloadAsync(checks);
            return checks;
        }
        finally { FAUISettings.SetAnimationsEnabledAtAppLevel(animationsBefore); }
    }

    private static async Task PrecisionBoundariesAsync(List<object> checks)
    {
        foreach (bool bottom in new[] { false, true })
        {
            var content = ColorRows(100); var scroll = Viewer(content); SmoothScrolling.Attach(scroll);
            var window = WindowFor(scroll);
            try
            {
                window.Show(); await PumpAsync(window, 100);
                if (bottom) { scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height); await PumpAsync(window, 100); }
                int initial = ColoredSamples(window, scroll), minimum = initial; bool handled = true, logicalBounds = true;
                double maximum = scroll.Extent.Height - scroll.Viewport.Height;
                double peak = 0, limit = Math.Min(96, scroll.Viewport.Height * .12); ulong timestamp = 10_000;
                string? minimumSampleDetails = null;
                for (int packet = 0; packet < 16; packet++)
                {
                    var args = Wheel(window, content, new Vector(0, bottom ? -3.5 : 3.5), timestamp += 8);
                    handled &= args.Handled;
                    await PumpAsync(window, 8);
                    var rendered = Rendered(window, scroll);
                    if (rendered.Covered < minimum) { minimum = rendered.Covered; minimumSampleDetails = rendered.Missing; }
                    peak = Math.Max(peak, bottom ? rendered.BottomGap : rendered.TopGap);
                    logicalBounds &= scroll.Offset.Y >= -.01 && scroll.Offset.Y <= maximum + .01;
                }
                await ObserveAsync(window, 1400, () =>
                {
                    var rendered = Rendered(window, scroll);
                    if (rendered.Covered < minimum) { minimum = rendered.Covered; minimumSampleDetails = rendered.Missing; }
                    peak = Math.Max(peak, bottom ? rendered.BottomGap : rendered.TopGap);
                });
                Check(checks, initial == 16 && minimum >= 14 && logicalBounds && handled && peak > 1 && peak <= limit + 2,
                    "High precision bursts at the " + (bottom ? "bottom" : "top") + " retain a small bounded rebound and visible content.",
                    new { initialCoveredSamples = initial, minimumCoveredSamples = minimum, physicalPeakDip = peak, limit, logicalBounds, handled, minimumSampleDetails });
                var final = Rendered(window, scroll);
                Check(checks, final.Covered >= 15 && final.TopGap <= 1.5 && final.BottomGap <= 1.5,
                    "The precision boundary returns to a physically aligned viewport after its spring settles.");
            }
            finally { window.Close(); await PumpAsync(window, 80); }
        }
    }

    private static async Task PrecisionMotionAsync(List<object> checks)
    {
        foreach (bool osTail in new[] { false, true })
        {
            var content = ColorRows(100); var scroll = Viewer(content); SmoothScrolling.Attach(scroll); var window = WindowFor(scroll);
            try
            {
                window.Show(); await PumpAsync(window, 100); scroll.Offset = new Vector(0, 400); await PumpAsync(window, 80);
                double[] input = Enumerable.Repeat(10d, 10).Concat(osTail ? new[] { 8d, 6, 4, 3, 2, 1, .5, .25 } : []).ToArray();
                await FeedAsync(window, content, input, interval: osTail ? 8 : 32);
                double delivered = 400 + input.Sum(); var after = new List<double>();
                await ObserveAsync(window, 1500, () => after.Add(scroll.Offset.Y));
                if (osTail)
                    Check(checks, after.All(value => value >= delivered - 1 && value <= delivered + 1),
                        "An explicit 8 ms OS-decay tail settles at its supplied distance without a second or reverse fling.",
                        new { delivered, minimum = after.Min(), maximum = after.Max(), final = scroll.Offset.Y });
                else
                    Check(checks, scroll.Offset.Y > delivered + 5 && after.Select(value => Math.Round(value, 2)).Distinct().Count() >= 3,
                        "An abruptly stopped precision burst continues through several inertia frames instead of stopping immediately.",
                        new { delivered, final = scroll.Offset.Y });
            }
            finally { window.Close(); await PumpAsync(window, 80); }
        }
    }

    private static async Task PrecisionDirectionsAsync(List<object> checks)
    {
        var content = ColorRows(100); var scroll = Viewer(content); SmoothScrolling.Attach(scroll); var window = WindowFor(scroll);
        try
        {
            window.Show(); await PumpAsync(window, 100); scroll.Offset = new Vector(0, 400); await PumpAsync(window, 80);
            ulong timestamp = await FeedAsync(window, content, Enumerable.Repeat(10d, 6), interval: 8);
            double beforeReverse = scroll.Offset.Y;
            Wheel(window, content, new Vector(0, 10d / 48), timestamp + 8); await PumpAsync(window, 24);
            double reversed = scroll.Offset.Y; var later = new List<double>();
            await ObserveAsync(window, 1200, () => later.Add(scroll.Offset.Y));
            Check(checks, reversed < beforeReverse - 1 && later.All(value => value <= reversed + 1),
                "A short final reverse packet moves back promptly and never releases inertia in the obsolete direction.");
            scroll.Offset = new Vector(0, 400); await PumpAsync(window, 80);
            timestamp = await FeedAsync(window, content, new[] { 10d, 10 }, interval: 32);
            double beforeInteger = scroll.Offset.Y;
            Wheel(window, content, new Vector(0, -1), timestamp + 32); await PumpAsync(window, 24);
            double afterInteger = scroll.Offset.Y;
            Wheel(window, content, new Vector(0, -10d / 48), timestamp + 64); await PumpAsync(window, 24);
            Check(checks, afterInteger > beforeInteger + 1 && scroll.Offset.Y > afterInteger + 1,
                "An integer packet inside a precision session and its next fractional packet both continue the user's motion.");
        }
        finally { window.Close(); await PumpAsync(window, 80); }
    }

    private static async Task PrecisionFlingBoundsAsync(List<object> checks)
    {
        foreach (bool fast in new[] { false, true })
        {
            var content = ColorRows(100); var scroll = Viewer(content); SmoothScrolling.Attach(scroll); var window = WindowFor(scroll);
            try
            {
                window.Show(); await PumpAsync(window, 100); double maximum = scroll.Extent.Height - scroll.Viewport.Height;
                scroll.Offset = new Vector(0, maximum - (fast ? 400 : 120)); await PumpAsync(window, 80);
                await FeedAsync(window, content, Enumerable.Repeat(fast ? 80d : 25d, 4), interval: fast ? 16 : 32);
                if (scroll.Offset.Y >= maximum - 2) throw new InvalidOperationException("Fling-boundary input must end before the boundary.");
                double peak = 0, limit = Math.Min(96, scroll.Viewport.Height * .12); int minimum = 16;
                await ObserveAsync(window, 1600, () =>
                {
                    var rendered = Rendered(window, scroll); peak = Math.Max(peak, rendered.BottomGap);
                    minimum = Math.Min(minimum, rendered.Covered);
                });
                Check(checks, peak > 1 && peak <= limit + 2 && minimum >= 14,
                    (fast ? "A high-speed" : "A near-boundary") + " nonzero-velocity fling hits the edge without exceeding the physical rebound bound.",
                    new { peak, limit, minimumCoveredSamples = minimum });
                var final = Rendered(window, scroll);
                Check(checks, Math.Abs(scroll.Offset.Y - maximum) <= 1 && final.BottomGap <= 1.5,
                    "The collision spring returns to the boundary without leaving the content displaced.");
            }
            finally { window.Close(); await PumpAsync(window, 80); }
        }
    }

    private static async Task PrecisionCancellationAsync(List<object> checks)
    {
        var content = ColorRows(100); var scroll = Viewer(content); SmoothScrolling.Attach(scroll); var window = WindowFor(scroll);
        try
        {
            window.Show(); await PumpAsync(window, 100); scroll.Offset = new Vector(0, 400); await PumpAsync(window, 80);
            ulong timestamp = await FeedAsync(window, content, Enumerable.Repeat(10d, 5), interval: 32);
            scroll.Offset = new Vector(0, 900); var after = new List<double>();
            await ObserveAsync(window, 300, () => after.Add(scroll.Offset.Y));
            Check(checks, after.All(value => Math.Abs(value - 900) <= 1),
                "Programmatic positioning cancels a pending precision release without an obsolete pullback.");
            Wheel(window, content, new Vector(0, -10d / 48), timestamp + 32); await PumpAsync(window, 800);
            Check(checks, Math.Abs(scroll.Offset.Y - 910) <= 1,
                "A fresh precision packet starts a usable new session after programmatic cancellation.");
            scroll.Offset = new Vector(0, 400); await PumpAsync(window, 80);
            await FeedAsync(window, content, Enumerable.Repeat(10d, 5), interval: 32);
            await PumpAsync(window, 24);
            double takeover = scroll.Offset.Y; var point = new Point(100, 180);
            using (var touch = window.TouchBegin(point))
            { await PumpAsync(window, 40); window.TouchEnd(touch, point); }
            after.Clear(); await ObserveAsync(window, 800, () => after.Add(scroll.Offset.Y));
            Check(checks, after.All(value => Math.Abs(value - takeover) <= 1),
                "A new touch contact takes over without a delayed precision-wheel fling.",
                new { takeover, minimum = after.Min(), maximum = after.Max() });
        }
        finally { window.Close(); await PumpAsync(window, 80); }
    }

    private static async Task PrecisionNestedAsync(List<object> checks)
    {
        var innerContent = ColorRows(40); var inner = Viewer(innerContent); inner.Height = 120;
        var outerContent = new StackPanel(); outerContent.Children.Add(inner); outerContent.Children.Add(ColorRows(40));
        var outer = Viewer(outerContent); SmoothScrolling.Attach(outer); var window = WindowFor(outer);
        try
        {
            window.Show(); await PumpAsync(window, 100);
            Wheel(window, innerContent, new Vector(0, -.5)); await PumpAsync(window, 80);
            Check(checks, inner.Offset.Y > 0 && Math.Abs(outer.Offset.Y) < .01,
                "A precision packet consumed by an unmarked nested viewer does not move its smooth ancestor.");
            inner.Offset = new Vector(0, inner.Extent.Height - inner.Viewport.Height); await PumpAsync(window, 80);
            Wheel(window, innerContent, new Vector(0, -.5)); await PumpAsync(window, 80);
            Check(checks, outer.Offset.Y > 0,
                "Precision input chains from a nested boundary to an ancestor that can still scroll.");
            outer.Offset = default; inner.IsScrollChainingEnabled = false; await PumpAsync(window, 80);
            Wheel(window, innerContent, new Vector(0, -.5)); await PumpAsync(window, 100);
            Check(checks, Math.Abs(outer.Offset.Y) < .01,
                "The precision adapter preserves an explicitly disabled nested chaining boundary.");
        }
        finally { window.Close(); await PumpAsync(window, 80); }
    }

    private static async Task HeaderTapAsync(List<object> checks, double move)
    {
        var scene = await HeaderScene.CreateAsync("降噪");
        try
        {
            await TouchAsync(scene.Window, scene.Header, new Vector(0, -move));
            Check(checks, scene.Expander.IsExpanded && scene.Toggles == 1 && Math.Abs(scene.ActiveScroll.Offset.Y) <= 2,
                move == 0 ? "A short header touch still toggles its expander exactly once."
                    : "Below-threshold touch movement remains a single header tap rather than a scroll or double toggle.",
                new { move, scene.Toggles, offset = scene.ActiveScroll.Offset.Y });
        }
        finally { await scene.CloseAsync(); }
    }

    private static async Task HeaderDragAsync(List<object> checks, string title)
    {
        var scene = await HeaderScene.CreateAsync(title);
        try
        {
            // 24 DIP ends inside the header: the old behavior incorrectly treated this drag as a click.
            await TouchAsync(scene.Window, scene.Header, new Vector(0, -24));
            Check(checks, scene.ActiveScroll.Offset.Y > 5 && !scene.Expander.IsExpanded && scene.Toggles == 0,
                "A touch drag that starts on the " + title + " header scrolls without accidentally toggling it.",
                new { offset = scene.ActiveScroll.Offset.Y, scene.Toggles });
        }
        finally { await scene.CloseAsync(); }
    }

    private static async Task FooterButtonAsync(List<object> checks)
    {
        int clicks = 0; var button = new Button { Content = "Footer action" }; button.Click += (_, _) => clicks++;
        var scene = await HeaderScene.CreateAsync("均衡器", expanded: true, footer: button);
        try
        {
            await TouchAsync(scene.Window, button, default);
            Check(checks, clicks == 1 && scene.Expander.IsExpanded && scene.Toggles == 0 && Math.Abs(scene.ActiveScroll.Offset.Y) < .5,
                "The header's footer button retains its own touch click without header interception.");
        }
        finally { await scene.CloseAsync(); }
    }

    private static async Task FooterSliderAsync(List<object> checks)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 180 };
        var scene = await HeaderScene.CreateAsync("音效", expanded: true, footer: slider);
        try
        {
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            await TouchAsync(scene.Window, thumb, new Vector(50, 20));
            Check(checks, slider.Value > 55 && scene.Expander.IsExpanded && scene.Toggles == 0 && Math.Abs(scene.ActiveScroll.Offset.Y) < .5,
                "The header's footer slider retains a diagonal touch drag without vertical-page capture.", new { slider.Value });
        }
        finally { await scene.CloseAsync(); }
    }

    private static async Task NestedHeaderAsync(List<object> checks)
    {
        var scene = await HeaderScene.CreateAsync("降噪", nested: true);
        try
        {
            await TouchAsync(scene.Window, scene.Header, new Vector(0, -70));
            Check(checks, scene.ActiveScroll.Offset.Y > 5 && Math.Abs(scene.OuterScroll.Offset.Y) < .5
                && !scene.Expander.IsExpanded && scene.Toggles == 0,
                "A header owned by an unmarked nested viewer scrolls that viewer without being stolen by the smooth ancestor.",
                new { inner = scene.ActiveScroll.Offset.Y, outer = scene.OuterScroll.Offset.Y });
        }
        finally { await scene.CloseAsync(); }
    }

    private static async Task HeaderReloadAsync(List<object> checks)
    {
        var scene = await HeaderScene.CreateAsync("音效");
        try
        {
            var start = Center(scene.Header, scene.Window);
            using (var touch = scene.Window.TouchBegin(start))
            {
                scene.Window.TouchMove(touch, start - new Vector(0, 30)); await PumpAsync(scene.Window, 30);
                scene.Holder.Child = null; await PumpAsync(scene.Window, 60);
                scene.Window.TouchEnd(touch, start - new Vector(0, 30));
            }
            scene.Holder.Child = scene.OuterScroll; await PumpAsync(scene.Window, 120);
            scene.ActiveScroll.Offset = default; await PumpAsync(scene.Window, 100);
            await TouchAsync(scene.Window, scene.Header, new Vector(0, -50));
            Check(checks, scene.ActiveScroll.Offset.Y > 5 && !scene.Expander.IsExpanded && scene.Toggles == 0,
                "Detaching during a header gesture and reloading leaves a usable touch scroller without a late toggle.");
        }
        finally { await scene.CloseAsync(); }
    }

    private sealed class HeaderScene
    {
        internal Window Window = null!;
        internal Border Holder = null!;
        internal ScrollViewer ActiveScroll = null!, OuterScroll = null!;
        internal FASettingsExpander Expander = null!;
        internal ToggleButton Header = null!;
        internal int Toggles;
        internal static async Task<HeaderScene> CreateAsync(string title, bool expanded = false, Control? footer = null, bool nested = false)
        {
            // Footer belongs to the header template, whose FooterPresenter sits inside its ToggleButton.
            // A footer on an expanded child item would not exercise that interception boundary.
            var expander = new FASettingsExpander { Header = title, IsExpanded = expanded, Footer = footer };
            expander.Items.Add(new FASettingsExpanderItem { Content = "Settings item" });
            var page = new StackPanel(); page.Children.Add(new Border { Height = 100 }); page.Children.Add(expander); page.Children.Add(ColorRows(50));
            var active = Viewer(page); ScrollViewer outer;
            if (nested)
            {
                active.Height = 240;
                var outerPage = new StackPanel(); outerPage.Children.Add(active); outerPage.Children.Add(ColorRows(30));
                outer = Viewer(outerPage); SmoothScrolling.Attach(outer);
            }
            else { outer = active; SmoothScrolling.Attach(outer); }
            var holder = new Border { Child = outer }; var window = WindowFor(holder);
            var result = new HeaderScene { Window = window, Holder = holder, ActiveScroll = active, OuterScroll = outer, Expander = expander };
            expander.PropertyChanged += (_, args) => { if (args.Property.Name == "IsExpanded") result.Toggles++; };
            window.Show(); await PumpAsync(window, 220);
            result.Header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(item => item.Name == "ToggleButton"
                && item.TemplatedParent is Expander inner && ReferenceEquals(inner.TemplatedParent, expander));
            return result;
        }
        internal async Task CloseAsync() { if (Window.IsVisible) Window.Close(); await PumpAsync(Window, 80); }
    }

    private static async Task TouchAsync(Window window, Control target, Vector movement)
    {
        var start = Center(target, window);
        using (var touch = window.TouchBegin(start))
        {
            await PumpAsync(window, 30);
            int steps = movement.Length == 0 ? 0 : 10;
            for (int step = 1; step <= steps; step++)
            { window.TouchMove(touch, start + movement * (step / (double)steps)); await PumpAsync(window, 12); }
            window.TouchEnd(touch, start + movement);
        }
        await PumpAsync(window, 450);
    }
    private static Point Center(Control target, Window window) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
    private static ScrollViewer Viewer(Control content) => new()
    {
        Content = content, Background = Brushes.White, AllowAutoHide = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private static Window WindowFor(Control content) => new()
    { Content = content, Width = 420, Height = 360, Background = Brushes.White, WindowDecorations = WindowDecorations.None };
    private static StackPanel ColorRows(int count)
    {
        var panel = new StackPanel();
        for (int index = 0; index < count; index++) panel.Children.Add(new Border { Height = 32,
            Background = new SolidColorBrush(Color.Parse(index % 2 == 0 ? "#2176FF" : "#12B34A")) });
        return panel;
    }
    private static PointerWheelEventArgs Wheel(Window window, Control target, Vector delta, ulong? timestamp = null)
    {
        var args = new PointerWheelEventArgs(target, new Pointer(1, PointerType.Mouse, true), window, new Point(80, 60),
            timestamp ?? (ulong)Environment.TickCount64, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, delta);
        target.RaiseEvent(args); Dispatcher.UIThread.RunJobs(); return args;
    }
    private static async Task<ulong> FeedAsync(Window window, Control target, IEnumerable<double> deltas, int interval)
    {
        ulong timestamp = 20_000;
        foreach (double delta in deltas)
        {
            Wheel(window, target, new Vector(0, -delta / 48), timestamp += (ulong)interval);
            // Timestamps define the simulated device cadence; delivery stays within the quiet window.
            await PumpAsync(window, 8);
        }
        return timestamp;
    }
    private static async Task ObserveAsync(Window window, int duration, Action sample)
    {
        var clock = Stopwatch.StartNew();
        do { await PumpAsync(window, 12); sample(); } while (clock.ElapsedMilliseconds < duration);
    }
    private readonly record struct RenderedContent(int Covered, double TopGap, double BottomGap, string? Missing = null);
    private static int ColoredSamples(Window window, ScrollViewer scroll) => Rendered(window, scroll).Covered;
    private static RenderedContent Rendered(Window window, ScrollViewer scroll)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing rendered input-test frame.");
        using var pixels = frame.Lock();
        if (pixels.Format != PixelFormat.Bgra8888 && pixels.Format != PixelFormat.Rgba8888)
            throw new InvalidOperationException("Unexpected input-test framebuffer format: " + pixels.Format);
        var bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        var origin = scroll.TranslatePoint(default, window)!.Value; int covered = 0;
        int x = (int)((origin.X + scroll.Bounds.Width / 2) * window.RenderScaling);
        int firstY = (int)(origin.Y * window.RenderScaling), endY = (int)((origin.Y + scroll.Bounds.Height) * window.RenderScaling);
        bool IsBand(int y)
        {
            int at = y * pixels.RowBytes + x * 4;
            byte red = bytes[at + (pixels.Format == PixelFormat.Bgra8888 ? 2 : 0)], green = bytes[at + 1],
                blue = bytes[at + (pixels.Format == PixelFormat.Bgra8888 ? 0 : 2)];
            // The fixture contains saturated rows over a white viewport. Fractional
            // composition produces other chromatic pixels between the row endpoints;
            // classify visible color, keeping white exposed by overscroll a strict gap.
            return Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) >= 30;
        }
        var missing = new List<string>();
        for (int index = 0; index < 16; index++)
        {
            int y = (int)((origin.Y + (index + .5) * scroll.Bounds.Height / 16) * window.RenderScaling);
            if (IsBand(y)) covered++;
            else
            {
                int at = y * pixels.RowBytes + x * 4;
                missing.Add($"y={y}:bytes={bytes[at]},{bytes[at + 1]},{bytes[at + 2]},{bytes[at + 3]}");
            }
        }
        int firstBand = -1, lastBand = -1;
        for (int y = firstY; y < endY; y++) if (IsBand(y)) { if (firstBand < 0) firstBand = y; lastBand = y; }
        return firstBand < 0 ? new(covered, scroll.Bounds.Height, scroll.Bounds.Height, string.Join(";", missing))
            : new(covered, (firstBand - firstY) / window.RenderScaling, (endY - 1 - lastBand) / window.RenderScaling, string.Join(";", missing));
    }
    private static async Task PumpAsync(Window window, int duration)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            if (window.IsVisible) { using var pixels = window.CaptureRenderedFrame(); }
            await Task.Delay(8);
        } while (timer.ElapsedMilliseconds < duration);
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(List<object> checks, bool passed, string message, object? details = null)
    {
        if (!passed) throw new InvalidOperationException(message + (details is null ? "" : " " + System.Text.Json.JsonSerializer.Serialize(details)));
        checks.Add(new { message, details }); Console.WriteLine("PASS " + message);
    }
}
