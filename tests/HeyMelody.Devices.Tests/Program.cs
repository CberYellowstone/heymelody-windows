using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--verify-source")
    {
        Console.Error.WriteLine("Usage: HeyMelody.Devices.Tests [--verify-source <original-evidence.zip>]");
        return 2;
    }
    try { SourceArchive(Path.GetFullPath(args[1])); Console.WriteLine("PASS original ZIP member matches pinned source and embedded asset"); return 0; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL source provenance: {error.Message}"); return 1; }
}

var tests = new (string Name, Action Body)[]
{
    ("Pinned source asset equals embedded resource and recorded hash", Asset),
    ("Exact product identity has no name or alias fallback", Identity),
    ("All X4 projections preserve values, order and raw fields", Metadata),
    ("Sparse EQ and unknown ANC children use protocol IDs", Modes),
    ("Metadata is detached and read-only", Isolation),
    ("Version conditions preserve domains and three outcomes", Versions),
    ("Read/write command support gates independently", CommandGates),
    ("Unknown product cannot be inferred from commands", UnknownModel),
    ("Missing and unknown states differ from false", States),
    ("Spatial routes remain distinct despite absent highAudio", Spatial),
    ("Legacy and modern game candidates follow reported features", Games),
    ("Game sound uses dedicated commands and original type/mutex lists", GameSound),
    ("JSON projection is complete and serializable", JsonProjection),
};
int failed = 0;
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string reason)
{ if (!condition) throw new InvalidOperationException(reason); }
static void Eq<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}");
static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Check(expected.SequenceEqual(actual), "Sequence mismatch");
static DeviceCapabilities Resolve(Dictionary<byte, byte>? states = null, params int[] bits) => CapabilityResolver.Resolve(X4Profile.Load(), Commands(bits), states ?? []);
static CommandCapabilities Commands(params int[] bits)
{
    byte[] bitmap = new byte[9];
    foreach (int bit in bits) bitmap[bit / 8] |= (byte)(1 << (bit % 8));
    return CommandCapabilities.Parse(bitmap);
}
static string ProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null)
    { if (File.Exists(Path.Combine(directory.FullName, "src/HeyMelody.Devices/Assets/oppo-enco-x4.json"))) return directory.FullName; directory = directory.Parent; }
    throw new InvalidOperationException("Cannot locate repository asset for profile tests");
}
static void Asset()
{
    string root = ProjectRoot();
    byte[] copy = File.ReadAllBytes(Path.Combine(root, "src/HeyMelody.Devices/Assets/oppo-enco-x4.json"));
    Eq(X4Profile.AssetSha256, Convert.ToHexString(SHA256.HashData(copy)));
    using var embedded = typeof(X4Profile).Assembly.GetManifestResourceStream("HeyMelodyWindows.Devices.Assets.oppo-enco-x4.json")!;
    using var data = new MemoryStream(); embedded.CopyTo(data); Sequence(copy, data.ToArray());
    Eq(X4Profile.AssetSha256, Convert.ToHexString(SHA256.HashData(data.ToArray())));
    Eq(X4Profile.AssetSha256, X4Profile.Load().Provenance.Sha256);
    Eq(X4Profile.ArchiveMember, X4Profile.Load().Provenance.ArchiveMember);
}
static void SourceArchive(string path)
{
    Asset();
    using var archive = ZipFile.OpenRead(path);
    using var source = (archive.GetEntry(X4Profile.ArchiveMember)
        ?? throw new InvalidDataException($"Missing evidence member: {X4Profile.ArchiveMember}")).Open();
    using var buffer = new MemoryStream(); source.CopyTo(buffer);
    byte[] original = buffer.ToArray();
    Eq(X4Profile.AssetSha256, Convert.ToHexString(SHA256.HashData(original)));
    Sequence(original, File.ReadAllBytes(Path.Combine(ProjectRoot(), "src/HeyMelody.Devices/Assets/oppo-enco-x4.json")));
}
static void Identity()
{
    Check(ReferenceEquals(X4Profile.Load(), DeviceModelResolver.FindByProductId("06EC10")), "Known product must load pinned profile");
    foreach (var id in new string?[] { null, "", "OPPO Enco X4", "OPPO Enco Bohr", "Enco X", "06ec10", "006EC10", " 06EC10", "06EC11" })
        Check(DeviceModelResolver.FindByProductId(id) is null, "Product resolution guessed a name or malformed ID");
}
static void Metadata()
{
    var p = X4Profile.Load();
    Sequence(new[] { "OPPO Enco Bohr" }, p.Aliases);
    Eq(Guid.Parse("0000079A-D102-11E1-9B23-00025B00A5A5"), p.Uuid); Check(p.SupportSpp, "SPP lost");
    Eq((long?)16010000, p.MinimumAndroidAppVersionMarker);
    Eq((int?)1, p.HighToneQuality); Eq((int?)null, p.HighAudio); Eq((int?)1, p.SpatialHiResMutex);
    Sequence(new[] {0,1,2}, p.SpatialTypes);
    Sequence(new[] {31,62,125,250,500,1000,2000,4000,8000,16000}, p.CustomEqFrequencies);
    Eq((int?)1, p.GameMode); Eq((int?)2, p.MultiDevicesConnect);
    Sequence(new[] {3,0}, p.GameSoundModes.Select(x=>x.Type)); Sequence(new[] {1,3}, p.GameSoundMutexes);
    Eq(13, p.GameSoundModes[0].PackageNames.Count); Eq(0, p.GameSoundModes[1].PackageNames.Count);
    Sequence(new[] {"displayPairedDevice","connectDisconnectDevice","unpairDevice","setPriorityDevice"}, p.MultiConnectFunctions.Select(x=>x.FunctionType));
    foreach (var function in p.MultiConnectFunctions)
    { Eq(0, function.RawSource.GetProperty("minFirmVersion").GetInt32()); Eq(VersionConditionStatus.Pass, function.Conditions.Evaluate()); }
    using var source = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProjectRoot(), "src/HeyMelody.Devices/Assets/oppo-enco-x4.json")));
    Eq(source.RootElement.GetRawText(), p.RawSource.GetRawText());
    Eq(source.RootElement.GetProperty("function").GetRawText(), p.RawFunctions.GetRawText());
    Check(p.RawFunctions.TryGetProperty("control", out _) && p.RawFunctions.TryGetProperty("callControl", out _), "Unprojected fields lost");
}
static void Modes()
{
    var p = X4Profile.Load();
    Sequence(new[] {0,7,2,1,3}, p.EqualizerModes.Select(x=>x.ProtocolIndex));
    Sequence(new[] {42,43,28,46,29}, p.EqualizerModes.Select(x=>x.ModeType));
    Sequence(new[] {11,12,3,2,4}, p.EqualizerModes.Select(x=>x.SummaryId!.Value));
    Check(p.EqualizerModes.All(x=>x.Tag == 3), "EQ tags changed");
    Eq(43, p.FindEqualizerByProtocolId(7)!.ModeType); Eq(46, p.FindEqualizerByProtocolId(1)!.ModeType);
    Check(p.FindEqualizerByProtocolId(4) is null, "Sparse ID was treated as array index");
    Sequence(new[] {1,11,0,2}, p.NoiseReductionModes.Select(x=>x.ProtocolIndex));
    Sequence(new[] {5,10,1,2}, p.NoiseReductionModes.Select(x=>x.ModeType));
    Sequence(new[] {7,4,5,6}, p.NoiseReductionModes[0].Children.Select(x=>x.ProtocolIndex));
    Sequence(new[] {7,4,8,3}, p.NoiseReductionModes[0].Children.Select(x=>x.ModeType));
    Sequence(new[] {12,13,14}, p.NoiseReductionModes[1].Children.Select(x=>x.ProtocolIndex));
    Sequence(new[] {11,12,13}, p.NoiseReductionModes[1].Children.Select(x=>x.ModeType));
    foreach (int index in new[] {12,13,14})
    { var mode = p.FindNoiseReductionByProtocolId(index)!; Eq(index, mode.RawSource.GetProperty("protocolIndex").GetInt32()); Eq(index-1, mode.ModeType); }
    Eq(3, p.NoiseReductionModes[2].Children.Single().ProtocolIndex);
    Eq(8, p.NoiseReductionModes[3].Children.Single().ProtocolIndex);
}
static void Isolation()
{
    var profile = X4Profile.Load();
    var editable = JsonNode.Parse(profile.RawSource.GetRawText())!;
    editable["function"]!["highToneQuality"] = 99;
    editable["function"]!["equalizerMode"]![0]!["protocolIndex"] = 99;
    Eq(1, profile.RawFunctions.GetProperty("highToneQuality").GetInt32()); Eq(0, profile.EqualizerModes[0].ProtocolIndex);
    var list = (IList<int>)profile.SpatialTypes;
    try { list[0] = 99; throw new InvalidOperationException("Read-only list allowed mutation"); } catch (NotSupportedException) { }
    foreach (var type in new[] {typeof(DeviceProfile),typeof(EqualizerMode),typeof(NoiseReductionMode),typeof(MultiConnectFunction),typeof(VersionConditions)})
        Check(type.GetProperties().All(x=>x.SetMethod is null), "Metadata contains a setter");
    VersionConditions detached;
    using (var document = JsonDocument.Parse("{\"minFirmVersion\":120,\"future\":{\"values\":[7,9]}}"))
        detached = VersionConditions.FromSource(document.RootElement);
    Eq(9, detached.RawSource.GetProperty("future").GetProperty("values")[1].GetInt32());
    Eq(120, detached.MinimumFirmware!.Value.GetInt32());
}
static VersionConditions Conditions(string json)
{ using var document = JsonDocument.Parse(json); return VersionConditions.FromSource(document.RootElement); }
static void Versions()
{
    var p = X4Profile.Load();
    Eq(VersionConditionStatus.Conditional, p.Conditions.Evaluate());
    Eq(VersionConditionStatus.Conditional, p.Conditions.Evaluate(new("99999999")));
    Eq(VersionConditionStatus.Pass, p.Conditions.Evaluate(new(AndroidAppVersionMarker:16010000)));
    Eq(VersionConditionStatus.Fail, p.Conditions.Evaluate(new(AndroidAppVersionMarker:16009999)));
    var minimum = Conditions("{\"minFirmVersion\":120,\"minAppVersion\":16010000,\"futureFlag\":1}");
    Eq(VersionConditionStatus.Pass, minimum.Evaluate(new("121",16010000)));
    Eq(VersionConditionStatus.Fail, minimum.Evaluate(new("119",16010000)));
    Eq(VersionConditionStatus.Conditional, minimum.Evaluate(new(AndroidAppVersionMarker:16010000)));
    Eq(VersionConditionStatus.Conditional, minimum.Evaluate(new("1.2.1",16010000)));
    Eq(VersionConditionStatus.Fail, minimum.Evaluate(new("121",1)));
    var dotted = Conditions("{\"minFirmware\":\"1.2\"}");
    Eq(VersionConditionStatus.Pass, dotted.Evaluate(new("1.2.0")));
    Eq(VersionConditionStatus.Fail, dotted.Evaluate(new("1.1.99")));
    Eq(VersionConditionStatus.Conditional, dotted.Evaluate(new("V1.3")));
    Eq(VersionConditionStatus.Pass, Conditions("{\"minFirmVersion\":0}").Evaluate());
    Eq(VersionConditionStatus.Conditional, Conditions("{\"minAppVersion\":\"unknown\"}").Evaluate(new("999",99999999)));
    Eq(VersionConditionStatus.Conditional, Conditions("{\"minFirmVersion\":{\"unknown\":true}}").Evaluate(new("999")));
    Eq(2, Conditions("{\"minFirmware\":120,\"minFirmVersion\":122}").Requirements.Count);
    Eq(VersionConditionStatus.Fail, Conditions("{\"minFirmware\":120,\"minFirmVersion\":122}").Evaluate(new("121")));
}
static void CommandGates()
{
    var empty = Resolve();
    Check(empty.HiRes.StaticDeclared == true && empty.HiRes.ProtocolReadSupported && !empty.HiRes.ProtocolWriteSupported, "HiRes write inferred from static/read support");
    Check(!empty.Anc.ProtocolReadSupported && !empty.Anc.ProtocolWriteSupported, "ANC capability inferred from profile");
    var genericSetter = Resolve(null,7);
    Check(genericSetter.HiRes.ProtocolWriteSupported, "HiRes setter capability ignored");
    Check(!genericSetter.BassEngine.ProtocolWriteSupported, "Generic feature setter incorrectly used for bass");
    var eqOnly = Resolve(null,10);
    Check(eqOnly.Equalizer.ProtocolReadSupported && eqOnly.Equalizer.ProtocolWriteSupported, "EQ mapped commands missing");
    Check(!eqOnly.CustomEqualizer.ProtocolReadSupported, "Preset EQ inferred custom EQ");
    var independent = Resolve(null,8,34,38,47,49);
    Check(independent.Anc.ProtocolReadSupported && independent.CustomEqualizer.ProtocolWriteSupported && independent.BassEngine.ProtocolReadSupported, "Independent commands lost");
    Check(independent.SpatialAudio.ProtocolReadSupported && independent.GameSound.ProtocolWriteSupported, "Dedicated space/sound commands missing");
    Eq((ushort)0x010C, independent.Anc.ReadCommand); Eq((ushort)0x0404, independent.Anc.WriteCommand);
    Eq((ushort)0x010F, independent.Equalizer.ReadCommand); Eq((ushort)0x0406, independent.Equalizer.WriteCommand);
    Eq((ushort)0x0122, independent.CustomEqualizer.ReadCommand); Eq((ushort)0x0418, independent.CustomEqualizer.WriteCommand);
    Eq((ushort)0x0124, independent.BassEngine.ReadCommand); Eq((ushort)0x041B, independent.BassEngine.WriteCommand);
    Eq((byte?)0x11, independent.DualConnection.FeatureId);
}
static void UnknownModel()
{
    var caps = CapabilityResolver.Resolve(null,Commands(7,8,10,34,38,47,49),new Dictionary<byte,byte>{{0x18,1}});
    Eq((string?)null, caps.ProductId); Eq((bool?)null, caps.HiRes.StaticDeclared); Eq((bool?)null, caps.Anc.StaticDeclared);
    Eq((bool?)null, caps.GameMode.StaticDeclared); Eq((bool?)null, caps.LegacySpatialAudio.StaticDeclared);
    Check(caps.HiRes.ProtocolWriteSupported, "Unknown product discarded observed command metadata");
    Eq(0, caps.SpatialTypes.Count); Eq(0, caps.GameSoundTypes.Count);
    Eq((bool?)null, caps.SpatialHiResMutex); Eq(VersionConditionStatus.Conditional, caps.ProfileVersionCondition);
}
static void States()
{
    Eq((byte?)null, Resolve().HiRes.RawState); Eq((bool?)null, Resolve().HiRes.State);
    foreach (byte raw in new byte[] {0,1,2,255})
    {
        var states = new Dictionary<byte,byte>{{0x18,raw}};
        var capability = Resolve(states).HiRes;
        Eq((byte?)raw,capability.RawState); Eq(raw switch {0=>(bool?)false,1=>true,_=>null},capability.State);
        states[0x18] = 99; Eq((byte?)raw,capability.RawState);
    }
    Check(Resolve(new(){{0xFE,1}}).HiRes.State is null, "Unknown feature polluted HiRes");
}
static void Spatial()
{
    var caps = Resolve(new(){{0x1B,1}},7,47);
    Eq((int?)null, X4Profile.Load().HighAudio);
    Eq((bool?)true,caps.LegacySpatialAudio.StaticDeclared);
    Eq((byte?)0x1B,caps.LegacySpatialAudio.FeatureId); Eq((bool?)true,caps.LegacySpatialAudio.State);
    Eq((ushort)0x010D,caps.LegacySpatialAudio.ReadCommand); Eq((ushort)0x0403,caps.LegacySpatialAudio.WriteCommand);
    Eq((ushort)0x012A,caps.SpatialAudio.ReadCommand); Eq((ushort)0x0422,caps.SpatialAudio.WriteCommand);
    Eq((byte?)null,caps.SpatialAudio.FeatureId); Eq((bool?)null,caps.SpatialAudio.State);
    Sequence(new[] {0,1,2},caps.SpatialTypes); Eq((bool?)true,caps.SpatialHiResMutex);
    Check(!Resolve(null,7).SpatialAudio.ProtocolReadSupported, "Legacy setter inferred new space command");
    Check(Resolve(null,47).SpatialAudio.ProtocolWriteSupported && !Resolve(null,47).LegacySpatialAudio.ProtocolWriteSupported, "New/old space routes combined");
}
static void Games()
{
    var none = Resolve(null,7);
    Eq(GameModeSelection.Unobserved,none.GameModeSelection); Eq((byte?)null,none.GameMode.FeatureId); Eq((bool?)null,none.GameMode.State);
    Eq((byte?)0x06,none.LegacyGameMode.FeatureId); Eq((byte?)0x28,none.ModernGameMode.FeatureId);
    foreach (var candidate in new[] {none.LegacyGameMode,none.ModernGameMode})
    { Eq((bool?)true,candidate.StaticDeclared); Eq((ushort)0x010D,candidate.ReadCommand); Eq((ushort)0x0403,candidate.WriteCommand); }
    var legacy = Resolve(new(){{0x06,1}},7);
    Eq(GameModeSelection.LegacyReported,legacy.GameModeSelection); Eq((byte?)0x06,legacy.GameMode.FeatureId); Eq((bool?)true,legacy.GameMode.State);
    var modern = Resolve(new(){{0x28,0}},7);
    Eq(GameModeSelection.ModernReported,modern.GameModeSelection); Eq((byte?)0x28,modern.GameMode.FeatureId); Eq((bool?)false,modern.GameMode.State);
    var both = Resolve(new(){{0x06,1},{0x28,0}},7);
    Eq((bool?)true,both.LegacyGameMode.State); Eq((bool?)false,both.ModernGameMode.State); Eq((bool?)false,both.GameMode.State);
    foreach (byte unknown in new byte[] {2,255})
    { var c=Resolve(new(){{0x28,unknown}},7); Eq((byte?)unknown,c.GameMode.RawState); Eq((bool?)null,c.GameMode.State); }
    Eq(GameModeSelection.Unobserved,Resolve(new(){{0x18,1}}).GameModeSelection);
}
static void GameSound()
{
    var generic = Resolve(new(){{0x28,1}},7);
    Eq((ushort)0x012B,generic.GameSound.ReadCommand); Eq((ushort)0x0423,generic.GameSound.WriteCommand);
    Eq((byte?)null,generic.GameSound.FeatureId); Eq((byte?)null,generic.GameSound.RawState); Eq((bool?)null,generic.GameSound.State);
    Check(!generic.GameSound.ProtocolReadSupported && !generic.GameSound.ProtocolWriteSupported, "Modern game feature became game sound");
    var dedicated = Resolve(null,49);
    Check(dedicated.GameSound.ProtocolReadSupported && dedicated.GameSound.ProtocolWriteSupported, "Game sound dedicated command support lost");
    Check(!dedicated.ModernGameMode.ProtocolWriteSupported, "Game sound commands inferred game mode setter");
    Sequence(new[] {3,0},dedicated.GameSoundTypes); Sequence(new[] {1,3},dedicated.GameSoundMutexes);
    Check(!dedicated.GameSoundMutexes.Contains(2), "Space mutual exclusion invented");
}
static void JsonProjection()
{
    var model = JsonSerializer.SerializeToElement(new {profile=X4Profile.Load(),capabilities=Resolve(new(){{0x18,1}}),settingsTransmissionEnabled=false});
    Eq("06EC10",model.GetProperty("profile").GetProperty("ProductId").GetString());
    Eq("Conditional",model.GetProperty("capabilities").GetProperty("ProfileVersionCondition").GetString());
    Check(model.GetProperty("profile").GetProperty("RawSource").TryGetProperty("rssi",out _), "Raw profile absent from projection");
    int bytes=System.Text.Encoding.UTF8.GetByteCount(model.GetRawText());
    Check(bytes<512*1024,"Projection exceeds model size limit");
    Console.WriteLine($"model JSON bytes: {bytes}");
}
