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
    private long _editRevision;
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
    public bool BooleanValue { get => _booleanValue; set { if (SetProperty(ref _booleanValue, value)) _editRevision++; } }
    public double NumberValue { get => _numberValue; set { if (SetProperty(ref _numberValue, value)) _editRevision++; } }
    public string TextValue { get => _textValue; set { if (SetProperty(ref _textValue, value)) _editRevision++; } }
    public UiOption? SelectedOption { get => _selectedOption; set { if (SetProperty(ref _selectedOption, value)) _editRevision++; } }
    public async Task SubmitAsync()
    {
        long submittedRevision = _editRevision;
        try { if (CanEdit) await _submit(this); }
        finally
        {
            // A changed readback creates a new model. If this model was reused because the
            // observation stayed unchanged, settle its submitted value from that observation.
            // A newer edit belongs to a later intent and must survive this completion.
            if (_editRevision == submittedRevision)
            {
                switch (Kind)
                {
                    case "Switch": BooleanValue = Definition.Value == 1; break;
                    case "Combo" or "Radio": SelectedOption = Options.FirstOrDefault(x => x.Key == Definition.Value); break;
                    case "Text": TextValue = Definition.Text ?? ""; break;
                    case "Slider": NumberValue = Definition.Value ?? 0; break;
                }
            }
        }
    }
}
public sealed record FeatureSectionViewModel(string Id, string Header, string Description,
    bool DefaultExpanded, IReadOnlyList<FeatureControlViewModel> Controls);

public sealed partial class MainViewModel
{
    private IReadOnlyList<UiAncMode> _ancModes = [];
    private UiAncMode? _selectedAncMode;
    private UiOption? _selectedAncSubmode;
    private IReadOnlyList<FeatureSectionViewModel> _featureSections = [];
    private IReadOnlyList<FeatureControlViewModel> _equalizerFeatures = [];
    private string _featureContext = "";
    private bool _useOnlineResources = true;
    private string _cloudKey = "", _resourceStatus = "";
    private bool _configuredOnline = true, _redownloadingResources, _redownloadFailed;
    private string _redownloadMessage = "";
    private long _artworkRevision;
    private bool _presentingResources, _resourcePreferencesDirty;
    private bool _savingResources, _hearingPending;
    private IReadOnlyList<UiHearingProfile> _hearingProfiles = [];
    private UiHearingProfile? _selectedHearingProfile;
    public bool UseOnlineResources { get => _useOnlineResources; set { if (SetProperty(ref _useOnlineResources, value) && !_presentingResources) _resourcePreferencesDirty = true; } }
    public string CloudKey { get => _cloudKey; set { if (SetProperty(ref _cloudKey, value) && !_presentingResources) _resourcePreferencesDirty = true; } }
    public string ResourceStatus => _resourceStatus;
    public long ResourceRevision => _snapshot?.ResourceRevision ?? 0;
    /// <summary>Changes when the backend instance or its published resource revision advances; never on a cleared snapshot.</summary>
    public string ArtworkEpoch => (_backendInstanceId ?? "local") + ":" + _artworkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    public IReadOnlyList<FeatureControlViewModel> EqualizerFeatures => _equalizerFeatures;
    public bool HasEqualizerFeatures => _equalizerFeatures.Count > 0;
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
        FeatureControlViewModel CreateControl(UiFeatureControl definition) => new(definition, vm =>
        {
            if (_snapshot?.Identity != identity || _snapshot.DeviceGeneration != generation || _snapshot.ResourceRevision != sourceRevision)
                return Task.CompletedTask;
            int value = vm.Kind == "Switch" ? (vm.BooleanValue ? 1 : 0) : vm.Kind is "Radio" or "Combo"
                ? vm.SelectedOption?.Key ?? int.MinValue : checked((int)Math.Round(vm.NumberValue));
            return QueueFeatureAsync(new("Feature", value, FeatureId: vm.Id, Text: vm.Kind == "Text" ? vm.TextValue : null), sourceRevision);
        }, () => CanWrite);
        var featureSections = _writeTargets.Count == 0 ? snapshot.FeatureSections
            : snapshot.FeatureSections.Select(section => section with
            { Controls = section.Controls.Select(control => ShowQueuedFeature(control, snapshot)).ToArray() }).ToArray();
        var equalizerDefinitions = featureSections.SelectMany(section => section.Controls)
            .Where(control => control.Id is "bass-enabled" or "bass").OrderBy(control => control.Id == "bass-enabled" ? 0 : 1)
            .Select(control => control.Id == "bass-enabled" ? control with
            {
                Label = "BassWave™动态低音",
                Description = "动态增益低音，清晰且高保真地呈现深邃低音效果。"
            } : control).ToArray();
        if (_featureContext != context || _equalizerFeatures.Count != equalizerDefinitions.Length
            || !_equalizerFeatures.Zip(equalizerDefinitions).All(pair => SameFeature(pair.First.Definition, pair.Second)))
            _equalizerFeatures = equalizerDefinitions.Select(CreateControl).ToArray();
        // Apply the same presentation to live definitions, demos and older offline snapshots.
        var visibleSections = featureSections.Select(section => section with
        { Controls = GroupGameControls(section.Controls.Where(control => control.Id is not ("auto-volume" or "game-sound" or "bass-enabled" or "bass")).ToArray()) })
            .Where(section => section.Controls.Count != 0);
        var nextSections = visibleSections.Select(s =>
        {
            if (previous.TryGetValue(s.Id, out var old) && old.Header == s.Header && old.Description == s.Description
                && old.DefaultExpanded == s.DefaultExpanded && old.Controls.Count == s.Controls.Count
                && old.Controls.Zip(s.Controls).All(pair => SameFeature(pair.First.Definition, pair.Second))) return old;
            return new FeatureSectionViewModel(s.Id, s.Header, s.Description,
            s.DefaultExpanded, s.Controls.Select(CreateControl).ToArray());
        }).ToArray();
        if (!_featureSections.SequenceEqual(nextSections)) _featureSections = nextSections;
        _featureContext = context;
        var profiles = snapshot.HearingProfiles;
        _hearingProfiles = profiles.Count == 0 ? [] : new[] {new UiHearingProfile(null, "关闭", snapshot.HearingEnabled == false, true)}.Concat(profiles).ToArray();
        _selectedHearingProfile = _hearingProfiles.FirstOrDefault(x => x.Current);
    }
    private static IReadOnlyList<UiFeatureControl> GroupGameControls(IReadOnlyList<UiFeatureControl> controls)
    {
        var lowLatency = controls.FirstOrDefault(control => control.Id == "game");
        var gameSound = controls.FirstOrDefault(control => control.Id == "game-sound-enabled");
        if (lowLatency is null || gameSound is null) return controls;
        return controls.Where(control => control.Id != "game-sound-enabled")
            .SelectMany(control => control.Id == "game" ? new[] { control, gameSound } : new[] { control }).ToArray();
    }
    private static bool SameFeature(UiFeatureControl a, UiFeatureControl b) => a.Id == b.Id && a.Label == b.Label
        && a.Kind == b.Kind && a.Value == b.Value && a.Text == b.Text && a.Minimum == b.Minimum && a.Maximum == b.Maximum
        && a.Step == b.Step && a.CanWrite == b.CanWrite && a.Description == b.Description && (a.Options ?? []).SequenceEqual(b.Options ?? []);
    public Task SelectAncModeAsync(UiAncMode mode)
    {
        if (_displaying || !CanChooseAncMode || !AncModes.Any(x => x.Key == mode.Key)
            || mode.Key == _selectedAncMode?.Key) return Task.CompletedTask;
        return QueueAncActionAsync(new("AncMode", mode.Key));
    }
    public Task ApplyAncSubmodeAsync(UiOption option)
    {
        if (_displaying || !CanChooseAnc || _selectedAncMode?.Options.Any(x => x.Key == option.Key) != true) return Task.CompletedTask;
        return QueueAncActionAsync(new("Anc", option.Key));
    }
    public Task SelectPresetAsync(UiOption option)
    { if (_displaying) return Task.CompletedTask; SelectedPreset = option; return ApplyOptionAsync("Equalizer", option); }
    public async Task ActivateEqEntryAsync(UiEqEntry entry)
    { await SelectEqEntryAsync(entry); if (SelectedEqEntry?.Id == entry.Id && !EqDirty) await QueueEqActionAsync(new("CustomSelect", entry.Id)); }
    private async Task LoadResourcePreferencesAsync()
    {
        if (_service is not IResourceSettingsService resources) return;
        try { PresentResourceSettings(await resources.GetResourceSettingsAsync(_lifetime.Token)); }
        catch { _resourceStatus = "资源设置暂不可用"; NotifyState(); }
    }
    private void ObserveArtworkRevision(long revision) { if (revision > _artworkRevision) _artworkRevision = revision; }
    private void PresentResourceSettings(UiResourceSettings settings, bool replaceDraft = false)
    {
        _configuredOnline = settings.UseOnlineResources;
        if (!_resourcePreferencesDirty || replaceDraft)
        {
            _presentingResources = true;
            try
            {
                UseOnlineResources = settings.UseOnlineResources; CloudKey = settings.HeyMelodyKey;
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
        _savingResources = true;
        try
        {
            var settings = new UiResourceSettings(UseOnlineResources: UseOnlineResources, HeyMelodyKey: CloudKey);
            PresentResourceSettings(await resources.SaveResourceSettingsAsync(settings, _lifetime.Token), replaceDraft: true);
        }
        catch { SetStatus("无法保存资源设置，请重试。", true); }
        finally { _savingResources = false; }
    }
    /// <summary>Re-fetches online resources using saved preferences; the result stays visible after the dialog closes.</summary>
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
