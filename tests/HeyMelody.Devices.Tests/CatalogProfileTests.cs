using System.Text.Json;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Resources;

internal static class CatalogProfileTests
{
    internal static void AllBundledProfiles()
    {
        var bundles = new BundledResourceStore();
        foreach (var source in Enum.GetValues<ResourceSource>())
        {
            var catalog = bundles.GetCatalog(source);
            Equal(source == ResourceSource.HeyMelody ? 85 : 141, catalog.Profiles.Count);
            foreach (var row in catalog.Profiles.Values)
            {
                try
                {
                    var profile = DeviceProfile.FromResource(row.Raw, source.ToString(), catalog.Sha256);
                    Equal(row.ProductId, profile.ProductId);
                    Equal(row.SupportSpp, profile.SupportSpp);
                    Equal(row.Raw.GetRawText(), profile.RawSource.GetRawText());
                    var caps = CapabilityResolver.Resolve(profile, CommandCapabilities.Parse(new byte[9]), new Dictionary<byte, byte>());
                    foreach (var feature in new[] { caps.HiRes, caps.Anc, caps.Equalizer, caps.CustomEqualizer,
                        caps.DualConnection, caps.BassEngine, caps.SpatialAudio, caps.LegacySpatialAudio,
                        caps.GameSound, caps.LegacyGameMode, caps.ModernGameMode })
                        Require(!feature.ProtocolWriteSupported, "Profile alone enabled a setter");
                    Require(profile.Uuid != Guid.Empty || !row.CanUseControlTransport, "Invalid UUID enabled transport");
                    if (!row.SupportSpp || row.ExternalOnly)
                        Require(!row.CanUseControlTransport, "SPP/external-only restriction was lost");
                    var knownVersions = new DeviceVersionContext("999.999.0", 999999999);
                    foreach (var mode in profile.GetEffectiveEqualizerModes(knownVersions))
                    {
                        Require(mode.ProtocolIndex is >= 0 and <= byte.MaxValue, "EQ protocol ID cannot be encoded");
                        Equal(VersionConditionStatus.Pass, mode.EvaluateConditionsForControl(knownVersions));
                    }
                    Require(profile.GetEffectiveEqualizerModes().All(x => x.EvaluateConditionsForControl() == VersionConditionStatus.Pass),
                        "Unknown App version enabled a conditional mode");
                }
                catch (Exception error)
                { throw new InvalidOperationException($"{source}/{row.ProductId} ({row.Name}): {error.Message}", error); }
            }
        }
    }

    internal static void MalformedOptionalMetadata()
    {
        var p = Profile("""
            {"id":"ABCDEF","name":null,"uuid":123,"supportSpp":1,"children":[null,7,{"name":123},{"name":"Alias"}],
             "function":{"highToneQuality":"1","equalizer":true,"spatialTypes":[null,"2",-1,{},2],
              "customEqualizer":1,"customEqFrequency":[31,"62",125],"customEqMax":-1,
              "noiseReductionMode":[null,{}, {"protocolIndex":"1","modeType":2},
               {"protocolIndex":1,"modeType":2,"childrenMode":[null,{"protocolIndex":256,"modeType":3},{"protocolIndex":2,"modeType":3}]}],
              "equalizerMode":[null,{}, {"protocolIndex":-1,"modeType":1},{"protocolIndex":256,"modeType":1},
               {"protocolIndex":2,"modeType":"1"},{"protocolIndex":3,"modeType":4,"future":{"retained":true}}],
              "equalizerModeCompat":{},"equalizerModeByVersion":"bad","equalizerModeByColor":[17],
              "gameSoundList":[null,{"type":-1},{"type":1,"packageNameList":[null,4,"game"]}],
              "multiConnectFunctions":[null,{"functionType":7},{"functionType":""},{"functionType":"safe","minFirmVersion":{}}]}}
            """);
        Equal("ABCDEF", p.Name); Equal(Guid.Empty, p.Uuid); Require(!p.SupportSpp, "Nonboolean SPP granted transport");
        Sequence(new[] { "Alias" }, p.Aliases); Sequence(new[] { 2 }, p.SpatialTypes);
        Equal(0, p.CustomEqFrequencies.Count); Equal(0, p.CustomEqualizerCapacity);
        Equal(1, p.NoiseReductionModes.Count); Equal(2, p.NoiseReductionModes[0].Children.Single().ProtocolIndex);
        Equal(3, p.EqualizerModes.Single().ProtocolIndex); Require(p.EqualizerModes[0].RawSource.GetProperty("future").GetProperty("retained").GetBoolean(), "Future metadata lost");
        Equal(0, p.EqualizerModeCompat.Count); Equal(0, p.EqualizerModeByVersion.Count); Equal(0, p.EqualizerModeByColor.Count);
        Sequence(new[] { "game" }, p.GameSoundModes.Single().PackageNames);
        Equal(VersionConditionStatus.Conditional, p.MultiConnectFunctions.Single().Conditions.Evaluate());
        var caps = CapabilityResolver.Resolve(p, CommandCapabilities.Parse(new byte[9]), new Dictionary<byte, byte>());
        Equal((bool?)null, caps.HiRes.StaticDeclared); Equal((bool?)null, caps.Equalizer.StaticDeclared);
        Require(!caps.Equalizer.ProtocolWriteSupported && !caps.Anc.ProtocolWriteSupported, "Malformed static metadata granted SET");

        foreach (string function in new[] { "null", "7", "true", "\"bad\"", "[]" })
        {
            var empty = Profile("{\"id\":\"ABCDEF\",\"function\":" + function + "}");
            Equal(0, empty.EqualizerModes.Count); Equal(0, empty.NoiseReductionModes.Count);
            Equal(0, empty.CustomEqFrequencies.Count); Equal((int?)null, empty.FunctionVersion("equalizer"));
        }
        foreach (string id in new[] { "", "abcdef", "ABCDE", "ABCDEG", " ABCDEF", "ABCDEF0" })
        {
            bool rejected = false;
            try { Profile(JsonSerializer.Serialize(new { id })); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Malformed product ID was accepted");
        }
        foreach (string capacity in new[] { "null", "\"3\"", "-1", "256", "2147483648", "[]" })
            Equal(0, Profile("{\"id\":\"ABCDEF\",\"function\":{\"customEqMax\":" + capacity + "}}").CustomEqualizerCapacity);
        Equal(3, Profile("{\"id\":\"ABCDEF\",\"function\":{\"customEqualizer\":1}}").CustomEqualizerCapacity);
        Sequence(new[] { 62, 250, 1000, 4000, 8000, 16000 }, Profile("{\"id\":\"ABCDEF\",\"function\":{\"customEqualizer\":1}}").CustomEqFrequencies);
    }

    internal static void CompatibilityEqualizers()
    {
        var bundles = new BundledResourceStore();
        foreach (var source in Enum.GetValues<ResourceSource>())
        {
            var catalog = bundles.GetCatalog(source);
            foreach (var (id, protocol, type, minimum) in new[] { ("068C10", 7, 34, 118), ("063C10", 4, 16, 112), ("062014", 7, 19, 165) })
            {
                var p = DeviceProfile.FromResource(catalog.Profiles[id].Raw, source.ToString());
                var mode = p.EqualizerModeCompat.Single(); Equal(protocol, mode.ProtocolIndex); Equal(type, mode.ModeType);
                Equal(VersionConditionStatus.Conditional, mode.EvaluateConditions());
                Equal(VersionConditionStatus.Fail, mode.EvaluateConditions(new((minimum - 1).ToString())));
                Equal(VersionConditionStatus.Pass, mode.EvaluateConditions(new(minimum.ToString())));
                Equal(VersionConditionStatus.Pass, mode.EvaluateConditions(new($"{minimum}.{minimum + 1}.0")));
                Require(p.GetEffectiveEqualizerModes().Any(x => x.ProtocolIndex == protocol), "Unknown firmware blocked compatibility EQ");
                Require(p.GetEffectiveEqualizerModes(new((minimum - 1).ToString())).Any(x => x.ProtocolIndex == protocol), "Old firmware blocked compatibility EQ");
                Equal(VersionConditionStatus.Conditional, mode.GetFirmwareIssues().Single().Status);
                Equal(VersionConditionStatus.Fail, mode.GetFirmwareIssues(new((minimum - 1).ToString())).Single().Status);
                Equal(type, p.GetEffectiveEqualizerModes(new(minimum.ToString())).Single(x => x.ProtocolIndex == protocol).ModeType);
                Equal(p.EqualizerModes.Count + 1, p.GetEffectiveEqualizerModes(new(minimum.ToString())).Count);
            }
            var free4Dynaudio = DeviceProfile.FromResource(catalog.Profiles["06C010"].Raw, source.ToString());
            Sequence(new[] { 0, 1, 2, 7, 3, 8 }, free4Dynaudio.GetEffectiveEqualizerModes(new("136.136.0", 15011000)).Select(x => x.ProtocolIndex));
            Sequence(new[] { 0, 1, 2, 7, 3, 8 }, free4Dynaudio.GetEffectiveEqualizerModes(new("135.136.0", 15011000)).Select(x => x.ProtocolIndex));
        }
    }

    internal static void VersionEqualizers()
    {
        var bundles = new BundledResourceStore();
        foreach (var source in Enum.GetValues<ResourceSource>())
        {
            var catalog = bundles.GetCatalog(source);
            foreach (string id in new[] { "06E010", "06E410" })
            {
                var p = DeviceProfile.FromResource(catalog.Profiles[id].Raw, source.ToString());
                var extra = p.EqualizerModeByVersion.Single(); Equal(1, extra.ProtocolIndex); Equal(41, extra.ModeType);
                foreach (var (firmware, app, expected) in new (string?, long?, VersionConditionStatus)[]
                {
                    ("130",16009000,VersionConditionStatus.Pass), ("131",16009001,VersionConditionStatus.Pass),
                    ("129",16009000,VersionConditionStatus.Fail), ("130",16008999,VersionConditionStatus.Fail),
                    ("129",null,VersionConditionStatus.Fail), (null,16008999,VersionConditionStatus.Fail),
                    (null,16009000,VersionConditionStatus.Conditional), ("130",null,VersionConditionStatus.Conditional),
                    (null,null,VersionConditionStatus.Conditional), ("bad",16009000,VersionConditionStatus.Conditional),
                    ("130",-1,VersionConditionStatus.Conditional), ("130.129.0",16009000,VersionConditionStatus.Fail),
                    ("0.130.0",16009000,VersionConditionStatus.Pass), ("130.0.0",16009000,VersionConditionStatus.Pass),
                })
                {
                    var context = new DeviceVersionContext(firmware, app);
                    Equal(expected, extra.EvaluateConditions(context));
                    Equal(app >= 16009000, p.GetEffectiveEqualizerModes(context).Any(x => x.ProtocolIndex == 1));
                    Sequence(p.EqualizerModes.Select(x => x.ProtocolIndex), p.GetEffectiveEqualizerModes(context).Where(x => x.ProtocolIndex != 1).Select(x => x.ProtocolIndex));
                }
            }
        }
    }

    internal static void ApkEqualizerOrderingAndColor()
    {
        var p = Profile("""
            {"id":"ABCDEF","function":{
             "equalizerModeCompat":[{"protocolIndex":7,"modeType":34,"minFirmVersion":118,"order":1}],
             "equalizerModeByColor":[{"protocolIndex":8,"modeType":80,"colorId":0,"order":1},
              {"protocolIndex":8,"modeType":81,"colorId":1,"order":1},{"protocolIndex":9,"modeType":9,"order":1}],
             "equalizerModeByVersion":[{"protocolIndex":1,"modeType":41,"minAppVersion":16009000,"order":1}],
             "equalizerMode":[{"protocolIndex":0,"modeType":26,"order":2},
              {"protocolIndex":7,"modeType":7,"order":1},{"protocolIndex":3,"modeType":3}]}}
            """);
        var context = new DeviceVersionContext("118", 16009000);
        // Stable ties retain APK insertion order. The APK does not replace or deduplicate protocol IDs.
        Sequence(new[] { 3, 34, 41, 7, 26 }, p.GetEffectiveEqualizerModes(context).Select(x => x.ModeType));
        Sequence(new[] { 3, 34, 80, 41, 7, 26 }, p.GetEffectiveEqualizerModes(context, 0).Select(x => x.ModeType));
        Sequence(new[] { 3, 34, 81, 41, 7, 26 }, p.GetEffectiveEqualizerModes(context, 1).Select(x => x.ModeType));
        Sequence(new[] { 3, 34, 41, 7, 26 }, p.GetEffectiveEqualizerModes(context, 2).Select(x => x.ModeType));
        Equal(2, p.GetEffectiveEqualizerModes(context).Count(x => x.ProtocolIndex == 7));
        Sequence(new[] { 0, 7, 3 }, p.EqualizerModes.Select(x => x.ProtocolIndex));
        Equal("equalizerModeCompat", p.EqualizerModeCompat.Single().SourceArray);
        Equal("equalizerModeByVersion", p.EqualizerModeByVersion.Single().SourceArray);
        var readonlyModes = (System.Collections.Generic.IList<EqualizerMode>)p.GetEffectiveEqualizerModes(context);
        bool rejected = false; try { readonlyModes.Clear(); } catch (NotSupportedException) { rejected = true; }
        Require(rejected, "Effective EQ list is mutable");
    }

    internal static void FirmwareMarkerAndMalformedConditions()
    {
        foreach (var (firmware, expected) in new[] { ("118", 118), ("118.119.7", 118), ("119.118.7", 118),
            ("0.118.0",118), ("118.0.0",118), ("0.0.0",0), ("118.1",118), ("118.1.2.3",118), ("118.119.build",118), ("119.118.",119) })
        { Require(EqualizerVersionConditionEvaluator.TryGetFirmwareMarker(firmware, out int actual), "Valid vendor firmware marker rejected"); Equal(expected, actual); }
        foreach (string? firmware in new[] { null, "", ".", "V118", "-118", "118.bad.0", "2147483648", " .118.0" })
            Require(!EqualizerVersionConditionEvaluator.TryGetFirmwareMarker(firmware, out _), "Invalid firmware marker accepted");
        foreach (string minimum in new[] { "null", "\"118\"", "\"1.2\"", "-1", "2147483648", "{}" })
        {
            var mode = Profile("{\"id\":\"ABCDEF\",\"function\":{\"equalizerModeCompat\":[{\"protocolIndex\":7,\"modeType\":34,\"minFirmVersion\":" + minimum + "}]}}");
            Equal(VersionConditionStatus.Conditional, mode.EqualizerModeCompat.Single().EvaluateConditions(new("999", 999999999)));
            Equal(1, mode.GetEffectiveEqualizerModes(new("999", 999999999)).Count);
            Equal(VersionConditionStatus.Conditional, mode.EqualizerModeCompat.Single().GetFirmwareIssues(new("999", 999999999)).Single().Status);
        }
        foreach (string minimum in new[] { "null", "\"16009000\"", "-1", "9223372036854775808", "{}" })
        {
            var mode = Profile("{\"id\":\"ABCDEF\",\"function\":{\"equalizerModeByVersion\":[{\"protocolIndex\":1,\"modeType\":41,\"minAppVersion\":" + minimum + "}]}}");
            Equal(VersionConditionStatus.Conditional, mode.EqualizerModeByVersion.Single().EvaluateConditions(new("999", 999999999)));
            Equal(0, mode.GetEffectiveEqualizerModes(new("999", 999999999)).Count);
        }
        var noMinimum = Profile("""
            {"id":"ABCDEF","function":{"equalizerModeByVersion":[{"protocolIndex":1,"modeType":41,"minFirmVersion":0,"minAppVersion":0}],
             "equalizerModeCompat":[{"protocolIndex":7,"modeType":34,"minAppVersion":99999999}],
             "equalizerMode":[{"protocolIndex":0,"modeType":26,"minAppVersion":99999999}]}}
            """);
        Sequence(new[] { 7, 1, 0 }, noMinimum.GetEffectiveEqualizerModes().Select(x => x.ProtocolIndex));
        // The app marker is only a selection branch for equalizerModeByVersion in the supplied APKs.
        Equal(VersionConditionStatus.Pass, noMinimum.EqualizerModes.Single().EvaluateConditions(new(AndroidAppVersionMarker: 1)));
        Equal(VersionConditionStatus.Fail, noMinimum.EqualizerModes.Single().Conditions.Evaluate(new(AndroidAppVersionMarker: 1)));
    }

    private static DeviceProfile Profile(string json)
    { using var document = JsonDocument.Parse(json); return DeviceProfile.FromResource(document.RootElement, "test"); }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    { Require(expected.SequenceEqual(actual), $"Expected [{string.Join(',', expected)}]; got [{string.Join(',', actual)}]"); }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}"); }
}
