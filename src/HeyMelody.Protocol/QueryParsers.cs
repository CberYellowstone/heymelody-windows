using System.Buffers.Binary;
using System.Globalization;
using System.Text;
namespace HeyMelodyWindows.Protocol;

public sealed class QueryReply
{
    public ushort Command { get; set; }
    public byte? Status { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
}
public sealed record FirmwareEntry(int DeviceType, int VersionType, string Version);

/// <summary>Parses the stage-one GET ACK payloads and the color GET. Errors contain no received bytes.</summary>
public static class QueryParsers
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static QueryReply Parse(ushort requestCommand, ReadOnlySpan<byte> payload)
    {
        var reply = new QueryReply { Command = requestCommand };
        if (requestCommand is not (0x0100 or 0x0101 or 0x0102 or 0x0103 or 0x0104 or 0x0105 or 0x010B or 0x0114 or 0x010D)) return Fail(reply, "Unsupported query command.");
        if (payload.IsEmpty) return Fail(reply, "Missing reply status.");
        reply.Status = payload[0];
        if (payload[0] != 0) return Fail(reply, "Query rejected by device.");
        try
        {
            switch (requestCommand)
            {
                case 0x0100:
                    // APK commands/m.G: status then an LSB-first bitmap, mapped by i8.1/a (69 bits).
                    // Only commands permitted in stage one are exposed as supportedCommands.
                    if (payload.Length < 2 || payload.Length > 512) return Invalid(reply);
                    var commands = new SortedSet<ushort> { 0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x010D };
                    if ((payload[1] & 1) != 0) commands.Add(0x0105);
                    if (payload.Length > 3 && (payload[3] & 2) != 0) commands.Add(0x0114);
                    reply.Data["supportedCommands"] = commands.ToArray();
                    reply.Data["supportedCommandsScope"] = "stage-one-query-subset";
                    reply.Data["foundationIncluded"] = true;
                    reply.Data["knowncapbits"] = new int[] { 0, 17 };
                    reply.Data["capabilityBitmapHex"] = Convert.ToHexString(payload[1..]);
                    reply.Data["capabilityExtraBytes"] = Math.Max(0, payload.Length - 10);
                    var capabilities = CommandCapabilities.Parse(payload[1..]);
                    reply.Data["allSupportedCommands"] = capabilities.SupportedCommands.ToArray();
                    reply.Data["allSupportedCommandsScope"] = "apk-69-bit-map-and-foundation";
                    reply.Data["knownCapabilityBitCount"] = CommandCapabilities.KnownBitCount;
                    reply.Data["unknownCapabilitySetBits"] = capabilities.UnknownSetBits.ToArray();
                    break;
                case 0x0101:
                case 0x0102:
                    // commands/m.H and m.J require exactly three bytes.
                    if (payload.Length != 3) return Invalid(reply);
                    int value = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
                    reply.Data[requestCommand == 0x0101 ? "remoteMtu" : "vendorId"] = value;
                    break;
                case 0x0103:
                    if (payload.Length != 4) return Invalid(reply);
                    int product = payload[1] | (payload[2] << 8) | (payload[3] << 16);
                    reply.Data["productId"] = product.ToString("X6", CultureInfo.InvariantCulture);
                    break;
                case 0x010B:
                    // Both APKs' 0x810B branch checks status and length, then reads payload[1] & 0xFF
                    // as the protocol color used by artwork requests. No color-name mapping exists.
                    if (payload.Length < 2 || payload.Length > 512) return Invalid(reply);
                    reply.Data["color"] = payload[1];
                    break;
                case 0x0104:
                    // commands/m.F constructs default-charset text; no binary field layout is specified.
                    // X4 may return three non-text bytes. Preserve that variant without inventing a version.
                    if (payload.Length < 2 || payload.Length > 512) return Invalid(reply);
                    string? protocol = null;
                    try
                    {
                        string text = StrictUtf8.GetString(payload[1..]).TrimEnd('\0', ' ');
                        if (text.Length != 0 && !text.Any(char.IsControl)) protocol = text;
                    }
                    catch (DecoderFallbackException) { }
                    if (protocol is null && payload.Length != 4) return Invalid(reply);
                    reply.Data["protocolVersion"] = protocol;
                    reply.Data["protocolVersionRawHex"] = Convert.ToHexString(payload[1..]);
                    reply.Data["protocolVersionInterpretationVerified"] = protocol is not null;
                    break;
                case 0x0105:
                    // commands/m.K -> commands/e.m(1): count then UTF-8 comma-separated triples.
                    if (payload.Length < 3 || payload[1] == 0) return Invalid(reply);
                    string[] fields = DecodeText(payload[2..]).Split(',');
                    if (fields.Length != payload[1] * 3) return Invalid(reply);
                    var entries = new List<FirmwareEntry>();
                    for (int i = 0; i < fields.Length; i += 3)
                    {
                        if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out int deviceType)
                            || !int.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int versionType)
                            || fields[i + 2].Length == 0) return Invalid(reply);
                        entries.Add(new(deviceType, versionType, fields[i + 2]));
                    }
                    reply.Data["firmwareEntries"] = entries;
                    // Preserve versions individually; do not join unrelated hardware/software entries into an invented version.
                    reply.Data["firmwareVersion"] = entries.Count == 1 ? entries[0].Version : null;
                    break;
                case 0x0114:
                    // commands/m codec-type branch reads byte 1; codec-list is a separate command.
                    if (payload.Length < 2 || payload.Length > 512) return Invalid(reply);
                    reply.Data["extensionBytesLength"] = payload.Length - 2;
                    reply.Data["codecRaw"] = payload[1];
                    reply.Data["codecName"] = null;
                    break;
                case 0x010D:
                    // commands/m -> commands/e.i(1): count followed by two-byte FeatureSwitchInfo items.
                    if (payload.Length < 2 || payload.Length > 512 || payload[1] == 0 || payload.Length < 2 + payload[1] * 2) return Invalid(reply);
                    int featureEnd = 2 + payload[1] * 2;
                    reply.Data["extensionBytesLength"] = payload.Length - featureEnd;
                    var features = new Dictionary<string, byte>(StringComparer.Ordinal);
                    for (int i = 2; i < featureEnd; i += 2)
                        if (!features.TryAdd(payload[i].ToString("X2", CultureInfo.InvariantCulture), payload[i + 1])) return Invalid(reply);
                    reply.Data["featureValues"] = features;
                    // Stage-one query requests feature 0x18. Preserve unknown future states as null.
                    reply.Data["hiRes"] = features.TryGetValue("18", out byte hiResStatus)
                        ? hiResStatus switch { 0 => (bool?)false, 1 => true, _ => null }
                        : null;
                    break;
            }
            reply.Success = true;
            return reply;
        }
        catch (DecoderFallbackException) { return Invalid(reply); }
        catch (FormatException) { return Invalid(reply); }
    }
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        // A terminating NUL is allowed, embedded controls are rejected; strict UTF-8 never substitutes bad bytes.
        string text = StrictUtf8.GetString(bytes).TrimEnd('\0', ' ').Trim();
        if (text.Any(char.IsControl)) throw new FormatException();
        return text;
    }
    private static QueryReply Invalid(QueryReply reply) => Fail(reply, "Invalid query reply layout.");
    private static QueryReply Fail(QueryReply reply, string error)
    {
        reply.Success = false; reply.Error = error; reply.Data.Clear(); return reply;
    }
}
