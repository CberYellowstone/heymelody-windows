using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Ava3D;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

/// <summary>Experimental artwork viewport. Rendering and glTF loading belong to Ava3DControl.</summary>
public class DeviceModelView : UserControl
{
    // Display frames can arrive slightly early; a capped rate still draws on every second (60) or fourth (30) one.
    private const double FrameTolerance = .002;
    private const int VerifiedModelCacheCapacity = 2;
    private static readonly LinkedList<(string Sha256, byte[] Bytes)> VerifiedModels = new();
    private readonly DeviceModelVisual _visual;
    private IDisposable? _resourceLease;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock _diagnostics = new()
    {
        IsHitTestVisible = false, IsVisible = false, FontSize = 12, Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock _status = new()
    {
        Text = "正在加载耳机模型…", IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16)
    };
    private Window? _window;
    private IPointer? _pointer;
    private GltfModel? _model;
    private SoftwareModelMaterials? _materials;
    private bool? _softwareProfile;
    private bool _materialsPending;
    private bool _attached;
    private int _loadGeneration;
    private TopLevel? _frameSource;
    private bool _loopActive;
    private int _loopGeneration;
    private TimeSpan? _lastFrame;
    private double _sinceCamera;
    private float _appliedYaw = float.NaN, _appliedPitch = float.NaN;

    public Ava3DView Viewport { get; } = new()
    {
        // The constructor applies an explicit saved software choice before attachment.
        PreferredBackend = RenderBackendKind.Automatic,
        AutoFit = false,
        IsOrbitEnabled = false, IsZoomEnabled = false, IsPanEnabled = false,
        IsPickingEnabled = false, IsAnimationEnabled = false,
        // Frames are requested by the vsync-aligned loop below; MaxFrameRate 0 leaves pacing to the display.
        RenderTrigger = RenderTrigger.OnDemand,
        MaxFrameRate = 0, SampleCount = 4
    };
    public ModelOrbitMotion Motion { get; } = new();
    public bool ModelReady { get; private set; }
    public string? LoadError { get; private set; }
    public long CameraUpdateCount { get; private set; }
    /// <summary>Whether the per-frame rotation loop is running (visible, attached and not minimised).</summary>
    public bool AnimationActive => _loopActive;

    public DeviceModelView(DeviceModelVisual visual)
    {
        _visual = visual;
        ApplyRenderingPreference();
        ClipToBounds = true;
        Content = new Grid { Children = { Viewport, _status, _diagnostics } };
        AutomationProperties.SetName(this, "耳机三维模型，可用鼠标拖动旋转");
        AutomationProperties.SetAccessibilityView(_diagnostics, AccessibilityView.Raw);
        Viewport.SceneReady += (_, _) =>
        {
            if (Viewport.Scene is null) return;
            ModelReady = true;
            _status.IsVisible = false;
            FitCamera();
        };
        Viewport.PropertyChanged += (_, change) =>
        {
            if (change.Property == BoundsProperty) FitCamera();
            if (change.Property != Ava3DView.InfoProperty) return;
            ApplySceneRenderingProfile();
            UpdateDiagnostics();
        };
        Viewport.PointerPressed += OnPointerPressed;
        Viewport.PointerMoved += OnPointerMoved;
        // Finish our gesture before the viewport's class handler releases capture.
        Viewport.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Viewport.PointerCaptureLost += (_, _) =>
        {
            if (_pointer is null) return;
            _pointer = null;
            Motion.CancelDrag();
        };
        ActualThemeVariantChanged += (_, _) => ApplyBackground();
        ApplyBackground();
    }

    /// <summary>Validates the bundled original GLB and parses a fresh scene off the UI thread.</summary>
    internal static async Task<GltfModel> LoadModelAsync(DeviceModelVisual visual)
    {
        return await Task.Run(() =>
        {
            var (bytes, hash) = ReadVerifiedModel(visual.File);
            var model = GltfLoader.LoadModel(bytes, "Earbuds");
            // This verified OEM asset marks its solid material as BLEND, although
            // its RGB base-color map and factor are fully opaque. Alpha rendering
            // disables depth writes, letting internal parts overwrite the shell.
            // Correct only this model's material; retain the original GLB and culling.
            if (hash == EncoFree4ModelView.ModelSha256 || hash == EncoFree4DynaudioModelView.ModelSha256
                || hash == "6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363")
            {
                var material = model.Materials["1001"];
                material.Blend = BlendMode.Opaque;
                material.DepthWrite = true;
            }
            return model;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and verifies a model file. Re-attaching after a device or layout switch reuses the last two verified files
    /// instead of reading and hashing up to 64 MB again; every view still parses its own independent scene.
    /// </summary>
    private static (byte[] Bytes, string Sha256) ReadVerifiedModel(ArtworkFile file)
    {
        var expected = file.Sha256?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(expected))
            lock (VerifiedModels)
                for (var node = VerifiedModels.First; node is not null; node = node.Next)
                    if (node.Value.Sha256 == expected)
                    {
                        VerifiedModels.Remove(node);
                        VerifiedModels.AddFirst(node);
                        return (node.Value.Bytes, expected);
                    }
        using var stream = file.Open();
        if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Model size exceeds its limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrEmpty(expected) && hash != expected) throw new InvalidDataException("Model integrity check failed.");
        if (!string.IsNullOrEmpty(expected))
            lock (VerifiedModels)
            {
                VerifiedModels.AddFirst((hash, bytes));
                while (VerifiedModels.Count > VerifiedModelCacheCapacity) VerifiedModels.RemoveLast();
            }
        return (bytes, hash);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ApplyRenderingPreference();
        ModelRenderingPreferences.Changed += OnRenderingPreferenceChanged;
        RenderDiagnostics.Changed += OnDiagnosticsChanged;
        UpdateDiagnostics();
        base.OnAttachedToVisualTree(e);
        _attached = true;
        try { _resourceLease = _visual.File.Acquire(); }
        catch (ObjectDisposedException)
        {
            // The catalog replaced this package; its successor arrives with the next visual.
            Content = DeviceVisuals.DefaultTemplate.Build(_visual.Fallback);
            return;
        }
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowPropertyChanged;
        RefreshActivity();
        if (_model is null) _ = LoadAsync(++_loadGeneration);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ModelRenderingPreferences.Changed -= OnRenderingPreferenceChanged;
        RenderDiagnostics.Changed -= OnDiagnosticsChanged;
        _attached = false;
        ++_loadGeneration;
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        ReleasePointer();
        RefreshActivity();
        _model = null;
        _materials = null;
        _materialsPending = false;
        _softwareProfile = null;
        Viewport.Scene = null;
        ModelReady = false;
        Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) RefreshActivity();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The hero's tier limits (up to the wide tier) bound the actual size; this only stops unbounded growth.
        var requested = new Size(Math.Min(UiMetrics.WideHeroMaxWidth, availableSize.Width), Math.Min(UiMetrics.WideHeroMaxHeight, availableSize.Height));
        base.MeasureOverride(requested);
        return requested;
    }

    private async Task LoadAsync(int generation)
    {
        try
        {
            using var loadingLease = _visual.File.Acquire();
            var model = await LoadModelAsync(_visual);
            if (!_attached || generation != _loadGeneration) return;
            _model = model;
            // Software materials decode and sample every map; they are built only if the CPU renderer is used.
            _materials = null;
            _materialsPending = false;
            _softwareProfile = null;
            ApplySceneRenderingProfile();
            model.Scene.ShadowMapSize = 1024;
            Viewport.Scene = model.Scene;
            ApplyBackground();
            FitCamera();
            RefreshActivity();
        }
        catch (Exception exception)
        {
            if (!_attached || generation != _loadGeneration) return;
            LoadError = exception.ToString();
            Content = DeviceVisuals.DefaultTemplate.Build(_visual.Fallback);
            Trace.WriteLine(LoadError);
        }
    }

    private void ApplyBackground()
    {
        var color = ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);
        Viewport.ClearColor = color;
        if (_model is not null)
        {
            _model.Scene.Background = color;
            Viewport.InvalidateScene();
        }
    }

    private void OnRenderingPreferenceChanged(object? sender, EventArgs e) => ApplyRenderingPreference();

    private void ApplyRenderingPreference()
    {
        Viewport.PreferredBackend = ModelRenderingPreferences.UseGpuAcceleration
            ? RenderBackendKind.Automatic : RenderBackendKind.Software;
        ApplySceneRenderingProfile();
    }

    private void ApplySceneRenderingProfile()
    {
        if (_model is null) return;
        // Automatic can also fall back to software. Ignore stale diagnostics while
        // changing the requested backend, then follow the actual renderer once known.
        var info = Viewport.Info;
        var software = Viewport.PreferredBackend == RenderBackendKind.Software
            || (info.FramesRendered > 0 && info.PreferredBackend == Viewport.PreferredBackend
                && info.ActiveBackend == RenderBackendKind.Software);
        if (_softwareProfile == software) return;
        if (software && _materials is null) { BuildSoftwareMaterials(_model); return; }
        _softwareProfile = software;
        _materials?.Apply(software);
        var scene = _model.Scene;
        // CPU lighting is tone-mapped before its base-colour map is multiplied in.
        // A bright uniform environment clips that lighting and erases curvature.
        scene.Environment = EnvironmentLight.Studio(software ? .06f : 1.2f);
        scene.Light.Direction = Vector3.Normalize(software ? new Vector3(-4, -2, -1) : new Vector3(-1, -2, -3));
        scene.Light.Intensity = software ? 1.5f : 2;
        scene.Light.Ambient = software ? .02f : .15f;
        Viewport.InvalidateScene();
    }

    private void BuildSoftwareMaterials(GltfModel model)
    {
        if (_materialsPending) return;
        _materialsPending = true;
        var generation = _loadGeneration;
        _ = Task.Run(() => new SoftwareModelMaterials(model)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _loadGeneration || !ReferenceEquals(model, _model)) return;
            _materialsPending = false;
            if (task.IsCompletedSuccessfully) _materials = task.Result;
            else Trace.WriteLine(task.Exception);
            // Without software materials the scene keeps its original maps, which the CPU renderer can still draw.
            if (_materials is null) _softwareProfile = null;
            ApplySceneRenderingProfile();
        }), TaskScheduler.Default);
    }

    private void FitCamera()
    {
        var size = Viewport.Bounds.Size;
        if (_model is null || size.Width <= 0 || size.Height <= 0) return;
        // Fit's second argument is a margin, not an aspect ratio. Keep wide compact
        // layouts at the same scale; only narrow views need extra horizontal clearance.
        var margin = 1.08f * Math.Max(1, (float)(size.Height / size.Width));
        Viewport.Camera.Fit(_model.Root.WorldBounds, margin: margin);
        _appliedYaw = _appliedPitch = float.NaN;
        ApplyCamera();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) RefreshActivity();
    }

    private void RefreshActivity()
    {
        var active = _attached && IsVisible && _window?.IsVisible == true
            && _window.WindowState != WindowState.Minimized && _model is not null;
        Motion.SetActive(active);
        if (!active)
        {
            StopFrameLoop();
            ReleasePointer();
            return;
        }
        if (_loopActive || TopLevel.GetTopLevel(this) is not { } source) return;
        _frameSource = source;
        _loopActive = true;
        _lastFrame = null;
        _sinceCamera = double.MaxValue;
        RequestFrame(++_loopGeneration);
    }

    private void StopFrameLoop()
    {
        _loopActive = false;
        _frameSource = null;
        ++_loopGeneration;
    }

    // The callback runs once per composited frame, at the display's refresh rate, unlike a coarse Win32 timer.
    private void RequestFrame(int generation) => _frameSource?.RequestAnimationFrame(time => OnFrame(time, generation));

    private void OnFrame(TimeSpan time, int generation)
    {
        if (generation != _loopGeneration || !_loopActive) return;
        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, .1) : 0;
        _lastFrame = time;
        Motion.Step(dt);
        _sinceCamera += dt;
        var interactive = Motion.Phase is ModelOrbitPhase.Dragging or ModelOrbitPhase.Inertia;
        var interval = FrameInterval(ModelRenderingPreferences.FrameRate, _softwareProfile == true, interactive,
            Viewport.Info.RenderMilliseconds);
        if (_sinceCamera + FrameTolerance >= interval)
        {
            _sinceCamera = Math.Clamp(_sinceCamera - interval, 0, interval);
            ApplyCamera();
        }
        RequestFrame(generation);
    }

    /// <summary>Minimum seconds between camera updates; 0 draws on every display frame.</summary>
    /// <remarks>
    /// The CPU renderer draws inside the window's frame and manages about 55 fps on a 16-core desktop for a far larger
    /// scene, so it keeps the earlier 30 fps idle rotation and at most 60 fps while dragging, slower if a frame takes longer.
    /// </remarks>
    internal static double FrameInterval(ModelFrameRate preference, bool software, bool interactive, double renderMilliseconds)
    {
        var interval = preference switch { ModelFrameRate.Fps60 => 1d / 60, ModelFrameRate.Fps30 => 1d / 30, _ => 0 };
        if (!software) return interval;
        var softwareInterval = interactive ? Math.Max(1d / 60, 1.3 * Math.Max(0, renderMilliseconds) / 1000) : 1d / 30;
        return Math.Max(interval, softwareInterval);
    }

    private void ApplyCamera()
    {
        if (_model is null) return;
        var yaw = (float)Motion.Yaw;
        var pitch = (float)Motion.Pitch;
        if (yaw == _appliedYaw && pitch == _appliedPitch) return;
        _appliedYaw = yaw;
        _appliedPitch = pitch;
        Viewport.Camera.Yaw = yaw;
        Viewport.Camera.Pitch = pitch;
        Viewport.InvalidateCamera();
        ++CameraUpdateCount;
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs e) => UpdateDiagnostics();

    private void UpdateDiagnostics()
    {
        _diagnostics.IsVisible = RenderDiagnostics.Enabled;
        if (!RenderDiagnostics.Enabled) return;
        var info = Viewport.Info;
        _diagnostics.Text = $"3D {info.FramesPerSecond:0} fps · P95 {info.P95FrameMilliseconds:0.0} ms · 渲染 {info.RenderMilliseconds:0.0} ms"
            + $" · {info.ActiveBackend} · MSAA {info.SampleCount}× · 延迟帧 {info.LateFrames}";
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ModelReady || !Motion.IsActive || !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(Viewport);
        _pointer = e.Pointer;
        Motion.BeginDrag(point.X, point.Y, _clock.Elapsed.TotalSeconds);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer != e.Pointer) return;
        var point = e.GetPosition(Viewport);
        // The frame loop applies the camera once per display frame, merging high-rate mouse input.
        Motion.MoveDrag(point.X, point.Y, _clock.Elapsed.TotalSeconds);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointer != e.Pointer) return;
        Motion.EndDrag(_clock.Elapsed.TotalSeconds);
        ReleasePointer();
        e.Handled = true;
    }

    private void ReleasePointer()
    {
        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        Motion.CancelDrag();
    }
}

/// <summary>Compatibility viewport for the separately selectable original Free4 preview.</summary>
public sealed class EncoFree4ModelView : DeviceModelView
{
    public const string ModelSha256 = "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc";
    private static readonly DeviceModelVisual Bundled = new(new ArtworkFile(
        new Uri("avares://HeyMelody/Assets/Devices/EncoFree4/model.glb"), "", ModelSha256), DeviceVisuals.GenericProduct);
    public EncoFree4ModelView() : base(Bundled) { }
    public static Task<GltfModel> LoadModelAsync() => DeviceModelView.LoadModelAsync(Bundled);
}
