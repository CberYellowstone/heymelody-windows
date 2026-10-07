using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HeyMelodyWindows.App;

public partial class MainView
{
    private bool _centerDevicePending = true;
    private double _deviceViewportWidth, _deviceStripWidth;
    private IReadOnlyList<UiDevice>? _centeringDevices;
    private string? _centeringDeviceIdentity;

    private void InitializeDeviceBar()
    {
        AttachedToVisualTree += (_, _) => RequestDeviceCenter();
        LayoutUpdated += (_, _) => UpdateDeviceBarLayout();
        DeviceBarScroll.AddHandler(PointerWheelChangedEvent, DeviceBar_PointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void RequestDeviceCenter()
    {
        _centeringDevices = ViewModel?.Devices;
        _centeringDeviceIdentity = ViewModel?.SelectedDevice?.Identity;
        _centerDevicePending = true;
        DeviceBarScroll.InvalidateArrange();
    }

    private void RequestDeviceCenterIfChanged()
    {
        // State notifications also mention unchanged device properties during polling.
        // Updating their picture or status must preserve the user's manual scroll position.
        if (!ReferenceEquals(_centeringDevices, ViewModel?.Devices)
            || _centeringDeviceIdentity != ViewModel?.SelectedDevice?.Identity)
            RequestDeviceCenter();
    }

    private void UpdateDeviceBarLayout()
    {
        var viewport = DeviceBarScroll.Viewport.Width;
        var stripWidth = DeviceStrip.Bounds.Width;
        if (viewport <= 0 || stripWidth <= 0) return;

        if (Math.Abs(viewport - _deviceViewportWidth) > 0.1 || Math.Abs(stripWidth - _deviceStripWidth) > 0.1)
        {
            _deviceViewportWidth = viewport;
            _deviceStripWidth = stripWidth;
            _centerDevicePending = true;
        }

        var overflowing = stripWidth > viewport;
        var card = DevicePicker.ContainerFromIndex(DevicePicker.SelectedIndex) as Control;
        if (!_centerDevicePending) return;
        if (!overflowing)
        {
            DeviceBarScroll.Offset = default;
            _centerDevicePending = false;
            return;
        }
        if (ViewModel?.SelectedDevice is null)
        {
            _centerDevicePending = false;
            return;
        }
        if (card is null || card.Bounds.Width <= 0 || !Equals(DevicePicker.SelectedItem, ViewModel.SelectedDevice)) return;
        var centre = card.TranslatePoint(new Point(card.Bounds.Width / 2, 0), DeviceStripPadding);
        if (centre is null) return;
        var maximum = Math.Max(0, DeviceBarScroll.Extent.Width - viewport);
        // Centre the card where possible, stopping naturally at the real strip edges.
        var offset = Math.Clamp(centre.Value.X - viewport / 2, 0, maximum);
        DeviceBarScroll.Offset = new Vector(offset, 0);
        _centerDevicePending = false;
    }

    private void DeviceBar_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // A regular mouse wheel also moves this horizontal device strip.
        if (e.Handled || Math.Abs(e.Delta.X) > 0.001 || e.Delta.Y == 0) return;
        var maximum = Math.Max(0, DeviceBarScroll.Extent.Width - DeviceBarScroll.Viewport.Width);
        var offset = Math.Clamp(DeviceBarScroll.Offset.X - e.Delta.Y * 64, 0, maximum);
        if (Math.Abs(offset - DeviceBarScroll.Offset.X) < 0.1) return;
        _centerDevicePending = false;
        DeviceBarScroll.Offset = new Vector(offset, 0);
        e.Handled = true;
    }
}
