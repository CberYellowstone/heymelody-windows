using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Windows;

namespace HeyMelodyWindows.Host;

internal sealed class WorkerRequest
{
    public long Id { get; set; }
    public string Operation { get; set; } = "";
    public ulong Address { get; set; }
    public string Identity { get; set; } = "";
    public string Name { get; set; } = "";
    public RefreshScope Scope { get; set; }
    public UiAction? Action { get; set; }
    public string? CatalogGzip { get; set; }
    public string Source { get; set; } = "HeyMelody";
    public long ResourceRevision { get; set; }
    public long AppMarker { get; set; } = 17006004;
    public string StableKey { get; set; } = "";
    public long Generation { get; set; }
    public string Handle { get; set; } = "";
    public long Revision { get; set; }
    public string CalibrationKind { get; set; } = "";
    public UiCalibrationInput? Input { get; set; }
    public int? HearingUid { get; set; }
    public string? ObservedProductId { get; set; }
    public byte? ObservedColor { get; set; }
}

internal sealed class WorkerMessage
{
    public string Kind { get; set; } = "response";
    public long Id { get; set; }
    public bool Success { get; set; }
    public JsonElement? Data { get; set; }
    public UiSnapshot? Snapshot { get; set; }
    public UiCalibrationState? Calibration { get; set; }
    public string? Error { get; set; }
}

internal static class NativeWorker
{
    internal static async Task<int> RunAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        await using var service = new NativeHeadsetService();
        using var lifetime = new CancellationTokenSource();
        using var outputGate = new SemaphoreSlim(1);
        var snapshots = Channel.CreateBounded<UiSnapshot>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        service.SnapshotChanged += snapshot =>
        {
            // Dropping a closed-lid observation would hide the next open edge. The
            // supervisor treats child termination as uncertain and never replays writes.
            if (!snapshots.Writer.TryWrite(snapshot)) Environment.Exit(1);
        };
        var calibrations = Channel.CreateBounded<UiCalibrationState>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        service.CalibrationChanged += state => calibrations.Writer.TryWrite(state);
        async Task WriteAsync(WorkerMessage message)
        {
            var line = JsonSerializer.Serialize(message, RpcWire.Json);
            if (line.Length > RpcWire.MaximumLineCharacters) throw new InvalidDataException();
            await outputGate.WaitAsync(lifetime.Token);
            try { await Console.Out.WriteLineAsync(line); await Console.Out.FlushAsync(); }
            finally { outputGate.Release(); }
        }
        var publishing = Task.Run(async () =>
        {
            try
            {
                await foreach (var snapshot in snapshots.Reader.ReadAllAsync(lifetime.Token))
                    await WriteAsync(new() { Kind = "snapshot", Snapshot = snapshot });
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        });
        var publishingCalibration = Task.Run(async () =>
        {
            try { await foreach (var state in calibrations.Reader.ReadAllAsync(lifetime.Token))
                await WriteAsync(new() { Kind = "calibration", Calibration = state }); }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        });
        try
        {
            long lastId = 0;
            bool initialized = false;
            while (await RpcWire.ReadLineAsync(Console.In, lifetime.Token) is { } line)
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line, RpcWire.Json) ?? throw new InvalidDataException();
                if (request.Id <= lastId) throw new InvalidDataException();
                lastId = request.Id;
                var response = new WorkerMessage { Id = request.Id };
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(request.Operation == "apply" ? 105 : 65));
                try
                {
                    object data;
                    if (request.Operation == "initialize" && !initialized)
                    {
                        initialized = true;
                        service.ConfigureResources(request);
                        data = await service.InitializePeerAsync(request.Identity, request.Name, request.Address, budget.Token);
                    }
                    else if (!initialized) throw new InvalidDataException();
                    else data = request.Operation switch
                    {
                        "refresh" => await service.RefreshAsync(request.Scope, budget.Token),
                        "apply" => await service.ApplyAsync(request.Action ?? throw new InvalidDataException(), budget.Token),
                        "diagnostics" => service.Diagnostics(),
                        "createCalibration" => service.CreateCalibration(request.Handle, request.CalibrationKind),
                        "getCalibration" => service.GetCalibration(request.Handle),
                        "advanceCalibration" => await service.AdvanceCalibrationAsync(request.Handle, request.Revision, request.Input ?? throw new InvalidDataException(), budget.Token),
                        "cancelCalibration" => await service.CancelCalibrationAsync(request.Handle, budget.Token),
                        "hearingProfiles" => await service.ReadHearingProfilesAsync(budget.Token),
                        "selectHearing" => await service.SelectHearingAsync(request.HearingUid, budget.Token),
                        "observedColor" => await service.SetObservedColorAsync(request.ObservedProductId ?? throw new InvalidDataException(),
                            request.ObservedColor ?? throw new InvalidDataException(), budget.Token),
                        "shutdown" => await ShutdownAsync(),
                        _ => throw new InvalidDataException()
                    };
                    response.Success = true; response.Data = JsonSerializer.SerializeToElement(data, RpcWire.Json);
                }
                catch (Exception error) { response.Success = false; response.Error = error is InvalidOperationException or ArgumentException ? error.Message : "设备未能完成操作，请检查连接后重试。"; }
                await WriteAsync(response);
                if (request.Operation == "shutdown") break;
            }
            return 0;
            async Task<bool> ShutdownAsync() { await service.DisconnectAsync(CancellationToken.None); return true; }
        }
        catch (Exception) { return 1; }
        finally { await service.DisposeCalibrationAsync(); lifetime.Cancel(); snapshots.Writer.TryComplete(); calibrations.Writer.TryComplete(); try { await Task.WhenAll(publishing, publishingCalibration); } catch { } }
    }

    internal static async Task<int> EnumerateAsync()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var line = JsonSerializer.Serialize(WindowsBluetooth.EnumeratePaired(), RpcWire.Json);
            if (line.Length > RpcWire.MaximumLineCharacters) return 1;
            await Console.Out.WriteLineAsync(line); await Console.Out.FlushAsync(); return 0;
        }
        catch (Exception) { return 1; }
    }

    internal static ProcessStartInfo StartInfo(string mode)
    {
        var file = Environment.ProcessPath ?? throw new IOException();
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(file).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        else if (!Path.GetFileNameWithoutExtension(file).Equals("HeyMelody.Host", StringComparison.OrdinalIgnoreCase))
        {
            // Test and tooling hosts run the actual backend apphost rather than themselves.
            var apphost = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "HeyMelody.Host.exe");
            start.FileName = File.Exists(apphost) ? apphost : "dotnet";
            if (!File.Exists(apphost)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        start.ArgumentList.Add(mode); return start;
    }
}

internal interface IPeerWorker : IAsyncDisposable
{
    event Action<UiSnapshot>? SnapshotChanged;
    event Action? Unavailable;
    Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token);
    Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token);
    Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token);
}

internal sealed partial class PeerWorker(Func<ProcessStartInfo>? startFactory = null, TimeSpan? testBudget = null) : IPeerWorker, IResourcePeerWorker, ICalibrationPeerWorker
{
    private readonly SemaphoreSlim _commands = new(1), _writes = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<WorkerMessage>> _pending = new();
    private Process? _process;
    private long _nextId;
    private int _stopped;
    public event Action<UiSnapshot>? SnapshotChanged;
    public event Action? Unavailable;

    public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        => RequestAsync<UiSnapshot>(new() { Operation = "initialize", Address = address, Identity = identity, Name = name,
            CatalogGzip = _catalogGzip, Source = _source, ResourceRevision = _resourceRevision, AppMarker = _appMarker, StableKey = _stableKey, Generation = _generation,
            ObservedProductId = _observedProductId, ObservedColor = _observedColor }, token);
    public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token)
        => RequestAsync<UiSnapshot>(new() { Operation = "refresh", Scope = scope }, token);
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        => RequestAsync<UiApplyResult>(new() { Operation = "apply", Action = action }, token);

    private async Task<T> RequestAsync<T>(WorkerRequest request, CancellationToken token)
    {
        await _commands.WaitAsync(token);
        bool submitted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_stopped != 0, this);
            if (_process is null)
            {
                _process = Process.Start(startFactory?.Invoke() ?? NativeWorker.StartInfo("--worker")) ?? throw new IOException();
                _ = DrainErrorsAsync(_process.StandardError);
                _ = ReadAsync(_process.StandardOutput);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            deadline.CancelAfter(testBudget ?? TimeSpan.FromSeconds(request.Operation == "apply" ? 120 : 75));
            request.Id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.Id] = completion;
            await _writes.WaitAsync(deadline.Token);
            try
            {
                var line = JsonSerializer.Serialize(request, RpcWire.Json);
                if (line.Length > RpcWire.MaximumLineCharacters) throw new InvalidDataException();
                submitted = true;
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token);
                await _process.StandardInput.FlushAsync(deadline.Token);
            }
            finally { _writes.Release(); }
            var response = await completion.Task.WaitAsync(deadline.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "设备未能完成操作。");
            if (response.Data is null) throw new InvalidDataException();
            return response.Data.Value.Deserialize<T>(RpcWire.Json) ?? throw new InvalidDataException();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception)
        {
            Stop();
            if (request.Operation == "apply") return (T)(object)new UiApplyResult
            {
                FailureKind = ApplyFailure.WorkerUnavailable, ConnectionState = ConnectionState.Unknown,
                SubmissionAttempted = submitted, AcknowledgementUnknown = submitted, PartialEffects = submitted,
                Message = submitted ? "未收到完整操作结果。设置可能已生效，后台不会重复提交。" : "设置尚未提交，蓝牙后台暂不可用。"
            };
            throw new IOException("设备后台操作未完成。");
        }
        finally { _pending.TryRemove(request.Id, out _); _commands.Release(); }
    }

    private async Task ReadAsync(TextReader reader)
    {
        try
        {
            while (await RpcWire.ReadLineAsync(reader, _lifetime.Token) is { } line)
            {
                var message = JsonSerializer.Deserialize<WorkerMessage>(line, RpcWire.Json) ?? throw new InvalidDataException();
                if (message.Kind == "snapshot" && message.Snapshot is { } snapshot) SnapshotChanged?.Invoke(snapshot);
                else if (message.Kind == "calibration" && message.Calibration is { } calibration) CalibrationChanged?.Invoke(calibration);
                else if (message.Kind == "response" && _pending.TryRemove(message.Id, out var pending)) pending.TrySetResult(message);
                else throw new InvalidDataException();
            }
        }
        catch (Exception) { }
        finally { Stop(); }
    }

    private static async Task DrainErrorsAsync(TextReader reader)
    { var buffer = new char[2048]; try { while (await reader.ReadAsync(buffer) > 0) { } } catch { } }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _lifetime.Cancel();
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("设备后台已中断。"));
        var process = _process;
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        Unavailable?.Invoke();
    }
    private int _disposing;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        if (_stopped == 0 && _process is { HasExited: false })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await RequestAsync<bool>(new() { Operation = "shutdown" }, deadline.Token); } catch { }
        }
        Stop(); _process?.Dispose();
    }

    internal static async Task<IReadOnlyList<BluetoothCandidate>> EnumerateAsync(CancellationToken token)
    {
        using var process = Process.Start(NativeWorker.StartInfo("--catalog-worker")) ?? throw new IOException();
        _ = DrainErrorsAsync(process.StandardError);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            var line = await RpcWire.ReadLineAsync(process.StandardOutput, budget.Token) ?? throw new IOException();
            var result = JsonSerializer.Deserialize<List<BluetoothCandidate>>(line, RpcWire.Json) ?? throw new InvalidDataException();
            await process.WaitForExitAsync(budget.Token);
            if (process.ExitCode != 0 || result.Count > 4096) throw new IOException();
            return result;
        }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    }
}
