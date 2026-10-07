using System.Buffers.Binary;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;

int passed = 0;
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task Test(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
void RemoveTestDirectory(string directory)
{
    string full = Path.GetFullPath(directory), root = Path.GetFullPath(Path.GetTempPath());
    string relative = Path.GetRelativePath(root, full);
    Check(!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal)
        && Path.GetFileName(full).StartsWith("HeyMelody.Calibration.Tests-", StringComparison.Ordinal));
    if (Directory.Exists(full)) Directory.Delete(full, true);
}
CalibrationDeviceContext Context(bool fit = true) => new()
{
    StableKey = "oppo/test-device", Generation = 7, Fit = fit, HearingV1 = true, HearingV2 = true, EarScan = true,
    Commands = new HashSet<ushort> { 0x0107, 0x0405, 0x040D, 0x040E, 0x0415, 0x0115, 0x0116, 0x011E }, Events = new HashSet<byte> { 4, 14 }
};
HearingLevel[] Levels() => Enumerable.Range(0, 12).Select(x => new HearingLevel((byte)(x / 6 + 1), (byte)(x % 6 + 1), (sbyte)(x - 20))).ToArray();

await Test("APK calibration payloads preserve signed dB and big endian signed UID", () =>
{
    Check(CalibrationCodecs.Fit(true).SequenceEqual(new byte[] { 1 }));
    Check(CalibrationCodecs.HearingDetection(false).SequenceEqual(new byte[] { 2, 0 }));
    Check(CalibrationCodecs.EarScanDetection(true, -2).SequenceEqual(new byte[] { 4, 1, 255, 255, 255, 254 }));
    Check(CalibrationCodecs.Frequency(new(2, 6, -55)).SequenceEqual(new byte[] { 3, 1, 2, 6, 201 }));
    Check(CalibrationCodecs.Parameters(4, [], -2).SequenceEqual(new byte[] { 4, 0, 255, 255, 255, 254 }));
    Check(CalibrationCodecs.SaveEarScan([9, 8], -2).SequenceEqual(new byte[] { 3, 2, 0, 9, 8, 255, 255, 255, 254 }));
    Check(CalibrationCodecs.FilterRequest([new(1, 1, -55)], -2).SequenceEqual(new byte[] { 1, 1, 1, 201, 255, 255, 255, 254 }));
    return Task.CompletedTask;
});
await Test("Current waveform and scan strict parsing", () =>
{
    byte[] parameters = CalibrationCodecs.Parameters(3, Levels(), -123, "我的听感");
    var result = CalibrationCodecs.ParseCurrentHearing([0, .. parameters]);
    Check(result.Uid == -123 && result.Action == 3 && result.Name == "我的听感" && result.Levels.SequenceEqual(Levels()));
    var scan = CalibrationCodecs.ParseCurrentEarScan([0, .. CalibrationCodecs.SaveEarScan([1, 2, 3], -123)]);
    Check(scan.Uid == -123 && scan.Data.SequenceEqual(new byte[] { 1, 2, 3 }));
    Throws<FormatException>(() => CalibrationCodecs.ParseEarScan([1, 20, 0, 0, 0, 0, 0]));
    Throws<FormatException>(() => CalibrationCodecs.ParseCurrentHearing([0, 3, 1, 1, 7, 0, 0, 0, 0, 1]));
    Throws<CalibrationDeviceException>(() => CalibrationCodecs.ParseCurrentHearing([0x10]));
    return Task.CompletedTask;
});
await Test("Fit accepts original left/right codes and rejects duplicate/truncated results", () =>
{
    var fit = CalibrationCodecs.ParseFit([2, 6, 1, 0]); Check(fit[1] == 0 && fit[2] == 6);
    Throws<FormatException>(() => CalibrationCodecs.ParseFit([1, 1, 1, 0]));
    Throws<FormatException>(() => CalibrationCodecs.ParseFit([1, 1, 2, 2]));
    Throws<FormatException>(() => CalibrationCodecs.ParseFit([1, 1]));
    Check(CalibrationCodecs.TryEvent(new(0x8202, 1, [0, 4, 1, 1, 2, 6]), 4, out var data) && data.Length == 4);
    Check(!CalibrationCodecs.TryEvent(new(0x0503, 1, [4, 1, 1, 2, 6]), 4, out _));
    return Task.CompletedTask;
});
await Test("V1 original branches and rounding converge to bounds", () =>
{
    var search = new HearingV1Search(); int[] no = [10, 15, 18, 20];
    for (int i = 0; i < no.Length; i++) { bool finished = search.Answer(false); Check(search.Current == no[i] && finished == (i == 3)); }
    search = new(); int[] yes = [-30, -45, -53, -57, -60];
    for (int i = 0; i < yes.Length; i++) { bool finished = search.Answer(true); Check(search.Current == yes[i] && finished == (i == 4)); }
    search = new(); bool[] mixed = [false, true, false, true, false]; int[] expected = [10, 5, 8, 6, 8];
    for (int i = 0; i < mixed.Length; i++) { bool finished = search.Answer(mixed[i]); Check(search.Current == expected[i] && finished == (i == 4)); }
    return Task.CompletedTask;
});
await Test("Fit notification before ACK cannot be lost and revisions grow", async () =>
{
    var fake = new Fake(); fake.OnRequest = (c, p) => { if (c == 0x0405 && p[0] == 1) fake.Push(0x0204, [4, 1, 1, 2, 6]); return null; };
    await using var engine = new CalibrationEngine(fake, Context()); var revisions = new List<long>(); engine.Changed += s => revisions.Add(s.Revision);
    var state = await engine.RunFitAsync(); Check(state.Stage == CalibrationStage.Completed && state.FitResults[2] == 6);
    Check(revisions.SequenceEqual(revisions.Order()) && revisions.Distinct().Count() == revisions.Count);
});
await Test("Missing declaration or missing dual wearing never sends Fit SET", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context(false));
    try { await engine.RunFitAsync(); throw new Exception("Expected unsupported"); } catch (NotSupportedException) { }
    Check(fake.Sent.Count == 0);
    var second = new Fake { Wearing = false }; await using var other = new CalibrationEngine(second, Context());
    try { await other.RunFitAsync(); throw new Exception("Expected wearing guard"); } catch (InvalidOperationException) { }
    Check(second.Sent.All(x => x.Command != 0x0405));
});
await Test("Fit rejects original ACK codes without fake success", async () =>
{
    foreach (byte status in new byte[] { 9, 15, 16 })
    {
        var fake = new Fake(); fake.OnRequest = (c, p) => c == 0x0405 ? new((ushort)(c | 0x8000), 1, [status]) : null;
        await using var engine = new CalibrationEngine(fake, Context());
        try { await engine.RunFitAsync(); throw new Exception("Expected rejection"); } catch (CalibrationDeviceException e) { Check(e.Status == status); }
        Check(engine.State.Stage == CalibrationStage.Failed && engine.State.RawStatus == status);
    }
});
await Test("Cancel sends explicit Fit stop while event await does not hold the gate", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context());
    Task<CalibrationSnapshot> run = engine.RunFitAsync();
    for (int i = 0; engine.State.Stage != CalibrationStage.Measuring && i < 100; i++) await Task.Delay(1);
    await engine.CancelAsync(); try { await run; } catch (OperationCanceledException) { }
    Check(engine.State.Stage == CalibrationStage.Cancelled && fake.Sent.Any(x => x.Command == 0x0405 && x.Payload.SequenceEqual(new byte[] { 0 })));
    fake.Push(0x0204, [4, 1, 1, 2, 1]); Check(engine.State.Stage == CalibrationStage.Cancelled);
});
await Test("Ear scan ignores wrong signed UID and accepts matching nonempty event 14", async () =>
{
    var fake = new Fake(); fake.OnRequest = (c, p) =>
    {
        if (c == 0x040D && p[0] == 4 && p[1] == 1)
        {
            int uid = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(2));
            fake.Push(0x0204, [14, .. CalibrationCodecs.SaveEarScan([7], unchecked(uid + 1))]);
            fake.Push(0x0204, [14, .. CalibrationCodecs.SaveEarScan([8], uid)]);
        }
        return null;
    };
    await using var engine = new CalibrationEngine(fake, Context()); var scan = await engine.StartEarScanAsync();
    Check(scan.Stage == CalibrationStage.Completed && scan.ScanResult!.Data.SequenceEqual(new byte[] { 8 }));
    int uid2 = scan.Uid; await engine.BeginHearingAsync(HearingVersion.V2); Check(engine.State.Uid == uid2 && engine.State.ScanResult is not null);
});
await Test("V2 pause ACK precedes next type and failed pause cannot advance", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context()); await engine.BeginHearingAsync(HearingVersion.V2);
    Check(engine.State.Decibels == -19 && engine.State.FrequencyType == 1);
    await engine.SetLevelAsync(0, true); await engine.ContinueAsync();
    Check(engine.State.StepIndex == 1 && engine.State.FrequencyType == 2 && engine.State.HearingLevels[0].Decibels == -55);
    var commands = fake.Sent.Where(x => x.Command == 0x040E).Select(x => x.Payload).ToArray();
    Check(commands[^2][0] == 4 && commands[^1].SequenceEqual(new byte[] { 3, 1, 1, 2, 237 }));
    fake.OnRequest = (c, p) => c == 0x040E && p[0] == 4 ? new(0x840E, 1, [15]) : null;
    try { await engine.ContinueAsync(); throw new Exception("Expected rejected pause"); } catch (CalibrationDeviceException) { }
    Check(engine.State.Stage == CalibrationStage.Failed && engine.State.StepIndex == 1);
});
await Test("History apply guards identity, missing scan and replacement before any SET", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context());
    var entry = new CalibrationHistoryEntry("other/device", -5, "旧听感", HearingVersion.V2, Levels(), null, DateTimeOffset.UtcNow);
    try { await engine.ApplyHistoryAsync(entry); throw new Exception("Expected identity guard"); } catch (InvalidOperationException) { }
    Check(fake.Sent.Count == 0);
    entry = entry with { StableKey = "oppo/test-device" }; fake.Hearing = CalibrationCodecs.Parameters(3, Levels(), 100, "旧听感");
    await ThrowsAsync<InvalidDataException>(() => engine.ApplyHistoryAsync(entry, true)); Check(fake.Sent.Count == 0);
    await using var other = new CalibrationEngine(fake, Context() with { EarScan = false });
    await ThrowsAsync<InvalidOperationException>(() => other.ApplyHistoryAsync(entry));
    Check(fake.Sent.All(x => x.Command < 0x0400) && other.State.Stage == CalibrationStage.Idle);
    await ThrowsAsync<InvalidDataException>(() => other.ApplyHistoryAsync(entry, true));
    Check(other.State.Stage == CalibrationStage.Failed);
});
await Test("Filter assembler checks UID, fragments, order and float endianness", () =>
{
    byte[] bytes = new byte[14]; bytes[0] = 0; BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), -1); bytes[5] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0x180); bytes[8] = 1; bytes[9] = 2; BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(10), 1.25f);
    var first = CalibrationCodecs.ParseFilter(bytes); Check(first.Uid == -1 && first.Values[0] == 1.25f);
    var assembler = new HearingFilterAssembler(-1); Check(assembler.Add(first) is null);
    Check(assembler.Add(first with { Uid = 3, Index = 2 }) is null);
    var curve = assembler.Add(first with { Index = 2, Values = new float[] { -2.5f } }); Check(curve!.Values.SequenceEqual(new float[] { 1.25f, -2.5f }));
    Throws<FormatException>(() => assembler.Add(first with { Values = new float[] { 9 } }));
    return Task.CompletedTask;
});
await Test("History persists signed UID and enforces ten entry limit", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "HeyMelody.Calibration.Tests-" + Guid.NewGuid().ToString("N"));
    var store = new CalibrationHistoryStore(directory);
    try
    {
        for (int i = 0; i < 10; i++) await store.SaveAsync(new("device", -i - 1, "听感" + i, HearingVersion.V2, Levels(), null, DateTimeOffset.UtcNow.AddSeconds(i)));
        var candidate = new CalibrationHistoryEntry("device", -12, "新听感", HearingVersion.V2, Levels(), null, DateTimeOffset.UtcNow.AddSeconds(12));
        await ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(candidate));
        await ThrowsAsync<InvalidOperationException>(() => store.CheckSaveAsync(candidate with { Name = "听感0" }, -2));
        await ThrowsAsync<InvalidOperationException>(() => store.CheckSaveAsync(candidate, -99));
        Check((await store.ReadAsync("device")).Count == 10);
        await store.SaveAsync(candidate, replacementUid: -1);
        var entries = await store.ReadAsync("device"); Check(entries.Count == 10 && entries[0].Uid == -12 && entries.All(x => x.Uid < 0) && entries.All(x => x.Uid != -1));
        await store.DeleteAsync("device", -12); Check((await store.ReadAsync("device")).Count == 9);
        Throws<ArgumentException>(() => CalibrationHistoryStore.ValidateName(" "));
        Throws<ArgumentException>(() => CalibrationHistoryStore.ValidateName(new string('a', 41)));
    }
    finally { RemoveTestDirectory(directory); }
});
await Test("Previous pauses before playback and removes stale later measurements", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context());
    await engine.BeginHearingAsync(HearingVersion.V2); await engine.SetLevelAsync(0, true); await engine.ContinueAsync();
    await engine.SetLevelAsync(11, true); await engine.ContinueAsync();
    int before = fake.Sent.Count; await engine.PreviousAsync();
    Check(engine.State.StepIndex == 1 && engine.State.FrequencyType == 2 && engine.State.LevelIndex == 11
        && engine.State.Decibels == 17 && engine.State.HearingLevels.Count == 1);
    var writes = fake.Sent.Skip(before).Where(x => x.Command == 0x040E).ToArray();
    Check(writes.Length == 2 && writes[0].Payload[0] == 4 && writes[1].Payload.SequenceEqual(new byte[] { 3, 1, 1, 2, 17 }));
    await engine.PreviousAsync(); Check(engine.State.StepIndex == 0 && engine.State.LevelIndex == 0 && engine.State.HearingLevels.Count == 0);
});
await Test("V1 previous resets the exact original search bracket", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context());
    await engine.BeginHearingAsync(HearingVersion.V1);
    for (int i = 0; i < 4; i++) await engine.AnswerAsync(false);
    Check(engine.State.StepIndex == 1 && engine.State.HearingLevels[0].Decibels == 20);
    await engine.PreviousAsync();
    Check(engine.State.StepIndex == 0 && engine.State.Decibels == 0 && engine.State.LowerBound == -60 && engine.State.UpperBound == 20 && engine.State.HearingLevels.Count == 0);
});
await Test("Full V2 save confirms profile then persists history, with preflight guards", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "HeyMelody.Calibration.Tests-" + Guid.NewGuid().ToString("N"));
    var store = new CalibrationHistoryStore(directory); var fake = new Fake();
    try
    {
        await using var engine = new CalibrationEngine(fake, Context() with { EarScan = false }, store);
        await engine.BeginHearingAsync(HearingVersion.V2);
        for (int i = 0; i < 12; i++) await engine.ContinueAsync();
        Check(engine.State.Stage == CalibrationStage.Paused && engine.State.StepIndex == 12);
        for (int i = 0; i < 10; i++) await store.SaveAsync(new("oppo/test-device", i, "已有" + i, HearingVersion.V2, Levels(), null, DateTimeOffset.UtcNow));
        int before = fake.Sent.Count;
        await ThrowsAsync<InvalidOperationException>(() => engine.SaveHearingAsync("新听感", true));
        Check(fake.Sent.Count == before && engine.State.Stage == CalibrationStage.Paused);
        await ThrowsAsync<InvalidOperationException>(() => engine.SaveHearingAsync("已有1", true, replaceHistoryUid: 0));
        Check(fake.Sent.Count == before && engine.State.Stage == CalibrationStage.Paused);
        fake.Hearing = CalibrationCodecs.Parameters(3, Levels(), 100, "手机听感");
        await ThrowsAsync<InvalidOperationException>(() => engine.SaveHearingAsync("新听感", false, replaceHistoryUid: 0));
        Check(fake.Sent.Skip(before).All(x => x.Command < 0x0400) && engine.State.Stage == CalibrationStage.Paused);
        fake.OnRequest = (c, p) => { if (c == 0x040E && p[0] == 3 && p[1] == 12) fake.Hearing = p.ToArray(); return null; };
        await engine.SaveHearingAsync("新听感", true, replaceHistoryUid: 0);
        var saved = await store.ReadAsync("oppo/test-device");
        Check(engine.State.Stage == CalibrationStage.Completed && saved.Count == 10 && saved.Any(x => x.Uid == engine.State.Uid && x.Name == "新听感") && saved.All(x => x.Uid != 0));
    }
    finally { RemoveTestDirectory(directory); }
});
await Test("Cancellation cannot clear a calibration changed by another controller", async () =>
{
    var fake = new Fake { Hearing = CalibrationCodecs.Parameters(3, Levels(), 100, "原听感") };
    await using var engine = new CalibrationEngine(fake, Context()); await engine.BeginHearingAsync(HearingVersion.V2);
    fake.Hearing = CalibrationCodecs.Parameters(3, Levels(), 200, "手机听感"); int before = fake.Sent.Count;
    await engine.CancelAsync();
    Check(engine.State.Stage == CalibrationStage.Cancelled && fake.Sent.Skip(before).All(x => x.Command < 0x0400)
        && engine.State.Message?.Contains("changed elsewhere", StringComparison.Ordinal) == true);
});
await Test("Disconnect during a command prevents late ACK from resurrecting a workflow", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context()); await engine.BeginHearingAsync(HearingVersion.V2);
    fake.OnRequest = (c, p) => { if (c == 0x040E && p[0] == 4) fake.Disconnect(); return null; };
    await ThrowsAsync<OperationCanceledException>(() => engine.ContinueAsync());
    Check(engine.State.Stage is CalibrationStage.Failed or CalibrationStage.Cancelled && engine.State.StepIndex == 0);
});
Console.WriteLine($"{passed} calibration tests passed.");

sealed class Fake : ICalibrationTransport
{
    public bool IsConnected { get; private set; } = true;
    public event Action<Packet>? Notification;
    public event Action? Disconnected;
    public bool Wearing { get; init; } = true;
    public List<(ushort Command, byte[] Payload)> Sent { get; } = [];
    public Func<ushort, byte[], Packet?>? OnRequest { get; set; }
    public byte[] Hearing { get; set; } = CalibrationCodecs.Parameters(3, [], 0);
    public Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Sent.Add((command, payload.ToArray()));
        Packet? reply = OnRequest?.Invoke(command, payload);
        reply ??= new((ushort)(command | 0x8000), 1, command switch { 0x0107 => [0, 2, 1, Wearing ? (byte)2 : (byte)0, 2, 2], 0x0115 => [0, .. Hearing], _ => [0] });
        return Task.FromResult(reply);
    }
    public void Push(ushort command, byte[] data) => Notification?.Invoke(new(command, 1, data));
    public void Disconnect() { IsConnected = false; Disconnected?.Invoke(); }
}
