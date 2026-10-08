using System.Buffers.Binary;
using System.Text;
using HeyMelodyWindows.Devices;

namespace HeyMelodyWindows.Daily;

/// <summary>Strict GET schemas; no payload-only desktop fallback is used.</summary>
public static class DailyParsers
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static void Require(bool valid) { if (!valid) throw new FormatException("Invalid daily query layout."); }
    private static void Status(ReadOnlySpan<byte> data) => Require(data.Length >= 1 && data[0] == 0);
    public static BatterySnapshot ParseBattery(ReadOnlySpan<byte> data)
    {
        Status(data);
        Require(data.Length >= 2 && data[1] > 0 && data.Length == 2 + data[1] * 2);
        var result = new BatterySnapshot();
        var seen = new HashSet<byte>();
        for (int p = 2; p < data.Length; p += 2)
        {
            byte component = data[p], raw = data[p + 1];
            Require(seen.Add(component));
            result.RawComponents.Add(component, raw);
            int level = raw & 0x7F;
            var entry = new BatteryLevel(raw, level is >= 1 and <= 100 ? level : null,
                level is >= 1 and <= 100 ? (raw & 0x80) != 0 : null);
            switch (component) { case 1: result.Left = entry; break; case 2: result.Right = entry; break; case 3: result.Case = entry; break; }
        }
        return result;
    }
    public static AncSnapshot ParseAnc(ReadOnlySpan<byte> data, DeviceProfile profile)
    {
        Status(data);
        Require(data.Length >= 4 && data.Length <= 35);
        var state = new AncSnapshot { Kind = data[1], ModeType = data[2], RawMask = data[3..].ToArray() };
        if (state.Kind is not (1 or 4) || state.ModeType is not (1 or 5)) return state;
        var bits = new List<byte>();
        for (int n = 0; n < state.RawMask.Length * 8; n++)
            if ((state.RawMask[n / 8] & (1 << (n % 8))) != 0) bits.Add((byte)n);
        // Multiple bits cannot be silently collapsed into a guessed selected mode.
        if (bits.Count != 1) return state;
        byte selected = bits[0];
        // Kind=4 is an intelligent ANC live level only for ANC children. Other masks remain raw.
        if (state.Kind == 4 && selected is not (4 or 5 or 6 or 7)) return state;
        foreach (var primary in profile.NoiseReductionModes)
        {
            var leaf = primary.Children.FirstOrDefault(x => x.ProtocolIndex == selected);
            if (leaf is null && primary.ProtocolIndex != selected) continue;
            state.Primary = (byte)primary.ProtocolIndex;
            // A parent bitmap confirms the mode, but does not report one of its child adjustments.
            state.Level = leaf is null && primary.Children.Count > 0 ? null : selected;
            // Smart response kind=4 describes its instantaneous child, not a new manual selection.
            if (state.Kind == 4) { state.Primary = 1; state.Level = 7; }
            return state;
        }
        return state;
    }
    public static byte ParsePreset(ReadOnlySpan<byte> data) { Status(data); Require(data.Length == 2); return data[1]; }
    public static byte? ParseSpatial(ReadOnlySpan<byte> data)
    { Status(data); Require(data.Length == 2); return data[1] <= 2 ? data[1] : null; }
    public static Dictionary<byte, byte> ParseFeatures(ReadOnlySpan<byte> data)
    {
        Status(data); Require(data.Length >= 2 && data.Length == 2 + data[1] * 2);
        var result = new Dictionary<byte, byte>();
        for (int p = 2; p < data.Length; p += 2) Require(result.TryAdd(data[p], data[p + 1]));
        return result;
    }
    public static List<CustomEqualizer> ParseEqualizers(ReadOnlySpan<byte> data)
    {
        Status(data); Require(data.Length >= 2);
        var result = new List<CustomEqualizer>();
        var ids = new HashSet<byte>(); int p = 2, selectedCount = 0;
        try
        {
            for (int n = 0; n < data[1]; n++)
            {
                Require(data.Length - p >= 6 && data[p] <= 1);
                bool selected = data[p++] == 1;
                var eq = new CustomEqualizer { Selected = selected, MinimumGain = unchecked((sbyte)data[p++]),
                    MaximumGain = unchecked((sbyte)data[p++]), Id = data[p++] };
                Require(eq.Id != 0 && ids.Add(eq.Id) && eq.MinimumGain <= eq.MaximumGain);
                int names = data[p++]; Require(data.Length - p >= names + 1);
                eq.Name = Utf8.GetString(data.Slice(p, names)).TrimEnd('\0'); p += names;
                Require(!eq.Name.Any(char.IsControl));
                int bands = data[p++]; Require(bands > 0 && data.Length - p >= bands * 3);
                var frequencies = new HashSet<ushort>();
                for (int b = 0; b < bands; b++)
                {
                    ushort frequency = BinaryPrimitives.ReadUInt16LittleEndian(data[p..]); p += 2;
                    sbyte gain = unchecked((sbyte)data[p++]);
                    Require(frequency > 0 && frequencies.Add(frequency) && gain >= eq.MinimumGain && gain <= eq.MaximumGain);
                    eq.Bands.Add(new(frequency, gain));
                }
                if (selected) selectedCount++;
                result.Add(eq);
            }
            Require(p == data.Length && selectedCount <= 1);
            return result;
        }
        catch (DecoderFallbackException) { throw new FormatException("Invalid equalizer text."); }
    }
    public static byte[] BuildAnc(byte level)
    {
        var data = new byte[3 + level / 8]; data[0] = 1; data[1] = 1;
        data[2 + level / 8] = (byte)(1 << (level % 8)); return data;
    }
    public static byte[] BuildEqualizer(EqualizerEdit edit, IReadOnlyList<ushort> frequencies, bool preserveReportedName = false)
    {
        ArgumentNullException.ThrowIfNull(edit);
        string nameText = edit.Name ?? throw new FormatException("Invalid equalizer name.");
        Require(!nameText.Any(char.IsControl));
        if (!preserveReportedName)
            Require(!string.IsNullOrWhiteSpace(nameText) && nameText.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or >= '\u4e00' and <= '\u9fa5'));
        byte[] name = Utf8.GetBytes(nameText);
        Require(name.Length <= 255 && frequencies.Count is > 0 and <= 32 && edit.Bands.Count == frequencies.Count
            && edit.MinimumGain <= edit.MaximumGain && frequencies.All(x => x > 0) && frequencies.Distinct().Count() == frequencies.Count);
        for (int n = 0; n < frequencies.Count; n++)
            Require(edit.Bands[n].Frequency == frequencies[n] && edit.Bands[n].Gain >= edit.MinimumGain
                && edit.Bands[n].Gain <= edit.MaximumGain);
        byte[] data = new byte[6 + name.Length + edit.Bands.Count * 3];
        data[0] = edit.Id == 0 ? (byte)1 : (byte)2;
        data[1] = unchecked((byte)edit.MinimumGain); data[2] = unchecked((byte)edit.MaximumGain);
        data[3] = edit.Id; data[4] = (byte)name.Length; name.CopyTo(data, 5);
        int p = 5 + name.Length; data[p++] = (byte)edit.Bands.Count;
        foreach (var band in edit.Bands)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(p), band.Frequency); p += 2;
            data[p++] = unchecked((byte)band.Gain);
        }
        return data;
    }
}
