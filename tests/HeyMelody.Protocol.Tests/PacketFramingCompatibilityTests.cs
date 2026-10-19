using HeyMelodyWindows.Protocol;

internal static class PacketFramingCompatibilityTests
{
    private const string ReasonKey = "HeyMelody.Protocol.FrameFailure";
    private const string CommandKey = "HeyMelody.Protocol.ResponseCommand";
    private const string SequenceKey = "HeyMelody.Protocol.ResponseSequence";
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

    private static FormatException FailureFrom(byte[] wire, PacketDecoder? decoder = null)
    {
        decoder ??= new PacketDecoder();
        try { decoder.Feed(wire); }
        catch (FormatException failure)
        {
            Check(failure.Message == FailureMessage && !decoder.HasIncompleteMessage);
            return failure;
        }
        throw new Exception("Malformed framing was accepted.");
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
        test("Complete malformed inner headers expose only typed numeric correlation and reset state", () =>
        {
            foreach (var (hex, command, sequence) in new (string, ushort, byte)[]
            {
                ("AA0A00001481070200000800", 0x8114, 7),
                ("AA0A00000681DE0200000800", 0x8106, 0xDE),
                ("AA0A00000005FF0200000800", 0x0500, 0xFF),
            })
            {
                byte[] wire = Convert.FromHexString(hex);
                for (int split = 0; split < wire.Length; split++)
                {
                    var decoder = new PacketDecoder();
                    Check(decoder.Feed(wire.AsSpan(0, split)).Count == 0);
                    var failure = FailureFrom(wire.AsSpan(split).ToArray(), decoder);
                    Check(Equals(failure.Data[ReasonKey], "inner-length") && failure.Data.Count == 3);
                    Check(failure.Data[CommandKey] is ushort numericCommand && numericCommand == command);
                    Check(failure.Data[SequenceKey] is byte numericSequence && numericSequence == sequence);
                    CheckCodecReply(decoder.Feed(Convert.FromHexString("AA09000014810702000008")).Single());
                }
            }
        });
        test("Outer errors and incomplete inner headers do not invent response correlation", () =>
        {
            foreach (var (hex, reason) in new[]
            {
                ("00", "frame-start"),
                ("AA8700", "frame-length"),
                ("AA06000014810700", "inner-length"),
                ("AA09010000148107010000", "inner-length"),
            })
            {
                var failure = FailureFrom(Convert.FromHexString(hex));
                Check(Equals(failure.Data[ReasonKey], reason) && failure.Data.Count == 1);
                Check(!failure.Data.Contains(CommandKey) && !failure.Data.Contains(SequenceKey));
            }
        });
        test("Codec length compatibility requires a live matching sequence and never broadens other shapes", () =>
        {
            byte[] known = Convert.FromHexString("AA0A00001481070200000800");
            byte? pendingSequence = null;
            int permissionCalls = 0;
            var decoder = new PacketDecoder(sequence => { permissionCalls++; return pendingSequence == sequence; });
            var noContext = FailureFrom(known, decoder);
            Check(noContext.Data[CommandKey] is ushort command && command == 0x8114);
            Check(noContext.Data[SequenceKey] is byte sequence && sequence == 7);
            Check(permissionCalls == 1);
            pendingSequence = 8;
            FailureFrom(known, decoder); Check(permissionCalls == 2);
            pendingSequence = 7;
            var packet = decoder.Feed(known).Single();
            Check(packet.Command == 0x8114 && packet.Sequence == 7
                && packet.Payload.SequenceEqual(new byte[] { 0, 8, 0 }));
            Check(permissionCalls == 3);

            foreach (var (offset, value) in new (int, byte)[] { (4, 0x06), (7, 1), (9, 1), (10, 3), (11, 1) })
            {
                byte[] wire = known.ToArray(); wire[offset] = value;
                var failure = FailureFrom(wire, decoder);
                Check(Equals(failure.Data[ReasonKey], "inner-length") && permissionCalls == 3);
            }
            CheckCodecReply(decoder.Feed(Convert.FromHexString("AA09000014810702000008")).Single());
            Check(permissionCalls == 3); decoder.Complete();
        });
        test("The decode observer sees preceding valid frames before a later merged frame fails", () =>
        {
            var observed = new List<Packet>();
            var decoder = new PacketDecoder(null, observed.Add);
            byte[] good = Convert.FromHexString("AA09000014810702000008");
            byte[] bad = Convert.FromHexString("AA0A00001481070200000800");
            var failure = FailureFrom([.. good, .. bad], decoder);
            Check(observed.Count == 1); CheckCodecReply(observed[0]);
            Check(failure.Data.Count == 3 && failure.Data[CommandKey] is ushort command && command == 0x8114
                && failure.Data[SequenceKey] is byte sequence && sequence == 7);
            CheckCodecReply(decoder.Feed(good).Single()); Check(observed.Count == 2);
            var noValidFrame = new List<Packet>();
            FailureFrom(bad, new PacketDecoder(null, noValidFrame.Add));
            Check(noValidFrame.Count == 0);
        });
        test("The decode observer waits for an entire fragmented message before recording a packet", () =>
        {
            var observed = new List<Packet>();
            var decoder = new PacketDecoder(null, observed.Add);
            Check(decoder.Feed(Convert.FromHexString("AA05FD7E001481")).Count == 0 && observed.Count == 0);
            Check(decoder.Feed(Convert.FromHexString("AA06FEA501070200")).Count == 0 && observed.Count == 0);
            var packet = decoder.Feed(Convert.FromHexString("AA05FFFF020008")).Single();
            CheckCodecReply(packet); Check(observed.Count == 1); CheckCodecReply(observed[0]);
            decoder.Complete();
        });
    }
}
