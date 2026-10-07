using HeyMelodyWindows.Protocol;

internal static class OppoAdvertisementTests
{
    public static void Run(Action<string, Action> test)
    {
        test("BLE parses OPPO and OnePlus companies, OEM 98xx IDs and little-endian product", () =>
        {
            foreach (ushort company in new ushort[] { 0x079A, 0x072F })
            {
                Check(OppoAdvertisementParser.TryParse(company, [0x10, 0xEC, 6, 1, 0, 7, 0x98], out var value));
                Check(value is { ProductId: "06EC10", FastPairId: 0x9807, CaseOpen: null,
                    LeftPlacement: AdvertisedPlacement.Unknown, LeftBattery: null });
            }
            Check(!OppoAdvertisementParser.TryParse(0x004C, [0x10, 0xEC, 6, 1, 0, 1, 0x98], out _));
            Check(!OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 0, 0, 1, 0x98], out _));
            Check(!OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 1, 0, 1, 0x99], out _));
        });
        test("BLE validates every declared optional field before committing a record", () =>
        {
            byte[] complete = [0x10, 0xEC, 6, 0xFF, 3, 3, 0x98,
                1, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x17,
                161, 101, 41, 2, 3, 10, 11, 12, 0xAA, 0xBB, 1, 2, 3, 4];
            Check(OppoAdvertisementParser.TryParse(0x079A, complete, out var record));
            Check(record?.EmbeddedAddress == 0x112233445566 && record.CaseOpen == true);
            for (int length = 0; length < complete.Length; length++)
                Check(!OppoAdvertisementParser.TryParse(0x079A, complete.AsSpan(0, length), out var truncated) && truncated is null);
            Check(OppoAdvertisementParser.TryParse(0x079A, complete.Concat(new byte[] { 0xFF }).ToArray(), out _));
        });
        test("BLE lid and placement bits use OEM semantics, worn takes precedence", () =>
        {
            for (int bits = 0; bits < 32; bits++)
            {
                Check(OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 9, 0, 1, 0x98, (byte)bits], out var record));
                Check(record!.CaseOpen == ((bits & 1) != 0));
                Check(record.LeftPlacement == ((bits & 4) != 0 ? AdvertisedPlacement.InEar :
                    (bits & 2) != 0 ? AdvertisedPlacement.OutOfCase : AdvertisedPlacement.InCase));
                Check(record.RightPlacement == ((bits & 16) != 0 ? AdvertisedPlacement.InEar :
                    (bits & 8) != 0 ? AdvertisedPlacement.OutOfCase : AdvertisedPlacement.InCase));
            }
        });
        test("BLE batteries retain valid/raw distinction and OEM ten-percent buckets", () =>
        {
            foreach ((byte raw, int? expected) in new (byte, int?)[] { (0, null), (2, null), (3, 1),
                (1, 10), (19, 10), (21, 20), (161, 90), (181, 100), (201, 100), (255, 100) })
            {
                Check(OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 0x11, 0, 1, 0x98, raw, raw, raw], out var record));
                Check(record!.LeftBattery == expected && record.RightBattery == expected && record.CaseBattery == expected);
                Check(record.RawLeftBattery == raw);
            }
        });
        test("BLE 9803 embedded address resolves random advertisements without exposing anotherAddress", () =>
        {
            Check(OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 5, 0, 3, 0x98,
                0x66, 0x55, 0x44, 0x33, 0x22, 0x11], out var record));
            Check(record!.TryResolveAddress(0xAABBCCDDEEFF, false, out var address) && address == 0x112233445566);
            Check(OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 5, 0, 1, 0x98,
                0x66, 0x55, 0x44, 0x33, 0x22, 0x11], out record));
            Check(!record!.TryResolveAddress(0xAABBCCDDEEFF, false, out _));
            Check(record.TryResolveAddress(0xAABBCCDDEEFF, true, out address) && address == 0xAABBCCDDEEFF);
        });
        test("BLE zero embedded and absent address require a nonzero public sender", () =>
        {
            Check(OppoAdvertisementParser.TryParse(0x079A, [0x10, 0xEC, 6, 5, 0, 3, 0x98, 0, 0, 0, 0, 0, 0], out var record));
            Check(record!.EmbeddedAddress is null && !record.TryResolveAddress(1, false, out _));
            Check(!record.TryResolveAddress(0, true, out _) && !record.TryResolveAddress(0x1000000000000, true, out _));
            Check(record.TryResolveAddress(0x112233445566, true, out _));
        });
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("BLE advertisement assertion failed."); }
}
