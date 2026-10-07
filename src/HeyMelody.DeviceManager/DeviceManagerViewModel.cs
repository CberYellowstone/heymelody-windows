using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.DeviceManager;

public sealed class DeviceCandidateRow : ObservableObject
{
    private bool _isChecked;
    public DeviceCandidateRow(UiDeviceCandidate candidate, bool selected)
    { Identity = candidate.Identity; Name = candidate.Name; Connected = candidate.Connected; _isChecked = selected; }
    public string Identity { get; }
    public string Name { get; }
    public bool Connected { get; }
    public string Status => Connected ? "已连接" : "已配对，未连接";
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}

public sealed class DeviceManagerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDeviceManagementService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _operation, _dispose;
    private bool _busy, _closing, _hasLoaded;
    private string _error = "";
    public DeviceManagerViewModel(IDeviceManagementService service)
    {
        _service = service;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanCommit);
        CancelCommand = new RelayCommand(() => ExitRequested?.Invoke(), () => CanCommit);
    }
    public ObservableCollection<DeviceCandidateRow> Devices { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public event Action? ExitRequested;
    public bool Busy => _busy;
    public bool Closing => _closing;
    public bool CanRefresh => !_busy && !_closing;
    public bool CanCommit => SelectedCount > 0 && !_busy && !_closing;
    public int SelectedCount => Devices.Count(row => row.IsChecked);
    public string SelectionText => $"已选择 {SelectedCount} 台设备";
    public bool HasDevices => Devices.Count > 0;
    public bool ShowEmpty => !_busy && _hasLoaded && !HasDevices;
    public string Error => _error;
    public bool HasError => _error.Length > 0;
    public int ExitCode { get; private set; } = 1;

    public Task RefreshAsync() => RunAsync(async token =>
    {
        try
        {
            var catalog = await _service.EnumerateCandidatesAsync(token);
            token.ThrowIfCancellationRequested();
            var previous = Devices.ToDictionary(row => row.Identity, row => row.IsChecked, StringComparer.Ordinal);
            foreach (var row in Devices) row.PropertyChanged -= SelectionChanged;
            Devices.Clear();
            foreach (var candidate in catalog.Candidates)
            {
                var row = new DeviceCandidateRow(candidate,
                    _hasLoaded && previous.TryGetValue(candidate.Identity, out var selected) ? selected : candidate.Enabled);
                row.PropertyChanged += SelectionChanged;
                Devices.Add(row);
            }
            _hasLoaded = true; ExitCode = 1; _error = catalog.SelectionWarning ?? "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { _error = "无法读取已配对设备，请确认蓝牙已开启后重试。"; }
    });

    public Task SaveAsync()
    {
        if (!CanCommit) return Task.CompletedTask;
        var identities = Devices.Where(row => row.IsChecked).Select(row => row.Identity).ToArray();
        return RunAsync(async token =>
        {
            try
            {
                var result = await _service.SaveWhitelistAsync(identities, token);
                token.ThrowIfCancellationRequested();
                if (result.Saved) { ExitCode = 0; ExitRequested?.Invoke(); }
                else _error = result.Error ?? "未能保存设备，请刷新列表后重试。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { _error = "未能保存设备，请刷新列表并确认勾选后重试。"; }
        });
    }

    public void ReportLaunchFailure()
    { _error = "无法打开蓝牙设置，请在 Windows 设置中打开蓝牙与设备。"; NotifyState(); }

    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (!CanRefresh) return Task.CompletedTask;
        _busy = true; NotifyState();
        return _operation = RunCoreAsync(work);
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        try { await work(_lifetime.Token); }
        finally { _busy = false; NotifyState(); }
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs change)
    { if (change.PropertyName == nameof(DeviceCandidateRow.IsChecked)) NotifyState(); }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(Busy), nameof(Closing), nameof(CanRefresh), nameof(CanCommit),
            nameof(SelectedCount), nameof(SelectionText), nameof(HasDevices), nameof(ShowEmpty), nameof(Error), nameof(HasError) })
            OnPropertyChanged(name);
        RefreshCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _closing = true; _lifetime.Cancel(); NotifyState();
        if (_operation is { } operation) await operation;
        if (_service is IAsyncDisposable disposable) await disposable.DisposeAsync();
        foreach (var row in Devices) row.PropertyChanged -= SelectionChanged;
        _lifetime.Dispose();
    }
}
