using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;
using static TestKit;

int passed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch { Console.WriteLine("FAIL " + name); throw; }
}

await Test("Structure X4 fixture includes synthetic 250/299 byte filters and 168-byte scan", () =>
{
    Check(Structure.Hearing.Length == 250 && Structure.ScanFilter.Length == 299 && Structure.ScanData.Length == 168);
    var hearing = new HearingFilterAssembler(Structure.Uid).Add(CalibrationCodecs.ParseFilter(Structure.Hearing))!;
    var scan = new EarScanFilterAssembler(Structure.Uid).Add(CalibrationCodecs.ParseEarScanFilter(Structure.ScanFilter))!;
    Check(hearing.Left.Count == 30 && hearing.Right.Count == 30 && hearing.EnhanceType == 1);
    Check(scan.Left.Count == 36 && scan.Right.Count == 36 && scan.SamplingFrequency == 44100);
    Check(CalibrationCodecs.FilterRequest(Structure.Levels, Structure.Uid).SequenceEqual(Structure.FilterRequest));
    Check(CalibrationCodecs.EarScanFilterRequest(Structure.ScanData, Structure.Uid).SequenceEqual(Structure.ScanFilterRequest)); return Task.CompletedTask;
});
await Test("V1 original bracket rounding and convergence", () =>
{
    var search = new HearingV1Search(); int[] no = [10, 15, 18, 20];
    for (int i = 0; i < no.Length; i++) Check(search.Answer(false) == (i == 3) && search.Current == no[i]);
    search = new(); int[] yes = [-30, -45, -53, -57, -60];
    for (int i = 0; i < yes.Length; i++) Check(search.Answer(true) == (i == 4) && search.Current == yes[i]);
    search = new(); bool[] mixed = [false, true, false, true, false]; int[] values = [10, 5, 8, 6, 8];
    for (int i = 0; i < mixed.Length; i++) Check(search.Answer(mixed[i]) == (i == 4) && search.Current == values[i]); return Task.CompletedTask;
});
await Test("Fit early event survives START ACK and completion waits for STOP ACK", async () =>
{
    var fake = new Fake(); var stop = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fake.Hook = (c, p, t) => c == 0x0405 && p[0] == 0 ? stop.Task : Task.FromResult<Packet?>(null);
    await using var engine = Engine(fake); var revisions = new List<long>(); engine.Changed += s => revisions.Add(s.Revision);
    var task = engine.RunFitAsync(); await Until(() => fake.Sent.Any(x => x.Command == 0x0405 && x.Payload[0] == 0));
    Check(!task.IsCompleted && engine.State.Stage == CalibrationStage.Measuring);
    stop.SetResult(new(0x8405, 1, [0])); var result = await task;
    Check(result.Stage == CalibrationStage.Completed && result.FitResults[1] == 1 && result.FitResults[2] == 1);
    Check(fake.Sent[0].Command == 0x0109 && fake.Sent.All(x => x.Command != 0x0107));
    Check(revisions.SequenceEqual(revisions.Order()) && revisions.Distinct().Count() == revisions.Count);
});
await Test("Fit START ACK without event times out and sends STOP", async () =>
{
    var fake = new Fake { FitEvent = false }; await using var engine = Engine(fake);
    await Throws<TimeoutException>(() => engine.RunFitAsync());
    Check(engine.State.Stage == CalibrationStage.Failed && engine.State.FitResults.Count == 0);
    Check(fake.Sent.Any(x => x.Command == 0x0405 && x.Payload.SequenceEqual(new byte[] { 0 })));
});
await Test("Failed Fit STOP retains ownership so Cancel retries", async () =>
{
    var fake = new Fake { Hook = (c, p, t) => Task.FromResult<Packet?>(c == 0x0405 && p[0] == 0 ? new(0x8405, 1, [9]) : null) };
    await using var engine = Engine(fake); await Throws<CalibrationDeviceException>(() => engine.RunFitAsync());
    Check(engine.State.Stage == CalibrationStage.Cancelling && engine.State.RawStatus == 9);
    int before = fake.Sent.Count; fake.Hook = null; await engine.CancelAsync();
    Check(engine.State.Stage == CalibrationStage.Cancelled && fake.Sent.Skip(before).Any(x => x.Command == 0x0405 && x.Payload[0] == 0));
});
await Test("Fit declaration and both-wearing guard prevent all SETs", async () =>
{
    var fake = new Fake(); await using var engine = new CalibrationEngine(fake, Context() with { Fit = false }, timings: Fast);
    await Throws<NotSupportedException>(() => engine.RunFitAsync()); Check(fake.Sent.Count == 0);
    var other = new Fake { Wearing = false }; await using var second = Engine(other);
    await Throws<InvalidOperationException>(() => second.RunFitAsync()); Check(other.Sent.All(x => x.Command < 0x0400));
});
await Test("Fit rejected START status is preserved without fake result or unnecessary STOP", async () =>
{
    foreach (byte status in new byte[] { 9, 15, 16 })
    {
        var fake = new Fake { Hook = (c, p, t) => Task.FromResult<Packet?>(c == 0x0405 ? new(0x8405, 1, [status]) : null) };
        await using var engine = Engine(fake); await Throws<CalibrationDeviceException>(() => engine.RunFitAsync());
        Check(engine.State.Stage == CalibrationStage.Failed && engine.State.RawStatus == status && fake.Sent.Count(x => x.Command == 0x0405) == 1);
    }
});
await Test("Fit cancellation interrupts result wait and late event cannot revive it", async () =>
{
    var fake = new Fake { FitEvent = false }; await using var engine = Engine(fake);
    var running = engine.RunFitAsync(); await Until(() => engine.State.Stage == CalibrationStage.Measuring);
    await engine.CancelAsync(); await Throws<OperationCanceledException>(() => running);
    fake.Push([4, 1, 1, 2, 1]); Check(engine.State.Stage == CalibrationStage.Cancelled);
});
await Test("Ear scan ignores wrong UID, does not require advertised event14/action3, and settles before hearing", async () =>
{
    var fake = new Fake { ScanEvent = false };
    fake.Hook = (c, p, t) =>
    {
        if (c == 0x040D && p[0] == 4 && p[1] == 1)
        {
            int uid = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(2));
            fake.Push([14, .. CalibrationCodecs.EarScanParameters(1, Structure.ScanData, unchecked(uid + 1))]);
            fake.Push([14, .. CalibrationCodecs.EarScanParameters(1, Structure.ScanData, uid)]);
        }
        return Task.FromResult<Packet?>(null);
    };
    await using var engine = Engine(fake, true, timing: Fast with { ScanSettle = TimeSpan.FromMilliseconds(20) });
    var scan = await engine.StartEarScanAsync(); Check(scan.Stage == CalibrationStage.Completed && scan.ScanResult!.Data.SequenceEqual(Structure.ScanData) && scan.Uid > 0);
    var stopped = fake.Sent.Single(x => x.Command == 0x040D && x.Payload[0] == 4 && x.Payload[1] == 0);
    await engine.BeginHearingAsync(HearingVersion.V2);
    var started = fake.Sent.Single(x => x.Command == 0x040D && x.Payload.SequenceEqual(new byte[] { 2, 1 }));
    Check(engine.State.Uid == scan.Uid && engine.State.ScanResult!.Uid == scan.Uid && started.At - stopped.At >= TimeSpan.FromMilliseconds(15));
});
await Test("Empty ear scan event does not succeed and cancellation stops detector", async () =>
{
    var fake = new Fake { ScanEvent = false }; await using var engine = Engine(fake, true);
    var run = engine.StartEarScanAsync(); await Until(() => engine.State.Stage == CalibrationStage.Measuring);
    fake.Push([14, .. CalibrationCodecs.EarScanParameters(1, [], engine.State.Uid)]);
    Check(!run.IsCompleted); await engine.CancelAsync(); await Throws<OperationCanceledException>(() => run);
    Check(fake.Sent.Any(x => x.Command == 0x040D && x.Payload[0] == 4 && x.Payload[1] == 0));
});
await Test("Absent hearing NV permits new test at index5/-19 with exact five-byte tone", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    Check(engine.State.Decibels == -19 && engine.State.LevelIndex == 5 && engine.State.DesiredLevelIndex == 5 && engine.State.ToneAcknowledged);
    Check(fake.Sent.Any(x => x.Command == 0x040E && x.Payload.SequenceEqual(new byte[] { 3, 1, 1, 1, 237 })));
    Check(fake.Sent.Single(x => x.Command == 0x040D).Payload.SequenceEqual(new byte[] { 2, 1 }));
});
await Test("Unknown current-profile error is not treated as no history; required scan cannot be bypassed", async () =>
{
    var fake = new Fake { Hook = (c, p, t) => Task.FromResult<Packet?>(c == 0x0115 ? new(0x8115, 1, [15]) : null) };
    await using var engine = Engine(fake); await Throws<CalibrationDeviceException>(() => engine.BeginHearingAsync(HearingVersion.V2));
    Check(engine.State.Stage == CalibrationStage.Failed && fake.Sent.All(x => x.Command != 0x040D));
    var other = new Fake(); await using var second = Engine(other, true);
    await Throws<InvalidOperationException>(() => second.BeginHearingAsync(HearingVersion.V2)); Check(other.Sent.Count == 0);
});
await Test("Slider exposes desired/applied difference and waits pause ACK before sending new tone", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    var pause = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fake.Hook = (c, p, t) => c == 0x040E && p[0] == 4 ? pause.Task : Task.FromResult<Packet?>(null);
    int before = fake.Sent.Count; var set = engine.SetLevelAsync(4, true); await Until(() => fake.Sent.Count > before);
    Check(engine.State.Stage == CalibrationStage.AwaitingAnswer && !engine.State.ToneAcknowledged && engine.State.DesiredLevelIndex == 4 && engine.State.LevelIndex == 5);
    Check(fake.Sent.Count - before == 1 && fake.Sent[^1].Payload.SequenceEqual(new byte[] { 4 }));
    pause.SetResult(new(0x840E, 1, [0])); await set;
    Check(engine.State.Decibels == -25 && engine.State.ToneAcknowledged); fake.Hook = null; await engine.ContinueAsync();
    Check(engine.State.HearingLevels[0].Decibels == -25 && engine.State.StepIndex == 1 && engine.State.Decibels == -19);
});
await Test("Next cannot record a displayed value pending trailing throttle", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake, timing: Fast with { LevelThrottle = TimeSpan.FromMilliseconds(150) });
    await engine.BeginHearingAsync(HearingVersion.V2); var pending = engine.SetLevelAsync(0, false);
    await Throws<InvalidOperationException>(() => engine.ContinueAsync());
    Check(engine.State.StepIndex == 0 && engine.State.HearingLevels.Count == 0 && !engine.State.ToneAcknowledged);
    await pending; await engine.ContinueAsync(); Check(engine.State.HearingLevels[0].Decibels == -55);
});
await Test("Superseded slider request neither fails nor stops workflow; final ACK wins", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    var pause = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously); int pauses = 0;
    fake.Hook = (c, p, t) => c == 0x040E && p[0] == 4 && Interlocked.Increment(ref pauses) == 1 ? pause.Task : Task.FromResult<Packet?>(null);
    var first = engine.SetLevelAsync(0, true); await Until(() => pauses == 1);
    var second = engine.SetLevelAsync(11, true); Check(!second.IsCompleted); pause.SetResult(new(0x840E, 1, [0])); await Task.WhenAll(first, second);
    Check(engine.State.Stage == CalibrationStage.AwaitingAnswer && engine.State.LevelIndex == 11 && engine.State.Decibels == 17 && engine.State.ToneAcknowledged);
    Check(fake.Sent.Count(x => x.Command == 0x040D) == 1);
});
await Test("Rejected slider pause prevents tone and recording", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2); int before = fake.Sent.Count;
    fake.Hook = (c, p, t) => Task.FromResult<Packet?>(c == 0x040E && p[0] == 4 ? new(0x840E, 1, [15]) : null);
    await Throws<CalibrationDeviceException>(() => engine.SetLevelAsync(0, true));
    Check(engine.State.Stage == CalibrationStage.Failed && engine.State.StepIndex == 0 && fake.Sent.Skip(before).All(x => x.Command != 0x040E || x.Payload[0] != 3));
});
await Test("Previous discards stale measurements and resets V2 to -19", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    await engine.SetLevelAsync(0, true); await engine.ContinueAsync(); await engine.SetLevelAsync(11, true); await engine.ContinueAsync();
    int before = fake.Sent.Count; await engine.PreviousAsync(); var calls = fake.Sent.Skip(before).Where(x => x.Command == 0x040E).ToArray();
    Check(engine.State.StepIndex == 1 && engine.State.HearingLevels.Count == 1 && engine.State.LevelIndex == 5 && engine.State.Decibels == -19);
    Check(calls.Length == 2 && calls[0].Payload.SequenceEqual(new byte[] { 4 }) && calls[1].Payload.SequenceEqual(new byte[] { 3, 1, 1, 2, 237 }));
    await engine.PreviousAsync(); Check(engine.State.StepIndex == 0 && engine.State.HearingLevels.Count == 0);
});
await Test("V1 Previous resets original search and full 12 tasks stop before filters", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V1);
    for (int i = 0; i < 4; i++) await engine.AnswerAsync(false);
    await engine.PreviousAsync(); Check(engine.State.Decibels == 0 && engine.State.LowerBound == -60 && engine.State.UpperBound == 20);
    for (int i = 0; i < 48; i++) await engine.AnswerAsync(false);
    Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.HearingLevels.Count == 12 && engine.State.HearingLevels.All(x => x.Decibels == 20));
    Check(fake.Sent.FindIndex(x => x.Command == 0x040D && x.Payload[1] == 0) < fake.Sent.FindIndex(x => x.Command == 0x0116));
});
await Test("Synthetic X4 12 choices stop detector and complete independent hearing/scan filters", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake, true); await Complete(engine, true, Structure.Levels);
    Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.FiltersReady && engine.State.CanPreview && !engine.State.ToneAcknowledged);
    Check(engine.State.HearingLevels.SequenceEqual(Structure.Levels) && engine.State.Curves[0].Left.Count == 30 && engine.State.EarScanCurves[0].Right.Count == 36);
    Check(engine.State.RestoreDescriptionId is >= 7 and <= 12);
    int stop = fake.Sent.FindIndex(x => x.Command == 0x040D && x.Payload.SequenceEqual(new byte[] { 2, 0 }));
    Check(stop < fake.Sent.FindIndex(x => x.Command == 0x0116) && stop < fake.Sent.FindIndex(x => x.Command == 0x011F));
});
await Test("Missing right ear scan fragment keeps result unavailable until asynchronous completion", async () =>
{
    var fake = new Fake { FilterFactory = (c, p) => Structure.Filter(c, BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(p.Length - 4)), c == 0x011F ? (byte)1 : (byte)4) };
    await using var engine = Engine(fake, true); await engine.StartEarScanAsync(); await engine.BeginHearingAsync(HearingVersion.V2);
    for (int i = 0; i < 11; i++) await engine.ContinueAsync(); var last = engine.ContinueAsync();
    await Until(() => fake.Sent.Any(x => x.Command == 0x011F));
    Check(!last.IsCompleted && engine.State.Stage == CalibrationStage.LoadingCurves && !engine.State.FiltersReady && engine.State.Curves.Count == 1 && engine.State.EarScanCurves.Count == 0);
    fake.PushPacket(0x811F, Structure.Filter(0x011F, engine.State.Uid, 2)); await last; Check(engine.State.Stage == CalibrationStage.ResultReady);
});
await Test("Filter timeout retains 12 answers, prevents Save and permits GET-only retry", async () =>
{
    var fake = new Fake { FilterFactory = (c, p) => Structure.Filter(c, BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(p.Length - 4)), 1) };
    await using var engine = Engine(fake, true); await Throws<TimeoutException>(() => Complete(engine, true));
    Check(engine.State.Stage == CalibrationStage.Failed && engine.State.HearingLevels.Count == 12 && !engine.State.FiltersReady);
    await Throws<InvalidOperationException>(() => engine.SaveHearingAsync("test", true));
    int before = fake.Sent.Count; fake.FilterFactory = null; await engine.ReadCurvesAsync();
    Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.FiltersReady && fake.Sent.Skip(before).All(x => x.Command < 0x0400));
});
await Test("Event8 during START ACK cannot be overwritten; resolved interruption needs explicit replay", async () =>
{
    var fake = new Fake(); fake.Hook = (c, p, t) => { if (c == 0x040D && p.SequenceEqual(new byte[] { 2, 1 })) fake.Push([8, 2, 1]); return Task.FromResult<Packet?>(null); };
    await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    Check(engine.State.Stage == CalibrationStage.Interrupted && !engine.State.CanResume && !engine.State.ToneAcknowledged);
    Check(fake.Sent.All(x => x.Command != 0x040E || x.Payload[0] != 3));
    await Throws<InvalidOperationException>(() => engine.ResumeAsync()); fake.Push([8, 2, 2]); Check(engine.State.CanResume);
    fake.Hook = null; await engine.ResumeAsync(); Check(engine.State.Stage == CalibrationStage.AwaitingAnswer && engine.State.StepIndex == 0 && engine.State.Decibels == -19 && engine.State.ToneAcknowledged);
});
await Test("Wear interruption prevents late tone ACK from reviving playback and resumes same selection", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    var tone = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fake.Hook = (c, p, t) => c == 0x040E && p.Length == 5 ? tone.Task : Task.FromResult<Packet?>(null);
    var set = engine.SetLevelAsync(3, true); await Until(() => fake.Sent[^1].Payload.SequenceEqual(new byte[] { 3, 1, 1, 1, 221 }));
    fake.Push([2, 3, 1, 1, 2, 7, 3, 4]); tone.SetResult(new(0x840E, 1, [0])); await set;
    Check(engine.State.Stage == CalibrationStage.Interrupted && !engine.State.ToneAcknowledged && !engine.State.CanResume);
    fake.Push([2, 3, 1, 7, 2, 7, 3, 4]); fake.Hook = null; await engine.ResumeAsync();
    Check(engine.State.StepIndex == 0 && engine.State.Decibels == -35 && engine.State.LevelIndex == 3 && engine.State.ToneAcknowledged);
});
await Test("Noise hints do not interrupt; manual Pause/Resume retains current task", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    fake.Push([8, 2, 8]); Check(engine.State.NoiseWarning && engine.State.Stage == CalibrationStage.AwaitingAnswer);
    fake.Push([8, 2, 9]); Check(!engine.State.NoiseWarning); await engine.PauseHearingAsync();
    Check(engine.State.Stage == CalibrationStage.Interrupted && engine.State.CanResume && !engine.State.ToneAcknowledged);
    await engine.ResumeAsync(); Check(engine.State.ToneAcknowledged && engine.State.StepIndex == 0);
});
await Test("Terminal event8 cleans detector and cannot be reversed by later resume event", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2); fake.Push([8, 2, 7]);
    await Until(() => fake.Sent.Any(x => x.Command == 0x040D && x.Payload.SequenceEqual(new byte[] { 2, 0 })));
    Check(engine.State.Stage == CalibrationStage.Failed && !engine.State.ToneAcknowledged); fake.Push([8, 2, 6]); Check(engine.State.Stage == CalibrationStage.Failed);
});
await Test("Disconnect during pause rejects late ACK without recording an answer", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    fake.Hook = (c, p, t) => { if (c == 0x040E && p[0] == 4) fake.Disconnect(); return Task.FromResult<Packet?>(null); };
    await Throws<OperationCanceledException>(() => engine.ContinueAsync()); Check(engine.State.Stage == CalibrationStage.Failed && engine.State.StepIndex == 0);
});
await Test("Save follows all four writes and all four GETs before local persistence", () => WithHistory(async (store, dir) =>
{
    var fake = new Fake(); await using var engine = Engine(fake, true, store); await Complete(engine, true, Structure.Levels);
    int before = fake.Sent.Count; await engine.SaveHearingAsync("PC-X4-test", true); var calls = fake.Sent.Skip(before).ToArray();
    Check(calls.Select(x => x.Command).SequenceEqual(new ushort[] { 0x0115, 0x0411, 0x040E, 0x0415, 0x0403, 0x0115, 0x011E, 0x0118, 0x010D }));
    Check(calls[1].Payload.Length == 5 && calls[4].Payload.SequenceEqual(new byte[] { 0x0B, 1 }) && calls[^1].Payload.SequenceEqual(new byte[] { 1, 0x0B }));
    var saved = (await store.ReadAsync("oppo/test-device")).Single();
    Check(engine.State.Stage == CalibrationStage.Completed && saved.Levels.SequenceEqual(Structure.Levels) && saved.ScanData!.SequenceEqual(Structure.ScanData)
        && saved.RestoreDescriptionId == engine.State.RestoreDescriptionId && saved.Name == "PC-X4-test" && saved.WireName == saved.Name);
}));
await Test("Hearing save verifies feature0B and persists despite duplicate disabled auto volume entries", () => WithHistory(async (store, dir) =>
{
    var fake = new Fake(); await using var engine = Engine(fake, true, store); await Complete(engine, true, Structure.Levels);
    fake.Hook = (c, p, t) => Task.FromResult<Packet?>(c == 0x010D ? new(0x810D, 1, [0,4,0x1C,0,0x0B,1,0x1C,255,0xFE,7]) : null);
    int before = fake.Sent.Count; await engine.SaveHearingAsync("verified", true);
    var saved = (await store.ReadAsync("oppo/test-device")).Single();
    Check(engine.State.Stage == CalibrationStage.Completed && saved.Name == "verified" && saved.Levels.SequenceEqual(Structure.Levels)
        && saved.ScanData!.SequenceEqual(Structure.ScanData));
    Check(fake.Sent.Skip(before).Last().Payload.SequenceEqual(new byte[] {1,0x0B})
        && fake.Sent.Where(x => x.Command == 0x0403).All(x => x.Payload.SequenceEqual(new byte[] {0x0B,1})));
}));
await Test("Ignoring auto volume cannot authorize missing off duplicate or malformed hearing verification", async () =>
{
    byte[][] replies = [ [0,3,0x1C,0,0x0B,0,0x1C,255], [0,2,0x1C,0,0x1C,255],
        [0,4,0x1C,0,0x0B,1,0x1C,255,0x0B,1], [0,3,0x1C,0,0x0B,1,0x1C] ];
    for (int i = 0; i < replies.Length; i++) await WithHistory(async (store, dir) =>
    {
        var fake = new Fake(); await using var engine = Engine(fake, true, store); await Complete(engine, true);
        fake.Hook = (c, p, t) => Task.FromResult<Packet?>(c == 0x010D ? new(0x810D, 1, replies[i]) : null);
        if (i < 2) await Throws<InvalidDataException>(() => engine.SaveHearingAsync("invalid", true));
        else await Throws<FormatException>(() => engine.SaveHearingAsync("invalid", true));
        Check(engine.State.Stage == CalibrationStage.Failed && (await store.ReadAsync("oppo/test-device")).Count == 0);
    });
});
await Test("Every rejected save write halts chain and does not persist history", async () =>
{
    foreach (ushort rejected in new ushort[] { 0x0411, 0x040E, 0x0415, 0x0403 }) await WithHistory(async (store, dir) =>
    {
        var fake = new Fake(); await using var engine = Engine(fake, true, store); await Complete(engine, true);
        fake.Hook = (c, p, t) => Task.FromResult<Packet?>(c == rejected ? new((ushort)(c | 0x8000), 1, [15]) : null);
        int before = fake.Sent.Count; await Throws<CalibrationDeviceException>(() => engine.SaveHearingAsync("test", true));
        Check(engine.State.Stage == CalibrationStage.Failed && (await store.ReadAsync("oppo/test-device")).Count == 0);
        Check(fake.Sent.Skip(before).Where(x => x.Command >= 0x0400).Last().Command == rejected);
    });
});
await Test("Mismatch in each public readback field prevents completed/local history", async () =>
{
    foreach (ushort corrupted in new ushort[] { 0x0115, 0x011E, 0x0118, 0x010D }) await WithHistory(async (store, dir) =>
    {
        var fake = new Fake(); await using var engine = Engine(fake, true, store); await Complete(engine, true);
        fake.Hook = (c, p, t) => Task.FromResult<Packet?>(c == corrupted && fake.HasSaved ? new((ushort)(c | 0x8000), 1, c switch
        { 0x0115 => [0, .. CalibrationCodecs.Parameters(3, Structure.Levels, 99, "other")], 0x011E => [0, .. CalibrationCodecs.SaveEarScan([9], 99)],
            0x0118 => [0, .. CalibrationCodecs.RestoreDescription(20)], _ => [0, 1, 0x0B, 0] }) : null);
        await Throws<InvalidDataException>(() => engine.SaveHearingAsync("test", true));
        Check(engine.State.Stage == CalibrationStage.Failed && (await store.ReadAsync("oppo/test-device")).Count == 0);
    });
});
await Test("Save preserves exact one-byte name; oversized UTF8 is preflight failure", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await Complete(engine);
    int before = fake.Sent.Count; await Throws<ArgumentException>(() => engine.SaveHearingAsync(new string('中', 14), true));
    Check(fake.Sent.Count == before && engine.State.Stage == CalibrationStage.ResultReady);
    await engine.SaveHearingAsync("A", true); Check(CalibrationCodecs.ParseCurrentHearing([0, .. fake.Hearing!]).Name == "A");
});
await Test("History full/duplicate and other current UID fail preflight preserving results", () => WithHistory(async (store, dir) =>
{
    var fake = new Fake(); await using var engine = Engine(fake, store: store); await Complete(engine);
    for (int i = 0; i < 10; i++) await store.SaveAsync(new("oppo/test-device", i, "old" + i, HearingVersion.V2, Structure.Levels, null, DateTimeOffset.UtcNow.AddSeconds(i)));
    int before = fake.Sent.Count; await Throws<InvalidOperationException>(() => engine.SaveHearingAsync("new", true));
    await Throws<InvalidOperationException>(() => engine.SaveHearingAsync("old1", true, replaceHistoryUid: 0));
    Check(fake.Sent.Count == before && engine.State.Stage == CalibrationStage.ResultReady);
    fake.Hearing = CalibrationCodecs.Parameters(3, Structure.Levels, 100, "other");
    await Throws<InvalidOperationException>(() => engine.SaveHearingAsync("new", false, replaceHistoryUid: 0));
    Check(fake.Sent.Skip(before).All(x => x.Command < 0x0400) && engine.State.Stage == CalibrationStage.ResultReady);
    await engine.SaveHearingAsync("new", true, replaceHistoryUid: 0); var entries = await store.ReadAsync("oppo/test-device");
    Check(entries.Count == 10 && entries.Any(x => x.Name == "new") && entries.All(x => x.Uid != 0));
}));
await Test("Legacy missing description is supplemented and long display name is retained separately", () => WithHistory(async (store, dir) =>
{
    var fake = new Fake(); var old = new CalibrationHistoryEntry("oppo/test-device", -10, new string('中', 40), HearingVersion.V2, Structure.Levels, Structure.ScanData, DateTimeOffset.UtcNow);
    await store.SaveAsync(old); await using var engine = Engine(fake, true, store); await engine.ApplyHistoryAsync(old, true);
    var saved = (await store.ReadAsync("oppo/test-device")).Single();
    Check(saved.RestoreDescriptionId is >= 7 and <= 12 && saved.Name == old.Name && Encoding.UTF8.GetByteCount(saved.WireName!) == 16);
    Check(fake.Sent.Any(x => x.Command == 0x0116) && fake.Sent.Any(x => x.Command == 0x011F) && engine.State.Stage == CalibrationStage.Completed && saved.Uid == -10);
}));
await Test("Old JSON without optional fields and signed/zero UID remains compatible with ten-record history", () => WithHistory(async (store, dir) =>
{
    Directory.CreateDirectory(dir); var entry = new CalibrationHistoryEntry("oppo/test-device", 0, "old", HearingVersion.V2, Structure.Levels, null, DateTimeOffset.UtcNow);
    string path = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.StableKey))) + ".json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { new { entry.StableKey, entry.Uid, entry.Name, entry.Version, entry.Levels, entry.ScanData, entry.SavedAtUtc } }));
    var old = (await store.ReadAsync(entry.StableKey)).Single(); Check(old.RestoreDescriptionId is null && old.WireName is null && old.Uid == 0);
    for (int i = 1; i < 10; i++) await store.SaveAsync(entry with { Uid = -i, Name = "old" + i });
    await Throws<InvalidOperationException>(() => store.SaveAsync(entry with { Uid = 99, Name = "new" }));
    await store.SaveAsync(entry with { Uid = 99, Name = "new" }, replacementUid: 0); Check((await store.ReadAsync(entry.StableKey)).Count == 10);
    await store.DeleteAsync(entry.StableKey, 99); Check((await store.ReadAsync(entry.StableKey)).Count == 9);
}));
await Test("Wrong-device/missing scan history fails before mutation", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake, true);
    var entry = new CalibrationHistoryEntry("other", -5, "old", HearingVersion.V2, Structure.Levels, null, DateTimeOffset.UtcNow, 7);
    await Throws<InvalidOperationException>(() => engine.ApplyHistoryAsync(entry, true));
    await Throws<InvalidDataException>(() => engine.ApplyHistoryAsync(entry with { StableKey = "oppo/test-device" }, true));
    Check(fake.Sent.Count == 0 && engine.State.Stage == CalibrationStage.Idle);
});
await Test("Description reuses exact ordered thresholds and asymmetry uses unequal V2 index spacing", () => WithHistory(async (store, dir) =>
{
    var levels = Enumerable.Range(0, 12).Select(i => new HearingLevel((byte)(i / 6 + 1), (byte)(i % 6 + 1), (sbyte)(i == 0 ? -55 : -19))).ToArray();
    await store.SaveAsync(new("oppo/test-device", 22, "same", HearingVersion.V2, levels, null, DateTimeOffset.UtcNow, 9));
    var fake = new Fake(); await using var engine = Engine(fake, store: store); await Complete(engine, levels: levels);
    Check(engine.State.RestoreDescriptionId == 9 && engine.State.HasInterEarDifference);
}));
await Test("Original/enhanced previews overwrite temporary parameters, stop both effects only on exit, never save NV", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake, true); await Complete(engine, true);
    int before = fake.Sent.Count; await engine.PreviewAsync(CalibrationPreviewMode.Original); var original = fake.Sent.Skip(before).ToArray();
    Check(original.Select(x => x.Command).SequenceEqual(new ushort[] { 0x040E, 0x0415 }));
    Check(original[0].Payload.SequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0 }) && original[1].Payload.SequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0, 0 }));
    before = fake.Sent.Count;
    await engine.PreviewAsync(CalibrationPreviewMode.Enhanced); Check(engine.State.PreviewMode == CalibrationPreviewMode.Enhanced);
    Check(fake.Sent.Skip(before).Select(x => x.Command).SequenceEqual(new ushort[] { 0x040E, 0x0415 })
        && fake.Sent.Skip(before).All(x => x.Payload[0] == 1));
    await engine.PreviewAsync(CalibrationPreviewMode.Stopped); Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.PreviewMode == CalibrationPreviewMode.Stopped);
    Check(fake.Sent.Skip(before).All(x => (x.Command is 0x040E or 0x0415) && (x.Payload[0] is 1 or 2)));
    Check(fake.Sent[^2].Payload.SequenceEqual(new byte[] { 2 }) && fake.Sent[^1].Payload.SequenceEqual(new byte[] { 2 }) && !fake.HasSaved);
});
await Test("Continuous A/B sends operation1 only, waits for hearing ACK before scan and returns to Results on explicit stop", async () =>
{
    foreach (bool scan in new[] { false, true })
    {
        var fake = new Fake(); await using var engine = Engine(fake, scan); await Complete(engine, scan);
        await engine.PreviewAsync(CalibrationPreviewMode.Original);
        var ack = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Hook = (c, p, t) => c == 0x040E && p[0] == 1 ? ack.Task : Task.FromResult<Packet?>(null);
        int before = fake.Sent.Count;
        var enhanced = engine.PreviewAsync(CalibrationPreviewMode.Enhanced);
        await Until(() => fake.Sent.Count > before);
        Check(engine.State.PreviewMode == CalibrationPreviewMode.Original && !enhanced.IsCompleted
            && fake.Sent.Skip(before).Select(x => x.Command).SequenceEqual(new ushort[] { 0x040E }));
        ack.SetResult(new(0x840E, 1, [0])); await enhanced;
        fake.Hook = null;
        foreach (var mode in new[] { CalibrationPreviewMode.Original, CalibrationPreviewMode.Enhanced })
        {
            before = fake.Sent.Count; await engine.PreviewAsync(mode);
            var switchCommands = fake.Sent.Skip(before).ToArray();
            Check(switchCommands.Select(x => x.Command).SequenceEqual(scan ? new ushort[] { 0x040E, 0x0415 } : new ushort[] { 0x040E })
                && switchCommands.All(x => x.Payload[0] == 1) && engine.State.PreviewMode == mode && !fake.HasSaved);
        }
        before = fake.Sent.Count; await engine.PreviewAsync(CalibrationPreviewMode.Stopped);
        Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.PreviewMode == CalibrationPreviewMode.Stopped
            && fake.Sent.Skip(before).All(x => x.Payload.SequenceEqual(new byte[] { 2 })));
    }
});
await Test("Preview cancellation cleans temporary effects and idle stop cannot invent Results", async () =>
{
    var fake = new Fake(); await using var idle = Engine(fake); await idle.PreviewAsync(CalibrationPreviewMode.Stopped); Check(idle.State.Stage == CalibrationStage.Idle && fake.Sent.Count == 0);
    var other = new Fake(); await using var engine = Engine(other, true); await Complete(engine, true); await engine.PreviewAsync(CalibrationPreviewMode.Enhanced);
    int before = other.Sent.Count; await engine.CancelAsync();
    Check(engine.State.Stage == CalibrationStage.Cancelled && other.Sent.Skip(before).Select(x => x.Command).SequenceEqual(new ushort[] { 0x040E, 0x0415 }));
});
await Test("Transport ignoring cancellation still times out, stops owned detector and cannot resurrect on late ACK", async () =>
{
    var fake = new Fake(); var lost = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fake.Hook = (c, p, t) => c == 0x040D && p.SequenceEqual(new byte[] { 2, 1 }) ? lost.Task : Task.FromResult<Packet?>(null);
    await using var engine = Engine(fake, timing: Fast with { CommandTimeout = TimeSpan.FromMilliseconds(30) });
    await Throws<TimeoutException>(() => engine.BeginHearingAsync(HearingVersion.V2));
    Check(engine.State.Stage == CalibrationStage.Failed && fake.Sent.Any(x => x.Command == 0x040D && x.Payload.SequenceEqual(new byte[] { 2, 0 })));
    lost.SetResult(new(0x840D, 1, [0])); Check(engine.State.Stage == CalibrationStage.Failed);
});
await Test("Cancel drains an already submitted START ACK without cancelling the shared wire then stops detector", async () =>
{
    var fake = new Fake(); var start = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken wireToken = default;
    fake.Hook = (c, p, t) => { if (c == 0x040D && p.SequenceEqual(new byte[] { 2, 1 })) { wireToken = t; return start.Task; } return Task.FromResult<Packet?>(null); };
    await using var engine = Engine(fake); var begin = engine.BeginHearingAsync(HearingVersion.V2);
    await Until(() => fake.Sent.Any(x => x.Command == 0x040D)); var cancel = engine.CancelAsync();
    Check(!wireToken.IsCancellationRequested && !cancel.IsCompleted);
    start.SetResult(new(0x840D, 1, [0])); await Throws<OperationCanceledException>(() => begin); await cancel;
    Check(engine.State.Stage == CalibrationStage.Cancelled && fake.Sent.Any(x => x.Command == 0x040D && x.Payload.SequenceEqual(new byte[] { 2, 0 })));
});
await Test("Normal hearing STOP event7 before ACK does not convert completed measurement into failure", async () =>
{
    var fake = new Fake(); fake.Hook = (c, p, t) => { if (c == 0x040D && p.SequenceEqual(new byte[] { 2, 0 })) fake.Push([8, 2, 7]); return Task.FromResult<Packet?>(null); };
    await using var engine = Engine(fake); await Complete(engine); Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.FiltersReady);
});
await Test("Fatal notification keeps nonterminal stage until owned STOP ACK has drained", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    var stop = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fake.Hook = (c, p, t) => c == 0x040D && p.SequenceEqual(new byte[] { 2, 0 }) ? stop.Task : Task.FromResult<Packet?>(null);
    fake.Push([8, 2, 7]); await Until(() => fake.Sent.Any(x => x.Command == 0x040D && x.Payload[1] == 0));
    Check(engine.State.Stage == CalibrationStage.Cancelling);
    stop.SetResult(new(0x840D, 1, [0])); await Until(() => engine.State.Stage == CalibrationStage.Failed);
});
await Test("Idle deadline cleans active tone/detector before failure and paused test is bounded too", async () =>
{
    foreach (bool paused in new[] { false, true })
    {
        var fake = new Fake(); await using var engine = Engine(fake, timing: Fast with { HearingIdleTimeout = TimeSpan.FromMilliseconds(40) });
        await engine.BeginHearingAsync(HearingVersion.V2); if (paused) await engine.PauseHearingAsync();
        await Until(() => engine.State.Stage == CalibrationStage.Failed);
        Check(engine.State.HearingLevels.Count == 0 && !engine.State.ToneAcknowledged && fake.Sent.Any(x => x.Command == 0x040D && x.Payload.SequenceEqual(new byte[] { 2, 0 })));
    }
});
await Test("Valid slider activity resets idle deadline instead of ending at original timer", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake, timing: Fast with { HearingIdleTimeout = TimeSpan.FromMilliseconds(250) });
    await engine.BeginHearingAsync(HearingVersion.V2); await Task.Delay(150); await engine.SetLevelAsync(7, true); await Task.Delay(150);
    Check(engine.State.Stage == CalibrationStage.AwaitingAnswer && engine.State.ToneAcknowledged && engine.State.Decibels == -5);
    await Until(() => engine.State.Stage == CalibrationStage.Failed);
});
await Test("Enco X4 UID0 history remains locally compatible but applying is rejected before every device operation", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake, true);
    var entry = new CalibrationHistoryEntry("oppo/test-device", 0, "old", HearingVersion.V2, Structure.Levels, Structure.ScanData, DateTimeOffset.UtcNow, 7);
    await Throws<InvalidDataException>(() => engine.ApplyHistoryAsync(entry, true)); Check(fake.Sent.Count == 0 && engine.State.Stage == CalibrationStage.Idle);
});
await Test("Resolving wearing interruption cannot dismiss an outstanding audio/call interruption", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2);
    fake.Push([8, 2, 1]); fake.Push([8, 2, 5]); fake.Push([8, 2, 6]);
    Check(engine.State.Stage == CalibrationStage.Interrupted && !engine.State.CanResume);
    await Throws<InvalidOperationException>(() => engine.ResumeAsync()); fake.Push([8, 2, 2]);
    Check(engine.State.CanResume); await engine.ResumeAsync(); Check(engine.State.ToneAcknowledged);
});
await Test("A new interruption during resume's wearing query cannot be cleared by its late ACK", async () =>
{
    var fake = new Fake(); await using var engine = Engine(fake); await engine.BeginHearingAsync(HearingVersion.V2); await engine.PauseHearingAsync();
    fake.Hook = (c, p, t) => { if (c == 0x0109) fake.Push([8, 2, 3]); return Task.FromResult<Packet?>(null); };
    int before = fake.Sent.Count; await engine.ResumeAsync();
    Check(engine.State.Stage == CalibrationStage.Interrupted && !engine.State.CanResume && engine.State.RawStatus == 3
        && fake.Sent.Skip(before).All(x => x.Command != 0x040E || x.Payload[0] != 3));
});
await Test("A previous scan filter status1 while retry's hearing ACK is pending cannot poison new results", async () =>
{
    var fake = new Fake { FilterFactory = (c, p) => Structure.Filter(c, BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(p.Length - 4)), 1) };
    await using var engine = Engine(fake, true); await Throws<TimeoutException>(() => Complete(engine, true));
    var reply = new TaskCompletionSource<Packet?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fake.FilterFactory = null;
    fake.Hook = (c, p, t) => c == 0x0116 ? reply.Task : Task.FromResult<Packet?>(null);
    var retry = engine.ReadCurvesAsync(); await Until(() => engine.State.Stage == CalibrationStage.LoadingCurves);
    fake.PushPacket(0x811F, [1], 1); fake.PushPacket(0x811F, Structure.Filter(0x011F, engine.State.Uid), 1);
    Check(engine.State.Stage == CalibrationStage.LoadingCurves && !retry.IsCompleted);
    reply.SetResult(new(0x8116, 7, Structure.Filter(0x0116, engine.State.Uid)));
    await retry; Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.EarScanCurves.Count == 1);
});
await Test("Fragments before first reply and afterward aggregate only its sequence, including same UID stale errors", async () =>
{
    var fake = new Fake(); fake.Hook = (c, p, t) =>
    {
        if (c is not (0x0116 or 0x011F)) return Task.FromResult<Packet?>(null);
        int uid = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(p.Length - 4)); byte seq = c == 0x0116 ? (byte)7 : (byte)8;
        fake.PushPacket((ushort)(c | 0x8000), [1], 1);
        fake.PushPacket((ushort)(c | 0x8000), Structure.Filter(c, uid, 2), seq);
        return Task.FromResult<Packet?>(new((ushort)(c | 0x8000), seq, Structure.Filter(c, uid, 1)));
    };
    await using var engine = Engine(fake, true); await Complete(engine, true);
    Check(engine.State.Stage == CalibrationStage.ResultReady && engine.State.Curves[0].Right.Count == 30 && engine.State.EarScanCurves[0].Right.Count == 36);
});
await HearingRadarTests.RunAsync(Test);
Console.WriteLine($"{passed} calibration tests passed.");
