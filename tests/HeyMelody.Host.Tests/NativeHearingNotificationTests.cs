using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Protocol;

internal static class NativeHearingNotificationTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("retired auto volume notifications cannot suppress a mixed hearing profile refresh", async () =>
        {
            var transport = new Transport();
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(new
            {
                id = "06EC10", name = "Memory hearing fixture", supportSpp = true,
                uuid = "00001101-0000-1000-8000-00805f9b34fb", function = new { hearingEnhancement = 1 }
            }), "memory-test");
            var client = new DailyClient(transport, _ => profile);
            await using var service = new NativeHeadsetService(
                () => throw new InvalidOperationException("Hearing notification test enumerated Bluetooth"),
                new DeviceWhitelistStore(Path.Combine(scratch, "hearing-notification.json")));
            Field("_client").SetValue(service, client);
            var initial = await client.RefreshAsync(DailyRefreshScope.Identity);
            Check(initial.Connected && initial.ProfileVerified, "Memory transport did not establish a verified observation");
            string stableKey = "memory-test/" + Guid.NewGuid().ToString("N");
            Field("_stableKey").SetValue(service, stableKey);
            Field("_calibrationContext").SetValue(service, client.CalibrationContext(stableKey, 1));
            Field("_hearingRefreshLifetime").SetValue(service, new CancellationTokenSource());
            Field("_hearingProfiles").SetValue(service, new UiHearingProfile[] { new(7, "旧配置", true, true) });
            var notify = typeof(NativeHeadsetService).GetMethod("OnHearingNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Push(byte[] payload) => notify.Invoke(service, [new Packet(0x0503, 0, payload)]);

            int before = transport.Submissions.Count;
            Push([2, 0x1C, 0, 0x1C, 0xFF]); // An ignored feature alone must not cause a hearing GET.
            Push([3, 0x1C, 0, 0x1C, 0xFF, 0x0B]); // Envelope remains strict.
            Push([2, 0x0B, 0, 0x0B, 1]); // Unrelated duplicate IDs remain strict.
            Check(transport.Submissions.Count == before && ((Task)Field("_hearingRefreshTask").GetValue(service)!).IsCompleted,
                "Ignored-only or invalid notifications queued a profile refresh");

            Push([3, 0x1C, 0, 0x1C, 0xFF, 0x0B, 1]);
            await ((Task)Field("_hearingRefreshTask").GetValue(service)!).WaitAsync(TimeSpan.FromSeconds(3));
            var profiles = (IReadOnlyList<UiHearingProfile>)Field("_hearingProfiles").GetValue(service)!;
            Check(profiles is [{ Uid: 77, Name: "手机更新后的听感", Current: true, CanApply: true }],
                "A duplicate retired feature suppressed the new phone hearing profile metadata");
            Check(transport.Submissions.Skip(before).Select(packet => packet.Command).SequenceEqual(new ushort[] { 0x0115, 0x010D }),
                "Mixed hearing notification must refresh current metadata and feature state with GET only");
        });
    }

    private static FieldInfo Field(string name) => typeof(NativeHeadsetService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class Transport : IControlConnector, IControlTransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<Packet> Submissions { get; } = [];
        public Task<IControlTransport> OpenAsync(CancellationToken token) => Task.FromResult<IControlTransport>(this);
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            var packet = new PacketDecoder().Feed(bytes.Span).Single();
            Submissions.Add(packet);
            byte[] reply = packet.Command switch
            {
                0x0103 => [0, 0x10, 0xEC, 6],
                0x0100 => [0, 0x90, 0, 8, 2], // Placement, feature SET and hearing commands; no event registration.
                0x010B => [0, 0],
                0x0115 => [0, .. CalibrationCodecs.Parameters(3,
                    Enumerable.Range(1, 2).SelectMany(side => Enumerable.Range(1, 6)
                        .Select(type => new HearingLevel((byte)side, (byte)type, 0))).ToArray(), 77, "手机更新后的听感")],
                0x010D => [0, 1, 0x0B, 1],
                _ => throw new InvalidOperationException($"Unexpected memory command {packet.Command:X4}")
            };
            _replies.Writer.TryWrite(PacketEncoder.Encode((ushort)(packet.Command | 0x8000), packet.Sequence, reply));
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            var reply = await _replies.Reader.ReadAsync(token); reply.CopyTo(buffer); return reply.Length;
        }
        public ValueTask DisposeAsync() { _replies.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
