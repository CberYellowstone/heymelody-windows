using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HeyMelodyWindows.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] is "--ui-preview" or "--ui-live-preview")
        {
            IHeadsetService service = e.Args[0] == "--ui-preview" ? new PreviewService() : new ReadOnlyPreviewService(new ClientBridge());
            var window = new MainWindow(service, preview: true)
            { Width = 1280, Height = 1440, WindowStartupLocation = WindowStartupLocation.Manual, Left = -15000, Top = -15000 };
            MainWindow = window;
            var exitCode = 0;
            try
            {
                await window.InitializeAsync();
                if (!window.HasVerifiedSnapshot) throw new InvalidOperationException();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var content = (FrameworkElement)window.Content;
                window.Content = null; content.Width = 1280; content.Height = 1440;
                TextElement.SetFontFamily(content, new FontFamily("Segoe UI")); TextElement.SetFontSize(content, 14);
                content.Measure(new Size(1280, 1440)); content.Arrange(new Rect(0, 0, 1280, 1440)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1280, 1440, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new FileStream(Path.GetFullPath(e.Args[1]), FileMode.CreateNew, FileAccess.Write); encoder.Save(stream);
            }
            catch { exitCode = 1; }
            finally
            {
                try { await service.DisposeAsync(); } catch { exitCode = 1; }
                window.Close();
            }
            Shutdown(exitCode); return;
        }
        // ClientBridge is root-owned and isolated from WPF; constructor performs no native work.
        MainWindow = new MainWindow(new ClientBridge());
        MainWindow.Show();
    }
}
