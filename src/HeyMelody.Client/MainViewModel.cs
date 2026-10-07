using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HeyMelodyWindows.App;

public sealed class EqBandViewModel : ObservableObject
{
    private int _gain, _minimum = -6, _maximum = 6;
    public EqBandViewModel(int frequencyHz) => FrequencyHz = frequencyHz;
    public int FrequencyHz { get; }
    public string FrequencyLabel => FrequencyHz >= 1000 ? $"{FrequencyHz / 1000}k" : FrequencyHz.ToString();
    public int Gain
    {
        get => _gain;
        set { if (SetProperty(ref _gain, Math.Clamp(value, Minimum, Maximum))) OnPropertyChanged(nameof(GainLabel)); }
    }
    public string GainLabel => $"{Gain:+0;-0;0} dB";
    public int Minimum { get => _minimum; internal set => SetProperty(ref _minimum, value); }
    public int Maximum { get => _maximum; internal set => SetProperty(ref _maximum, value); }
}

/// <summary>UI-independent editing, prioritized device work and bounded user-authorized retries.</summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly int[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private static readonly string[] PresentationProperties = typeof(MainViewModel).GetProperties()
        .Where(p => !p.Name.EndsWith("Command", StringComparison.Ordinal)).Select(p => p.Name).ToArray();
    private readonly IHeadsetService _service;
    private readonly IConfirmationService _confirmations;
    private readonly bool _preview;
    private readonly OperationCoordinator _operations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _logs = [];
    private readonly List<IRelayCommand> _commands = [];
    private readonly Dictionary<string, object?> _published = [];
    private readonly Dictionary<string, PendingAction> _pending = [];
    private readonly Dictionary<string, string> _actionWarnings = [];
    private CancellationTokenSource _sessionWork = new();
    private long _sessionEpoch, _ancVersion, _presetVersion, _spatialVersion, _eqVersion, _draftIdentity;
    private int? _eqDraftId;
    private bool _refreshing, _lifecyclePending, _closeDialogRunning;
    private Task? _refreshTask;
    private UiSnapshot? _snapshot;
    private string? _sessionIdentity;
    private UiDevice? _selectedDevice;
    private UiEqEntry? _selectedEqEntry;
    private UiOption? _selectedAnc, _selectedPreset, _selectedSpatial;
    private IReadOnlyList<UiDevice> _devices = [];
    private IReadOnlyList<UiOption> _ancOptions = [], _presetOptions = [], _spatialOptions = [];
    private IReadOnlyList<UiEqEntry> _eqEntries = [];
    private string _title = "选择一副耳机", _subtitle = "连接耳机后查看设备状态", _status = "选择耳机后连接", _eqName = "我的均衡器";
    private bool _displaying, _eqDirty, _pendingAnc, _pendingPreset, _pendingSpatial, _closing, _closeRequested, _statusWarning;
    private Task? _disposeTask;

    public MainViewModel(IHeadsetService service, IConfirmationService confirmations, bool preview = false)
    {
        _service = service; _confirmations = confirmations; _preview = preview;
        Dialogs = new(_lifetime.Token);
        Bands = new(Frequencies.Select(hz => new EqBandViewModel(hz)));
        foreach (var band in Bands) band.PropertyChanged += BandChanged;
        RefreshDevicesCommand = Register(new AsyncRelayCommand(RefreshDevicesAsync, () => CanRefreshDevices));
        ConnectCommand = Register(new AsyncRelayCommand(ConnectOrDisconnectAsync, () => CanConnect));
        RefreshStateCommand = Register(new AsyncRelayCommand(RefreshStateAsync, () => CanRefreshState));
        ApplyAncCommand = Register(new AsyncRelayCommand(() => ApplyOptionAsync("Anc", SelectedAnc), () => CanSetAnc && SelectedAnc is not null));
        ApplyPresetCommand = Register(new AsyncRelayCommand(() => ApplyOptionAsync("Equalizer", SelectedPreset), () => CanSetEqualizer && SelectedPreset is not null));
        ApplySpatialCommand = Register(new AsyncRelayCommand(() => ApplyOptionAsync("Spatial", SelectedSpatial), () => CanSetSpatial && SelectedSpatial is not null));
        ToggleDualCommand = Register(new AsyncRelayCommand<bool>(value => ApplyAsync(new("DualConnection", value ? 1 : 0)), _ => CanSetDualConnection));
        ToggleGameCommand = Register(new AsyncRelayCommand<bool>(value => ApplyAsync(new("GameMode", value ? 1 : 0)), _ => CanSetGameMode));
        ToggleHiResCommand = Register(new AsyncRelayCommand<bool>(value => ApplyAsync(new("HiRes", value ? 1 : 0)), _ => CanSetHiRes));
        NewEqCommand = Register(new AsyncRelayCommand(NewEqAsync, () => CanEditEq));
        ResetEqCommand = Register(new RelayCommand(() => LoadEntry(SelectedEqEntry), () => CanEditEq));
        SaveEqCommand = Register(new AsyncRelayCommand(SaveEqAsync, () => CanSetCustomEqualizer && EqDirty));
        UseEqCommand = Register(new AsyncRelayCommand(UseEqAsync, () => CanSetCustomEqualizer && !EqDirty && SelectedEqEntry is { Id: > 0 }));
    }

    public IReadOnlyList<UiDevice> Devices => _devices;
    public UiDevice? SelectedDevice => _selectedDevice;
    public bool HasDevices => Devices.Count != 0;
    public string Title => _title;
    public string Subtitle => _subtitle;
    public string Status => _actionWarnings.Count == 0 ? _status : string.Join(Environment.NewLine, _actionWarnings.Values);
    public bool StatusWarning => _statusWarning || _actionWarnings.Count != 0;
    public bool Busy => _lifecyclePending || _pending.Count != 0;
    public bool IsRefreshing => _refreshing;
    public bool HasActivity => Busy || IsRefreshing;
    public bool Closing => _closing || _closeRequested;
    public bool UpdatingState => _displaying;
    public DialogCoordinator Dialogs { get; }
    public bool HasVerifiedSnapshot => _snapshot is { Connected: true, SupportedModel: true, Failure: null }
        && _snapshot.Identity == _sessionIdentity && _sessionIdentity == SelectedDevice?.Identity;
    private bool CanWrite => HasVerifiedSnapshot && !Closing && !_lifecyclePending;
    public bool CanConnect => !Closing && !_lifecyclePending && SelectedDevice is not null;
    public bool CanRefreshDevices => !Closing && !_lifecyclePending;
    public bool CanRefreshState => !Closing && !_lifecyclePending && _sessionIdentity is not null;
    public string ConnectButtonText => _sessionIdentity is null ? "连接耳机" : "断开客户端";
    public bool CanChooseAnc => CanWrite && _snapshot!.CanSetAnc;
    public bool CanChoosePreset => CanWrite && _snapshot!.CanSetEqualizer;
    public bool CanChooseSpatial => CanWrite && _snapshot!.CanSetSpatial;
    public bool CanSetAnc => CanChooseAnc && !_pending.ContainsKey("Anc");
    public bool CanSetEqualizer => CanChoosePreset && !_pending.ContainsKey("Equalizer");
    public bool CanSetCustomEqualizer => CanEditEq && !_pending.ContainsKey("Equalizer");
    public bool CanSetSpatial => CanChooseSpatial && !_pending.ContainsKey("Audio");
    public bool CanSetDualConnection => CanWrite && _snapshot!.CanSetDualConnection && _snapshot.DualConnection is not null && !_pending.ContainsKey("DualConnection");
    public bool CanSetGameMode => CanWrite && _snapshot!.CanSetGameMode && _snapshot.GameMode is not null && !_pending.ContainsKey("GameMode");
    public bool CanSetHiRes => CanWrite && _snapshot!.CanSetHiRes && _snapshot.HiRes is not null && !_pending.ContainsKey("Audio");
    public bool CanEditEq => CanWrite && _snapshot!.CanSetCustomEqualizer;
    public bool? DualConnection => PendingToggle("DualConnection") ?? _snapshot?.DualConnection;
    public bool? GameMode => PendingToggle("GameMode") ?? _snapshot?.GameMode;
    public bool? HiRes => PendingToggle("HiRes") ?? _snapshot?.HiRes;
    public string DualStateText => PendingState("DualConnection") ?? BooleanState(_snapshot?.DualConnection);
    public string GameStateText => PendingState("GameMode") ?? BooleanState(_snapshot?.GameMode);
    public string HiResStateText => PendingState("Audio") ?? BooleanState(_snapshot?.HiRes);
    public string LeftBatteryText => Battery(_snapshot?.LeftBattery);
    public string RightBatteryText => Battery(_snapshot?.RightBattery);
    public string CaseBatteryText => Battery(_snapshot?.CaseBattery);
    public string LeftChargeText => Charge(_snapshot?.LeftCharging, _snapshot?.LeftBattery);
    public string RightChargeText => Charge(_snapshot?.RightCharging, _snapshot?.RightBattery);
    public string CaseChargeText => Charge(_snapshot?.CaseCharging, _snapshot?.CaseBattery);
    public string? Firmware => _snapshot?.Firmware;
    public string? ProductId => _snapshot is { } snapshot
        && snapshot.Identity == _sessionIdentity && _sessionIdentity == SelectedDevice?.Identity
        ? snapshot.ProductId : null;
    public bool SpatialHiResMutex => _snapshot?.SpatialHiResMutex == true;
    public IReadOnlyList<string> Warnings => _snapshot?.Warnings ?? [];
    public bool HasWarnings => Warnings.Count != 0;
    public string WarningsText => string.Join(Environment.NewLine, Warnings.Select(Sanitize));
    public string LogText => string.Join(Environment.NewLine, _logs);
    public IReadOnlyList<UiOption> AncOptions => _ancOptions;
    public IReadOnlyList<UiOption> PresetOptions => _presetOptions;
    public IReadOnlyList<UiOption> SpatialOptions => _spatialOptions;
    public UiOption? SelectedAnc { get => _selectedAnc; set { if (SetProperty(ref _selectedAnc, value)) { if (!_displaying) { _pendingAnc = true; _ancVersion++; } NotifyCommands(); } } }
    public UiOption? SelectedPreset { get => _selectedPreset; set { if (SetProperty(ref _selectedPreset, value)) { if (!_displaying) { _pendingPreset = true; _presetVersion++; } NotifyCommands(); } } }
    public UiOption? SelectedSpatial { get => _selectedSpatial; set { if (SetProperty(ref _selectedSpatial, value)) { if (!_displaying) { _pendingSpatial = true; _spatialVersion++; } NotifyCommands(); } } }
    public IReadOnlyList<UiEqEntry> EqEntries => _eqEntries;
    public UiEqEntry? SelectedEqEntry => _selectedEqEntry;
    public string EqName { get => _eqName; set { if (SetProperty(ref _eqName, value) && !_displaying) SetEqDirty(true); } }
    public ObservableCollection<EqBandViewModel> Bands { get; }
    public bool EqDirty => _eqDirty;
    public string EqDirtyText => EqDirty ? "有未保存的调整" : "";
    public string EqHint => _snapshot?.CanSetCustomEqualizer == true ? "调节各频段，然后将整组设置写入耳机。" : "连接支持自定义 EQ 的耳机后可编辑。";

    public IAsyncRelayCommand RefreshDevicesCommand { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RefreshStateCommand { get; }
    public IAsyncRelayCommand ApplyAncCommand { get; }
    public IAsyncRelayCommand ApplyPresetCommand { get; }
    public IAsyncRelayCommand ApplySpatialCommand { get; }
    public IAsyncRelayCommand<bool> ToggleDualCommand { get; }
    public IAsyncRelayCommand<bool> ToggleGameCommand { get; }
    public IAsyncRelayCommand<bool> ToggleHiResCommand { get; }
    public IAsyncRelayCommand NewEqCommand { get; }
    public IRelayCommand ResetEqCommand { get; }
    public IAsyncRelayCommand SaveEqCommand { get; }
    public IAsyncRelayCommand UseEqCommand { get; }
    private T Register<T>(T command) where T : IRelayCommand { _commands.Add(command); return command; }
    private void NotifyCommands() { foreach (var command in _commands) command.NotifyCanExecuteChanged(); }
    private void NotifyState()
    {
        foreach (var property in PresentationProperties)
        {
            var value = typeof(MainViewModel).GetProperty(property)!.GetValue(this);
            if (!_published.TryGetValue(property, out var old) || !Equals(old, value))
            { _published[property] = value; OnPropertyChanged(property); }
        }
        NotifyCommands();
    }

    public async Task InitializeAsync(IReadOnlyList<UiDevice>? devices = null)
    {
        await EnumerateAsync(devices);
        if (_preview && SelectedDevice is not null) await ConnectOrDisconnectAsync();
    }

    private async Task RunAsync(string status, Func<Task> action, bool silent = false)
    {
        if (Closing || _lifecyclePending) return;
        _lifecyclePending = true; _operations.CancelQueuedWrites(); NotifyState();
        try
        {
            await _operations.RunAsync(async () =>
            {
                if (!silent) SetStatus(status);
                await action();
            }, OperationCoordinator.Lifecycle, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { if (!Closing) { InvalidateSnapshot(); SetStatus("无法读取耳机，请确认蓝牙连接后重试。", true); } }
        finally { _lifecyclePending = false; NotifyState(); }
    }

    private Task EnumerateAsync(IReadOnlyList<UiDevice>? initialDevices = null) => RunAsync("正在查找已配对耳机…", async () =>
    {
        var devices = initialDevices ?? await _service.EnumerateAsync(_lifetime.Token);
        var selected = devices.FirstOrDefault(d => d.Identity == SelectedDevice?.Identity) ?? devices.FirstOrDefault();
        if (selected?.Identity != SelectedDevice?.Identity)
        {
            AdvanceSession();
            if (_sessionIdentity is not null) await _service.DisconnectAsync(_lifetime.Token);
            _sessionIdentity = null; ResetSelection();
        }
        _devices = devices; _selectedDevice = selected; _title = selected?.Name ?? "选择一副耳机";
        SetStatus(_service is UnsupportedPlatformService ? UnsupportedPlatformService.Message : devices.Count == 0 ? "未发现已配对耳机" : _snapshot is { Connected: true } ? "设备列表已刷新 · 耳机保持连接" : "选择耳机后连接", _service is UnsupportedPlatformService);
        NotifyState();
    });

    private async Task RefreshDevicesAsync()
    {
        await RunAsync("正在查找已配对耳机…", async () =>
        {
            if (EqDirty && !await ConfirmAsync("刷新设备", "刷新设备后，若连接变化，未保存的均衡器调整会被丢弃。继续刷新吗？")) return;
            // Enumerate directly because the operation gate is already held.
            var devices = await _service.EnumerateAsync(_lifetime.Token);
            var selected = devices.FirstOrDefault(d => d.Identity == SelectedDevice?.Identity) ?? devices.FirstOrDefault();
            if (selected?.Identity != SelectedDevice?.Identity)
            {
                AdvanceSession();
                if (_sessionIdentity is not null) await _service.DisconnectAsync(_lifetime.Token);
                _sessionIdentity = null; ResetSelection();
            }
            _devices = devices; _selectedDevice = selected; _title = selected?.Name ?? "选择一副耳机";
            SetStatus(_service is UnsupportedPlatformService ? UnsupportedPlatformService.Message : devices.Count == 0 ? "未发现已配对耳机" : _snapshot is { Connected: true } ? "设备列表已刷新 · 耳机保持连接" : "选择耳机后连接", _service is UnsupportedPlatformService);
            NotifyState();
        });
    }

    public Task SelectDeviceAsync(UiDevice? device) => RunAsync("正在切换耳机…", async () =>
    {
        await ChangeSelectedDeviceAsync(device, "切换耳机", "切换耳机会丢弃尚未保存的均衡器调整。继续切换吗？");
    });

    private Task ConnectOrDisconnectAsync() => RunAsync("正在连接耳机并读取状态…", async () =>
    {
        if (_sessionIdentity is not null)
        {
            if (EqDirty && !await ConfirmAsync("断开客户端", "断开客户端会丢弃尚未保存的均衡器调整。继续吗？")) return;
            AdvanceSession();
            await _service.DisconnectAsync(_lifetime.Token); _sessionIdentity = null; ResetSelection();
            SetStatus("客户端已断开 · 蓝牙配对保留"); Log("已断开客户端连接。"); return;
        }
        if (SelectedDevice is not { } device) return;
        AdvanceSession();
        var snapshot = await _service.ConnectAsync(device.Identity, _lifetime.Token);
        if (snapshot.Identity != device.Identity) throw new InvalidOperationException();
        _sessionIdentity = device.Identity; DisplaySnapshot(snapshot);
        SetStatus(snapshot.Failure is null && snapshot.Connected ? "已连接 · 状态会自动刷新" : "连接尚未就绪，请检查耳机状态。", snapshot.Failure is not null);
        Log(snapshot.Failure is null && snapshot.Connected ? "已连接耳机，读取当前设置。" : "连接尚未就绪，设置暂不可用。");
    });

    public Task PollAsync() => CanRefreshState && !Busy ? RefreshStateAsync() : Task.CompletedTask;
    private Task RefreshStateAsync()
    {
        if (!CanRefreshState) return Task.CompletedTask;
        if (_refreshing) return _refreshTask ?? Task.CompletedTask;
        _refreshing = true; NotifyState();
        return _refreshTask = RefreshCycleAsync(_sessionEpoch, _sessionWork.Token);
    }

    private async Task RefreshCycleAsync(long epoch, CancellationToken token)
    {
        try
        {
            foreach (var scope in new[] { RefreshScope.Identity, RefreshScope.Features, RefreshScope.Anc,
                RefreshScope.Equalizer, RefreshScope.Battery, RefreshScope.Information })
            {
                if (epoch != _sessionEpoch || Closing || _lifecyclePending) break;
                var valid = false;
                await _operations.RunAsync(async () =>
                {
                    if (epoch != _sessionEpoch || Closing) return;
                    var snapshot = await _service.RefreshAsync(scope, _lifetime.Token);
                    if (epoch != _sessionEpoch || Closing) return;
                    valid = AdoptRead(snapshot);
                }, OperationCoordinator.Refresh, token);
                if (!valid) break;
            }
        }
        catch (OperationCanceledException) { }
        catch { if (epoch == _sessionEpoch && !Closing) { InvalidateSnapshot(); SetStatus("耳机状态未确认，请刷新或重新连接。", true); } }
        finally { _refreshing = false; NotifyState(); }
    }

    private bool AdoptRead(UiSnapshot snapshot)
    {
        if (snapshot.Identity != _sessionIdentity || _sessionIdentity != SelectedDevice?.Identity)
        { InvalidateSnapshot(); SetStatus("连接设备已变化，请重新选择并连接耳机。", true); return false; }
        DisplaySnapshot(snapshot);
        var valid = snapshot.Connected && snapshot.Failure is null;
        SetStatus(valid ? "已连接 · 状态已刷新" : "耳机状态未确认，暂时不能修改设置。", !valid);
        return valid;
    }

    private void AdvanceSession()
    {
        _sessionEpoch++;
        _sessionWork.Cancel(); _sessionWork.Dispose(); _sessionWork = new();
        _operations.CancelQueuedWrites(); _pending.Clear();
    }

    private void DisplaySnapshot(UiSnapshot snapshot)
    {
        if (snapshot.Failure is not null || !snapshot.Connected || snapshot.Identity != _sessionIdentity) { InvalidateSnapshot(); return; }
        _snapshot = snapshot; _displaying = true;
        _title = snapshot.DeviceName;
        _subtitle = snapshot.SupportedModel ? $"已连接{(snapshot.Firmware is null ? "" : $" · 固件 {snapshot.Firmware}")}" : "该型号的设置尚未适配";
        var anc = _pendingAnc ? SelectedAnc?.Key : snapshot.Anc;
        var preset = _pendingPreset ? SelectedPreset?.Key : snapshot.Equalizer;
        var spatial = _pendingSpatial ? SelectedSpatial?.Key : snapshot.Spatial;
        _ancOptions = RetainOptions(_ancOptions, snapshot.AncOptions);
        _presetOptions = RetainOptions(_presetOptions, snapshot.EqualizerOptions);
        _spatialOptions = RetainOptions(_spatialOptions, snapshot.SpatialOptions);
        // Publish the new item lists first: two-way ComboBox bindings may clear an old selection
        // when ItemsSource changes. Restore the explicit protocol keys only after that notification.
        NotifyState();
        SelectedAnc = _ancOptions.FirstOrDefault(x => x.Key == anc);
        SelectedPreset = _presetOptions.FirstOrDefault(x => x.Key == preset);
        SelectedSpatial = _spatialOptions.FirstOrDefault(x => x.Key == spatial);
        var selectedId = _eqDraftId;
        _eqEntries = RetainEntries(_eqEntries, snapshot.EqualizerEntries);
        NotifyState();
        if (!EqDirty)
        {
            var entry = _eqEntries.FirstOrDefault(x => x.Id == selectedId) ?? _eqEntries.FirstOrDefault(x => x.Selected) ?? _eqEntries.FirstOrDefault();
            if (!ReferenceEquals(entry, _selectedEqEntry)) LoadEntry(entry);
        }
        else _selectedEqEntry = _eqEntries.FirstOrDefault(x => x.Id == selectedId);
        NotifyState(); _displaying = false;
    }

    private void InvalidateSnapshot()
    {
        _snapshot = null; _displaying = true;
        if (!_pendingAnc) SelectedAnc = null;
        if (!_pendingPreset) SelectedPreset = null;
        if (!_pendingSpatial) SelectedSpatial = null;
        _subtitle = "当前状态未确认，刷新成功后才可修改";
        NotifyState(); _displaying = false;
    }

    private void ResetSelection()
    {
        _pendingAnc = _pendingPreset = _pendingSpatial = false;
        _ancOptions = []; _presetOptions = []; _spatialOptions = []; _actionWarnings.Clear();
        _eqEntries = []; LoadEntry(null); InvalidateSnapshot();
    }

    private Task ApplyOptionAsync(string kind, UiOption? option) => option is null ? Task.CompletedTask : ApplyAsync(new(kind, option.Key));
    private bool Supports(UiAction action) => _snapshot is { } s && action.Kind switch
    {
        "Anc" => s.CanSetAnc && s.AncOptions.Any(x => x.Key == action.Value),
        "Equalizer" => s.CanSetEqualizer && s.EqualizerOptions.Any(x => x.Key == action.Value),
        "Spatial" => s.CanSetSpatial && s.SpatialOptions.Any(x => x.Key == action.Value),
        "DualConnection" => s.CanSetDualConnection && s.DualConnection is not null,
        "GameMode" => s.CanSetGameMode && s.GameMode is not null,
        "HiRes" => s.CanSetHiRes && s.HiRes is not null,
        "CustomEqualizer" => s.CanSetCustomEqualizer && action.EqualizerEntry is not null,
        "CustomSelect" => s.CanSetCustomEqualizer && s.EqualizerEntries.Any(x => x.Id == action.Value && x.Id > 0),
        _ => false
    };

    public async Task SelectEqEntryAsync(UiEqEntry? entry)
    {
        if (Closing || _displaying) return;
        if (entry?.Id == SelectedEqEntry?.Id) return;
        if (entry is not null && !EqEntries.Any(x => x.Id == entry.Id)) return;
        var epoch = _sessionEpoch; var token = _sessionWork.Token;
        try
        {
            if (EqDirty && !await ConfirmAsync("切换均衡器", "要丢弃尚未保存的均衡器调整吗？", token)) return;
            if (Closing || epoch != _sessionEpoch) return;
            var latest = entry is null ? null : EqEntries.FirstOrDefault(x => x.Id == entry.Id);
            if (entry is not null && latest is null) return;
            LoadEntry(latest);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Closing) { }
    }

    private async Task NewEqAsync()
    {
        if (Closing) return;
        if (!HasVerifiedSnapshot || _snapshot?.CanSetCustomEqualizer != true) return;
        var epoch = _sessionEpoch; var token = _sessionWork.Token;
        try
        {
            if (EqDirty && !await ConfirmAsync("新建均衡器", "新建均衡器会丢弃尚未保存的调整。继续吗？", token)) return;
            if (Closing || epoch != _sessionEpoch) return;
            LoadEntry(null); SetEqDirty(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Closing) { }
    }

    private Task SaveEqAsync()
    {
        var name = EqName.Trim();
        if (name.Length is < 1 or > 30 || name.Any(char.IsControl)) { SetStatus("请输入有效的均衡器名称（1 至 30 个字符）。", true); return Task.CompletedTask; }
        if (!EqDirty) return Task.CompletedTask;
        var entry = new UiEqEntry(_eqDraftId ?? 0, name, Bands[0].Minimum, Bands[0].Maximum,
            SelectedEqEntry?.Selected ?? false, Bands.Select(b => new UiBand(b.FrequencyHz, b.Gain)).ToArray());
        return ApplyAsync(new("CustomEqualizer", entry.Id, entry));
    }

    private Task UseEqAsync() => SelectedEqEntry is { Id: > 0 } entry && !EqDirty
        ? ApplyAsync(new("CustomSelect", entry.Id)) : Task.CompletedTask;

    private void LoadEntry(UiEqEntry? entry)
    {
        var displaying = _displaying; _displaying = true; _selectedEqEntry = entry; _eqDraftId = entry?.Id;
        _draftIdentity++; _eqVersion++; EqName = entry?.Name ?? "我的均衡器";
        foreach (var band in Bands)
        {
            band.Minimum = entry?.MinimumGain ?? -6; band.Maximum = entry?.MaximumGain ?? 6;
            band.Gain = entry?.Bands.FirstOrDefault(b => b.FrequencyHz == band.FrequencyHz)?.Gain ?? 0;
        }
        SetEqDirty(false); _displaying = displaying; NotifyState();
    }

    private void BandChanged(object? sender, PropertyChangedEventArgs e) { if (!_displaying && e.PropertyName == nameof(EqBandViewModel.Gain)) SetEqDirty(true); }
    private void SetEqDirty(bool value) { _eqDirty = value; if (value) _eqVersion++; NotifyState(); }
    private async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellation = default)
    {
        return await Dialogs.RunAsync(token => _confirmations.ConfirmAsync(title, message, token), cancellation);
    }
    private void SetStatus(string text, bool warning = false) { _status = text; _statusWarning = warning; NotifyState(); }
    private void Log(string text)
    {
        _logs.Add($"{DateTime.Now:HH:mm:ss}  {Sanitize(text)}");
        if (_logs.Count > 80) _logs.RemoveAt(0);
        OnPropertyChanged(nameof(LogText));
    }
    private string Sanitize(string text)
    {
        foreach (var identity in Devices.Select(d => d.Identity).Append(_sessionIdentity ?? "").Distinct())
            if (identity.Length != 0) text = text.Replace(identity, "[设备标识]", StringComparison.Ordinal);
        var clean = Regex.Replace(text, @"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b[0-9a-f]{12}\b|(?:BTHENUM|BTHLEDEVICE|SWD)\\\S+", "[设备标识]");
        return string.Concat(clean.Where(c => !char.IsControl(c))).Trim();
    }
    private static string Battery(int? value) => value is >= 0 and <= 100 ? $"{value}%" : "—";
    private static string Charge(bool? charging, int? battery) => charging == true ? "正在充电" : battery is null ? "尚未读取" : charging == false ? "未在充电" : "充电状态未确认";
    private static string BooleanState(bool? value) => value is null ? "未确认" : value.Value ? "已开启" : "已关闭";

    public async Task<bool> RequestCloseAsync()
    {
        if (_closing) return true;
        if (_closeDialogRunning) return false;
        SuspendActionsForClose(); _closeDialogRunning = true;
        try
        {
            if (EqDirty && !_preview && !await ConfirmAsync("关闭客户端", "均衡器还有未保存的调整，确定关闭吗？")) return false;
            _closing = true; _lifetime.Cancel(); NotifyState();
            await DisposeAsync(); return true;
        }
        finally { _closeDialogRunning = false; if (!_closing) _closeRequested = false; NotifyState(); }
    }

    public void SuspendActionsForClose()
    {
        if (_closeRequested || _closing) return;
        _closeRequested = true; AdvanceSession(); NotifyState();
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _closing = true; _lifetime.Cancel();
        try { await _service.DisposeAsync(); } catch { }
        await _operations.WhenIdle;
        ClearDeviceManagement();
        foreach (var band in Bands) band.PropertyChanged -= BandChanged;
        NotifyState();
    }

    private static IReadOnlyList<UiOption> RetainOptions(IReadOnlyList<UiOption> previous, IReadOnlyList<UiOption> incoming)
    {
        var normalized = incoming.Select(value => previous.FirstOrDefault(old => old == value) ?? value).ToArray();
        return previous.Count == normalized.Length && previous.Zip(normalized).All(pair => ReferenceEquals(pair.First, pair.Second)) ? previous : normalized;
    }

    private static bool SameEntry(UiEqEntry first, UiEqEntry second) => first.Id == second.Id && first.Name == second.Name
        && first.MinimumGain == second.MinimumGain && first.MaximumGain == second.MaximumGain
        && first.Selected == second.Selected && first.Bands.SequenceEqual(second.Bands);

    private static IReadOnlyList<UiEqEntry> RetainEntries(IReadOnlyList<UiEqEntry> previous, IReadOnlyList<UiEqEntry> incoming)
    {
        var normalized = incoming.Select(value => previous.FirstOrDefault(old => SameEntry(old, value)) ?? value).ToArray();
        return previous.Count == normalized.Length && previous.Zip(normalized).All(pair => ReferenceEquals(pair.First, pair.Second)) ? previous : normalized;
    }
}
