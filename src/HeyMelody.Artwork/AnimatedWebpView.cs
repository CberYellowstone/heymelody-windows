using System.Numerics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace HeyMelodyWindows.Artwork;

/// <summary>CPU-decoded animated WebP artwork; each frame is composed by Skia's image codec.</summary>
public class AnimatedWebpView(ILottieSource source, Func<Control?>? fallback = null,
    Func<Control?>? poster = null) : UserControl
{
    private CompositionCustomVisual? _composition;
    private Window? _window;
    private Visual[] _visibilityAncestors = [];
    private DispatcherTimer? _poster;
    private int _generation;
    private bool _attached;
    private Size _sourceSize;
    private long _renderedFrames;
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
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged += VisibilityChanged;
        LoadError = null;
        int generation = ++_generation;
        if (poster is not null)
        {
            _poster = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _poster.Tick += (_, _) =>
            {
                StopPoster();
                if (_attached && generation == _generation && !AnimationLoaded && LoadError is null) Content = poster();
            };
            _poster.Start();
        }
        _ = LoadAsync(generation);
    }

    private async Task LoadAsync(int generation)
    {
        AnimatedWebpPlayback? playback = null;
        try
        {
            playback = await Task.Run(() => AnimatedWebpPlayback.Load(source));
            if (!_attached || generation != _generation) { playback.Dispose(); return; }
            var parent = ElementComposition.GetElementVisual(this);
            if (parent is null) { playback.Dispose(); return; }
            StopPoster(); Content = null;
            _sourceSize = new Size(playback.Frame.Width, playback.Frame.Height);
            _composition = parent.Compositor.CreateCustomVisual(new WebpHandler(playback,
                () => Interlocked.Increment(ref _renderedFrames), error => Dispatcher.UIThread.Post(() => Failed(generation, error))));
            playback = null;
            AnimationLoaded = true;
            ElementComposition.SetElementChildVisual(this, _composition);
            InvalidateMeasure(); InvalidateArrange(); Resize(); RefreshActivity();
        }
        catch (Exception error) { playback?.Dispose(); Failed(generation, error); }
    }

    private void Failed(int generation, Exception error)
    {
        if (!_attached || generation != _generation) return;
        StopPoster();
        _composition?.SendHandlerMessage(Command.Dispose);
        ElementComposition.SetElementChildVisual(this, null);
        _composition = null;
        AnimationLoaded = AnimationActive = false;
        LoadError = error.Message;
        Content = fallback?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; ++_generation;
        StopPoster();
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= VisibilityChanged;
        _visibilityAncestors = [];
        _window = null;
        _composition?.SendHandlerMessage(Command.Dispose);
        ElementComposition.SetElementChildVisual(this, null);
        _composition = null;
        AnimationLoaded = AnimationActive = false;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) Resize();
        if (e.Property == IsVisibleProperty) RefreshActivity();
    }
    protected override Size MeasureOverride(Size availableSize) => Content is not null
        ? base.MeasureOverride(availableSize) : Stretch.Uniform.CalculateSize(availableSize, _sourceSize);
    private void StopPoster() { _poster?.Stop(); _poster = null; }
    private void Resize() { if (_composition is not null) _composition.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height); }
    private void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty) RefreshActivity(); }
    private void RefreshActivity()
    {
        AnimationActive = _attached && IsEffectivelyVisible && _window is { IsVisible: true, WindowState: not WindowState.Minimized };
        _composition?.SendHandlerMessage(AnimationActive ? Command.Play : Command.Pause);
    }

    private enum Command { Play, Pause, Dispose }
    private sealed class WebpHandler(AnimatedWebpPlayback playback, Action rendered, Action<Exception> failed) : CompositionCustomVisualHandler
    {
        private AnimatedWebpPlayback? _playback = playback;
        private bool _playing, _settled;
        private TimeSpan? _lastFrame;
        private double _elapsed;
        private int _frame;
        public override void OnMessage(object message)
        {
            if (message is not Command command) return;
            if (command == Command.Dispose) { _playing = false; _playback?.Dispose(); _playback = null; return; }
            _playing = command == Command.Play;
            _lastFrame = null;
            Invalidate();
            if (_playing && !_settled) RegisterForNextAnimationFrameUpdate();
        }
        public override void OnAnimationFrameUpdate()
        {
            if (!_playing || _playback is null || _settled) return;
            if (_lastFrame is { } last) _elapsed += Math.Max(0, (CompositionNow - last).TotalSeconds);
            _lastFrame = CompositionNow;
            int next = _playback.FrameAt(_elapsed, out _settled);
            if (next != _frame) { _frame = next; Invalidate(); }
            if (!_settled) RegisterForNextAnimationFrameUpdate();
        }
        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_playback is not { } loaded || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            var bounds = GetRenderBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            try
            {
                loaded.SeekFrame(_frame);
                var frame = loaded.Frame;
                double scale = Math.Min(bounds.Width / frame.Width, bounds.Height / frame.Height);
                float width = (float)(frame.Width * scale), height = (float)(frame.Height * scale);
                float left = (float)(bounds.Width - width) / 2, top = (float)(bounds.Height - height) / 2;
                using var clip = context.PushClip(bounds);
                using var lease = feature.Lease();
                using var image = SKImage.FromBitmap(frame);
                lease.SkCanvas.DrawImage(image, new SKRect(left, top, left + width, top + height), new SKSamplingOptions(SKFilterMode.Linear));
                rendered();
            }
            catch (Exception error)
            {
                _playing = false; _playback.Dispose(); _playback = null;
                failed(error);
            }
        }
    }
}

/// <summary>Keeps one decoded canvas, including prior-frame blending and disposal, rather than caching every frame.</summary>
internal sealed class AnimatedWebpPlayback : IDisposable
{
    private readonly SKData _data;
    private readonly SKCodec _codec;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly double[] _frameEnds;
    private readonly IDisposable? _lease;
    private bool _disposed;
    private int _decodedFrame = -1;
    public SKBitmap Frame { get; }
    public int FrameCount => _frames.Length;
    public double Duration => _frameEnds[^1];
    public int RepetitionCount => _codec.RepetitionCount;
    private AnimatedWebpPlayback(SKData data, SKCodec codec, SKCodecFrameInfo[] frames, SKBitmap bitmap, IDisposable? lease)
    {
        _data = data; _codec = codec; _frames = frames; Frame = bitmap; _lease = lease;
        _frameEnds = new double[frames.Length];
        double end = 0;
        for (int i = 0; i < frames.Length; i++) _frameEnds[i] = end += (frames[i].Duration > 0 ? Math.Max(10, frames[i].Duration) : 100) / 1000d;
    }
    public static AnimatedWebpPlayback Load(ILottieSource source)
    {
        var lease = source.Acquire();
        SKData? data = null;
        SKCodec? codec = null;
        SKBitmap? bitmap = null;
        try
        {
            using var stream = source.Open();
            if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Animated image exceeds its limit.");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            if (!string.IsNullOrEmpty(source.Sha256) && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Animated image integrity check failed.");
            data = SKData.CreateCopy(bytes);
            codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported animated image.");
            var info = codec.Info;
            if (codec.EncodedFormat != SKEncodedImageFormat.Webp || info.Width is <= 0 or > 8192 || info.Height is <= 0 or > 8192
                || (long)info.Width * info.Height > 16 * 1024 * 1024 || codec.FrameCount is < 2 or > 4096)
                throw new InvalidDataException("Animated WebP bounds or frame count exceed their limit.");
            var frames = codec.FrameInfo;
            if (frames.Length != codec.FrameCount || frames.Any(frame => !frame.FullyRecieved)
                || frames.Sum(frame => (long)(frame.Duration > 0 ? Math.Max(10, frame.Duration) : 100)) > 60 * 60 * 1000)
                throw new InvalidDataException("Animated WebP timeline is incomplete or exceeds its limit.");
            bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var playback = new AnimatedWebpPlayback(data, codec, frames, bitmap, lease);
            playback.SeekFrame(0);
            return playback;
        }
        catch { bitmap?.Dispose(); codec?.Dispose(); data?.Dispose(); lease?.Dispose(); throw; }
    }
    public int FrameAt(double elapsed, out bool settled)
    {
        elapsed = Math.Max(0, elapsed);
        settled = RepetitionCount >= 0 && elapsed >= Duration * (RepetitionCount + 1d);
        if (settled) return FrameCount - 1;
        double time = elapsed % Duration;
        int index = Array.BinarySearch(_frameEnds, time);
        return index >= 0 ? Math.Min(index + 1, FrameCount - 1) : ~index;
    }
    public void SeekFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _decodedFrame) return;
        int required = _frames[index].RequiredFrame;
        int prior = required >= 0 && _decodedFrame >= required && _decodedFrame < index
            && _frames[_decodedFrame].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious ? _decodedFrame : -1;
        // With no reusable prior frame the codec decodes all dependencies and clears disposed areas.
        var result = _codec.GetPixels(Frame.Info, Frame.GetPixels(), Frame.RowBytes, new SKCodecOptions(index, prior));
        if (result != SKCodecResult.Success) throw new InvalidDataException("Animated WebP frame decoding failed: " + result);
        Frame.NotifyPixelsChanged();
        _decodedFrame = index;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Frame.Dispose(); _codec.Dispose(); _data.Dispose(); _lease?.Dispose();
    }
}
