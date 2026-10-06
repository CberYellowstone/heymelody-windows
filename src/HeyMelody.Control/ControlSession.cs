using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Control;

internal sealed class ControlSession(IControlTransport transport, int number, HiResWorkflowOptions options,
    HiResWorkflowResult result) : IAsyncDisposable
{
    private readonly SequenceAllocator _sequences = new();
    private readonly PacketDecoder _decoder = new();
    private bool _usable = true;
    public int Number => number;
    public CommandCapabilities? Capabilities { get; set; }

    public async Task<(ControlAction Action, byte[]? Payload)> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        if (!ControlPolicy.IsAllowed(command, payload)) throw new InvalidOperationException("Outside enable-only control policy.");
        var action = new ControlAction { CommandHex = $"0x{command:X4}", PayloadHex = Convert.ToHexString(payload), ControlConnectionNumber = number };
        result.Actions.Add(action);
        if (!_usable || !_sequences.TryTake(out byte sequence))
        {
            action.Disposition = "session-unavailable";
            return (action, null);
        }
        action.Sequence = sequence;
        var key = new RequestKey(command, sequence);
        byte[] frame = PacketEncoder.Encode(command, sequence, payload);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            // A send can fail after partial transmission. Record submission before entering the transport.
            action.Submitted = true;
            if (command is 0x0403 or 0x0422)
            {
                result.SettingsAttempted++;
                if (command == 0x0403 && payload[0] == 0x18) result.HiResSetSubmitted = true;
            }
            await transport.SendAsync(frame, timeout.Token).WaitAsync(timeout.Token);
            var buffer = new byte[4096];
            while (true)
            {
                int count = await transport.ReadAsync(buffer, timeout.Token).WaitAsync(timeout.Token);
                if (count == 0) { _decoder.Complete(); action.Disposition = "disconnected"; break; }
                if (count < 0 || count > buffer.Length) throw new FormatException();
                Packet? matched = _decoder.Feed(buffer.AsSpan(0, count)).FirstOrDefault(key.MatchesResponse);
                if (matched is null) continue;
                action.ResponseMatched = true;
                action.Status = matched.Payload.Length > 0 ? matched.Payload[0] : null;
                action.Success = action.Status == 0;
                action.Disposition = action.Status is null ? "missing-status" : action.Success ? "response" : "device-rejected";
                if (action.Status is null) _usable = false;
                return (action, matched.Payload);
            }
        }
        catch (OperationCanceledException)
        {
            action.Disposition = token.IsCancellationRequested ? "cancelled" : "timeout";
        }
        catch (FormatException) { action.Disposition = "malformed-frame"; }
        catch (Exception) { action.Disposition = "transport-failure"; }
        _usable = false;
        _decoder.Reset();
        action.Error = "Control response unavailable; this submission must not be replayed.";
        return (action, null);
    }

    public async ValueTask DisposeAsync()
    {
        _usable = false;
        try { await transport.DisposeAsync(); }
        catch (Exception) { result.Warnings.Add("Control transport disposal failed."); }
    }
}
