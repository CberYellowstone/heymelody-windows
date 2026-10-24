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
                for (int packet = 0; packet < 16; packet++)
                {
                    var args = Wheel(window, content, new Vector(0, bottom ? -3.5 : 3.5));
                    handled &= args.Handled;
                    await PumpAsync(window, 8);
                    minimum = Math.Min(minimum, ColoredSamples(window, scroll));
                    logicalBounds &= scroll.Offset.Y >= -.01 && scroll.Offset.Y <= maximum + .01;
                }
                Check(checks, initial == 16 && minimum >= 15 && logicalBounds && handled,
                    "High precision wheel bursts at the " + (bottom ? "bottom" : "top") + " keep actual rendered content visible without elastic flight.",
                    new { initialCoveredSamples = initial, minimumCoveredSamples = minimum, totalSamples = 16, maximum });
                await PumpAsync(window, 300);
                Check(checks, ColoredSamples(window, scroll) >= 15,
                    "The precision boundary remains physically covered after pending composition work completes.");
            }
            finally { window.Close(); await PumpAsync(window, 80); }
        }
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
    private static PointerWheelEventArgs Wheel(Window window, Control target, Vector delta)
    {
        var args = new PointerWheelEventArgs(target, new Pointer(1, PointerType.Mouse, true), window, new Point(80, 60),
            (ulong)Environment.TickCount64, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, delta);
        target.RaiseEvent(args); Dispatcher.UIThread.RunJobs(); return args;
    }
    private static int ColoredSamples(Window window, ScrollViewer scroll)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing rendered input-test frame.");
        using var pixels = frame.Lock();
        if (pixels.Format != PixelFormat.Bgra8888 && pixels.Format != PixelFormat.Rgba8888)
            throw new InvalidOperationException("Unexpected input-test framebuffer format: " + pixels.Format);
        var bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        var origin = scroll.TranslatePoint(default, window)!.Value; int covered = 0;
        for (int index = 0; index < 16; index++)
        {
            int x = (int)((origin.X + scroll.Bounds.Width / 2) * window.RenderScaling);
            int y = (int)((origin.Y + (index + .5) * scroll.Bounds.Height / 16) * window.RenderScaling);
            int at = y * pixels.RowBytes + x * 4;
            byte green = bytes[at + 1], low = Math.Min(bytes[at], bytes[at + 2]), high = Math.Max(bytes[at], bytes[at + 2]);
            bool blueBand = green is >= 110 and <= 125 && low is >= 25 and <= 40 && high >= 245;
            bool greenBand = green is >= 170 and <= 190 && low < 30 && high is >= 65 and <= 85;
            if (blueBand || greenBand) covered++;
        }
        return covered;
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
        if (!passed) throw new InvalidOperationException(message);
        checks.Add(new { message, details }); Console.WriteLine("PASS " + message);
    }
}
