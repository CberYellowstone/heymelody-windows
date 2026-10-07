namespace HeyMelodyWindows.App;

public sealed record UiHearingProfile(int? Uid, string Name, bool Current, bool CanApply);
public sealed record UiFitResult(int Component, int Status);
public sealed record UiHearingPoint(int Side, int FrequencyType, int Decibels);
public sealed record UiCalibrationState
{
    public string Handle { get; set; } = "";
    public string Identity { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public long Generation { get; set; }
    public long Revision { get; set; }
    public string Workflow { get; set; } = "";
    public string Stage { get; set; } = "Ready";
    public string HearingVersion { get; set; } = "V1";
    public bool SupportsEarScan { get; set; }
    public int? Uid { get; set; }
    public int Side { get; set; } = 1;
    public int FrequencyType { get; set; } = 1;
    public int StepIndex { get; set; }
    public int StepCount { get; set; } = 12;
    public int LevelIndex { get; set; } = 5;
    public int Decibels { get; set; } = -19;
    public byte? RawStatus { get; set; }
    public string Message { get; set; } = "";
    public IReadOnlyList<UiFitResult> FitResults { get; set; } = [];
    public IReadOnlyList<UiHearingPoint> HearingLevels { get; set; } = [];
}
public sealed record UiCalibrationInput(string Action, bool? Heard = null, int? LevelIndex = null,
    bool Released = false, string? Name = null, bool AllowReplacement = false);
public interface ICalibrationService
{
    Task<IReadOnlyList<UiHearingProfile>> GetHearingProfilesAsync(string identity, long generation, CancellationToken token);
    Task<UiApplyResult> SelectHearingProfileAsync(string identity, long generation, int? uid, CancellationToken token);
    Task<UiCalibrationState> CreateCalibrationAsync(string identity, long generation, string kind, CancellationToken token);
    Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token);
    Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token);
    Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token);
}
