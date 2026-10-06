using HeyMelodyWindows.Control;
using HeyMelodyWindows.Protocol;

var cases = new (string Name, Func<Task> Run)[]
{
    ("enable and fresh identity", async () => {
        var fake = new FakeConnector(); var r = await Run(fake);
        Check(r.Success && r.HiResAckAccepted == true && r.FinalHiResVerified && r.FreshSessionVerified, "verified enable");
        Check(r.SettingsAttempted == 1 && r.HiResSetSubmitted, "one SET");
        Check(fake.Requests.Where(x => x.Connection == 2).First().Packet.Command == 0x0103, "fresh starts with ID");
        Check(fake.Requests.Where(x => x.Connection == 2).First().Packet.Sequence == 0, "fresh independent allocator");
    }),
    ("already on uses GET only", async () => {
        var fake = new FakeConnector { HiRes = 1 }; var r = await Run(fake);
        Check(r.Success && r.SettingsAttempted == 0 && !r.HiResSetSubmitted && r.HiResAckAccepted is null, "idempotence");
        Check(fake.Requests.All(x => x.Packet.Command < 0x0400), "no setter");
    }),
    ("unknown new space blocks SET and legacy fallback", async () => {
        var fake = new FakeConnector { Space = 3 }; var r = await Run(fake);
        Check(!r.Success && r.Before?.SpaceMode is null && r.SettingsAttempted == 0, "unknown blocks");
        Check(!fake.Requests.Any(x => x.Packet.Command == 0x010D && x.Packet.Payload[1] == 0x1B), "no fallback on unknown");
    }),
    ("missing new space mode blocks SET", async () => {
        var fake = new FakeConnector { SpaceReply = [0] }; var r = await Run(fake);
        Check(!r.Success && r.SettingsAttempted == 0, "missing mode");
    }),
    ("space close ACK and GET precede HiRes", async () => {
        var fake = new FakeConnector { Space = 2 }; var r = await Run(fake);
        Check(r.Success && r.SettingsAttempted == 2 && r.Before?.SpaceMode == 2 && r.After?.SpaceMode == 0, "closed space");
        var close = fake.Requests.FindIndex(x => x.Packet.Command == 0x0422);
        var hiRes = fake.Requests.FindIndex(x => IsHiResSet(x.Packet));
        Check(close >= 0 && hiRes > close + 1 && fake.Requests[close + 1].Packet.Command == 0x012A, "close then readback then enable");
    }),
    ("failed space close does not enable HiRes", async () => {
        var fake = new FakeConnector { Space = 1, CloseAck = [1] }; var r = await Run(fake);
        Check(!r.Success && r.SettingsAttempted == 1 && !r.HiResSetSubmitted, "close rejected");
    }),
    ("close readback must be zero", async () => {
        var fake = new FakeConnector { Space = 1, ApplyClose = false }; var r = await Run(fake);
        Check(!r.Success && !r.HiResSetSubmitted && r.SettingsAttempted == 1, "close did not take effect");
    }),
    ("legacy route only when modern unsupported", async () => {
        var fake = new FakeConnector { ModernSpace = false, Space = 1 }; var r = await Run(fake);
        Check(r.Success && r.Before?.SpaceRoute == "legacy", "legacy enable");
        Check(fake.Requests.Any(x => x.Packet.Command == 0x0403 && x.Packet.Payload.SequenceEqual(new byte[] { 0x1B, 0 })), "legacy close");
        Check(!fake.Requests.Any(x => x.Packet.Command is 0x012A or 0x0422), "no modern requests");
    }),
    ("SET timeout never replays, verifies on fresh session", async () => {
        var fake = new FakeConnector { HiResAck = null }; var r = await Run(fake);
        Check(r.Success && r.HiResAckAccepted is null && r.FinalHiResVerified, "unknown ACK and verified readback");
        Check(fake.Requests.Count(x => IsHiResSet(x.Packet)) == 1 && r.SettingsAttempted == 1, "unique submission");
        Check(r.Actions.Single(x => x.CommandHex == "0x0403").Disposition == "timeout", "recorded timeout");
    }),
    ("wrong sequence ACK cannot match", async () => {
        var fake = new FakeConnector { WrongHiResSequence = true }; var r = await Run(fake);
        Check(r.HiResAckAccepted is null && r.Success, "wrong sequence ignored");
        Check(!r.Actions.Single(x => x.CommandHex == "0x0403").ResponseMatched, "not matched");
        Check(fake.Requests.Count(x => IsHiResSet(x.Packet)) == 1, "no replay");
    }),
    ("empty SET ACK is unknown; independent HiRes readback verifies success", async () => {
        var fake = new FakeConnector { HiResAck = [] }; var r = await Run(fake);
        Check(r.Success && r.HiResAckAccepted is null && r.FinalHiResVerified && r.Phase == "complete-ack-unknown", "empty ACK remains unknown");
        Check(!r.Actions.Single(x => x.CommandHex == "0x0403").Success, "empty ACK itself is not accepted");
    }),
    ("explicit nonzero SET status remains rejected", async () => {
        var fake = new FakeConnector { HiResAck = [1] }; var r = await Run(fake);
        Check(!r.Success && r.HiResAckAccepted == false && r.FinalHiResVerified, "known rejection is separate from state");
    }),
    ("fresh snapshots refresh codec and firmware metadata", async () => {
        var fake = new FakeConnector { MetadataSupported = true }; var r = await Run(fake);
        Check(r.Success && r.Before?.CodecRaw == 8 && r.After?.CodecRaw == 9, "wire codec refreshed without enum interpretation");
        Check(r.Before?.FirmwareEntries.Single().Version == "before" && r.After?.FirmwareEntries.Single().Version == "after", "firmware refreshed");
        Check(fake.Requests.Count(x => x.Connection > 1 && x.Packet.Command == 0x0114) == 2, "both fresh observations include codec");
    }),
    ("new session wrong identity blocks state reads", async () => {
        var fake = new FakeConnector { BadFreshIdentity = true }; var r = await Run(fake);
        Check(!r.Success && !r.FreshSessionVerified && !r.FinalHiResVerified && r.HiResSetSubmitted, "fresh ID checked");
        Check(fake.Requests.Where(x => x.Connection > 1).All(x => x.Packet.Command == 0x0103), "no reads on wrong peer");
    }),
    ("bad capabilities block SET", async () => {
        var fake = new FakeConnector { FeatureSetSupported = false }; var r = await Run(fake);
        Check(!r.Success && r.SettingsAttempted == 0, "unsupported SET blocked");
    }),
    ("truncated capability map blocks SET", async () => {
        var fake = new FakeConnector { ShortCaps = true }; var r = await Run(fake);
        Check(!r.Success && r.SettingsAttempted == 0, "69-bit map required");
    }),
    ("send failure records Submitted before throw and never replays", async () => {
        var fake = new FakeConnector { ThrowAfterHiResSend = true }; var r = await Run(fake);
        Check(r.HiResSetSubmitted && r.SettingsAttempted == 1 && r.HiResAckAccepted is null && r.FinalHiResVerified, "partial send recorded");
        Check(r.Actions.Single(x => x.CommandHex == "0x0403").Submitted, "submitted persisted");
    }),
    ("delayed disconnect followed by GET-only reconnect", async () => {
        var fake = new FakeConnector { DisconnectSecondSessionAfterSnapshot = true }; var r = await Run(fake);
        Check(r.Success && fake.OpenCount >= 3 && r.After?.ControlConnectionNumber == 3, "fresh reconnect after delayed EOF");
        Check(fake.Requests.Count(x => IsHiResSet(x.Packet)) == 1, "SET stays unique");
    }),
    ("enable-only send policy", () => {
        Check(ControlPolicy.IsAllowed(0x0403, [0x18, 1]) && ControlPolicy.IsAllowed(0x0422, [0]), "allowed setters");
        foreach (var x in new (ushort Cmd, byte[] Body)[] { (0x0403, [0x18, 0]), (0x0403, [0x1B, 1]), (0x0422, [1]), (0x0419, []), (0x010D, [1, 0x28]), (0x012A, [0]), (0x0103, [0]) })
            Check(!ControlPolicy.IsAllowed(x.Cmd, x.Body), "blocked policy expansion");
        return Task.CompletedTask;
    })
};
foreach (var test in cases) { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
Console.WriteLine($"PASS {cases.Length} offline control tests");

static bool IsHiResSet(Packet p) => p.Command == 0x0403 && p.Payload.SequenceEqual(new byte[] { 0x18, 1 });
static void Check(bool value, string label) { if (!value) throw new Exception(label); }
static Task<HiResWorkflowResult> Run(FakeConnector fake) => HiResWorkflow.EnableAsync(fake, new HiResWorkflowOptions
{
    RequestTimeout = TimeSpan.FromMilliseconds(40), VerificationBudget = TimeSpan.FromSeconds(2),
    RetryInterval = TimeSpan.Zero, InitialObservationDelay = TimeSpan.Zero, StableObservationDelay = TimeSpan.Zero
}, CancellationToken.None);

sealed class FakeConnector : IControlConnector
{
    public byte HiRes;
    public byte Space;
    public bool ModernSpace = true, FeatureSetSupported = true, ShortCaps, BadFreshIdentity, WrongHiResSequence,
        ThrowAfterHiResSend, DisconnectSecondSessionAfterSnapshot, MetadataSupported;
    public bool ApplyClose = true;
    public byte[]? HiResAck = [0];
    public byte[] CloseAck = [0];
    public byte[]? SpaceReply;
    public int OpenCount;
    public List<(int Connection, Packet Packet)> Requests = [];
    public Task<IControlTransport> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<IControlTransport>(new FakeTransport(this, ++OpenCount));
    }
    private sealed class FakeTransport(FakeConnector owner, int number) : IControlTransport
    {
        private readonly Queue<byte[]> _responses = new();
        private bool _disposed, _disconnect;
        private int _queries;
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            if (_disposed) throw new ObjectDisposedException("fake");
            var request = new PacketDecoder().Feed(bytes.Span).Single();
            if (!ControlPolicy.IsAllowed(request.Command, request.Payload)) throw new Exception("Unsafe request");
            owner.Requests.Add((number, request));
            if (number == 2 && owner.DisconnectSecondSessionAfterSnapshot && _queries++ >= 4) { _disconnect = true; return Task.CompletedTask; }
            byte[]? response;
            switch (request.Command)
            {
                case 0x0103: response = number > 1 && owner.BadFreshIdentity ? [0, 0x11, 0xEC, 0x06] : [0, 0x10, 0xEC, 0x06]; break;
                case 0x0100:
                    response = new byte[owner.ShortCaps ? 2 : 10];
                    if (owner.FeatureSetSupported) response[1] |= 0x80;
                    if (owner.ModernSpace && !owner.ShortCaps) response[6] |= 0x80;
                    if (owner.MetadataSupported && !owner.ShortCaps) { response[1] |= 1; response[3] |= 2; }
                    break;
                case 0x0105: response = new byte[] { 0, 1 }.Concat(System.Text.Encoding.UTF8.GetBytes(number == 1 ? "1,1,before" : "1,1,after")).ToArray(); break;
                case 0x0114: response = [0, number == 1 ? (byte)8 : (byte)9]; break;
                case 0x010D: response = [0, 1, request.Payload[1], request.Payload[1] == 0x18 ? owner.HiRes : owner.Space]; break;
                case 0x012A: response = owner.SpaceReply ?? [0, owner.Space]; break;
                case 0x0422:
                    if (owner.ApplyClose) owner.Space = 0;
                    response = owner.CloseAck; break;
                case 0x0403:
                    if (request.Payload[0] == 0x1B) { if (owner.ApplyClose) owner.Space = 0; response = owner.CloseAck; }
                    else {
                        owner.HiRes = 1;
                        if (owner.ThrowAfterHiResSend) throw new IOException("simulated partial send");
                        response = owner.HiResAck;
                    }
                    break;
                default: throw new Exception("Unexpected command");
            }
            if (response is not null)
                _responses.Enqueue(PacketEncoder.Encode((ushort)(request.Command | 0x8000),
                    owner.WrongHiResSequence && request.Command == 0x0403 && request.Payload[0] == 0x18 ? (byte)(request.Sequence + 1) : request.Sequence, response));
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (_disconnect) return 0;
            if (_responses.TryDequeue(out var response)) { response.CopyTo(buffer); return response.Length; }
            await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0;
        }
        public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    }
}
