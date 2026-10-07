using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

                Push(service.State with { Stage = "AwaitingAnswer", HearingVersion = "V1" });
                Check(HasButton("听到了") && HasButton("没听到") && !view.GetVisualDescendants().OfType<Slider>().Any(),
                    "The V1 workflow uses two answer buttons.");
                Capture(window, "hearing-v1");
                Push(service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", StepIndex = 1, LevelIndex = 8 });
                var slider = view.GetVisualDescendants().OfType<Slider>().Single();
                Check(slider.Value == 8 && HasButton("继续") && HasButton("上一步") && service.Inputs.Count == 0,
                    "V2 has its discrete level slider and both navigation buttons; remote state updates send no command.");
                Push(service.State with { LevelIndex = 9 });
                Check(slider.Value == 9 && service.Inputs.Count == 0, "A pushed level changes the existing slider without starting a measurement command.");
                Capture(window, "hearing-v2");
                Push(service.State with { Stage = "Results" });
                Check(view.GetVisualDescendants().OfType<TextBox>().Count() == 1 && HasButton("保存并启用"),
                    "The result page names and saves a profile, separately from the main-window selector.");
                Capture(window, "hearing-results");
            }
            finally { window.Close(); }
            return true;
            bool HasButton(string name) => view.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, name));
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
                model.Adopt(service.State with { Revision = 2, Stage = "Completed", FitResults = [new(1, 1), new(2, 6)] });
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
        Console.WriteLine("Calibration UI screenshots " + output);
        return 2;
        void Capture(Window window, string name)
        {
            using var pixels = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing calibration pixels.");
            using var file = File.Create(Path.Combine(output, name + ".png"));
            pixels.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
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
