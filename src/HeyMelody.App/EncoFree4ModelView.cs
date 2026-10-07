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
public sealed class EncoFree4ModelView : UserControl
{
    public const string ModelSha256 = "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc";
    private const int ModelByteLength = 9_727_500;
    private static readonly Lazy<Task<byte[]>> ModelBytes = new(ReadModelBytesAsync);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 30) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock _status = new()
    {
        Text = "正在加载 Enco Free4 模型…", IsHitTestVisible = false,
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

    public EncoFree4ModelView()
    {
        ClipToBounds = true;
        Content = new Grid { Children = { Viewport, _status } };
        AutomationProperties.SetName(this, "Enco Free4 三维模型，可用鼠标拖动旋转");
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
    public static async Task<GltfModel> LoadModelAsync()
    {
        var bytes = await ModelBytes.Value.ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var model = GltfLoader.LoadModel(bytes, "Enco Free4");
            // This verified OEM asset marks its solid material as BLEND, although
            // its RGB base-color map and factor are fully opaque. Alpha rendering
            // disables depth writes, letting internal parts overwrite the shell.
            // Correct only this model's material; retain the original GLB and culling.
            var material = model.Materials["1001"];
            material.Blend = BlendMode.Opaque;
            material.DepthWrite = true;
            return model;
        }).ConfigureAwait(false);
    }

    private static Task<byte[]> ReadModelBytesAsync() => Task.Run(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://HeyMelody/Assets/Devices/EncoFree4/model.glb"));
        var bytes = new byte[ModelByteLength];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Enco Free4 model integrity check failed.");
        return bytes;
    });

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
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
            var model = await LoadModelAsync();
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
            _status.Text = "Enco Free4 模型加载失败。\n请查看验证日志。";
            _status.IsVisible = true;
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
