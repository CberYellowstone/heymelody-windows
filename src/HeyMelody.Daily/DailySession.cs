using System.ComponentModel;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

internal sealed record DailyResponse(bool Submitted, bool Matched, byte? Status, byte[]? Payload,
    bool ConnectionInterrupted = false);
internal sealed class DailySession(IControlTransport transport) : IAsyncDisposable
{
    private readonly SequenceAllocator _sequences = new();
    private readonly PacketDecoder _decoder = new();
    public bool Usable { get; private set; } = true;
    public int Remaining => _sequences.Remaining;
    public async Task<DailyResponse> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        if (!Usable || !_sequences.TryTake(out byte seq)) throw new IOException("Daily session unavailable.");
        var key = new RequestKey(command, seq);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        bool submitted = false, eof = false;
        try
        {
            submitted = true;
            await transport.SendAsync(PacketEncoder.Encode(command, seq, payload), timeout.Token).WaitAsync(timeout.Token);
            byte[] buffer = new byte[4096];
            int ignored = 0;
            while (true)
            {
                int count = await transport.ReadAsync(buffer, timeout.Token).WaitAsync(timeout.Token);
                if (count == 0) { eof = true; _decoder.Complete(); throw new EndOfStreamException(); }
                if (count < 0 || count > buffer.Length) throw new FormatException();
                foreach (Packet packet in _decoder.Feed(buffer.AsSpan(0, count)))
                {
                    if (!key.MatchesResponse(packet))
                    { if (++ignored > 256) throw new FormatException(); continue; }
                    byte? status = packet.Payload.Length == 0 ? null : packet.Payload[0];
                    if (status is null) Usable = false;
                    return new(submitted, true, status, packet.Payload);
                }
            }
        }
        catch (Exception failure)
        {
            Usable = false; _decoder.Reset();
            return new(submitted, false, null, null, eof || IsConnectionInterrupted(failure));
        }
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
    public async ValueTask DisposeAsync()
    {
        Usable = false;
        try { await transport.DisposeAsync(); } catch (Exception) { }
    }
}
