using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

/// <summary>
/// Settings-card content of the application settings dialog. Edits stay local until the dialog's
/// Save button. Re-downloading acts on saved resource preferences.
/// </summary>
public partial class AppSettingsPanel : UserControl
{
    private readonly MainViewModel? _viewModel;
    private bool _redownloadObserved;
    private readonly bool _canManageDevices;
    private static readonly ModelFrameRate[] FrameRates = [ModelFrameRate.Display, ModelFrameRate.Fps60, ModelFrameRate.Fps30];

    public AppSettingsPanel() => InitializeComponent();

    internal AppSettingsPanel(MainViewModel viewModel, string theme, bool canManageDevices, Action manageDevices) : this()
    {
        _viewModel = viewModel;
        ThemeChoice.ItemsSource = new[] { "跟随系统", "浅色", "深色" };
        ThemeChoice.SelectedIndex = theme switch { "light" => 1, "dark" => 2, _ => 0 };
        // The theme previews at once; the owner restores the saved theme when the dialog is cancelled.
        ThemeChoice.SelectionChanged += (_, _) => ThemePreviewChanged?.Invoke(SelectedTheme);
        ModelFrameRateChoice.ItemsSource = new[] { "跟随显示器", "60 帧", "30 帧" };
        ModelFrameRateChoice.SelectedIndex = Math.Max(0, Array.IndexOf(FrameRates, ModelRenderingPreferences.FrameRate));
        KeyBox.Text = viewModel.CloudKey;
        OnlineSwitch.IsChecked = viewModel.UseOnlineResources;
        ModelGpuSwitch.IsChecked = ModelRenderingPreferences.UseGpuAcceleration;
        _canManageDevices = canManageDevices;
        ManageDevicesButton.Click += (_, _) => manageDevices();
        RedownloadButton.Click += async (_, _) => { _redownloadObserved = true; await viewModel.RedownloadResourcesAsync(); };
        RedownloadResult.Closed += (_, _) => RedownloadResult.IsVisible = false;
        AboutCard.Content = "HeyMelody " + AppVersion.Display;
        Present();
    }

    internal string SelectedTheme => ThemeChoice.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
    internal bool UseOnlineResources => OnlineSwitch.IsChecked == true;
    internal bool UseModelGpuAcceleration => ModelGpuSwitch.IsChecked == true;
    internal ModelFrameRate ModelFrameRate => FrameRates[Math.Clamp(ModelFrameRateChoice.SelectedIndex, 0, FrameRates.Length - 1)];
    internal string CloudKey => KeyBox.Text ?? "";
    /// <summary>Resource rows accept edits only while no connection or device switch is in progress.</summary>
    internal bool ResourcesEditable => _viewModel?.CanManageDevices == true;
    internal event Action<string>? ThemePreviewChanged;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is not null) { _viewModel.PropertyChanged += ViewModelChanged; Present(); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is null or nameof(MainViewModel.IsRedownloadingResources) or nameof(MainViewModel.CanRedownloadResources)
            or nameof(MainViewModel.RedownloadMessage) or nameof(MainViewModel.ResourceStatus)
            or nameof(MainViewModel.OnlineResourcesConfigured) or nameof(MainViewModel.CanManageDevices))
            Present();
    }

    private void Present()
    {
        if (_viewModel is not { } vm) return;
        // Appearance stays editable; resource and device rows wait for a connection or switch to finish.
        var editable = vm.CanManageDevices;
        ResourceLockNotice.IsVisible = !editable;
        OnlineSwitch.IsEnabled = KeyBox.IsEnabled = editable;
        ManageDevicesButton.IsEnabled = editable && _canManageDevices;
        bool running = vm.IsRedownloadingResources;
        RedownloadProgress.IsVisible = RedownloadProgress.IsIndeterminate = running;
        RedownloadButton.IsEnabled = vm.CanRedownloadResources;
        RedownloadCard.Description = running ? "正在重新下载，可以关闭此窗口，完成后结果会写入操作记录。"
            : !vm.ResourceServiceAvailable ? "当前模式不连接资源服务，无法重新下载。"
            : !vm.OnlineResourcesConfigured ? "需要开启并保存“使用在线资源”后才能重新下载。"
            : "重新获取欢律的配置与美术资源；新文件校验通过后才替换本机缓存。";
        ResourceStatusText.Text = vm.ResourceStatus;
        ResourceStatusText.IsVisible = vm.ResourceStatus.Length != 0;
        if (running) { _redownloadObserved = true; RedownloadResult.IsOpen = false; RedownloadResult.IsVisible = false; return; }
        // Only results of a run seen by this dialog are shown; older ones live in the operation log.
        if (!_redownloadObserved || vm.RedownloadMessage.Length == 0) return;
        RedownloadResult.Severity = vm.RedownloadFailed ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success;
        RedownloadResult.Title = vm.RedownloadFailed ? "重新下载未全部完成" : "重新下载完成";
        RedownloadResult.Message = vm.RedownloadMessage;
        RedownloadResult.IsVisible = RedownloadResult.IsOpen = true;
    }
}
