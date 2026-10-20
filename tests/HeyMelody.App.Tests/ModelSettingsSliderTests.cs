using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using SkiaSharp;

internal static class ModelSettingsSliderTests
{
    internal static async Task RunAsync(string output)
    {
        var path = ThemePreferences.SettingsPathOverride!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string legacy = "{\"theme\":\"system\",\"modelFrameRate\":\"display\",\"otherPreference\":42}";
        File.WriteAllText(path, legacy);
        ModelRenderingPreferences.Reload();
        Check(ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited
            && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Unlimited,
            "The legacy display setting loads as the unlimited default.");
        var standalone = new AppSettingsPanel();
        Check(Slider(standalone).Value == 2 && standalone.ModelFrameRate == ModelFrameRate.Unlimited,
            "The public settings-panel constructor also defaults to the third, unlimited stop.");
        Check(Equals(ToolTip.GetTip(Slider(standalone)), "无限制")
            && AutomationProperties.GetHelpText(Slider(standalone)) == "无限制",
            "The default tooltip and accessible current choice say unlimited rather than zero fps.");

        var previousTheme = Application.Current!.RequestedThemeVariant;
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        var receipts = new List<object>();
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            foreach (var dark in new[] { false, true })
            foreach (var scaling in new[] { 1d, 1.5d, 2d })
            {
                Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.SetRenderScaling(scaling);
                var operation = view.OpenSettingsDialogAsync();
                var dialog = await SettingsAsync(window);
                var panel = (AppSettingsPanel)dialog.Content!;
                var slider = Slider(panel);
                var rateCard = panel.FindControl<FASettingsExpanderItem>("ModelFrameRateCard")!;
                var apiCard = panel.FindControl<FASettingsExpanderItem>("ModelCompatibilityCard")!;
                rateCard.BringIntoView();
                await SettleAsync();
                Check(Equals(rateCard.Content, "3D模型渲染帧率") && rateCard.Description is null or ""
                    && AutomationProperties.GetName(slider) == "3D模型渲染帧率",
                    "The frame-rate row has the exact title and accessibility name, with no description.");
                Check(Equals(apiCard.Content, "3D模型渲染API")
                    && Equals(apiCard.Description, "若存在渲染问题，请尝试切换到 OpenGL"),
                    "The rendering API row preserves its exact title and guidance.");
                Check(slider.Minimum == 0 && slider.Maximum == 2 && slider.Value == 2
                    && slider.SmallChange == 1 && slider.LargeChange == 1
                    && slider.IsSnapToTickEnabled && slider.TickFrequency == 1
                    && slider.TickPlacement == TickPlacement.BottomRight
                    && slider.Ticks is { } ticks && ticks.SequenceEqual(new[] { 0d, 1d, 2d }),
                    "The standard slider exposes exactly three marked, snapping stops with one-stop keyboard changes.");
                Check(!panel.GetVisualDescendants().OfType<ComboBox>().Any(control => control.Name == "ModelFrameRateChoice"),
                    "The obsolete frame-rate dropdown is absent.");
                CheckLayout(panel, slider, rateCard, apiCard);
                var name = $"settings-slider-{(dark ? "dark" : "light")}-{scaling * 100:0}";
                CaptureRows(window, apiCard, rateCard, output, name);
                receipts.Add(new { theme = dark ? "dark" : "light", scaling, rowWidth = rateCard.Bounds.Width,
                    sliderWidth = slider.Bounds.Width, labels = Labels(panel).Select(label => label.Text).ToArray() });

                foreach (var index in new[] { 0, 1, 2 })
                {
                    DragMouse(window, slider, index);
                    Check(slider.Value == index && panel.ModelFrameRate == Rate(index),
                        "Mouse dragging snaps to the selected frame-rate choice.");
                    var label = index switch { 0 => "30帧", 1 => "60帧", _ => "无限制" };
                    Check(Equals(ToolTip.GetTip(slider), label) && AutomationProperties.GetHelpText(slider) == label,
                        "Tooltip and accessible help identify the selected frame-rate choice.");
                }
                slider.Focus();
                Key(window, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);
                Check(slider.Value == 1, "The left arrow moves from unlimited to 60 fps.");
                Key(window, Avalonia.Input.Key.PageDown, PhysicalKey.PageDown);
                Check(slider.Value == 0, "Page Down changes by one stop to 30 fps.");
                Key(window, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
                Check(slider.Value == 1, "The right arrow moves by one stop to 60 fps.");
                Key(window, Avalonia.Input.Key.PageUp, PhysicalKey.PageUp);
                Check(slider.Value == 2, "Page Up changes by one stop to unlimited.");
                var start = PointAt(window, slider, 2);
                var end = PointAt(window, slider, 0);
                using (var touch = window.TouchBegin(start))
                {
                    window.TouchMove(touch, end);
                    window.TouchEnd(touch, end);
                }
                Dispatcher.UIThread.RunJobs();
                Check(slider.Value == 0 && panel.ModelFrameRate == ModelFrameRate.Fps30,
                    "Touch dragging snaps to 30 fps using the standard slider.");
                Check(ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited && File.ReadAllText(path) == legacy,
                    "Mouse, keyboard and touch only update the settings draft.");
                dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
                Check(ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited && File.ReadAllText(path) == legacy,
                    "Cancelling preserves the running frame rate and the unchanged legacy settings file.");
            }

            window.SetRenderScaling(1);
            foreach (var index in new[] { 2, 0, 1, 2 })
            {
                var previous = ModelRenderingPreferences.FrameRate;
                var operation = view.OpenSettingsDialogAsync();
                var dialog = await SettingsAsync(window);
                var panel = (AppSettingsPanel)dialog.Content!;
                Check(panel.ModelFrameRate == previous, "Reopening settings displays the active saved frame rate.");
                var slider = Slider(panel);
                panel.FindControl<FASettingsExpanderItem>("ModelFrameRateCard")!.BringIntoView();
                await SettleAsync();
                DragMouse(window, slider, index);
                Check(ModelRenderingPreferences.FrameRate == previous,
                    "A different selected stop does not apply before Save.");
                var save = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "保存"));
                save.BringIntoView(); Dispatcher.UIThread.RunJobs();
                var point = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await operation.WaitAsync(TimeSpan.FromSeconds(3));
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Check(ModelRenderingPreferences.FrameRate == Rate(index)
                    && ThemePreferences.LoadModelFrameRate() == Rate(index)
                    && document.RootElement.GetProperty("modelFrameRate").GetString() == (index switch { 0 => "30", 1 => "60", _ => "unlimited" })
                    && document.RootElement.GetProperty("otherPreference").GetInt32() == 42,
                    "Saving immediately applies one of the three canonical rates and preserves unrelated preferences.");
            }
            File.WriteAllText(Path.Combine(output, "settings-slider-receipts.json"),
                JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await view.DismissSettingsDialogAsync(); window.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    private static void CheckLayout(AppSettingsPanel panel, Slider slider, FASettingsExpanderItem rateCard, FASettingsExpanderItem apiCard)
    {
        var labels = Labels(panel);
        Check(labels.Select(label => label.Text).SequenceEqual(new[] { "30帧", "60帧", "无限制" }),
            "The only labels identify 30, 60 and unlimited.");
        var track = slider.GetVisualDescendants().OfType<Track>().Single();
        var halfThumb = track.Thumb!.Bounds.Width / 2;
        var travel = track.Bounds.Width - track.Thumb.Bounds.Width;
        var editor = panel.FindControl<Grid>("ModelFrameRateEditor")!;
        var trackAt = track.TranslatePoint(default, editor)!.Value;
        for (var i = 0; i < labels.Length; i++)
        {
            var at = labels[i].TranslatePoint(default, editor)!.Value;
            Check(Math.Abs(at.X + labels[i].Bounds.Width / 2 - trackAt.X - halfThumb - travel * i / 2) <= 1,
                "Each frame-rate label is centered under its actual slider stop.");
            Check(at.X >= -.5 && at.X + labels[i].Bounds.Width <= editor.Bounds.Width + .5,
                "Frame-rate labels stay inside their editor.");
        }
        Check(slider.GetVisualDescendants().OfType<TickBar>().Any(ticks => ticks.IsEffectivelyVisible),
            "The standard slider renders visible tick marks.");
        CheckText(rateCard, "3D模型渲染帧率");
        if (apiCard.IsVisible)
        {
            CheckText(apiCard, "3D模型渲染API");
            CheckText(apiCard, "若存在渲染问题，请尝试切换到 OpenGL");
        }
        var editorAt = editor.TranslatePoint(default, rateCard)!.Value;
        Check(editorAt.X >= 0 && editorAt.X + editor.Bounds.Width <= rateCard.Bounds.Width + .5,
            "The three-stop editor fits the existing settings card width.");
    }

    private static void CheckText(FASettingsExpanderItem card, string value)
    {
        var text = card.GetVisualDescendants().OfType<TextBlock>().First(control => control.Text == value);
        var at = text.TranslatePoint(default, card)!.Value;
        Check(text.IsEffectivelyVisible && !text.TextLayout.TextLines.Any(line => line.HasCollapsed)
            && at.X >= 0 && at.Y >= 0 && at.X + text.Bounds.Width <= card.Bounds.Width + .5
            && at.Y + text.Bounds.Height <= card.Bounds.Height + .5
            && text.TextLayout.Height <= text.Bounds.Height + .5
            && text.TextLayout.Width <= text.Bounds.Width + .5,
            "The settings title or guidance fits its card without clipping or ellipsis.");
    }

    private static Point PointAt(Window window, Slider slider, double index)
    {
        var track = slider.GetVisualDescendants().OfType<Track>().Single();
        var thumb = track.Thumb!;
        return track.TranslatePoint(new Point(thumb.Bounds.Width / 2
            + (track.Bounds.Width - thumb.Bounds.Width) * index / 2, track.Bounds.Height / 2), window)!.Value;
    }

    private static void DragMouse(Window window, Slider slider, int index)
    {
        window.MouseDown(PointAt(window, slider, slider.Value), MouseButton.Left);
        window.MouseMove(PointAt(window, slider, index));
        window.MouseUp(PointAt(window, slider, index), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Key(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<FAContentDialog> SettingsAsync(Window window)
    {
        await ModelRendererLifecycleTests.UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>()
            .Any(dialog => Equals(dialog.Title, "应用设置")), "The application settings dialog opens.");
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, "应用设置"));
    }

    private static async Task SettleAsync()
    {
        await Task.Delay(250); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }

    private static void CaptureRows(Window window, FASettingsExpanderItem api, FASettingsExpanderItem rate, string output, string name)
    {
        using var full = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing settings frame.");
        using var memory = new MemoryStream();
        full.Save(memory, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        File.WriteAllBytes(Path.Combine(output, name + "-full.png"), memory.ToArray());
        using var bitmap = SKBitmap.Decode(memory.ToArray());
        foreach (var (card, suffix) in new[] { (api, "api"), (rate, "rate") })
        {
            if (!card.IsVisible) continue;
            var origin = card.TranslatePoint(default, window)!.Value;
            var scale = window.RenderScaling;
            var rect = new SKRectI((int)Math.Floor(origin.X * scale), (int)Math.Floor(origin.Y * scale),
                (int)Math.Ceiling((origin.X + card.Bounds.Width) * scale), (int)Math.Ceiling((origin.Y + card.Bounds.Height) * scale));
            using var crop = new SKBitmap();
            Check(bitmap.ExtractSubset(crop, rect), "The changed settings row fits its window viewport.");
            using var encoded = crop.Encode(SKEncodedImageFormat.Png, 100);
            using var file = new FileStream(Path.Combine(output, name + "-" + suffix + ".png"), FileMode.CreateNew);
            encoded.SaveTo(file);
        }
    }

    private static Slider Slider(AppSettingsPanel panel) => panel.FindControl<Slider>("ModelFrameRateSlider")!;
    private static TextBlock[] Labels(AppSettingsPanel panel) => [panel.FindControl<TextBlock>("ModelFrameRate30Label")!,
        panel.FindControl<TextBlock>("ModelFrameRate60Label")!, panel.FindControl<TextBlock>("ModelFrameRateUnlimitedLabel")!];
    private static ModelFrameRate Rate(int index) => index switch
    { 0 => ModelFrameRate.Fps30, 1 => ModelFrameRate.Fps60, _ => ModelFrameRate.Unlimited };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }
}
