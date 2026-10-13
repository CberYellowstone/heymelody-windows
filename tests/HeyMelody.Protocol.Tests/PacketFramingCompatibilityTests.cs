using HeyMelodyWindows.Protocol;

internal static class PacketFramingCompatibilityTests
{
    private const string ReasonKey = "HeyMelody.Protocol.FrameFailure";
    private const string FailureMessage = "Malformed protocol frame or fragment sequence.";

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Packet framing compatibility assertion failed.");
    }

    private static void CheckCodecReply(Packet packet) => Check(packet.Command == 0x8114
        && packet.Sequence == 7 && packet.Payload.SequenceEqual(new byte[] { 0, 8 }));

    private static void Reject(byte[] wire, string reason, PacketDecoder? decoder = null)
    {
        decoder ??= new PacketDecoder();
        bool rejected = false;
        try { decoder.Feed(wire); }
        catch (FormatException failure)
        {
            rejected = true;
            Check(failure.Message == FailureMessage && Equals(failure.Data[ReasonKey], reason));
        }
        Check(rejected && !decoder.HasIncompleteMessage);
        CheckCodecReply(decoder.Feed(Convert.FromHexString("AA09000014810702000008")).Single());
    }

    internal static void Run(Action<string, Action> test)
    {
        test("Air4 Pro v125 reconstructed command bitmap declares codec GET", () =>
        {
            // Static GET 0x0100 data reconstructed from firmware; not a device capture.
            var commands = CommandCapabilities.Parse(Convert.FromHexString("FF2752EEA314"));
            Check(commands.Supports(0x0114));
        });
        test("Official L1 control flags and second header byte preserve an independent codec ACK at every split", () =>
        {
            // Independent wire bytes; the production PacketEncoder is not used.
            foreach (byte flags in new byte[] { 0, 4, 0x80, 0xFC })
            foreach (byte second in new byte[] { 0, 1, 0x7E, 0xFF })
            {
                byte[] wire = Convert.FromHexString("AA09000014810702000008");
                wire[2] = flags; wire[3] = second;
                for (int split = 0; split <= wire.Length; split++)
                {
                    var decoder = new PacketDecoder();
                    var packets = decoder.Feed(wire.AsSpan(0, split))
                        .Concat(decoder.Feed(wire.AsSpan(split))).ToArray();
                    Check(packets.Length == 1); CheckCodecReply(packets[0]);
                    decoder.Complete(); Check(!decoder.HasIncompleteMessage);
                }
            }
        });
        test("L1 header flags follow a two-byte ULEB length at every split", () =>
        {
            byte[] payload = Enumerable.Range(0, 121).Select(x => (byte)x).ToArray();
            byte[] wire = [.. Convert.FromHexString("AA8001FC7E1481077900"), .. payload];
            for (int split = 0; split <= wire.Length; split++)
            {
                var decoder = new PacketDecoder();
                var packet = decoder.Feed(wire.AsSpan(0, split))
                    .Concat(decoder.Feed(wire.AsSpan(split))).Single();
                Check(packet.Command == 0x8114 && packet.Sequence == 7 && packet.Payload.SequenceEqual(payload));
                decoder.Complete();
            }
        });
        test("Independent fragments accept L1 flags and second bytes at every split and bytewise", () =>
        {
            byte[] wire = Convert.FromHexString("AA05FD7E001481AA06FEA501070200AA05FFFF020008");
            for (int split = 0; split <= wire.Length; split++)
            {
                var decoder = new PacketDecoder();
                var packet = decoder.Feed(wire.AsSpan(0, split))
                    .Concat(decoder.Feed(wire.AsSpan(split))).Single();
                CheckCodecReply(packet); decoder.Complete();
            }
            var incremental = new PacketDecoder(); var packets = new List<Packet>();
            foreach (byte value in wire) packets.AddRange(incremental.Feed(new byte[] { value }));
            CheckCodecReply(packets.Single()); incremental.Complete();
        });
        test("L1 compatibility retains fixed framing failures and resets malformed state", () =>
        {
            Reject([0], "frame-start");
            Reject(Convert.FromHexString("AA8700"), "frame-length");
            Reject(Convert.FromHexString("AA03FD7E00"), "fragment-layout");
            Reject(Convert.FromHexString("AA04FE7E0100"), "fragment-order");
            Reject(Convert.FromHexString("AA04FD7E0100"), "fragment-sequence");
            Reject(Convert.FromHexString("AA09FC7E14810703000008"), "inner-length");
            Reject(Convert.FromHexString("AA05FD7E001481AA06FEA502070200"), "fragment-sequence");
            Reject(Convert.FromHexString("AA05FD7E001481AA09FC7E14810702000008"), "fragment-order");
        });
        test("An incomplete flagged ACK preserves the fixed EOF failure and resets", () =>
        {
            var decoder = new PacketDecoder();
            Check(decoder.Feed(Convert.FromHexString("AA09FC7E148107020000")).Count == 0);
            bool rejected = false;
            try { decoder.Complete(); }
            catch (FormatException failure)
            {
                rejected = true;
                Check(failure.Message == FailureMessage && Equals(failure.Data[ReasonKey], "incomplete-message"));
            }
            Check(rejected && !decoder.HasIncompleteMessage);
            CheckCodecReply(decoder.Feed(Convert.FromHexString("AA09FC7E14810702000008")).Single());
        });
        test("Air4 Pro v125 malformed codec ACK stays strict by default and preserves bytes under explicit permission", () =>
        {
            // Reconstructed from v125 ARM instructions, not a hardware capture:
            // 0x8114 declares two payload bytes but sends status + a LE ushort codec.
            byte[] original = Convert.FromHexString("AA0A00001481070200000800");
            Reject(original, "inner-length");
            foreach (byte codec in new byte[] { 1, 2, 8 })
            {
                byte[] wire = original.ToArray(); wire[10] = codec;
                for (int split = 0; split <= wire.Length; split++)
                {
                    var decoder = new PacketDecoder(sequence => sequence == 7);
                    var packet = decoder.Feed(wire.AsSpan(0, split))
                        .Concat(decoder.Feed(wire.AsSpan(split))).Single();
                    Check(packet.Command == 0x8114 && packet.Sequence == 7
                        && packet.Payload.SequenceEqual(new byte[] { 0, codec, 0 }));
                    var parsed = QueryParsers.Parse(0x0114, packet.Payload);
                    Check(parsed.Success && Equals(parsed.Data["codecRaw"], codec)
                        && Equals(parsed.Data["extensionBytesLength"], 1));
                    decoder.Complete();
                }
            }
        });
        test("Codec firmware exception rejects other sequences layouts statuses commands and unknown codecs", () =>
        {
            byte[] original = Convert.FromHexString("AA0A00001481070200000800");
            Reject(original, "inner-length", new PacketDecoder(_ => false));
            foreach (var (offset, value) in new (int, byte)[]
            {
                (6, 8), (4, 0x13), (5, 0x05), (7, 1), (9, 1),
                (10, 0), (10, 3), (10, 0xFF), (11, 1), (11, 0xFF)
            })
            {
                byte[] wire = original.ToArray(); wire[offset] = value;
                Reject(wire, "inner-length", new PacketDecoder(sequence => sequence == 7));
            }
            byte[] extra = [.. original, 0]; extra[1]++;
            Reject(extra, "inner-length", new PacketDecoder(_ => true));
            // Known firmware sends this short ACK as a single frame; fragments
            // retain the exact inner length rule even with a permissive callback.
            Reject(Convert.FromHexString("AA050100001481AA06020001070200AA06030002000800"),
                "inner-length", new PacketDecoder(_ => true));
        });
    }
}
