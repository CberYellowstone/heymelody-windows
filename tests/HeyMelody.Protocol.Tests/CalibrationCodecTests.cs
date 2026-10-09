using System.Buffers.Binary;
using System.Text;
using HeyMelodyWindows.Protocol;

internal static class CalibrationCodecTests
{
    private const int Uid = unchecked((int)0xF1234567);
    // Captured X4 v116 response headers and sizes, LIVE-TEST.md: 250 / 299 bytes.
    // UID and all personal coefficients are replaced; left/right use distinct synthetic stable IIR stages.
    private const string HearingFixtureHex = "00F1234567043C0001010000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E";
    private const string ScanFixtureHex = "00F12345670444AC4800010000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F00000000000000000000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E0000003F0000803E0000003E0000803F000080BE0000003E";

    public static void Run(Action<string, Action> test)
    {
        test("calibration stop/pause are single byte and signed UID/dB retain wire order", () =>
        {
            Check(CalibrationCodecs.Parameters(4, [], Uid).SequenceEqual(new byte[] { 4 }));
            Check(CalibrationCodecs.Parameters(2, [], Uid).SequenceEqual(new byte[] { 2 }));
            Check(CalibrationCodecs.PauseHearing().SequenceEqual(new byte[] { 4 }));
            Check(CalibrationCodecs.StopHearingPreview().SequenceEqual(new byte[] { 2 }));
            Check(CalibrationCodecs.EarScanParameters(2, [9, 8], Uid).SequenceEqual(new byte[] { 2 }));
            Check(CalibrationCodecs.EarScanDetection(true, Uid).SequenceEqual(Convert.FromHexString("0401F1234567")));
            Check(CalibrationCodecs.EarScanDetection(false, 0x01234567).SequenceEqual(Convert.FromHexString("040001234567")));
            Check(CalibrationCodecs.HearingDetection(false).SequenceEqual(new byte[] { 2, 0 }));
            Check(CalibrationCodecs.Frequency(new(2, 6, -55)).SequenceEqual(Convert.FromHexString("03010206C9")));
            Check(CalibrationCodecs.Frequency(new(1, 1, sbyte.MinValue))[4] == 0x80);
            Check(CalibrationCodecs.Frequency(new(1, 1, sbyte.MaxValue))[4] == 0x7F);
            Throws<ArgumentException>(() => CalibrationCodecs.Frequency(new(0, 1, 0)));
            Throws<ArgumentException>(() => CalibrationCodecs.Frequency(new(1, 7, 0)));
        });
        test("complete hearing save validates UTF8 byte count, not character count", () =>
        {
            HearingLevel[] levels = Levels();
            string name39 = new('x', 39);
            byte[] bytes = CalibrationCodecs.Parameters(3, levels, Uid, name39);
            Check(bytes.Length == 81 && bytes[0] == 3 && bytes[1] == 12);
            Check(bytes.AsSpan(38, 4).SequenceEqual(Convert.FromHexString("F1234567")));
            var parsed = CalibrationCodecs.ParseCurrentHearing([0, .. bytes]);
            Check(parsed.Uid == Uid && parsed.Name == name39 && parsed.Levels.SequenceEqual(levels));
            string chinese39 = new('听', 13);
            Check(CalibrationCodecs.Parameters(3, levels, 0x01234567, chinese39).Length == 81);
            Check(CalibrationCodecs.Parameters(3, levels, 0, "test").AsSpan(38, 4).SequenceEqual(new byte[4]));
            foreach (string invalid in new[] { "", new string('x', 40), new string('听', 14), "a\0b", "\uD800" })
                Throws<ArgumentException>(() => CalibrationCodecs.Parameters(3, levels, Uid, invalid));
            Throws<ArgumentException>(() => CalibrationCodecs.Parameters(3, levels[..11], Uid, "test"));
            Throws<ArgumentException>(() => CalibrationCodecs.Parameters(3, [.. levels[..11], levels[0]], Uid, "test"));
            Check(CalibrationCodecs.Parameters(1, [], 0).SequenceEqual(Convert.FromHexString("010000000000")));
            Check(CalibrationCodecs.Parameters(1, levels, 0).Length == 42);
            byte[] filterRequest = CalibrationCodecs.FilterRequest(levels, Uid);
            Check(filterRequest.Length == 41 && filterRequest[0] == 12
                && filterRequest.AsSpan(37).SequenceEqual(Convert.FromHexString("F1234567")));
        });
        test("0115 preserves failure status and refuses invalid successful records", () =>
        {
            var error = Throws<CalibrationDeviceException>(() => CalibrationCodecs.ParseCurrentHearing([1]));
            Check(error.Status == 1);
            byte[] valid = [0, .. CalibrationCodecs.Parameters(3, Levels(), Uid, "test")];
            byte[] zeroUid = valid.ToArray();
            zeroUid.AsSpan(39, 4).Clear();
            byte[] duplicateLevel = valid.ToArray();
            duplicateLevel.AsSpan(3, 3).CopyTo(duplicateLevel.AsSpan(6, 3));
            foreach (byte[] invalid in new byte[][] { [0, 3, 0, 0, 0, 0, 0], valid[..43], [.. valid, .. new byte[40]], zeroUid, duplicateLevel })
                Throws<FormatException>(() => CalibrationCodecs.ParseCurrentHearing(invalid));
            byte[] badUtf8 = valid.ToArray();
            badUtf8[^1] = 0xFF;
            Throws<FormatException>(() => CalibrationCodecs.ParseCurrentHearing(badUtf8));
            byte[] badName = valid.ToArray();
            badName[^1] = 0;
            Throws<FormatException>(() => CalibrationCodecs.ParseCurrentHearing(badName));
        });
        test("0411 description is five-byte TLV and 0118 preserves unknown items", () =>
        {
            Check(CalibrationCodecs.RestoreDescription(7).SequenceEqual(Convert.FromHexString("0101010007")));
            Check(CalibrationCodecs.ParseRestoreDescription(Convert.FromHexString("000101010007")) == 7);
            byte[] multiple = Convert.FromHexString("0002090200AABB0101000C");
            var items = CalibrationCodecs.ParseRestoreItems(multiple);
            Check(items.Count == 2 && items[0].Type == 9 && items[0].Value.SequenceEqual(new byte[] { 0xAA, 0xBB }));
            Check(CalibrationCodecs.ParseRestoreDescription(multiple) == 12);
            Check(CalibrationCodecs.ParseRestoreDescription(Convert.FromHexString("000109010001")) is null);
            Check(CalibrationCodecs.ParseRestoreDescription([0, 0]) is null);
            foreach (byte[] invalid in new byte[][] { [0], [0, 1], [0, 1, 1, 1, 0], [0, 1, 1, 0, 0],
                [0, 1, 1, 2, 0, 7, 8], [0, 0, 0], [0, 2, 1, 1, 0, 7, 1, 1, 0, 8], [0, 1, 9, 0, 1] })
                Throws<FormatException>(() => CalibrationCodecs.ParseRestoreItems(invalid));
            Check(Throws<CalibrationDeviceException>(() => CalibrationCodecs.ParseRestoreDescription([1])).Status == 1);
        });
        test("011F scan request and 0415 preview/save use their own layouts", () =>
        {
            byte[] scan = Enumerable.Range(0, 168).Select(x => (byte)x).ToArray();
            byte[] request = CalibrationCodecs.EarScanFilterRequest(scan, Uid);
            Check(request.Length == 174 && request.AsSpan(0, 2).SequenceEqual(Convert.FromHexString("A800")));
            Check(request.AsSpan(2, 168).SequenceEqual(scan) && request.AsSpan(170).SequenceEqual(Convert.FromHexString("F1234567")));
            byte[] save = CalibrationCodecs.SaveEarScan(scan, Uid);
            Check(save.Length == 175 && save.AsSpan(0, 3).SequenceEqual(Convert.FromHexString("03A800")));
            var result = CalibrationCodecs.ParseCurrentEarScan([0, .. save]);
            Check(result.Action == 3 && result.Uid == Uid && result.Data.SequenceEqual(scan));
            Check(CalibrationCodecs.EarScanParameters(1, [], 0).SequenceEqual(new byte[7] { 1, 0, 0, 0, 0, 0, 0 }));
            Check(CalibrationCodecs.SaveEarScan([1], 0).Length == 8);
            Throws<ArgumentOutOfRangeException>(() => CalibrationCodecs.EarScanFilterRequest([], Uid));
            Throws<ArgumentOutOfRangeException>(() => CalibrationCodecs.SaveEarScan([], Uid));
            Throws<ArgumentOutOfRangeException>(() => CalibrationCodecs.SaveEarScan(new byte[32768], Uid));
            Throws<FormatException>(() => CalibrationCodecs.ParseEarScan([3, 1, 0, 9, 0, 0, 0]));
        });
        test("event8 keeps detection type, status and optional attributes apart from ACKs", () =>
        {
            Check(CalibrationCodecs.TryEvent(new(0x0204, 1, [8, 4, 15]), 8, out var data));
            var state = CalibrationCodecs.ParseHearingState(data);
            Check(state.DetectionType == 4 && state.Status == 15 && state.Details.Count == 0);
            Check(CalibrationCodecs.TryEvent(new(0x8202, 1, [0, 8, 3, 7, 1, 2, 3]), 8, out data));
            state = CalibrationCodecs.ParseHearingState(data);
            Check(state.DetectionType == 3 && state.Status == 7 && state.Details.SequenceEqual(new byte[] { 1, 2, 3 }));
            state = CalibrationCodecs.ParseHearingState([0xFE, 0xFA]);
            Check(state.DetectionType == 0xFE && state.Status == 0xFA);
            Check(!CalibrationCodecs.TryEvent(new(0x840D, 1, [0]), 8, out _));
            Check(!CalibrationCodecs.TryEvent(new(0x8202, 1, [1, 8, 4, 0]), 8, out _));
            Throws<FormatException>(() => CalibrationCodecs.ParseHearingState([4]));
            Check(CalibrationCodecs.TryEvent(new(0x0204, 1, [4, 1, 1, 2, 1]), 4, out data));
            var fit = CalibrationCodecs.ParseFit(data);
            Check(fit[1] == 1 && fit[2] == 1);
            Throws<FormatException>(() => CalibrationCodecs.ParseFit([2, 1, 1, 1, 2]));
        });
        test("captured 250-byte 0116 header yields 30 left and 30 right coefficients", () =>
        {
            byte[] bytes = Convert.FromHexString(HearingFixtureHex);
            Check(bytes.Length == 250);
            var packet = CalibrationCodecs.ParseFilter(bytes);
            Check(packet.Uid == Uid && packet.DeviceType == 4 && packet.TotalSamples == 60
                && packet.PacketId == 1 && packet.EnhanceType == 1 && packet.Values.Count == 60);
            var assembler = new HearingFilterAssembler(Uid);
            var curve = assembler.Add(packet)!;
            Check(curve is not null && curve.DeviceType == 4 && curve.EnhanceType == 1 && curve.TotalSamples == 60
                && curve.SourceCommand == 0x0116 && curve.SamplingFrequency is null);
            Check(curve!.Left.Count == 30 && curve.Right.Count == 30 && curve.Values.Count == 60);
            Check(curve.Left[0] == 1 && curve.Right[0] == 0.5f && curve.Right[4] == -0.25f);
            Check(curve.Values.SequenceEqual(curve.Left.Concat(curve.Right)));
            Check(assembler.Add(packet) is null);
        });
        test("captured 299-byte 011F header carries sampling rate without enhanceType", () =>
        {
            byte[] bytes = Convert.FromHexString(ScanFixtureHex);
            Check(bytes.Length == 299);
            var packet = CalibrationCodecs.ParseEarScanFilter(bytes);
            Check(packet.Uid == Uid && packet.DeviceType == 4 && packet.TotalSamples == 72
                && packet.PacketId == 1 && packet.SamplingFrequency == 44100 && packet.Values.Count == 72);
            var curve = new EarScanFilterAssembler(Uid).Add(packet)!;
            Check(curve.Left.Count == 36 && curve.Right.Count == 36 && curve.EnhanceType is null
                && curve.SamplingFrequency == 44100 && curve.SourceCommand == 0x011F);
            Check(curve.Left[0] == 1 && curve.Right[0] == 0.5f && curve.Right[4] == -0.25f);
            Throws<FormatException>(() => CalibrationCodecs.ParseFilter(bytes));
            Throws<FormatException>(() => CalibrationCodecs.ParseEarScanFilter(Convert.FromHexString(HearingFixtureHex)));
        });
        test("single-ear fragments sort IDs and complete by per-ear samples without contiguous IDs", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var lowLeft = Fragment(full, 1, 77, full.Values.Take(10).ToArray());
            var highLeft = Fragment(full, 1, 101, full.Values.Skip(10).Take(20).ToArray());
            var lowRight = Fragment(full, 2, 77, full.Values.Skip(30).Take(10).ToArray());
            var highRight = Fragment(full, 2, 101, full.Values.Skip(40).Take(20).ToArray());
            var assembler = new HearingFilterAssembler(Uid);
            Check(assembler.Add(full with { Uid = 7 }) is null);
            Check(assembler.Add(highRight) is null && assembler.Add(highRight) is null);
            Check(assembler.Add(highLeft) is null && assembler.Add(lowRight) is null);
            var curve = assembler.Add(lowLeft)!;
            Check(curve.Values.SequenceEqual(full.Values));
        });
        test("packet IDs above 127 sort as signed Java bytes while preserving wire IDs", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var low = Fragment(full, 1, 201, full.Values.Take(10).ToArray());
            var high = Fragment(full, 1, 77, full.Values.Skip(10).Take(20).ToArray());
            var right = Fragment(full, 2, 77, full.Values.Skip(30).ToArray());
            var assembler = new HearingFilterAssembler(Uid);
            Check(low.PacketId == 201 && assembler.Add(high) is null && assembler.Add(low) is null);
            Check(assembler.Add(right)!.Values.SequenceEqual(full.Values));
        });
        test("single-ear packets with identical IDs do not complete before both ears", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var left = Fragment(full, 1, 93, full.Values.Take(30).ToArray());
            var right = Fragment(full, 2, 93, full.Values.Skip(30).ToArray());
            var assembler = new HearingFilterAssembler(Uid);
            Check(assembler.Add(left) is null && assembler.Add(left) is null);
            var curve = assembler.Add(right)!;
            Check(curve.Left.SequenceEqual(left.Values) && curve.Right.SequenceEqual(right.Values));
            var partial = new HearingFilterAssembler(Uid);
            Check(partial.Add(left) is null);
            Check(partial.Add(right with { Values = right.Values.Take(29).ToArray() }) is null);
        });
        test("filter assembly rejects changed metadata, conflicting repeats and per-ear overflow", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var part = Fragment(full, 1, 8, full.Values.Take(6).ToArray());
            var assembler = new HearingFilterAssembler(Uid);
            Check(assembler.Add(part) is null);
            Throws<FormatException>(() => assembler.Add(part with { EnhanceType = 2 }));
            Throws<FormatException>(() => assembler.Add(part with { TotalSamples = 72 }));
            float[] changed = part.Values.ToArray();
            changed[0] = 0.75f;
            Throws<FormatException>(() => assembler.Add(part with { Values = changed }));
            Throws<FormatException>(() => assembler.Add(full with { PacketId = 9 }));
            Check(assembler.Add(part) is null);
            var remainder = Fragment(full, 1, 9, full.Values.Skip(6).Take(24).ToArray());
            Check(assembler.Add(remainder) is null);
            Check(assembler.Add(Fragment(full, 2, 9, full.Values.Skip(30).ToArray()))!.Values.SequenceEqual(full.Values));
            var scan = CalibrationCodecs.ParseEarScanFilter(Convert.FromHexString(ScanFixtureHex));
            var scanAssembler = new EarScanFilterAssembler(Uid);
            Check(scanAssembler.Add(scan with { DeviceType = 1, Values = scan.Values.Take(36).ToArray() }) is null);
            Throws<FormatException>(() => scanAssembler.Add(scan with { DeviceType = 2, SamplingFrequency = 48000,
                Values = scan.Values.Skip(36).ToArray() }));
            Check(scanAssembler.Add(scan with { DeviceType = 2, Values = scan.Values.Skip(36).ToArray() }) is not null);
        });
        test("bad dual-ear packet cannot commit its left half before right validation", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var right = Fragment(full, 2, 9, full.Values.Skip(30).ToArray());
            var assembler = new HearingFilterAssembler(Uid);
            Check(assembler.Add(right) is null);
            float[] bad = full.Values.ToArray();
            bad[^1] = 2;
            Throws<FormatException>(() => assembler.Add(Fragment(full, 4, 9, bad)));
            Check(assembler.Add(Fragment(full, 1, 8, full.Values.Take(30).ToArray()))!.Values.SequenceEqual(full.Values));
        });
        test("retained coefficients are immutable and exact duplicate float bits matter", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            float[] leftValues = full.Values.Take(30).ToArray();
            var left = Fragment(full, 1, 1, leftValues);
            var assembler = new HearingFilterAssembler(Uid);
            Check(assembler.Add(left) is null);
            leftValues[0] = 9;
            Throws<FormatException>(() => assembler.Add(left));
            var curve = assembler.Add(Fragment(full, 2, 1, full.Values.Skip(30).ToArray()))!;
            Check(curve.Left[0] == 1);
            var exact = new HearingFilterAssembler(Uid);
            var zeros = Fragment(full, 1, 1, new float[] { 0 });
            Check(exact.Add(zeros) is null);
            Throws<FormatException>(() => exact.Add(zeros with { Values = new[] { BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)) } }));
        });
        test("384 format requires 384 samples in each ear, not 384 combined", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var assembler = new HearingFilterAssembler(Uid);
            float[] left = Enumerable.Range(0, 384).Select(x => (float)x).ToArray();
            float[] right = Enumerable.Range(0, 384).Select(x => (float)(x + 1000)).ToArray();
            var header = full with { TotalSamples = 384 };
            Check(assembler.Add(Fragment(header, 1, 3, left.Take(192).ToArray())) is null);
            Check(assembler.Add(Fragment(header, 2, 3, right.Take(192).ToArray())) is null);
            Check(assembler.Add(Fragment(header, 1, 7, left.Skip(192).ToArray())) is null);
            var curve = assembler.Add(Fragment(header, 2, 7, right.Skip(192).ToArray()))!;
            Check(curve.TotalSamples == 384 && curve.Left.Count == 384 && curve.Right.Count == 384
                && curve.Left.SequenceEqual(left) && curve.Right.SequenceEqual(right));
        });
        test("384 dual-ear format consumes two full declared packets and rejects guessed short layouts", () =>
        {
            var full = CalibrationCodecs.ParseFilter(Convert.FromHexString(HearingFixtureHex));
            var header = full with { TotalSamples = 384 };
            float[] left = Enumerable.Range(0, 384).Select(x => (float)x).ToArray();
            float[] right = Enumerable.Range(0, 384).Select(x => (float)(x + 1000)).ToArray();
            var first = Fragment(header, 4, 1, left.Take(192).Concat(right.Take(192)).ToArray());
            var second = Fragment(header, 4, 2, left.Skip(192).Concat(right.Skip(192)).ToArray());
            var assembler = new HearingFilterAssembler(Uid);
            Check(assembler.Add(first) is null && assembler.Add(first) is null);
            Throws<FormatException>(() => assembler.Add(first with { PacketId = 2, Values = first.Values.Take(192).ToArray() }));
            Throws<FormatException>(() => assembler.Add(first with { PacketId = 2, Values = left.Concat(right).ToArray() }));
            var curve = assembler.Add(second)!;
            Check(curve.Left.SequenceEqual(left) && curve.Right.SequenceEqual(right));
            Throws<FormatException>(() => new HearingFilterAssembler(Uid).Add(full with { Values = full.Values.Take(48).ToArray() }));
        });
        test("filter parsers reject truncated floats, odd/zero counts, invalid ears and nonfinite values", () =>
        {
            byte[] hearing = Convert.FromHexString(HearingFixtureHex);
            byte[] scan = Convert.FromHexString(ScanFixtureHex);
            foreach (byte[] original in new[] { hearing, scan })
            {
                bool isScan = original.Length == 299;
                int headerLength = isScan ? 11 : 10;
                int totalOffset = isScan ? 8 : 6;
                Action<byte[]> parse = bytes => { if (isScan) CalibrationCodecs.ParseEarScanFilter(bytes); else CalibrationCodecs.ParseFilter(bytes); };
                foreach (byte[] invalid in new[] { original[..(headerLength - 1)], original[..headerLength], original[..^1] })
                    Throws<FormatException>(() => parse(invalid));
                foreach (ushort total in new ushort[] { 0, 1, 59, ushort.MaxValue })
                {
                    byte[] invalid = original.ToArray();
                    BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(totalOffset), total);
                    Throws<FormatException>(() => parse(invalid));
                }
                foreach (byte side in new byte[] { 0, 3, 255 })
                {
                    byte[] invalid = original.ToArray();
                    invalid[5] = side;
                    Throws<FormatException>(() => parse(invalid));
                }
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                {
                    byte[] invalid = original.ToArray();
                    BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(headerLength), value);
                    Throws<FormatException>(() => parse(invalid));
                }
                // An even total cannot justify an unequal dual-ear split or excess samples.
                Throws<FormatException>(() => parse(original[..^4]));
                byte[] tooMany = [.. original, 0, 0, 0, 0, 0, 0, 0, 0];
                Throws<FormatException>(() => parse(tooMany));
                byte[] failure = [15];
                Check(Throws<CalibrationDeviceException>(() => parse(failure)).Status == 15);
            }
            byte[] zeroSampling = scan.ToArray();
            zeroSampling.AsSpan(6, 2).Clear();
            Throws<FormatException>(() => CalibrationCodecs.ParseEarScanFilter(zeroSampling));
            byte[] maximum = hearing.ToArray();
            maximum[5] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(maximum.AsSpan(6), 65534);
            var maximumPacket = CalibrationCodecs.ParseFilter(maximum);
            Check(maximumPacket.TotalSamples == 65534 && new HearingFilterAssembler(Uid).Add(maximumPacket) is null);
        });
    }

    private static HearingLevel[] Levels() => Enumerable.Range(0, 12)
        .Select(x => new HearingLevel((byte)(x / 6 + 1), (byte)(x % 6 + 1), (sbyte)(x - 25))).ToArray();
    private static HearingFilterPacket Fragment(HearingFilterPacket header, byte deviceType, byte packetId, float[] values)
        => header with { DeviceType = deviceType, PacketId = packetId, Values = values };
    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Calibration codec assertion failed.");
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
