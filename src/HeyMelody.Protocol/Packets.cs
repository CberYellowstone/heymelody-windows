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
    public IReadOnlyList<Packet> Feed(ReadOnlySpan<byte> bytes)
    {
        var result = new List<Packet>();
        try
        {
            foreach (byte value in bytes)
            {
                if (_frame.Count == 0)
                {
                    if (value != 0xAA) throw Malformed();
                    _frame.Add(value); continue;
                }
                _frame.Add(value);
                if (_expectedFrameSize == 0)
                {
                    if (++_lengthBytes > 2) throw Malformed();
                    _length |= (value & 127) << (7 * (_lengthBytes - 1));
                    if ((value & 128) != 0) continue;
                    if (_lengthBytes > 1 && value == 0) throw Malformed();
                    _expectedFrameSize = 1 + _lengthBytes + _length;
                    if (_length < 3 || _expectedFrameSize > MaximumFrameBytes) throw Malformed();
                }
                if (_frame.Count == _expectedFrameSize)
                {
                    ProcessFrame(result);
                    _frame.Clear(); _expectedFrameSize = _length = _lengthBytes = 0;
                }
                if (_frame.Count > MaximumFrameBytes) throw Malformed();
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
        if (incomplete) throw Malformed();
    }

    public void Reset()
    {
        _frame.Clear(); _message.Clear(); _expectedFrameSize = _length = _lengthBytes = _nextFsn = 0; _fragmenting = false;
    }
    private void ProcessFrame(List<Packet> output)
    {
        int offset = 1 + _lengthBytes;
        byte control = _frame[offset++];
        if (control > 3 || _frame[offset++] != 0) throw Malformed();
        if (control == 0)
        {
            if (_fragmenting) throw Malformed();
            output.Add(ParseInner(_frame.GetRange(offset, _frame.Count - offset).ToArray())); return;
        }
        if (offset >= _frame.Count) throw Malformed();
        byte fsn = _frame[offset++];
        if (offset == _frame.Count) throw Malformed();
        if (control == 1)
        {
            if (_fragmenting || fsn != 0) throw Malformed();
            _fragmenting = true; _nextFsn = 1;
        }
        else
        {
            if (!_fragmenting || _nextFsn > 255 || fsn != _nextFsn) throw Malformed();
            _nextFsn++;
        }
        if (_message.Count + _frame.Count - offset > MaximumMessageBytes) throw Malformed();
        for (; offset < _frame.Count; offset++) _message.Add(_frame[offset]);
        if (_message.Count >= 5)
        {
            int declared = _message[3] | (_message[4] << 8);
            if (_message.Count > declared + 5 || (control != 3 && _message.Count == declared + 5)) throw Malformed();
        }
        if (control == 3)
        {
            output.Add(ParseInner(_message.ToArray())); _message.Clear(); _fragmenting = false; _nextFsn = 0;
        }
    }
    private static Packet ParseInner(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 || bytes.Length != 5 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..])) throw Malformed();
        return new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), bytes[2], bytes[5..].ToArray());
    }
    private static FormatException Malformed() => new("Malformed protocol frame or fragment sequence.");
}
