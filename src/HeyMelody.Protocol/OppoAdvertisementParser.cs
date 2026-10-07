using System.Buffers.Binary;

namespace HeyMelodyWindows.Protocol;

public enum AdvertisedPlacement { Unknown, InCase, InEar, OutOfCase }

/// <summary>A validated OPPO manufacturer record. Its battery encoding differs from SPP.</summary>
public sealed record OppoAdvertisement
{
    public required string ProductId { get; init; }
    public ushort Flags { get; init; }
    public ushort FastPairId { get; init; }
    public ulong? EmbeddedAddress { get; init; }
    public byte? RawStatus { get; init; }
    public bool? CaseOpen { get; init; }
    public AdvertisedPlacement LeftPlacement { get; init; }
    public AdvertisedPlacement RightPlacement { get; init; }
    public byte? RawLeftBattery { get; init; }
    public byte? RawRightBattery { get; init; }
    public byte? RawCaseBattery { get; init; }
    public int? LeftBattery { get; init; }
    public int? RightBattery { get; init; }
    public int? CaseBattery { get; init; }

    /// <summary>Resolve only documented addresses; the caller must match an approved Classic peer.</summary>
    public bool TryResolveAddress(ulong sender, bool publicSender, out ulong address)
    {
        if (FastPairId == 0x9803 && EmbeddedAddress is > 0)
        {
            address = EmbeddedAddress.Value;
            return true;
        }
        address = publicSender && sender is > 0 and <= 0xFFFFFFFFFFFF ? sender : 0;
        return address != 0;
    }
}

public static class OppoAdvertisementParser
{
    /// <summary>Data excludes the two-byte Bluetooth manufacturer company identifier.</summary>
    public static bool TryParse(ushort companyId, ReadOnlySpan<byte> data, out OppoAdvertisement? advertisement)
    {
        advertisement = null;
        if (companyId is not (0x079A or 0x072F) || data.Length < 7) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]);
        ushort fastPair = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
        if ((flags & 1) == 0 || (fastPair & 0xFF00) != 0x9800) return false;

        int cursor = 7;
        ulong? address = null;
        byte? status = null, left = null, right = null, batteryCase = null;
        if ((flags & 0x0002) != 0 && !Skip(data, ref cursor, 1)) return false;
        if ((flags & 0x0004) != 0)
        {
            if (data.Length - cursor < 6) return false;
            ulong value = 0;
            for (int i = 0; i < 6; i++) value |= (ulong)data[cursor + i] << (8 * i);
            address = value == 0 ? null : value;
            cursor += 6;
        }
        if ((flags & 0x0008) != 0)
        {
            if (data.Length - cursor < 1) return false;
            status = data[cursor++];
        }
        if ((flags & 0x0010) != 0)
        {
            if (data.Length - cursor < 3) return false;
            left = data[cursor++]; right = data[cursor++]; batteryCase = data[cursor++];
        }
        if ((flags & 0x0020) != 0 && !Skip(data, ref cursor, 1)) return false;
        if ((flags & 0x0040) != 0)
        {
            if (data.Length - cursor < 1) return false;
            int length = data[cursor++];
            if (!Skip(data, ref cursor, length)) return false;
        }
        if ((flags & 0x0080) != 0 && !Skip(data, ref cursor, 2)) return false;
        // 0x0100 declares LE Audio capability, without a payload byte.
        if ((flags & 0x0200) != 0 && !Skip(data, ref cursor, 4)) return false;

        advertisement = new OppoAdvertisement
        {
            ProductId = ((uint)data[0] | (uint)data[1] << 8 | (uint)data[2] << 16).ToString("X6"),
            Flags = flags, FastPairId = fastPair, EmbeddedAddress = address, RawStatus = status,
            CaseOpen = status is { } bits ? (bits & 1) != 0 : null,
            LeftPlacement = Placement(status, 1, 2), RightPlacement = Placement(status, 3, 4),
            RawLeftBattery = left, RawRightBattery = right, RawCaseBattery = batteryCase,
            LeftBattery = DecodeBattery(left), RightBattery = DecodeBattery(right), CaseBattery = DecodeBattery(batteryCase)
        };
        return true;
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int cursor, int count)
    {
        if (data.Length - cursor < count) return false;
        cursor += count;
        return true;
    }

    private static AdvertisedPlacement Placement(byte? status, int outOfCaseBit, int wornBit) => status switch
    {
        null => AdvertisedPlacement.Unknown,
        { } value when (value & (1 << wornBit)) != 0 => AdvertisedPlacement.InEar,
        { } value when (value & (1 << outOfCaseBit)) != 0 => AdvertisedPlacement.OutOfCase,
        _ => AdvertisedPlacement.InCase
    };

    private static int? DecodeBattery(byte? raw)
    {
        if (raw is not { } value || (value & 1) == 0) return null;
        int decoded = (value >> 1) & 0x7F;
        return decoded == 1 ? 1 : Math.Min((decoded / 10 + 1) * 10, 100);
    }
}
