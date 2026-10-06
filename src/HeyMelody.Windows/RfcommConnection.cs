namespace HeyMelodyWindows.Windows;

/// <summary>Raw RFCOMM byte transport. The caller enforces the packet whitelist.
/// No discovery, retry, channel fallback, notification registration, or device command is generated.
/// Native sockets stay nonblocking; readiness polls use 100 ms waits. Cancellation is cooperative,
/// not an absolute wall-clock guarantee against an unresponsive OS Bluetooth stack.</summary>
public sealed class RfcommConnection : IAsyncDisposable
{
    private readonly SafeRfcommSocket _socket;
    private readonly object _nativeGate = new();
    private readonly SemaphoreSlim _readGate = new(1, 1), _writeGate = new(1, 1);
    private int _disposed;
    private RfcommConnection(SafeRfcommSocket socket) => _socket = socket;

    /// <summary>Connects once to the supplied SDP-derived Melody channel. Channel 0 and service
    /// fallback are rejected. Supply a deadline token; no automatic reconnection occurs.</summary>
    public static Task<RfcommConnection> ConnectAsync(ulong address, Guid serviceId, uint channel, CancellationToken token)
    {
        Native.Validate(address, serviceId);
        if (channel is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(channel));
        return Task.Run(() => ConnectCore(address, serviceId, channel, token), token);
    }
    private static unsafe RfcommConnection ConnectCore(ulong address, Guid serviceId, uint channel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Native.Startup();
        var raw = Native.socket(Native.AfBth, Native.SockStream, Native.Rfcomm);
        var socketError = raw == -1 ? Native.WSAGetLastError() : 0;
        if (raw == -1) throw Native.Error(socketError, "RFCOMM socket creation failed.");
        var socket = new SafeRfcommSocket(raw);
        try
        {
            uint nonblocking = 1;
            var mode = Native.ioctlsocket(raw, unchecked((int)0x8004667E), ref nonblocking);
            var modeError = mode == 0 ? 0 : Native.WSAGetLastError();
            if (mode != 0) throw Native.Error(modeError, "RFCOMM nonblocking configuration failed.");
            // Port is the explicit SDP result; do not request a second implicit service lookup.
            var peer = new Native.SockAddrBth { Family = Native.AfBth, Address = address, ServiceId = Guid.Empty, Port = channel };
            var connected = Native.connect(raw, ref peer, sizeof(Native.SockAddrBth));
            var error = connected == 0 ? 0 : Native.WSAGetLastError();
            if (connected != 0 && error != Native.WouldBlock) throw Native.Error(error, "RFCOMM connection failed.");
            while (connected != 0)
            {
                token.ThrowIfCancellationRequested();
                if (!Ready(raw, write: true)) continue;
                var socketStatus = 0;
                var size = sizeof(int);
                var status = Native.getsockopt(raw, 0xFFFF, 0x1007, ref socketStatus, ref size);
                var statusError = status == 0 ? 0 : Native.WSAGetLastError();
                if (status != 0) throw Native.Error(statusError, "RFCOMM connection status failed.");
                if (socketStatus != 0) throw Native.Error(socketStatus, "RFCOMM connection failed.");
                break;
            }
            token.ThrowIfCancellationRequested();
            return new RfcommConnection(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => SendCore(bytes, token), token).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
    private unsafe void SendCore(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        using var pinned = bytes.Pin();
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            lock (_nativeGate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                var raw = _socket.DangerousGetHandle();
                if (!Ready(raw, write: true)) continue;
                var count = Native.send(raw, (byte*)pinned.Pointer + offset, Math.Min(bytes.Length - offset, 65536), 0);
                var error = count < 0 ? Native.WSAGetLastError() : 0;
                if (count < 0 && error == Native.WouldBlock) continue;
                if (count < 0) throw Native.Error(error, "RFCOMM send failed.");
                if (count == 0) throw new IOException("RFCOMM closed during send.");
                offset += count;
            }
        }
        token.ThrowIfCancellationRequested();
        ThrowIfDisposed();
    }
    public async Task<int> ReadAsync(Memory<byte> bytes, CancellationToken token)
    {
        ThrowIfDisposed();
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => ReadCore(bytes, token), token).ConfigureAwait(false); }
        finally { _readGate.Release(); }
    }
    private unsafe int ReadCore(Memory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (bytes.IsEmpty) return 0;
        using var pinned = bytes.Pin();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            lock (_nativeGate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                var raw = _socket.DangerousGetHandle();
                if (!Ready(raw, write: false)) continue;
                var count = Native.recv(raw, (byte*)pinned.Pointer, Math.Min(bytes.Length, 65536), 0);
                var error = count < 0 ? Native.WSAGetLastError() : 0;
                if (count < 0 && error == Native.WouldBlock) continue;
                if (count < 0) throw Native.Error(error, "RFCOMM receive failed.");
                return count;
            }
        }
    }
    private static unsafe bool Ready(nint socket, bool write)
    {
        var ready = Native.FdSet.One(socket);
        var errors = Native.FdSet.One(socket);
        var timeout = new Native.TimeVal { Microseconds = 100_000 };
        var selected = Native.select(0, write ? null : &ready, write ? &ready : null, &errors, ref timeout);
        var error = selected < 0 ? Native.WSAGetLastError() : 0;
        if (selected < 0) throw Native.Error(error, "RFCOMM readiness polling failed.");
        return selected > 0;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Prevents further native operations, then closes under the same lock as select/send/recv.
    /// No managed lock is held across an unbounded I/O wait. Native scheduling latency is not bounded.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        lock (_nativeGate) _socket.Dispose();
        // SemaphoreSlim instances are deliberately not disposed while queued/active callers can
        // still release them. They own no WaitHandle; concurrent callers observe _disposed.
        return ValueTask.CompletedTask;
    }
}
