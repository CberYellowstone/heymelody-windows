namespace HeyMelodyWindows.Protocol;

public sealed record BassEngineRange(int Minimum, int Maximum, int Current);
public sealed record GestureAssignment(byte Device, byte Button, byte Action, byte Function);
public sealed record GameSoundState(byte SelectedType, IReadOnlyList<byte> SupportedTypes);

/// <summary>Native daily-control layouts audited against OEM 17.6.3 classes.dex.</summary>
public static class DailyFeatureCodecs
{
    private static void Require(bool valid) { if (!valid) throw new FormatException("Invalid daily feature layout."); }
    private static void Status(ReadOnlySpan<byte> bytes) => Require(bytes.Length > 0 && bytes[0] == 0);
    // commands.d.c @ 0x5780C4, commands.n.M 8124 branch @ 0x57DA6C.
    public static BassEngineRange ParseBass(ReadOnlySpan<byte> bytes)
    {
        Status(bytes); Require(bytes.Length == 4);
        var value = new BassEngineRange(unchecked((sbyte)bytes[1]), unchecked((sbyte)bytes[2]), unchecked((sbyte)bytes[3]));
        Require(value.Minimum <= value.Maximum && value.Current >= value.Minimum && value.Current <= value.Maximum); return value;
    }
    // HeadsetCoreService.F0 @ 0x5760F8 writes current,max,min; GET uses min,max,current.
    public static byte[] Bass(BassEngineRange reported, int value)
    {
        Require(reported.Minimum >= sbyte.MinValue && reported.Maximum <= sbyte.MaxValue
            && reported.Minimum <= reported.Maximum && value >= reported.Minimum && value <= reported.Maximum);
        return [unchecked((byte)(sbyte)value), unchecked((byte)(sbyte)reported.Maximum), unchecked((byte)(sbyte)reported.Minimum)];
    }
    // HeadsetCoreService.l0 @ 0x573650 / n1 @ 0x577798; n.M 8130 @ 0x57D6BE.
    public static byte ParsePromptVolume(ReadOnlySpan<byte> bytes) { Status(bytes); Require(bytes.Length == 2); return bytes[1]; }
    public static byte[] PromptVolume(int value) { Require(value is >= 0 and <= 127); return [(byte)value]; }
    // n.M 812B @ 0x57D784; HeadsetCoreService.Q0 @ 0x5768E4 writes type,enable.
    public static GameSoundState ParseGameSound(ReadOnlySpan<byte> bytes)
    {
        Status(bytes); Require(bytes.Length >= 3 && bytes.Length == 3 + bytes[2]);
        byte[] types = bytes[3..].ToArray(); Require(types.Distinct().Count() == types.Length);
        return new(bytes[1], Array.AsReadOnly(types));
    }
    // The second byte enables this type assignment. It is not the independent
    // feature 0x27 switch, which is written through 0x0403 instead.
    public static byte[] GameSoundType(byte type, bool typeEnabled = true) => [type, typeEnabled ? (byte)1 : (byte)0];
    /// <summary>Compatibility encoder for the type assignment's enable byte, not feature 0x27.</summary>
    public static byte[] GameSound(byte type, bool enabled) => GameSoundType(type, enabled);
    // n.t @ 0x57BDF0: normal (non-TV) query asks components 2,3,1.
    public static byte[] GestureQuery() => [3, 2, 3, 1];
    // KeyFunctionInfo.<init>(I,[B) @ 0x5687F0; n.M 8108 @ 0x57EA16.
    public static IReadOnlyList<GestureAssignment> ParseGestures(ReadOnlySpan<byte> bytes)
    {
        Status(bytes); Require(bytes.Length >= 2 && bytes.Length == 2 + bytes[1] * 4);
        var result = new List<GestureAssignment>(); var keys = new HashSet<(byte, byte, byte)>();
        for (int p = 2; p < bytes.Length; p += 4)
        {
            Require(keys.Add((bytes[p], bytes[p + 1], bytes[p + 2])));
            result.Add(new(bytes[p], bytes[p + 1], bytes[p + 2], bytes[p + 3]));
        }
        return result.AsReadOnly();
    }
    // HeadsetCoreService.X0 @ 0x576EDC; BluetoothService handler defaults command to 0401 @ 0x56E724.
    public static byte[] Gesture(GestureAssignment current, byte function) => [1, current.Device, current.Button, current.Action, function];
}
