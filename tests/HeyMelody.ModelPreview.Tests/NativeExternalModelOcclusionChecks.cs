using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Ava3D;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using HeyMelodyWindows.App;

/// <summary>Explicit local-file GPU acceptance for the audited original Free4 and Enco X3 GLBs.</summary>
internal static class NativeExternalModelOcclusionChecks
{
    private static State _state = null!;
    private sealed record AuditedSource(string Name, string Path, string Sha256, byte[] Bytes,
        int Meshes, int Nodes, int Triangles, int Textures);
    private sealed class State(AuditedSource source, string output, Action<bool, string> check)
    {
        internal AuditedSource Source = source;
        internal string Output = output;
        internal Action<bool, string> Check = check;
        internal List<object> Receipts = [];
        internal bool Closed;
        internal Exception? Failure;
        internal DeviceModelVisual Visual = new(new ArtworkFile(new Uri(source.Path),
            System.IO.Path.GetDirectoryName(source.Path)!, source.Sha256), DeviceVisuals.GenericProduct);
    }

    internal static object Run(string modelFile, string output, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("External model GPU capture requires Windows.");
        _state = new(ReadAuditedSource(modelFile, check), output, check);
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            var oldPreference = ModelRenderingPreferences.UseGpuAcceleration;
            ThemePreferences.SettingsPathOverride = Path.Combine(output, "unwritten-preferences.json");
            ModelRenderingPreferences.Reload();
            ModelRenderingPreferences.Apply(true);
            try
            {
                AppBuilder.Configure<OcclusionApplication>().UsePlatformDetect().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error; }
            finally { ThemePreferences.SettingsPathOverride = oldPath; ModelRenderingPreferences.Apply(oldPreference); }
        }) { IsBackground = true, Name = "Audited original-file GPU occlusion verification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(85))) throw new TimeoutException("External model GPU verification did not close within 85 seconds.");
        var result = new
        {
            success = _state.Failure is null, _state.Closed,
            modelName = _state.Source.Name, modelSha256 = _state.Source.Sha256,
            modelBytes = _state.Source.Bytes.Length, sourceMeshes = _state.Source.Meshes, sourceNodes = _state.Source.Nodes,
            sourceTriangles = _state.Source.Triangles, sourceTextures = _state.Source.Textures,
            nativeBluetoothCalls = 0, states = _state.Receipts,
            scope = "Explicit local audited original GLB; mandatory actual Windows GPU SDK frame capture through the app loader and renderer. Additional Win32 desktop screenshots are optional, with failures recorded as warnings. No model network fetch, Bluetooth calls, asset changes or user preferences writes. CPU and physical-device behavior are not verified.",
            failure = _state.Failure?.ToString()
        };
        File.WriteAllText(Path.Combine(output, "native-external-occlusion-receipts.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (_state.Failure is not null) throw new InvalidOperationException("External model GPU verification failed.", _state.Failure);
        check(_state.Closed, "External model GPU verification window closes normally");
        check(!File.Exists(Path.Combine(output, "unwritten-preferences.json")), "External model capture never writes user preferences");
        return result;
    }

    public sealed class OcclusionApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var model = new DeviceModelView(_state.Visual);
            _state.Check(model.Viewport.PreferredBackend == RenderBackendKind.Automatic,
                "External model selects automatic GPU rendering before visual-tree attachment");
            var window = new Window
            {
                Title = _state.Source.Name + " — isolated GPU occlusion verification",
                Width = 1000, Height = 700, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(243, 243, 243)),
                Content = new Border { Padding = new Thickness(16), Child = model }
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            var started = false;
            window.Opened += (_, _) =>
            {
                if (started) return;
                started = true;
                _ = VerifyAsync(window, model, desktop);
            };
            _ = WatchdogAsync(window, desktop);
        }
    }

    private static async Task VerifyAsync(Window window, DeviceModelView model,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (model.LoadError is null && (!model.ModelReady || model.Viewport.Info.FramesRendered == 0
                || model.Viewport.Info.Triangles == 0))
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("External model did not render.");
                await Task.Delay(20);
            }
            _state.Check(model.LoadError is null, "Audited original file loads through the application's actual model view");
            var handle = window.TryGetPlatformHandle();
            _state.Check(handle is not null && handle.HandleDescriptor == "HWND" && handle.Handle != IntPtr.Zero,
                "External model acceptance runs in a real Win32 window");
            _state.Check(model.Viewport.Info.ActiveBackend is not RenderBackendKind.Software and not RenderBackendKind.Automatic,
                "The actual model view renders with a GPU backend without software fallback");
            _state.Check(model.Viewport.Info.SceneItems == _state.Source.Meshes
                && model.Viewport.Scene!.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0) == _state.Source.Triangles,
                $"The actual app view retains all {_state.Source.Meshes} meshes and {_state.Source.Triangles:N0} source triangles");
            _state.Check(string.IsNullOrEmpty(model.Viewport.Info.Error), "The actual model view reports no renderer error");
            var raw = await Task.Run(() => GltfLoader.LoadModel(_state.Source.Bytes, _state.Source.Name + " original alpha scene"));
            var corrected = await DeviceModelView.LoadModelAsync(_state.Visual);
            _state.Check(raw.Scene.EnumerateMeshes().Count() == _state.Source.Meshes && corrected.Scene.EnumerateMeshes().Count() == _state.Source.Meshes
                && raw.Scene.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0) == _state.Source.Triangles
                && corrected.Scene.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0) == _state.Source.Triangles,
                "Raw and corrected loaders retain the complete audited source geometry");
            var before = raw.Materials["1001"];
            var after = corrected.Materials["1001"];
            _state.Check(before.Metallic == after.Metallic && before.Roughness == after.Roughness
                && before.EmissiveColor == after.EmissiveColor
                && SameTexture(before.BaseColorTexture, after.BaseColorTexture)
                && SameTexture(before.MetallicRoughnessTexture, after.MetallicRoughnessTexture)
                && SameTexture(before.EmissiveTexture, after.EmissiveTexture),
                "The correction preserves authored PBR factors and texture bindings");
            await NativeModelOcclusionChecks.RunAsync(window, model, raw, corrected, _state.Output,
                _state.Check, _state.Receipts, _state.Source.Name, _state.Source.Sha256, _state.Source.Meshes,
                requireGpu: true, windowCaptureRequired: false);
        }
        catch (Exception error) { _state.Failure = error; }
        finally { window.Close(); desktop.Shutdown(); }
    }

    private static bool SameTexture(Texture? first, Texture? second) => first is null ? second is null
        : second is not null && first.Width == second.Width && first.Height == second.Height
            && first.Format == second.Format && first.ColorSpace == second.ColorSpace
            && first.WrapU == second.WrapU && first.WrapV == second.WrapV && first.Filter == second.Filter
            && SameBytes(first.Encoded, second.Encoded) && SameBytes(first.Pixels, second.Pixels);

    private static bool SameBytes(byte[]? first, byte[]? second) => first is null ? second is null
        : second is not null && first.AsSpan().SequenceEqual(second);

    private static async Task WatchdogAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(75));
        if (_state.Closed) return;
        _state.Failure ??= new TimeoutException("External model GPU verification exceeded its watchdog.");
        window.Close();
        desktop.Shutdown();
    }

    private static AuditedSource ReadAuditedSource(string path, Action<bool, string> check)
    {
        using var stream = File.OpenRead(path);
        check(stream.Length is 10_835_448 or 10_508_032 or 9_727_500, "External GLB has an audited original source size");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var audit = hash switch
        {
            ModelMaterialCorrections.EncoX3BlackSha256 when bytes.Length == 10_835_448
                => (Name: "Enco X3 black", Meshes: 31, Nodes: 39, Triangles: 98_367),
            ModelMaterialCorrections.EncoX3WhiteSha256 when bytes.Length == 10_508_032
                => (Name: "Enco X3 white", Meshes: 31, Nodes: 39, Triangles: 98_367),
            EncoFree4ModelView.ModelSha256 when bytes.Length == 9_727_500
                => (Name: "Enco Free4", Meshes: 28, Nodes: 67, Triangles: 93_875),
            _ => throw new InvalidDataException("--model-file accepts only the unchanged audited Free4 or black/white Enco X3 GLB.")
        };
        check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == 0x46546c67
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) == 2
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) == bytes.Length,
            "External source remains the complete original glTF 2.0 binary");
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var source = json.RootElement;
        check(source.GetProperty("meshes").GetArrayLength() == audit.Meshes && source.GetProperty("nodes").GetArrayLength() == audit.Nodes,
            $"Audited original source retains {audit.Meshes} meshes and {audit.Nodes} nodes");
        var materials = source.GetProperty("materials");
        check(materials.GetArrayLength() == 1 && materials[0].GetProperty("name").GetString() == "1001"
            && materials[0].GetProperty("alphaMode").GetString() == "BLEND",
            "Audited original source declares its sole shared material as BLEND");
        return new(audit.Name, path, hash, bytes, audit.Meshes, audit.Nodes, audit.Triangles,
            source.GetProperty("textures").GetArrayLength());
    }
}
