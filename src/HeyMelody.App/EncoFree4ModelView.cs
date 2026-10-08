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

namespace HeyMelodyWindows.App;

/// <summary>Experimental artwork viewport. Rendering and glTF loading belong to Ava3DControl.</summary>
public class DeviceModelView : UserControl
{
    private readonly DeviceModelVisual _visual;
    private IDisposable? _resourceLease;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 30) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
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
    private bool _attached;
    private int _loadGeneration;
    private double _lastTick;

    public Ava3DView Viewport { get; } = new()
    {
        AutoFit = false,
        IsOrbitEnabled = false, IsZoomEnabled = false, IsPanEnabled = false,
        IsPickingEnabled = false, IsAnimationEnabled = false,
        RenderTrigger = RenderTrigger.OnDemand,
        MaxFrameRate = 30, SampleCount = 4
    };
    public ModelOrbitMotion Motion { get; } = new();
    public bool ModelReady { get; private set; }
    public string? LoadError { get; private set; }
    public long CameraUpdateCount { get; private set; }
    public bool AnimationActive => _timer.IsEnabled;

    public DeviceModelView(DeviceModelVisual visual)
    {
        _visual = visual;
        ClipToBounds = true;
        Content = new Grid { Children = { Viewport, _status } };
        AutomationProperties.SetName(this, "耳机三维模型，可用鼠标拖动旋转");
        _timer.Tick += OnTick;
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
            using var stream = visual.File.Open();
            if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Model size exceeds its limit.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.IsNullOrEmpty(visual.File.Sha256) && !StringComparer.OrdinalIgnoreCase.Equals(hash, visual.File.Sha256))
                throw new InvalidDataException("Model integrity check failed.");
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _resourceLease = _visual.File.Acquire();
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowPropertyChanged;
        RefreshActivity();
        if (_model is null) _ = LoadAsync(++_loadGeneration);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ++_loadGeneration;
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        ReleasePointer();
        RefreshActivity();
        _model = null;
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
        var requested = new Size(Math.Min(600, availableSize.Width), Math.Min(520, availableSize.Height));
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
            model.Scene.Environment = EnvironmentLight.Studio(1.2f);
            model.Scene.Light.Direction = Vector3.Normalize(new Vector3(-1, -2, -3));
            model.Scene.Light.Intensity = 2;
            model.Scene.Light.Ambient = .15f;
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

    private void FitCamera()
    {
        var size = Viewport.Bounds.Size;
        if (_model is null || size.Width <= 0 || size.Height <= 0) return;
        // Fit's second argument is a margin, not an aspect ratio. Keep wide compact
        // layouts at the same scale; only narrow views need extra horizontal clearance.
        var margin = 1.08f * Math.Max(1, (float)(size.Height / size.Width));
        Viewport.Camera.Fit(_model.Root.WorldBounds, margin: margin);
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
        _lastTick = _clock.Elapsed.TotalSeconds;
        if (active) _timer.Start();
        else
        {
            _timer.Stop();
            ReleasePointer();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        Motion.Step(Math.Clamp(now - _lastTick, 0, .1));
        _lastTick = now;
        var interactive = Motion.Phase is ModelOrbitPhase.Dragging or ModelOrbitPhase.Inertia;
        var rate = interactive ? 60 : 30;
        _timer.Interval = TimeSpan.FromSeconds(1d / rate);
        Viewport.MaxFrameRate = rate;
        ApplyCamera();
    }

    private void ApplyCamera()
    {
        if (_model is null) return;
        Viewport.Camera.Yaw = (float)Motion.Yaw;
        Viewport.Camera.Pitch = (float)Motion.Pitch;
        Viewport.InvalidateCamera();
        ++CameraUpdateCount;
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
        Motion.MoveDrag(point.X, point.Y, _clock.Elapsed.TotalSeconds);
        ApplyCamera();
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
