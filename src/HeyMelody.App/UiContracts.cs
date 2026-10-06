using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HeyMelodyWindows.App;

// BCL-only contracts. Identity values stay in memory and must never enter logs.
public sealed record UiDevice(string Identity, string Name, bool Connected);
public sealed record UiOption(int Key, string Name);
public sealed record UiBand(int FrequencyHz, int Gain);
public sealed record UiEqEntry(int Id, string Name, int MinimumGain, int MaximumGain,
    bool Selected, IReadOnlyList<UiBand> Bands);

public sealed class UiSnapshot
{
    public string Identity { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? Failure { get; set; }
    public bool Connected { get; set; }
    public bool SupportedModel { get; set; }
    public int? LeftBattery { get; set; }
    public int? RightBattery { get; set; }
    public int? CaseBattery { get; set; }
    public bool? LeftCharging { get; set; }
    public bool? RightCharging { get; set; }
    public bool? CaseCharging { get; set; }
    public int? Anc { get; set; }
    public int? Equalizer { get; set; }
    public int? Spatial { get; set; }
    public bool? DualConnection { get; set; }
    public bool? GameMode { get; set; }
    public bool? HiRes { get; set; }
    public bool CanSetAnc { get; set; }
    public bool CanSetEqualizer { get; set; }
    public bool CanSetCustomEqualizer { get; set; }
    public bool CanSetDualConnection { get; set; }
    public bool CanSetGameMode { get; set; }
    public bool CanSetSpatial { get; set; }
    public bool CanSetHiRes { get; set; }
    public bool SpatialHiResMutex { get; set; }
    public IReadOnlyList<UiOption> AncOptions { get; set; } = Array.Empty<UiOption>();
    public IReadOnlyList<UiOption> EqualizerOptions { get; set; } = Array.Empty<UiOption>();
    public IReadOnlyList<UiOption> SpatialOptions { get; set; } = Array.Empty<UiOption>();
    public IReadOnlyList<UiEqEntry> EqualizerEntries { get; set; } = Array.Empty<UiEqEntry>();
    public string? Firmware { get; set; }
    public DateTimeOffset SampleTime { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

// Kind: Anc | Equalizer | CustomEqualizer | CustomSelect | DualConnection | GameMode | Spatial | HiRes.
// Value is an explicit protocol ID or 0/1; custom EQ carries the complete entry.
public sealed record UiAction(string Kind, int Value, UiEqEntry? EqualizerEntry = null,
    bool AllowMutexChange = false);
public sealed class UiApplyResult
{
    public bool Accepted { get; set; }
    public bool? ReadbackMatches { get; set; }
    public bool SubmissionAttempted { get; set; }
    public bool PartialEffects { get; set; }
    public bool AcknowledgementUnknown { get; set; }
    public string Message { get; set; } = "";
    public UiSnapshot? After { get; set; }
}

public interface IHeadsetService : IAsyncDisposable
{
    Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken);
    Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken);
    Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken);
}
