using System.Buffers.Binary;
using System.Text;

namespace HeyMelodyWindows.Protocol;

public sealed record HearingLevel(byte Side, byte Type, sbyte Decibels);
public sealed record HearingParameters(byte Action, IReadOnlyList<HearingLevel> Levels, int Uid, string Name);
public sealed record EarScanResult(byte Action, byte[] Data, int Uid);
public sealed record HearingRestoreItem(byte Type, IReadOnlyList<byte> Value);
public sealed record HearingState(byte DetectionType, byte Status, IReadOnlyList<byte> Details);
public sealed record HearingFilterPacket(int Uid, byte DeviceType, ushort TotalSamples, byte PacketId,
    byte EnhanceType, IReadOnlyList<float> Values);
public sealed record EarScanFilterPacket(int Uid, byte DeviceType, ushort SamplingFrequency,
    ushort TotalSamples, byte PacketId, IReadOnlyList<float> Values);

/// <summary>A complete pair of filters. 0116 carries no sampling frequency or transport cache key.</summary>
public sealed record HearingFilterCurve(int Uid, byte DeviceType, byte? EnhanceType,
    IReadOnlyList<float> Left, IReadOnlyList<float> Right, ushort TotalSamples,
    ushort? SamplingFrequency, ushort SourceCommand)
{
    public IReadOnlyList<float> Values => Array.AsReadOnly(Left.Concat(Right).ToArray());
}

/// <summary>APK and firmware calibration payloads. UIDs are signed Java ints in big endian order.</summary>
public static class CalibrationCodecs
{
    public const ushort FitCommand = 0x0405, HearingDetectionCommand = 0x040D,
        HearingParametersCommand = 0x040E, RestoreCommand = 0x0411, EarScanSaveCommand = 0x0415,
        CurrentHearingCommand = 0x0115, FilterCommand = 0x0116, CurrentRestoreCommand = 0x0118,
        CurrentEarScanCommand = 0x011E, EarScanFilterCommand = 0x011F;
    public const byte FitEvent = 4, HearingStateEvent = 8, EarScanEvent = 14;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Fit(bool start) => [start ? (byte)1 : (byte)0];
    public static byte[] HearingDetection(bool start) => [2, start ? (byte)1 : (byte)0];
    public static byte[] EarScanDetection(bool start, int uid)
    {
        byte[] bytes = [4, start ? (byte)1 : (byte)0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), uid);
        return bytes;
    }
    public static byte[] Frequency(HearingLevel level)
    {
        ValidateLevels([level]);
        return [3, 1, level.Side, level.Type, unchecked((byte)level.Decibels)];
    }
    public static byte[] PauseHearing() => [4];
    public static byte[] StopHearingPreview() => [2];
    public static byte[] Parameters(byte action, IReadOnlyList<HearingLevel> levels, int uid, string name = "")
    {
        if (action is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(action));
        // These actions carry no count, UID or name, including for legacy callers.
        if (action is 2 or 4) return [action];
        ValidateLevels(levels);
        if (action == 3)
        {
            ValidateCompleteLevels(levels);
            ValidateName(name);
        }
        else
        {
            if (levels.Count != 0) ValidateCompleteLevels(levels);
            ValidateName(name, allowEmpty: true);
        }
        byte[] text = Utf8.GetBytes(name);
        byte[] bytes = new byte[6 + levels.Count * 3 + text.Length];
        bytes[0] = action;
        bytes[1] = checked((byte)levels.Count);
        WriteLevels(bytes.AsSpan(2), levels);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2 + levels.Count * 3), uid);
        text.CopyTo(bytes, 6 + levels.Count * 3);
        return bytes;
    }
    public static byte[] FilterRequest(IReadOnlyList<HearingLevel> levels, int uid)
    {
        ValidateLevels(levels);
        byte[] bytes = new byte[5 + levels.Count * 3];
        bytes[0] = checked((byte)levels.Count);
        WriteLevels(bytes.AsSpan(1), levels);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1 + levels.Count * 3), uid);
        return bytes;
    }
    public static byte[] EarScanFilterRequest(ReadOnlySpan<byte> scan, int uid)
    {
        ValidateScanLength(scan, allowEmpty: false);
        byte[] bytes = new byte[6 + scan.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)scan.Length);
        scan.CopyTo(bytes.AsSpan(2));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2 + scan.Length), uid);
        return bytes;
    }
    public static byte[] SaveEarScan(ReadOnlySpan<byte> scan, int uid) => EarScanParameters(3, scan, uid);
    public static byte[] EarScanParameters(byte action, ReadOnlySpan<byte> scan, int uid)
    {
        if (action is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(action));
        if (action == 2) return [2];
        ValidateScanLength(scan, allowEmpty: action == 1);
        byte[] bytes = new byte[7 + scan.Length];
        bytes[0] = action;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)scan.Length);
        scan.CopyTo(bytes.AsSpan(3));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(3 + scan.Length), uid);
        return bytes;
    }
    public static byte[] RestoreDescription(byte descriptionId) => [1, 1, 1, 0, descriptionId];
    public static byte? ParseRestoreDescription(ReadOnlySpan<byte> reply)
    {
        HearingRestoreItem? description = ParseRestoreItems(reply).SingleOrDefault(x => x.Type == 1);
        return description is null ? null : description.Value[0];
    }
    public static IReadOnlyList<HearingRestoreItem> ParseRestoreItems(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply);
        if (reply.Length < 2) throw Malformed();
        var items = new HearingRestoreItem[reply[1]];
        int offset = 2;
        bool haveDescription = false;
        for (int i = 0; i < items.Length; i++)
        {
            if (reply.Length - offset < 3) throw Malformed();
            byte type = reply[offset];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(reply[(offset + 1)..]);
            offset += 3;
            if (reply.Length - offset < length) throw Malformed();
            if (type == 1)
            {
                if (length != 1 || haveDescription) throw Malformed();
                haveDescription = true;
            }
            items[i] = new(type, Array.AsReadOnly(reply.Slice(offset, length).ToArray()));
            offset += length;
        }
        if (offset != reply.Length) throw Malformed();
        return Array.AsReadOnly(items);
    }
    public static HearingParameters ParseCurrentHearing(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply);
        if (reply.Length < 7 || reply[1] != 3 || reply[2] != 12) throw Malformed();
        const int end = 39;
        if (reply.Length < end + 5 || reply.Length > end + 4 + 39) throw Malformed();
        var levels = new HearingLevel[12];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = new(reply[3 + i * 3], reply[4 + i * 3], unchecked((sbyte)reply[5 + i * 3]));
        try { ValidateCompleteLevels(levels); }
        catch (ArgumentException) { throw Malformed(); }
        int uid = BinaryPrimitives.ReadInt32BigEndian(reply[end..]);
        if (uid == 0) throw Malformed();
        string name;
        try { name = Utf8.GetString(reply[(end + 4)..]); }
        catch (DecoderFallbackException) { throw Malformed(); }
        if (name.Contains('\0')) throw Malformed();
        return new(3, Array.AsReadOnly(levels), uid, name);
    }
    public static EarScanResult ParseCurrentEarScan(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply);
        return ParseEarScan(reply[1..]);
    }
    public static EarScanResult ParseEarScan(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) throw Malformed();
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]);
        if (length > 32767 || data.Length != length + 7) throw Malformed();
        return new(data[0], data.Slice(3, length).ToArray(), BinaryPrimitives.ReadInt32BigEndian(data[(3 + length)..]));
    }
    public static HearingState ParseHearingState(ReadOnlySpan<byte> eventData)
    {
        if (eventData.Length < 2) throw Malformed();
        return new(eventData[0], eventData[1], Array.AsReadOnly(eventData[2..].ToArray()));
    }
    public static IReadOnlyDictionary<byte, byte> ParseFit(ReadOnlySpan<byte> data)
    {
        if (data.Length != 4) throw Malformed();
        var result = new Dictionary<byte, byte>();
        for (int p = 0; p < 4; p += 2)
        {
            if (data[p] is not (1 or 2) || data[p + 1] is not (0 or 1 or 6)
                || !result.TryAdd(data[p], data[p + 1])) throw Malformed();
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<byte, byte>(result);
    }
    public static bool TryEvent(Packet packet, byte eventId, out byte[] data)
    {
        data = [];
        int offset = packet.Command == 0x0204 ? 0 : packet.Command == 0x8202 ? 1 : -1;
        if (offset < 0 || packet.Payload.Length <= offset || offset == 1 && packet.Payload[0] != 0
            || packet.Payload[offset] != eventId) return false;
        data = packet.Payload[(offset + 1)..];
        return true;
    }
    public static HearingFilterPacket ParseFilter(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply);
        if (reply.Length < 10) throw Malformed();
        byte deviceType = reply[5];
        ushort totalSamples = BinaryPrimitives.ReadUInt16LittleEndian(reply[6..]);
        IReadOnlyList<float> values = ReadFilterValues(reply[10..]);
        ValidateFilterData(deviceType, totalSamples, values);
        return new(BinaryPrimitives.ReadInt32BigEndian(reply[1..]), deviceType,
            totalSamples, reply[8], reply[9], values);
    }
    public static EarScanFilterPacket ParseEarScanFilter(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply);
        if (reply.Length < 11) throw Malformed();
        byte deviceType = reply[5];
        ushort samplingFrequency = BinaryPrimitives.ReadUInt16LittleEndian(reply[6..]);
        ushort totalSamples = BinaryPrimitives.ReadUInt16LittleEndian(reply[8..]);
        IReadOnlyList<float> values = ReadFilterValues(reply[11..]);
        if (samplingFrequency == 0) throw Malformed();
        ValidateFilterData(deviceType, totalSamples, values);
        return new(BinaryPrimitives.ReadInt32BigEndian(reply[1..]), deviceType,
            samplingFrequency, totalSamples, reply[10], values);
    }
    public static void ValidateLevels(IReadOnlyList<HearingLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count > 12 || levels.Any(x => x is null || x.Side is not (1 or 2) || x.Type is < 1 or > 6)
            || levels.Select(x => (x.Side, x.Type)).Distinct().Count() != levels.Count)
            throw new ArgumentException("Invalid hearing levels.", nameof(levels));
    }
    public static void ValidateCompleteLevels(IReadOnlyList<HearingLevel> levels)
    {
        ValidateLevels(levels);
        if (levels.Count != 12) throw new ArgumentException("Both ears require all six hearing levels.", nameof(levels));
    }
    public static void ValidateName(string name, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        int length = Utf8.GetByteCount(name);
        if (length > 39 || !allowEmpty && length == 0 || name.Contains('\0'))
            throw new ArgumentException("Hearing name must contain 1 to 39 UTF-8 bytes without NUL.", nameof(name));
    }
    private static void ValidateScanLength(ReadOnlySpan<byte> scan, bool allowEmpty)
    {
        if (scan.Length > 32767 || !allowEmpty && scan.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(scan));
    }
    private static IReadOnlyList<float> ReadFilterValues(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length % 4 != 0) throw Malformed();
        var values = new float[data.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
            if (!float.IsFinite(values[i])) throw Malformed();
        }
        return Array.AsReadOnly(values);
    }
    internal static int SamplesPerEar(ushort totalSamples)
    {
        if (totalSamples == 0 || totalSamples % 2 != 0) throw Malformed();
        return totalSamples == 384 ? 384 : totalSamples / 2;
    }
    internal static void ValidateFilterData(byte deviceType, ushort totalSamples, IReadOnlyList<float> values)
    {
        int perEar = SamplesPerEar(totalSamples);
        // SDK device4 slices at total/2 and total. Short dual-ear packets have no proven layout.
        // For total384 each such packet contributes 192 per ear; two packets complete both ears.
        if (deviceType is not (1 or 2 or 4) || values.Count == 0
            || deviceType == 4 && values.Count != totalSamples
            || deviceType != 4 && values.Count > perEar || values.Any(x => !float.IsFinite(x))) throw Malformed();
    }
    private static void WriteLevels(Span<byte> target, IReadOnlyList<HearingLevel> levels)
    {
        for (int i = 0; i < levels.Count; i++)
        {
            target[3 * i] = levels[i].Side;
            target[3 * i + 1] = levels[i].Type;
            target[3 * i + 2] = unchecked((byte)levels[i].Decibels);
        }
    }
    public static void RequireSuccess(ReadOnlySpan<byte> reply)
    {
        if (reply.Length == 0) throw Malformed();
        if (reply[0] != 0) throw new CalibrationDeviceException(reply[0]);
    }
    private static FormatException Malformed() => new("Invalid calibration response.");
}

public sealed class CalibrationDeviceException(byte status) : IOException($"Earphones rejected calibration (0x{status:X2}).")
{
    public byte Status { get; } = status;
}

/// <summary>0116 has per-ear sample counts and packet IDs, independently of Bluetooth framing.</summary>
public sealed class HearingFilterAssembler(int uid)
{
    private readonly FilterSampleAssembler _samples = new(uid, CalibrationCodecs.FilterCommand);
    public HearingFilterCurve? Add(HearingFilterPacket packet) => _samples.Add(packet.Uid, packet.DeviceType,
        packet.TotalSamples, packet.PacketId, packet.EnhanceType, null, packet.Values);
}

public sealed class EarScanFilterAssembler(int uid)
{
    private readonly FilterSampleAssembler _samples = new(uid, CalibrationCodecs.EarScanFilterCommand);
    public HearingFilterCurve? Add(EarScanFilterPacket packet) => _samples.Add(packet.Uid, packet.DeviceType,
        packet.TotalSamples, packet.PacketId, null, packet.SamplingFrequency, packet.Values);
}

internal sealed class FilterSampleAssembler(int uid, ushort sourceCommand)
{
    // Android sorts List<Byte>, retaining Java's signed byte ordering above wire ID 127.
    private static readonly IComparer<byte> PacketOrder = Comparer<byte>.Create(
        (left, right) => unchecked((sbyte)left).CompareTo(unchecked((sbyte)right)));
    private readonly object _sync = new();
    private readonly SortedDictionary<byte, float[]> _left = new(PacketOrder), _right = new(PacketOrder);
    private ushort? _totalSamples, _samplingFrequency;
    private byte? _enhanceType;
    private int _leftCount, _rightCount;

    public HearingFilterCurve? Add(int packetUid, byte deviceType, ushort totalSamples, byte packetId,
        byte? enhanceType, ushort? samplingFrequency, IReadOnlyList<float> values)
    {
        if (packetUid != uid) return null;
        CalibrationCodecs.ValidateFilterData(deviceType, totalSamples, values);
        if (samplingFrequency == 0) throw new FormatException("Invalid filter sampling frequency.");
        // Copy before retaining any packet, so caller-owned arrays cannot change duplicates or curves.
        float[]? left = deviceType == 2 ? null : values.Take(deviceType == 4 ? values.Count / 2 : values.Count).ToArray();
        float[]? right = deviceType == 1 ? null : values.Skip(deviceType == 4 ? values.Count / 2 : 0).ToArray();
        lock (_sync)
        {
            if (_totalSamples.HasValue && (_totalSamples != totalSamples || _enhanceType != enhanceType
                || _samplingFrequency != samplingFrequency)) throw new FormatException("Inconsistent filter packet metadata.");
            int perEar = CalibrationCodecs.SamplesPerEar(totalSamples);
            bool addLeft = CheckPart(_left, _leftCount, left, packetId, perEar);
            bool addRight = CheckPart(_right, _rightCount, right, packetId, perEar);
            // Validate both ears before mutating either one; bad dual-ear packets cannot leave partial state.
            if (!addLeft && !addRight) return null;
            _totalSamples = totalSamples;
            _enhanceType = enhanceType;
            _samplingFrequency = samplingFrequency;
            if (addLeft) { _left.Add(packetId, left!); _leftCount += left!.Length; }
            if (addRight) { _right.Add(packetId, right!); _rightCount += right!.Length; }
            if (_leftCount != perEar || _rightCount != perEar) return null;
            return new(uid, 4, enhanceType,
                Array.AsReadOnly(_left.Values.SelectMany(x => x).ToArray()),
                Array.AsReadOnly(_right.Values.SelectMany(x => x).ToArray()),
                totalSamples, samplingFrequency, sourceCommand);
        }
    }
    private static bool CheckPart(SortedDictionary<byte, float[]> parts, int sampleCount,
        float[]? values, byte packetId, int expectedCount)
    {
        if (values is null) return false;
        if (parts.TryGetValue(packetId, out float[]? old))
        {
            if (old.Length != values.Length || !old.Select(BitConverter.SingleToInt32Bits)
                .SequenceEqual(values.Select(BitConverter.SingleToInt32Bits)))
                throw new FormatException("Conflicting filter packet contents.");
            return false;
        }
        if (sampleCount + values.Length > expectedCount) throw new FormatException("Excess filter samples for an ear.");
        return true;
    }
}
