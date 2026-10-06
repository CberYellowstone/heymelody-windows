using HeyMelodyWindows.Control;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

internal sealed record DailyResponse(bool Submitted, bool Matched, byte? Status, byte[]? Payload);
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
        bool submitted = false;
        try
        {
            submitted = true;
            await transport.SendAsync(PacketEncoder.Encode(command, seq, payload), timeout.Token).WaitAsync(timeout.Token);
            byte[] buffer = new byte[4096];
            int ignored = 0;
            while (true)
            {
                int count = await transport.ReadAsync(buffer, timeout.Token).WaitAsync(timeout.Token);
                if (count == 0) { _decoder.Complete(); throw new IOException(); }
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
        catch (Exception) { Usable = false; _decoder.Reset(); return new(submitted, false, null, null); }
    }
    public async ValueTask DisposeAsync()
    {
        Usable = false;
        try { await transport.DisposeAsync(); } catch (Exception) { }
    }
}
