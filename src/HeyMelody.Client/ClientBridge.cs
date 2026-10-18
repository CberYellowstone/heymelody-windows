using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

/// <summary>A client of the shared tray backend. Disposing a client never terminates the backend.</summary>
public sealed partial class ClientBridge : IHeadsetEventService, IDeviceManagementService, IResourceSettingsService, ICalibrationService, IArtworkService
{
    private readonly string _pipeName;
    private readonly Action? _startBackend;
    private readonly TimeSpan? _testBudget;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private sealed record QueuedEvent(BackendEvent Value, Connection? Source);
    private readonly Channel<QueuedEvent> _events = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<QueuedEvent> _spectra = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _stateGate = new();
    private readonly Dictionary<string, UiSnapshot> _snapshots = [];
    private Connection? _connection;
    private long _nextId;
    private string? _backendInstanceId, _selectedIdentity, _role;
    private int _reconnecting;
    private volatile bool _disposed, _stopping, _versionMismatch;
    private DateTimeOffset _lastStart;

    public event Action<BackendEvent>? EventReceived;

    public ClientBridge() : this(BackendEndpoint.PipeName, StartBackend) { }

    internal ClientBridge(string pipeName, Action? startBackend = null, TimeSpan? testBudget = null)
    {
        _pipeName = pipeName; _startBackend = startBackend; _testBudget = testBudget;
        _ = Task.Run(() => DeliverEventsAsync(_events));
        _ = Task.Run(() => DeliverEventsAsync(_spectra));
    }

    public async Task<BackendSubscription> SubscribeAsync(string role, CancellationToken cancellationToken)
    {
        _role = role;
        BackendSubscription subscription;
        try
        {
            subscription = await RequestAsync<BackendSubscription>(new() { Operation = "subscribe", Role = role },
                TimeSpan.FromSeconds(90), cancellationToken, (baseline, connection) =>
                {
                    lock (_stateGate)
                    {
                        if (!ReferenceEquals(_connection, connection) || connection.Closed)
                            throw new IOException("订阅连接已变化。");
                        SetBackendInstance(baseline.BackendInstanceId);
                        foreach (var snapshot in baseline.Snapshots) Remember(snapshot);
                    }
                });
        }
        catch (IOException)
        {
            if (!_versionMismatch && !_disposed && !_stopping && !cancellationToken.IsCancellationRequested)
                _ = ReconnectAsync();
            throw;
        }
        return subscription;
    }

    public async Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken) =>
        await RequestAsync<List<UiDevice>>(new() { Operation = "enumerate" }, TimeSpan.FromSeconds(90), cancellationToken);

    public Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken cancellationToken) =>
        RequestAsync<UiDeviceCatalog>(
            new() { Operation = "enumerateCandidates" }, TimeSpan.FromSeconds(90), cancellationToken);

    public Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken cancellationToken) =>
        RequestAsync<UiWhitelistSaveResult>(
            new() { Operation = "saveWhitelist", DeviceIdentities = identities.ToArray() }, TimeSpan.FromSeconds(90), cancellationToken);

    public async Task<UiSnapshot> SelectAsync(string identity, CancellationToken cancellationToken)
    {
        var snapshot = await RequestAsync<UiSnapshot>(new() { Operation = "select", Identity = identity }, TimeSpan.FromSeconds(15), cancellationToken);
        lock (_stateGate) { _selectedIdentity = identity; Remember(snapshot); }
        return snapshot;
    }

    public async Task<UiSnapshot> ResumeAsync(string identity, CancellationToken cancellationToken)
    {
        var snapshot = await RequestAsync<UiSnapshot>(new() { Operation = "resume", Identity = identity }, TimeSpan.FromSeconds(90), cancellationToken);
        lock (_stateGate) { _selectedIdentity = identity; Remember(snapshot); }
        return snapshot;
    }

    public async Task<UiSnapshot> PauseAsync(string identity, CancellationToken cancellationToken)
    {
        var snapshot = await RequestAsync<UiSnapshot>(new() { Operation = "pause", Identity = identity }, TimeSpan.FromSeconds(15), cancellationToken);
        lock (_stateGate) Remember(snapshot);
        return snapshot;
    }

    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken) => ResumeAsync(identity, cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _ = await PauseAsync(SelectedIdentity(), cancellationToken);
    }

    public Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshAsync(SelectedIdentity(), RefreshScope.Full, cancellationToken);

    public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken cancellationToken) =>
        RefreshAsync(SelectedIdentity(), scope, cancellationToken);

    public async Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken cancellationToken)
    {
        var snapshot = await RequestAsync<UiSnapshot>(new() { Operation = "refresh", Identity = identity, RefreshScope = scope },
                TimeSpan.FromSeconds(90), cancellationToken);
        lock (_stateGate) Remember(snapshot);
        return snapshot;
    }

    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            string identity = SelectedIdentity();
            long generation = _snapshots.TryGetValue(identity, out var snapshot) ? snapshot.DeviceGeneration : 0;
            return ApplyAsync(identity, generation, action, cancellationToken);
        }
    }

    public async Task<UiApplyResult> ApplyAsync(string identity, long deviceGeneration, UiAction action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RequestAsync<UiApplyResult>(new()
            {
                Operation = "apply", Identity = identity, DeviceGeneration = deviceGeneration,
                Action = JsonSerializer.SerializeToElement(action, RpcWire.Json)
            }, TimeSpan.FromSeconds(120), cancellationToken);
            if (result.After is { } snapshot) lock (_stateGate) Remember(snapshot);
            return result;
        }
        catch (BackendOperationException failure)
        {
            return new UiApplyResult { BackendReceiptReceived = true, FailureKind = ApplyFailure.StateUnavailable,
                ConnectionState = ConnectionState.Unknown, Message = failure.Message };
        }
        catch (BackendUnavailableException failure)
        {
            // Never retry a write: a lost receipt does not mean the device rejected it.
            return new UiApplyResult
            {
                BackendReceiptReceived = false, FailureKind = ApplyFailure.WorkerUnavailable,
                ConnectionState = ConnectionState.Unknown, SubmissionAttempted = failure.Submitted,
                AcknowledgementUnknown = failure.Submitted, PartialEffects = failure.Submitted,
                Message = failure.Submitted ? "未收到完整操作结果。设置可能已生效，请等待后台重新读取状态后再操作。" : failure.Message
            };
        }
    }

    public async Task AcknowledgeExitAsync(string exitRequestId, bool approved, CancellationToken cancellationToken)
    {
        _ = await RequestAsync<bool>(new() { Operation = "exitVote", ExitRequestId = exitRequestId, Approved = approved },
            TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task ActivateMainUiAsync(CancellationToken cancellationToken)
    {
        _ = await RequestAsync<bool>(new() { Operation = "activateMain" }, TimeSpan.FromSeconds(15), cancellationToken);
    }

    private string SelectedIdentity()
    {
        lock (_stateGate) return _selectedIdentity ?? throw new InvalidOperationException("请先选择耳机。");
    }

    private async Task<T> RequestAsync<T>(BackendRequest request, TimeSpan budget, CancellationToken token,
        Action<T, Connection>? onResult = null)
    {
        bool submitted = false, recordWritten = false;
        Connection? connection = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.CancelAfter(_testBudget ?? budget);
        var pending = new TaskCompletionSource<BackendMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            connection = await EnsureConnectionAsync(linked.Token);
            lock (_stateGate) request.BackendInstanceId = _backendInstanceId;
            await connection.WriteGate.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // IDs follow wire order even when callers race for the write lock.
                request.Id = Interlocked.Increment(ref _nextId);
                string record = JsonSerializer.Serialize(request, RpcWire.Json);
                if (record.Length > RpcWire.MaximumLineCharacters) throw new InvalidDataException("后台请求过大。");
                connection.Pending[request.Id] = pending;
                submitted = true;
                await connection.Writer.WriteLineAsync(record.AsMemory(), linked.Token);
                await connection.Writer.FlushAsync(linked.Token);
                recordWritten = true;
            }
            finally { connection.WriteGate.Release(); }
            var response = await pending.Task.WaitAsync(linked.Token);
            if (!response.Success)
            {
                if (response.ErrorCode == "versionMismatch")
                {
                    _versionMismatch = true;
                    throw new BackendOperationException("后台与界面版本不一致，请从托盘退出后台后重新打开完整客户端。");
                }
                throw new BackendOperationException(response.Error ?? "后台操作未完成，请重新读取耳机状态。");
            }
            if (response.Data is not { } data) throw new InvalidDataException("后台返回了无效结果。");
            var result = data.Deserialize<T>(RpcWire.Json) ?? throw new InvalidDataException("后台返回了无效结果。");
            onResult?.Invoke(result, connection);
            return result;
        }
        catch (BackendOperationException) { throw; }
        catch (Exception failure) when (failure is IOException or InvalidDataException or TimeoutException
            or OperationCanceledException or ObjectDisposedException or JsonException or DecoderFallbackException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (submitted && !recordWritten) connection?.Close(failure);
            throw new BackendUnavailableException(submitted && request.Operation == "apply",
                _versionMismatch ? "后台与界面版本不一致，请从托盘退出后台后重新打开完整客户端。" : "蓝牙后台暂时不可用，请等待重连。", failure);
        }
        finally { connection?.Pending.TryRemove(request.Id, out _); }
    }

    private async Task<Connection> EnsureConnectionAsync(CancellationToken token)
    {
        await _connectGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_versionMismatch) throw new BackendOperationException("后台与界面版本不一致，请从托盘退出后台后重新打开完整客户端。");
            if (_stopping) throw new IOException("后台正在退出。");
            lock (_stateGate) if (_connection is { Closed: false } connected) return connected;
            var pipe = NewPipe();
            try { await pipe.ConnectAsync(300, token); }
            catch (TimeoutException)
            {
                pipe.Dispose();
                if (_startBackend is null) throw;
                if (DateTimeOffset.UtcNow - _lastStart > TimeSpan.FromSeconds(10))
                { _lastStart = DateTimeOffset.UtcNow; _startBackend(); }
                pipe = NewPipe();
                try { await pipe.ConnectAsync(15000, token); }
                catch { pipe.Dispose(); throw; }
            }
            catch { pipe.Dispose(); throw; }
            var connection = new Connection(pipe);
            lock (_stateGate) _connection = connection;
            _ = Task.Run(() => ReadMessagesAsync(connection));
            return connection;
        }
        finally { _connectGate.Release(); }
    }

    private NamedPipeClientStream NewPipe() => new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    private async Task ReadMessagesAsync(Connection connection)
    {
        Exception failure = new EndOfStreamException();
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                string? record = await RpcWire.ReadLineAsync(connection.Reader, _lifetime.Token);
                if (record is null) break;
                var message = JsonSerializer.Deserialize<BackendMessage>(record, RpcWire.Json) ?? throw new InvalidDataException();
                if (message.SchemaVersion != 4)
                { _versionMismatch = true; throw new InvalidDataException("后台通信版本不一致。"); }
                if (message.Kind == "response" && message.Id > 0 && message.Id <= Interlocked.Read(ref _nextId))
                {
                    if (connection.Pending.TryRemove(message.Id, out var pending)) pending.TrySetResult(message);
                }
                else if (message.Kind == "event" && message.Event is { } notification)
                {
                    lock (_stateGate)
                    {
                        if (!ReferenceEquals(_connection, connection) || connection.Closed) continue;
                        if (notification.Kind == "stopping") _stopping = true;
                        SetBackendInstance(notification.BackendInstanceId);
                        if (notification.Snapshot is { } snapshot) Remember(snapshot);
                        if (notification.CalibrationSpectrum is not null) _spectra.Writer.TryWrite(new(notification, connection));
                        else if (!_events.Writer.TryWrite(new(notification, connection))) throw new IOException("界面无法及时接收后台事件。");
                    }
                }
                else throw new InvalidDataException("无效的后台消息。");
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            connection.Close(failure);
            bool reconnect = false;
            lock (_stateGate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _backendInstanceId = null; _snapshots.Clear();
                    if (!_disposed && !_stopping)
                    {
                        _events.Writer.TryWrite(new(new BackendEvent { Kind = "unavailable", Message = _versionMismatch
                            ? "后台与界面版本不一致，请从托盘退出后台后重新打开完整客户端。" : "蓝牙后台已中断，正在重新连接。" }, connection));
                        reconnect = !_versionMismatch && _role is not null;
                    }
                }
            }
            if (reconnect) _ = ReconnectAsync();
        }
    }

    private async Task ReconnectAsync()
    {
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
        try
        {
            while (!_disposed && !_stopping && !_versionMismatch)
            {
                await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
                try
                {
                    var subscription = await SubscribeAsync(_role!, _lifetime.Token);
                    lock (_stateGate)
                    {
                        if (_connection is not { Closed: false } connection || _backendInstanceId != subscription.BackendInstanceId) continue;
                        _events.Writer.TryWrite(new(new BackendEvent { Kind = "catalog", BackendInstanceId = subscription.BackendInstanceId,
                            Catalog = subscription.Catalog, Devices = subscription.Devices }, connection));
                        foreach (var snapshot in subscription.Snapshots)
                            _events.Writer.TryWrite(new(new BackendEvent { Kind = "snapshot", BackendInstanceId = subscription.BackendInstanceId, Snapshot = snapshot }, connection));
                    }
                    return;
                }
                catch (BackendOperationException) when (_versionMismatch) { return; }
                catch (Exception) when (!_lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
            bool restart;
            lock (_stateGate) restart = !_disposed && !_stopping && !_versionMismatch && _role is not null
                && (_connection is null || _connection.Closed);
            if (restart) _ = ReconnectAsync();
        }
    }

    private async Task DeliverEventsAsync(Channel<QueuedEvent> channel)
    {
        try
        {
            await foreach (var queued in channel.Reader.ReadAllAsync(_lifetime.Token))
            {
                var notification = queued.Value;
                lock (_stateGate)
                {
                    if (queued.Source is { } source && (!ReferenceEquals(_connection, source)
                        || source.Closed && notification.Kind is not ("stopping" or "unavailable"))) continue;
                }
                var handlers = EventReceived;
                if (handlers is null) continue;
                foreach (Action<BackendEvent> handler in handlers.GetInvocationList())
                    try { handler(notification); } catch (Exception) { /* Subscribers cannot stop the receive pump. */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Call only with _stateGate held; revisions from another backend/generation are incomparable.
    private void SetBackendInstance(string backend)
    {
        if (string.IsNullOrEmpty(backend) || _backendInstanceId == backend) return;
        _backendInstanceId = backend; _snapshots.Clear(); _selectedIdentity = null;
    }

    private void Remember(UiSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(snapshot.BackendInstanceId))
        {
            if (_backendInstanceId is null) SetBackendInstance(snapshot.BackendInstanceId);
            else if (_backendInstanceId != snapshot.BackendInstanceId) return;
        }
        if (_snapshots.TryGetValue(snapshot.Identity, out var old) &&
            old.BackendInstanceId == snapshot.BackendInstanceId &&
            (snapshot.DeviceGeneration < old.DeviceGeneration ||
             snapshot.DeviceGeneration == old.DeviceGeneration && snapshot.Revision < old.Revision)) return;
        _snapshots[snapshot.Identity] = snapshot;
    }

    private static void StartBackend()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "backend", "HeyMelody.Host.exe");
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "HeyMelody.Host.exe");
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory };
        if (!File.Exists(executable))
        {
            string? dll = new[] { "Debug", "Release" }.Select(configuration => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../HeyMelody.Host/bin", configuration, "net10.0-windows10.0.19041.0/HeyMelody.Host.dll"))).FirstOrDefault(File.Exists);
            if (dll is null) throw new FileNotFoundException("蓝牙后台组件缺失，请重新解压完整客户端。");
            start.FileName = "dotnet"; start.ArgumentList.Add(dll);
        }
        using var process = Process.Start(start) ?? throw new IOException("无法启动蓝牙后台。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _events.Writer.TryComplete(); _spectra.Writer.TryComplete();
        await _connectGate.WaitAsync();
        try { _connection?.Close(new ObjectDisposedException(nameof(ClientBridge))); _connection = null; }
        finally { _connectGate.Release(); }
    }

    private sealed class Connection(NamedPipeClientStream pipe)
    {
        public StreamReader Reader { get; } = new(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        public StreamWriter Writer { get; } = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public ConcurrentDictionary<long, TaskCompletionSource<BackendMessage>> Pending { get; } = new();
        private int _closed;
        public bool Closed => Volatile.Read(ref _closed) != 0;
        public void Close(Exception error)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            pipe.Dispose();
            foreach (var item in Pending) if (Pending.TryRemove(item.Key, out var pending)) pending.TrySetException(error);
        }
    }

    private sealed class BackendOperationException(string message) : IOException(message);
    private sealed class BackendUnavailableException(bool submitted, string message, Exception inner) : IOException(message, inner)
    { public bool Submitted { get; } = submitted; }
}
