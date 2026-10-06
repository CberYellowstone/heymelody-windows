using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

if (args.Length == 3 && args[0] == "--fake-worker") return await FakeWorker(args[1], args[2]);

int passed = 0;
string scratch = Path.Combine(Path.GetTempPath(), "HeyMelody-ClientTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    await Test("RPC rejects an incomplete EOF record", async () =>
        await Throws<InvalidDataException>(() => RpcWire.ReadLineAsync(new StringReader("{}"), default)));
    await Test("RPC bounds records before deserialization", async () =>
        await Throws<InvalidDataException>(() => RpcWire.ReadLineAsync(new StringReader(new string('a', RpcWire.MaximumLineCharacters + 1) + "\n"), default)));
    await Test("RPC accepts its exact maximum", async () =>
        Check((await RpcWire.ReadLineAsync(new StringReader(new string('a', RpcWire.MaximumLineCharacters) + "\n"), default))?.Length == RpcWire.MaximumLineCharacters));
    await Test("isolated worker enumerates and reads without applying", async () =>
    {
        string counter = Counter("read");
        await using var bridge = Bridge("normal", counter);
        var devices = await bridge.EnumerateAsync(default);
        Check(devices.Count == 1 && devices[0].Identity == "opaque-peer");
        var snapshot = await bridge.ConnectAsync(devices[0].Identity, default);
        Check(snapshot.Connected && snapshot.HiRes == true);
        await bridge.RefreshAsync(default); await bridge.DisconnectAsync(default);
        Check(!File.Exists(counter));
    });
    foreach (string mode in new[] { "eof", "wrong-id", "error", "malformed" })
        await Test("lost " + mode + " receipt is not replayed", async () =>
        {
            string counter = Counter(mode);
            await using var bridge = Bridge(mode, counter);
            var result = await bridge.ApplyAsync(new UiAction("GameMode", 0), default);
            Check(result.SubmissionAttempted && result.AcknowledgementUnknown && result.PartialEffects && result.After is null);
            Check(File.ReadAllLines(counter).Length == 1);
        });
    await Test("worker timeout terminates an unresponsive process without replay", async () =>
    {
        string counter = Counter("hang");
        await using var bridge = Bridge("hang", counter, TimeSpan.FromSeconds(2));
        var timer = Stopwatch.StartNew();
        var result = await bridge.ApplyAsync(new UiAction("Spatial", 0), default);
        Check(timer.Elapsed < TimeSpan.FromSeconds(8));
        Check(result.SubmissionAttempted && result.AcknowledgementUnknown);
        Check(File.ReadAllLines(counter).Length == 1);
    });
    await Test("unknown peer ACK with verified readback survives the process boundary", async () =>
    {
        string counter = Counter("unknown-ack");
        await using var bridge = Bridge("unknown-ack", counter);
        var result = await bridge.ApplyAsync(new UiAction("GameMode", 0), default);
        Check(!result.Accepted && result.ReadbackMatches == true && result.AcknowledgementUnknown && result.After?.GameMode == false);
        Check(File.ReadAllLines(counter).Length == 1);
    });
    await VmTests.RunAsync(Test);
    Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), nativeBluetoothCalls = 0 }, RpcWire.Json));
    return 0;
}
finally
{
    // The only recursive target is this exact GUID test directory created above.
    if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(scratch, recursive: true);
}

string Counter(string name) => Path.Combine(scratch, name + ".txt");
ClientBridge Bridge(string mode, string counter, TimeSpan? budget = null) => new(() =>
{
    string host = Environment.ProcessPath ?? throw new InvalidOperationException();
    var start = new ProcessStartInfo(host)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
    };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--fake-worker"); start.ArgumentList.Add(mode); start.ArgumentList.Add(counter); return start;
}, budget);
async Task Test(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static async Task<int> FakeWorker(string mode, string counter)
{
    Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonSerializer.Deserialize<RpcRequest>(line, RpcWire.Json)!;
        if (request.Operation == "apply")
        {
            await File.AppendAllTextAsync(counter, "apply\n");
            if (mode == "eof") return 0;
            if (mode == "hang") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
            if (mode == "malformed") { Console.WriteLine("{}"); continue; }
        }
        object data = request.Operation switch
        {
            "enumerate" => new List<UiDevice> { new("opaque-peer", "OPPO Enco X4", true) },
            "connect" or "refresh" => new UiSnapshot { Connected = true, SupportedModel = true, HiRes = true },
            "apply" => new UiApplyResult { SubmissionAttempted = true, AcknowledgementUnknown = true, ReadbackMatches = true, After = new UiSnapshot { GameMode = false } },
            _ => true
        };
        var response = new RpcResponse
        {
            Id = request.Id + (mode == "wrong-id" ? 1 : 0), Success = mode != "error",
            Data = JsonSerializer.SerializeToElement(data, RpcWire.Json)
        };
        Console.WriteLine(JsonSerializer.Serialize(response, RpcWire.Json)); await Console.Out.FlushAsync();
    }
    return 0;
}
