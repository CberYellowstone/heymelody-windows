using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using System.ComponentModel;
using Avalonia.VisualTree;

namespace HeyMelodyWindows.App;

public partial class MainView : UserControl
{
    private bool _selectingDevice;
    private bool _deviceSelectionSyncQueued;
    private MainViewModel? _observedViewModel;
    private FAContentDialog? _settingsDialog;
    private Task? _settingsOperation;
    private CancellationTokenSource? _settingsCancellation;
    private CancellationTokenSource? _bluetoothSettingsCancellation;
    private bool _openingBluetoothSettings;
    private bool _closingWindow;
    private bool _modelDemo;
    private bool _copyingDeviceInfo;
    private long _deviceInfoCopyVersion;
    internal ILauncher? LauncherOverride { get; set; }
    internal Func<string, Task>? WriteClipboardOverride { get; set; }
    internal Func<Task>? ManageDevicesRequested { get; set; }
    internal Func<Task>? RequestFitTest { get; set; }
    internal Func<Task>? RequestHearingSetup { get; set; }
    private bool _manageDevicesRequested;
    private bool _savingResourcePreferences;
    private ResourcePreferenceDraft? _resourcePreferenceDraft;
    private sealed record ResourcePreferenceDraft(UiOption Source, bool Online, string Key);
    private readonly Dictionary<string, bool> _sectionExpansion = new(StringComparer.Ordinal);
    public DeviceVisualCatalog VisualCatalog { get; } = new();
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    public MainView()
    {
        InitializeComponent();
        InitializeDeviceBar();
        InitializeDemoDeviceInteraction();
        UpdateConnectionIndicator();
        VisualCatalog.PropertyChanged += (_, _) => UpdateHeroVisual();
        UserInputCommit.Combo(HearingProfileCombo, async () =>
        {
            if (ViewModel is { } vm && HearingProfileCombo.SelectedItem is UiHearingProfile profile)
                await vm.SelectHearingProfileAsync(profile);
        });
        AddDeviceHost.IsEnabled = OperatingSystem.IsWindows();
        SizeChanged += (_, _) => UpdateLayoutMode(Bounds.Width);
        DataContextChanged += (_, _) =>
        {
            CancelAddDeviceHold();
            if (_observedViewModel is not null) _observedViewModel.PropertyChanged -= ViewModelPropertyChanged;
            VisualCatalog.Clear();
            _sectionExpansion.Clear();
            _demoPresentationInitialized = false;
            _observedViewModel = ViewModel;
            if (ViewModel is { } vm)
            {
                vm.PropertyChanged += ViewModelPropertyChanged;
            }
            UpdateArtwork();
            UpdateDemoPresentation();
            UpdateConnectionIndicator();
            ResetDeviceInfoCopyFeedback();
            RequestDeviceSelectionSync();
            RequestDeviceCenter();
        };
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.ProductId) or nameof(MainViewModel.HasVerifiedSnapshot)
            or nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.Devices) or nameof(MainViewModel.ResourceSource)
            or nameof(MainViewModel.ArtworkEpoch) or nameof(MainViewModel.DeviceColor)) UpdateArtwork();
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.Devices))
        {
            RequestDeviceSelectionSync();
            RequestDeviceCenterIfChanged();
        }
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.IsDemoSelected)) UpdateDemoPresentation();
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.SelectedDevice)
            or nameof(MainViewModel.Title) or nameof(MainViewModel.ProductId) or nameof(MainViewModel.Firmware)
            or nameof(MainViewModel.Closing)) ResetDeviceInfoCopyFeedback();
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName == nameof(MainViewModel.Indicator)) UpdateConnectionIndicator();
    }

    private void UpdateConnectionIndicator()
    {
        var indicator = ViewModel?.Indicator ?? ConnectionIndicator.Offline;
        ConnectionDot.Classes.Set("pending", indicator == ConnectionIndicator.Pending);
        ConnectionDot.Classes.Set("online", indicator == ConnectionIndicator.Online);
        var text = indicator switch
        {
            ConnectionIndicator.Online => "已连接",
            ConnectionIndicator.Pending => "正在连接或写入",
            _ => "未连接"
        };
        ToolTip.SetTip(ConnectionDot, text);
        Avalonia.Automation.AutomationProperties.SetName(ConnectionDot, "连接状态：" + text);
    }

    public void SetPlatformNotice(bool visible) => PlatformNotice.IsOpen = PlatformNotice.IsVisible = visible;
    internal void SetModelDemo()
    {
        _modelDemo = true;
        AddDeviceHost.IsEnabled = false;
        PlatformNotice.IsOpen = PlatformNotice.IsVisible = false;
        UpdateHeroVisual();
    }
    public bool IsCompact { get; private set; }
    public void ExpandAllSettings()
    { foreach (var section in this.GetVisualDescendants().OfType<FASettingsExpander>()) section.IsExpanded = true; }
    internal bool SectionExpanded(string id) => _sectionExpansion.GetValueOrDefault(ExpansionKey(id), false);
    internal void RememberSectionExpansion(string id, bool expanded) => _sectionExpansion[ExpansionKey(id)] = expanded;

    private void UpdateArtwork()
    {
        var vm = ViewModel;
        VisualCatalog.Update(vm?.Devices ?? [], vm?.SelectedDevice, vm?.ProductId, vm?.DeviceColor,
            vm?.ResourceSource ?? "HeyMelody", vm?.ArtworkEpoch ?? "", vm?.ArtworkService);
        UpdateHeroVisual();
    }

    private void UpdateLayoutMode(double width)
    {
        var compact = width > 0 && width < 1080;
        if (compact == IsCompact) return;
        IsCompact = compact;
        DeviceArea.ColumnDefinitions.Clear(); DeviceArea.RowDefinitions.Clear();
        if (compact)
        {
            DeviceArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            DeviceArea.RowDefinitions.Add(new RowDefinition(new GridLength(170)));
            DeviceArea.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetColumn(ControlPane, 0); Grid.SetRow(ControlPane, 1);
            HeroPanel.Margin = new Thickness(0); HeroArt.MaxHeight = 150;
        }
        else
        {
            DeviceArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            DeviceArea.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(500)));
            Grid.SetColumn(ControlPane, 1); Grid.SetRow(ControlPane, 0);
            HeroPanel.Margin = new Thickness(8, 0, 32, 0); HeroArt.MaxHeight = 520;
        }
    }

    private async void Device_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selectingDevice || ViewModel is not { } vm) return;
        // Replacing ItemsSource can briefly clear selection. Only a different, current
        // device is a user switch; the view model owns null and canonical selections.
        var selected = DevicePicker.SelectedItem as UiDevice;
        if (vm.UpdatingState || selected is null || selected.Identity == vm.SelectedDevice?.Identity)
        { RequestDeviceSelectionSync(); return; }
        var current = vm.Devices.FirstOrDefault(device => device.Identity == selected.Identity);
        if (current is null) { RequestDeviceSelectionSync(); return; }
        _selectingDevice = true;
        try { await vm.SelectDeviceAsync(current); }
        finally { _selectingDevice = false; RequestDeviceSelectionSync(); RequestDeviceCenter(); }
    }

    private void RequestDeviceSelectionSync()
    {
        if (_deviceSelectionSyncQueued) return;
        _deviceSelectionSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _deviceSelectionSyncQueued = false;
            if (_selectingDevice) return; // The switch's finally block requests another sync.
            var vm = ViewModel;
            var selected = vm?.Devices.FirstOrDefault(device => device.Identity == vm.SelectedDevice?.Identity);
            if (ReferenceEquals(DevicePicker.SelectedItem, selected)) return;
            _selectingDevice = true;
            try { DevicePicker.SetCurrentValue(ListBox.SelectedItemProperty, selected); }
            finally { _selectingDevice = false; }
            RequestDeviceCenterIfChanged();
        }, DispatcherPriority.Loaded);
    }
    private async void FitTest_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsDemoSelected: true } demo) { demo.ShowDemoFeedback("耳塞贴合度测试"); return; }
        if (RequestFitTest is { } request) await request();
    }
    private async void HearingSetup_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsDemoSelected: true } demo) { demo.ShowDemoFeedback("设置黄金听感"); return; }
        if (RequestHearingSetup is { } request) await request();
    }

    private async void AppSettings_Click(object? sender, RoutedEventArgs e) => await OpenSettingsDialogAsync();

    private void ResetDeviceInfoCopyFeedback()
    {
        ++_deviceInfoCopyVersion;
        DeviceInfoCopyStatus.Text = "";
        UpdateDeviceInfoCopyAvailability();
    }

    private void UpdateDeviceInfoCopyAvailability() => CopyDeviceInfoButton.IsEnabled = !_copyingDeviceInfo
        && !_closingWindow && ViewModel is { Closing: false, SelectedDevice: not null };

    private async void CopyDeviceInfo_Click(object? sender, RoutedEventArgs e) => await CopyDeviceInfoAsync();

    internal async Task CopyDeviceInfoAsync()
    {
        if (_copyingDeviceInfo || _closingWindow || ViewModel is not { Closing: false, SelectedDevice: not null } vm) return;
        var text = $"设备名称：{vm.Title}{Environment.NewLine}产品编号：{vm.ProductId ?? "—"}{Environment.NewLine}固件版本：{vm.Firmware ?? "—"}";
        var version = _deviceInfoCopyVersion;
        _copyingDeviceInfo = true;
        DeviceInfoCopyStatus.Text = "";
        UpdateDeviceInfoCopyAvailability();
        try
        {
            if (WriteClipboardOverride is { } write) await write(text);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            else throw new InvalidOperationException("Clipboard unavailable.");
            if (version == _deviceInfoCopyVersion && ReferenceEquals(vm, ViewModel) && !_closingWindow && !vm.Closing)
                DeviceInfoCopyStatus.Text = "已复制";
        }
        catch (Exception)
        {
            if (version == _deviceInfoCopyVersion && ReferenceEquals(vm, ViewModel) && !_closingWindow && !vm.Closing)
                DeviceInfoCopyStatus.Text = "复制失败，请重试";
        }
        finally { _copyingDeviceInfo = false; UpdateDeviceInfoCopyAvailability(); }
    }

    private async void AddDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppressAddDeviceClick) { _suppressAddDeviceClick = false; return; }
        await OpenBluetoothSettingsAsync();
    }

    internal async Task OpenBluetoothSettingsAsync()
    {
        if (_modelDemo || !OperatingSystem.IsWindows() || _closingWindow || _openingBluetoothSettings
            || ViewModel is not { Closing: false } vm || TopLevel.GetTopLevel(this) is not { } owner) return;
        using var cancellation = new CancellationTokenSource();
        _bluetoothSettingsCancellation = cancellation;
        _openingBluetoothSettings = true;
        AddDeviceHost.IsEnabled = false;
        try
        {
            bool launched;
            try { launched = await (LauncherOverride ?? owner.Launcher).LaunchUriAsync(new Uri("ms-settings:bluetooth")); }
            catch { launched = false; }
            if (launched || _closingWindow || vm.Closing || cancellation.IsCancellationRequested) return;
            await vm.Dialogs.RunAsync(async token =>
            {
                if (_closingWindow || vm.Closing) return;
                await ShowSettingsDialogAsync(new FAContentDialog
                {
                    Title = "无法打开蓝牙设置",
                    Content = "无法打开蓝牙设置，请在 Windows 设置中打开蓝牙与设备。",
                    CloseButtonText = "确定",
                    DefaultButton = FAContentDialogButton.Close
                }, owner, token);
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || vm.Closing) { }
        finally
        {
            _bluetoothSettingsCancellation = null;
            _openingBluetoothSettings = false;
            AddDeviceHost.IsEnabled = !_modelDemo && OperatingSystem.IsWindows() && !_closingWindow;
        }
    }

    internal async Task OpenSettingsDialogAsync()
    {
        if (_closingWindow || _settingsOperation is not null || _savingResourcePreferences || ViewModel is not { Closing: false } vm) return;
        _manageDevicesRequested = false;
        _resourcePreferenceDraft = null;
        using var cancellation = new CancellationTokenSource();
        _settingsCancellation = cancellation;
        _settingsOperation = vm.Dialogs.RunAsync(ShowAppSettingsAsync, cancellation.Token);
        try { await _settingsOperation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || vm.Closing) { }
        finally { _settingsOperation = null; _settingsCancellation = null; }
        // The settings lease must be released before the EQ discard confirmation.
        if (_resourcePreferenceDraft is { } draft && !_closingWindow && !vm.Closing)
        {
            _resourcePreferenceDraft = null;
            _savingResourcePreferences = true;
            try
            {
                vm.SelectedResourceSource = draft.Source;
                vm.UseOnlineResources = draft.Online;
                vm.CloudKey = draft.Key;
                await vm.SaveResourcePreferencesAsync();
            }
            finally { _savingResourcePreferences = false; }
        }
        if (_manageDevicesRequested && !_closingWindow && !vm.Closing && ManageDevicesRequested is { } manage)
            await manage();
    }

    public async Task DismissSettingsDialogAsync()
    {
        _closingWindow = true;
        ResetDeviceInfoCopyFeedback();
        CancelAddDeviceHold();
        AddDeviceHost.IsEnabled = false;
        _bluetoothSettingsCancellation?.Cancel();
        _settingsCancellation?.Cancel();
        _settingsDialog?.Hide();
        if (_settingsOperation is { } task)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }
    public void ResumeSettingsDialogs()
    {
        _closingWindow = false;
        UpdateDeviceInfoCopyAvailability();
        AddDeviceHost.IsEnabled = !_modelDemo && OperatingSystem.IsWindows() && !_openingBluetoothSettings;
    }

    private async Task ShowAppSettingsAsync(CancellationToken token)
    {
        if (_closingWindow || TopLevel.GetTopLevel(this) is not { } owner || ViewModel is not { } vm) return;
        token.ThrowIfCancellationRequested();
        FAContentDialog? dialog = null;
        var panel = new AppSettingsPanel(vm, ThemePreferences.Load(),
            OperatingSystem.IsWindows() && ManageDevicesRequested is not null && vm.CanManageDevices,
            () => { _manageDevicesRequested = true; dialog?.Hide(); });
        dialog = new FAContentDialog { Title = "应用设置", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Primary };
        if (await ShowSettingsDialogAsync(dialog, owner, token) != FAContentDialogResult.Primary || _closingWindow || token.IsCancellationRequested) return;
        var selected = panel.SelectedTheme;
        if (panel.SelectedSource is { } resourceSource)
            _resourcePreferenceDraft = new(resourceSource, panel.UseOnlineResources, panel.CloudKey);
        if (Application.Current is App app) app.ApplyTheme(selected);
        ModelRenderingPreferences.Apply(panel.UseModelGpuAcceleration);
        if (!ThemePreferences.Save(selected, panel.UseModelGpuAcceleration))
            await ShowSettingsDialogAsync(new FAContentDialog { Title = "外观已切换", Content = "无法保存外观偏好，下次启动将使用原来的设置。", CloseButtonText = "确定" }, owner, token);

    }

    private async Task<FAContentDialogResult> ShowSettingsDialogAsync(FAContentDialog dialog, TopLevel owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _settingsDialog = dialog;
        using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Hide));
        try { return await dialog.ShowAsync(owner); }
        finally { _settingsDialog = null; }
    }
}
