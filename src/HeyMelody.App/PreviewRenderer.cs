using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.Styling;

namespace HeyMelodyWindows.App;

internal sealed record PreviewOptions(bool Live, string Output, string Theme = "light", int Width = 1280,
    int Height = 900, string Scenario = "connected", bool ExpandSettings = false)
{
    public const string Usage = "--ui-preview <new.png> [--theme light|dark] [--size WIDTHxHEIGHT] [--scenario connected|empty|unsupported|partial|disconnected] [--expand-settings]\n--ui-live-preview <new.png> [--theme light|dark] [--size WIDTHxHEIGHT] [--expand-settings]";
    public static bool TryParse(string[] args, out PreviewOptions? options, out string? error)
    {
        options = null; error = null;
        if (args.Length == 0 || args[0] is not ("--ui-preview" or "--ui-live-preview")) return false;
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)) { error = Usage; return true; }
        options = new(args[0] == "--ui-live-preview", args[1]);
        for (int i = 2; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--expand-settings") { options = options with { ExpandSettings = true }; continue; }
            if (++i >= args.Length) { error = Usage; return true; }
            var value = args[i];
            if (flag == "--theme" && value is "light" or "dark") options = options with { Theme = value };
            else if (flag == "--scenario" && !options.Live && value is "connected" or "empty" or "unsupported" or "partial" or "disconnected") options = options with { Scenario = value };
            else if (flag == "--size")
            {
                var parts = value.Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height) ||
                    width is < 900 or > 4096 || height is < 640 or > 4096) { error = "预览尺寸应在 900×640 至 4096×4096 之间。"; return true; }
                options = options with { Width = width, Height = height };
            }
            else { error = Usage; return true; }
        }
        return true;
    }
}

internal static class PreviewRenderer
{
    public static int Run(PreviewOptions options)
    {
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewBootstrap));
            return session.Dispatch(async () =>
            {
                if (Application.Current is App app) app.ApplyTheme(options.Theme);
                if (Application.Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault() is { } theme)
                    theme.CustomAccentColor = Color.FromRgb(0, 120, 212);
                var service = options.Live ? HeadsetServiceFactory.CreateLivePreview() : new ScenarioPreviewService(options.Scenario);
                await using var vm = new MainViewModel(service, new RejectConfirmationService(), preview: true);
                await vm.InitializeAsync();
                if ((options.Live || options.Scenario == "connected") && !vm.HasVerifiedSnapshot)
                    throw new InvalidOperationException("没有可信的耳机状态，无法生成连接预览。");
                var view = new MainView { DataContext = vm };
                var window = new Window { Width = options.Width, Height = options.Height, Content = view, WindowDecorations = WindowDecorations.None };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    if (options.ExpandSettings)
                    {
                        // Expanders exist only after the templates are realized, and their clip animates open.
                        view.ExpandAllSettings();
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromMilliseconds(350))
                        {
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                            Dispatcher.UIThread.RunJobs();
                            await Task.Delay(20);
                        }
                    }
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("渲染器未生成图像。");
                    using var output = new FileStream(Path.GetFullPath(options.Output), FileMode.CreateNew, FileAccess.Write);
                    frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    return 0;
                }
                finally { window.Close(); }
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception failure)
        {
            // Preview failures may contain peer details: emit only the failure type.
            Console.Error.WriteLine($"预览未完成：{failure.GetType().Name}。请检查输出路径、构建产物与耳机状态。");
            return 1;
        }
    }
}

public static class PreviewBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => Program.BuildHeadlessApp();
}

internal sealed class RejectConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
}

internal sealed class ScenarioPreviewService(string scenario) : IHeadsetService
{
    private readonly PreviewService _inner = new();
    public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => scenario == "empty"
        ? Task.FromResult<IReadOnlyList<UiDevice>>([]) : _inner.EnumerateAsync(token);
    public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => Adjust(await _inner.ConnectAsync(identity, token));
    public async Task<UiSnapshot> RefreshAsync(CancellationToken token) => Adjust(await _inner.RefreshAsync(token));
    public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException("Preview forbids settings.");
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private UiSnapshot Adjust(UiSnapshot snapshot)
    {
        if (scenario == "unsupported")
        {
            snapshot.DeviceName = "未适配的 OPPO 耳机"; snapshot.ProductId = "未知"; snapshot.SupportedModel = false;
            snapshot.Firmware = null; snapshot.LeftBattery = snapshot.RightBattery = snapshot.CaseBattery = null;
            snapshot.AncOptions = snapshot.EqualizerOptions = snapshot.SpatialOptions = [];
            snapshot.EqualizerEntries = [];
        }
        if (scenario == "partial")
        {
            snapshot.CaseBattery = null; snapshot.CaseCharging = null; snapshot.HiRes = null; snapshot.CanSetHiRes = false;
            snapshot.Warnings = ["充电盒和 Hi-Res 状态暂时不可用。"];
        }
        if (scenario == "disconnected") { snapshot.Connected = false; snapshot.Failure = "耳机已断开。"; }
        return snapshot;
    }
}
