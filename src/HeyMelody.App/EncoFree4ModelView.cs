using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

/// <summary>Filament renders the original GLB on its worker; Avalonia owns presentation, input and overlays.</summary>
public class DeviceModelView : UserControl
{
    private const int VerifiedModelCacheCapacity = 2;
    private static readonly LinkedList<FilamentModelData> VerifiedModels = new();
    internal static IFilamentRendererFactory? DefaultFactoryOverride { get; set; }
    private readonly DeviceModelVisual _visual;
    private readonly IFilamentRendererFactory _factory;
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private readonly Grid _root;
    private readonly ContentControl _fallback = new() { IsVisible = false, IsHitTestVisible = false };
    private IDisposable? _resourceLease;
    private bool _resourceAvailable;
    private readonly List<Visual> _visibilityAncestors = new();
    private bool _visibilityRefreshQueued;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock _diagnostics = new()
    {
        IsHitTestVisible = false, IsVisible = false, FontSize = 12, Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock _status = new()
    {
        Text = "正在加载耳机模型…", IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16)
    };
    private Window? _window;
    private IPointer? _pointer;
    private IFilamentRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private byte[]? _frameBuffer;
    private FilamentFrameInfo _lastFrameInfo;
    private FilamentDiagnostics _lastDiagnostics = FilamentDiagnostics.Empty;
    private FilamentRenderConfiguration _configuration;
    private bool _attached;
    private bool _loading;
    private int _loadGeneration;
    private CancellationTokenSource? _loadCancellation;
    private Task _rendererRetirement = Task.CompletedTask;
    private FilamentBackend? _requestedBackend;
    private ModelOrbitMotion.SavedMotion? _motionToRestore;
    private TopLevel? _frameSource;
    private bool _loopActive;
    private int _loopGeneration;
    private TimeSpan? _lastFrame;
    private readonly ModelFramePacer _framePacer = new();
    private float _appliedYaw = float.NaN, _appliedPitch = float.NaN;
    private ulong _afterFrameId;
    private double _lastPresentedAt;
    private readonly Queue<double> _presentationIntervals = new();
    private long _presentedFrames;
    private double _diagnosticsAt;

    public Image Viewport { get; } = new()
    {
        Stretch = Stretch.Fill, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
    };
    public ModelOrbitMotion Motion { get; } = new();
    public bool ModelReady { get; private set; }
    public string? LoadError { get; private set; }
    public long CameraUpdateCount { get; private set; }
    internal long AnimationCallbackCount { get; private set; }
    public bool AnimationActive => _loopActive;
    public FilamentDiagnostics Diagnostics
    {
        get
        {
            try { return _renderer?.GetDiagnostics() ?? _lastDiagnostics; }
            catch (Exception) { return _lastDiagnostics; }
        }
    }

    public DeviceModelView(DeviceModelVisual visual, IFilamentRendererFactory? rendererFactory = null)
    {
        _visual = visual;
        _factory = rendererFactory ?? DefaultFactoryOverride ?? NativeFilamentRendererFactory.Instance;
        ClipToBounds = true;
        _root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _fallback, Viewport, _status, _diagnostics }
        };
        Grid.SetRowSpan(Viewport, 2);
        Grid.SetRowSpan(_diagnostics, 2);
        Content = _root;
        AutomationProperties.SetName(this, "耳机三维模型，可用鼠标拖动旋转");
        AutomationProperties.SetAccessibilityView(_diagnostics, AccessibilityView.Raw);
        Viewport.PropertyChanged += (_, change) => { if (change.Property == BoundsProperty) FitCamera(); };
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.PointerCaptureLost += (_, _) =>
        {
            if (_pointer is null) return;
            _pointer = null;
            Motion.CancelDrag();
        };
        ActualThemeVariantChanged += (_, _) => ApplyBackground();
        ApplyBackground();
        ApplyRenderingPreference();
    }

    /// <summary>Validates the original GLB without loading the native renderer.</summary>
    internal static Task<FilamentModelData> LoadModelAsync(DeviceModelVisual visual)
        => Task.Run(() => ReadVerifiedModel(visual.File));

    private static FilamentModelData ReadVerifiedModel(ArtworkFile file)
    {
        var expected = file.Sha256?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(expected))
            lock (VerifiedModels)
                for (var node = VerifiedModels.First; node is not null; node = node.Next)
                    if (node.Value.Sha256 == expected)
                    {
                        VerifiedModels.Remove(node);
                        VerifiedModels.AddFirst(node);
                        return node.Value;
                    }
        using var stream = file.Open();
        if (stream.Length is <= 0 or > FilamentModelData.MaximumBytes)
            throw new InvalidDataException("Model size exceeds its limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var model = FilamentModelData.CreateValidated(bytes, string.IsNullOrEmpty(expected) ? null : expected);
        if (!string.IsNullOrEmpty(expected))
            lock (VerifiedModels)
            {
                VerifiedModels.AddFirst(model);
                while (VerifiedModels.Count > VerifiedModelCacheCapacity) VerifiedModels.RemoveLast();
            }
        return model;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ModelRenderingPreferences.Changed += OnRenderingPreferenceChanged;
        RenderDiagnostics.Changed += OnDiagnosticsChanged;
        try { _resourceLease = _visual.File.Acquire(); _resourceAvailable = true; }
        catch (ObjectDisposedException) { ShowFallback("三维预览暂不可用"); return; }
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.ScalingChanged += OnScalingChanged;
        }
        foreach (var ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
            _visibilityAncestors.Add(ancestor);
        }
        ApplyRenderingPreference();
        UpdateDiagnostics();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ModelRenderingPreferences.Changed -= OnRenderingPreferenceChanged;
        RenderDiagnostics.Changed -= OnDiagnosticsChanged;
        _attached = false;
        _resourceAvailable = false;
        _motionToRestore = null;
        ++_loadGeneration;
        _loadCancellation?.Cancel();
        _loading = false;
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.ScalingChanged -= OnScalingChanged;
        }
        _window = null;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors.Clear();
        ReleasePointer();
        RetireRenderer();
        Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property.Name == "IsEffectivelyVisible") RefreshActivity();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var requested = new Size(Math.Min(UiMetrics.WideHeroMaxWidth, availableSize.Width),
            Math.Min(UiMetrics.WideHeroMaxHeight, availableSize.Height));
        base.MeasureOverride(requested);
        return requested;
    }

    private async Task LoadAsync(int generation, FilamentBackend backend, CancellationTokenSource cancellation, Task retirement)
    {
        IFilamentRenderer? renderer = null;
        try
        {
            using var loadingLease = _visual.File.Acquire();
            renderer = await Task.Run(async () =>
            {
                // Never create the replacement engine while the old engine is still draining readbacks.
                await retirement.ConfigureAwait(false);
                await _lifetimeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (!IsCurrentLoad(generation)) return null;
                    var model = ReadVerifiedModel(_visual.File);
                    if (!IsCurrentLoad(generation)) return null;
                    var created = _factory.Create(backend, cancellation.Token);
                    try { created.LoadModel(model); }
                    catch { created.Dispose(); throw; }
                    if (IsCurrentLoad(generation)) return created;
                    created.Dispose();
                    return null;
                }
                finally { _lifetimeGate.Release(); }
            });
            if (!IsCurrentLoad(generation) || renderer is null) return;
            _renderer = renderer;
            renderer = null;
            _loading = false;
            LoadError = null;
            ModelReady = true;
            _lastDiagnostics = _renderer.GetDiagnostics();
            _afterFrameId = 0;
            _presentedFrames = 0;
            _lastPresentedAt = 0;
            _presentationIntervals.Clear();
            _fallback.IsVisible = false;
            Grid.SetRow(_status, 0);
            Viewport.IsVisible = true;
            _status.IsVisible = true;
            _status.Text = "正在加载耳机模型…";
            FitCamera();
            RefreshActivity();
            if (_motionToRestore is { } motion)
            {
                Motion.RestoreMotion(motion);
                _motionToRestore = null;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsCurrentLoad(generation)) return;
            _loading = false;
            FailRenderer(exception);
        }
        finally
        {
            if (renderer is not null) QueueRendererDisposal(renderer);
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    // Worker-side checks dispose cancelled loads even when the window's dispatcher has already stopped.
    private bool IsCurrentLoad(int generation) => Volatile.Read(ref _attached)
        && generation == Volatile.Read(ref _loadGeneration);

    private void OnRenderingPreferenceChanged(object? sender, EventArgs e) => ApplyRenderingPreference();
    private void ApplyRenderingPreference()
    {
        if (_framePacer.FrameRate != ModelRenderingPreferences.FrameRate)
            _framePacer.Reset(ModelRenderingPreferences.FrameRate);
        var backend = ModelRenderingPreferences.Backend;
        if (_requestedBackend != backend)
        {
            if (ModelReady && Motion.IsActive) _motionToRestore = Motion.CaptureMotion();
            _requestedBackend = backend;
            ++_loadGeneration;
            _loadCancellation?.Cancel();
            _loading = false;
            RetireRenderer();
            LoadError = null;
        }
        if (_attached && _renderer is null && !_loading && _resourceAvailable)
        {
            _loading = true;
            _fallback.IsVisible = false;
            Grid.SetRow(_status, 0);
            _status.Text = "正在加载耳机模型…";
            _status.IsVisible = true;
            var cancellation = _loadCancellation = new CancellationTokenSource();
            _ = LoadAsync(++_loadGeneration, backend, cancellation, _rendererRetirement);
        }
        RefreshActivity();
    }

    private void ShowFallback(string? status)
    {
        // Reserve a separate footer for failure guidance so it cannot cover the static artwork.
        Grid.SetRow(_status, 1);
        _fallback.Content ??= DeviceVisuals.DefaultTemplate.Build(_visual.Fallback) ?? DeviceVisuals.DefaultTemplate.Build(DeviceVisuals.GenericProduct);
        _fallback.IsVisible = true;
        Viewport.IsVisible = false;
        _status.Text = status;
        _status.IsVisible = status is not null;
        ModelReady = false;
        RefreshActivity();
    }

    private void RetireRenderer()
    {
        var retired = _renderer;
        _renderer = null;
        ModelReady = false;
        RefreshActivity();
        Viewport.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _frameBuffer = null;
        _lastFrameInfo = default;
        if (retired is not null) QueueRendererDisposal(retired);
    }

    private void QueueRendererDisposal(IFilamentRenderer renderer)
    {
        var previous = _rendererRetirement;
        _rendererRetirement = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await DisposeRendererAsync(renderer).ConfigureAwait(false);
        });
    }

    private Task DisposeRendererAsync(IFilamentRenderer renderer) => Task.Run(async () =>
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try { renderer.Dispose(); }
        catch (Exception exception) { Trace.WriteLine(exception); }
        finally { _lifetimeGate.Release(); }
    });

    private void FailRenderer(Exception exception)
    {
        _motionToRestore = null;
        LoadError = exception.ToString();
        Trace.WriteLine(LoadError);
        RetireRenderer();
        var message = exception switch
        {
            FilamentRendererException { Kind: FilamentFailureKind.BackendUnavailable, Backend: FilamentBackend.Vulkan }
                or FilamentRendererException { Kind: FilamentFailureKind.RuntimeFailure, Stage: FilamentFailureStage.Rendering,
                    Backend: FilamentBackend.Vulkan }
                => "Vulkan 三维预览不可用，可在应用设置的“3D模型渲染API”中选择 OpenGL。",
            FilamentRendererException { Kind: FilamentFailureKind.ComponentUnavailable }
                or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException => "三维渲染组件不可用",
            FilamentRendererException { Kind: FilamentFailureKind.InvalidModel } or InvalidDataException => "三维模型资源无效",
            FilamentRendererException { Stage: FilamentFailureStage.ModelLoading } => "三维模型资源加载失败",
            _ => "三维预览暂不可用"
        };
        ShowFallback(message);
    }

    private void ApplyBackground()
    {
        var color = ActualThemeVariant == ThemeVariant.Dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);
        _root.Background = new SolidColorBrush(color);
        _configuration = _configuration with { ClearRed = color.R / 255f, ClearGreen = color.G / 255f, ClearBlue = color.B / 255f };
        ApplyCamera(force: true);
    }

    private void FitCamera()
    {
        var source = TopLevel.GetTopLevel(this);
        var size = Viewport.Bounds.Size;
        // An Image has no intrinsic size until its first frame. The host already has its layout allocation.
        if (size.Width <= 0 || size.Height <= 0) size = _root.Bounds.Size;
        if (_renderer is null || source is null || size.Width <= 0 || size.Height <= 0) return;
        var scaling = source.RenderScaling;
        var width = checked((int)Math.Ceiling(size.Width * scaling));
        var height = checked((int)Math.Ceiling(size.Height * scaling));
        if (width != _configuration.Width || height != _configuration.Height) _lastFrameInfo = default;
        _configuration = _configuration with
        {
            Width = width, Height = height, Zoom = 1, VerticalFovDegrees = 45,
            Exposure = 1, LightIntensity = 100000, MsaaSamples = 4,
            FitMargin = 1.08f
        };
        _appliedYaw = _appliedPitch = float.NaN;
        ApplyCamera(force: true);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) RefreshActivity();
    }
    private void OnScalingChanged(object? sender, EventArgs e) => FitCamera();

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        RefreshActivity();
        if (_visibilityRefreshQueued) return;
        _visibilityRefreshQueued = true;
        // Allow Avalonia to propagate the ancestor visibility before deciding whether to resume.
        Dispatcher.UIThread.Post(() =>
        {
            _visibilityRefreshQueued = false;
            if (_attached) RefreshActivity();
        }, DispatcherPriority.Render);
    }

    private void RefreshActivity()
    {
        bool visible = _attached && IsEffectivelyVisible && _window?.IsVisible == true && _window.WindowState != WindowState.Minimized;
        if (!visible) _motionToRestore = null;
        var active = visible && ModelReady && _renderer is not null;
        Motion.SetActive(active);
        if (!active) { StopFrameLoop(); ReleasePointer(); return; }
        if (_loopActive || TopLevel.GetTopLevel(this) is not { } source) return;
        _frameSource = source;
        _loopActive = true;
        _lastFrame = null;
        _framePacer.Reset(ModelRenderingPreferences.FrameRate);
        RequestFrame(++_loopGeneration);
    }

    private void StopFrameLoop()
    {
        _loopActive = false;
        _frameSource = null;
        _framePacer.Reset(ModelRenderingPreferences.FrameRate);
        ++_loopGeneration;
    }
    private void RequestFrame(int generation) => _frameSource?.RequestAnimationFrame(time => OnFrame(time, generation));
    private void OnFrame(TimeSpan time, int generation)
    {
        if (generation != _loopGeneration || !_loopActive) return;
        if (!IsEffectivelyVisible) { RefreshActivity(); return; }
        ++AnimationCallbackCount;
        try
        {
            var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, .1) : 0;
            _lastFrame = time;
            Motion.Step(dt);
            if (_framePacer.ShouldRender(time)) ApplyCamera();
            PresentLatestFrame();
        }
        catch (Exception exception) { FailRenderer(exception); return; }
        RequestFrame(generation);
    }

    private void ApplyCamera(bool force = false)
    {
        if (_renderer is null || _configuration.Width <= 0 || _configuration.Height <= 0) return;
        var yaw = (float)Motion.Yaw;
        var pitch = (float)Motion.Pitch;
        if (!force && yaw == _appliedYaw && pitch == _appliedPitch) return;
        _appliedYaw = yaw;
        _appliedPitch = pitch;
        _configuration = _configuration with { YawRadians = yaw, PitchRadians = pitch };
        try
        {
            _renderer.Configure(_configuration);
            _renderer.RequestFrame();
            ++CameraUpdateCount;
        }
        catch (Exception exception) { FailRenderer(exception); }
    }

    private void PresentLatestFrame()
    {
        if (_renderer is null || _configuration.Width <= 0 || _configuration.Height <= 0) return;
        var required = checked(_configuration.Width * _configuration.Height * 4);
        if (_frameBuffer is null || _frameBuffer.Length < required) _frameBuffer = new byte[required];
        if (!_renderer.TryCopyLatestFrame(_afterFrameId, _frameBuffer, out var frame)) return;
        _afterFrameId = frame.FrameId;
        if (frame.Width != _configuration.Width || frame.Height != _configuration.Height) { _lastFrameInfo = default; return; }
        if (frame.RowBytes < frame.Width * 4 || (long)frame.RowBytes * frame.Height > _frameBuffer.Length)
            throw new InvalidDataException("The renderer returned an invalid frame stride.");
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            Viewport.Source = null;
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormats.Rgba8888, AlphaFormat.Opaque);
            Viewport.Source = _bitmap;
        }
        using (var buffer = _bitmap.Lock())
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(_frameBuffer, y * frame.RowBytes, buffer.Address + y * buffer.RowBytes, frame.Width * 4);
        _lastFrameInfo = frame;
        _status.IsVisible = false;
        Viewport.InvalidateVisual();
        var now = _clock.Elapsed.TotalSeconds;
        if (_lastPresentedAt > 0)
        {
            _presentationIntervals.Enqueue((now - _lastPresentedAt) * 1000);
            while (_presentationIntervals.Count > 120) _presentationIntervals.Dequeue();
        }
        _lastPresentedAt = now;
        ++_presentedFrames;
        if (now - _diagnosticsAt >= .5) { _diagnosticsAt = now; _lastDiagnostics = _renderer.GetDiagnostics(); UpdateDiagnostics(); }
    }

    /// <summary>Copies the last frame presented by Avalonia, for focused native-render validation.</summary>
    public byte[]? CaptureLatestFrame(out FilamentFrameInfo frame)
    {
        frame = _lastFrameInfo;
        return frame.FrameId == 0 || _frameBuffer is null ? null : _frameBuffer.AsSpan(0, checked(frame.RowBytes * frame.Height)).ToArray();
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs e) => UpdateDiagnostics();
    private void UpdateDiagnostics()
    {
        _diagnostics.IsVisible = RenderDiagnostics.Enabled;
        if (!_diagnostics.IsVisible) return;
        var intervals = _presentationIntervals.Order().ToArray();
        var fps = intervals.Length == 0 ? 0 : 1000 / intervals.Average();
        var p95 = intervals.Length == 0 ? 0 : intervals[Math.Min(intervals.Length - 1, (int)Math.Ceiling(intervals.Length * .95) - 1)];
        _diagnostics.Text = $"3D {fps:0} fps · P95 {p95:0.0} ms · 渲染 {_lastFrameInfo.RenderMilliseconds:0.0} ms"
            + $" · 回读 {_lastFrameInfo.ReadbackMilliseconds:0.0} ms · {_lastDiagnostics.Backend} · MSAA {_lastDiagnostics.MsaaSamples}×"
            + $" · 已呈现 {_presentedFrames} 帧";
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ModelReady || !Motion.IsActive || !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(Viewport);
        _pointer = e.Pointer;
        Motion.BeginDrag(point.X, point.Y, _clock.Elapsed.TotalSeconds);
        e.Pointer.Capture(_root);
        e.Handled = true;
    }
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer != e.Pointer) return;
        var point = e.GetPosition(Viewport);
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
    public static Task<FilamentModelData> LoadModelAsync() => DeviceModelView.LoadModelAsync(Bundled);
}
