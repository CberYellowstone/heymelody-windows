using System.ComponentModel;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Threading;

namespace HeyMelodyWindows.App;

// Appearance hints never grant device capabilities. A product ID comes from the Host's remembered
// verified observations or the selected device's live snapshot, never from a Bluetooth name.
public sealed class DeviceVisualCatalog : INotifyPropertyChanged, IDisposable
{
    private readonly Dictionary<string, Appearance> _appearances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    // identity/hero -> slot whose visual is displayed; replaced only once the desired slot is ready.
    private readonly Dictionary<string, string> _shown = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _retry = new();
    private string _epoch = "";
    private IArtworkService? _artwork;
    private bool _disposed;
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    public DeviceVisualCatalog() => _retry.Tick += (_, _) => RetryDue();

    private sealed record Appearance(string ProductId, int? Color);
    private sealed class Slot(string key, string productId, int? color)
    {
        public string Key { get; } = key;
        public string ProductId { get; } = productId;
        public int? Color { get; } = color;
        public object? Visual;
        public string? Content;
        public SharedArtworkLease? Lease, FallbackLease;
        public CancellationTokenSource? Loading;
        public string ResolvedEpoch = "";
        public int Failures;
        public DateTime? RetryAt;
        public void Release()
        {
            Loading?.Cancel(); Loading?.Dispose(); Loading = null;
            Lease?.Dispose(); FallbackLease?.Dispose(); Lease = FallbackLease = null;
        }
    }

    internal void Clear()
    {
        ResetSlots();
        _appearances.Clear();
        Changed();
    }

    internal void Update(IReadOnlyList<UiDevice> devices, UiDevice? selected, string? productId,
        int? color = null, string epoch = "", IArtworkService? artwork = null)
    {
        if (_disposed) return;
        bool changed = false;
        if (!ReferenceEquals(_artwork, artwork))
        {
            // Another backend owns different leases: its responses must never be displayed.
            changed = _slots.Count != 0;
            ResetSlots(); _artwork = artwork;
        }
        if (_epoch != epoch)
        {
            // A restarted Host or newly published resources make a pending retry worth trying now.
            _epoch = epoch;
            foreach (var slot in _slots.Values) slot.RetryAt = null;
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (MainViewModel.IsDemoDevice(device)) continue;
            identities.Add(device.Identity);
            changed |= Remember(device.Identity, device.ProductId, device.Color);
        }
        // The client only exposes ProductId from a snapshot matched to its active session.
        if (selected is not null && identities.Contains(selected.Identity)) changed |= Remember(selected.Identity, productId, color);
        foreach (string removed in _appearances.Keys.Where(identity => !identities.Contains(identity)).ToArray())
        { _appearances.Remove(removed); changed = true; }
        foreach (string removed in _shown.Keys.Where(key => !identities.Contains(key[..key.LastIndexOf('/')])).ToArray())
            _shown.Remove(removed);
        if (_artwork is not null)
            foreach (var (identity, appearance) in _appearances)
            {
                var slot = Ensure(appearance);
                if (NeedsResolve(slot)) Resolve(slot);
                changed |= Show(identity, slot);
            }
        Collect();
        if (changed) Changed();
    }

    internal object ProductFor(UiDevice? device) => MainViewModel.IsDemoDevice(device) ? new EncoFree4DynaudioModelVisual()
        : Displayed(device) ?? (_artwork is null && IsX4(device) ? DeviceVisuals.X4Product
            : FirstResolving(device) ? DeviceVisuals.LoadingProduct : DeviceVisuals.GenericProduct);

    // Only the first lookup shows the loading state: after a failure the generic icon stays through the retries.
    private bool FirstResolving(UiDevice? device) => _artwork is not null && device is not null
        && _appearances.TryGetValue(device.Identity, out var appearance) && _slots.TryGetValue(KeyFor(appearance), out var slot)
        && slot is { Visual: null, Loading: not null, Failures: 0 };

    internal bool IsX4(UiDevice? device) => device is not null && _appearances.TryGetValue(device.Identity, out var appearance)
        && StringComparer.OrdinalIgnoreCase.Equals(appearance.ProductId, "06EC10");

    private object? Displayed(UiDevice? device) => device is not null && _shown.TryGetValue(device.Identity + "/hero", out var key)
        && _slots.TryGetValue(key, out var slot) ? slot.Visual : null;

    /// <summary>Learned values only accumulate: an unknown value never clears a known one.</summary>
    private bool Remember(string identity, string? productId, int? color)
    {
        if (string.IsNullOrWhiteSpace(productId)) return false;
        string actual = productId.Trim().ToUpperInvariant();
        if (!_appearances.TryGetValue(identity, out var old) || old.ProductId != actual)
        {
            // Another model must not keep borrowing the previous model's artwork while its own loads.
            if (old is not null) _shown.Remove(identity + "/hero");
            _appearances[identity] = new(actual, color);
            return true;
        }
        if (color is null || old.Color == color) return false;
        _appearances[identity] = old with { Color = color };
        return true;
    }

    private static string KeyFor(Appearance appearance) =>
        "hero/" + appearance.ProductId + "/" + (appearance.Color?.ToString(CultureInfo.InvariantCulture) ?? "-");
    private Slot Ensure(Appearance appearance)
    {
        string key = KeyFor(appearance);
        if (!_slots.TryGetValue(key, out var slot)) _slots[key] = slot = new(key, appearance.ProductId, appearance.Color);
        return slot;
    }
    private bool Show(string identity, Slot desired)
    {
        string place = identity + "/hero";
        // Until the desired slot is ready, keep showing the previous one (e.g. art resolved before the color was known).
        if (desired.Visual is null || _shown.TryGetValue(place, out var shown) && shown == desired.Key) return false;
        _shown[place] = desired.Key;
        return true;
    }
    private bool NeedsResolve(Slot slot) => slot.Loading is null && slot.RetryAt is null
        && (slot.Visual is null || slot.ResolvedEpoch != _epoch);

    private void Resolve(Slot slot)
    {
        var loading = new CancellationTokenSource();
        slot.Loading = loading;
        _ = ResolveAsync(slot, _epoch, _artwork!, loading.Token);
    }

    private async Task ResolveAsync(Slot slot, string epoch, IArtworkService service, CancellationToken token)
    {
        UiArtwork? main = null, fallback = null;
        try
        {
            main = await service.AcquireArtworkAsync(slot.ProductId, slot.Color, "hero", token);
            if (main?.Kind.ToLowerInvariant() is "model" or "glb" or "lottie" or "animated-webp")
            {
                try { fallback = await service.AcquireArtworkAsync(slot.ProductId, slot.Color, "detail", token); }
                catch (Exception error) when (error is not OperationCanceledException) { }
                if (fallback is not null && (fallback.Sha256 == main.Sha256 || fallback.Kind.ToLowerInvariant() is "model" or "glb" or "lottie" or "animated-webp"))
                { Release(service, fallback); fallback = null; }
            }
            var (ownedMain, ownedFallback) = (main, fallback);
            main = fallback = null;
            await Dispatcher.UIThread.InvokeAsync(() => Complete(slot, epoch, service, ownedMain, ownedFallback));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { await Dispatcher.UIThread.InvokeAsync(() => Fail(slot, token)); }
        finally
        {
            if (main is not null) Release(service, main);
            if (fallback is not null) Release(service, fallback);
        }
    }

    private void Complete(Slot slot, string epoch, IArtworkService service, UiArtwork? main, UiArtwork? fallback)
    {
        bool current = !_disposed && ReferenceEquals(service, _artwork)
            && _slots.TryGetValue(slot.Key, out var live) && ReferenceEquals(live, slot) && slot.Loading is { IsCancellationRequested: false };
        if (!current) { Release(service, main); Release(service, fallback); return; }
        slot.Loading!.Dispose(); slot.Loading = null;
        slot.Failures = 0; slot.RetryAt = null; slot.ResolvedEpoch = epoch;
        // Equal digests mean equal bytes; a republished or bundled copy needs no new renderer.
        string content = main is null ? "none" : main.Kind.ToLowerInvariant() + "|" + main.Sha256 + "|" + fallback?.Sha256;
        if (slot.Visual is not null && slot.Content == content)
        {
            // Identical package: keep the attached renderer instead of reloading the model or restarting an animation.
            Release(service, main); Release(service, fallback);
            return;
        }
        var lease = main is null ? null : new SharedArtworkLease(new BackendArtworkLease(service, main.LeaseId));
        var fallbackLease = fallback is null ? null : new SharedArtworkLease(new BackendArtworkLease(service, fallback.LeaseId));
        object placeholder = DeviceVisuals.GenericProduct;
        object visual = placeholder;
        if (main is not null)
        {
            var file = new ArtworkFile(new Uri(main.FullPath), main.PackageRoot, main.Sha256, lease!.Acquire);
            object staticVisual = fallback is null ? placeholder : new FileBitmapVisual(
                new ArtworkFile(new Uri(fallback.FullPath), fallback.PackageRoot, fallback.Sha256, fallbackLease!.Acquire), placeholder);
            visual = main.Kind.ToLowerInvariant() switch
            {
                "model" or "glb" => new DeviceModelVisual(file, staticVisual),
                "lottie" => new LottieVisual(file, staticVisual),
                "animated-webp" => new AnimatedWebpVisual(file, staticVisual),
                _ => new FileBitmapVisual(file, placeholder)
            };
        }
        // Renderers hold their own lease references, so replacing these cannot delete files in use.
        slot.Lease?.Dispose(); slot.FallbackLease?.Dispose();
        slot.Visual = visual; slot.Content = content; slot.Lease = lease; slot.FallbackLease = fallbackLease;
        if (visual is DeviceModelVisual modelVisual) DeviceModelView.PrefetchModel(modelVisual);
        foreach (var (identity, appearance) in _appearances)
            if (KeyFor(appearance) == slot.Key) Show(identity, slot);
        Collect();
        Changed();
    }

    private void Fail(Slot slot, CancellationToken token)
    {
        if (token.IsCancellationRequested || _disposed || !_slots.TryGetValue(slot.Key, out var live) || !ReferenceEquals(live, slot)) return;
        slot.Loading?.Dispose(); slot.Loading = null;
        ScheduleRetry(slot);
        // A first failure ends the loading state shown for this slot.
        Changed();
    }

    private void ScheduleRetry(Slot slot)
    {
        var delay = RetryDelays[Math.Min(slot.Failures, RetryDelays.Count - 1)];
        slot.Failures++;
        slot.RetryAt = DateTime.UtcNow + delay;
        RestartRetryTimer();
    }

    private void RetryDue()
    {
        _retry.Stop();
        if (_disposed || _artwork is null) return;
        var now = DateTime.UtcNow;
        foreach (var slot in _slots.Values.Where(slot => slot.RetryAt <= now).ToArray())
        { slot.RetryAt = null; Resolve(slot); }
        RestartRetryTimer();
    }

    private void RestartRetryTimer()
    {
        _retry.Stop();
        var next = _slots.Values.Where(slot => slot.RetryAt is not null).Select(slot => slot.RetryAt!.Value).DefaultIfEmpty().Min();
        if (next == default) return;
        _retry.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (next - DateTime.UtcNow).TotalMilliseconds));
        _retry.Start();
    }

    /// <summary>Releases slots that no device wants or still displays.</summary>
    private void Collect()
    {
        var wanted = _appearances.Values.Select(KeyFor)
            .Concat(_shown.Values).ToHashSet(StringComparer.Ordinal);
        foreach (var slot in _slots.Values.Where(slot => !wanted.Contains(slot.Key)).ToArray())
        { slot.Release(); _slots.Remove(slot.Key); }
    }

    private void ResetSlots()
    {
        foreach (var slot in _slots.Values) slot.Release();
        _slots.Clear(); _shown.Clear(); _retry.Stop();
    }

    public void Dispose() { _disposed = true; ResetSlots(); }

    private static void Release(IArtworkService service, UiArtwork? artwork)
    { if (artwork is not null) new BackendArtworkLease(service, artwork.LeaseId).Dispose(); }

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

public sealed class DemoDeviceCaptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => MainViewModel.IsDemoDevice(value as UiDevice) ? "演示设备" : "已配对";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Screen-reader name of a device card: its name and the caption shown under it.</summary>
public sealed class DeviceCardNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is UiDevice device ? device.Name + "，" + (MainViewModel.IsDemoDevice(device) ? "演示设备" : "已配对") : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
