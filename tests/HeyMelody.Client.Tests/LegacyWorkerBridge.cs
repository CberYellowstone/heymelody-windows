using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

/// <summary>Historical schema-1 stdio fixture for private-worker failure and receipt tests.</summary>
internal sealed class LegacyWorkerBridge : IHeadsetService, IDeviceManagementService
{
    private const int MaximumRecord = RpcWire.MaximumLineCharacters;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _worker;
    private RpcLineReader? _workerOutput;
    private Task? _errorDrain;
    private long _nextId;
    private bool _disposed;
    private readonly Func<ProcessStartInfo> _workerFactory;
    private readonly TimeSpan? _testBudget;
    private static readonly JsonSerializerOptions Json = RpcWire.Json;

    internal LegacyWorkerBridge(Func<ProcessStartInfo> workerFactory, TimeSpan? testBudget = null)
    { ArgumentNullException.ThrowIfNull(workerFactory); _workerFactory = workerFactory; _testBudget = testBudget; }

    public async Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken) =>
        await RequestAsync<List<UiDevice>>("enumerate", null, null, TimeSpan.FromSeconds(75), cancellationToken);

    public Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken cancellationToken) =>
        RequestAsync<UiDeviceCatalog>("enumerateCandidates", null, null, TimeSpan.FromSeconds(75), cancellationToken);

    public Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken cancellationToken) =>
        RequestAsync<UiWhitelistSaveResult>("saveWhitelist", null, null, TimeSpan.FromSeconds(75), cancellationToken,
            deviceIdentities: identities.ToArray());

    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken) =>
        RequestAsync<UiSnapshot>("connect", identity, null, TimeSpan.FromSeconds(90), cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken) =>
        _ = await RequestAsync<bool>("disconnect", null, null, TimeSpan.FromSeconds(10), cancellationToken);

    public Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        RequestAsync<UiSnapshot>("refresh", null, null, TimeSpan.FromSeconds(75), cancellationToken);
    public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken cancellationToken) =>
        RequestAsync<UiSnapshot>("refresh", null, null, TimeSpan.FromSeconds(75), cancellationToken, scope);

    public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken)
    {
        try
        {
            return await RequestAsync<UiApplyResult>("apply", null, action, TimeSpan.FromSeconds(120), cancellationToken);
        }
        catch (WorkerUnavailableException failure)
        {
            // The worker can disappear after a setter has reached the peer. Never resend it.
            return new UiApplyResult
            {
                BackendReceiptReceived = false,
                FailureKind = ApplyFailure.WorkerUnavailable,
                ConnectionState = ConnectionState.Unknown,
                SubmissionAttempted = failure.RequestSubmitted,
                AcknowledgementUnknown = failure.RequestSubmitted,
                PartialEffects = failure.RequestSubmitted,
                Message = failure.RequestSubmitted
                    ? "未收到完整操作结果。设置可能已生效，请重新连接并读取状态后再操作。"
                    : "尚未发送设置。请重新连接耳机后再操作。"
            };
        }
    }

    private async Task<T> RequestAsync<T>(string operation, string? identity, UiAction? action,
        TimeSpan budget, CancellationToken token, RefreshScope? scope = null, string[]? deviceIdentities = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.CancelAfter(_testBudget ?? budget);
        bool entered = false, submitted = false;
        try
        {
            await _gate.WaitAsync(linked.Token); entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartWorker();
            long id = checked(++_nextId);
            var request = JsonSerializer.Serialize(new { schemaVersion = 1, id, operation, identity, action,
                refreshScope = scope, deviceIdentities }, Json);
            if (request.Length > MaximumRecord) throw new InvalidDataException();
            submitted = true;
            await _worker!.StandardInput.WriteLineAsync(request.AsMemory(), linked.Token);
            await _worker.StandardInput.FlushAsync(linked.Token);
            string? record = await _workerOutput!.ReadLineAsync(linked.Token);
            if (record is null) throw new EndOfStreamException();
            var response = JsonSerializer.Deserialize<RpcResponse>(record, Json) ?? throw new InvalidDataException();
            if (response.SchemaVersion != 1 || response.Id != id) throw new InvalidDataException();
            if (!response.Success)
                throw new WorkerUnavailableException(submitted && operation == "apply", "蓝牙后台操作未完成，请重新连接耳机。", stopWorker: false);
            if (response.Data is null) throw new InvalidDataException();
            return response.Data.Value.Deserialize<T>(Json) ?? throw new InvalidDataException();
        }
        catch (WorkerUnavailableException failure)
        {
            if (failure.StopWorker && entered) StopWorker();
            throw;
        }
        catch (Exception) when (entered)
        {
            StopWorker();
            throw new WorkerUnavailableException(submitted && operation == "apply", "蓝牙后台已中断，请重新连接耳机。");
        }
        finally { if (entered) _gate.Release(); }
    }

    private void StartWorker()
    {
        if (_worker is { HasExited: false }) return;
        StopWorker();
        _worker = Process.Start(_workerFactory()) ?? throw new IOException();
        _workerOutput = new(_worker.StandardOutput.BaseStream);
        _errorDrain = DrainAsync(_worker.StandardError);
    }

    private static async Task DrainAsync(TextReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer) != 0) { } } catch (Exception) { }
    }

    private void StopWorker()
    {
        var worker = _worker; _worker = null; _workerOutput = null;
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch (Exception) { }
        worker.Dispose();
        _errorDrain = null;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try { if (!_disposed) { _disposed = true; StopWorker(); } }
        finally { _gate.Release(); }
    }

    private sealed class WorkerUnavailableException(bool submitted, string message, bool stopWorker = true) : IOException(message)
    {
        public bool RequestSubmitted { get; } = submitted;
        public bool StopWorker { get; } = stopWorker;
    }
}
