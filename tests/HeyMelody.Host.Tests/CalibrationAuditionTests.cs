using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Protocol;

internal static class CalibrationAuditionTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("audition endpoint identity requires one exact selected A2DP ancestor", () =>
        {
            const ulong address = 0x112233445566;
            string selected = Ancestor(address), other = Ancestor(0x223344556677);
            Check(AuditionEndpointAddress.TryParse(selected.ToLowerInvariant(), out var parsed) && parsed == address, "A2DP address was not parsed exactly");
            Check(AuditionEndpointAddress.TryParse(selected + "_C12345678", out parsed) && parsed == address, "Windows connection suffix was not supported");
            foreach (string invalid in new[] { selected.Replace("0000110b", "0000111e"), selected + "_unknown", selected.Replace("112233445566", "1122334455667"), Ancestor(0), "Same friendly name 112233445566" })
                Check(!AuditionEndpointAddress.TryParse(invalid, out _), "An HFP/name/partial/invalid identity was accepted");
            IReadOnlyList<IReadOnlyList<string>> identities = [new[] { other }, new[] { "SWD\\MMDEVAPI\\same-name", selected }];
            Check(EndpointIdentity.SelectIndex(identities, address) == 1, "A different selected address won endpoint selection");
            Throws<EndpointMatchException>(() => EndpointIdentity.SelectIndex([new[] { other }], address));
            Throws<EndpointMatchException>(() => EndpointIdentity.SelectIndex([new[] { selected }, new[] { selected }], address));
            int attempts = 0, waits = 0;
            int resolved = EndpointIdentity.WaitForStable(() => ++attempts < 3 ? throw new EndpointMatchException(2, 0) : 1,
                default, new(), _ => waits++);
            Check(resolved == 1 && attempts == 3 && waits == 2, "Endpoint discovery did not have bounded retries");
            attempts = waits = 0;
            Throws<EndpointMatchException>(() => EndpointIdentity.WaitForStable<int>(() => { attempts++; throw new EndpointMatchException(2, 2); }, default, new(), _ => waits++));
            Check(attempts == 1 && waits == 0, "Ambiguous endpoint identity was retried or guessed");
            return Task.CompletedTask;
        });
        await test("embedded audition WAV has the verified source identity and strict PCM envelope", () =>
        {
            using var stream = typeof(NativeHeadsetService).Assembly.GetManifestResourceStream("HeyMelody.Host.Assets.hearing_enhancement_audition.wav")!;
            using var memory = new MemoryStream(); stream.CopyTo(memory); byte[] embedded = memory.ToArray();
            Check(Convert.ToHexString(SHA256.HashData(embedded)).Equals("F9896F960E09F3A69777F41C2421632C447889750C506E584D16E42557FC9833", StringComparison.Ordinal), "Embedded audio identity changed");
            Check(AuditionWave.Read(new MemoryStream(embedded)).Frames == 1107174, "Embedded clip duration/format changed");
            byte[] valid = Wave(); var wave = AuditionWave.Read(new MemoryStream(valid));
            Check(wave.Frames == 1 && wave.Samples.SequenceEqual(new short[] { 123, -321 }), "PCM samples were decoded incorrectly");
            var invalid = new List<byte[]> { valid[..^1] };
            byte[] wrongRate = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(wrongRate.AsSpan(24), 44100); invalid.Add(wrongRate);
            byte[] oversized = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(40), uint.MaxValue); invalid.Add(oversized);
            byte[] trailing = [..valid, 0]; BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(4), (uint)trailing.Length - 8); invalid.Add(trailing);
            foreach (byte[] bytes in invalid) Throws<InvalidDataException>(() => AuditionWave.Read(new MemoryStream(bytes)));
            return Task.CompletedTask;
        });
        await test("fatal audition render initialization drains commands and rejects future work without hanging", async () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            await using var player = new WasapiCalibrationAuditionPlayer(() => { entered.Set(); release.Wait(); throw new IOException("Synthetic COM initialization failure"); }, () => { });
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "Synthetic render thread never started");
            Task queued = player.PrepareAsync(1, default); release.Set();
            await ThrowsAsync<IOException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await ThrowsAsync<ObjectDisposedException>(() => player.PrepareAsync(1, default).WaitAsync(TimeSpan.FromSeconds(2)));
            await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        });
        await test("first audition waits for DSP ACK while A/B keeps the stream and pause preserves the cursor", async () =>
        {
            await using var fixture = new Fixture(scratch);
            var ack = NewSignal(); var request = NewSignal();
            fixture.Transport.OnRequest = async (_, payload, token) => { if (payload[0] == 1) { request.TrySetResult(); await ack.Task.WaitAsync(token); } return [0]; };
            Task<UiCalibrationState> start = fixture.Advance("preview-enhanced");
            await request.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Player.PreparedAddress == 0x112233445566 && fixture.Player.Plays == 0 && !fixture.Player.Playing,
                "Local audio started before exact endpoint preparation and DSP acknowledgment");
            ack.SetResult(); var enhanced = await start;
            Check(enhanced.PreviewAudioPlaying && enhanced.PreviewMode == "Enhanced", "Acknowledged enhanced audition did not start");
            fixture.Player.Cursor = 1234;
            int pauses = fixture.Player.Pauses, plays = fixture.Player.Plays, stops = fixture.Player.Stops;
            var original = await fixture.Advance("preview-original");
            Check(original.PreviewAudioPlaying && original.PreviewMode == "Original" && original.PreviewEpoch > enhanced.PreviewEpoch
                && fixture.Player.Pauses == pauses && fixture.Player.Plays == plays && fixture.Player.Stops == stops && fixture.Player.Cursor == 1234,
                "A/B interrupted or restarted the existing stream");
            var frames = new List<UiCalibrationSpectrum>(); fixture.Service.CalibrationSpectrumChanged += frames.Add;
            long originalRevision = original.Revision;
            fixture.Player.EmitSpectrum();
            Check(frames.Count == 1 && frames[0].PreviewEpoch == original.PreviewEpoch && fixture.State.Revision == originalRevision,
                "Original audition lost its music spectrum or advanced the control revision");
            enhanced = await fixture.Advance("preview-enhanced");
            long revision = enhanced.Revision;
            fixture.Player.EmitSpectrum(); fixture.Player.EmitSpectrum();
            Check(frames.Count == 3 && frames[2].Sequence > frames[1].Sequence && frames.Skip(1).All(x => x.PreviewEpoch == enhanced.PreviewEpoch)
                && fixture.State.Revision == revision, "Spectrum advanced control revision or lost its epoch/sequence");
            await fixture.Advance("preview-stop");
            Check(!fixture.Player.Playing && fixture.Player.Pauses == pauses + 1 && fixture.Player.Cursor == 1234, "Pause lost the music cursor");
            fixture.Player.EmitSpectrum(); Check(frames.Count == 3, "Paused playback emitted a spectrum");
            await fixture.Advance("preview-original");
            Check(fixture.Player.Playing && fixture.Player.Cursor == 1234, "Resuming a paused audition restarted the clip");
        });
        await test("DSP rejection and local start failure stop audition and clear enhanced playback", async () =>
        {
            foreach (bool rejectDsp in new[] { true, false })
            {
                await using var fixture = new Fixture(scratch);
                if (rejectDsp) fixture.Transport.OnRequest = (_, payload, _) => Task.FromResult<byte[]>(payload[0] == 1 ? [1] : [0]);
                else fixture.Player.ThrowPlay = true;
                await ThrowsAsync<InvalidOperationException>(() => fixture.Advance("preview-enhanced"));
                Check(!fixture.Player.Playing && !fixture.State.PreviewAudioPlaying && fixture.Player.Stops > 0,
                    "An unacknowledged or failed stream kept playing");
                Check(fixture.Transport.Payloads.Any(x => x[0] == 2) && fixture.Engine.State.PreviewMode == CalibrationPreviewMode.Stopped,
                    "Failed audition left temporary DSP enabled");
            }
        });
        await test("failed A/B silences the existing stream rather than continuing an unknown effect", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await fixture.Advance("preview-enhanced");
            fixture.Transport.OnRequest = (_, payload, _) => Task.FromResult<byte[]>(payload[0] == 1 ? [1] : [0]);
            await ThrowsAsync<InvalidOperationException>(() => fixture.Advance("preview-original"));
            Check(!fixture.Player.Playing && !fixture.State.PreviewAudioPlaying && fixture.Player.Stops > 0,
                "Failed A/B kept audio playing with an unconfirmed effect");
        });
        await test("rejected A/B silences local music before a blocked device STOP acknowledgment", async () =>
        {
            await using var fixture = new Fixture(scratch); await fixture.Advance("preview-enhanced");
            var stopping = NewSignal(); var ack = NewSignal();
            fixture.Transport.OnRequest = async (_, payload, token) =>
            {
                if (payload[0] == 1) return [1];
                stopping.TrySetResult(); await ack.Task.WaitAsync(token); return [0];
            };
            Task<UiCalibrationState> change = fixture.Advance("preview-original");
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!change.IsCompleted && !fixture.Player.Playing && !fixture.State.PreviewAudioPlaying && fixture.State.Stage == "Cancelling",
                "Unknown DSP kept local music playing while device STOP was awaiting its ACK");
            ack.SetResult(); await ThrowsAsync<InvalidOperationException>(() => change);
        });
        await test("a failed local pause still stops the private stream and temporary device DSP", async () =>
        {
            await using var fixture = new Fixture(scratch); await fixture.Advance("preview-enhanced"); fixture.Player.ThrowPause = true;
            await ThrowsAsync<InvalidOperationException>(() => fixture.Advance("preview-stop"));
            Check(!fixture.Player.Playing && !fixture.State.PreviewAudioPlaying && fixture.Engine.State.PreviewMode == CalibrationPreviewMode.Stopped
                && fixture.Transport.Payloads.Any(x => x[0] == 2), "A failed local pause skipped silence or temporary DSP cleanup");
        });
        await test("audio failure during a pending A/B ACK cannot be revived by the acknowledged DSP mode", async () =>
        {
            await using var fixture = new Fixture(scratch); await fixture.Advance("preview-enhanced");
            var submitted = NewSignal(); var ack = NewSignal(); int failureRaised = 0, revived = 0;
            fixture.Service.CalibrationChanged += state => { if (Volatile.Read(ref failureRaised) != 0 && state.PreviewAudioPlaying) Interlocked.Increment(ref revived); };
            fixture.Transport.OnRequest = async (_, payload, token) =>
            {
                if (payload[0] == 1) { submitted.TrySetResult(); await ack.Task.WaitAsync(token); }
                return [0];
            };
            Task<UiCalibrationState> change = fixture.Advance("preview-original");
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Volatile.Write(ref failureRaised, 1); fixture.Player.EmitFailure();
            Check(!fixture.Player.Playing && !fixture.State.PreviewAudioPlaying && fixture.State.PreviewAudioError.Length > 0,
                "The current transition ignored its render failure while retaining the old displayed epoch");
            ack.SetResult(); await ThrowsAsync<InvalidOperationException>(() => change);
            Check(!fixture.Player.Playing && !fixture.State.PreviewAudioPlaying && fixture.State.PreviewAudioError.Length > 0
                && fixture.Player.Plays == 1 && fixture.Player.Disposals > 0 && revived == 0,
                "DSP ACK revived a failed stream or published a false playing state");
        });
        await test("failed preparation discards a terminated player so the next audition can initialize a fresh stream", async () =>
        {
            var terminated = new FakePlayer { ThrowPrepareDisposed = true }; var healthy = new FakePlayer(); int factories = 0;
            await using var fixture = new Fixture(scratch, terminated, () => ++factories == 1 ? terminated : healthy);
            await ThrowsAsync<InvalidOperationException>(() => fixture.Advance("preview-enhanced"));
            Check(terminated.Disposals > 0 && !terminated.Playing && !fixture.State.PreviewAudioPlaying, "Terminated player was retained after its successful Stop");
            var ready = await fixture.Advance("preview-original");
            Check(factories == 2 && ready.PreviewAudioPlaying && healthy.Playing && healthy.PreparedAddress == 0x112233445566,
                "Retry reused the disposed player instead of creating a usable stream");
        });
        await test("cancel and disconnect cancel delayed preparation before any DSP or music starts", async () =>
        {
            foreach (bool disconnect in new[] { false, true })
            {
                await using var fixture = new Fixture(scratch); var preparing = NewSignal();
                fixture.Player.OnPrepare = async token => { preparing.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
                Task<UiCalibrationState> start = fixture.Advance("preview-enhanced");
                await preparing.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Task cleanup = disconnect ? fixture.Service.DisconnectAsync(default) : fixture.Service.CancelCalibrationAsync(fixture.Handle, default);
                await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                await ThrowsAsync<OperationCanceledException>(() => start);
                Check(fixture.Player.Plays == 0 && fixture.Transport.Payloads.Count == 0 && !fixture.Player.Playing,
                    "Cancelled endpoint preparation started DSP or audio");
                if (disconnect) Check(fixture.Player.Disposals > 0, "Disconnect did not release its private stream");
            }
        });
        await test("a newer preview survives cleanup of a cancelled old preparation", async () =>
        {
            await using var fixture = new Fixture(scratch); var preparing = NewSignal(); int calls = 0;
            fixture.Player.OnPrepare = async token => { if (++calls == 1) { preparing.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
            Task<UiCalibrationState> old = fixture.Advance("preview-enhanced");
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task<UiCalibrationState> newer = fixture.Advance("preview-original");
            await ThrowsAsync<OperationCanceledException>(() => old);
            var final = await newer.WaitAsync(TimeSpan.FromSeconds(2));
            Check(final.PreviewAudioPlaying && final.PreviewMode == "Original" && final.PreviewAudioError == "" && fixture.Player.Playing,
                "Old prepare cleanup stopped or overwrote the newer preview");
        });
        await test("a new preview cannot be stopped or overwritten by an awaited old failure callback", async () =>
        {
            await using var fixture = new Fixture(scratch);
            await fixture.Advance("preview-enhanced");
            var stopping = NewSignal(); var release = NewSignal(); bool blockOnce = true;
            fixture.Player.OnStop = async () => { if (blockOnce) { blockOnce = false; stopping.TrySetResult(); await release.Task; } };
            fixture.Player.EmitFailure(); await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task<UiCalibrationState> newer = fixture.Advance("preview-original"); release.SetResult();
            var final = await newer.WaitAsync(TimeSpan.FromSeconds(2));
            Check(final.PreviewAudioPlaying && final.PreviewMode == "Original" && final.PreviewAudioError == "" && fixture.Player.Playing,
                "An old failure callback stopped or published into the newer audition epoch");
        });
        await test("save preflight, cancellation, transport loss and failing local cleanup all silence audition", async () =>
        {
            foreach (string end in new[] { "save", "cancel", "transport", "cleanup-failure" })
            {
                await using var fixture = new Fixture(scratch); await fixture.Advance("preview-enhanced");
                if (end == "save") await ThrowsAsync<ArgumentException>(() => fixture.Advance("save"));
                else if (end == "transport") { fixture.Transport.Disconnect(); await Until(() => !fixture.Player.Playing); }
                else
                {
                    if (end == "cleanup-failure") fixture.Player.ThrowStop = fixture.Player.ThrowDispose = true;
                    await fixture.Service.CancelCalibrationAsync(fixture.Handle, default).WaitAsync(TimeSpan.FromSeconds(2));
                }
                Check(!fixture.Player.Playing && !fixture.State.PreviewAudioPlaying, "Ending the workflow left private audition playing");
                if (end == "cleanup-failure") Check(fixture.Transport.Payloads.Any(x => x[0] == 2), "Local dispose failure skipped device DSP cleanup");
            }
        });
    }

    private static string Ancestor(ulong address) => @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\8&1234&0&" + address.ToString("X12");
    private static byte[] Wave()
    {
        byte[] bytes = new byte[48]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 40);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48000); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 192000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 4); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 4);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44), 123); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(46), -321); return bytes;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> condition) { using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2)); while (!condition()) await Task.Delay(5, budget.Token); }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Handle = Guid.NewGuid().ToString("N");
        internal readonly FakePlayer Player;
        internal readonly Transport Transport = new();
        internal readonly CalibrationEngine Engine;
        internal readonly NativeHeadsetService Service;
        internal UiCalibrationState State => Service.GetCalibration(Handle);
        internal Fixture(string scratch, FakePlayer? player = null, Func<ICalibrationAuditionPlayer>? auditionFactory = null)
        {
            Player = player ?? new();
            var context = new CalibrationDeviceContext { StableKey = "memory-audition", Generation = 1, HearingV2 = true, Commands = new HashSet<ushort> { 0x040E }, Events = new HashSet<byte>() };
            Engine = new(Transport, context);
            Field(typeof(CalibrationEngine), "_state").SetValue(Engine, new CalibrationSnapshot { Generation = 1, Workflow = CalibrationWorkflow.HearingV2,
                Stage = CalibrationStage.ResultReady, StepIndex = 12, FiltersReady = true, CanPreview = true, Uid = 123,
                HearingLevels = Enumerable.Range(1, 2).SelectMany(side => Enumerable.Range(1, 6).Select(type => new HearingLevel((byte)side, (byte)type, 0))).ToArray() });
            Service = new(() => throw new InvalidOperationException("Audition fixture enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, Handle + ".json")), auditionFactory ?? (() => Player));
            Field(typeof(NativeHeadsetService), "_calibration").SetValue(Service, Engine);
            Field(typeof(NativeHeadsetService), "_calibrationContext").SetValue(Service, context);
            Field(typeof(NativeHeadsetService), "_selectedAddress").SetValue(Service, (ulong?)0x112233445566);
            Field(typeof(NativeHeadsetService), "_calibrationState").SetValue(Service, new UiCalibrationState { Handle = Handle, Generation = 1, Workflow = "hearing", Stage = "Results", CanPreview = true, FiltersReady = true });
            Field(typeof(NativeHeadsetService), "_hearingHistory").SetValue(Service, new CalibrationHistoryStore(Path.Combine(scratch, Handle + "-history")));
            Engine.Changed += (Action<CalibrationSnapshot>)typeof(NativeHeadsetService).GetMethod("EngineChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Action<CalibrationSnapshot>), Service);
        }
        internal Task<UiCalibrationState> Advance(string action) => Service.AdvanceCalibrationAsync(Handle, State.Revision, new(action), default);
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
    private sealed class FakePlayer : ICalibrationAuditionPlayer
    {
        public event Action<IReadOnlyList<double>>? Spectrum;
        public event Action? Failed;
        internal int Pauses, Plays, Stops, Disposals, Cursor;
        internal ulong PreparedAddress;
        internal bool Playing, ThrowPlay, ThrowPause, ThrowStop, ThrowDispose, ThrowPrepareDisposed;
        internal Func<CancellationToken, Task>? OnPrepare;
        internal Func<Task>? OnStop;
        public async Task PrepareAsync(ulong address, CancellationToken token) { PreparedAddress = address; if (ThrowPrepareDisposed) throw new ObjectDisposedException("Synthetic terminated render thread"); if (OnPrepare is { } prepare) await prepare(token); }
        public Task PauseAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Pauses++; if (ThrowPause) throw new IOException("Synthetic WASAPI pause error"); Playing = false; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Plays++; if (ThrowPlay) throw new IOException("Synthetic WASAPI start error"); Playing = true; return Task.CompletedTask; }
        public async Task StopAsync() { Stops++; Playing = false; Cursor = 0; if (OnStop is { } stop) await stop(); if (ThrowStop) throw new IOException("Synthetic WASAPI stop error"); }
        public ValueTask DisposeAsync() { Disposals++; Playing = false; if (ThrowDispose) throw new IOException("Synthetic WASAPI dispose error"); return ValueTask.CompletedTask; }
        internal void EmitSpectrum() => Spectrum?.Invoke([.1,.2,.3,.4,.5,.6]);
        internal void EmitFailure() => Failed?.Invoke();
    }
    private sealed class Transport : ICalibrationTransport
    {
        public bool IsConnected { get; private set; } = true;
        public event Action<Packet>? Notification { add { } remove { } }
        public event Action? Disconnected;
        internal List<byte[]> Payloads { get; } = [];
        internal Func<ushort, byte[], CancellationToken, Task<byte[]>>? OnRequest;
        public async Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
        { Payloads.Add(payload.ToArray()); return new((ushort)(command | 0x8000), 0, OnRequest is { } request ? await request(command, payload, token) : [0]); }
        internal void Disconnect() { IsConnected = false; Disconnected?.Invoke(); }
    }
}
