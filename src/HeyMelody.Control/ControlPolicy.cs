namespace HeyMelodyWindows.Control;

/// <summary>Enable-only policy. Capabilities never widen this authorization.</summary>
public static class ControlPolicy
{
    public static bool IsAllowed(ushort command, ReadOnlySpan<byte> payload) => command switch
    {
        0x0103 or 0x0100 or 0x0105 or 0x0114 or 0x012A => payload.IsEmpty,
        0x010D => payload.SequenceEqual(new byte[] { 1, 0x18 }) || payload.SequenceEqual(new byte[] { 1, 0x1B }),
        0x0403 => payload.SequenceEqual(new byte[] { 0x18, 1 }) || payload.SequenceEqual(new byte[] { 0x1B, 0 }),
        0x0422 => payload.SequenceEqual(new byte[] { 0 }),
        _ => false
    };
}
