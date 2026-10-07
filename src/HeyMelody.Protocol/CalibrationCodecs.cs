using System.Buffers.Binary;
using System.Text;

namespace HeyMelodyWindows.Protocol;

public sealed record HearingLevel(byte Side, byte Type, sbyte Decibels);
public sealed record HearingParameters(byte Action, IReadOnlyList<HearingLevel> Levels, int Uid, string Name);
public sealed record EarScanResult(byte Action, byte[] Data, int Uid);
public sealed record HearingFilterPacket(int Uid, byte TotalPackets, ushort Key, byte Index,
    byte EnhanceType, IReadOnlyList<float> Values);
public sealed record HearingFilterCurve(int Uid, ushort Key, byte EnhanceType, IReadOnlyList<float> Values);

/// <summary>APK-backed calibration payloads. UIDs are signed Java ints in big endian order.</summary>
public static class CalibrationCodecs
{
    public const ushort FitCommand = 0x0405, HearingDetectionCommand = 0x040D,
        HearingParametersCommand = 0x040E, EarScanSaveCommand = 0x0415,
        CurrentHearingCommand = 0x0115, FilterCommand = 0x0116, CurrentEarScanCommand = 0x011E;
    public const byte FitEvent = 4, EarScanEvent = 14;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Fit(bool start) => [start ? (byte)1 : (byte)0];
    public static byte[] HearingDetection(bool start) => [2, start ? (byte)1 : (byte)0];
    public static byte[] EarScanDetection(bool start, int uid)
    {
        byte[] bytes = [4, start ? (byte)1 : (byte)0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), uid); return bytes;
    }
    public static byte[] Frequency(HearingLevel level)
    {
        ValidateLevels([level]); return [3, 1, level.Side, level.Type, unchecked((byte)level.Decibels)];
    }
    public static byte[] Parameters(byte action, IReadOnlyList<HearingLevel> levels, int uid, string name = "")
    {
        if (action is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(action));
        ValidateLevels(levels);
        byte[] text = Utf8.GetBytes(name);
        if (text.Length > 160 || name.Contains('\0')) throw new ArgumentException("Invalid hearing name.", nameof(name));
        byte[] bytes = new byte[6 + levels.Count * 3 + text.Length];
        bytes[0] = action; bytes[1] = checked((byte)levels.Count);
        WriteLevels(bytes.AsSpan(2), levels);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2 + levels.Count * 3), uid);
        text.CopyTo(bytes, 6 + levels.Count * 3); return bytes;
    }
    public static byte[] FilterRequest(IReadOnlyList<HearingLevel> levels, int uid)
    {
        ValidateLevels(levels);
        byte[] bytes = new byte[5 + levels.Count * 3]; bytes[0] = checked((byte)levels.Count);
        WriteLevels(bytes.AsSpan(1), levels);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1 + levels.Count * 3), uid); return bytes;
    }
    public static byte[] SaveEarScan(ReadOnlySpan<byte> scan, int uid)
    {
        if (scan.Length is 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(scan));
        byte[] bytes = new byte[7 + scan.Length]; bytes[0] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)scan.Length);
        scan.CopyTo(bytes.AsSpan(3)); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(3 + scan.Length), uid);
        return bytes;
    }
    public static HearingParameters ParseCurrentHearing(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply); if (reply.Length < 7) throw Malformed();
        byte action = reply[1], count = reply[2]; int end = 3 + count * 3;
        if (count > 12 || reply.Length < end + 4) throw Malformed();
        var levels = new HearingLevel[count];
        for (int i = 0; i < count; i++) levels[i] = new(reply[3 + i * 3], reply[4 + i * 3], unchecked((sbyte)reply[5 + i * 3]));
        ValidateParsedLevels(levels);
        int uid = BinaryPrimitives.ReadInt32BigEndian(reply[end..]);
        string name;
        try { name = Utf8.GetString(reply[(end + 4)..]); } catch (DecoderFallbackException) { throw Malformed(); }
        if (name.Contains('\0') || name.Length > 40) throw Malformed();
        return new(action, Array.AsReadOnly(levels), uid, name);
    }
    public static EarScanResult ParseCurrentEarScan(ReadOnlySpan<byte> reply)
    { RequireSuccess(reply); return ParseEarScan(reply[1..]); }
    public static EarScanResult ParseEarScan(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) throw Malformed();
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]);
        if (length > 32767 || data.Length != length + 7) throw Malformed();
        return new(data[0], data.Slice(3, length).ToArray(), BinaryPrimitives.ReadInt32BigEndian(data[(3 + length)..]));
    }
    public static IReadOnlyDictionary<byte, byte> ParseFit(ReadOnlySpan<byte> data)
    {
        if (data.Length != 4) throw Malformed();
        var result = new Dictionary<byte, byte>();
        for (int p = 0; p < 4; p += 2)
        {
            if (data[p] is not (1 or 2) || data[p + 1] is not (0 or 1 or 6) || !result.TryAdd(data[p], data[p + 1])) throw Malformed();
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<byte, byte>(result);
    }
    public static bool TryEvent(Packet packet, byte eventId, out byte[] data)
    {
        data = []; int offset = packet.Command == 0x0204 ? 0 : packet.Command == 0x8202 ? 1 : -1;
        if (offset < 0 || packet.Payload.Length <= offset || offset == 1 && packet.Payload[0] != 0 || packet.Payload[offset] != eventId) return false;
        data = packet.Payload[(offset + 1)..]; return true;
    }
    public static HearingFilterPacket ParseFilter(ReadOnlySpan<byte> reply)
    {
        RequireSuccess(reply);
        if (reply.Length < 10 || (reply.Length - 10) % 4 != 0 || reply[5] == 0) throw Malformed();
        var values = new float[(reply.Length - 10) / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(reply[(10 + i * 4)..]);
            if (!float.IsFinite(values[i])) throw Malformed();
        }
        return new(BinaryPrimitives.ReadInt32BigEndian(reply[1..]), reply[5],
            BinaryPrimitives.ReadUInt16LittleEndian(reply[6..]), reply[8], reply[9], Array.AsReadOnly(values));
    }
    public static void ValidateLevels(IReadOnlyList<HearingLevel> levels)
    {
        if (levels.Count > 12 || levels.Any(x => x.Side is not (1 or 2) || x.Type is < 1 or > 6 || x.Decibels is < -60 or > 20)
            || levels.Select(x => (x.Side, x.Type)).Distinct().Count() != levels.Count)
            throw new ArgumentException("Invalid hearing levels.", nameof(levels));
    }
    private static void ValidateParsedLevels(IReadOnlyList<HearingLevel> levels)
    { try { ValidateLevels(levels); } catch (ArgumentException) { throw Malformed(); } }
    private static void WriteLevels(Span<byte> target, IReadOnlyList<HearingLevel> levels)
    {
        for (int i = 0; i < levels.Count; i++) { target[3 * i] = levels[i].Side; target[3 * i + 1] = levels[i].Type; target[3 * i + 2] = unchecked((byte)levels[i].Decibels); }
    }
    public static void RequireSuccess(ReadOnlySpan<byte> reply)
    { if (reply.Length == 0) throw Malformed(); if (reply[0] != 0) throw new CalibrationDeviceException(reply[0]); }
    private static FormatException Malformed() => new("Invalid calibration response.");
}

public sealed class CalibrationDeviceException(byte status) : IOException($"Earphones rejected calibration (0x{status:X2}).")
{ public byte Status { get; } = status; }

/// <summary>0116 uses application-level packets in addition to the Bluetooth framing layer.</summary>
public sealed class HearingFilterAssembler(int uid)
{
    private readonly Dictionary<ushort, SortedDictionary<byte, HearingFilterPacket>> _parts = [];
    public HearingFilterCurve? Add(HearingFilterPacket packet)
    {
        if (packet.Uid != uid) return null;
        lock (_parts)
        {
            if (!_parts.TryGetValue(packet.Key, out var parts)) _parts.Add(packet.Key, parts = []);
            if (parts.Count != 0 && (parts.Values.First().TotalPackets != packet.TotalPackets || parts.Values.First().EnhanceType != packet.EnhanceType)) throw new FormatException("Inconsistent hearing filter packets.");
            if (parts.TryGetValue(packet.Index, out var old))
            { if (!old.Values.SequenceEqual(packet.Values)) throw new FormatException("Conflicting hearing filter packet."); return null; }
            parts.Add(packet.Index, packet);
            if (parts.Count > packet.TotalPackets) throw new FormatException("Excess hearing filter packets.");
            if (parts.Count != packet.TotalPackets) return null;
            byte first = parts.Keys.First();
            if (first > 1 || !parts.Keys.Select(x => (int)x).SequenceEqual(Enumerable.Range(first, packet.TotalPackets))) throw new FormatException("Incomplete hearing filter sequence.");
            return new(uid, packet.Key, packet.EnhanceType, Array.AsReadOnly(parts.Values.SelectMany(x => x.Values).ToArray()));
        }
    }
}
