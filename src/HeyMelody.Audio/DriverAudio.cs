using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using LhdcManager.Core;

namespace HeyMelodyWindows.Audio;

public static class DriverAudio
{
    public static DriverAudioReport ReadForPeer(ulong bluetoothAddress) => new DriverAudioService(new NativeDriverCore()).ReadForPeer(bluetoothAddress);
    public static DriverRateResult RequestRate(ulong bluetoothAddress, uint sampleRateHz) => new DriverAudioService(new NativeDriverCore()).RequestRate(bluetoothAddress, sampleRateHz);
}

// Per-operation seam; tests never replace the process-wide native adapter.
internal interface IDriverCore
{
    IReadOnlyList<AudioDevice> Enumerate();
    DriverSnapshot Read(string instanceId);
    ApplyResult Apply(string instanceId, SettingKind kind, uint value, bool persist);
}

internal sealed class NativeDriverCore : IDriverCore
{
    public IReadOnlyList<AudioDevice> Enumerate() => WindowsDevices.Enumerate();
    public DriverSnapshot Read(string instanceId) => LhdcDriver.Read(instanceId);
    public ApplyResult Apply(string instanceId, SettingKind kind, uint value, bool persist) => LhdcDriver.Apply(instanceId, kind, value, persist);
}

internal static partial class PeerIdentity
{
    // Actual SetupAPI shape: ...\7&...&0&001122334455_C00000000.
    // Parse only the last node's final address token; the service GUID is never an address.
    [GeneratedRegex(@"(?:^|&)([0-9A-Fa-f]{12})(?:_C[0-9A-Fa-f]{8})?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressTail();

    internal static bool TryParseSinkAddress(string? id, out ulong address)
    {
        address = 0;
        if (!WindowsDevices.IsSinkInstanceId(id)) return false;
        var segments = id!.Split('\\');
        if (segments.Length != 3 || segments[2].Length == 0) return false;
        var match = AddressTail().Match(segments[2]);
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out address) && address is > 0 and <= 0xFFFFFFFFFFFF;
    }

    internal static string Resolve(IReadOnlyList<AudioDevice> devices, ulong address)
    {
        if (address is 0 or > 0xFFFFFFFFFFFF) throw new ArgumentException("Invalid Bluetooth address.");
        var matches = devices.Where(d => d.Present && TryParseSinkAddress(d.InstanceId, out var candidate) && candidate == address).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Peer identity is not unique.");
        return matches[0].InstanceId;
    }
}

internal sealed class DriverAudioService(IDriverCore core)
{
    internal string Resolve(ulong address) => PeerIdentity.Resolve(core.Enumerate(), address);

    internal DriverAudioReport ReadForPeer(ulong address)
    {
        try { return ReadResolved(Resolve(address)); }
        catch (Exception e) { return Failure(e); }
    }

    internal DriverAudioReport ReadResolved(string id)
    {
        try { return Report(core.Read(id), id); }
        catch (Exception e) { return Failure(e) with { PeerMatched = true }; }
    }

    internal DriverRateResult RequestRate(ulong address, uint rate)
    {
        var result = new DriverRateResult { RequestedSampleRateHz = rate };
        if (rate is not (96000 or 192000)) return result with { Message = "Only 96000 or 192000 Hz may be requested; no setting sent." };
        DriverAudioReport? before = null;
        bool applying = false;
        try
        {
            string id = Resolve(address);
            var snapshot = core.Read(id);
            before = Report(snapshot, id);
            result = result with { Before = before };
            if (!before.ReadSucceeded) return result with { Win32Error = before.Win32Error, Message = before.Failure ?? "Fresh read failed; no setting sent." };
            if (DriverProtocol.Reject(snapshot, SettingKind.SampleRate, rate, false) is not null)
                return result with { Message = "Fresh driver capabilities or stream state reject this rate; no setting sent." };
            result = result with { FreshCapabilitiesAllowRequest = true };
            applying = true;
            // Exactly one Apply invocation. Core rechecks capabilities and identity before its single Send.
            var applied = core.Apply(id, SettingKind.SampleRate, rate, persist: false);
            return result with
            {
                SubmissionAttempted = applied.SubmissionAttempted,
                Accepted = applied.Accepted, TargetMatches = applied.TargetMatches,
                PersistenceRequested = applied.PersistenceRequested, NeedStreamRestart = applied.NeedStreamRestart,
                Win32Error = applied.Win32Error,
                After = applied.After is null ? null : Report(applied.After, id),
                Message = applied.SubmissionAttempted
                    ? "Single request submitted. Readback is the driver target; wire audio remains unverified. Never retry automatically."
                    : "No transport submission reported; inspect the native error or fresh capabilities."
            };
        }
        catch (Exception e)
        {
            // An unexpected adapter exception after entering Apply has uncertain side effects.
            // Conservatively classify it as attempted, even if the adapter failed before Send.
            return result with { Before = before, SubmissionAttempted = applying, Win32Error = ErrorCode(e),
                Message = applying ? "Apply completion is unknown; no retry. Wire audio remains unverified." : Failure(e).Failure! };
        }
    }

    internal static DriverAudioReport Report(DriverSnapshot snapshot, string expectedId)
    {
        if (!string.Equals(snapshot.InstanceId, expectedId, StringComparison.OrdinalIgnoreCase))
            return new() { PeerMatched = true, Failure = "Driver snapshot identity changed; no setting sent." };
        return new()
        {
            PeerMatched = true, ReadSucceeded = snapshot.Failure is null,
            Failure = snapshot.Failure is null ? null : "Driver read failed; identifiers withheld.",
            Win32Error = ParseReadError(snapshot.Failure),
            InterfaceVersion = snapshot.InterfaceVersion, Mode = (uint)snapshot.Mode, Codec = snapshot.Codec,
            StreamState = snapshot.State, SampleRates = [.. snapshot.SampleRates], BitDepths = [.. snapshot.BitDepths],
            Qualities = [.. snapshot.Qualities], ReportedTargetSampleRateHz = snapshot.SampleRateHz,
            ReportedTargetBitDepth = snapshot.BitDepth, ReportedTargetQualityIndex = snapshot.Quality,
            WorkingQualityIndex = snapshot.WorkingQuality, LowLatency = snapshot.LowLatency,
            SupportsLowLatency = snapshot.SupportsLowLatency
        };
    }

    private static int? ParseReadError(string? failure)
    {
        if (failure is null || !failure.StartsWith("Win32 ", StringComparison.Ordinal)) return null;
        int colon = failure.IndexOf(':', 6);
        return colon > 6 && int.TryParse(failure.AsSpan(6, colon - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static int? ErrorCode(Exception e) => e is Win32Exception w ? w.NativeErrorCode : e is UnauthorizedAccessException ? 5 : null;
    private static DriverAudioReport Failure(Exception e) => new()
    {
        Win32Error = ErrorCode(e), Failure = e switch
        {
            InvalidOperationException => "Stable peer could not be uniquely matched; no setting sent.",
            ArgumentException => "Invalid Bluetooth address; no setting sent.",
            PlatformNotSupportedException => "Windows is required.",
            _ => "Driver operation failed; identifiers withheld."
        }
    };
}
