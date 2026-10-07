using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Calibration;

public interface ICalibrationTransport
{
    bool IsConnected { get; }
    event Action<Packet>? Notification;
    event Action? Disconnected;
    Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token);
}
public enum CalibrationWorkflow { None, Fit, HearingV1, HearingV2, EarScan }
public enum CalibrationStage { Idle, CheckingWear, Starting, Measuring, AwaitingAnswer, Paused, Saving, Completed, Failed, Cancelling, Cancelled }
public enum HearingVersion { V1 = 1, V2 = 2 }
public sealed record CalibrationDeviceContext
{
    public required string StableKey { get; init; }
    public long Generation { get; init; }
    public required IReadOnlySet<ushort> Commands { get; init; }
    public required IReadOnlySet<byte> Events { get; init; }
    public bool Fit { get; init; }
    public bool HearingV1 { get; init; }
    public bool HearingV2 { get; init; }
    public bool EarScan { get; init; }
}
public sealed record CalibrationSnapshot
{
    public long Revision { get; init; }
    public long Generation { get; init; }
    public CalibrationWorkflow Workflow { get; init; }
    public CalibrationStage Stage { get; init; }
    public int Uid { get; init; }
    public byte Side { get; init; } = 1;
    public byte FrequencyType { get; init; } = 1;
    public int StepIndex { get; init; }
    public int StepCount { get; init; } = 12;
    public int LevelIndex { get; init; } = 5;
    public int Decibels { get; init; }
    public int LowerBound { get; init; } = -60;
    public int UpperBound { get; init; } = 20;
    public byte? RawStatus { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<byte, byte> FitResults { get; init; } = new Dictionary<byte, byte>();
    public IReadOnlyList<HearingLevel> HearingLevels { get; init; } = [];
    public EarScanResult? ScanResult { get; init; }
    public IReadOnlyList<HearingFilterCurve> Curves { get; init; } = [];
    public string? SavedName { get; init; }
}

/// <summary>Exact bracket and step rules from OEM h0.onClick at 0x3380E4.</summary>
public sealed class HearingV1Search
{
    public int Lower { get; private set; } = -60;
    public int Upper { get; private set; } = 20;
    public int Current { get; private set; }
    private int _step = int.MinValue;
    public bool Answer(bool heard)
    {
        if (heard)
        {
            if (Current == -60 || Current == Lower || Current - Lower <= (Current < 0 ? 4 : 2)) { Current = Lower; return true; }
            _step = Half(_step == int.MinValue ? Current - Lower : _step);
            Upper = Current; Current -= _step;
        }
        else
        {
            if (Current == 20 || Current == Upper || Upper - Current <= (Current < 0 ? 4 : 2)) { Current = Upper; return true; }
            _step = Half(_step == int.MinValue ? Upper - Current : _step);
            Lower = Current; Current += _step;
        }
        return false;
    }
    private static int Half(int value) => value >= 0 ? (int)Math.Ceiling(value / 2d) : (int)Math.Floor(value / 2d);
}
