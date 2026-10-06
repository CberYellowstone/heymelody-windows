using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HeyMelodyWindows.App;

public partial class MainWindow : Window
{
    private static readonly int[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private readonly IHeadsetService _service;
    private readonly bool _preview;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly Slider[] _sliders = new Slider[10];
    private readonly TextBlock[] _gainLabels = new TextBlock[10];
    private readonly List<string> _logs = [];
    private UiSnapshot? _snapshot;
    private string? _sessionIdentity;
    private string? _selectedIdentity;
    private UiEqEntry? _editingEntry;
    private bool _busy, _displaying, _eqDirty, _pendingAnc, _pendingPreset, _pendingSpatial, _closing, _closed;

    public MainWindow(IHeadsetService service, bool preview = false)
    {
        _service = service; _preview = preview;
        InitializeComponent(); BuildBands();
        AncCombo.SelectionChanged += (_, _) => { if (!_displaying) _pendingAnc = true; };
        PresetCombo.SelectionChanged += (_, _) => { if (!_displaying) _pendingPreset = true; };
        SpatialCombo.SelectionChanged += (_, _) => { if (!_displaying) _pendingSpatial = true; };
        _poll.Tick += async (_, _) => { if (!_busy && _sessionIdentity is not null && !_closing) await RefreshStateAsync(); };
        Loaded += async (_, _) => { if (!_preview) { await InitializeAsync(); _poll.Start(); } };
        Closing += OnClosing;
        UpdateEnabled();
    }

    public async Task InitializeAsync()
    {
        await EnumerateAsync();
        if (_preview && DeviceList.SelectedItem is UiDevice device) await ConnectAsync(device);
    }
    public bool HasVerifiedSnapshot => _snapshot is { Connected: true, SupportedModel: true, Failure: null } && _snapshot.Identity == _sessionIdentity;

    private void BuildBands()
    {
        for (var i = 0; i < Frequencies.Length; i++)
        {
            BandsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = "0 dB", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Muted") };
            var slider = new Slider { Orientation = Orientation.Vertical, Minimum = -6, Maximum = 6, Value = 0, Height = 115, Width = 30, Margin = new Thickness(0, 8, 0, 5), TickPlacement = System.Windows.Controls.Primitives.TickPlacement.None };
            System.Windows.Automation.AutomationProperties.SetName(slider, $"{Frequencies[i]} Hz 增益");
            var index = i;
            slider.ValueChanged += (_, _) => { _gainLabels[index].Text = $"{(int)slider.Value:+0;-0;0} dB"; if (!_displaying) SetEqDirty(true); };
            _sliders[i] = slider; _gainLabels[i] = label;
            column.Children.Add(label); column.Children.Add(slider);
            column.Children.Add(new TextBlock { Text = Frequencies[i] >= 1000 ? $"{Frequencies[i] / 1000}k" : Frequencies[i].ToString(), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(column, i); BandsGrid.Children.Add(column);
        }
    }

    private async Task RunAsync(string status, Func<Task> action, bool silent = false)
    {
        if (_closing || !await _operation.WaitAsync(0)) return;
        _busy = true; UpdateEnabled(); if (!silent) SetStatus(status);
        try { await action(); }
        catch (OperationCanceledException) { if (!_closing) { InvalidateSnapshot(); SetStatus("操作已取消，请刷新状态后重试。", true); } }
        catch { if (!_closing) { InvalidateSnapshot(); SetStatus("无法读取耳机，请确认蓝牙连接后重试。", true); Log("通信未完成，未自动重发设置。"); } }
        finally { _busy = false; _operation.Release(); if (!_closed) UpdateEnabled(); }
    }

    private async Task EnumerateAsync()
    {
        await RunAsync("正在查找已配对耳机…", async () =>
        {
            var devices = await _service.EnumerateAsync(_lifetime.Token);
            _displaying = true;
            DeviceList.ItemsSource = devices;
            DeviceList.SelectedItem = devices.FirstOrDefault(d => d.Identity == _selectedIdentity) ?? devices.FirstOrDefault();
            _displaying = false;
            var selected = DeviceList.SelectedItem as UiDevice;
            if (_selectedIdentity != selected?.Identity) { _selectedIdentity = selected?.Identity; _sessionIdentity = null; ResetSelection(); }
            DeviceTitle.Text = selected?.Name ?? "选择一副耳机";
            NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(devices.Count == 0 ? "未发现已配对耳机" : _snapshot is { Connected: true } ? "设备列表已刷新 · 耳机保持连接" : "选择耳机后连接");
        });
    }

    private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_displaying || _busy || _closing) return;
        var selected = DeviceList.SelectedItem as UiDevice;
        if (selected?.Identity == _selectedIdentity) return;
        if (_eqDirty && MessageBox.Show(this, "切换耳机会丢弃尚未保存的均衡器调整。继续切换吗？", "切换耳机", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            _displaying = true; DeviceList.SelectedItem = DeviceList.Items.Cast<UiDevice>().FirstOrDefault(d => d.Identity == _selectedIdentity); _displaying = false; return;
        }
        var hadSession = _sessionIdentity is not null;
        _selectedIdentity = selected?.Identity;
        _sessionIdentity = null; ResetSelection(); DeviceTitle.Text = selected?.Name ?? "选择一副耳机";
        if (hadSession) await RunAsync("正在结束上一次连接…", () => _service.DisconnectAsync(_lifetime.Token));
        SetStatus("选择耳机后连接");
    }

    private async Task ConnectAsync(UiDevice device)
    {
        await RunAsync("正在连接耳机并读取状态…", async () =>
        {
            var snapshot = await _service.ConnectAsync(device.Identity, _lifetime.Token);
            if (snapshot.Identity != device.Identity) throw new InvalidOperationException();
            _sessionIdentity = device.Identity;
            DisplaySnapshot(snapshot);
            SetStatus(snapshot.Failure is null && snapshot.Connected ? "已连接 · 状态会自动刷新" : "连接尚未就绪，请检查耳机状态。", snapshot.Failure is not null);
            Log(snapshot.Failure is null && snapshot.Connected ? "已连接耳机，读取当前设置。" : "连接尚未就绪，设置暂不可用。");
        });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionIdentity is not null)
        {
            await RunAsync("正在断开客户端连接…", async () => { await _service.DisconnectAsync(_lifetime.Token); _sessionIdentity = null; ResetSelection(); SetStatus("客户端已断开 · 蓝牙配对保留"); Log("已断开客户端连接。"); });
        }
        else if (DeviceList.SelectedItem is UiDevice device) await ConnectAsync(device);
    }
    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_eqDirty && MessageBox.Show(this, "刷新设备后，若连接变化，未保存的均衡器调整会被丢弃。继续刷新吗？", "刷新设备", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await EnumerateAsync();
    }
    private async void RefreshCurrent_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();
    private async Task RefreshStateAsync() => await RunAsync("正在刷新状态…", async () =>
    {
        var snapshot = await _service.RefreshAsync(_lifetime.Token);
        if (snapshot.Identity != _sessionIdentity) throw new InvalidOperationException();
        DisplaySnapshot(snapshot);
        if (snapshot.Failure is not null || !snapshot.Connected) SetStatus("耳机状态未确认，暂时不能修改设置。", true);
    }, silent: true);

    private void DisplaySnapshot(UiSnapshot snapshot)
    {
        if (snapshot.Failure is not null || !snapshot.Connected) { InvalidateSnapshot(); return; }
        _snapshot = snapshot; _displaying = true;
        DeviceTitle.Text = snapshot.DeviceName;
        DeviceSubtitle.Text = snapshot.SupportedModel ? $"已连接{(snapshot.Firmware is null ? "" : $" · 固件 {snapshot.Firmware}")}" : "该型号的设置尚未适配";
        LeftBatteryText.Text = Battery(snapshot.LeftBattery); RightBatteryText.Text = Battery(snapshot.RightBattery); CaseBatteryText.Text = Battery(snapshot.CaseBattery);
        LeftChargeText.Text = Charge(snapshot.LeftCharging, snapshot.LeftBattery); RightChargeText.Text = Charge(snapshot.RightCharging, snapshot.RightBattery); CaseChargeText.Text = Charge(snapshot.CaseCharging, snapshot.CaseBattery);
        BindOptions(AncCombo, snapshot.AncOptions, snapshot.Anc, _pendingAnc);
        BindOptions(PresetCombo, snapshot.EqualizerOptions, snapshot.Equalizer, _pendingPreset);
        BindOptions(SpatialCombo, snapshot.SpatialOptions, snapshot.Spatial, _pendingSpatial);
        DualSwitch.IsChecked = snapshot.DualConnection == true; GameSwitch.IsChecked = snapshot.GameMode == true; HiResSwitch.IsChecked = snapshot.HiRes == true;
        DualStateText.Text = BooleanState(snapshot.DualConnection); GameStateText.Text = BooleanState(snapshot.GameMode); HiResStateText.Text = BooleanState(snapshot.HiRes);
        MutexText.Visibility = snapshot.SpatialHiResMutex ? Visibility.Visible : Visibility.Collapsed;
        if (!_eqDirty)
        {
            var selectedId = (EqEntryCombo.SelectedItem as UiEqEntry)?.Id;
            EqEntryCombo.ItemsSource = snapshot.EqualizerEntries;
            var entry = snapshot.EqualizerEntries.FirstOrDefault(x => x.Id == selectedId) ?? snapshot.EqualizerEntries.FirstOrDefault(x => x.Selected) ?? snapshot.EqualizerEntries.FirstOrDefault();
            EqEntryCombo.SelectedItem = entry; LoadEntry(entry);
        }
        _displaying = false; UpdateEnabled();
    }

    private static void BindOptions(ComboBox combo, IReadOnlyList<UiOption> options, int? current, bool pending)
    {
        var key = pending && combo.SelectedItem is UiOption choice ? choice.Key : current;
        combo.ItemsSource = options; combo.SelectedItem = key is int k ? options.FirstOrDefault(x => x.Key == k) : null;
    }
    private static string Battery(int? value) => value is >= 0 and <= 100 ? $"{value}%" : "—";
    private static string Charge(bool? charging, int? battery) => charging == true ? "正在充电" : battery is null ? "尚未读取" : charging == false ? "未在充电" : "充电状态未确认";
    private static string BooleanState(bool? value) => value is null ? "未确认" : value.Value ? "已开启" : "已关闭";

    private void InvalidateSnapshot()
    {
        _snapshot = null; _displaying = true;
        LeftBatteryText.Text = RightBatteryText.Text = CaseBatteryText.Text = "—";
        LeftChargeText.Text = RightChargeText.Text = CaseChargeText.Text = "尚未读取";
        AncCombo.ItemsSource = PresetCombo.ItemsSource = SpatialCombo.ItemsSource = null;
        DualSwitch.IsChecked = GameSwitch.IsChecked = HiResSwitch.IsChecked = false;
        DualStateText.Text = GameStateText.Text = HiResStateText.Text = "未确认";
        DeviceSubtitle.Text = "当前状态未确认，刷新成功后才可修改";
        _displaying = false; UpdateEnabled();
    }
    private void ResetSelection()
    {
        _pendingAnc = _pendingPreset = _pendingSpatial = false; _eqDirty = false; _editingEntry = null;
        _displaying = true; EqEntryCombo.ItemsSource = null; LoadEntry(null); _displaying = false;
        InvalidateSnapshot(); EqDirtyText.Text = "";
    }
    private void UpdateEnabled()
    {
        var valid = !_busy && _snapshot is { Connected: true, SupportedModel: true, Failure: null } && _snapshot.Identity == _sessionIdentity && _sessionIdentity == _selectedIdentity;
        DeviceList.IsEnabled = RefreshDevicesButton.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !_busy && DeviceList.SelectedItem is UiDevice;
        StateRefreshButton.IsEnabled = !_busy && _sessionIdentity is not null;
        ConnectButton.Content = _sessionIdentity is null ? "连接耳机" : "断开客户端";
        AncCombo.IsEnabled = AncApplyButton.IsEnabled = valid && _snapshot!.CanSetAnc;
        PresetCombo.IsEnabled = PresetApplyButton.IsEnabled = valid && _snapshot!.CanSetEqualizer;
        SpatialCombo.IsEnabled = SpatialApplyButton.IsEnabled = valid && _snapshot!.CanSetSpatial;
        DualSwitch.IsEnabled = valid && _snapshot!.CanSetDualConnection && _snapshot.DualConnection is not null;
        GameSwitch.IsEnabled = valid && _snapshot!.CanSetGameMode && _snapshot.GameMode is not null;
        HiResSwitch.IsEnabled = valid && _snapshot!.CanSetHiRes && _snapshot.HiRes is not null;
        var canEq = valid && _snapshot!.CanSetCustomEqualizer;
        EqEntryCombo.IsEnabled = EqNameBox.IsEnabled = NewEqButton.IsEnabled = ResetEqButton.IsEnabled = canEq;
        EqApplyButton.IsEnabled = canEq && _eqDirty;
        UseEqButton.IsEnabled = canEq && !_eqDirty && _editingEntry is { Id: > 0 };
        foreach (var slider in _sliders) slider.IsEnabled = canEq;
        EqHint.Text = _snapshot?.CanSetCustomEqualizer == true ? "调节各频段，然后将整组设置写入耳机。" : "连接支持自定义 EQ 的耳机后可编辑。";
    }

    private async Task ApplyAsync(UiAction action)
    {
        if (_snapshot is null || _snapshot.Identity != _sessionIdentity || _sessionIdentity != _selectedIdentity) return;
        var snapshot = _snapshot;
        var needsMutex = snapshot.SpatialHiResMutex && ((action.Kind == "Spatial" && action.Value != 0 && snapshot.HiRes == true) || (action.Kind == "HiRes" && action.Value == 1 && snapshot.Spatial is > 0));
        if (needsMutex)
        {
            var message = action.Kind == "Spatial" ? "开启空间音频需要先关闭 Hi-Res，音频连接可能短暂中断。继续吗？" : "开启 Hi-Res 需要先关闭空间音频，音频连接可能短暂中断。继续吗？";
            if (MessageBox.Show(this, message, "切换音频模式", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { DisplaySnapshot(snapshot); return; }
            action = action with { AllowMutexChange = true };
        }
        await RunAsync("正在应用设置并回读…", async () =>
        {
            var result = await _service.ApplyAsync(action, _lifetime.Token);
            if (action.Kind == "Anc") _pendingAnc = false;
            if (action.Kind == "Equalizer") _pendingPreset = false;
            if (action.Kind == "Spatial") _pendingSpatial = false;
            var reachedTarget = result.ReadbackMatches == true && (result.Accepted || !result.SubmissionAttempted) && !result.PartialEffects && !result.AcknowledgementUnknown;
            if (action.Kind == "CustomEqualizer" && reachedTarget) SetEqDirty(false);
            if (result.After is not null && result.After.Identity == _sessionIdentity) DisplaySnapshot(result.After); else InvalidateSnapshot();
            var message = result.PartialEffects ? "部分操作已提交，请核对当前状态。" : result.AcknowledgementUnknown ? "设置响应未确认，请刷新状态；不会自动重发。" : reachedTarget ? result.Accepted ? "设置已接受，回读与请求一致。" : "耳机已处于所选设置。" : result.Accepted ? "设置已接受，当前状态尚未确认。" : result.SubmissionAttempted ? "设置未完成，请核对当前状态；不会自动重发。" : "设置未发送，请刷新状态后重试。";
            SetStatus(message, !reachedTarget); Log(message);
            if (!string.IsNullOrWhiteSpace(result.Message)) Log(Sanitize(result.Message));
        });
    }
    private async void AncApply_Click(object sender, RoutedEventArgs e) { if (AncCombo.SelectedItem is UiOption option) await ApplyAsync(new("Anc", option.Key)); }
    private async void PresetApply_Click(object sender, RoutedEventArgs e) { if (PresetCombo.SelectedItem is UiOption option) await ApplyAsync(new("Equalizer", option.Key)); }
    private async void SpatialApply_Click(object sender, RoutedEventArgs e) { if (SpatialCombo.SelectedItem is UiOption option) await ApplyAsync(new("Spatial", option.Key)); }
    private async void DualSwitch_Click(object sender, RoutedEventArgs e) => await ApplyAsync(new("DualConnection", DualSwitch.IsChecked == true ? 1 : 0));
    private async void GameSwitch_Click(object sender, RoutedEventArgs e) => await ApplyAsync(new("GameMode", GameSwitch.IsChecked == true ? 1 : 0));
    private async void HiResSwitch_Click(object sender, RoutedEventArgs e) => await ApplyAsync(new("HiRes", HiResSwitch.IsChecked == true ? 1 : 0));

    private void LoadEntry(UiEqEntry? entry)
    {
        var wasDisplaying = _displaying; _displaying = true;
        _editingEntry = entry; EqNameBox.Text = entry?.Name ?? "我的均衡器"; EqNameBox.MaxLength = 30;
        for (var i = 0; i < Frequencies.Length; i++)
        {
            _sliders[i].Minimum = entry?.MinimumGain ?? -6; _sliders[i].Maximum = entry?.MaximumGain ?? 6;
            _sliders[i].Value = entry?.Bands.FirstOrDefault(b => b.FrequencyHz == Frequencies[i])?.Gain ?? 0;
        }
        SetEqDirty(false); _displaying = wasDisplaying;
    }
    private void SetEqDirty(bool dirty) { _eqDirty = dirty; EqDirtyText.Text = dirty ? "有未保存的调整" : ""; if (_sliders[9] is not null) UpdateEnabled(); }
    private void EqName_TextChanged(object sender, TextChangedEventArgs e) { if (!_displaying && IsLoaded) SetEqDirty(true); }
    private void EqEntry_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_displaying) return;
        if (_eqDirty && MessageBox.Show(this, "要丢弃尚未保存的均衡器调整吗？", "切换均衡器", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        { _displaying = true; EqEntryCombo.SelectedItem = _editingEntry; _displaying = false; return; }
        LoadEntry(EqEntryCombo.SelectedItem as UiEqEntry);
    }
    private void NewEq_Click(object sender, RoutedEventArgs e)
    {
        if (_eqDirty && MessageBox.Show(this, "新建均衡器会丢弃尚未保存的调整。继续吗？", "新建均衡器", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _displaying = true; EqEntryCombo.SelectedItem = null; LoadEntry(null); _displaying = false; SetEqDirty(true);
    }
    private void ResetEq_Click(object sender, RoutedEventArgs e) => LoadEntry(_editingEntry);
    private async void UseEq_Click(object sender, RoutedEventArgs e)
    {
        if (_editingEntry is { Id: > 0 } entry && !_eqDirty) await ApplyAsync(new("CustomSelect", entry.Id));
    }
    private async void EqApply_Click(object sender, RoutedEventArgs e)
    {
        var name = EqNameBox.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) { SetStatus("请输入有效的均衡器名称。", true); return; }
        var entry = new UiEqEntry(_editingEntry?.Id ?? 0, name, (int)_sliders[0].Minimum, (int)_sliders[0].Maximum, _editingEntry?.Selected ?? false, Frequencies.Select((hz, i) => new UiBand(hz, (int)_sliders[i].Value)).ToArray());
        await ApplyAsync(new("CustomEqualizer", entry.Id, entry));
    }
    private void SetStatus(string text, bool warning = false) { StatusText.Text = text; StatusText.ToolTip = text; StatusDot.Fill = new SolidColorBrush(warning ? Color.FromRgb(176, 124, 54) : Color.FromRgb(32, 133, 140)); }
    private void Log(string text) { _logs.Add($"{DateTime.Now:HH:mm:ss}  {Sanitize(text)}"); if (_logs.Count > 80) _logs.RemoveAt(0); LogBox.Text = string.Join(Environment.NewLine, _logs); LogBox.ScrollToEnd(); }
    private static string Sanitize(string text) { var clean = Regex.Replace(text, @"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b[0-9a-f]{12}\b|(?:BTHENUM|BTHLEDEVICE|SWD)\\\S+", "[设备标识]"); return string.Concat(clean.Where(c => !char.IsControl(c))).Trim(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        if (_preview) { _closed = true; _closing = true; _poll.Stop(); _lifetime.Cancel(); return; }
        e.Cancel = true;
        if (_closing) return;
        if (_eqDirty && !_preview && MessageBox.Show(this, "均衡器还有未保存的调整，确定关闭吗？", "关闭客户端", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _closing = true; _poll.Stop(); _lifetime.Cancel();
        try { await _service.DisposeAsync(); } catch { }
        _closed = true; _ = Dispatcher.BeginInvoke((Action)Close);
    }
}
