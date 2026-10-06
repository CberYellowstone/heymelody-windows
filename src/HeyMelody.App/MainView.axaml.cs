using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private bool _closingWindow;
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode(Bounds.Width);
        DataContextChanged += (_, _) =>
        {
            if (_observedViewModel is not null) _observedViewModel.PropertyChanged -= ViewModelPropertyChanged;
            _observedViewModel = ViewModel;
            if (ViewModel is { } vm)
            {
                vm.PropertyChanged += ViewModelPropertyChanged;
            }
            UpdateArtwork();
        };
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (string.IsNullOrEmpty(change.PropertyName) || change.PropertyName is nameof(MainViewModel.ProductId) or nameof(MainViewModel.HasVerifiedSnapshot)) UpdateArtwork();
    }

    public void SetPlatformNotice(bool visible) => PlatformNotice.IsOpen = PlatformNotice.IsVisible = visible;
    public bool IsCompact { get; private set; }
    public void ExpandAllSettings()
    { EqualizerPanel.IsExpanded = ExperiencePanel.IsExpanded = DeviceInfoPanel.IsExpanded = true; }

    private void UpdateArtwork() => HeroArt.ShowX4 = ViewModel is { HasVerifiedSnapshot: true, ProductId: "06EC10" };

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
        finally { _selectingDevice = false; }
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

    internal async Task OpenSettingsDialogAsync()
    {
        if (_closingWindow || _settingsOperation is not null || ViewModel is not { Closing: false } vm) return;
        using var cancellation = new CancellationTokenSource();
        _settingsCancellation = cancellation;
        _settingsOperation = vm.Dialogs.RunAsync(ShowAppSettingsAsync, cancellation.Token);
        try { await _settingsOperation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || vm.Closing) { }
        finally { _settingsOperation = null; _settingsCancellation = null; }
    }

    public async Task DismissSettingsDialogAsync()
    {
        _closingWindow = true;
        _settingsCancellation?.Cancel();
        _settingsDialog?.Hide();
        if (_settingsOperation is { } task)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }
    public void ResumeSettingsDialogs() => _closingWindow = false;

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
