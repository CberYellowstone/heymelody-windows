namespace HeyMelodyWindows.Protocol;

public enum DeviceNotificationKind { Battery, Placement, Anc, GameMode, DualConnection, Capabilities, Features, Preset, CustomEqualizers, SpatialInvalidated }
public sealed record DeviceNotification(DeviceNotificationKind Kind, byte[] Data);

/// <summary>APK notification envelopes. Parsing never generates an ACK or a device write.</summary>
public static class NotificationParsers
{
    public static bool IsNotificationCommand(ushort command) => command is 0x0204 or 0x8202 or 0x0503 or 0x0504 or 0x0506 or 0x051D;
    public static byte[] ParseSupportedEvents(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0 || payload.Length != payload[1] + 2)
            throw new FormatException("Invalid supported-event response.");
        byte[] ids = payload[2..].ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new FormatException("Duplicate supported event.");
        return ids;
    }
    public static bool TryParse(Packet packet, out DeviceNotification? notification)
    {
        notification = null;
        ReadOnlySpan<byte> data = packet.Payload;
        DeviceNotificationKind? kind;
        if (packet.Command is 0x0204 or 0x8202)
        {
            int offset = packet.Command == 0x0204 ? 1 : 2;
            if (data.Length < offset || packet.Command == 0x8202 && data[0] != 0)
                throw new FormatException("Invalid device-event envelope.");
            kind = data[offset - 1] switch
            {
                1 => DeviceNotificationKind.Battery, 2 => DeviceNotificationKind.Placement,
                3 => DeviceNotificationKind.Anc, 5 => DeviceNotificationKind.GameMode,
                6 => DeviceNotificationKind.DualConnection, 15 => DeviceNotificationKind.Capabilities, _ => null
            };
            data = data[offset..];
        }
        else kind = packet.Command switch
        {
            0x0503 => DeviceNotificationKind.Features, 0x0504 => DeviceNotificationKind.Preset,
            0x0506 => DeviceNotificationKind.CustomEqualizers,
            // Phone spatial negotiation is not an authoritative 012A state report.
            0x051D => DeviceNotificationKind.SpatialInvalidated, _ => null
        };
        if (kind is null) return false;
        notification = new(kind.Value, data.ToArray()); return true;
    }
    public static Dictionary<byte, byte> ParseComponentPairs(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length != 1 + payload[0] * 2)
            throw new FormatException("Invalid notification component pairs.");
        var result = new Dictionary<byte, byte>();
        for (int p = 1; p < payload.Length; p += 2)
            if (!result.TryAdd(payload[p], payload[p + 1])) throw new FormatException("Duplicate notification component.");
        return result;
    }
}
