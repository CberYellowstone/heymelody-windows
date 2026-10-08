using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

/// <summary>
/// Settings-card content of the application settings dialog. Edits stay local until the dialog's
/// Save button; each resource source keeps its own key draft. Re-downloading acts on the saved source.
/// </summary>
public partial class AppSettingsPanel : UserControl
{
    private readonly MainViewModel? _viewModel;
    private readonly Dictionary<int, string> _keys = [];
    private UiOption? _shownSource;
    private bool _redownloadObserved;

    public AppSettingsPanel() => InitializeComponent();

    internal AppSettingsPanel(MainViewModel viewModel, string theme, bool canManageDevices, Action manageDevices) : this()
    {
        _viewModel = viewModel;
        ThemeChoice.ItemsSource = new[] { "跟随系统", "浅色", "深色" };
        ThemeChoice.SelectedIndex = theme switch { "light" => 1, "dark" => 2, _ => 0 };
        SourceChoice.ItemTemplate = new FuncDataTemplate<UiOption>((option, _) => new TextBlock { Text = option?.Name });
        SourceChoice.ItemsSource = viewModel.ResourceSourceOptions;
        SourceChoice.SelectedItem = _shownSource = viewModel.SelectedResourceSource;
        _keys[_shownSource.Key] = viewModel.CloudKey;
        KeyBox.Text = viewModel.CloudKey;
        OnlineSwitch.IsChecked = viewModel.UseOnlineResources;
        ModelGpuSwitch.IsChecked = ModelRenderingPreferences.UseGpuAcceleration;
        SourceChoice.SelectionChanged += (_, _) => SwitchSourceKey();
        ManageDevicesButton.IsEnabled = canManageDevices;
        ManageDevicesButton.Click += (_, _) => manageDevices();
        RedownloadButton.Click += async (_, _) => { _redownloadObserved = true; await viewModel.RedownloadResourcesAsync(); };
        RedownloadResult.Closed += (_, _) => RedownloadResult.IsVisible = false;
        AboutCard.Content = "HeyMelody " + AppVersion.Display;
        Present();
    }

    internal string SelectedTheme => ThemeChoice.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
    internal UiOption? SelectedSource => SourceChoice.SelectedItem as UiOption;
    internal bool UseOnlineResources => OnlineSwitch.IsChecked == true;
    internal bool UseModelGpuAcceleration => ModelGpuSwitch.IsChecked == true;
    internal string CloudKey => KeyBox.Text ?? "";

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

    private void SwitchSourceKey()
    {
        if (_viewModel is null || SourceChoice.SelectedItem is not UiOption next || _shownSource is null || next.Key == _shownSource.Key) return;
        _keys[_shownSource.Key] = KeyBox.Text ?? "";
        if (!_keys.TryGetValue(next.Key, out var key)) _keys[next.Key] = key = _viewModel.ResourceKeyFor(next);
        _shownSource = next;
        KeyBox.Text = key;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is null or nameof(MainViewModel.IsRedownloadingResources) or nameof(MainViewModel.CanRedownloadResources)
            or nameof(MainViewModel.RedownloadMessage) or nameof(MainViewModel.ResourceStatus) or nameof(MainViewModel.ConfiguredResourceSourceName)
            or nameof(MainViewModel.OnlineResourcesConfigured))
            Present();
    }

    private void Present()
    {
        if (_viewModel is not { } vm) return;
        bool running = vm.IsRedownloadingResources;
        RedownloadProgress.IsVisible = running;
        RedownloadButton.IsEnabled = vm.CanRedownloadResources;
        RedownloadCard.Description = running ? "正在重新下载，可以关闭此窗口，完成后结果会写入操作记录。"
            : !vm.ResourceServiceAvailable ? "当前模式不连接资源服务，无法重新下载。"
            : !vm.OnlineResourcesConfigured ? "需要开启并保存“使用在线资源”后才能重新下载。"
            : $"重新获取“{vm.ConfiguredResourceSourceName}”的配置与美术资源；新文件校验通过后才替换本机缓存。";
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
