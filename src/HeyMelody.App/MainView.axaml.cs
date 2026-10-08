using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using System.ComponentModel;
using Avalonia.VisualTree;

namespace HeyMelodyWindows.App;

public partial class MainView : UserControl
{
    private bool _selectingDevice;
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
            RequestDeviceCenter();
        };
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.ProductId) or nameof(MainViewModel.HasVerifiedSnapshot)
            or nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.Devices) or nameof(MainViewModel.ResourceSource)
            or nameof(MainViewModel.ResourceRevision) or nameof(MainViewModel.DeviceColor)) UpdateArtwork();
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.Devices)) RequestDeviceCenterIfChanged();
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.IsDemoSelected)) UpdateDemoPresentation();
    }

    public void SetPlatformNotice(bool visible) => PlatformNotice.IsOpen = PlatformNotice.IsVisible = visible;
    internal void SetModelDemo()
    {
        _modelDemo = true;
        AddDeviceHost.IsEnabled = false;
        PlatformNotice.IsOpen = PlatformNotice.IsVisible = false;
        HeroCaption.Text = "Enco Free4 丹拿版 · 布局演示";
        UpdateHeroVisual();
    }
    public bool IsCompact { get; private set; }
    public void ExpandAllSettings()
    { foreach (var section in this.GetVisualDescendants().OfType<FASettingsExpander>()) section.IsExpanded = true; }
    internal bool SectionExpanded(string id, bool initial) => _sectionExpansion.GetValueOrDefault(ExpansionKey(id), ViewModel?.IsDemoSelected == true || initial);
    internal void RememberSectionExpansion(string id, bool expanded) => _sectionExpansion[ExpansionKey(id)] = expanded;

    private void UpdateArtwork()
    {
        var vm = ViewModel;
        VisualCatalog.Update(vm?.Devices ?? [], vm?.SelectedDevice, vm?.ProductId, vm?.DeviceColor,
            vm?.ResourceSource ?? "HeyMelody", vm?.ResourceRevision ?? 0, vm?.ArtworkService);
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
        if (ViewModel is not { } vm) return;
        var source = new ComboBox { ItemsSource = vm.ResourceSourceOptions, SelectedItem = vm.SelectedResourceSource,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<UiOption>((option, _) => new TextBlock { Text = option?.Name }) };
        var online = new ToggleSwitch { Content = "使用在线资源", IsChecked = vm.UseOnlineResources, OnContent = "", OffContent = "" };
        var key = new TextBox { Text = vm.CloudKey, PasswordChar = '●', PlaceholderText = "自定义云端密钥", MaxLength = 1024 };
        // Keep edits local to this dialog, with a separate key for each source.
        var initialSource = vm.SelectedResourceSource;
        var initialKey = vm.CloudKey;
        var shownSource = initialSource;
        var keys = new Dictionary<int, string> { [initialSource.Key] = initialKey };
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is not UiOption next || next.Key == shownSource.Key) return;
            keys[shownSource.Key] = key.Text ?? "";
            if (!keys.TryGetValue(next.Key, out var nextKey))
            {
                nextKey = vm.ResourceKeyFor(next);
                keys[next.Key] = nextKey;
            }
            shownSource = next;
            key.Text = nextKey;
        };
        Avalonia.Automation.AutomationProperties.SetName(source, "资源来源");
        Avalonia.Automation.AutomationProperties.SetName(key, "云端资源密钥");
        content.Children.Add(new TextBlock { Text = "耳机资源", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(source); content.Children.Add(online); content.Children.Add(key);
        content.Children.Add(new TextBlock { Text = vm.ResourceStatus, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "关闭在线资源后，已缓存的较新版本仍可使用。", FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = $"HeyMelody {AppVersion.Display} · 社区客户端\n通过蓝牙管理已配对的耳机。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var dialog = new FAContentDialog { Title = "应用设置", Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Primary };
        var manageDevices = new Button { Content = "管理设备", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            IsEnabled = OperatingSystem.IsWindows() && ManageDevicesRequested is not null && ViewModel is { CanManageDevices: true } };
        manageDevices.Click += (_, _) => { _manageDevicesRequested = true; dialog.Hide(); };
        content.Children.Add(manageDevices);
        if (await ShowSettingsDialogAsync(dialog, owner, token) != FAContentDialogResult.Primary || _closingWindow || token.IsCancellationRequested) return;
        var selected = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        if (source.SelectedItem is UiOption resourceSource)
            _resourcePreferenceDraft = new(resourceSource, online.IsChecked == true, key.Text ?? "");
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
