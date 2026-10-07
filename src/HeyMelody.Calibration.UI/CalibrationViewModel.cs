using System.ComponentModel;
using System.Runtime.CompilerServices;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>Reusable presentation of one Host-owned operation, without owning the earbud connection.</summary>
public sealed class CalibrationViewModel(ICalibrationService service, string handle, string kind) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private UiCalibrationState _state = new() { Handle = handle, Workflow = kind };
    private bool _busy, _cancelled;
    private string _error = "", _name = "";
    private UiCalibrationInput? _pendingLevel;
    private Task? _levelOperation;
    private string _resourceSource = "HeyMelody";
    public event PropertyChangedEventHandler? PropertyChanged;
    public UiCalibrationState State => _state;
    public string Kind => kind;
    public string ResourceSource => _resourceSource;
    public bool Busy => _busy;
    public string Error => _error;
    public string Name { get => _name; set { _name = value; Changed(); } }
    public IReadOnlyList<UiHearingProfile> Profiles { get; private set; } = [];
    public bool NeedsReplacement => Profiles.Count(x => x.Uid.HasValue) >= 10;
    public bool ReplacesCurrent => Profiles.Any(x => x.Uid.HasValue && x.Current);
    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            Adopt(await service.GetCalibrationAsync(handle, _lifetime.Token));
            if (service is IResourceSettingsService resources)
                SetResourceSource((await resources.GetResourceSettingsAsync(_lifetime.Token)).Source);
            if (kind == "hearing") Profiles = await service.GetHearingProfilesAsync(_state.Identity, _state.Generation, _lifetime.Token);
        });
    }
    public void Adopt(UiCalibrationState state)
    {
        if (state.Handle != handle || state.Revision < _state.Revision ||
            _state.BackendInstanceId.Length > 0 && state.BackendInstanceId != _state.BackendInstanceId) return;
        _state = state; Changed(nameof(State));
    }
    public void BackendUnavailable(string message)
    { _error = message.Length > 0 ? message : "蓝牙后台已断开，请关闭此窗口后重试。"; Changed(nameof(Error)); }
    public void SetResourceSource(string source)
    { _resourceSource = source; Changed(nameof(ResourceSource)); }
    public async Task SendAsync(string action, bool? heard = null, int? level = null, bool released = false, bool allowReplacement = false)
    {
        if (_levelOperation is { } levelOperation) await levelOperation;
        await RunAsync(async () => Adopt(await service.AdvanceCalibrationAsync(handle, _state.Revision,
            new UiCalibrationInput(action, heard, level, released, action == "save" ? Name.Trim() : null, allowReplacement), _lifetime.Token)));
    }
    public Task AdjustLevelAsync(int level, bool released)
    {
        if (_cancelled) return Task.CompletedTask;
        _pendingLevel = new UiCalibrationInput("level", LevelIndex: level, Released: released || _pendingLevel?.Released == true);
        return _levelOperation ??= SendLevelsAsync();
    }
    private async Task SendLevelsAsync()
    {
        await Task.Yield();
        try
        {
            while (!_cancelled && _pendingLevel is { } input)
            {
                _pendingLevel = null;
                Adopt(await service.AdvanceCalibrationAsync(handle, _state.Revision, input, _lifetime.Token));
            }
        }
        catch (OperationCanceledException) when (_cancelled) { }
        catch { if (!_cancelled) { _error = "音量调整未确认，请重试。"; Changed(nameof(Error)); } }
        finally { _levelOperation = null; }
    }
    public string? ValidateName()
    {
        var name = Name.Trim();
        if (name.Length is < 2 or > 40) return "名称应为 2 至 40 个字符。";
        if (Profiles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return "已有同名配置，请使用其他名称。";
        return null;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _cancelled) return;
        _busy = true; _error = ""; Changed(nameof(Busy)); Changed(nameof(Error));
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _error = error is InvalidOperationException ? error.Message : "操作未能完成，请重试或取消测试。"; Changed(nameof(Error)); }
        finally { _busy = false; Changed(nameof(Busy)); }
    }
    public async Task CancelAsync()
    {
        if (_cancelled) return;
        _cancelled = true;
        _lifetime.Cancel();
        // Cancellation is a separate request and must not wait behind the measurement task.
        try { Adopt(await service.CancelCalibrationAsync(handle, CancellationToken.None)); }
        catch { }
    }
    public async ValueTask DisposeAsync() { await CancelAsync(); _lifetime.Dispose(); }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
