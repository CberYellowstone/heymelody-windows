using System.Text.Json;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Resources;

internal static class AudioFeatureCompatibilityTests
{
    private static DeviceProfile Profile(string functions, string extra = "") => DeviceProfile.FromResource(
        JsonDocument.Parse("{\"id\":\"06EC10\",\"name\":\"Fixture\",\"uuid\":\"00001101-0000-1000-8000-00805f9b34fb\",\"supportSpp\":true," + extra + "\"function\":" + functions + "}").RootElement,
        "audio-compatibility-fixture");
    private static DailyFeatureControl? Control(DailySnapshot snapshot, string id) =>
        snapshot.FeatureSections.SelectMany(section => section.Controls).SingleOrDefault(control => control.Id == id);
    private static byte[] Bitmap(params int[] bits)
    {
        byte[] bitmap = new byte[9];
        foreach (int bit in bits) bitmap[bit / 8] |= (byte)(1 << (bit % 8));
        return bitmap;
    }
    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        void Check(bool value, string message = "audio compatibility assertion") => check(value, message);
        var bundles = new BundledResourceStore();
        foreach (var source in Enum.GetValues<ResourceSource>())
        {
            var catalog = bundles.GetCatalog(source);
            await test($"{source} complete bundled profiles preserve declared audio controls and target-only SETs", async () =>
            {
                int gameProfiles = 0, hiresProfiles = 0, spatialProfiles = 0;
                foreach (var resource in catalog.Profiles.Values)
                {
                    var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                    var fake = new Fake { ProductId = profile.ProductId };
                    fake.Features[0x27] = 0;
                    if (profile.GameSoundModes.Count > 0)
                        fake.GameSoundTypes = profile.GameSoundModes.Select(mode => checked((byte)mode.Type)).Distinct().ToArray();
                    await using var client = new DailyClient(fake, id => resource.CanUseControlTransport && id == profile.ProductId ? profile : null);
                    var snapshot = await client.RefreshAsync();
                    string context = $"{source}/{profile.ProductId}/{profile.Name}";
                    if (!resource.CanUseControlTransport)
                    {
                        Check(!snapshot.ProfileVerified && snapshot.FeatureSections.Count == 0 && fake.Writes.Count == 0, context);
                        continue;
                    }
                    Check(snapshot.ProfileVerified, context);
                    Check((Control(snapshot, "game-sound") is not null) == (profile.GameSoundModes.Count > 0), context);
                    Check((Control(snapshot, "hires") is not null) == (profile.HighToneQuality > 0), context);
                    Check((Control(snapshot, "spatial") is not null) == (profile.SpatialTypes.Count > 0), context);
                    if (profile.GameSoundModes.Count > 0)
                    {
                        gameProfiles++;
                        var type = Control(snapshot, "game-sound")!;
                        Check(type.CanWrite && Control(snapshot, "game-sound-enabled")!.CanWrite, context);
                        Check(type.Options.Select(option => option.Value).SequenceEqual(profile.GameSoundModes.Select(mode => mode.Type)), context);
                        Check(type.Options.Single(option => option.Value == 0).Name == "关闭", context);
                        Check(type.Options.Single(option => option.Value != 0).Name == (profile.ProductId == "063C14"
                            ? "和平精英职业联赛游戏音效" : "射击类游戏音效"), context);
                        byte enabledType = fake.GameSoundTypes.First(value => value != 0);
                        var enabled = await client.ApplyFeatureAsync("game-sound-enabled", 1);
                        Check(enabled.Success && fake.Writes.Single().Command == 0x0403
                            && fake.Writes.Single().Payload.SequenceEqual(new byte[] {0x27,1}), context);
                        var selected = await client.ApplyFeatureAsync("game-sound", enabledType);
                        Check(selected.Success && fake.Writes.Count == 2 && fake.Writes.Last().Command == 0x0423
                            && fake.Writes.Last().Payload.SequenceEqual(new byte[] {enabledType,1}) && fake.Features[0x27] == 1, context);
                        var offType = await client.ApplyFeatureAsync("game-sound", 0);
                        Check(offType.Success && fake.Writes.Count == 3
                            && fake.Writes.Last().Payload.SequenceEqual(new byte[] {0,1}) && fake.Features[0x27] == 1, context);
                        var disabled = await client.ApplyFeatureAsync("game-sound-enabled", 0);
                        Check(disabled.Success && fake.Writes.Count == 4 && fake.Writes.Last().Command == 0x0403
                            && fake.Writes.Last().Payload.SequenceEqual(new byte[] {0x27,0}), context);
                        fake.Writes.Clear();
                    }
                    if (profile.SpatialTypes.Count > 0)
                    {
                        spatialProfiles++;
                        byte mode = (byte)profile.SpatialTypes.Max();
                        var spatial = await client.SetSpatialModeAsync(mode);
                        Check(spatial.Success && !spatial.PrerequisiteSubmitted && fake.Writes.Single().Command == 0x0422
                            && fake.Writes.Single().Payload.SequenceEqual(new byte[] {mode}) && spatial.After?.HiRes == true, context);
                        fake.Writes.Clear();
                    }
                    if (profile.HighToneQuality > 0)
                    {
                        hiresProfiles++;
                        fake.Features[0x18] = 0;
                        int? spatialBefore = (await client.RefreshAsync()).SpatialMode;
                        var hires = await client.SetHiResAsync(true, true);
                        Check(hires.Success && !hires.PrerequisiteSubmitted && fake.Writes.Single().Command == 0x0403
                            && fake.Writes.Single().Payload.SequenceEqual(new byte[] {0x18,1})
                            && hires.After?.SpatialMode == spatialBefore, context);
                    }
                }
                // The wireless catalog also inherits OnePlus Nord Newton (065814)
                // from its parent profile; it is a separate controllable product ID.
                Check(gameProfiles == 17 && hiresProfiles == 28 && spatialProfiles == (source == ResourceSource.HeyMelody ? 48 : 50),
                    $"{source} declared profile totals: game={gameProfiles}, HiRes={hiresProfiles}, spatial={spatialProfiles}");
            });
            await test($"{source} all spatial profiles preserve legacy feature1B route and reject head tracking", async () =>
            {
                foreach (var resource in catalog.Profiles.Values.Where(resource => resource.CanUseControlTransport))
                {
                    var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                    if (profile.SpatialTypes.Count == 0) continue;
                    var fake = new Fake { ProductId = profile.ProductId, NoModernSpatial = true };
                    await using var client = new DailyClient(fake, _ => profile);
                    var snapshot = await client.RefreshAsync();
                    Check(snapshot.Capabilities.SpatialRoute == "legacy" && Control(snapshot, "spatial")!.Options.All(option => option.Value <= 1),
                        $"{source}/{profile.ProductId} legacy route");
                    var result = await client.SetSpatialModeAsync(1);
                    Check(result.Success && fake.Writes.Single().Command == 0x0403
                        && fake.Writes.Single().Payload.SequenceEqual(new byte[] {0x1B,1}) && result.After?.HiRes == true,
                        $"{source}/{profile.ProductId} legacy SET");
                    Check(!(await client.SetSpatialModeAsync(2)).SubmissionAttempted && fake.Writes.Count == 1);
                }
            });
        }
        await test("source differences remain whole-source for BudsPro2 spatial and version2 HiRes", async () =>
        {
            foreach (var source in Enum.GetValues<ResourceSource>())
            {
                var catalog = bundles.GetCatalog(source);
                foreach (string id in new[] {"062014", "06EC10", "067814"})
                {
                    var resource = catalog.Find(id)!;
                    var profile = DeviceProfile.FromResource(resource.Raw, source.ToString(), catalog.Sha256);
                    var fake = new Fake { ProductId = id };
                    await using var client = new DailyClient(fake, _ => profile);
                    var snapshot = await client.RefreshAsync();
                    if (id == "062014")
                    {
                        Check((Control(snapshot, "spatial") is not null) == (source == ResourceSource.WirelessEarphones));
                        if (source == ResourceSource.HeyMelody)
                            Check(!(await client.SetSpatialModeAsync(1)).SubmissionAttempted && fake.Writes.Count == 0);
                    }
                    else Check(profile.HighToneQuality == (source == ResourceSource.HeyMelody ? 1 : 2)
                        && Control(snapshot, "hires")!.CanWrite);
                }
            }
        });
        await test("game switch remains writable without dedicated type command or readback", async () =>
        {
            foreach (bool malformedType in new[] {false, true})
            {
                var fake = new Fake { CapabilityOverride = malformedType ? Bitmap(0,7,49) : Bitmap(0,7) };
                fake.Features[0x27] = 0;
                if (malformedType) fake.ReadReplies[0x012B] = [0];
                await using var client = new DailyClient(fake, _ => Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
                var snapshot = await client.RefreshAsync();
                Check(Control(snapshot, "game-sound-enabled")!.CanWrite && !Control(snapshot, "game-sound")!.CanWrite);
                var result = await client.ApplyFeatureAsync("game-sound-enabled", 1);
                Check(result.Success && fake.Writes.Single().Command == 0x0403
                    && fake.Writes.Single().Payload.SequenceEqual(new byte[] {0x27,1}));
            }
        });
        await test("game type is independent of feature27 and general SET capability", async () =>
        {
            foreach (byte? state in new byte?[] {null, 7, 0})
            {
                var fake = new Fake { CapabilityOverride = Bitmap(0,49), GameSoundType = 250 };
                if (state is { } value) fake.Features[0x27] = value;
                await using var client = new DailyClient(fake, _ => Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
                var snapshot = await client.RefreshAsync();
                Check(Control(snapshot, "game-sound")!.CanWrite && Control(snapshot, "game-sound")!.Value == 250
                    && !Control(snapshot, "game-sound-enabled")!.CanWrite);
                var result = await client.ApplyFeatureAsync("game-sound", 3);
                Check(result.Success && result.ReadbackMatches == true && fake.Writes.Single().Command == 0x0423
                    && fake.Writes.Single().Payload.SequenceEqual(new byte[] {3,1}));
                Check(state is null ? !fake.Features.ContainsKey(0x27) : fake.Features[0x27] == state);
            }
        });
        await test("game type readback accepts an independently changed switch", async () =>
        {
            var fake = new Fake { GameSoundFeatureAfterTypeSet = 1 }; fake.Features[0x27] = 0;
            await using var client = new DailyClient(fake, _ => Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
            var result = await client.ApplyFeatureAsync("game-sound", 3);
            Check(result.Success && result.After?.RawFeatures[0x27] == 1 && result.After.GameSound?.SelectedType == 3
                && fake.Writes.Single().Command == 0x0423);
        });
        await test("game capabilities require their own states source versions and type intersection", async () =>
        {
            foreach (byte? state in new byte?[] {null, 7})
            {
                var fake = new Fake(); if (state is { } value) fake.Features[0x27] = value;
                await using var client = new DailyClient(fake, _ => Profile("{\"gameSoundList\":[{\"type\":3}]}"));
                Check(!(await client.ApplyFeatureAsync("game-sound-enabled", 1)).SubmissionAttempted && fake.Writes.Count == 0);
            }
            foreach (string extra in new[] {"\"minFirmwareVersion\":\"999\",", "\"minVersion\":99999999,"})
            {
                var fake = new Fake(); fake.Features[0x27] = 0;
                await using var client = new DailyClient(fake, _ => Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":3}]}", extra));
                Check(!(await client.ApplyFeatureAsync("game-sound-enabled", 1)).SubmissionAttempted
                    && !(await client.ApplyFeatureAsync("game-sound", 3)).SubmissionAttempted && fake.Writes.Count == 0);
            }
            var restricted = new Fake { GameSoundTypes = [0,3,7] }; restricted.Features[0x27] = 0;
            await using var filtered = new DailyClient(restricted, _ => Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":1},{\"type\":3,\"minFirmVersion\":999}]}"));
            var filteredSnapshot = await filtered.RefreshAsync();
            Check(Control(filteredSnapshot, "game-sound")!.Options.Select(option => option.Value).SequenceEqual(new[] {0}));
            Check(!(await filtered.ApplyFeatureAsync("game-sound", 3)).SubmissionAttempted
                && !(await filtered.ApplyFeatureAsync("game-sound", 7)).SubmissionAttempted && restricted.Writes.Count == 0);
            Check((await filtered.ApplyFeatureAsync("game-sound-enabled", 1)).Success && restricted.Writes.Single().Command == 0x0403);
        });
        await test("spatial and HiRes require only their own known state for modern and legacy routes", async () =>
        {
            foreach (bool legacy in new[] {false, true})
            {
                var fake = new Fake { NoModernSpatial = legacy }; fake.Features.Remove(0x18);
                await using var client = new DailyClient(fake, _ => Profile("{\"highToneQuality\":1,\"spatialTypes\":[0,1,2],\"spatialHiResMutex\":1}"));
                var result = await client.SetSpatialModeAsync(1);
                Check(result.Success && !result.PrerequisiteSubmitted && result.After?.HiRes is null
                    && fake.Writes.Single().Command == (legacy ? 0x0403 : 0x0422)
                    && fake.Writes.Single().Payload.SequenceEqual(legacy ? new byte[] {0x1B,1} : new byte[] {1}));
                if (legacy) Check(!(await client.SetSpatialModeAsync(2)).SubmissionAttempted && fake.Writes.Count == 1);
            }
            var hiresFake = new Fake(); hiresFake.Features[0x18] = 0; hiresFake.Features.Remove(0x1B);
            hiresFake.ReadReplies[0x012A] = [0,9];
            await using var hiresClient = new DailyClient(hiresFake, _ => Profile("{\"highToneQuality\":1,\"spatialTypes\":[0,1,2],\"spatialHiResMutex\":1}"));
            var hires = await hiresClient.SetHiResAsync(true);
            Check(hires.Success && hires.After?.SpatialMode is null && hiresFake.Writes.Single().Payload.SequenceEqual(new byte[] {0x18,1}));
            hiresFake.Features[0x18] = 7;
            Check(!(await hiresClient.SetHiResAsync(false)).SubmissionAttempted && hiresFake.Writes.Count == 1);
        });
        await test("game target writes never retry uncertain rejected or disconnected ACK", async () =>
        {
            foreach (string id in new[] {"game-sound-enabled", "game-sound"})
            foreach (int failure in new[] {0,1,2})
            {
                var fake = new Fake { EmptySetAck = failure == 0, SetterStatus = failure == 1 ? (byte)9 : (byte)0, SetReadEof = failure == 2 };
                fake.Features[0x27] = 0;
                await using var client = new DailyClient(fake, _ => Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
                var result = await client.ApplyFeatureAsync(id, id == "game-sound" ? 3 : 1);
                Check(!result.Success && result.TargetSubmitted && !result.PrerequisiteSubmitted && fake.Writes.Count == 1
                    && result.ReadbackMatches == true && result.AckAccepted == (failure == 1 ? false : (bool?)null));
                Check(result.FailureKind == (failure == 1 ? DailyApplyFailure.PeerRejected
                    : failure == 2 ? DailyApplyFailure.Disconnected : DailyApplyFailure.TransportUnknown));
            }
        });
    }
}
