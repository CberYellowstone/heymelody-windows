using System.Text.Json;

namespace HeyMelodyWindows.App;

// Public messages contain opaque device identities, never Bluetooth addresses.
public sealed class BackendRequest
{
    public int SchemaVersion { get; set; } = 2;
    public long Id { get; set; }
    public string Operation { get; set; } = "";
    public string? BackendInstanceId { get; set; }
    public string? Identity { get; set; }
    public long? DeviceGeneration { get; set; }
    public string[]? DeviceIdentities { get; set; }
    public RefreshScope? RefreshScope { get; set; }
    public JsonElement Action { get; set; } = JsonSerializer.SerializeToElement<object?>(null);
    public string? Role { get; set; }
    public string? ExitRequestId { get; set; }
    public bool? Approved { get; set; }
}

public sealed class BackendMessage
{
    public int SchemaVersion { get; set; } = 2;
    public string Kind { get; set; } = "response";
    public long Id { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
    public BackendEvent? Event { get; set; }
}

public sealed class BackendEvent
{
    // catalog | snapshot | caseOpen | health | activate | requestExit | exitCancelled | stopping | unavailable
    public string Kind { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public UiSnapshot? Snapshot { get; set; }
    public UiDeviceCatalog? Catalog { get; set; }
    public IReadOnlyList<UiDevice>? Devices { get; set; }
    public string? ExitRequestId { get; set; }
    public string? Message { get; set; }
    public bool FirstObservation { get; set; }
}

public sealed class BackendSubscription
{
    public string BackendInstanceId { get; set; } = "";
    public string Version { get; set; } = "0.7.1";
    public UiDeviceCatalog Catalog { get; set; } = new([]);
    public IReadOnlyList<UiDevice> Devices { get; set; } = [];
    public IReadOnlyList<UiSnapshot> Snapshots { get; set; } = [];
}

/// <summary>Shared backend access; selection does not own a Bluetooth connection.</summary>
public interface IHeadsetEventService : IHeadsetService
{
    event Action<BackendEvent>? EventReceived;
    Task<BackendSubscription> SubscribeAsync(string role, CancellationToken cancellationToken);
    Task<UiSnapshot> SelectAsync(string identity, CancellationToken cancellationToken);
    Task<UiSnapshot> PauseAsync(string identity, CancellationToken cancellationToken);
    Task<UiSnapshot> ResumeAsync(string identity, CancellationToken cancellationToken);
    Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken cancellationToken);
    Task<UiApplyResult> ApplyAsync(string identity, long deviceGeneration, UiAction action, CancellationToken cancellationToken);
    Task AcknowledgeExitAsync(string exitRequestId, bool approved, CancellationToken cancellationToken);
    Task ActivateMainUiAsync(CancellationToken cancellationToken);
}
