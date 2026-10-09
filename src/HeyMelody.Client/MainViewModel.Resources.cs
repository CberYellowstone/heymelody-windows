using CommunityToolkit.Mvvm.ComponentModel;

namespace HeyMelodyWindows.App;

public sealed class FeatureControlViewModel : ObservableObject
{
    private readonly Func<FeatureControlViewModel, Task> _submit;
    private readonly Func<bool> _canWrite;
    private bool _booleanValue;
    private double _numberValue;
    private string _textValue;
    private UiOption? _selectedOption;
    public FeatureControlViewModel(UiFeatureControl definition, Func<FeatureControlViewModel, Task> submit, Func<bool>? canWrite = null)
    {
        Definition = definition; _submit = submit;
        _canWrite = canWrite ?? (() => true);
        _booleanValue = definition.Value == 1; _numberValue = definition.Value ?? 0;
        _textValue = definition.Text ?? "";
        _selectedOption = Options.FirstOrDefault(x => x.Key == definition.Value);
    }
    public UiFeatureControl Definition { get; }
    public string Id => Definition.Id;
    public string Label => Definition.Label;
    public string Description => Definition.Description;
    public string Kind => Definition.Kind;
    public bool CanEdit => Definition.CanWrite && _canWrite();
    internal void RefreshPermissions() => OnPropertyChanged(nameof(CanEdit));
    public double Minimum => Definition.Minimum;
    public double Maximum => Definition.Maximum;
    public double Step => Definition.Step;
    public IReadOnlyList<UiOption> Options => Definition.Options ?? [];
    public bool BooleanValue { get => _booleanValue; set => SetProperty(ref _booleanValue, value); }
    public double NumberValue { get => _numberValue; set => SetProperty(ref _numberValue, value); }
    public string TextValue { get => _textValue; set => SetProperty(ref _textValue, value); }
    public UiOption? SelectedOption { get => _selectedOption; set => SetProperty(ref _selectedOption, value); }
    public Task SubmitAsync() => CanEdit ? _submit(this) : Task.CompletedTask;
}
public sealed record FeatureSectionViewModel(string Id, string Header, string Description,
    bool DefaultExpanded, IReadOnlyList<FeatureControlViewModel> Controls);

public sealed partial class MainViewModel
{
    private IReadOnlyList<UiAncMode> _ancModes = [];
    private UiAncMode? _selectedAncMode;
    private UiOption? _selectedAncSubmode;
    private IReadOnlyList<FeatureSectionViewModel> _featureSections = [];
    private string _featureContext = "";
    private UiOption _selectedResourceSource = new(0, "欢律");
    private bool _useOnlineResources = true;
    private string _cloudKey = "", _resourceStatus = "";
    private string _configuredResourceSource = "HeyMelody";
    private bool _configuredOnline = true, _redownloadingResources, _redownloadFailed;
    private string _redownloadMessage = "";
    private long _artworkRevision;
    private readonly Dictionary<int, string> _sourceKeys = new() { [0] = "", [1] = "" };
    private bool _presentingResources, _resourcePreferencesDirty;
    private bool _savingResources, _hearingPending;
    private IReadOnlyList<UiHearingProfile> _hearingProfiles = [];
    private UiHearingProfile? _selectedHearingProfile;
    public IReadOnlyList<UiOption> ResourceSourceOptions { get; } = [new(0, "欢律"), new(1, "无线耳机")];
    public UiOption SelectedResourceSource
    {
        get => _selectedResourceSource;
        set
        {
            if (!_presentingResources) _sourceKeys[_selectedResourceSource.Key] = _cloudKey;
            if (SetProperty(ref _selectedResourceSource, value) && !_presentingResources) CloudKey = _sourceKeys.GetValueOrDefault(value.Key, "");
            if (!_presentingResources) _resourcePreferencesDirty = true;
        }
    }
    public bool UseOnlineResources { get => _useOnlineResources; set { if (SetProperty(ref _useOnlineResources, value) && !_presentingResources) _resourcePreferencesDirty = true; } }
    public string CloudKey { get => _cloudKey; set { if (SetProperty(ref _cloudKey, value) && !_presentingResources) _resourcePreferencesDirty = true; } }
    public string ResourceKeyFor(UiOption source) => source.Key == SelectedResourceSource.Key ? CloudKey : _sourceKeys.GetValueOrDefault(source.Key, "");
    public string ResourceStatus => _resourceStatus;
    // Host-confirmed values only: a settings draft never redirects artwork requests.
    public string ResourceSource => _snapshot?.ResourceSource ?? _configuredResourceSource;
    public long ResourceRevision => _snapshot?.ResourceRevision ?? 0;
    /// <summary>Changes when the backend instance or its published resource revision advances; never on a cleared snapshot.</summary>
    public string ArtworkEpoch => (_backendInstanceId ?? "local") + ":" + _artworkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ConfiguredResourceSourceName => _configuredResourceSource == "WirelessEarphones" ? "无线耳机" : "欢律";
    public bool CanRedownloadResources => _service is IResourceSettingsService && _configuredOnline && !_redownloadingResources
        && !_savingResources && !Closing;
    public bool IsRedownloadingResources => _redownloadingResources;
    public bool ResourceServiceAvailable => _service is IResourceSettingsService;
    public bool OnlineResourcesConfigured => _configuredOnline;
    public string RedownloadMessage => _redownloadMessage;
    public bool RedownloadFailed => _redownloadFailed;
    public byte? DeviceColor => _snapshot?.Color;
    public string? SelectedDeviceIdentity => _sessionIdentity;
    public IArtworkService? ArtworkService => _service as IArtworkService;
    public IReadOnlyList<UiAncMode> AncModes => _ancModes;
    public UiAncMode? SelectedAncMode => _selectedAncMode;
    public UiOption? SelectedAncSubmode { get => _selectedAncSubmode; set => SetProperty(ref _selectedAncSubmode, value); }
    public bool HasAncSubmodes => _selectedAncMode?.Options.Count > 1;
    public IReadOnlyList<UiOption> AncSubmodes => _selectedAncMode?.Options ?? [];
    public bool CanCreateEq => CanEditEq && EqEntries.Count < (_snapshot?.CustomEqualizerCapacity ?? 3);
    public IReadOnlyList<FeatureSectionViewModel> FeatureSections => _featureSections;
    public IReadOnlyList<UiHearingProfile> HearingProfiles => _hearingProfiles;
    public UiHearingProfile? SelectedHearingProfile { get => _selectedHearingProfile; set => SetProperty(ref _selectedHearingProfile, value); }
    public bool CanSelectHearingProfile => CanWrite && !_hearingPending && _hearingProfiles.Any(x => x.Uid.HasValue && x.CanApply);
    public bool CanSetupHearing => CanWrite && _snapshot!.CanSetupHearing && !_hearingPending;
    public bool CanFitTest => CanWrite && _snapshot!.CanFitTest;

    private void EnsureBands(IReadOnlyList<int> frequencies)
    {
        if (Bands.Select(b => b.FrequencyHz).SequenceEqual(frequencies)) return;
        foreach (var band in Bands) band.PropertyChanged -= BandChanged;
        Bands.Clear();
        foreach (int hz in frequencies)
        { var band = new EqBandViewModel(hz); band.PropertyChanged += BandChanged; Bands.Add(band); }
    }
    private void UpdateResourcePresentation(UiSnapshot snapshot)
    {
        if (!EqDirty && snapshot.EqualizerFrequencies.Count > 0) EnsureBands(snapshot.EqualizerFrequencies);
        _ancModes = snapshot.AncModes.Count > 0 ? snapshot.AncModes : snapshot.AncOptions.GroupBy(x => x.Key >> 8)
            .Select(g => new UiAncMode(g.Key, g.First().Name, "anc", g.ToArray())).ToArray();
        var selected = _pendingAnc ? _pendingAncKey : snapshot.Anc;
        var primary = _pendingAnc ? _pendingAncPrimary : snapshot.AncPrimary ?? (snapshot.Anc >> 8);
        _selectedAncMode = _ancModes.FirstOrDefault(x => x.Key == primary);
        _selectedAncSubmode = _selectedAncMode?.Options.FirstOrDefault(x => x.Key == selected);
        var identity = snapshot.Identity; var generation = snapshot.DeviceGeneration; var sourceRevision = snapshot.ResourceRevision;
        string context = identity + "/" + generation + "/" + sourceRevision;
        var previous = _featureContext == context ? _featureSections.ToDictionary(s => s.Id) : new Dictionary<string, FeatureSectionViewModel>();
        // Old offline presentations may still contain this retired control.
        var visibleSections = snapshot.FeatureSections.Select(section => section with
        { Controls = section.Controls.Where(control => control.Id != "auto-volume").ToArray() })
            .Where(section => section.Controls.Count != 0);
        var nextSections = visibleSections.Select(s =>
        {
            if (previous.TryGetValue(s.Id, out var old) && old.Header == s.Header && old.Description == s.Description
                && old.DefaultExpanded == s.DefaultExpanded && old.Controls.Count == s.Controls.Count
                && old.Controls.Zip(s.Controls).All(pair => SameFeature(pair.First.Definition, pair.Second))) return old;
            return new FeatureSectionViewModel(s.Id, s.Header, s.Description,
            s.DefaultExpanded, s.Controls.Select(c => new FeatureControlViewModel(c, vm =>
            {
                if (_snapshot?.Identity != identity || _snapshot.DeviceGeneration != generation || _snapshot.ResourceRevision != sourceRevision)
                    return Task.CompletedTask;
                int value = vm.Kind == "Switch" ? (vm.BooleanValue ? 1 : 0) : vm.Kind is "Radio" or "Combo"
                    ? vm.SelectedOption?.Key ?? int.MinValue : checked((int)Math.Round(vm.NumberValue));
                return ApplyAsync(new("Feature", value, FeatureId: vm.Id, Text: vm.Kind == "Text" ? vm.TextValue : null));
            }, () => CanWrite && !_pending.ContainsKey("Feature"))).ToArray());
        }).ToArray();
        if (!_featureSections.SequenceEqual(nextSections)) _featureSections = nextSections;
        _featureContext = context;
        var profiles = snapshot.HearingProfiles;
        _hearingProfiles = profiles.Count == 0 ? [] : new[] {new UiHearingProfile(null, "关闭", snapshot.HearingEnabled == false, true)}.Concat(profiles).ToArray();
        _selectedHearingProfile = _hearingProfiles.FirstOrDefault(x => x.Current);
    }
    private static bool SameFeature(UiFeatureControl a, UiFeatureControl b) => a.Id == b.Id && a.Label == b.Label
        && a.Kind == b.Kind && a.Value == b.Value && a.Text == b.Text && a.Minimum == b.Minimum && a.Maximum == b.Maximum
        && a.Step == b.Step && a.CanWrite == b.CanWrite && a.Description == b.Description && (a.Options ?? []).SequenceEqual(b.Options ?? []);
    public async Task SelectAncModeAsync(UiAncMode mode)
    {
        if (_displaying || !CanSetAncMode || !AncModes.Any(x => x.Key == mode.Key)
            || mode.Key == _selectedAncMode?.Key) return;
        _pendingAnc = true; _pendingAncPrimary = mode.Key; _pendingAncKey = null; _ancVersion++;
        _selectedAncMode = _ancModes.Single(x => x.Key == mode.Key);
        _selectedAncSubmode = null; _selectedAnc = null;
        NotifyState(); await ApplyAsync(new("AncMode", mode.Key));
    }
    public Task ApplyAncSubmodeAsync(UiOption option)
    {
        if (_displaying || !CanSetAnc || _selectedAncMode?.Options.Any(x => x.Key == option.Key) != true) return Task.CompletedTask;
        SelectedAnc = option; SelectedAncSubmode = option; return ApplyOptionAsync("Anc", option);
    }
    public Task SelectPresetAsync(UiOption option)
    { if (_displaying) return Task.CompletedTask; SelectedPreset = option; return ApplyOptionAsync("Equalizer", option); }
    public async Task ActivateEqEntryAsync(UiEqEntry entry)
    { await SelectEqEntryAsync(entry); if (SelectedEqEntry?.Id == entry.Id && !EqDirty) await ApplyAsync(new("CustomSelect", entry.Id)); }
    private async Task LoadResourcePreferencesAsync()
    {
        if (_service is not IResourceSettingsService resources) return;
        try { PresentResourceSettings(await resources.GetResourceSettingsAsync(_lifetime.Token)); }
        catch { _resourceStatus = "资源设置暂不可用"; NotifyState(); }
    }
    private void ObserveArtworkRevision(long revision) { if (revision > _artworkRevision) _artworkRevision = revision; }
    private void PresentResourceSettings(UiResourceSettings settings, bool replaceDraft = false)
    {
        _configuredResourceSource = settings.Source; _configuredOnline = settings.UseOnlineResources;
        if (!_resourcePreferencesDirty || replaceDraft)
        {
            _presentingResources = true;
            try
            {
                _sourceKeys[0] = settings.HeyMelodyKey ?? ""; _sourceKeys[1] = settings.WirelessEarphonesKey ?? "";
                SelectedResourceSource = ResourceSourceOptions[settings.Source == "WirelessEarphones" ? 1 : 0];
                UseOnlineResources = settings.UseOnlineResources; CloudKey = settings.CloudKey;
                _resourcePreferencesDirty = false;
            }
            finally { _presentingResources = false; }
        }
        _resourceStatus = settings.Status.Length > 0 ? settings.Status : $"{(settings.IsOnline ? "在线" : "预装")}资源 · {settings.ContentVersion}";
        NotifyState();
    }
    public async Task SaveResourcePreferencesAsync()
    {
        if (_savingResources || Closing || _service is not IResourceSettingsService resources) return;
        string source = SelectedResourceSource.Key == 0 ? "HeyMelody" : "WirelessEarphones";
        bool discardDraft = EqDirty && source != _configuredResourceSource;
        if (discardDraft && !IsDemoSelected && !await ConfirmAsync("切换资源", "切换资源将丢弃未保存的均衡器调整。继续吗？", _lifetime.Token)) return;
        _savingResources = true;
        try
        {
            _sourceKeys[SelectedResourceSource.Key] = CloudKey;
            var settings = new UiResourceSettings(source, UseOnlineResources, CloudKey)
            { HeyMelodyKey = _sourceKeys[0], WirelessEarphonesKey = _sourceKeys[1] };
            PresentResourceSettings(await resources.SaveResourceSettingsAsync(settings, _lifetime.Token), replaceDraft: true);
            if (discardDraft && EqDirty) LoadEntry(null);
        }
        catch { SetStatus("无法保存资源设置，请重试。", true); }
        finally { _savingResources = false; }
    }
    /// <summary>Re-fetches the saved source's online resources; the result stays visible after the dialog closes.</summary>
    public async Task RedownloadResourcesAsync()
    {
        if (!CanRedownloadResources || _service is not IResourceSettingsService resources) return;
        _redownloadingResources = true; _redownloadFailed = false; _redownloadMessage = ""; NotifyState();
        try
        {
            var result = await resources.RedownloadResourcesAsync(_lifetime.Token);
            _redownloadFailed = !result.Success; _redownloadMessage = Sanitize(result.Message);
            PresentResourceSettings(result.Settings);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            _redownloadFailed = true;
            _redownloadMessage = error is IOException or NotSupportedException or InvalidOperationException && error.Message.Length is > 0 and <= 180
                ? Sanitize(error.Message) : "无法重新下载在线资源，请检查网络后重试。";
        }
        finally { _redownloadingResources = false; NotifyState(); }
        Log("在线资源：" + _redownloadMessage);
    }
    public async Task SelectHearingProfileAsync(UiHearingProfile profile)
    {
        if (_displaying || !CanSelectHearingProfile || !profile.CanApply) return;
        if (IsDemoSelected && _demoSnapshot is { } demo)
        {
            if (!_hearingProfiles.Any(candidate => candidate.Uid == profile.Uid)) return;
            demo.HearingEnabled = profile.Uid.HasValue;
            demo.HearingProfiles = demo.HearingProfiles.Select(candidate => candidate with { Current = candidate.Uid == profile.Uid }).ToArray();
            demo.Revision++;
            DisplaySnapshot(demo);
            ShowDemoFeedback("已选择演示听感");
            return;
        }
        if (_service is not ICalibrationService calibration) return;
        var target = _snapshot!; _hearingPending = true; NotifyState();
        try
        {
            var result = await calibration.SelectHearingProfileAsync(target.Identity, target.DeviceGeneration, profile.Uid, _lifetime.Token);
            if (_snapshot?.Identity == target.Identity && _snapshot.DeviceGeneration == target.DeviceGeneration)
            { if (result.After is { } after) DisplaySnapshot(after); SetStatus(result.Message, result.ReadbackMatches != true); }
        }
        catch { SetStatus("听感设置结果未确认，请刷新后核实。", true); }
        finally { _hearingPending = false; NotifyState(); }
    }
    public Task<UiCalibrationState> CreateCalibrationAsync(string kind)
    {
        if (IsDemoSelected) throw new InvalidOperationException("演示设备只用于布局检查，不会启动耳塞贴合度测试或黄金听感流程。");
        if (_service is not ICalibrationService calibration || _snapshot is not { } snapshot ||
            kind == "fit" && !CanFitTest || kind == "hearing" && !CanSetupHearing)
            throw new InvalidOperationException("当前设备无法启动该测试。");
        return calibration.CreateCalibrationAsync(snapshot.Identity, snapshot.DeviceGeneration, kind, _lifetime.Token);
    }
}
