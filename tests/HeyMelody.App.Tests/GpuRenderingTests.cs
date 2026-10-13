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
        Run("model GPU acceleration defaults on and preserves an explicit software preference", PreferencesAsync);
        Run("model rendering applies saved choices to existing, new and reattached views", ViewsAsync);
        Run("model GPU settings default on, cancel drafts and save either rendering choice", SettingsAsync);
        Run("software materials use each atlas region and restore original GPU materials", SoftwareMaterialTests.AtlasAsync);
        Run("audited models receive opaque depth correction without changing unknown artwork", ModelMaterialCorrectionTests.RunAsync);
        return 5;

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
        Check(ThemePreferences.LoadModelGpuAcceleration() && ModelRenderingPreferences.UseGpuAcceleration,
            "A new installation defaults to automatic GPU model rendering.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"theme\":\"dark\",\"otherPreference\":42}");
        Check(ThemePreferences.Load() == "dark" && ThemePreferences.LoadModelGpuAcceleration(),
            "Existing theme-only files retain their theme and adopt the GPU default.");
        Check(ThemePreferences.Save("light", false), "An explicit software preference can be saved.");
        ModelRenderingPreferences.Reload();
        Check(ThemePreferences.Load() == "light" && !ModelRenderingPreferences.UseGpuAcceleration,
            "A subsequent startup retains saved false instead of replacing it with the GPU default.");
        Check(ThemePreferences.Save("dark"), "Updating the theme succeeds.");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Check(document.RootElement.GetProperty("otherPreference").GetInt32() == 42
                && !document.RootElement.GetProperty("modelGpuAcceleration").GetBoolean(),
                "Theme-only updates preserve an explicit software choice and other stored preferences.");
        ModelRenderingPreferences.Reload();
        Check(!ModelRenderingPreferences.UseGpuAcceleration, "Theme-only saves also retain software across a reload.");
        Check(ThemePreferences.Save("light", true), "GPU rendering can be explicitly re-enabled.");
        ModelRenderingPreferences.Reload();
        Check(ModelRenderingPreferences.UseGpuAcceleration && ThemePreferences.LoadModelGpuAcceleration(),
            "An explicit saved true reloads as GPU rendering.");
        Check(ThemePreferences.Save("dark") && ThemePreferences.LoadModelGpuAcceleration(),
            "A later theme-only save retains the explicit GPU choice.");
        foreach (var json in new[]
        {
            "{", "[]", "{}", "{\"modelGpuAcceleration\":\"true\"}", "{\"modelGpuAcceleration\":\"false\"}",
            "{\"modelGpuAcceleration\":null}", "{\"modelGpuAcceleration\":0}",
            "{\"modelGpuAcceleration\":[]}", "{\"modelGpuAcceleration\":{}}"
        })
        {
            File.WriteAllText(path, json);
            ModelRenderingPreferences.Reload();
            Check(ThemePreferences.LoadModelGpuAcceleration() && ModelRenderingPreferences.UseGpuAcceleration,
                "Missing, invalid or mistyped GPU settings use the GPU default.");
        }
        File.WriteAllText(path, "{\"theme\":\"dark\",\"modelGpuAcceleration\":false}");
        ModelRenderingPreferences.Reload();
        Check(ThemePreferences.Load() == "dark" && !ThemePreferences.LoadModelGpuAcceleration()
            && !ModelRenderingPreferences.UseGpuAcceleration,
            "An existing file with saved false retains both its theme and software preference.");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Atomic preference saves leave no temporary file.");
        return Task.CompletedTask;
    }

    private static Task ViewsAsync()
    {
        var model = new EncoFree4DynaudioModelView();
        Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic
            && new EncoFree4ModelView().Viewport.PreferredBackend == RenderBackendKind.Automatic,
            "Both bundled model entry points default to automatic GPU rendering before attachment.");
        var window = new Window { Content = model, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            ModelRenderingPreferences.Apply(false);
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Software
                && new EncoFree4ModelView().Viewport.PreferredBackend == RenderBackendKind.Software,
                "Disabling GPU updates the live viewport and subsequent views.");
            ModelRenderingPreferences.Apply(true);
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic,
                "Re-enabling GPU applies immediately to the live viewport.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            ModelRenderingPreferences.Apply(false);
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic && !model.AnimationActive,
                "A detached model releases its rendering preference subscription and timer.");
            window.Content = model; Dispatcher.UIThread.RunJobs();
            Check(model.Viewport.PreferredBackend == RenderBackendKind.Software, "Reattaching uses the current software setting.");
        }
        finally { window.Close(); ModelRenderingPreferences.Apply(true); }
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
            Check(toggle.IsChecked == true, "The GPU setting is visible and on by default.");
            toggle.IsChecked = false;
            Check(ModelRenderingPreferences.UseGpuAcceleration, "Editing a software draft leaves the active GPU preference unchanged.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(ModelRenderingPreferences.UseGpuAcceleration && !File.Exists(ThemePreferences.SettingsPathOverride!),
                "Cancelling leaves the model setting and preference file unchanged.");

            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            toggle = dialog.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "ModelGpuSwitch");
            Check(toggle.IsChecked == true, "Reopening a cancelled draft retains the GPU default.");
            toggle.IsChecked = false;
            ClickSave(window, dialog); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            var model = view.GetVisualDescendants().OfType<EncoFree4DynaudioModelView>().Single();
            Check(!ModelRenderingPreferences.UseGpuAcceleration && !ThemePreferences.LoadModelGpuAcceleration()
                && model.Viewport.PreferredBackend == RenderBackendKind.Software,
                "Saving off persists software rendering on the already displayed demo.");

            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "应用设置"));
            toggle = dialog.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "ModelGpuSwitch");
            Check(toggle.IsChecked == false, "Reopening settings displays the saved software choice.");
            toggle.IsChecked = true;
            ClickSave(window, dialog); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(ModelRenderingPreferences.UseGpuAcceleration && ThemePreferences.LoadModelGpuAcceleration()
                && model.Viewport.PreferredBackend == RenderBackendKind.Automatic,
                "Saving on returns the existing model to GPU rendering without restarting or losing the resource.");
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
