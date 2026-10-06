namespace HeyMelodyWindows.Protocol;

/// <summary>APK capability metadata, including foundational commands; this does not authorize sending commands.</summary>
public sealed class CommandCapabilities
{
    public const int KnownBitCount = 69;

    // APK jadx p133i8/a.java f28901a; corroborated by apktool smali_classes3/i8.1/a.smali.
    // LSB-first, with empty entries deliberately retained so later bits never shift.
    private static readonly ushort[][] BitCommands =
    [
        [0x0105], // bit 0
        [0x0106], // bit 1
        [0x0107], // bit 2
        [0x0108, 0x0401, 0x0416], // bit 3
        [0x0109], // bit 4
        [0x0400], // bit 5
        [0x0402], // bit 6
        [0x0403], // bit 7
        [0x010C, 0x0404], // bit 8
        [0x0405], // bit 9
        [0x0406, 0x010F], // bit 10
        [0x0407], // bit 11
        [], // bit 12
        [0x0408], // bit 13
        [0x0409], // bit 14
        [], // bit 15
        [], // bit 16
        [0x0114], // bit 17
        [], // bit 18
        [0x040E, 0x040D, 0x0115, 0x0116], // bit 19
        [0x040F], // bit 20
        [0x0410, 0x0119], // bit 21
        [0x0205], // bit 22
        [0x0F00], // bit 23
        [], // bit 24
        [0x0118, 0x0411], // bit 25
        [0x011A, 0x0412], // bit 26
        [0x011C, 0x0413], // bit 27
        [], // bit 28
        [0x0112, 0x040B], // bit 29
        [0x011E, 0x011F, 0x0415], // bit 30
        [0x040D], // bit 31
        [], // bit 32
        [0x0121, 0x0417], // bit 33
        [0x0122, 0x0418], // bit 34
        [0x0419], // bit 35
        [0x011D, 0x0414], // bit 36
        [0x0123, 0x041A], // bit 37
        [0x0124, 0x041B], // bit 38
        [0x0125, 0x041C, 0x0127, 0x041D, 0x041F], // bit 39
        [0x0421, 0x0023, 0x0024, 0x0022, 0x0126, 0x0129], // bit 40
        [0xEF01], // bit 41
        [0xEF02], // bit 42
        [0xEF03, 0x041E], // bit 43
        [0x0420], // bit 44
        [0x001C], // bit 45
        [], // bit 46
        [0x0422, 0x012A], // bit 47
        [0xEF04], // bit 48
        [0x0423, 0x012B], // bit 49
        [], // bit 50
        [0x0424], // bit 51
        [0xEF06], // bit 52
        [], // bit 53
        [], // bit 54
        [0x0425, 0x012E, 0x0426], // bit 55
        [0x012F], // bit 56
        [0x0427, 0x0130], // bit 57
        [0x0131, 0x0428], // bit 58
        [0x0429, 0x0132], // bit 59
        [0x0014], // bit 60
        [0x042D, 0x0133], // bit 61
        [0x042E], // bit 62
        [0xEF07], // bit 63
        [0xEF08, 0x0136, 0x0433], // bit 64
        [0xEF09], // bit 65
        [0x0431, 0x0134], // bit 66
        [0x0432, 0x0135, 0x0435], // bit 67
        [0x0117], // bit 68
    ];

    // APK p133i8/a.java f28902b and p280s8/c.java c(): always available before bitmap lookup.
    // f28903c is a separate command category, not a foundation or a bitmap extension.
    private static readonly ushort[] FoundationAlways = [0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x010B, 0x0F00, 0x0F04, 0x010D, 0x0F03, 0x0106];
    private readonly HashSet<ushort> _commands;

    private CommandCapabilities(SortedSet<ushort> commands, List<int> unknownBits, byte[] bitmap)
    {
        _commands = new HashSet<ushort>(commands);
        SupportedCommands = Array.AsReadOnly(commands.ToArray());
        UnknownSetBits = unknownBits.AsReadOnly();
        RawBitmap = Array.AsReadOnly(bitmap);
    }

    public IReadOnlyList<ushort> SupportedCommands { get; }
    public IReadOnlyList<int> UnknownSetBits { get; }
    public IReadOnlyList<byte> RawBitmap { get; }
    public bool Supports(ushort command) => _commands.Contains(command);

    public static CommandCapabilities Parse(ReadOnlySpan<byte> bitmap)
    {
        if (bitmap.Length > int.MaxValue / 8) throw new ArgumentOutOfRangeException(nameof(bitmap));
        var commands = new SortedSet<ushort>(FoundationAlways);
        var unknownBits = new List<int>();
        for (int byteIndex = 0; byteIndex < bitmap.Length; byteIndex++)
            for (int bitOffset = 0; bitOffset < 8; bitOffset++)
            {
                if ((bitmap[byteIndex] & (1 << bitOffset)) == 0) continue;
                int bit = byteIndex * 8 + bitOffset;
                if (bit < KnownBitCount) commands.UnionWith(BitCommands[bit]);
                else unknownBits.Add(bit);
            }
        return new(commands, unknownBits, bitmap.ToArray());
    }
}
