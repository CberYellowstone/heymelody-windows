using System.ComponentModel;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HeyMelodyWindows.App;

// Appearance hints stay in this view's lifetime and never grant device capabilities.
public sealed class DeviceVisualCatalog : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _productIds = new(StringComparer.Ordinal);
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Clear()
    {
        if (_productIds.Count == 0) return;
        _productIds.Clear();
        Changed();
    }

    internal void Update(IReadOnlyList<UiDevice> devices, UiDevice? selected, string? productId)
    {
        var identities = devices.Select(device => device.Identity).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var identity in _productIds.Keys.Where(identity => !identities.Contains(identity)).ToArray())
            changed |= _productIds.Remove(identity);
        // The client only exposes ProductId from a snapshot matched to its active session.
        // Unsupported and unknown product IDs also override a misleading Bluetooth name.
        if (selected is not null && identities.Contains(selected.Identity) && !string.IsNullOrWhiteSpace(productId))
        {
            var actual = productId.Trim();
            if (!_productIds.TryGetValue(selected.Identity, out var previous) || !StringComparer.Ordinal.Equals(previous, actual))
            {
                _productIds[selected.Identity] = actual;
                changed = true;
            }
        }
        if (changed) Changed();
    }

    internal bool IsX4(UiDevice? device)
    {
        if (device is null) return false;
        if (_productIds.TryGetValue(device.Identity, out var productId))
            return StringComparer.OrdinalIgnoreCase.Equals(productId, "06EC10");
        var name = device.Name.Trim();
        return StringComparer.OrdinalIgnoreCase.Equals(name, "OPPO Enco X4")
            || StringComparer.OrdinalIgnoreCase.Equals(name, "Enco X4");
    }

    internal object ProductFor(UiDevice? device) => IsX4(device) ? DeviceVisuals.X4Product : DeviceVisuals.GenericProduct;
    internal object IconFor(UiDevice? device) => IsX4(device) ? DeviceVisuals.X4Icon : DeviceVisuals.GenericIcon;

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
