using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;
using HeyMelody.Rendering.Filament;
using HeyMelody.Rendering.Tests;

/// <summary>Real offscreen Filament checks; PNGs contain only the changed model area.</summary>
internal static class NativeModelSmoke
{
    private sealed record Shot(byte[] Pixels, FilamentFrameInfo Frame, int Foreground, int Left, int Top, int Right, int Bottom,
        int InteriorPixels, double BrightnessP10, double BrightnessP90, double ClippedWhiteFraction);
    public static object Run(string output, Action<bool, string> check, FilamentBackend backend,
        bool occlusionOnly = false, string? externalModel = null)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This native validation currently requires Windows x64; portable checks do not load Filament.");
        var files = externalModel is not null ? new[] { externalModel } : new[]
        {
            GlbSource.BundledModel("EncoFree4"), GlbSource.BundledModel("EncoFree4Dynaudio"),
            Path.Combine(GlbSource.RepositoryRoot(), "src", "HeyMelody.Resources", "Bundles", "data", GlbSource.X3BlackSha256 + ".glb")
        };
        var receipts = new List<object>();
        foreach (string path in files)
        {
            var bytes = File.ReadAllBytes(path);
            var source = GlbSource.Inspect(bytes);
            check(FilamentModelProfiles.IsAuditedOpaque1001(source.Sha256), "Native fixtures are exact independently audited original models.");
            using var renderer = NativeFilamentRendererFactory.Instance.Create(backend);
            renderer.LoadModel(new(bytes, source.Sha256));
            var config = new FilamentRenderConfiguration(320, 280, ClearRed: .11f, ClearGreen: .23f, ClearBlue: .37f);
            var first = Capture(renderer, config, 0, check);
            var diagnostics = renderer.GetDiagnostics();
            check(diagnostics.RequestedBackend == backend && diagnostics.ActualBackend == backend,
                "The real native renderer uses the explicitly requested backend without fallback.");
            check(diagnostics.SourceMeshes == source.Meshes && diagnostics.SourceNodes == source.Nodes
                && diagnostics.SourcePrimitives == source.Primitives && diagnostics.SourceTriangles == source.Triangles
                && diagnostics.SourceMaterials == source.Materials,
                "The native loader retains every original mesh, node, primitive, material and triangle.");
            check(diagnostics.PatchedMaterials == 1 && source.AlphaMode == "BLEND" && source.MaterialName == "1001",
                "The native exact-SHA correction selects opaque-1001 while source transparency remains authored.");
            check(diagnostics.State == FilamentRendererState.Ready && diagnostics.MsaaSamples == 4
                && !string.IsNullOrWhiteSpace(diagnostics.Backend) && diagnostics.Backend != "Unavailable",
                "A real ready Filament backend with four samples renders the model.");
            string stem = source.Sha256[..8];
            Save(Path.Combine(output, stem + "-front.png"), first);
            check(first.InteriorPixels > 500 && first.BrightnessP90 - first.BrightnessP10 >= 8 && first.ClippedWhiteFraction < .9,
                $"The actual model has internal shading rather than a flat white silhouette; interior={first.InteriorPixels}, "
                + $"P10={first.BrightnessP10:F2}, P90={first.BrightnessP90:F2}, whiteFraction={first.ClippedWhiteFraction:F4}.");
            check(first.Left > 0 && first.Top > 0 && first.Right < first.Frame.Width - 1 && first.Bottom < first.Frame.Height - 1,
                "Automatic camera fitting leaves the complete foreground inside the frame.");
            var unchanged = new byte[first.Pixels.Length];
            ulong settledFrame = first.Frame.FrameId;
            var settling = Stopwatch.StartNew();
            var idle = Stopwatch.StartNew();
            while (settling.Elapsed < TimeSpan.FromSeconds(3) && idle.Elapsed < TimeSpan.FromMilliseconds(120))
            {
                if (renderer.TryCopyLatestFrame(settledFrame, unchanged, out var newer))
                {
                    if (newer.FrameId <= settledFrame) throw new InvalidOperationException("The native queue returned a duplicate frame ID.");
                    settledFrame = newer.FrameId;
                    idle.Restart();
                }
                Thread.Sleep(20);
            }
            check(idle.Elapsed >= TimeSpan.FromMilliseconds(120),
                "After submitted work settles, cached frame reads neither return duplicates nor keep rendering while idle.");
            var motion = new HeyMelodyWindows.App.ModelOrbitMotion();
            motion.BeginDrag(0, 0, 0); motion.MoveDrag(100, 35, .1); motion.EndDrag(.11); motion.Step(.15);
            check(motion.Yaw < 0 && motion.Pitch > 0, "Right/down dragging retains the desktop camera directions and inertia.");
            var rotated = Capture(renderer, config with { YawRadians = (float)motion.Yaw, PitchRadians = (float)motion.Pitch }, settledFrame, check);
            check(Changed(first.Pixels, rotated.Pixels) > 500, "Drag and inertial camera updates change actual rendered model pixels.");
            Save(Path.Combine(output, stem + "-rotated.png"), rotated);
            var dark = Capture(renderer, config with { Width = 400, Height = 300, YawRadians = .65f,
                ClearRed = 32f / 255, ClearGreen = 32f / 255, ClearBlue = 32f / 255 }, rotated.Frame.FrameId, check);
            check(dark.Frame.Width == 400 && dark.Frame.Height == 300 && dark.Frame.Generation >= rotated.Frame.Generation,
                "A physical-size change publishes a matching frame and a current generation.");
            Save(Path.Combine(output, stem + "-dark-resized.png"), dark);
            var portrait = Capture(renderer, config with { Width = 190, Height = 340, YawRadians = .65f }, dark.Frame.FrameId, check);
            check(portrait.Left > 0 && portrait.Top > 0 && portrait.Right < portrait.Frame.Width - 1 && portrait.Bottom < portrait.Frame.Height - 1,
                "Native aspect-aware camera fitting keeps the full model inside a narrow portrait frame after downsizing.");
            // An unrelated metadata change preserves geometry but must lose the exact-SHA material exemption.
            var unknownBytes = bytes.ToArray();
            int generator = unknownBytes.AsSpan().IndexOf("Adobe"u8);
            check(generator >= 20, "The fixture has a harmless generator metadata field for unknown-SHA regression.");
            unknownBytes[generator] = (byte)'Z';
            var unknown = FilamentModelData.CreateValidated(unknownBytes);
            check(unknown.MaterialProfile == "authored", "An unknown model with the same material name does not inherit the correction.");
            using (var separate = NativeFilamentRendererFactory.Instance.Create(backend))
            {
                separate.LoadModel(unknown);
                var unknownShot = Capture(separate, config, 0, check);
                var unknownDiagnostics = separate.GetDiagnostics();
                check(unknownDiagnostics.PatchedMaterials == 0 && unknownDiagnostics.SourceTriangles == source.Triangles,
                    "An independent native scene preserves unknown transparency and the complete geometry.");
                check(renderer.GetDiagnostics().PatchedMaterials == 1 && renderer.GetDiagnostics().SourceTriangles == source.Triangles,
                    "Loading another renderer cannot mutate the first renderer's materials or geometry.");
            }
            check(source.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                "Rendering, material profiles and camera changes never rewrite original model bytes.");
            receipts.Add(new { source, diagnostics, first.Frame, first.Foreground, first.InteriorPixels,
                first.BrightnessP10, first.BrightnessP90, first.ClippedWhiteFraction, resized = dark.Frame, portrait = portrait.Frame,
                frontPng = stem + "-front.png", rotatedPng = stem + "-rotated.png", darkPng = stem + "-dark-resized.png" });
            renderer.Dispose();
            bool disposed = false;
            try { renderer.RequestFrame(); } catch (ObjectDisposedException) { disposed = true; }
            check(disposed, "A disposed renderer rejects further frame work and repeated disposal is harmless.");
        }
        var nativeModule = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(module =>
            module.ModuleName.Contains("HeyMelody.Filament.Native", StringComparison.OrdinalIgnoreCase));
        check(nativeModule is not null, "The real native bridge is loaded in this process.");
        return new { models = receipts, requestedBackend = backend.ToString(), nativeLibrary = nativeModule!.FileName,
            nativeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativeModule.FileName))).ToLowerInvariant(),
            scope = "Windows x64 real offscreen Filament; original geometry, material allowlist, camera, RGBA readback, resize and disposal. UI integration is a separate native demo check." };
    }

    private static Shot Capture(IFilamentRenderer renderer, FilamentRenderConfiguration configuration,
        ulong afterFrame, Action<bool, string> check)
    {
        renderer.Configure(configuration);
        var buffer = new byte[checked(configuration.Width * configuration.Height * 4)];
        var clock = Stopwatch.StartNew();
        FilamentFrameInfo frame = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(35))
        {
            renderer.RequestFrame();
            var diagnostics = renderer.GetDiagnostics();
            if (diagnostics.State == FilamentRendererState.Failed) throw new InvalidOperationException("Native renderer entered a failed state.");
            if (renderer.TryCopyLatestFrame(afterFrame, buffer, out frame) && frame.Width == configuration.Width && frame.Height == configuration.Height) break;
            Thread.Sleep(25);
        }
        check(frame.FrameId > afterFrame && frame.Width == configuration.Width && frame.Height == configuration.Height
            && frame.RowBytes == configuration.Width * 4 && frame.RenderMilliseconds >= 0 && frame.ReadbackMilliseconds >= 0,
            "A completed native frame has current dimensions, tightly packed RGBA rows and nonnegative timings.");
        byte red = (byte)Math.Round(configuration.ClearRed * 255), green = (byte)Math.Round(configuration.ClearGreen * 255), blue = (byte)Math.Round(configuration.ClearBlue * 255);
        check(Math.Abs(buffer[0] - red) <= 3 && Math.Abs(buffer[1] - green) <= 3 && Math.Abs(buffer[2] - blue) <= 3,
            $"RGBA readback preserves distinct background channels; actual={buffer[0]},{buffer[1]},{buffer[2]},{buffer[3]}, expected={red},{green},{blue},255.");
        int foreground = 0, left = frame.Width, top = frame.Height, right = -1, bottom = -1;
        var mask = new bool[frame.Width * frame.Height];
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            int offset = (y * frame.Width + x) * 4;
            if (buffer[offset + 3] != 255) throw new InvalidOperationException("Native frames must have opaque alpha.");
            if (Math.Abs(buffer[offset] - red) <= 10 && Math.Abs(buffer[offset + 1] - green) <= 10 && Math.Abs(buffer[offset + 2] - blue) <= 10) continue;
            foreground++; left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            mask[y * frame.Width + x] = true;
        }
        check(foreground > 1000, "The native image contains substantial model pixels rather than only the background.");
        var brightness = new List<double>(foreground);
        int clipped = 0;
        for (int y = 1; y < frame.Height - 1; y++)
        for (int x = 1; x < frame.Width - 1; x++)
        {
            int pixel = y * frame.Width + x;
            if (!mask[pixel] || !mask[pixel - 1] || !mask[pixel + 1] || !mask[pixel - frame.Width] || !mask[pixel + frame.Width]) continue;
            int offset = pixel * 4;
            brightness.Add(.2126 * buffer[offset] + .7152 * buffer[offset + 1] + .0722 * buffer[offset + 2]);
            if (buffer[offset] >= 250 && buffer[offset + 1] >= 250 && buffer[offset + 2] >= 250) clipped++;
        }
        brightness.Sort();
        double p10 = brightness.Count == 0 ? 0 : brightness[(int)((brightness.Count - 1) * .1)];
        double p90 = brightness.Count == 0 ? 0 : brightness[(int)((brightness.Count - 1) * .9)];
        return new(buffer, frame, foreground, left, top, right, bottom, brightness.Count, p10, p90,
            brightness.Count == 0 ? 0 : (double)clipped / brightness.Count);
    }
    private static int Changed(byte[] first, byte[] second)
    {
        int count = 0;
        for (int index = 0; index < Math.Min(first.Length, second.Length); index += 4)
            if (Math.Abs(first[index] - second[index]) + Math.Abs(first[index + 1] - second[index + 1]) + Math.Abs(first[index + 2] - second[index + 2]) > 15) count++;
        return count;
    }
    private static void Save(string path, Shot shot)
    {
        // The direct engine test deliberately initializes no Avalonia window or render service.
        using var bitmap = new SKBitmap(shot.Frame.Width, shot.Frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int row = 0; row < shot.Frame.Height; row++)
            Marshal.Copy(shot.Pixels, row * shot.Frame.RowBytes, bitmap.GetPixels() + row * bitmap.RowBytes, shot.Frame.Width * 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = new FileStream(path, FileMode.CreateNew);
        png.SaveTo(output);
    }
}
