using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;
using SkiaSharp;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Resources;
using System.Security.Cryptography;

internal static class DeviceVisualTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("artwork requires actual PID and does not infer models from Bluetooth names", IdentityAsync);
        Run("source changes reject old artwork requests and release their leases", SourceAsync);
        Run("colour changes cancel stale requests and resolve the replacement artwork", ColorAsync);
        Run("X4 product PNG uses independent presentation controls and shared bitmap data", PngAsync);
        Run("active renderer leases outlive catalog replacement and release exactly once", LeaseAsync);
        Run("Skottie renders embedded and package images and rejects escaped paths", LottieAsync);
        Run("original hearing Lottie renders a frame, pauses while hidden, and releases on detach", OriginalLottieAsync);
        return 7;
        void Run(string label, Func<Task> test)
        { session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("PASS " + label); }
    }
    public static void CheckScenario(MainView view, string scenario)
    {
        var hero = view.FindControl<ProductArtwork>("HeroArt")!;
        if (scenario is "connected" or "partial")
            Check(Equals(hero.Visual, DeviceVisuals.X4Product), "Verified X4 keeps its own artwork.");
        else Check(Equals(hero.Visual, DeviceVisuals.GenericProduct), "Unknown and absent devices use generic artwork.");
    }
    private static Task IdentityAsync()
    {
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "OPPO Enco X4", false);
        catalog.Update([device], device, null);
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "A model name does not provide an actual product ID.");
        catalog.Update([device], device, "06EC10");
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.X4Product), "X4 PID selects X4 artwork.");
        catalog.Update([device], device, "068C10");
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "Free4 never borrows X4 artwork.");
        catalog.Update([], null, null);
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "Removed devices lose temporary product knowledge.");
        return Task.CompletedTask;
    }
    private static async Task SourceAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", false);
        catalog.Update([device], device, "06EC10", 1, "HeyMelody", artwork: service);
        catalog.Update([device], device, "06EC10", 1, "WirelessEarphones", artwork: service);
        foreach (var request in service.Requests.ToArray()) request.Completion.TrySetResult(Art(request.Role, "HeyMelody"));
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "A response from another resource source cannot be displayed.");
        Check(service.Requests.Count == 2 && service.Requests.All(request => request.Role == "hero")
            && service.Releases.Count == 2, "Stale and wrong-source hero leases are released without requesting device icons.");
    }
    private static Task PngAsync()
    {
        var hero = new ProductArtwork { Visual = DeviceVisuals.X4Product, Width = 240, Height = 180 };
        var second = new ProductArtwork { Visual = new BitmapVisual(DeviceVisuals.X4Product.Source), Width = 240, Height = 180 };
        var window = new Window { Content = new StackPanel { Children = { hero, second } }, Width = 300, Height = 420 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var image = hero.GetVisualDescendants().OfType<Image>().Single();
            var another = second.GetVisualDescendants().OfType<Image>().Single();
            Check(!ReferenceEquals(image, another) && ReferenceEquals(image.Source, another.Source), "Hosts own their image controls and share decoded bundled bitmap data.");
            Check(image.Stretch == Stretch.Uniform && DeviceVisuals.X4Product.Source.AbsolutePath.EndsWith("x4-pods-with-earphones.png"),
                "The main product view keeps its original X4 artwork.");
            Check(!hero.GetVisualDescendants().OfType<DeviceModelView>().Any(), "X4 PNG does not display Free4's model.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }
    private static async Task ColorAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", false);
        catalog.Update([device], device, "068C10", 1, "HeyMelody", artwork: service);
        var stale = service.Requests.ToArray();
        catalog.Update([device], device, "068C10", 2, "HeyMelody", artwork: service);
        Check(service.Requests.Count == 2 && service.Requests.All(request => request.Role == "hero"),
            "Changing colour starts fresh hero requests without waiting for old requests or fetching device icons.");
        foreach (var request in stale) request.Completion.TrySetResult(Art(request.Role, "HeyMelody"));
        foreach (var request in service.Requests.Except(stale)) request.Completion.TrySetResult(Art(request.Role, "HeyMelody"));
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        Check(catalog.ProductFor(device) is FileBitmapVisual && service.Releases.Count == 1,
            "Only the replacement colour is accepted; the stale hero package lease is released.");
    }
    private static Task LeaseAsync()
    {
        var releases = 0;
        var shared = new SharedArtworkLease(new TestLease(() => ++releases));
        var first = shared.Acquire(); var second = shared.Acquire();
        shared.Dispose(); Check(releases == 0, "Replaced catalog data cannot delete resources used by renderers.");
        first.Dispose(); first.Dispose(); Check(releases == 0, "A renderer lease is idempotent.");
        second.Dispose(); Check(releases == 1, "The last renderer releases the package exactly once.");
        return Task.CompletedTask;
    }
    private static Task LottieAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "heymelody-lottie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        try
        {
            using var bitmap = new SKBitmap(32, 32); using var painter = new SKCanvas(bitmap); painter.Clear(SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var png = encoded.ToArray(); File.WriteAllBytes(Path.Combine(folder, "images", "sample.png"), png);
            foreach (var embedded in new[] { false, true })
            {
                var image = embedded ? "data:image/png;base64," + Convert.ToBase64String(png) : "sample.png";
                var prefix = embedded ? "" : "images/";
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    v = "5.7.0", fr = 30, ip = 0, op = 60, w = 64, h = 64,
                    assets = new[] { new { id = "pic", w = 32, h = 32, u = prefix, p = image, e = embedded ? 1 : 0 } },
                    layers = new[] { new { ddd = 0, ind = 1, ty = 2, refId = "pic",
                        ks = new { o = new { a = 0, k = 100 }, r = new { a = 0, k = 0 },
                            p = new { a = 0, k = new[] {32,32,0} }, a = new { a = 0, k = new[] {16,16,0} }, s = new { a = 0, k = new[] {100,100,100} } },
                        ip = 0, op = 60, st = 0, bm = 0 } }
                });
                var path = Path.Combine(folder, "animation.json"); File.WriteAllText(path, json);
                using var loaded = LottiePlayback.Load(new ArtworkFile(new Uri(path), folder, ""));
                using var frame = new SKBitmap(64, 64); using var canvas = new SKCanvas(frame); canvas.Clear(SKColors.Transparent);
                loaded.Animation.SeekFrameTime(.1); loaded.Animation.Render(canvas, new SKRect(0, 0, 64, 64));
                Check(frame.GetPixel(32, 32).Red > 200 && frame.GetPixel(32, 32).Alpha > 200, "Skottie decoded and rendered the image provider's pixels.");
            }
            var rejected = false;
            try { LottiePlayback.ValidateImages(Encoding.UTF8.GetBytes("{\"assets\":[{\"p\":\"../../outside.png\"}]}"), folder, folder); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Package image traversal is rejected before native parsing.");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    private static async Task OriginalLottieAsync()
    {
        var bundled = new BundledResourceStore();
        var asset = bundled.FindNamedAsset(ResourceSource.HeyMelody, "melody_ui_gold_hearing_detecting_guide")
            ?? throw new InvalidOperationException("Original hearing Lottie is missing from the installed resource bundle.");
        var path = bundled.FullPath(asset);
        var file = new ArtworkFile(new Uri(path), Path.GetDirectoryName(path)!, asset.Sha256);
        var output = Path.GetFullPath(Path.Combine("artifacts", "lottie-original-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        using (var loaded = LottiePlayback.Load(file))
        {
            using var frame = new SKBitmap(256, 256); using var canvas = new SKCanvas(frame);
            canvas.Clear(SKColors.Transparent); loaded.Animation.SeekFrameTime(loaded.Animation.Duration.TotalSeconds / 2);
            loaded.Animation.Render(canvas, new SKRect(0, 0, 256, 256));
            Check(frame.Pixels.Any(pixel => pixel.Alpha > 0), "Original APK animation produces visible pixels through native Skottie.");
            using var png = frame.Encode(SKEncodedImageFormat.Png, 100);
            var framePath = Path.Combine(output, "hearing-guide.png"); File.WriteAllBytes(framePath, png.ToArray());
            Console.WriteLine("Original Lottie frame SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(framePath))).ToLowerInvariant());
        }
        var acquired = 0; var released = 0;
        var animation = new LottieView(file with { AcquireLease = () => { Interlocked.Increment(ref acquired); return new TestLease(() => Interlocked.Increment(ref released)); } });
        var container = new Border { Child = animation };
        var window = new Window { Content = container, Width = 280, Height = 280 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            for (var attempt = 0; !animation.AnimationLoaded && animation.LoadError is null && attempt < 300; attempt++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Check(animation.AnimationLoaded && animation.AnimationActive, "The Avalonia compositor starts a loaded animation.");
            container.IsVisible = false; Dispatcher.UIThread.RunJobs();
            Check(!animation.AnimationActive, "A hidden ancestor pauses the animation.");
            container.IsVisible = true; Dispatcher.UIThread.RunJobs();
            Check(animation.AnimationActive, "Making the parent visible resumes the animation.");
            window.Hide(); Dispatcher.UIThread.RunJobs(); Check(!animation.AnimationActive, "A hidden window pauses animation frames.");
            window.Show(); Dispatcher.UIThread.RunJobs(); Check(animation.AnimationActive, "A shown window resumes without rereading its resource.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            for (var attempt = 0; released < acquired && attempt < 100; attempt++)
            { using var frame = window.CaptureRenderedFrame(); await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Check(!animation.AnimationActive && !animation.AnimationLoaded && acquired == 1 && released == 1,
                "Detaching disposes native Skottie objects and releases the active package lease once.");
        }
        finally { window.Close(); }
    }
    private static UiArtwork Art(string role, string source) => new(Guid.NewGuid().ToString("N"), Path.GetFullPath("artwork-" + role + ".png"), "image", Path.GetFullPath("."), "", 1, source, 0);
    private sealed class ArtworkService : IArtworkService
    {
        public sealed record Request(string Role, TaskCompletionSource<UiArtwork?> Completion);
        public List<Request> Requests { get; } = [];
        public List<string> Releases { get; } = [];
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token)
        { var completion = new TaskCompletionSource<UiArtwork?>(); Requests.Add(new(role, completion)); return completion.Task; }
        public Task ReleaseArtworkAsync(string id, CancellationToken token) { Releases.Add(id); return Task.CompletedTask; }
    }
    private sealed class TestLease(Action release) : IDisposable { public void Dispose() => release(); }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
