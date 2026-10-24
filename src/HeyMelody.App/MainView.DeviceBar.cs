using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

public partial class MainView
{
    private bool _centerDevicePending = true;
    private bool _smoothDeviceBar;
    private bool _layoutObserved;
    private double _deviceViewportWidth, _deviceStripWidth;
    private IReadOnlyList<UiDevice>? _centeringDevices;
    private string? _centeringDeviceIdentity;
    private string? _gestureIdentity;
    private CancellationTokenSource? _pendingRefresh;
    private TopLevel? _keyTopLevel;
    /// <summary>Replaces the system double-click interval that a double click waits for a third click.</summary>
    internal TimeSpan? DeviceGestureDelayOverride { get; set; }

    private void InitializeDeviceBar()
    {
        _smoothDeviceBar = SmoothScrolling.Attach(DeviceBarScroll, horizontal: true);
        if (_smoothDeviceBar) DeviceStripPadding.HorizontalAlignment = HorizontalAlignment.Left;
        AttachedToVisualTree += (_, _) => RequestDeviceCenter();
        // LayoutUpdated fires after every layout pass in the window, including each frame of an expander
        // animation, so it is observed only while a re-centre is pending; size changes start a new one.
        DeviceBarScroll.SizeChanged += (_, _) => RequestDeviceLayout();
        DeviceStrip.SizeChanged += (_, _) => RequestDeviceLayout();
        DeviceBarScroll.AddHandler(PointerWheelChangedEvent, DeviceBar_PointerWheelChanged, RoutingStrategies.Tunnel);
        // Cards mark presses handled when they select, so the gesture listens on the way down.
        DevicePicker.AddHandler(PointerPressedEvent, DevicePicker_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // On the window, so the keys also work before anything inside the view has focus.
        AttachedToVisualTree += (_, _) => ObserveKeys(TopLevel.GetTopLevel(this));
        DetachedFromVisualTree += (_, _) => ObserveKeys(null);
    }

    private void ObserveKeys(TopLevel? top)
    {
        if (ReferenceEquals(top, _keyTopLevel)) return;
        _keyTopLevel?.RemoveHandler(KeyDownEvent, MainView_KeyDown);
        _keyTopLevel = top;
        _keyTopLevel?.AddHandler(KeyDownEvent, MainView_KeyDown);
    }

    private void DevicePicker_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(DevicePicker).Properties.IsLeftButtonPressed) return;
        var device = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as UiDevice;
        HandleDeviceCardPress(device, e.ClickCount, e.Pointer.Type);
    }

    /// <summary>A double click on the current device card refreshes its state; a triple click reconnects it.</summary>
    /// <remarks>
    /// A double click waits one double-click interval, so a triple click reconnects without refreshing first. Only a
    /// sequence that starts on the card already selected counts; the first click on another card just switches to it.
    /// </remarks>
    internal void HandleDeviceCardPress(UiDevice? device, int clickCount, PointerType type)
    {
        if (clickCount <= 1)
        {
            CancelDeviceGesture();
            _gestureIdentity = device is not null && device.Identity == ViewModel?.SelectedDevice?.Identity ? device.Identity : null;
            return;
        }
        if (device is null || device.Identity != _gestureIdentity || ViewModel is not { } vm
            || vm.SelectedDevice?.Identity != device.Identity) { CancelDeviceGesture(); return; }
        if (clickCount == 2) _ = RefreshAfterDoubleClickAsync(vm, device.Identity, type);
        else if (clickCount == 3)
        {
            CancelPendingRefresh();
            _ = ExecuteAsync(vm.ReconnectCommand);
        }
    }

    private async Task RefreshAfterDoubleClickAsync(MainViewModel vm, string identity, PointerType type)
    {
        CancelPendingRefresh();
        var pending = _pendingRefresh = new CancellationTokenSource();
        var delay = DeviceGestureDelayOverride ?? Application.Current?.PlatformSettings?.GetDoubleTapTime(type)
            ?? TimeSpan.FromMilliseconds(500);
        try { await Task.Delay(delay, pending.Token); }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(_pendingRefresh, pending)) return;
        _pendingRefresh = null;
        pending.Dispose();
        if (ReferenceEquals(ViewModel, vm) && vm.SelectedDevice?.Identity == identity) await ExecuteAsync(vm.RefreshStateCommand);
    }

    private void CancelPendingRefresh()
    {
        if (_pendingRefresh is not { } pending) return;
        _pendingRefresh = null;
        pending.Cancel();
        pending.Dispose();
    }

    private void CancelDeviceGesture()
    {
        CancelPendingRefresh();
        _gestureIdentity = null;
    }

    /// <summary>Keyboard equivalents of the card gestures: F5 refreshes, Shift+F5 reconnects.</summary>
    /// <remarks>Keys from dialogs and overlays outside this view (for example the settings dialog) are left alone.</remarks>
    private async void MainView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.F5 || ViewModel is not { } vm || !IsEffectivelyEnabled) return;
        if (e.Source is Visual source && !ReferenceEquals(source, _keyTopLevel) && !this.IsVisualAncestorOf(source)) return;
        var command = e.KeyModifiers switch
        {
            KeyModifiers.None => vm.RefreshStateCommand,
            KeyModifiers.Shift => vm.ReconnectCommand,
            _ => null
        };
        if (command is null) return;
        e.Handled = true;
        await ExecuteAsync(command);
    }

    private static async Task ExecuteAsync(IAsyncRelayCommand command)
    {
        if (command.CanExecute(null)) await command.ExecuteAsync(null);
    }

    private void RequestDeviceCenter()
    {
        _centeringDevices = ViewModel?.Devices;
        _centeringDeviceIdentity = ViewModel?.SelectedDevice?.Identity;
        _centerDevicePending = true;
        ObserveLayout(true);
        DeviceBarScroll.InvalidateArrange();
    }

    private void RequestDeviceLayout()
    {
        ObserveLayout(true);
        DeviceBarScroll.InvalidateArrange();
    }

    private void ObserveLayout(bool observe)
    {
        if (observe == _layoutObserved) return;
        _layoutObserved = observe;
        if (observe) LayoutUpdated += OnDeviceBarLayoutUpdated;
        else LayoutUpdated -= OnDeviceBarLayoutUpdated;
    }

    private void OnDeviceBarLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateDeviceBarLayout();
        // Keep listening while the selected card is not yet realised; stop once the strip is settled.
        if (!_centerDevicePending) ObserveLayout(false);
    }

    private void RequestDeviceCenterIfChanged()
    {
        // State notifications also mention unchanged device properties during polling.
        // Updating their connection status must preserve the user's manual scroll position.
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
        if (_smoothDeviceBar)
        {
            if (e.Delta.X != 0 || e.Delta.Y != 0) _centerDevicePending = false;
            // The patched input source maps both precise and whole vertical packets onto X.
            return;
        }
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
