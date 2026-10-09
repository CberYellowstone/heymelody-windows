namespace HeyMelodyWindows.App;

/// <summary>Session-only layout fixtures. The appearance is Free4 Dynaudio; the controls combine supported UI features.</summary>
public static class DemoDeviceData
{
    public const string Identity = "demo-enco-free4";
    public const string Name = "Demo · Enco Free4 丹拿版";
    public const string ProductId = "06C010";
    public const byte Color = 3;
    public const string Notice = "演示设备仅供布局检查，调整只在本次界面中生效。";

    public static bool IsDemoIdentity(string? identity) => string.Equals(identity, Identity, StringComparison.Ordinal);

    public static UiSnapshot CreateSnapshot()
    {
        int[] frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        UiOption[] spatial = [new(0, "关闭"), new(1, "固定空间音效"), new(2, "头部跟踪")];
        UiOption[] gestures = [new(0, "无操作"), new(1, "播放 / 暂停"), new(5, "上一首"), new(6, "下一首"),
            new(7, "降噪开 / 关"), new(8, "音量调节"), new(10, "歌曲切换"), new(11, "增大音量"),
            new(12, "减小音量"), new(13, "设备切换"), new(17, "游戏模式"), new(22, "耳机专注"),
            new(26, "拒接来电"), new(27, "拒接来电 / 挂断电话"), new(29, "接听 / 挂断电话")];
        UiAncMode[] ancModes = [
            new(0, "关闭", "off", [new(0x0003, "关闭降噪")]),
            new(1, "降噪", "anc", [new(0x0107, "智能降噪"), new(0x0104, "深度降噪"), new(0x0105, "中度降噪"), new(0x0106, "轻度降噪")]),
            new(2, "通透", "transparency", [new(0x0208, "通透模式"), new(0x0209, "人声增强")])];
        static UiFeatureControl Switch(string id, string label, bool value = false) =>
            new(id, label, "Switch", value ? 1 : 0, CanWrite: true);
        var gestureControls = new List<UiFeatureControl>();
        string[] actions = ["单击", "双击", "三击", "长按", "滑动", "长按并保持"];
        for (int side = 1; side <= 2; side++)
            for (int action = 1; action <= actions.Length; action++)
                gestureControls.Add(new($"gesture:{side}:0:{action}", $"{(side == 1 ? "左耳" : "右耳")} · {actions[action - 1]}",
                    "Combo", action == 1 ? 1 : action == 2 ? 6 : 7, Options: gestures, Maximum: 29, CanWrite: true));
        return new()
        {
            Identity = Identity, DeviceName = Name, ProductId = ProductId, Color = Color, Firmware = "Demo",
            Connected = true, SupportedModel = true, ControlReady = true, DeviceGeneration = 1, Revision = 1,
            ConnectionState = ConnectionState.Connected, MonitoringState = MonitoringState.Monitoring,
            ObservationFresh = true, CaseLid = CaseLidState.Open, LeftPlacement = EarbudPlacement.InEar,
            RightPlacement = EarbudPlacement.InEar, LeftBattery = 92, RightBattery = 89, CaseBattery = 76,
            LeftCharging = false, RightCharging = false, CaseCharging = true,
            AncPrimary = 1, Anc = 0x0104, Equalizer = 0, Spatial = 0, HiRes = true, DualConnection = true, GameMode = false,
            CanSetAncMode = true, CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true,
            CanSetDualConnection = true, CanSetGameMode = true, CanSetSpatial = true, CanSetHiRes = true,
            SpatialHiResMutex = false, AncModes = ancModes, AncOptions = ancModes.SelectMany(mode => mode.Options).ToArray(),
            EqualizerOptions = [new(0, "丹拿原声"), new(7, "丹拿高解析"), new(2, "纯享人声"), new(1, "清亮高音"), new(3, "澎湃低音")],
            SpatialOptions = spatial, EqualizerFrequencies = frequencies, CustomEqualizerCapacity = 3,
            EqualizerEntries = [new(1, "我的均衡器", -6, 6, true, frequencies.Select((hz, i) => new UiBand(hz, i < 3 ? 2 : 0)).ToArray()),
                new(2, "人声示例", -6, 6, false, frequencies.Select((hz, i) => new UiBand(hz, i is >= 4 and <= 6 ? 3 : 0)).ToArray())],
            CanFitTest = true, CanSetupHearing = true, HearingEnabled = true,
            HearingProfiles = [new(1, "日常听感", true, true), new(2, "音乐听感", false, true)],
            FeatureSections = [
                new("audio-features", "声音", true, [Switch("game", "游戏低延迟"), Switch("hires", "Hi-Res 高音质", true),
                    new("spatial", "空间音效", "Radio", 0, Options: spatial, Maximum: 2, CanWrite: true),
                    Switch("vocal-enhance", "人声增强"), Switch("bass-enabled", "低音增强", true),
                    new("bass", "低音强度", "Slider", 5, Maximum: 10, CanWrite: true),
                    Switch("adaptive-volume", "自适应音量"), Switch("adaptive-ear", "自适应听感"),
                    Switch("speech-perception", "人声感知"), Switch("hearing-optimize", "听感优化"), Switch("game-sound-enabled", "游戏音效"),
                    new("game-sound", "游戏音效类型", "Combo", 0, Options: [new(0, "关闭"), new(3, "射击类游戏音效")], Maximum: 3, CanWrite: true)]),
                new("device-features", "耳机设置", true, [Switch("dual", "双设备连接", true), Switch("wear", "入耳检测", true),
                    Switch("long-power", "长续航模式"), Switch("smart-call", "智能通话", true),
                    Switch("long-press-volume", "长按调节音量", true), Switch("incoming-call", "来电控制", true), Switch("sleep-detection", "睡眠检测"),
                    new("prompt-volume", "提示音音量", "Slider", 5, Minimum: 1, Maximum: 10, CanWrite: true),
                    new("demo-text", "设备备注", "Text", Text: "布局演示", CanWrite: true, Description: "演示文本输入控件")]),
                new("gesture-features", "操控", true, gestureControls)],
            SampleTime = DateTimeOffset.UtcNow
        };
    }
}

public sealed partial class MainViewModel
{
    private UiDevice? _demoDevice;
    private UiSnapshot? _demoSnapshot;
    private UiEqEntry? _demoEqDraft;

    public static bool IsDemoDevice(UiDevice? device) => DemoDeviceData.IsDemoIdentity(device?.Identity);
    public bool IsDemoSelected => IsDemoDevice(SelectedDevice);

    public Task AddDemoDeviceAsync() => RunAsync("正在添加演示设备…", async () =>
    {
        var previousDevices = _devices;
        bool added = _demoDevice is null;
        _demoDevice ??= new(DemoDeviceData.Identity, DemoDeviceData.Name, true);
        _demoSnapshot ??= DemoDeviceData.CreateSnapshot();
        _devices = ComposeDevices(_devices);
        if (!await ChangeSelectedDeviceAsync(_demoDevice, "切换耳机", "切换耳机会丢弃尚未保存的均衡器调整。继续切换吗？"))
        {
            if (added) { _demoDevice = null; _demoSnapshot = null; _devices = previousDevices; }
            return;
        }
        ShowDemoFeedback("已添加演示设备");
    });

    public void ShowDemoFeedback(string action)
    {
        if (!IsDemoSelected || Closing) return;
        SetStatus(string.IsNullOrWhiteSpace(action) ? DemoDeviceData.Notice : $"{action} · {DemoDeviceData.Notice}");
    }

    private IReadOnlyList<UiDevice> ComposeDevices(IReadOnlyList<UiDevice> devices)
    {
        if (_demoDevice is null) return devices;
        if (devices.Count(device => IsDemoDevice(device)) == 1 && devices.Any(device => ReferenceEquals(device, _demoDevice))) return devices;
        return devices.Where(device => !IsDemoDevice(device)).Append(_demoDevice).ToArray();
    }

    private void AttachDemo()
    {
        if (_demoDevice is null || _demoSnapshot is null || !IsDemoSelected) return;
        _sessionIdentity = _demoDevice.Identity;
        DisplaySnapshot(_demoSnapshot);
        if (_demoEqDraft is { } draft)
        {
            _demoEqDraft = null;
            LoadEntry(draft);
            _eqDraftId = draft.Id > 0 ? draft.Id : null;
            _selectedEqEntry = _eqEntries.FirstOrDefault(entry => entry.Id == draft.Id);
            SetEqDirty(true);
        }
        ShowDemoFeedback("");
    }

    private void CaptureDemoDraft()
    {
        if (!IsDemoSelected) return;
        _demoEqDraft = EqDirty ? new(_eqDraftId ?? 0, EqName, Bands[0].Minimum, Bands[0].Maximum,
            SelectedEqEntry?.Selected ?? false, Bands.Select(band => new UiBand(band.FrequencyHz, band.Gain)).ToArray()) : null;
    }

    private void ApplyDemoAction(UiAction action)
    {
        if (_demoSnapshot is not { } snapshot) return;
        switch (action.Kind)
        {
            case "Anc":
                snapshot.AncPrimary = action.Value >> 8; snapshot.Anc = action.Value;
                _pendingAnc = false; _pendingAncKey = _pendingAncPrimary = null; break;
            case "AncMode":
                snapshot.AncPrimary = action.Value; snapshot.Anc = null;
                _pendingAnc = false; _pendingAncKey = _pendingAncPrimary = null; break;
            case "Equalizer":
                snapshot.Equalizer = action.Value;
                snapshot.EqualizerEntries = snapshot.EqualizerEntries.Select(entry => entry with { Selected = false }).ToArray();
                _pendingPreset = false; _pendingPresetKey = null;
                break;
            case "Spatial": snapshot.Spatial = action.Value; _pendingSpatial = false; _pendingSpatialKey = null; break;
            case "DualConnection": snapshot.DualConnection = action.Value == 1; break;
            case "GameMode": snapshot.GameMode = action.Value == 1; break;
            case "HiRes": snapshot.HiRes = action.Value == 1; break;
            case "Feature":
                snapshot.FeatureSections = snapshot.FeatureSections.Select(section => section with
                { Controls = section.Controls.Select(control => control.Id != action.FeatureId ? control : control with
                    { Value = action.Value, Text = control.Kind == "Text" ? action.Text : control.Text }).ToArray() }).ToArray();
                switch (action.FeatureId)
                {
                    case "dual": snapshot.DualConnection = action.Value == 1; break;
                    case "game": snapshot.GameMode = action.Value == 1; break;
                    case "hires": snapshot.HiRes = action.Value == 1; break;
                    case "spatial": snapshot.Spatial = action.Value; _pendingSpatial = false; _pendingSpatialKey = null; break;
                }
                break;
            case "CustomSelect":
                snapshot.Equalizer = null;
                snapshot.EqualizerEntries = snapshot.EqualizerEntries.Select(entry => entry with { Selected = entry.Id == action.Value }).ToArray();
                _eqDraftId = action.Value;
                break;
            case "CustomEqualizer" when action.EqualizerEntry is { } draft:
                snapshot.Equalizer = null;
                int id = draft.Id > 0 ? draft.Id : snapshot.EqualizerEntries.Select(entry => entry.Id).DefaultIfEmpty().Max() + 1;
                snapshot.EqualizerEntries = snapshot.EqualizerEntries.Where(entry => entry.Id != id)
                    .Select(entry => entry with { Selected = false }).Append(draft with { Id = id, Selected = true, Bands = draft.Bands.ToArray() }).ToArray();
                _eqDraftId = id;
                SetEqDirty(false);
                break;
        }
        if (action.Kind is "Spatial" or "DualConnection" or "GameMode" or "HiRes")
        {
            string feature = action.Kind switch { "Spatial" => "spatial", "DualConnection" => "dual", "GameMode" => "game", _ => "hires" };
            snapshot.FeatureSections = snapshot.FeatureSections.Select(section => section with
            { Controls = section.Controls.Select(control => control.Id == feature ? control with { Value = action.Value } : control).ToArray() }).ToArray();
        }
        snapshot.Revision++;
        DisplaySnapshot(snapshot);
        ShowDemoFeedback("已更新演示布局");
    }
}
