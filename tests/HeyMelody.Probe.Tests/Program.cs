using System.Text.Json;
using HeyMelodyWindows.Probe;

namespace HeyMelodyWindows.Probe.Tests;

internal static class TestRunner
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            Check("fixed GET allowlist", () =>
            {
                foreach (var id in new ushort[] { 0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0105, 0x0114 })
                {
                    Require(ReadOnlySession.IsAllowed(id, []));
                    Require(!ReadOnlySession.IsAllowed(id, [1]));
                }
                Require(ReadOnlySession.IsAllowed(0x010D, [1, 0x18]));
                Require(!ReadOnlySession.IsAllowed(0x010D, []));
                Require(!ReadOnlySession.IsAllowed(0x010D, [1, 0x11]));
                Require(!ReadOnlySession.IsAllowed(0x010D, [2, 0x18, 0x11]));
                foreach (var id in new ushort[] { 0x0403, 0x0419, 0x0201, 0x0205, 0x0300, 0x0302, 0x0500, 0x0422, 0xFFFF })
                    Require(!ReadOnlySession.IsAllowed(id, []));
            });

            await CheckAsync("wrong command cannot submit", async () =>
            {
                await using var transport = new FakeTransport([]);
                var session = new ReadOnlySession(transport);
                var evidence = new ReadOnlyEvidence();
                try { await session.QueryAsync(0x0403, new byte[] { 0x18, 1 }, evidence, CancellationToken.None); throw new Exception(); }
                catch (InvalidOperationException) { }
                Require(transport.Sent.Count == 0 && evidence.SentQueries.Count == 0);
            });

            await CheckAsync("split response and exact TX fixture", async () =>
            {
                // Independently written on-wire fixture: Product ID status0 + LE24 06EC10.
                var fixture = ProductReply(0);
                await using var transport = new FakeTransport([fixture[..1], fixture[1..6], fixture[6..]]);
                var evidence = new ReadOnlyEvidence();
                var reply = await new ReadOnlySession(transport).QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(reply.Success && reply.ResponseMatched && (string)reply.Data["productId"]! == "06EC10");
                Require(transport.Sent.Single().SequenceEqual(Convert.FromHexString("AA0700000301000000")));
            });

            await CheckAsync("same command wrong sequence ignored", async () =>
            {
                var combined = ProductReply(7).Concat(ProductReply(0)).ToArray();
                await using var transport = new FakeTransport([combined]);
                var evidence = new ReadOnlyEvidence();
                var reply = await new ReadOnlySession(transport).QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(reply.Success && evidence.IgnoredPackets == 1);
            });

            await CheckAsync("request without response bit ignored", async () =>
            {
                var request = ProductReply(0); request[5] = 1;
                await using var transport = new FakeTransport([request, ProductReply(0)]);
                var evidence = new ReadOnlyEvidence();
                var reply = await new ReadOnlySession(transport).QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(reply.Success && evidence.IgnoredPackets == 1);
            });

            await CheckAsync("identity mismatch stops before capabilities", async () =>
            {
                var oldModel = ProductReply(0); oldModel[10] = 0x10; oldModel[11] = 0x14; oldModel[12] = 0x06;
                await using var transport = new FakeTransport([oldModel]);
                var result = new ProbeResult();
                await new ReadOnlySession(transport).RunQueriesAsync(result, CancellationToken.None);
                Require(!result.Target.IdentityVerified && !result.Success && transport.Sent.Count == 1);
                Require(result.Queries.Single().RequestCommand == "0x0103");
            });

            await CheckAsync("empty ACK is not identity success", async () =>
            {
                await using var transport = new FakeTransport([Convert.FromHexString("AA0700000381000000")]);
                var result = new ProbeResult();
                await new ReadOnlySession(transport).RunQueriesAsync(result, CancellationToken.None);
                Require(!result.Success && !result.Target.IdentityVerified && transport.Sent.Count == 1);
            });

            await CheckAsync("malformed successful payload stops subsequent sends", async () =>
            {
                await using var transport = new FakeTransport([Reply(0x0103, 0, [0, 0x10])]);
                var session = new ReadOnlySession(transport);
                var evidence = new ReadOnlyEvidence();
                var first = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                var next = await session.QueryAsync(0x0100, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(first.ResponseMatched && !first.Success && first.Status == 0 && !next.Submitted && transport.Sent.Count == 1);
            });

            await CheckAsync("empty ACK stops subsequent sends", async () =>
            {
                await using var transport = new FakeTransport([Reply(0x0101, 0, [])]);
                var session = new ReadOnlySession(transport);
                var evidence = new ReadOnlyEvidence();
                var first = await session.QueryAsync(0x0101, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                var next = await session.QueryAsync(0x0102, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(first.ResponseMatched && !first.Success && first.Status is null && !next.Submitted && transport.Sent.Count == 1);
            });

            await CheckAsync("nonzero device status never treated as success", async () =>
            {
                await using var transport = new FakeTransport([Reply(0x0103, 0, [6])]);
                var evidence = new ReadOnlyEvidence();
                var result = await new ReadOnlySession(transport).QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(result.ResponseMatched && !result.Success && result.Status == 6 && result.Data.Count == 0);
            });

            await CheckAsync("late sequence from previous query ignored", async () =>
            {
                await using var transport = new FakeTransport([ProductReply(0), ProductReply(0), ProductReply(1)]);
                var evidence = new ReadOnlyEvidence();
                var session = new ReadOnlySession(transport);
                Require((await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None)).Success);
                Require((await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None)).Success);
                Require(evidence.IgnoredPackets == 1 && transport.Sent.Count == 2);
            });

            await CheckAsync("256 sequences cannot wrap into stale responses", async () =>
            {
                await using var transport = new FakeTransport(Enumerable.Range(0, 256).Select(index => ProductReply((byte)index)));
                var evidence = new ReadOnlyEvidence();
                var session = new ReadOnlySession(transport);
                for (var index = 0; index < 256; index++)
                {
                    var reply = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                    Require(reply.Success && reply.Sequence == index);
                }
                var exhausted = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(!exhausted.Submitted && exhausted.Sequence is null && exhausted.Disposition == "sequence-exhausted");
                Require(transport.Sent.Count == 256 && evidence.SentQueries.Select(query => query.Sequence).Distinct().Count() == 256);
            });

            await CheckAsync("EOF in partial frame is malformed and closes session", async () =>
            {
                await using var transport = new FakeTransport([ProductReply(0)[..8]]);
                var evidence = new ReadOnlyEvidence();
                var session = new ReadOnlySession(transport);
                var reply = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                var next = await session.QueryAsync(0x0100, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(reply.Disposition == "malformed-frame" && !reply.Success && !next.Submitted && next.Sequence is null);
                Require(transport.Sent.Count == 1);
            });

            await CheckAsync("complete eight-query fixture and safe receipt", async () =>
            {
                await using var transport = new FakeTransport(CompleteReplies());
                var result = new ProbeResult();
                await new ReadOnlySession(transport).RunQueriesAsync(result, CancellationToken.None);
                Require(result.Success && result.Phase == "complete" && result.Queries.Count == 8);
                Require(result.Queries.All(query => query.Success && query.Submitted && query.ResponseMatched));
                Require((bool)result.Queries.Last().Data["hiRes"]! == false);
                Require(transport.Sent.Last().SequenceEqual(Convert.FromHexString("AA0900000D010702000118")));
                var json = JsonSerializer.Serialize(result);
                Require(!json.Contains("BluetoothAddress", StringComparison.Ordinal) && !json.Contains("MAC", StringComparison.Ordinal));
                Require(result.ReadOnly.SettingCommandsSent == 0 && !result.ReadOnly.AddressIncluded && result.ReadOnly.SentQueries.Count == 8);
            });

            await CheckAsync("MTU failure prevents complete status", async () =>
            {
                var replies = CompleteReplies(); replies[2] = Reply(0x0101, 2, [6]);
                await using var transport = new FakeTransport(replies);
                var result = new ProbeResult();
                await new ReadOnlySession(transport).RunQueriesAsync(result, CancellationToken.None);
                Require(!result.Success && result.Phase == "partial" && result.Queries.Count == 8);
            });

            await CheckAsync("phase-two projection preserves sparse X4 options without sending", async () =>
            {
                var replies = CompleteReplies();
                replies[1] = Reply(0x0100, 1, new byte[] { 0 }.Concat(Convert.FromHexString("FF775AEEEE8FA65F1700000000000000")).ToArray());
                await using var transport = new FakeTransport(replies);
                var result = new ProbeResult();
                await new ReadOnlySession(transport).RunQueriesAsync(result, CancellationToken.None);
                var sent = transport.Sent.Count;
                Phase2ModelBuilder.Populate(result);
                Require(result.Success && result.SchemaVersion == 2 && result.DeviceModel is not null && transport.Sent.Count == sent);
                var model = result.DeviceModel!.Value;
                Require(model.GetProperty("identityMatch").GetString() == "product-id-exact");
                Require(!model.GetProperty("settingsTransmissionEnabled").GetBoolean());
                var profile = model.GetProperty("profile");
                Require(profile.GetProperty("productId").GetString() == "06EC10");
                Require(profile.GetProperty("equalizerModes").EnumerateArray().Select(item => item.GetProperty("protocolIndex").GetInt32())
                    .SequenceEqual(new[] { 0, 7, 2, 1, 3 }));
                Require(profile.GetProperty("customEqFrequencies").GetArrayLength() == 10);
                var hires = model.GetProperty("capabilities").GetProperty("hiRes");
                Require(!hires.GetProperty("state").GetBoolean() && hires.GetProperty("rawState").GetByte() == 0);
                Require(hires.GetProperty("protocolReadSupported").GetBoolean() && hires.GetProperty("protocolWriteSupported").GetBoolean());
                Require(result.ReadOnly.SentQueries.Count == 8 && result.ReadOnly.SettingCommandsSent == 0);
            });

            Check("name cannot select a profile for an unverified product", () =>
            {
                var result = new ProbeResult { Target = new TargetInfo { Name = "OPPO Enco X4", ProductId = "061410", IdentityVerified = false } };
                Phase2ModelBuilder.Populate(result);
                Require(result.DeviceModel is null);
            });

            await CheckAsync("unrequested feature values cannot select model state routes", async () =>
            {
                var replies = CompleteReplies();
                replies[1] = Reply(0x0100, 1, new byte[] { 0 }.Concat(Convert.FromHexString("FF775AEEEE8FA65F1700000000000000")).ToArray());
                replies[7] = Reply(0x010D, 7, [0, 3, 0x18, 0, 0x06, 1, 0x28, 1]);
                await using var transport = new FakeTransport(replies);
                var result = new ProbeResult();
                await new ReadOnlySession(transport).RunQueriesAsync(result, CancellationToken.None);
                Phase2ModelBuilder.Populate(result);
                var capabilities = result.DeviceModel!.Value.GetProperty("capabilities");
                Require(result.Success && capabilities.GetProperty("gameModeSelection").GetString() == "Unobserved");
                Require(capabilities.GetProperty("legacyGameMode").GetProperty("rawState").ValueKind == JsonValueKind.Null);
                Require(capabilities.GetProperty("modernGameMode").GetProperty("rawState").ValueKind == JsonValueKind.Null);
                Require(((IReadOnlyDictionary<string, byte>)result.Queries.Last().Data["featureValues"]!).Count == 3);
            });

            Check("successful receipt cannot omit verified capabilities", () =>
            {
                var result = new ProbeResult { Success = true, Target = new TargetInfo { ProductId = "06EC10", IdentityVerified = true } };
                try { Phase2ModelBuilder.Populate(result); throw new Exception("Missing capability evidence accepted."); }
                catch (InvalidDataException) { }
            });

            await CheckAsync("timeout abandons session without resend", async () =>
            {
                await using var transport = new FakeTransport([], waitForever: true);
                var session = new ReadOnlySession(transport, TimeSpan.FromMilliseconds(60));
                var evidence = new ReadOnlyEvidence();
                var reply = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(!reply.Success && reply.Disposition == "timeout");
                var after = await session.QueryAsync(0x0100, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(!after.Submitted && after.Disposition == "session-unavailable" && transport.Sent.Count == 1);
            });

            await CheckAsync("disconnect abandons session", async () =>
            {
                await using var transport = new FakeTransport([]);
                var session = new ReadOnlySession(transport);
                var evidence = new ReadOnlyEvidence();
                var first = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                var next = await session.QueryAsync(0x0100, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(first.Disposition == "disconnected" && !next.Submitted && transport.Sent.Count == 1);
            });

            await CheckAsync("cancelled send remains attempted", async () =>
            {
                await using var transport = new FakeTransport([], failSend: true);
                var session = new ReadOnlySession(transport);
                var evidence = new ReadOnlyEvidence();
                var first = await session.QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                var next = await session.QueryAsync(0x0100, ReadOnlyMemory<byte>.Empty, evidence, CancellationToken.None);
                Require(first.Submitted && !first.Success && evidence.SentQueries.Count == 1 && !next.Submitted);
            });

            Check("default report excludes address fields", () =>
            {
                var report = new ProbeResult();
                var json = JsonSerializer.Serialize(report);
                Require(!json.Contains("BluetoothAddress", StringComparison.Ordinal));
                Require(!json.Contains("MAC", StringComparison.Ordinal));
                Require(!report.ReadOnly.AddressIncluded && !report.ReadOnly.AudioNegotiationVerified
                    && !report.ReadOnly.BitrateMeasured && !report.ReadOnly.LatencyMeasured);
            });
            Console.WriteLine($"PASS {_passed} session tests");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL ({exception.GetType().Name})");
            return 1;
        }
    }

    private static byte[] ProductReply(byte sequence)
    {
        var fixture = Convert.FromHexString("AA0B000003810004000010EC06");
        fixture[6] = sequence;
        return fixture;
    }
    private static List<byte[]> CompleteReplies() =>
    [
        ProductReply(0), Reply(0x0100, 1, [0, 1, 0, 2]),
        Reply(0x0101, 2, [0, 0x94, 2]), Reply(0x0102, 3, [0, 0x3A, 5]),
        Reply(0x0104, 4, [0, (byte)'1', (byte)'.', (byte)'0']),
        Reply(0x0105, 5, new byte[] { 0, 1 }.Concat(System.Text.Encoding.UTF8.GetBytes("1,1,116")).ToArray()),
        Reply(0x0114, 6, [0, 9]), Reply(0x010D, 7, [0, 1, 0x18, 0])
    ];
    private static byte[] Reply(ushort request, byte sequence, byte[] payload)
    {
        if (payload.Length + 7 >= 128) throw new ArgumentException();
        var frame = new byte[9 + payload.Length];
        frame[0] = 0xAA; frame[1] = (byte)(7 + payload.Length);
        frame[4] = (byte)request; frame[5] = (byte)((request >> 8) | 0x80);
        frame[6] = sequence; frame[7] = (byte)payload.Length;
        payload.CopyTo(frame, 9);
        return frame;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException(); }
    private static void Check(string name, Action action) { action(); _passed++; Console.WriteLine($"PASS {name}"); }
    private static async Task CheckAsync(string name, Func<Task> action) { await action(); _passed++; Console.WriteLine($"PASS {name}"); }

    private sealed class FakeTransport(IEnumerable<byte[]> responses, bool waitForever = false, bool failSend = false) : IQueryTransport
    {
        private readonly Queue<byte[]> _responses = new(responses);
        public List<byte[]> Sent { get; } = [];
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(bytes.ToArray());
            return failSend ? Task.FromException(new IOException()) : Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> bytes, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                if (waitForever) await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            var next = _responses.Dequeue();
            next.CopyTo(bytes);
            return next.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

