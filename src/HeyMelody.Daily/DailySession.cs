using System.ComponentModel;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

internal sealed record DailyResponse(bool Submitted, bool Matched, byte? Status, byte[]? Payload,
    bool ConnectionInterrupted = false, byte? Sequence = null, string? Failure = null);
internal sealed class DailySession : IAsyncDisposable
{
    private readonly IControlTransport _transport;
    private readonly SequenceAllocator _sequences = new();
    private readonly PacketDecoder _decoder = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _pendingGate = new();
    private readonly Dictionary<ushort, byte> _filterSequences = [];
    private Task _reader = Task.CompletedTask;
    private RequestKey? _key;
    private TaskCompletionSource<Packet>? _reply;
    private Exception? _failure;
    private int _usable = 1, _disposed, _readerStarted;
    public event Action<Packet>? Notification;
    public event Action<Exception>? Ended;
    public bool Usable => Volatile.Read(ref _usable) != 0;
    public int Remaining => _sequences.Remaining;
    public DailySession(IControlTransport transport)
    {
        _transport = transport;
    }
    public void StartReader()
    {
        if (Interlocked.Exchange(ref _readerStarted, 1) != 0) return;
        // Wire consumers before starting so the first unsolicited packet cannot be lost.
        _reader = Task.Run(ReadPumpAsync);
    }
    /// <param name="optionalTimeout">Set only for reads whose silence means "unknown"; such a timeout keeps the session.</param>
    public async Task<DailyResponse> RequestAsync(ushort command, byte[] payload, CancellationToken token, TimeSpan? optionalTimeout = null)
    {
        if (!Usable || !_sequences.TryTake(out byte seq)) throw new IOException("Daily session unavailable.");
        var completion = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            if (_reply is not null) throw new InvalidOperationException("Requests must be serialized.");
            if (!Usable) throw new IOException("Daily session unavailable.");
            _key = new(command, seq); _reply = completion;
            // A hearing-filter request begins/retries the entire pair of filter loads.
            if (command == 0x0116) _filterSequences.Remove(0x011F);
            if (command is 0x0116 or 0x011F) _filterSequences[command] = seq;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(optionalTimeout ?? TimeSpan.FromSeconds(4));
        bool submitted = false, sent = false;
        try
        {
            submitted = true;
            await _transport.SendAsync(PacketEncoder.Encode(command, seq, payload), timeout.Token).WaitAsync(timeout.Token);
            sent = true;
            Packet packet = await completion.Task.WaitAsync(timeout.Token);
            byte? status = packet.Payload.Length == 0 ? null : packet.Payload[0];
            if (status is null) Stop(new FormatException("Missing response status."));
            return new(submitted, true, status, packet.Payload, Sequence: packet.Sequence,
                Failure: status is null ? "回执缺少状态字节" : null);
        }
        catch (OperationCanceledException) when (optionalTimeout is not null && sent && Usable
            && !token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            // A late reply cannot match a later request key and is not a notification command.
            return new(submitted, false, null, null);
        }
        catch (Exception failure)
        {
            Exception actual = _failure ?? failure;
            Stop(actual);
            return new(submitted, false, null, null, IsConnectionInterrupted(actual), Failure: DescribeFailure(actual));
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_reply, completion)) { _reply = null; _key = null; }
            }
        }
    }
    private async Task ReadPumpAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int count = await _transport.ReadAsync(buffer, _lifetime.Token);
                if (count == 0)
                {
                    // EOF itself establishes a broken connection even if the last frame was partial.
                    try { _decoder.Complete(); } catch (FormatException) { }
                    throw new EndOfStreamException("RFCOMM closed.");
                }
                if (count < 0 || count > buffer.Length) throw new FormatException("Invalid transport read size.");
                foreach (Packet packet in _decoder.Feed(buffer.AsSpan(0, count)))
                {
                    bool matched, currentFilter;
                    bool filter = packet.Command is 0x8116 or 0x811F;
                    lock (_pendingGate)
                    {
                        matched = _key is RequestKey key && key.MatchesResponse(packet);
                        if (matched) _reply!.TrySetResult(packet);
                        currentFilter = filter && _filterSequences.TryGetValue((ushort)(packet.Command & 0x7FFF), out byte sequence)
                            && sequence == packet.Sequence;
                    }
                    if (currentFilter || !filter && !matched && NotificationParsers.IsNotificationCommand(packet.Command))
                        Notification?.Invoke(packet);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception failure) { Stop(failure); }
    }
    private void Stop(Exception failure)
    {
        if (Interlocked.Exchange(ref _usable, 0) == 0) return;
        lock (_pendingGate) { _failure = failure; _reply?.TrySetException(failure); }
        _lifetime.Cancel();
        Ended?.Invoke(failure);
    }
    private static bool IsConnectionInterrupted(Exception failure) => failure switch
    {
        // A bad frame or a deadline establishes an unknown response, not a broken
        // Bluetooth connection. The native RFCOMM adapter reports Winsock errors
        // as Win32Exception, so retain only errors that establish a failed channel.
        IOException when failure is not InvalidDataException => true,
        Win32Exception socket => socket.NativeErrorCode is 10050 or 10051 or 10052 or 10053 or 10054
            or 10057 or 10058 or 10064 or 10065,
        _ => false
    };
    // Only fixed descriptions and numeric native error codes leave the transport.
    // Exception messages can contain peer addresses or received data.
    internal static string DescribeFailure(Exception failure) => failure switch
    {
        OperationCanceledException => "等待回执超时",
        FormatException or InvalidDataException => "控制数据格式无效",
        Win32Exception socket => $"Windows 蓝牙错误 {socket.NativeErrorCode}",
        IOException => "控制通道已中断",
        _ => "控制请求失败"
    };
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _usable, 0); _lifetime.Cancel();
        lock (_pendingGate) _reply?.TrySetCanceled(_lifetime.Token);
        try { await _transport.DisposeAsync(); } catch (Exception) { }
        await _reader;
        _lifetime.Dispose();
    }
}
