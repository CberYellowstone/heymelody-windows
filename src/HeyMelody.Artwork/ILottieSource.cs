using Avalonia.Platform;

namespace HeyMelodyWindows.Artwork;

public interface ILottieSource
{
    Uri Source { get; }
    string PackageRoot { get; }
    string Sha256 { get; }
    Stream Open();
    IDisposable? Acquire();
}
public sealed record FileLottieSource(Uri Source, string PackageRoot, string Sha256, Func<IDisposable>? AcquireLease = null) : ILottieSource
{
    public Stream Open() => Source.IsFile ? File.OpenRead(Source.LocalPath) : AssetLoader.Open(Source);
    public IDisposable? Acquire() => AcquireLease?.Invoke();
}
