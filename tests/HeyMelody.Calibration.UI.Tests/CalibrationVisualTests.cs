using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Calibration.UI;

internal static class CalibrationVisualTests
{
    public static int Run()
    {
        var output = Path.GetFullPath(Path.Combine("artifacts", "calibration-ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        using var session = HeadlessUnitTestSession.StartNew(typeof(CalibrationTestBootstrap));
        session.Dispatch<bool>(async () =>
        {
            var service = new TestService();
            await using var model = new CalibrationViewModel(service, "operation", "hearing");
            await model.InitializeAsync();
            var view = new CalibrationView(model);
            var window = new Window { Content = view, Width = 540, Height = 580, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                var guide = view.GetVisualDescendants().OfType<LottieView>().Single();
                for (int attempt = 0; !guide.AnimationLoaded && guide.LoadError is null && attempt < 300; attempt++)
                { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
                Check(guide.AnimationLoaded && guide.AnimationActive, "The independent setup's ready page actually displays its source's original Lottie guide.");
                for (int frame = 0; frame < 15; frame++)
                { using var tick = window.CaptureRenderedFrame(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
                Check(guide.RenderedFrameCount > 1, "The setup guide produces actual Avalonia compositor frames.");
                Capture(window, "hearing-ready");

                Push(service.State with { Stage = "AwaitingAnswer", HearingVersion = "V1", ToneAcknowledged = true });
                Check(HasButton("听到了") && HasButton("没听到") && !view.GetVisualDescendants().OfType<Slider>().Any(),
                    "The V1 workflow uses two answer buttons.");
                Capture(window, "hearing-v1");
                Push(service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", StepIndex = 1, LevelIndex = 8, FrequencyType = 2, FrequencyHz = 1000 });
                var slider = view.GetVisualDescendants().OfType<Slider>().Single();
                Check(slider.Value == 8 && HasButton("继续") && HasButton("上一步") && service.Inputs.Count == 0,
                    "V2 has its discrete level slider and both navigation buttons; remote state updates send no command.");
                Push(service.State with { LevelIndex = 9 });
                Check(slider.Value == 9 && service.Inputs.Count == 0, "A pushed level changes the existing slider without starting a measurement command.");
                Check(view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("1000 Hz") == true),
                    "The workflow displays the frequency supplied by the verified device mapping.");
                Push(service.State with { StepIndex = 2, FrequencyType = 3, FrequencyHz = 2000, LevelIndex = 5, Decibels = -19 });
                slider = view.GetVisualDescendants().OfType<Slider>().Single();
                Check(slider.Value == 5 && service.Inputs.Count == 0, "Every following frequency resets to the factory middle value without a duplicate UI write.");
                slider.Focus(NavigationMethod.Tab);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                await Until(() => service.Inputs.Count > 0 && !model.LevelPending);
                Check(slider.Value == 8 && service.Inputs is [{ Action: "level", LevelIndex: 8, Released: true }] && model.CanContinue,
                    "Actual slider keyboard input sends the final changed level and enables Next only after its ACK.");
                FindButton("继续").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => service.Inputs.Last().Action == "next" && !model.Busy);
                Check(view.GetVisualDescendants().OfType<Slider>().Single().Value == 5,
                    "A real Next click displays the following step's middle level.");
                Push(service.State with { FrequencyHz = null });
                Check(view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("频段 4") == true)
                    && !view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("4000 Hz") == true),
                    "An unverified model displays a frequency identifier rather than claiming the X4 Hz mapping.");
                Capture(window, "hearing-v2");
                Push(service.State with { Stage = "Interrupted", CanResume = false, ToneAcknowledged = false });
                Check(!view.GetVisualDescendants().OfType<Slider>().Any() && !FindButton("继续测试").IsEnabled,
                    "A hardware interruption removes answer controls until the earphones can resume.");
                Push(service.State with { CanResume = true });
                Check(FindButton("继续测试").IsEnabled, "A recovery event restores the resume button without rebuilding the operation.");
                FindButton("继续测试").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => service.Inputs.Last().Action == "resume" && !model.Busy);
                Push(service.State with { Stage = "Failed", StepIndex = 12, HearingLevels = Enumerable.Range(0, 12)
                    .Select(i => new UiHearingPoint(i / 6 + 1, i % 6 + 1, -19)).ToArray() });
                Check(HasButton("重新获取结果") && !HasButton("重试"), "A filter timeout retries the retained answers rather than starting a new hearing measurement.");
                FindButton("重新获取结果").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => service.Inputs.Last().Action == "retry-curves" && !model.Busy);
                Check(HasButton("保存并启用"), "A successful curve retry restores the result page.");
                Push(service.State with { Stage = "Results", StepIndex = 12, FiltersReady = false });
                Check(!view.GetVisualDescendants().OfType<TextBox>().Any() && !HasButton("保存并启用"),
                    "Twelve answers alone do not expose the result or save controls while filters are incomplete.");
                Push(service.State with { FiltersReady = true, CanPreview = true, HasInterEarDifference = true });
                Check(view.GetVisualDescendants().OfType<TextBox>().Count() == 1 && HasButton("保存并启用"),
                    "The result page names and saves a profile, separately from the main-window selector.");
                Check(view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("左右耳在部分频段的选择差异较大") == true),
                    "The result reports an inter-ear selection difference without assigning a hearing diagnosis.");
                var editor = view.GetVisualDescendants().OfType<TextBox>().Single(); editor.Text = "我的听感";
                foreach (var (text, action, mode) in new[] { ("原音", "preview-original", "Original"), ("黄金听感", "preview-enhanced", "Enhanced"), ("结束试听", "preview-stop", "Stopped") })
                {
                    FindButton(text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => service.Inputs.Last().Action == action && !model.Busy);
                    Check(model.State.PreviewMode == mode && ReferenceEquals(editor, view.GetVisualDescendants().OfType<TextBox>().Single())
                        && editor.Text == "我的听感", "Preview sends the chosen effect and preserves the unsaved name.");
                }
                Capture(window, "hearing-results");
            }
            finally { window.Close(); }
            return true;
            bool HasButton(string name) => view.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, name));
            Button FindButton(string name) => view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, name));
            void Push(UiCalibrationState state)
            { service.State = state with { Revision = model.State.Revision + 1 }; model.Adopt(service.State); Dispatcher.UIThread.RunJobs(); }
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS independent hearing wizard renders its original guide and V1/V2/results layouts without setters");

        session.Dispatch<bool>(async () =>
        {
            var service = new TestService();
            await using var model = new CalibrationViewModel(service, "operation", "fit");
            await model.InitializeAsync();
            var view = new CalibrationView(model);
            var window = new Window { Content = view, Width = 540, Height = 580, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs(); Capture(window, "fit-ready");
                service.FailNextStart = true;
                view.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "开始")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => model.State.Stage == "Failed" && !model.Busy);
                Check(model.Error.Length > 0 && view.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "重试")).IsEnabled,
                    "A failed fit start restores a usable retry and does not leave the body busy.");
                service.State = service.State with { Revision = service.State.Revision + 1, Stage = "Completed", FitResults = [new(1, 1), new(2, 6)] };
                model.Adopt(service.State);
                Dispatcher.UIThread.RunJobs();
                var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                Check(labels.Contains("贴合良好") && labels.Contains("贴合较差，请调整佩戴或更换耳塞")
                    && view.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "重新测试")),
                    "Fit results distinguish left/right and offer a retry.");
                Capture(window, "fit-results");
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS independent fit window renders both ears and retry");

        session.Dispatch<bool>(async () =>
        {
            var service = ActiveService();
            service.StopFailuresRemaining = 1;
            var window = new CalibrationWindow(service, "operation", "hearing");
            window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                window.Close();
                await Until(() => service.Cancels == 1 && Button(window, "重试停止")?.IsEnabled == true);
                Check(window.IsVisible && !service.Disposed && service.State.Stage == "Failed",
                    "A real window close remains open after a failed STOP and keeps its IPC service available to retry.");
                Capture(window, "stop-failed");
                service.StopFailuresRemaining = 1;
                service.Push(new BackendEvent { Kind = "requestExit", ExitRequestId = "exit-failed" });
                await Until(() => service.ExitApprovals.Count == 1);
                Check(service.ExitApprovals is [("exit-failed", false)] && window.IsVisible && !service.Disposed,
                    "A background exit request receives false and leaves the window open when STOP fails.");
                service.Push(new BackendEvent { Kind = "requestExit", ExitRequestId = "exit-retry" });
                await Until(() => !window.IsVisible);
                Check(service.ExitApprovals is [("exit-failed", false), ("exit-retry", true)] && service.Cancels == 3 && service.Disposed,
                    "A retried background exit receives true only after a confirmed STOP, then closes the window.");
            }
            finally { if (window.IsVisible) { service.StopFailuresRemaining = 0; window.Close(); await Until(() => !window.IsVisible); } }
            return true;
            static TestEventService ActiveService() => new() { State = new UiCalibrationState
                { Handle = "operation", Identity = "earbuds", Generation = 42, Revision = 1, BackendInstanceId = "fixture",
                    Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = true } };
            static Button? Button(Window target, string text) => target.GetVisualDescendants().OfType<Button>().SingleOrDefault(x => Equals(x.Content, text));
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS real window STOP failure/retry and truthful exit acknowledgements");
        Console.WriteLine("Calibration UI screenshots " + output);
        return 3;
        void Capture(Window window, string name)
        {
            using var pixels = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing calibration pixels.");
            using var file = File.Create(Path.Combine(output, name + ".png"));
            pixels.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
        Check(condition(), "Timed out waiting for calibration interaction.");
    }
}

public static class CalibrationTestBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<CalibrationTestApplication>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class CalibrationTestApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentAvaloniaTheme { CustomAccentColor = Color.FromRgb(0, 120, 212) });
    }
}
