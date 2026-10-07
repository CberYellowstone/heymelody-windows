using Avalonia.Platform;
using HeyMelodyWindows.Artwork;

namespace HeyMelodyWindows.App;

/// <summary>A verified artwork file and the package lifetime needed by its renderer.</summary>
public sealed record ArtworkFile(Uri Source, string PackageRoot, string Sha256, Func<IDisposable>? AcquireLease = null) : ILottieSource
{
    public Stream Open() => Source.IsFile ? File.OpenRead(Source.LocalPath) : AssetLoader.Open(Source);
    public IDisposable? Acquire() => AcquireLease?.Invoke();
}

internal sealed class SharedArtworkLease(IDisposable resource) : IDisposable
{
    private readonly object _gate = new();
    private int _count = 1;
    public IDisposable Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_count == 0, this);
            _count++;
            return new Release(this);
        }
    }
    public void Dispose()
    {
        lock (_gate) if (_count > 0 && --_count == 0) resource.Dispose();
    }
    private sealed class Release(SharedArtworkLease owner) : IDisposable
    {
        private SharedArtworkLease? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}

public sealed record FileBitmapVisual(ArtworkFile File, object Fallback);
public sealed record DeviceModelVisual(ArtworkFile File, object Fallback);
public sealed record LottieVisual(ArtworkFile File, object Fallback);
