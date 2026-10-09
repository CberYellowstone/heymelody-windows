using System.Buffers.Binary;
using System.Text.Json;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;

static class TestKit
{
    public static readonly Fixture Structure = Fixture.Load();
    public static readonly CalibrationTimings Fast = new() { FitTimeout = TimeSpan.FromMilliseconds(150), EarScanTimeout = TimeSpan.FromMilliseconds(150),
        FilterTimeout = TimeSpan.FromMilliseconds(200), CommandTimeout = TimeSpan.FromMilliseconds(500), ScanSettle = TimeSpan.Zero,
        StartToneDelay = TimeSpan.Zero, StepPauseDelay = TimeSpan.Zero, LevelThrottle = TimeSpan.FromMilliseconds(40) };
    public static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    public static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    public static async Task Until(Func<bool> condition)
    { for (int i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(2); } throw new TimeoutException("Test condition not reached."); }
    public static CalibrationDeviceContext Context(bool scan = false) => new()
    {
        StableKey = "oppo/test-device", Generation = 7, Fit = true, HearingV1 = true, HearingV2 = true, EarScan = scan, ProductId = "06EC10", HearingJudgeDb = 1,
        Commands = new HashSet<ushort> { 0x0109, 0x010D, 0x0403, 0x0405, 0x040D, 0x040E, 0x0411, 0x0415, 0x0115, 0x0116, 0x0118, 0x011E, 0x011F },
        Events = new HashSet<byte>()
    };
    public static CalibrationEngine Engine(Fake fake, bool scan = false, CalibrationHistoryStore? store = null, CalibrationTimings? timing = null)
        => new(fake, Context(scan), store, timing ?? Fast);
    public static async Task Complete(CalibrationEngine engine, bool scan = false, IReadOnlyList<HearingLevel>? levels = null)
    {
        if (scan) await engine.StartEarScanAsync();
        await engine.BeginHearingAsync(HearingVersion.V2);
        for (int i = 0; i < 12; i++)
        {
            int index = CalibrationEngine.V2Decibels.ToList().IndexOf(levels?[i].Decibels ?? -19); Check(index >= 0);
            if (engine.State.LevelIndex != index) await engine.SetLevelAsync(index, true);
            await engine.ContinueAsync();
        }
    }
    public static async Task WithHistory(Func<CalibrationHistoryStore, string, Task> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "HeyMelody.Calibration.Tests-" + Guid.NewGuid().ToString("N"));
        try { await action(new(dir), dir); }
        finally
        {
            string full = Path.GetFullPath(dir), relative = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), full);
            Check(!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal)
                && Path.GetFileName(full).StartsWith("HeyMelody.Calibration.Tests-", StringComparison.Ordinal));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
sealed record SentCall(ushort Command, byte[] Payload, DateTimeOffset At);
sealed class Fake : ICalibrationTransport
{
    public bool IsConnected { get; private set; } = true;
    public event Action<Packet>? Notification;
    public event Action? Disconnected;
    public bool Wearing { get; set; } = true;
    public bool FitEvent { get; set; } = true;
    public bool ScanEvent { get; set; } = true;
    private readonly List<SentCall> _sent = [];
    public List<SentCall> Sent { get { lock (_sent) return _sent.ToList(); } }
    public Func<ushort, byte[], CancellationToken, Task<Packet?>>? Hook { get; set; }
    public Func<ushort, byte[], byte[]>? FilterFactory { get; set; }
    public byte[]? Hearing { get; set; }
    public byte[]? Scan { get; set; }
    public byte Description { get; set; } = 7;
    public byte Enabled { get; set; }
    public bool HasSaved { get; private set; }
    public async Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); lock (_sent) _sent.Add(new(command, payload.ToArray(), DateTimeOffset.UtcNow));
        Packet? reply = Hook is null ? null : await Hook(command, payload, token);
        if (reply is not null) return reply;
        token.ThrowIfCancellationRequested();
        var f = TestKit.Structure;
        if (command == 0x0405 && payload[0] == 1 && FitEvent) Push(f.FitEvent);
        if (command == 0x040D && payload[0] == 4 && payload[1] == 1 && ScanEvent)
            Push([14, .. CalibrationCodecs.SaveEarScan(f.ScanData, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(2)))]);
        if (command == 0x0411) Description = payload[^1];
        if (command == 0x040E && payload[0] == 3 && payload.Length > 5) { Hearing = payload.ToArray(); HasSaved = true; }
        if (command == 0x0415 && payload[0] == 3) Scan = payload.ToArray();
        if (command == 0x0403 && payload[0] == 0x0B) Enabled = payload[1];
        byte[] data = command switch
        {
            0x0109 => Wearing ? f.Wear : [0, 3, 1, 1, 2, 7, 3, 4],
            0x0115 => Hearing is null ? [1] : [0, .. Hearing], 0x011E => Scan is null ? [1] : [0, .. Scan],
            0x0118 => [0, .. CalibrationCodecs.RestoreDescription(Description)], 0x010D => [0, 1, 0x0B, Enabled],
            0x0116 or 0x011F => FilterFactory?.Invoke(command, payload) ?? f.Filter(command, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(payload.Length - 4))), _ => [0]
        };
        return new((ushort)(command | 0x8000), 1, data);
    }
    public void Push(byte[] payload) => PushPacket(0x0204, payload);
    public void PushPacket(ushort command, byte[] payload, byte sequence = 1) => Notification?.Invoke(new(command, sequence, payload));
    public void Disconnect() { IsConnected = false; Disconnected?.Invoke(); }
}
sealed class Fixture
{
    public required int Uid { get; init; }
    public required byte[] Hearing { get; init; }
    public required byte[] ScanFilter { get; init; }
    public required byte[] ScanData { get; init; }
    public required byte[] Wear { get; init; }
    public required byte[] FitEvent { get; init; }
    public required byte[] FilterRequest { get; init; }
    public required byte[] ScanFilterRequest { get; init; }
    public required HearingLevel[] Levels { get; init; }
    public static Fixture Load()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "enco-x4-v116-structure.json")));
        var r = json.RootElement; byte[] Bytes(string key) => Convert.FromHexString(r.GetProperty(key).GetString()!);
        return new() { Uid = r.GetProperty("uid").GetInt32(), Hearing = Bytes("hearingFilterPayload"), ScanFilter = Bytes("earScanFilterPayload"),
            ScanData = Bytes("scanData"), Wear = Bytes("wearPayload"), FitEvent = Bytes("fitEventPayload"), FilterRequest = Bytes("hearingFilterRequest"),
            ScanFilterRequest = Bytes("earScanFilterRequest"), Levels = r.GetProperty("levels").EnumerateArray().Select(x => new HearingLevel(x.GetProperty("side").GetByte(), x.GetProperty("type").GetByte(), x.GetProperty("decibels").GetSByte())).ToArray() };
    }
    public byte[] Filter(ushort command, int uid, byte device = 4)
    {
        byte[] all = (command == 0x0116 ? Hearing : ScanFilter).ToArray(); BinaryPrimitives.WriteInt32BigEndian(all.AsSpan(1), uid);
        if (device == 4) return all;
        int header = command == 0x0116 ? 10 : 11, half = (all.Length - header) / 2;
        var result = new byte[header + half]; all.AsSpan(0, header).CopyTo(result); result[5] = device;
        all.AsSpan(header + (device == 2 ? half : 0), half).CopyTo(result.AsSpan(header)); return result;
    }
}
