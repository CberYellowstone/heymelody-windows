using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using System.ComponentModel;

namespace HeyMelodyWindows.App;

public partial class MainView : UserControl
{
    private bool _selectingDevice, _selectingEq;
    private MainViewModel? _observedViewModel;
    private FAContentDialog? _settingsDialog;
    private Task? _settingsOperation;
    private CancellationTokenSource? _settingsCancellation;
    private CancellationTokenSource? _bluetoothSettingsCancellation;
    private bool _openingBluetoothSettings;
    private bool _closingWindow;
    private bool _modelDemo;
    internal ILauncher? LauncherOverride { get; set; }
    internal Func<Task>? ManageDevicesRequested { get; set; }
    private bool _manageDevicesRequested;
    public DeviceVisualCatalog VisualCatalog { get; } = new();
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    public MainView()
    {
        InitializeComponent();
        InitializeDeviceBar();
        AddDeviceHost.IsEnabled = OperatingSystem.IsWindows();
        SizeChanged += (_, _) => UpdateLayoutMode(Bounds.Width);
        DataContextChanged += (_, _) =>
        {
            if (_observedViewModel is not null) _observedViewModel.PropertyChanged -= ViewModelPropertyChanged;
            VisualCatalog.Clear();
            _observedViewModel = ViewModel;
            if (ViewModel is { } vm)
            {
                vm.PropertyChanged += ViewModelPropertyChanged;
            }
            UpdateArtwork();
            RequestDeviceCenter();
        };
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.ProductId) or nameof(MainViewModel.HasVerifiedSnapshot)
            or nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.Devices)) UpdateArtwork();
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.Devices)) RequestDeviceCenterIfChanged();
    }

    public void SetPlatformNotice(bool visible) => PlatformNotice.IsOpen = PlatformNotice.IsVisible = visible;
    internal void SetModelDemo()
    {
        _modelDemo = true;
        AddDeviceHost.IsEnabled = false;
        PlatformNotice.Title = "Enco Free4 三维模型验证";
        PlatformNotice.Message = "拖动模型旋转，松手后观察惯性与阻尼；空闲时自动旋转。右侧为 X4 模拟数据，设定不会写入真实耳机。";
        PlatformNotice.IsOpen = PlatformNotice.IsVisible = true;
        HeroCaption.Text = "Enco Free4 外观 · X4 模拟控制界面";
    }
    public bool IsCompact { get; private set; }
    public void ExpandAllSettings()
    { EqualizerPanel.IsExpanded = ExperiencePanel.IsExpanded = DeviceInfoPanel.IsExpanded = true; }

    private void UpdateArtwork()
    {
        var vm = ViewModel;
        VisualCatalog.Update(vm?.Devices ?? [], vm?.SelectedDevice, vm?.ProductId);
        HeroArt.Visual = VisualCatalog.ProductFor(vm?.SelectedDevice);
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
            Grid.SetColumn(ControlScroll, 0); Grid.SetRow(ControlScroll, 1);
            HeroPanel.Margin = new Thickness(0); HeroArt.MaxHeight = 150;
            // The control header already identifies the device at narrow sizes.
            ((StackPanel)HeroPanel.Children[1]).IsVisible = false;
        }
        else
        {
            DeviceArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            DeviceArea.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(500)));
            Grid.SetColumn(ControlScroll, 1); Grid.SetRow(ControlScroll, 0);
            HeroPanel.Margin = new Thickness(8, 0, 32, 0); HeroArt.MaxHeight = 520;
            ((StackPanel)HeroPanel.Children[1]).IsVisible = true;
        }
    }

    private async void Device_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selectingDevice || ViewModel is not { } vm || vm.UpdatingState || Equals(DevicePicker.SelectedItem, vm.SelectedDevice)) return;
        _selectingDevice = true;
        try { await vm.SelectDeviceAsync(DevicePicker.SelectedItem as UiDevice); DevicePicker.SelectedItem = vm.SelectedDevice; }
        finally { _selectingDevice = false; RequestDeviceCenter(); }
    }
    private async void EqEntry_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selectingEq || ViewModel is not { } vm || vm.UpdatingState || Equals(EqEntryCombo.SelectedItem, vm.SelectedEqEntry)) return;
        _selectingEq = true;
        try { await vm.SelectEqEntryAsync(EqEntryCombo.SelectedItem as UiEqEntry); EqEntryCombo.SelectedItem = vm.SelectedEqEntry; }
        finally { _selectingEq = false; }
    }
    private async void Dual_Click(object? sender, RoutedEventArgs e)
    { if (ViewModel is { } vm) await vm.ToggleDualCommand.ExecuteAsync(DualSwitch.IsChecked == true); }
    private async void Game_Click(object? sender, RoutedEventArgs e)
    { if (ViewModel is { } vm) await vm.ToggleGameCommand.ExecuteAsync(GameSwitch.IsChecked == true); }
    private async void HiRes_Click(object? sender, RoutedEventArgs e)
    { if (ViewModel is { } vm) await vm.ToggleHiResCommand.ExecuteAsync(HiResSwitch.IsChecked == true); }

    private async void AppSettings_Click(object? sender, RoutedEventArgs e) => await OpenSettingsDialogAsync();

    private async void AddDevice_Click(object? sender, RoutedEventArgs e) => await OpenBluetoothSettingsAsync();

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
        if (_closingWindow || _settingsOperation is not null || ViewModel is not { Closing: false } vm) return;
        _manageDevicesRequested = false;
        using var cancellation = new CancellationTokenSource();
        _settingsCancellation = cancellation;
        _settingsOperation = vm.Dialogs.RunAsync(ShowAppSettingsAsync, cancellation.Token);
        try { await _settingsOperation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || vm.Closing) { }
        finally { _settingsOperation = null; _settingsCancellation = null; }
        // The settings lease must be released before the EQ discard confirmation.
        if (_manageDevicesRequested && !_closingWindow && !vm.Closing && ManageDevicesRequested is { } manage)
            await manage();
    }

    public async Task DismissSettingsDialogAsync()
    {
        _closingWindow = true;
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
        AddDeviceHost.IsEnabled = !_modelDemo && OperatingSystem.IsWindows() && !_openingBluetoothSettings;
    }

    private async Task ShowAppSettingsAsync(CancellationToken token)
    {
        if (_closingWindow || TopLevel.GetTopLevel(this) is not { } owner) return;
        token.ThrowIfCancellationRequested();
        var preference = ThemePreferences.Load();
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = preference switch { "light" => 1, "dark" => 2, _ => 0 }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "外观", FontWeight = Avalonia.Media.FontWeight.SemiBold }); content.Children.Add(theme);
        content.Children.Add(new TextBlock { Text = $"HeyMelody {AppVersion.Display} · 社区客户端\n通过蓝牙管理已配对的 OPPO 耳机。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var dialog = new FAContentDialog { Title = "应用设置", Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Primary };
        var manageDevices = new Button { Content = "管理设备", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            IsEnabled = OperatingSystem.IsWindows() && ManageDevicesRequested is not null && ViewModel is { CanManageDevices: true } };
        manageDevices.Click += (_, _) => { _manageDevicesRequested = true; dialog.Hide(); };
        content.Children.Add(manageDevices);
        if (await ShowSettingsDialogAsync(dialog, owner, token) != FAContentDialogResult.Primary || _closingWindow || token.IsCancellationRequested) return;
        var selected = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        if (Application.Current is App app) app.ApplyTheme(selected);
        if (!ThemePreferences.Save(selected))
            await ShowSettingsDialogAsync(new FAContentDialog { Title = "外观已切换", Content = "无法保存主题偏好，下次启动将使用原来的设置。", CloseButtonText = "确定" }, owner, token);

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
