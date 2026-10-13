using System.Buffers.Binary;
namespace HeyMelodyWindows.Protocol;

public sealed record Packet(ushort Command, byte Sequence, byte[] Payload);

public static class PacketEncoder
{
    public static byte[] Encode(ushort command, byte sequence, ReadOnlySpan<byte> payload, int maximumFrameBytes = 660)
    {
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));
        if (maximumFrameBytes < 10 || maximumFrameBytes > PacketDecoder.MaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        var inner = new byte[payload.Length + 5];
        BinaryPrimitives.WriteUInt16LittleEndian(inner, command);
        inner[2] = sequence;
        BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(3), (ushort)payload.Length);
        payload.CopyTo(inner.AsSpan(5));
        var output = new List<byte>();
        if (FrameSize(inner.Length + 2) <= maximumFrameBytes) WriteFrame(output, 0, 0, inner);
        else
        {
            int capacity = maximumFrameBytes - 4;
            while (FrameSize(capacity + 3) > maximumFrameBytes) capacity--;
            int frameCount = (inner.Length + capacity - 1) / capacity;
            if (frameCount > 256) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes), "Message requires more than 256 fragments.");
            for (int index = 0, offset = 0; offset < inner.Length; index++)
            {
                int count = Math.Min(capacity, inner.Length - offset);
                byte control = index == 0 ? (byte)1 : offset + count == inner.Length ? (byte)3 : (byte)2;
                WriteFrame(output, control, (byte)index, inner.AsSpan(offset, count));
                offset += count;
            }
        }
        return output.ToArray();
    }
    private static int FrameSize(int length) => 1 + (length < 128 ? 1 : length < 16384 ? 2 : 3) + length;
    private static void WriteFrame(List<byte> output, byte control, byte fsn, ReadOnlySpan<byte> bytes)
    {
        output.Add(0xAA);
        int length = bytes.Length + (control == 0 ? 2 : 3);
        do { output.Add((byte)((length & 127) | (length > 127 ? 128 : 0))); length >>= 7; } while (length != 0);
        output.Add(control); output.Add(0);
        if (control != 0) output.Add(fsn);
        foreach (byte value in bytes) output.Add(value);
    }
}

/// <summary>Incremental OPOv1 stream decoder. Malformed data throws a static-message FormatException and resets all buffered state. The caller must close the affected session.</summary>
public sealed class PacketDecoder
{
    public const int MaximumFrameBytes = 12388;
    public const int MaximumMessageBytes = ushort.MaxValue + 5;
    private readonly List<byte> _frame = new();
    private readonly List<byte> _message = new();
    private int _expectedFrameSize;
    private int _length;
    private int _lengthBytes;
    private int _nextFsn;
    private bool _fragmenting;
    private readonly Func<byte, bool>? _allowAir4ProV125CodecReply;

    public PacketDecoder() : this(null) { }

    /// <param name="allowAir4ProV125CodecReply">Optional permission for a known single-frame firmware defect.
    /// The caller must verify the current product, running firmware and pending GET 0x0114 sequence.</param>
    public PacketDecoder(Func<byte, bool>? allowAir4ProV125CodecReply)
    {
        _allowAir4ProV125CodecReply = allowAir4ProV125CodecReply;
    }
    public IReadOnlyList<Packet> Feed(ReadOnlySpan<byte> bytes)
    {
        var result = new List<Packet>();
        try
        {
            foreach (byte value in bytes)
            {
                if (_frame.Count == 0)
                {
                    if (value != 0xAA) throw Malformed("frame-start");
                    _frame.Add(value); continue;
                }
                _frame.Add(value);
                if (_expectedFrameSize == 0)
                {
                    if (++_lengthBytes > 2) throw Malformed("frame-length");
                    _length |= (value & 127) << (7 * (_lengthBytes - 1));
                    if ((value & 128) != 0) continue;
                    if (_lengthBytes > 1 && value == 0) throw Malformed("frame-length");
                    _expectedFrameSize = 1 + _lengthBytes + _length;
                    if (_length < 3 || _expectedFrameSize > MaximumFrameBytes) throw Malformed("frame-length");
                }
                if (_frame.Count == _expectedFrameSize)
                {
                    ProcessFrame(result);
                    _frame.Clear(); _expectedFrameSize = _length = _lengthBytes = 0;
                }
                if (_frame.Count > MaximumFrameBytes) throw Malformed("frame-length");
            }
            return result;
        }
        catch (FormatException) { Reset(); throw; }
    }
    public bool HasIncompleteMessage => _frame.Count != 0 || _fragmenting;

    /// <summary>Checks stream EOF. A partial frame or unfinished fragment sequence fails and resets buffered state.</summary>
    public void Complete()
    {
        bool incomplete = HasIncompleteMessage;
        Reset();
        if (incomplete) throw Malformed("incomplete-message");
    }

    public void Reset()
    {
        _frame.Clear(); _message.Clear(); _expectedFrameSize = _length = _lengthBytes = _nextFsn = 0; _fragmenting = false;
    }
    private void ProcessFrame(List<Packet> output)
    {
        int offset = 1 + _lengthBytes;
        // Both official OPOv1 wrappers use only the low two control bits and
        // skip the second L1 header byte. Neither byte authorizes a command.
        byte control = (byte)(_frame[offset++] & 3);
        offset++;
        if (control == 0)
        {
            if (_fragmenting) throw Malformed("fragment-order");
            output.Add(ParseInner(_frame.GetRange(offset, _frame.Count - offset).ToArray(), allowCompatibility: true)); return;
        }
        if (offset >= _frame.Count) throw Malformed("fragment-layout");
        byte fsn = _frame[offset++];
        if (offset == _frame.Count) throw Malformed("fragment-layout");
        if (control == 1)
        {
            if (_fragmenting) throw Malformed("fragment-order");
            if (fsn != 0) throw Malformed("fragment-sequence");
            _fragmenting = true; _nextFsn = 1;
        }
        else
        {
            if (!_fragmenting) throw Malformed("fragment-order");
            if (_nextFsn > 255 || fsn != _nextFsn) throw Malformed("fragment-sequence");
            _nextFsn++;
        }
        if (_message.Count + _frame.Count - offset > MaximumMessageBytes) throw Malformed("inner-length");
        for (; offset < _frame.Count; offset++) _message.Add(_frame[offset]);
        if (_message.Count >= 5)
        {
            int declared = _message[3] | (_message[4] << 8);
            if (_message.Count > declared + 5 || (control != 3 && _message.Count == declared + 5)) throw Malformed("inner-length");
        }
        if (control == 3)
        {
            output.Add(ParseInner(_message.ToArray())); _message.Clear(); _fragmenting = false; _nextFsn = 0;
        }
    }
    private Packet ParseInner(ReadOnlySpan<byte> bytes, bool allowCompatibility = false)
    {
        if (bytes.Length < 5) throw Malformed("inner-length");
        int declared = BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]);
        if (bytes.Length != 5 + declared)
        {
            // Air4 Pro v125 GET 0x0114 sends status + a known LE ushort codec,
            // while declaring status + one codec byte. Preserve all three bytes.
            // Unknown native codecs leave firmware stack bytes uninitialized,
            // so only the three fully initialized replies may use this exception.
            bool knownReply = allowCompatibility && bytes.Length == 8 && declared == 2
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0x8114
                && bytes[5] == 0 && bytes[6] is 1 or 2 or 8 && bytes[7] == 0;
            if (!knownReply || _allowAir4ProV125CodecReply?.Invoke(bytes[2]) != true)
                throw Malformed("inner-length");
        }
        return new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), bytes[2], bytes[5..].ToArray());
    }
    private static FormatException Malformed(string reason)
    {
        var failure = new FormatException("Malformed protocol frame or fragment sequence.");
        failure.Data["HeyMelody.Protocol.FrameFailure"] = reason;
        return failure;
    }
}
