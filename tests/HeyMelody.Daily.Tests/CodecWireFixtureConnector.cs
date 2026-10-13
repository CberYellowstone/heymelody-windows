using HeyMelodyWindows.Control;

// Replaces only the synthetic codec ACK with independent wire bytes, preserving
// Fake's identity, capability and submission accounting for the other reads.
internal sealed class CodecWireFixtureConnector(Fake inner, Func<byte, byte[]> codecReply,
    int chunkSize = 4096, bool holdCodecSend = false) : IControlConnector
{
    internal static byte[] Ack(byte sequence)
    {
        byte[] wire = Convert.FromHexString("AA09FC7E14810002000008");
        wire[6] = sequence;
        return wire;
    }

    internal static byte[] PresetNotification(byte sequence)
    {
        byte[] wire = Convert.FromHexString("AA08FC7E040500010000");
        wire[6] = sequence;
        return wire;
    }

    internal static byte[] Air4ProV125Ack(byte sequence, byte codec = 8)
    {
        // Reconstructed from the v125 ARM handler and sender; not a device capture.
        byte[] wire = Convert.FromHexString("AA0A00001481000200000800");
        wire[6] = sequence; wire[10] = codec;
        return wire;
    }

    public async Task<IControlTransport> OpenAsync(CancellationToken token) =>
        new Transport(await inner.OpenAsync(token), codecReply, chunkSize, holdCodecSend);

    private sealed class Transport(IControlTransport inner, Func<byte, byte[]> codecReply,
        int chunkSize, bool holdCodecSend) : IControlTransport
    {
        private byte[] _pending = [];
        private int _offset;

        public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            await inner.SendAsync(bytes, token);
            if (holdCodecSend && bytes.Length >= 9 && bytes.Span[4] == 0x14 && bytes.Span[5] == 0x01)
                await Task.Delay(Timeout.Infinite, token);
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_offset == _pending.Length)
            {
                int count = await inner.ReadAsync(buffer, token);
                if (count == 0) return 0;
                byte[] received = buffer.Span[..count].ToArray();
                // The base Fake emits this one-byte-length ACK as one complete
                // read. Rewrite it without using the production frame encoder.
                _pending = count >= 9 && received[0] == 0xAA && received[1] < 128
                    && received[4] == 0x14 && received[5] == 0x81
                    ? codecReply(received[6]) : received;
                _offset = 0;
            }
            int copied = Math.Min(Math.Min(chunkSize, buffer.Length), _pending.Length - _offset);
            _pending.AsMemory(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
