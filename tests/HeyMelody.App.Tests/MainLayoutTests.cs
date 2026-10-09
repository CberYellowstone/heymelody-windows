using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class MainLayoutTests
{
    internal static int Run(HeadlessUnitTestSession session, string output, List<object> receipts)
    {
        Run("real-device layout keeps ANC visible and other panels collapsed", () => LayoutAsync(false));
        Run("demo layout uses the same collapsed defaults and eight-pixel spacing", () => LayoutAsync(true));
        Run("device information copies current fields and handles pending, failed and stale requests", CopyAsync);
        return 3;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }

        async Task LayoutAsync(bool demo)
        {
            var oldTheme = Application.Current!.RequestedThemeVariant;
            try
            {
                foreach (var dark in new[] { false, true })
                foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
                {
                    Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
                    await vm.InitializeAsync();
                    if (demo) await vm.AddDemoDeviceAsync();
                    var view = new MainView { DataContext = vm };
                    var window = new Window { Content = view, Width = size.Width, Height = size.Height,
                        WindowDecorations = WindowDecorations.None };
                    try
                    {
                        window.Show(); Dispatcher.UIThread.RunJobs();
                        var hero = view.FindControl<Grid>("HeroPanel")!;
                        Check(hero.Children.Count == 1 && hero.Children[0] == view.FindControl<ProductArtwork>("HeroArt")
                            && view.FindControl<TextBlock>("HeroCaption") is null,
                            "Product artwork occupies its own panel without repeated title or caption rows.");
                        var anc = view.FindControl<Border>("AncPanel")!;
                        var selector = anc.GetVisualDescendants().OfType<AncModeSelector>().Single();
                        Check(anc.CornerRadius == new CornerRadius(6) && anc.IsVisible && selector.IsVisible
                            && selector.Bounds.Width > 0 && selector.Bounds.Height > 0
                            && anc.GetVisualDescendants().OfType<FASymbolIcon>().Any(icon => icon.Symbol == FASymbol.Audio)
                            && anc.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "调整你听见的环境声音"),
                            "ANC is a rounded card with its header, description and functional selector always present.");
                        Check(view.GetVisualDescendants().OfType<FASettingsExpander>().Any()
                            && view.GetVisualDescendants().OfType<FASettingsExpander>().All(expander => !expander.IsExpanded)
                            && !view.FindControl<Expander>("OperationLogPanel")!.IsExpanded,
                            "All optional main-interface groups start collapsed, including demo groups.");
                        var fit = view.FindControl<FASettingsExpanderItem>("FitTestPanel")!;
                        var hearing = view.FindControl<FASettingsExpander>("HearingPanel")!;
                        var information = view.FindControl<FASettingsExpander>("DeviceInfoPanel")!;
                        var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
                        var fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        var informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        var hearingTop = hearing.TranslatePoint(default, scroll)!.Value.Y;
                        Check(Math.Abs(informationTop - fitTop - fit.Bounds.Height - 8) < .6
                            && Math.Abs(fitTop - hearingTop - hearing.Bounds.Height - 8) < .6,
                            "Fit-test and device-information cards use the same actual eight-pixel gap as adjacent groups.");
                        var copy = view.FindControl<Button>("CopyDeviceInfoButton")!;
                        var copyContent = (StackPanel)copy.Content!;
                        Check(copy.IsEnabled && copy.Bounds.Width > 0 && copy.Bounds.Height > 0 && !information.IsExpanded
                            && copyContent.Children[0] is FASymbolIcon { Symbol: FASymbol.Copy }
                            && copyContent.Children[1] is TextBlock { Text: "复制" },
                            "The collapsed information header offers the Copy icon followed by its text.");
                        var picker = view.FindControl<ListBox>("DevicePicker")!;
                        var cards = picker.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "DeviceCardContent").ToArray();
                        Check(cards.Length > 0 && cards.All(card => card.ColumnDefinitions.Count == 1)
                            && !picker.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "DeviceIcon"),
                            "Device cards retain their text while removing the redundant icon slot.");
                        Check(view.IsCompact == (size.Width < 1080), "The simplified hero respects the responsive breakpoint.");

                        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing main-layout pixels.");
                        var name = $"main-collapsed-{(demo ? "demo" : "real")}-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}.png";
                        var path = Path.Combine(output, name);
                        using (var stream = new FileStream(path, FileMode.CreateNew)) frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        receipts.Add(new { scenario = "main-collapsed", demo, theme = dark ? "dark" : "light",
                            width = size.Width, height = size.Height, file = name,
                            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });

                        // Inspect the controls below the initial viewport without opening
                        // any panel. Center the adjacent fit-test and information cards
                        // together so their spacing and the collapsed Copy action are visible.
                        information.BringIntoView(); Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        var groupHeight = informationTop + information.Bounds.Height - fitTop;
                        var desiredOffset = scroll.Offset.Y + fitTop - Math.Max(0, (scroll.Viewport.Height - groupHeight) / 2);
                        scroll.Offset = new Vector(scroll.Offset.X,
                            Math.Clamp(desiredOffset, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        await Task.Delay(20);
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        var copyTop = copy.TranslatePoint(default, scroll)!.Value.Y;
                        Check(fitTop >= -.6 && informationTop + information.Bounds.Height <= scroll.Viewport.Height + .6
                            && copyTop >= -.6 && copyTop + copy.Bounds.Height <= scroll.Viewport.Height + .6
                            && !information.IsExpanded
                            && view.GetVisualDescendants().OfType<FASettingsExpander>().All(expander => !expander.IsExpanded),
                            "The scrolled information screenshot shows the complete fit-test card, collapsed information card and Copy button.");
                        using var informationFrame = window.CaptureRenderedFrame()
                            ?? throw new InvalidOperationException("Missing device-information layout pixels.");
                        var informationName = $"main-information-{(demo ? "demo" : "real")}-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}.png";
                        var informationPath = Path.Combine(output, informationName);
                        using (var stream = new FileStream(informationPath, FileMode.CreateNew))
                            informationFrame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        receipts.Add(new { scenario = "main-information", demo, theme = dark ? "dark" : "light",
                            width = size.Width, height = size.Height, file = informationName,
                            scrollY = scroll.Offset.Y, viewportHeight = scroll.Viewport.Height,
                            fitTop, informationTop, informationHeight = information.Bounds.Height,
                            copyTop, copyHeight = copy.Bounds.Height,
                            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(informationPath))).ToLowerInvariant() });
                    }
                    finally { window.Close(); }
                }
            }
            finally { Application.Current.RequestedThemeVariant = oldTheme; }
        }
    }

    private static async Task CopyAsync()
    {
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView();
        var window = new Window { Content = view, Width = 1280, Height = 900, WindowDecorations = WindowDecorations.None };
        var copied = new List<string>();
        view.WriteClipboardOverride = text => { copied.Add(text); return Task.CompletedTask; };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("CopyDeviceInfoButton")!;
            Check(!button.IsEnabled, "Copy remains disabled when no device is selected.");
            await view.CopyDeviceInfoAsync();
            Check(copied.Count == 0, "An unavailable copy action does not access the clipboard.");
            view.DataContext = vm; Dispatcher.UIThread.RunJobs();
            button.BringIntoView(); Dispatcher.UIThread.RunJobs();
            Click(window, button);
            Check(copied.SequenceEqual([Expected(vm)]) && view.FindControl<TextBlock>("DeviceInfoCopyStatus")!.Text == "已复制"
                && !view.FindControl<FASettingsExpander>("DeviceInfoPanel")!.IsExpanded,
                "Clicking the header button copies every displayed information field without expanding the panel.");

            await vm.AddDemoDeviceAsync(); Dispatcher.UIThread.RunJobs();
            await view.CopyDeviceInfoAsync();
            Check(copied.Count == 2 && copied[1] == Expected(vm) && copied[1] != copied[0],
                "A subsequent copy reads the currently selected device rather than cached metadata.");
            view.WriteClipboardOverride = _ => Task.FromException(new IOException("clipboard locked"));
            await view.CopyDeviceInfoAsync();
            Check(button.IsEnabled && view.FindControl<TextBlock>("DeviceInfoCopyStatus")!.Text == "复制失败，请重试"
                && !window.GetVisualDescendants().OfType<FAContentDialog>().Any(),
                "Clipboard failure keeps retry available and reports locally without opening a modal dialog.");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WriteClipboardOverride = text => { copied.Add(text); return completion.Task; };
            var pending = view.CopyDeviceInfoAsync();
            Check(!button.IsEnabled && !pending.IsCompleted, "A pending clipboard write disables duplicate requests.");
            await view.CopyDeviceInfoAsync();
            Check(copied.Count == 3, "Another request cannot race the pending clipboard write.");
            view.DataContext = null; Dispatcher.UIThread.RunJobs();
            completion.SetResult(); await pending;
            Check(!button.IsEnabled && string.IsNullOrEmpty(view.FindControl<TextBlock>("DeviceInfoCopyStatus")!.Text),
                "A late clipboard result cannot report success against a different device context.");
            view.DataContext = vm; Dispatcher.UIThread.RunJobs();
            await view.DismissSettingsDialogAsync();
            Check(!button.IsEnabled, "Closing the view disables device-information copying.");
        }
        finally { window.Close(); }
    }

    private static string Expected(MainViewModel vm) => $"设备名称：{vm.Title}{Environment.NewLine}产品编号：{vm.ProductId ?? "—"}{Environment.NewLine}固件版本：{vm.Firmware ?? "—"}";
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
