using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed class BackendPipeServer(BackendSupervisor supervisor, string? pipeName = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly SemaphoreSlim _exitGate = new(1);
    private readonly object _activationSync = new(), _voteSync = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _votes = [];
    private string? _exitId;
    private Process? _mainLaunch;
    private bool _pendingActivation;
    private Task? _accept;
    public event Func<Task>? ExitApproved;

    public void Start()
    {
        supervisor.EventPublished += Broadcast;
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName ?? BackendEndpoint.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(_lifetime.Token);
                    if (!IsLocalSession(pipe)) { pipe.Dispose(); continue; }
                    if (_clients.Count >= 16) { pipe.Dispose(); continue; }
                    var client = new Client(pipe, this);
                    _clients[client.Key] = client;
                    _ = client.RunAsync();
                }
                catch { pipe.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
    }
    private static bool IsLocalSession(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint processId)) return false;
            using var client = Process.GetProcessById(checked((int)processId));
            using var host = Process.GetCurrentProcess();
            return client.SessionId == host.SessionId;
        }
        catch { return false; }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    private void Broadcast(BackendEvent value)
    { foreach (var client in _clients.Values) if (client.Role is not null) client.SendEvent(value); }

    private async Task HandleAsync(Client client, BackendRequest request)
    {
        if (request.SchemaVersion != 3)
        { client.Reply(request.Id, null, "versionMismatch", "前后端版本不一致，请重新解压完整客户端。"); return; }
        if (request.Operation.Length is 0 or > 32)
        { client.Reply(request.Id, null, "invalidRequest", "请求无效。"); return; }
        if (request.Operation == "subscribe")
        {
            if (request.Role is not ("main" or "manager" or "preview" or "observer" or "calibration"))
            { client.Reply(request.Id, null, "invalidRole", "订阅角色无效。"); return; }
            supervisor.Subscribe(subscription =>
            {
                client.Role = request.Role; client.Reply(request.Id, subscription);
            });
            lock (_activationSync)
            {
                if (client.Role == "main" && _pendingActivation)
                { _pendingActivation = false; client.SendEvent(new() { Kind = "activate", BackendInstanceId = supervisor.InstanceId }); }
            }
            return;
        }
        try
        {
            object data;
            switch (request.Operation)
            {
                case "enumerate":
                    await supervisor.RefreshCatalogAsync(client.Lifetime.Token); data = supervisor.Devices(); break;
                case "enumerateCandidates":
                    await supervisor.RefreshCatalogAsync(client.Lifetime.Token); data = supervisor.Catalog(); break;
                case "saveWhitelist":
                    data = await supervisor.SaveWhitelistAsync(request.DeviceIdentities ?? throw new InvalidDataException(), client.Lifetime.Token); break;
                case "select": case "connect": data = supervisor.Select(Identity()); break;
                case "pause": data = supervisor.Pause(Identity()); break;
                case "resume": data = supervisor.Resume(Identity()); break;
                case "refresh": data = await supervisor.RefreshAsync(Identity(), request.RefreshScope ?? RefreshScope.Full, client.Lifetime.Token); break;
                case "apply":
                    if (request.BackendInstanceId != supervisor.InstanceId || request.DeviceGeneration is not { } generation)
                        throw new InvalidOperationException("后台或连接已变化，未提交设置。");
                    data = await supervisor.ApplyAsync(Identity(), generation,
                        request.Action.Deserialize<UiAction>(RpcWire.Json) ?? throw new InvalidDataException(), client.Lifetime.Token); break;
                case "resourceSettings": data = supervisor.ResourceSettings(); break;
                case "saveResourceSettings":
                    data = await supervisor.SaveResourceSettingsAsync(request.Action.Deserialize<UiResourceSettings>(RpcWire.Json) ?? throw new InvalidDataException(), client.Lifetime.Token); break;
                case "redownloadResources": data = await supervisor.RedownloadResourcesAsync().WaitAsync(client.Lifetime.Token); break;
                case "hearingProfiles":
                    data = await supervisor.HearingProfilesAsync(Identity(), Generation(), client.Lifetime.Token); break;
                case "selectHearing":
                    data = await supervisor.SelectHearingAsync(Identity(), Generation(), NullableInt("uid"), client.Lifetime.Token); break;
                case "createCalibration":
                    data = await supervisor.CreateCalibrationAsync(client.Key, Identity(), Generation(), StringArg("kind"), client.Lifetime.Token); break;
                case "getCalibration": data = supervisor.GetCalibration(client.Key, Handle(), client.Role); break;
                case "advanceCalibration":
                    GenerationCheck();
                    data = await supervisor.AdvanceCalibrationAsync(client.Key, Handle(), request.Revision ?? throw new InvalidDataException(),
                        request.Action.Deserialize<UiCalibrationInput>(RpcWire.Json) ?? throw new InvalidDataException(), client.Lifetime.Token); break;
                case "cancelCalibration": data = await supervisor.CancelCalibrationAsync(client.Key, Handle()); break;
                case "acquireArtwork":
                    data = new { artwork = await supervisor.AcquireArtworkAsync(client.Key, StringArg("productId"), NullableInt("color"), StringArg("role"), client.Lifetime.Token) }; break;
                case "releaseArtwork": data = supervisor.ReleaseArtwork(client.Key, StringArg("leaseId")); break;
                case "exitVote":
                    lock (_voteSync)
                    {
                        if (client.Role is not ("main" or "calibration") || request.ExitRequestId != _exitId || request.Approved is not { } approved
                            || !_votes.TryGetValue(client.Key, out var vote)) throw new InvalidOperationException();
                        client.Reply(request.Id, true);
                        vote.TrySetResult(approved);
                    }
                    return;
                case "activateMain": ActivateMain(false); data = true; break;
                default: throw new InvalidDataException();
            }
            client.Reply(request.Id, data);
        }
        catch (OperationCanceledException) { client.Reply(request.Id, null, "cancelled", "操作已取消。"); }
        catch (Exception error)
        {
            // Do not forward native exceptions, addresses or arbitrary peer-provided text.
            var message = error is InvalidOperationException && error.Message.Length <= 180
                ? error.Message : "后台操作未完成，请检查蓝牙状态后重试。";
            client.Reply(request.Id, null, "operationFailed", message);
        }
        string Identity() => request.Identity ?? throw new InvalidDataException();
        string Handle() => request.CalibrationHandle is { Length: 32 } handle ? handle : throw new InvalidDataException();
        string StringArg(string property) => request.Action.GetProperty(property).GetString() ?? throw new InvalidDataException();
        int? NullableInt(string property) => request.Action.GetProperty(property).ValueKind == JsonValueKind.Null ? null : request.Action.GetProperty(property).GetInt32();
        void GenerationCheck() { if (request.BackendInstanceId != supervisor.InstanceId) throw new InvalidOperationException("后台或连接已变化，未提交设置。"); }
        long Generation() { GenerationCheck(); return request.DeviceGeneration ?? throw new InvalidDataException(); }
    }

    public void ActivateMain(bool allowLaunch)
    {
        lock (_activationSync)
        {
            var main = _clients.Values.FirstOrDefault(c => c.Role == "main" && !c.Lifetime.IsCancellationRequested);
            if (main is not null) { main.SendEvent(new() { Kind = "activate", BackendInstanceId = supervisor.InstanceId }); return; }
            _pendingActivation = true;
            if (!allowLaunch || _mainLaunch is { HasExited: false }) return;
            _mainLaunch?.Dispose(); _mainLaunch = Process.Start(MainStartInfo());
        }
    }
    private static ProcessStartInfo MainStartInfo()
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "HeyMelody.exe"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (File.Exists(executable)) return start;
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../HeyMelody.App/bin", configuration, "net10.0/HeyMelody.dll"));
            if (!File.Exists(dll)) continue;
            start.FileName = "dotnet"; start.ArgumentList.Add(dll); start.WorkingDirectory = Path.GetDirectoryName(dll)!; return start;
        }
        throw new FileNotFoundException("主界面组件缺失，请重新解压完整客户端。");
    }

    public async Task RequestExitAsync()
    {
        if (!await _exitGate.WaitAsync(0)) return;
        try
        {
            Client[] main;
            string id = Guid.NewGuid().ToString("N");
            lock (_voteSync)
            {
                main = _clients.Values.Where(c => c.Role is "main" or "calibration" && !c.Lifetime.IsCancellationRequested).ToArray();
                _exitId = id; _votes.Clear();
                foreach (var client in main) _votes[client.Key] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            foreach (var client in main) client.SendEvent(new() { Kind = "requestExit", BackendInstanceId = supervisor.InstanceId, ExitRequestId = id });
            bool approved;
            try
            {
                Task<bool>[] tasks; lock (_voteSync) tasks = _votes.Values.Select(v => v.Task).ToArray();
                var result = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), _lifetime.Token);
                approved = result.All(value => value);
            }
            catch (Exception) { approved = false; }
            if (!approved)
            { Broadcast(new() { Kind = "exitCancelled", BackendInstanceId = supervisor.InstanceId, ExitRequestId = id, Message = "退出已取消。" }); return; }
            Broadcast(new() { Kind = "stopping", BackendInstanceId = supervisor.InstanceId });
            await supervisor.StopGracefullyAsync();
            if (ExitApproved is { } onExit) await onExit();
        }
        finally { lock (_voteSync) { _votes.Clear(); _exitId = null; } _exitGate.Release(); }
    }

    private void Detach(Client client)
    {
        _clients.TryRemove(client.Key, out _);
        supervisor.ClientDetached(client.Key);
        lock (_voteSync) if (_votes.TryGetValue(client.Key, out var vote)) vote.TrySetResult(true);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); supervisor.EventPublished -= Broadcast;
        foreach (var client in _clients.Values) client.Dispose();
        if (_accept is not null) { try { await _accept; } catch { } }
        _mainLaunch?.Dispose();
    }

    private sealed class Client(NamedPipeServerStream pipe, BackendPipeServer owner) : IDisposable
    {
        private readonly ConcurrentQueue<BackendMessage> _critical = new();
        private readonly Dictionary<string, BackendMessage> _updates = [];
        private readonly object _outboxSync = new();
        private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly SemaphoreSlim _dispatch = new(16);
        private int _criticalCount, _disposed;
        public string Key { get; } = Guid.NewGuid().ToString("N");
        public string? Role;
        public CancellationTokenSource Lifetime { get; } = new();

        public void Reply(long id, object? data, string? errorCode = null, string? error = null)
        {
            Enqueue(new() { Id = id, Success = errorCode is null, ErrorCode = errorCode, Error = error,
                Data = data is null ? null : JsonSerializer.SerializeToElement(data, RpcWire.Json) });
        }
        public void SendEvent(BackendEvent value)
        {
            var message = new BackendMessage { Kind = "event", Event = value };
            if (value.Kind is "snapshot" or "catalog" or "health" or "calibration" or "resources")
            {
                lock (_outboxSync)
                {
                    if (_updates.Count >= 256) { Dispose(); return; }
                    _updates[value.Kind + ":" + (value.Calibration?.Handle ?? value.Snapshot?.Identity)] = message;
                }
                _signal.Writer.TryWrite(0);
            }
            else Enqueue(message);
        }
        private void Enqueue(BackendMessage message)
        {
            if (Lifetime.IsCancellationRequested) return;
            if (Interlocked.Increment(ref _criticalCount) > 128) { Dispose(); return; }
            _critical.Enqueue(message); _signal.Writer.TryWrite(0);
        }
        public async Task RunAsync()
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            var output = OutputAsync(writer);
            try
            {
                long lastId = 0;
                while (await RpcWire.ReadLineAsync(reader, Lifetime.Token) is { } line)
                {
                    var request = JsonSerializer.Deserialize<BackendRequest>(line, RpcWire.Json) ?? throw new InvalidDataException();
                    if (request.Id <= lastId) throw new InvalidDataException(); lastId = request.Id;
                    if (!_dispatch.Wait(0)) throw new InvalidDataException("Too many outstanding requests.");
                    _ = DispatchAsync(request);
                }
            }
            catch (Exception) { }
            finally { Dispose(); owner.Detach(this); try { await output; } catch { } }
        }
        private async Task DispatchAsync(BackendRequest request)
        {
            try { await owner.HandleAsync(this, request); }
            catch (Exception) { Reply(request.Id, null, "operationFailed", "后台操作未完成。"); }
            finally { _dispatch.Release(); }
        }
        private async Task OutputAsync(StreamWriter writer)
        {
            try
            {
                await foreach (var _ in _signal.Reader.ReadAllAsync(Lifetime.Token))
                {
                    while (_critical.TryDequeue(out var message))
                    { Interlocked.Decrement(ref _criticalCount); await WriteAsync(message); }
                    BackendMessage[] updates;
                    lock (_outboxSync) { updates = _updates.Values.ToArray(); _updates.Clear(); }
                    foreach (var message in updates) await WriteAsync(message);
                }
            }
            catch (Exception) { Dispose(); }
            async Task WriteAsync(BackendMessage message)
            {
                var line = JsonSerializer.Serialize(message, RpcWire.Json);
                if (line.Length > RpcWire.MaximumLineCharacters) throw new InvalidDataException();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(10));
                await writer.WriteLineAsync(line.AsMemory(), budget.Token); await writer.FlushAsync(budget.Token);
            }
        }
        public void Dispose()
        { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; Lifetime.Cancel(); pipe.Dispose(); _signal.Writer.TryComplete(); }
    }
}
