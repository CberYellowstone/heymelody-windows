using System.Numerics;
using System.Text.Json;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using SkiaSharp.Resources;
using SkiaSharp.Skottie;
using ResourceProvider = SkiaSharp.Resources.ResourceProvider;

namespace HeyMelodyWindows.Artwork;

/// <summary>Skottie artwork rendered by Avalonia's compositor without a UI frame timer.</summary>
public class LottieView(ILottieSource source, Func<Control?>? fallback = null) : UserControl
{
    private CompositionCustomVisual? _composition;
    private Window? _window;
    private int _generation;
    private bool _attached;
    private Visual[] _visibilityAncestors = [];
    private long _renderedFrames;
    private Size _sourceSize;
    internal long RenderedFrameCount => Interlocked.Read(ref _renderedFrames);
    public bool AnimationActive { get; private set; }
    public bool AnimationLoaded { get; private set; }
    public string? LoadError { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        _visibilityAncestors = this.GetVisualAncestors().ToArray();
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged += WindowChanged;
        _ = LoadAsync(++_generation);
    }

    private async Task LoadAsync(int generation)
    {
        LottiePlayback? playback = null;
        try
        {
            playback = await Task.Run(() => LottiePlayback.Load(source));
            if (!_attached || generation != _generation) { playback.Dispose(); return; }
            var parent = ElementComposition.GetElementVisual(this);
            if (parent is null) { playback.Dispose(); return; }
            _sourceSize = new Size(playback.Animation.Size.Width, playback.Animation.Size.Height);
            _composition = parent.Compositor.CreateCustomVisual(new LottieHandler(playback, () => Interlocked.Increment(ref _renderedFrames)));
            AnimationLoaded = true;
            playback = null; // The compositor owns all native Skia objects from here.
            ElementComposition.SetElementChildVisual(this, _composition);
            InvalidateMeasure(); InvalidateArrange(); Resize(); RefreshActivity();
        }
        catch (Exception error)
        {
            playback?.Dispose();
            if (!_attached || generation != _generation) return;
            LoadError = error.Message;
            Content = fallback?.Invoke();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; ++_generation;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= WindowChanged;
        _visibilityAncestors = [];
        _window = null;
        _composition?.SendHandlerMessage(LottieCommand.Dispose);
        ElementComposition.SetElementChildVisual(this, null);
        _composition = null; AnimationActive = false; AnimationLoaded = false;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) Resize();
        if (e.Property == IsVisibleProperty) RefreshActivity();
    }
    protected override Size MeasureOverride(Size availableSize)
        => Content is not null ? base.MeasureOverride(availableSize)
            : Stretch.Uniform.CalculateSize(availableSize, _sourceSize);
    private void Resize() { if (_composition is not null) _composition.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height); }
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty) RefreshActivity(); }
    private void RefreshActivity()
    {
        AnimationActive = _attached && IsEffectivelyVisible && _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized;
        _composition?.SendHandlerMessage(AnimationActive ? LottieCommand.Play : LottieCommand.Pause);
    }

    private enum LottieCommand { Play, Pause, Dispose }
    private sealed class LottieHandler(LottiePlayback playback, Action rendered) : CompositionCustomVisualHandler
    {
        private LottiePlayback? _playback = playback;
        private bool _playing;
        private TimeSpan? _lastFrame;
        private double _elapsed;
        public override void OnMessage(object message)
        {
            if (message is not LottieCommand command) return;
            if (command == LottieCommand.Dispose)
            { _playing = false; _playback?.Dispose(); _playback = null; return; }
            _playing = command == LottieCommand.Play;
            _lastFrame = null;
            Invalidate();
            if (_playing) RegisterForNextAnimationFrameUpdate();
        }
        public override void OnAnimationFrameUpdate()
        {
            if (!_playing || _playback is null) return;
            if (_lastFrame is { } last) _elapsed += Math.Max(0, (CompositionNow - last).TotalSeconds);
            _lastFrame = CompositionNow;
            Invalidate(); RegisterForNextAnimationFrameUpdate();
        }
        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_playback is not { } loaded || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            var bounds = GetRenderBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var animation = loaded.Animation;
            animation.SeekFrameTime(_elapsed % animation.Duration.TotalSeconds);
            var scale = Math.Min(bounds.Width / animation.Size.Width, bounds.Height / animation.Size.Height);
            var w = animation.Size.Width * scale; var h = animation.Size.Height * scale;
            var destination = new SKRect((float)((bounds.Width - w) / 2), (float)((bounds.Height - h) / 2),
                (float)((bounds.Width + w) / 2), (float)((bounds.Height + h) / 2));
            using (context.PushClip(bounds))
            {
                using var lease = feature.Lease();
                animation.Render(lease.SkCanvas, destination);
            }
            rendered();
        }
    }
}

/// <summary>Validates local image references before allowing Skottie's package provider to open them.</summary>
internal sealed class LottiePlayback : IDisposable
{
    public Animation Animation { get; }
    private readonly IDisposable? _lease;
    private readonly ResourceProvider _provider;
    private readonly ResourceProvider _files;
    private LottiePlayback(Animation animation, IDisposable? lease, ResourceProvider provider, ResourceProvider files)
    { Animation = animation; _lease = lease; _provider = provider; _files = files; }

    public static LottiePlayback Load(ILottieSource file)
    {
        var lease = file.Acquire();
        FileResourceProvider? files = null;
        DataUriResourceProvider? provider = null;
        Animation? animation = null;
        try
        {
            using var stream = file.Open();
            if (stream.Length is <= 0 or > 24 * 1024 * 1024) throw new InvalidDataException("Animation file exceeds its limit.");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            var root = file.Source.IsFile ? Path.GetDirectoryName(file.Source.LocalPath)! : file.PackageRoot;
            var bytes = memory.ToArray();
            if (!string.IsNullOrEmpty(file.Sha256) && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Animation integrity check failed.");
            ValidateImages(bytes, root, file.PackageRoot);
            files = new FileResourceProvider(root, true);
            provider = new DataUriResourceProvider(files, true);
            using var builder = Animation.CreateBuilder();
            builder.SetResourceProvider(provider);
            memory.Position = 0;
            animation = builder.Build(memory) ?? throw new InvalidDataException("Unsupported animation.");
            if (animation.Size.Width is <= 0 or > 8192 || animation.Size.Height is <= 0 or > 8192
                || animation.Duration <= TimeSpan.Zero || animation.Duration > TimeSpan.FromHours(1))
                throw new InvalidDataException("Animation bounds exceed their limit.");
            return new LottiePlayback(animation, lease, provider, files);
        }
        catch { animation?.Dispose(); provider?.Dispose(); files?.Dispose(); lease?.Dispose(); throw; }
    }

    internal static void ValidateImages(byte[] json, string relativeRoot, string packageRoot)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        if (!document.RootElement.TryGetProperty("assets", out var assets)) return;
        if (assets.GetArrayLength() > 4096) throw new InvalidDataException("Too many animation assets.");
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("p", out var p)) continue;
            var path = p.GetString() ?? "";
            if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!path.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || path.Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Unsupported embedded animation image.");
                continue;
            }
            var prefix = asset.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(packageRoot) || Path.IsPathRooted(path) || Path.IsPathRooted(prefix)
                || path.Contains(':') || prefix.Contains(':')) throw new InvalidDataException("Animation images must be inside the resource package.");
            var full = Path.GetFullPath(Path.Combine(relativeRoot, prefix, path));
            var boundary = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidDataException("Animation image is outside its resource package or missing.");
            if (new FileInfo(full).Length > 16 * 1024 * 1024) throw new InvalidDataException("Animation image exceeds its limit.");
        }
    }
    public void Dispose() { Animation.Dispose(); _provider.Dispose(); _files.Dispose(); _lease?.Dispose(); }
}
