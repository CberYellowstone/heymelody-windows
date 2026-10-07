using System.ComponentModel;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Threading;

namespace HeyMelodyWindows.App;

// Appearance hints stay in this view's lifetime and never grant device capabilities.
public sealed class DeviceVisualCatalog : INotifyPropertyChanged, IDisposable
{
    private readonly Dictionary<string, string> _productIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _heroes = new(StringComparer.Ordinal), _icons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedArtworkLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _colors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
    private CancellationTokenSource _loads = new();
    private string _source = "";
    private long _resourceRevision;
    private IArtworkService? _artwork;
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Clear()
    {
        ResetArtwork();
        _productIds.Clear();
        _colors.Clear();
        Changed();
    }

    internal void Update(IReadOnlyList<UiDevice> devices, UiDevice? selected, string? productId,
        int? color = null, string source = "HeyMelody", long revision = 0, IArtworkService? artwork = null)
    {
        if (_source != source || _resourceRevision != revision || !ReferenceEquals(_artwork, artwork))
        { ResetArtwork(); _source = source; _resourceRevision = revision; _artwork = artwork; }
        var identities = devices.Select(device => device.Identity).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var identity in _productIds.Keys.Where(identity => !identities.Contains(identity)).ToArray())
        { changed |= _productIds.Remove(identity); _colors.Remove(identity); RemoveArtwork(identity); }
        // The client only exposes ProductId from a snapshot matched to its active session.
        // Unsupported and unknown product IDs also override a misleading Bluetooth name.
        if (selected is not null && identities.Contains(selected.Identity) && !string.IsNullOrWhiteSpace(productId))
        {
            var actual = productId.Trim();
            if (!_productIds.TryGetValue(selected.Identity, out var previous) || !StringComparer.Ordinal.Equals(previous, actual))
            {
                RemoveArtwork(selected.Identity);
                _productIds[selected.Identity] = actual;
                changed = true;
            }
            if (!_colors.TryGetValue(selected.Identity, out var previousColor) || previousColor != color)
            {
                _colors[selected.Identity] = color;
                RemoveArtwork(selected.Identity);
                changed = true;
            }
        }
        // A PID or colour change must also cancel requests still in flight. Their
        // completion must not mark the replacement request as already resolved.
        if (changed) ResetArtwork();
        if (_artwork is not null)
            foreach (var device in devices)
                if (_productIds.TryGetValue(device.Identity, out var pid))
                { Resolve(device.Identity, pid, _colors.GetValueOrDefault(device.Identity), "hero"); Resolve(device.Identity, pid, _colors.GetValueOrDefault(device.Identity), "icon"); }
        if (changed) Changed();
    }

    internal bool IsX4(UiDevice? device)
    {
        if (device is null) return false;
        if (_productIds.TryGetValue(device.Identity, out var productId))
            return StringComparer.OrdinalIgnoreCase.Equals(productId, "06EC10");
        return false;
    }

    internal object ProductFor(UiDevice? device) => device is not null && _heroes.TryGetValue(device.Identity, out var hero) ? hero
        : _artwork is null && IsX4(device) ? DeviceVisuals.X4Product : DeviceVisuals.GenericProduct;
    internal object IconFor(UiDevice? device) => device is not null && _icons.TryGetValue(device.Identity, out var icon) ? icon
        : _artwork is null && IsX4(device) ? DeviceVisuals.X4Icon : DeviceVisuals.GenericIcon;

    private void Resolve(string identity, string pid, int? color, string role)
    {
        var key = identity + "/" + role;
        if (_resolved.Contains(key) || _leases.ContainsKey(key) || !_pending.Add(key)) return;
        _ = ResolveAsync(identity, pid, color, role, key, _loads.Token, _artwork!);
    }
    private async Task ResolveAsync(string identity, string pid, int? color, string role, string key, CancellationToken token, IArtworkService service)
    {
        UiArtwork? artwork = null;
        UiArtwork? staticArtwork = null;
        try
        {
            artwork = await service.AcquireArtworkAsync(pid, color, role == "icon" ? "capsule" : role, token);
            if (artwork is null) return;
            if (role == "hero" && artwork.Kind.ToLowerInvariant() is "model" or "glb" or "lottie")
            {
                try { staticArtwork = await service.AcquireArtworkAsync(pid, color, "detail", token); } catch { }
                if (staticArtwork is not null && (staticArtwork.Sha256 == artwork.Sha256 || staticArtwork.Kind.ToLowerInvariant() is "model" or "glb" or "lottie"))
                { new BackendArtworkLease(service, staticArtwork.LeaseId).Dispose(); staticArtwork = null; }
            }
            var owned = artwork;
            var ownedStatic = staticArtwork;
            var lease = new SharedArtworkLease(new BackendArtworkLease(service, artwork.LeaseId));
            var staticLease = staticArtwork is null ? null : new SharedArtworkLease(new BackendArtworkLease(service, staticArtwork.LeaseId));
            artwork = null;
            staticArtwork = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !_productIds.TryGetValue(identity, out var actual) || actual != pid
                    || _colors.GetValueOrDefault(identity) != color || !StringComparer.OrdinalIgnoreCase.Equals(owned.Source, _source)) { lease.Dispose(); staticLease?.Dispose(); return; }
                var file = new ArtworkFile(new Uri(owned.FullPath), owned.PackageRoot, owned.Sha256, lease.Acquire);
                object fallback = role == "hero" ? DeviceVisuals.GenericProduct : DeviceVisuals.GenericIcon;
                if (ownedStatic is not null && staticLease is not null && StringComparer.OrdinalIgnoreCase.Equals(ownedStatic.Source, _source))
                {
                    fallback = new FileBitmapVisual(new ArtworkFile(new Uri(ownedStatic.FullPath), ownedStatic.PackageRoot, ownedStatic.Sha256, staticLease.Acquire), fallback);
                    _leases[key + "/fallback"] = staticLease;
                }
                else staticLease?.Dispose();
                object visual = owned.Kind.ToLowerInvariant() switch
                {
                    "model" or "glb" => new DeviceModelVisual(file, fallback),
                    "lottie" => new LottieVisual(file, fallback),
                    _ => new FileBitmapVisual(file, fallback)
                };
                _leases[key] = lease;
                (role == "hero" ? _heroes : _icons)[identity] = visual;
                Changed();
            });
        }
        catch (Exception) { }
        finally
        {
            if (artwork is not null) new BackendArtworkLease(service, artwork.LeaseId).Dispose();
            if (staticArtwork is not null) new BackendArtworkLease(service, staticArtwork.LeaseId).Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => { if (!token.IsCancellationRequested) { _pending.Remove(key); _resolved.Add(key); } });
        }
    }
    private void RemoveArtwork(string identity)
    {
        _heroes.Remove(identity); _icons.Remove(identity);
        foreach (var role in new[] { "hero", "hero/fallback", "icon" })
        {
            _resolved.Remove(identity + "/" + role);
            if (_leases.Remove(identity + "/" + role, out var lease)) lease.Dispose();
        }
    }
    private void ResetArtwork()
    {
        _loads.Cancel(); _loads.Dispose(); _loads = new();
        _pending.Clear(); _resolved.Clear(); _heroes.Clear(); _icons.Clear();
        foreach (var lease in _leases.Values) lease.Dispose(); _leases.Clear();
    }
    public void Dispose() { ResetArtwork(); _loads.Dispose(); }
    private sealed class BackendArtworkLease(IArtworkService service, string id) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) _ = ReleaseAsync(); }
        private async Task ReleaseAsync() { try { await service.ReleaseArtworkAsync(id, CancellationToken.None); } catch { } }
    }

    private void Changed()
    {
        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
    }
}

public sealed class DeviceIconVisualConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count >= 2 && values[0] is UiDevice device && values[1] is DeviceVisualCatalog catalog
            ? catalog.IconFor(device) : DeviceVisuals.GenericIcon;
}
