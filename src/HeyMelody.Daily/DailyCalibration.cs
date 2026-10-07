using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Devices;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient : ICalibrationTransport
{
    private readonly HashSet<byte> _registeredEvents = [];
    public bool IsConnected => _session is { Usable: true } && _latest is { Connected: true, ProfileVerified: true };
    public event Action<Packet>? Notification;
    public event Action? Disconnected;
    public CalibrationDeviceContext CalibrationContext(string stableKey, long generation)
    {
        lock (_stateGate)
        {
            bool allowed = _profile?.Conditions.Evaluate(new(_latest?.FirmwareVersion, _appVersionMarker)) == VersionConditionStatus.Pass;
            int Version(string name) => allowed ? _profile?.FunctionVersion(name) ?? 0 : 0;
            return new() { StableKey = stableKey, Generation = generation,
                Commands = (_commands?.SupportedCommands ?? []).ToHashSet(), Events = _registeredEvents.ToHashSet(),
                Fit = Version("fitDetection") > 0,
                HearingV1 = (Version("hearingEnhancement") > 0 || Version("hearingEnhancementNew") > 0) && Version("hearingJudgeDb") == 0,
                HearingV2 = (Version("hearingEnhancement") > 0 || Version("hearingEnhancementNew") > 0) && Version("hearingJudgeDb") > 0,
                EarScan = Version("earScan") > 0 };
        }
    }
    public async Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!IsConnected || _commands?.Supports(command) != true)
                throw new InvalidOperationException("耳机连接或能力已变化。");
            bool cleanup = command switch
            {
                0x0405 => payload is [0],
                0x040D => payload.Length >= 2 && payload[1] == 0,
                0x040E => payload is [2] or [4],
                0x0403 => payload is [0x0B, 0],
                _ => false
            };
            if (command is 0x0405 or 0x040D or 0x040E or 0x0415 or 0x0403 && !cleanup
                && _profile?.Conditions.Evaluate(new(_latest?.FirmwareVersion, _appVersionMarker)) != VersionConditionStatus.Pass)
                throw new InvalidOperationException("当前固件未通过此测试的配置条件，未提交设置。");
            var reply = await _session!.RequestAsync(command, payload, token);
            if (!reply.Matched || reply.Payload is null || !_session.Usable)
                throw new IOException("设备响应未确认，请重新读取状态。");
            return new((ushort)(command | 0x8000), 0, reply.Payload);
        }
        finally { _gate.Release(); }
    }
}
