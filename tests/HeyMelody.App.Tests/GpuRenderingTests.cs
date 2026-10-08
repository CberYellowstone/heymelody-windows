using System.Text.Json;
using Ava3D;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class GpuRenderingTests
{
    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        Run("model GPU acceleration defaults off and survives preference updates", PreferencesAsync);
        Run("model rendering applies saved choices to existing, new and reattached views", ViewsAsync);
        Run("model GPU settings cancel drafts and save the software choice immediately", SettingsAsync);
        Run("software materials use each atlas region and restore original GPU materials", SoftwareMaterialTests.AtlasAsync);
        return 4;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () =>
            {
                var oldPath = ThemePreferences.SettingsPathOverride;
                var oldPreference = ModelRenderingPreferences.UseGpuAcceleration;
                ThemePreferences.SettingsPathOverride = Path.Combine(output, "gpu-preferences-" + Guid.NewGuid().ToString("N"), "preferences.json");
                ModelRenderingPreferences.Reload();
                try { await action(); }
                finally { ThemePreferences.SettingsPathOverride = oldPath; ModelRenderingPreferences.Apply(oldPreference); }
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static Task PreferencesAsync()
    {
        var path = ThemePreferences.SettingsPathOverride!;
        Check(!ThemePreferences.LoadModelGpuAcceleration() && !ModelRenderingPreferences.UseGpuAcceleration,
            "A new installation explicitly requests software model rendering.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"theme\":\"dark\",\"otherPreference\":42}");
        Check(ThemePreferences.Load() == "dark" && !ThemePreferences.LoadModelGpuAcceleration(),
            "Existing theme-only files retain their theme and default to software.");
        Check(ThemePreferences.Save("light", true), "Both appearance choices can be saved.");
        ModelRenderingPreferences.Reload();
        Check(ThemePreferences.Load() == "light" && ModelRenderingPreferences.UseGpuAcceleration,
            "A subsequent startup reloads the selected model backend.");
        Check(ThemePreferences.Save("dark"), "Updating the theme succeeds.");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Check(document.RootElement.GetProperty("otherPreference").GetInt32() == 42
                && document.RootElement.GetProperty("modelGpuAcceleration").GetBoolean(),
                "Theme updates preserve the GPU choice and other stored preferences.");
        foreach (var json in new[] { "{", "[]", "{\"modelGpuAcceleration\":\"true\"}", "{\"modelGpuAcceleration\":null}" })
        {
            File.WriteAllText(path, json);
            Check(!ThemePreferences.LoadModelGpuAcceleration(), "Invalid or mistyped GPU settings remain safely off.");
        }
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Atomic preference saves leave no temporary file.");
        return Task.CompletedTask;
    }

    private static Task ViewsAsync()
    {
        var model = new EncoFree4DynaudioModelView();
        Check(model.Viewport.PreferredBackend == RenderBackendKind.Software
            && new EncoFree4ModelView().Viewport.PreferredBackend == RenderBackendKind.Software,
            "Both bundled model entry points are software before they are ever attached.");
        var window = new Window { Content = model, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            ModelRenderingPreferences.Apply(true);
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic
                && new EncoFree4ModelView().Viewport.PreferredBackend == RenderBackendKind.Automatic,
                "Opting in updates the live viewport and subsequent views.");
            ModelRenderingPreferences.Apply(false);
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Software, "Disabling GPU applies immediately to the live viewport.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            ModelRenderingPreferences.Apply(true);
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Software && !model.AnimationActive,
                "A detached model releases its rendering preference subscription and timer.");
            window.Content = model; Dispatcher.UIThread.RunJobs();
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic, "Reattaching uses the current setting.");
        }
        finally { window.Close(); ModelRenderingPreferences.Apply(false); }
        return Task.CompletedTask;
    }

    private static async Task SettingsAsync()
    {
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        await vm.AddDemoDeviceAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            var toggle = dialog.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "ModelGpuSwitch");
            Check(toggle.IsChecked == false, "The GPU setting is visible and off by default.");
            toggle.IsChecked = true;
            Check(!ModelRenderingPreferences.UseGpuAcceleration, "Editing a draft never initializes a GPU model backend.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!ModelRenderingPreferences.UseGpuAcceleration && !File.Exists(ThemePreferences.SettingsPathOverride!),
                "Cancelling leaves the model setting and preference file unchanged.");

            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            toggle = dialog.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "ModelGpuSwitch");
            toggle.IsChecked = true;
            ClickSave(window, dialog); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            var model = view.GetVisualDescendants().OfType<EncoFree4DynaudioModelView>().Single();
            Check(ModelRenderingPreferences.UseGpuAcceleration && ThemePreferences.LoadModelGpuAcceleration()
                && model.Viewport.PreferredBackend == RenderBackendKind.Automatic,
                "Saving enables and persists GPU rendering on the already displayed demo.");

            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            toggle = dialog.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "ModelGpuSwitch");
            Check(toggle.IsChecked == true, "Reopening settings displays the saved GPU choice.");
            toggle.IsChecked = false;
            ClickSave(window, dialog); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!ModelRenderingPreferences.UseGpuAcceleration && !ThemePreferences.LoadModelGpuAcceleration()
                && model.Viewport.PreferredBackend == RenderBackendKind.Software,
                "Saving off returns the existing model to software without restarting or losing the resource.");
        }
        finally { await view.DismissSettingsDialogAsync(); window.Close(); }
    }

    private static void ClickSave(Window window, FAContentDialog dialog)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "保存"));
        button.BringIntoView(); Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }
}
