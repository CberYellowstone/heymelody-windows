namespace HeyMelodyWindows.Control;

public interface IControlTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token);
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token);
}

public interface IControlConnector
{
    Task<IControlTransport> OpenAsync(CancellationToken token);
}

public sealed class Snapshot
{
    public DateTimeOffset ObservedAtUtc { get; set; }
    public int ControlConnectionNumber { get; set; }
    public string? ProductId { get; set; }
    public string? ProfileSha256 { get; set; }
    public bool ProfileVerified { get; set; }
    public int KnownCapabilityBitCount { get; set; }
    public string? CapabilityBitmapHex { get; set; }
    public List<string> SupportedCommands { get; set; } = [];
    public List<int> UnknownCapabilitySetBits { get; set; } = [];
    public bool? SpatialHiResMutex { get; set; }
    public bool? HiRes { get; set; }
    public byte? HiResRaw { get; set; }
    public int? SpaceMode { get; set; }
    public string? SpaceRoute { get; set; }
    public byte? CodecRaw { get; set; }
    public List<HeyMelodyWindows.Protocol.FirmwareEntry> FirmwareEntries { get; set; } = [];
}

public sealed class ControlAction
{
    public int ControlConnectionNumber { get; set; }
    public string CommandHex { get; set; } = "";
    public byte Sequence { get; set; }
    public string PayloadHex { get; set; } = "";
    public bool Submitted { get; set; }
    public bool ResponseMatched { get; set; }
    public byte? Status { get; set; }
    public bool Success { get; set; }
    public string Disposition { get; set; } = "not-submitted";
    public string? Error { get; set; }
}

public sealed class HiResWorkflowResult
{
    public bool Success { get; set; }
    public string Phase { get; set; } = "initializing";
    public Snapshot? Before { get; set; }
    public Snapshot? After { get; set; }
    public List<ControlAction> Actions { get; set; } = [];
    public int SettingsAttempted { get; set; }
    public bool HiResSetSubmitted { get; set; }
    public bool? HiResAckAccepted { get; set; }
    public bool FinalHiResVerified { get; set; }
    public bool FreshSessionVerified { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>Host observation delays; these are not estimates of earbud RTOS ticks.</summary>
public sealed class HiResWorkflowOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan VerificationBudget { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan InitialObservationDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan StableObservationDelay { get; set; } = TimeSpan.FromSeconds(1);
}
