using System.Text.Json;

namespace HeyMelodyWindows.Probe;

public sealed class ProbeResult
{
    public int SchemaVersion { get; set; } = 2;
    public string Operation { get; set; } = "x4-readonly-session";
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Phase { get; set; } = "discovery";
    public string? Error { get; set; }
    public int? NativeError { get; set; }
    public TargetInfo Target { get; set; } = new();
    public List<QueryResult> Queries { get; set; } = [];
    public ReadOnlyEvidence ReadOnly { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public JsonElement? DeviceModel { get; set; }
}

public sealed class TargetInfo
{
    public string Name { get; set; } = "OPPO Enco X4";
    public string ExpectedProductId { get; set; } = "06EC10";
    public string? ProductId { get; set; }
    public bool IdentityVerified { get; set; }
    public bool ConnectedToWindowsBefore { get; set; }
    public string ServiceUuid { get; set; } = "0000079a-d102-11e1-9b23-00025b00a5a5";
    public List<uint> DiscoveredChannels { get; set; } = [];
    public uint? SelectedChannel { get; set; }
}

public sealed class QueryResult
{
    public string RequestCommand { get; set; } = "";
    public string ResponseCommand { get; set; } = "";
    public byte? Sequence { get; set; }
    public bool Submitted { get; set; }
    public bool ResponseMatched { get; set; }
    public bool Success { get; set; }
    public string Disposition { get; set; } = "pending";
    public byte? Status { get; set; }
    public int? ResponsePayloadLength { get; set; }
    public string? ProtocolVersionPayloadHex { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object?> Data { get; set; } = [];
}

public sealed class ReadOnlyEvidence
{
    public string Policy { get; set; } = "fixed-get-allowlist-and-feature-18-only";
    public string[] AllowedCommands { get; set; } = ["0x0100", "0x0101", "0x0102", "0x0103", "0x0104", "0x0105", "0x010D", "0x0114"];
    public int SettingCommandsSent { get; set; }
    public List<SentQuery> SentQueries { get; set; } = [];
    public int IgnoredPackets { get; set; }
    public bool AddressIncluded { get; set; }
    public bool AudioNegotiationVerified { get; set; }
    public bool BitrateMeasured { get; set; }
    public bool LatencyMeasured { get; set; }
}

public sealed record SentQuery(string Command, byte Sequence, string PayloadHex, string FrameHex);

public interface IQueryTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task<int> ReadAsync(Memory<byte> bytes, CancellationToken cancellationToken);
}

