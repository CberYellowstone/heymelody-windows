using System.Text.Json;
using System.Runtime.InteropServices;
using HeyMelodyWindows.Audio;
using LhdcManager.Core;

const ulong Peer = 0x112233445566;
const string Id = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&000102B0_PID&0000\7&F26284D&0&112233445566_C00000001";
int passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    passed++;
}
AudioDevice Device(string id = Id, bool present = true) => new() { InstanceId = id, Name = "same display name", Present = present };
DriverSnapshot Snapshot() => new()
{
    InstanceId = Id, InterfaceVersion = 1, Mode = DriverMode.Lhdc, Codec = 2, State = 1,
    SampleRates = [48000, 96000, 192000], BitDepths = [16, 24], Qualities = [1, 13],
    SampleRateHz = 48000, BitDepth = 24, Quality = 13, WorkingQuality = 8
};
FakeCore Fake() => new() { Devices = [Device()], Snapshot = Snapshot() };

Check(PeerIdentity.TryParseSinkAddress(Id, out var address) && address == Peer, "actual SetupAPI C00000001 suffix");
Check(PeerIdentity.TryParseSinkAddress(Id.ToLowerInvariant(), out address) && address == Peer, "case insensitive service and address");
Check(PeerIdentity.TryParseSinkAddress(Id.Replace("_C00000001", "_C00000000"), out address) && address == Peer, "C00000000 suffix");
Check(PeerIdentity.TryParseSinkAddress(Id.Replace("_C00000001", ""), out address) && address == Peer, "bare final address token");
foreach (var malformed in new[] { Id + "_junk", Id.Replace("112233445566", "1122334455667"),
    Id.Replace("112233445566", "000000000000"), Id.Replace("0000110b", "0000111e"),
    @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_TEST\offline", Id + @"\112233445566" })
    Check(!PeerIdentity.TryParseSinkAddress(malformed, out _), "malformed or wrong service rejected");
Check(PeerIdentity.Resolve([Device(Id, false), Device()], Peer) == Id, "phantom node excluded");
var differentPeer = Device(Id.Replace("112233445566", "AABBCCDDEEFF"));
Check(PeerIdentity.Resolve([differentPeer, Device()], Peer) == Id, "same names never select peer");

foreach (var devices in new IReadOnlyList<AudioDevice>[] { [], [differentPeer], [Device(), Device()] })
{
    var fake = Fake(); fake.Devices = devices;
    var result = new DriverAudioService(fake).RequestRate(Peer, 96000);
    Check(!result.SubmissionAttempted && fake.Reads == 0 && fake.Applies == 0, "nonunique or absent peer fails closed");
}
foreach (uint rate in new uint[] { 0, 48000, 96001, uint.MaxValue })
{
    var fake = Fake();
    var result = new DriverAudioService(fake).RequestRate(Peer, rate);
    Check(!result.SubmissionAttempted && fake.Enumerations == 0 && fake.Reads == 0 && fake.Applies == 0, "unsupported rates fail before native access");
}
foreach (ulong invalid in new ulong[] { 0, 0x1000000000000 })
{
    var fake = Fake();
    var report = new DriverAudioService(fake).ReadForPeer(invalid);
    Check(!report.ReadSucceeded && fake.Reads == 0, "invalid peer does not reach driver read");
}
foreach (Action<DriverSnapshot> invalidate in new Action<DriverSnapshot>[] {
    s => s.SampleRates = [48000], s => s.InterfaceVersion = 2, s => s.State = 0,
    s => s.Mode = DriverMode.Windows, s => s.Codec = 1, s => s.Failure = "Win32 5: denied",
    s => s.InstanceId = differentPeer.InstanceId })
{
    var fake = Fake(); invalidate(fake.Snapshot);
    var result = new DriverAudioService(fake).RequestRate(Peer, 192000);
    Check(!result.FreshCapabilitiesAllowRequest && fake.Reads == 1 && fake.Applies == 0, "fresh read capability/state/identity gate");
}
{
    var fake = Fake(); var service = new DriverAudioService(fake);
    Check(service.ReadForPeer(Peer).ReadSucceeded, "read initial caps");
    fake.Snapshot.SampleRates = [48000];
    Check(!service.RequestRate(Peer, 96000).FreshCapabilitiesAllowRequest && fake.Reads == 2 && fake.Applies == 0, "request never uses prior report caps");
}
foreach (uint rate in new uint[] { 96000, 192000 })
{
    var fake = Fake(); fake.Result = new() { SubmissionAttempted = true, Accepted = true, TargetMatches = true, NeedStreamRestart = true, After = Snapshot() };
    fake.Result.After.SampleRateHz = rate;
    var result = new DriverAudioService(fake).RequestRate(Peer, rate);
    Check(result.Accepted && result.TargetMatches && result.NeedStreamRestart && fake.Applies == 1, "one valid rate request");
    Check(fake.LastKind == SettingKind.SampleRate && fake.LastValue == rate && !fake.LastPersist && fake.LastId == Id, "exact peer sample rate persist false");
    Check(result.After?.ReportedTargetSampleRateHz == rate && !result.WireActualVerified && result.WireActualSampleRateHz is null, "readback never proves wire audio");
}
foreach (bool accepted in new[] { false, true })
{
    var fake = Fake(); fake.Result = new() { SubmissionAttempted = true, Accepted = accepted, TargetMatches = false, Win32Error = 5 };
    var result = new DriverAudioService(fake).RequestRate(Peer, 96000);
    Check(fake.Applies == 1 && result.SubmissionAttempted && !result.RequiresElevationBeforeSend && !result.MayRetryAutomatically, "uncertain send or readback never retries/elevates");
}
{
    var fake = Fake(); fake.Result = new() { Win32Error = 5, SubmissionAttempted = false };
    var result = new DriverAudioService(fake).RequestRate(Peer, 96000);
    Check(result.RequiresElevationBeforeSend && !result.SubmissionAttempted && fake.Applies == 1, "permission denied before send available to root helper");
}
{
    var fake = Fake(); fake.ApplyException = new UnauthorizedAccessException(Id);
    var result = new DriverAudioService(fake).RequestRate(Peer, 96000);
    Check(result.SubmissionAttempted && fake.Applies == 1 && !result.RequiresElevationBeforeSend, "unexpected Apply exception conservatively uncertain");
}
{
    var fake = Fake(); fake.Snapshot.Failure = "Win32 5: " + Id;
    var result = new DriverAudioService(fake).RequestRate(Peer, 96000);
    Check(result.RequiresElevationBeforeSend && fake.Applies == 0, "fresh GET native error preserved");
    var json = JsonSerializer.Serialize(result);
    Check(!json.Contains("112233445566") && !json.Contains("BTHENUM") && !json.Contains("same display"), "result JSON contains no peer identity or name");
}
{
    var fake = Fake(); var report = new DriverAudioService(fake).ReadForPeer(Peer);
    Check(report.InterfaceVersion == 1 && report.StreamState == 1 && report.WorkingQualityIndex == 8 && report.ReportedTargetQualityIndex == 13
        && report.BitDepths.SequenceEqual(new uint[] { 16, 24 }) && report.Qualities.SequenceEqual(new uint[] { 1, 13 }), "report preserves numeric evidence");
    report.SampleRates[0] = 1;
    Check(fake.Snapshot.SampleRates[0] == 48000, "snapshot arrays copied");
}
{
    var chains = new IReadOnlyList<string>[] { [differentPeer.InstanceId], [@"SAVI_BTAUDIO_ENUM\offline_AUDIO", Id] };
    Check(EndpointIdentity.SelectIndex(chains, Id) == 1, "endpoint maps through exact sink ancestor");
    Check(EndpointIdentity.SelectIndex(chains, Id.ToLowerInvariant()) == 1, "endpoint identity comparison ignores case");
    foreach (var invalidChains in new IReadOnlyList<IReadOnlyList<string>>[] {
        [], [[differentPeer.InstanceId]], [[Id], [Id]], [[Id + "_extra"]] })
    {
        bool rejected = false;
        try { EndpointIdentity.SelectIndex(invalidChains, Id); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "absent, ambiguous or substring endpoint cannot be selected");
    }
}
foreach (uint rate in new uint[] { 96000, 192000 })
{
    var pointer = WaveFormat.PcmStereo16(rate);
    try
    {
        var format = WaveFormat.Parse(pointer);
        Check(format.SampleRateHz == rate && format.Channels == 2 && format.BitsPerSample == 16 && format.BlockAlign == 4
            && WaveFormat.IsSafeToRender(format), "requested PCM ABI format is explicit and valid");
        Check(!WaveFormat.IsSafeToRender(format with { FormatTag = 0x55 }), "compressed mix format never rendered as PCM");
    }
    finally { Marshal.FreeCoTaskMem(pointer); }
}
{
    var pointer = Marshal.AllocCoTaskMem(40);
    try
    {
        Marshal.Copy(new byte[40], 0, pointer, 40);
        Marshal.WriteInt16(pointer, 0, unchecked((short)0xFFFE));
        bool rejected = false;
        try { WaveFormat.Parse(pointer); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "truncated extensible format rejected before reading subformat");
        Marshal.WriteInt16(pointer, 2, 2); Marshal.WriteInt32(pointer, 4, 48000);
        Marshal.WriteInt16(pointer, 12, 8); Marshal.WriteInt16(pointer, 14, 32); Marshal.WriteInt16(pointer, 16, 22);
        Marshal.StructureToPtr(new Guid("00000003-0000-0010-8000-00aa00389b71"), IntPtr.Add(pointer, 24), false);
        var format = WaveFormat.Parse(pointer);
        Check(WaveFormat.IsSafeToRender(format) && format.SampleRateHz == 48000 && format.FormatTag == 0xFFFE,
            "native floating-point 48k engine format preserved separately");
    }
    finally { Marshal.FreeCoTaskMem(pointer); }
}
{
    var unsupported = await AudioPlaybackProbe.RunAsync(Peer, 48000, CancellationToken.None);
    Check(unsupported.Failure is not null && !unsupported.EndpointMatched && !unsupported.Initialized, "unsupported probe returns before native endpoint work");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var cancelledReport = await AudioPlaybackProbe.RunAsync(Peer, 96000, cancelled.Token);
    Check(cancelledReport.Cancelled && !cancelledReport.EndpointMatched && !cancelledReport.Started, "pre-cancelled probe has no native side effects");
    var json = JsonSerializer.Serialize(new PlaybackReport { RequestedSampleRateHz = 192000,
        EngineMixFormat = new(48000, 2, 32, 3, 8), WasapiClientFormat = new(192000, 2, 16, 1, 4),
        DriverBefore = new DriverAudioService(Fake()).ReadForPeer(Peer) });
    Check(!json.Contains("112233445566") && !json.Contains("BTHENUM") && json.Contains("\"WireActualVerified\":false"),
        "playback report JSON exposes no peer identity or false codec verification");
}
{
    int attempts = 0, waits = 0;
    var trace = new EndpointTrace();
    int selected = EndpointIdentity.WaitForStable(() =>
    {
        attempts++;
        return EndpointIdentity.SelectIndex(attempts < 3 ? [new string[] { differentPeer.InstanceId }] : [new string[] { Id }], Id);
    }, CancellationToken.None, trace, _ => waits++);
    Check(selected == 0 && attempts == 3 && waits == 2 && trace.Attempts == 3, "transient missing exact endpoint bounded to three pure selection attempts");
    attempts = waits = 0;
    try
    {
        EndpointIdentity.WaitForStable<int>(() => { attempts++; throw new EndpointMatchException(2, 0); },
            CancellationToken.None, trace, _ => waits++);
        Check(false, "persistent absence must fail");
    }
    catch (EndpointMatchException) { Check(attempts == 3 && waits == 2, "persistent absence stops after three attempts"); }
    attempts = waits = 0;
    try
    {
        EndpointIdentity.WaitForStable(() => { attempts++; return EndpointIdentity.SelectIndex([[Id], [Id]], Id); },
            CancellationToken.None, trace, _ => waits++);
        Check(false, "ambiguity must fail");
    }
    catch (EndpointMatchException e) { Check(e.MatchCount == 2 && attempts == 1 && waits == 0, "ambiguous endpoint refused without retry"); }
    attempts = waits = 0;
    try
    {
        EndpointIdentity.WaitForStable<int>(() => { attempts++; throw new COMException("withheld", unchecked((int)0x80004002)); },
            CancellationToken.None, trace, _ => waits++);
        Check(false, "unexpected native error must fail");
    }
    catch (COMException) { Check(attempts == 1 && waits == 0, "E_NOINTERFACE not hidden by identity retry"); }
    attempts = waits = 0;
    int result = EndpointIdentity.WaitForStable(() =>
    {
        attempts++;
        if (attempts == 1) throw new COMException("withheld", unchecked((int)0x88890004));
        return 1;
    }, CancellationToken.None, trace, _ => waits++);
    Check(result == 1 && attempts == 2 && waits == 1, "device invalidated identity enumeration can retry");
    attempts = waits = 0;
    using var stop = new CancellationTokenSource();
    try
    {
        EndpointIdentity.WaitForStable<int>(() => { attempts++; throw new EndpointMatchException(1, 0); },
            stop.Token, trace, _ => { waits++; stop.Cancel(); });
        Check(false, "cancelled identity wait must stop");
    }
    catch (OperationCanceledException) { Check(attempts == 1 && waits == 1, "cancellation prevents next identity attempt"); }
    trace.Phase = "Identity.Adapter.ReadInstanceId";
    Check(trace.Failed(unchecked((int)0x80070490)) && trace.LastNativeFailurePhase == trace.Phase
        && trace.LastNativeFailureHResult == unchecked((int)0x80070490), "optional property error retains sanitized phase and native code");
    var json = JsonSerializer.Serialize(new PlaybackReport { FailurePhase = "Identity.NoExactMatch",
        ActiveRenderEndpointCount = 2, ExactEndpointMatchCount = 0, EndpointIdentityAttempts = 3,
        EndpointLastNativeFailurePhase = trace.LastNativeFailurePhase, EndpointLastNativeFailureHResult = trace.LastNativeFailureHResult });
    Check(json.Contains("Identity.NoExactMatch") && !json.Contains(Id) && !json.Contains("112233445566"), "failure diagnostics carry phase and counts without identifiers");
}
SdkGuidChecks.Run(Check);
Console.WriteLine($"PASS {passed} offline assertions; fake Core only; no driver, endpoint, playback or capture operations.");

sealed class FakeCore : IDriverCore
{
    public IReadOnlyList<AudioDevice> Devices { get; set; } = [];
    public DriverSnapshot Snapshot { get; set; } = new();
    public ApplyResult Result { get; set; } = new();
    public Exception? ApplyException { get; set; }
    public int Enumerations { get; private set; }
    public int Reads { get; private set; }
    public int Applies { get; private set; }
    public string? LastId { get; private set; }
    public SettingKind LastKind { get; private set; }
    public uint LastValue { get; private set; }
    public bool LastPersist { get; private set; }
    public IReadOnlyList<AudioDevice> Enumerate() { Enumerations++; return Devices; }
    public DriverSnapshot Read(string id) { Reads++; return Snapshot; }
    public ApplyResult Apply(string id, SettingKind kind, uint value, bool persist)
    {
        Applies++; LastId = id; LastKind = kind; LastValue = value; LastPersist = persist;
        if (ApplyException is not null) throw ApplyException;
        return Result;
    }
}
