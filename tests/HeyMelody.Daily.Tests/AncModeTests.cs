using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;

internal static class AncModeTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("ANC parents report their mode without inventing a child adjustment", () =>
        {
            var profile = X4Profile.Load();
            foreach (byte primary in new byte[] { 0, 1, 2, 11 })
            {
                var state = DailyParsers.ParseAnc(Fake.AncReply(primary), profile);
                Check(state.Primary == primary && state.Level is null && state.Kind == 1 && state.ModeType == 1);
            }
            var standalone = Profile(source => Mode(source, 11).Remove("childrenMode"));
            var single = DailyParsers.ParseAnc(Fake.AncReply(11), standalone);
            Check(single.Primary == 11 && single.Level == 11);
            return Task.CompletedTask;
        });
        await test("ANC main selection sends independent parent payload goldens in one submission", async () =>
        {
            foreach (var (primary, golden) in new (byte, string)[]
                { (0, "010101"), (1, "010102"), (2, "010104"), (11, "01010008") })
            {
                var fake = new Fake { AncLevel = primary == 0 ? (byte)4 : (byte)3 };
                await using var client = new DailyClient(fake);
                var result = await client.ApplyAncModeAsync(primary);
                Check(result.Success && result.ResponseMatched && result.AckAccepted == true && result.ReadbackMatches == true);
                Check(result.After?.Anc is { Level: null } after && after.Primary == primary);
                Check(fake.Writes is [{ Command: 0x0404 }] && Convert.ToHexString(fake.Writes[0].Payload) == golden);
            }
        });
        await test("ANC main selection accepts a reported child of the requested parent", async () =>
        {
            foreach (var (primary, reported) in new (byte, byte)[] { (0, 3), (1, 4), (2, 8), (11, 12), (11, 13), (11, 14) })
            {
                var fake = new Fake { AncLevel = primary == 0 ? (byte)4 : (byte)3, AncAfterSet = reported };
                await using var client = new DailyClient(fake);
                var result = await client.ApplyAncModeAsync(primary);
                Check(result.Success && result.After?.Anc is { } after && after.Primary == primary && after.Level == reported
                    && fake.Writes.Count == 1);
            }
        });
        await test("adaptive ANC children retain independent exact payload goldens", async () =>
        {
            foreach (var (level, golden) in new (byte, string)[] { (12, "01010010"), (13, "01010020"), (14, "01010040") })
            {
                var fake = new Fake(); await using var client = new DailyClient(fake);
                var result = await client.ApplyAncAsync(11, level);
                Check(result.Success && result.After?.Anc is { Primary: 11 } after && after.Level == level);
                Check(fake.Writes is [{ Command: 0x0404 }] && Convert.ToHexString(fake.Writes[0].Payload) == golden);
            }
        });
        await test("ANC child requests require a child readback and never accept the parent alone", async () =>
        {
            foreach (var (reported, failure, matches) in new (byte, DailyApplyFailure, bool?)[]
                { (11, DailyApplyFailure.ReadbackUnavailable, null), (14, DailyApplyFailure.ReadbackMismatch, false) })
            {
                var fake = new Fake { AncAfterSet = reported }; await using var client = new DailyClient(fake);
                var result = await client.ApplyAncAsync(11, 13);
                Check(!result.Success && result.AckAccepted == true && result.FailureKind == failure
                    && result.ReadbackMatches == matches && fake.Writes.Count == 1);
            }
        });
        await test("ANC main readback distinguishes a different parent from unknown state", async () =>
        {
            var mismatch = new Fake { AncAfterSet = 4 }; await using var mismatchedClient = new DailyClient(mismatch);
            var result = await mismatchedClient.ApplyAncModeAsync(11);
            Check(!result.Success && result.ReadbackMatches == false && result.FailureKind == DailyApplyFailure.ReadbackMismatch
                && mismatch.Writes.Count == 1);
            var unknown = new Fake { AncResponseAfterSet = [0, 1, 1, 0, 0x80] };
            await using var unknownClient = new DailyClient(unknown);
            result = await unknownClient.ApplyAncModeAsync(11);
            Check(!result.Success && result.ReadbackMatches is null && result.FailureKind == DailyApplyFailure.ReadbackUnavailable
                && unknown.Writes.Count == 1 && result.After?.Anc?.Primary is null);
        });
        await test("ANC mode and leaf validation uses the active configured hierarchy", async () =>
        {
            var fake = new Fake(); await using var client = new DailyClient(fake);
            foreach (byte invalid in new byte[] { 4, 12, 13, 14, 255 })
                Check(!(await client.ApplyAncModeAsync(invalid)).SubmissionAttempted);
            Check(!(await client.ApplyAncAsync(11, 4)).SubmissionAttempted && fake.Writes.Count == 0);
            var relocated = Profile(source =>
            {
                var mode = Mode(source, 11); mode["protocolIndex"] = 17;
                mode["childrenMode"]![0]!["protocolIndex"] = 18;
            });
            var other = new Fake(); await using var configured = new DailyClient(other, _ => relocated);
            var result = await configured.ApplyAncModeAsync(17);
            Check(result.Success && Convert.ToHexString(other.Writes.Single().Payload) == "0101000002"
                && result.After?.Anc is { Primary: 17, Level: null });
            Check(!(await configured.ApplyAncModeAsync(11)).SubmissionAttempted && other.Writes.Count == 1);
            Check((await configured.ApplyAncAsync(17, 18)).Success
                && Convert.ToHexString(other.Writes.Last().Payload) == "0101000004" && other.Writes.Count == 2);
        });
        await test("ANC main validation rechecks parent child source and command conditions before writing", async () =>
        {
            foreach (var profile in new[]
            {
                Profile(source => Mode(source, 11)["minAppVersion"] = 17006005)
            })
            {
                var fake = new Fake(); await using var client = new DailyClient(fake, _ => profile);
                Check(!(await client.ApplyAncModeAsync(11)).SubmissionAttempted && fake.Writes.Count == 0);
            }
            var gated = Profile(source => Mode(source, 11)["minFirmVersion"] = 118);
            var changing = new Fake(); changing.ReadReplies[0x0105] = Firmware("119");
            await using var changingClient = new DailyClient(changing, _ => gated);
            Check((await changingClient.RefreshAsync()).AncOptions.Any(x => x.Primary == 11));
            changing.ReadReplies[0x0105] = Firmware("116");
            var changedFirmware = await changingClient.ApplyAncModeAsync(11);
            Check(changedFirmware.Success && changing.Writes.Count == 1
                && changedFirmware.Before!.Warnings.Any(warning => warning.Contains("固件兼容提示")));
            var unavailable = new Fake { CapabilityOverride = new byte[9] };
            await using var unavailableClient = new DailyClient(unavailable);
            Check(!(await unavailableClient.ApplyAncModeAsync(11)).SubmissionAttempted && unavailable.Writes.Count == 0);
        });
        await test("standalone ANC modes remain selectable without creating a default child", async () =>
        {
            var standalone = Profile(source => Mode(source, 11).Remove("childrenMode"));
            var fake = new Fake(); await using var client = new DailyClient(fake, _ => standalone);
            var result = await client.ApplyAncModeAsync(11);
            Check(result.Success && result.After?.Anc is { Primary: 11, Level: 11 } && fake.Writes.Count == 1);
        });
        await test("ANC main no-op accepts either the parent or any of its reported children", async () =>
        {
            foreach (byte reported in new byte[] { 11, 12, 13, 14 })
            {
                var fake = new Fake { AncLevel = reported }; await using var client = new DailyClient(fake);
                var result = await client.ApplyAncModeAsync(11);
                Check(result.Success && result.ReadbackMatches == true && !result.SubmissionAttempted
                    && fake.Writes.Count == 0 && result.After?.Anc?.Primary == 11);
            }
        });
        await test("ANC main unknown rejected and interrupted ACKs preserve evidence without replay", async () =>
        {
            foreach (var fake in new[] { new Fake { EmptySetAck = true }, new Fake { SetterStatus = 7 },
                new Fake { SetReadEof = true }, new Fake { SetReadFailure = new IOException() } })
            {
                await using var client = new DailyClient(fake);
                var result = await client.ApplyAncModeAsync(11);
                Check(!result.Success && result.TargetSubmitted && !result.PrerequisiteSubmitted
                    && result.ReadbackMatches == true && fake.Writes.Count == 1);
                if (fake.SetterStatus != 0)
                    Check(result.AckAccepted == false && result.AckStatus == 7 && result.FailureKind == DailyApplyFailure.PeerRejected);
                else if (fake.EmptySetAck)
                    Check(result.AckAccepted is null && result.ResponseMatched && result.FailureKind == DailyApplyFailure.TransportUnknown);
                else
                    Check(result.AckAccepted is null && result.FailureKind == DailyApplyFailure.Disconnected
                        && result.After?.Connected == true && fake.Opens == 2);
            }
        });
        await test("ANC parent notifications retain unknown children and never submit a setter", async () =>
        {
            var fake = new Fake(); await using var client = new DailyClient(fake);
            await client.RefreshAsync(); await client.StartMonitoringAsync();
            var observed = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SnapshotChanged += snapshot =>
            {
                if (snapshot.Anc is { Primary: 11, Level: null }) observed.TrySetResult(snapshot);
            };
            fake.Push(0x0204, [3, 1, 1, 0, 8]);
            var after = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(after.Capabilities.Anc && fake.Writes.Count == 0);
        });
    }

    private static DeviceProfile Profile(Action<JsonObject> configure)
    {
        var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
        configure(source);
        return DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "anc-mode-fixture");
    }
    private static JsonObject Mode(JsonObject source, int index) => source["function"]!["noiseReductionMode"]!.AsArray()
        .Single(x => x!["protocolIndex"]!.GetValue<int>() == index)!.AsObject();
    private static byte[] Firmware(string version) => [0, 3, ..Encoding.UTF8.GetBytes($"1,2,{version},2,2,{version},3,2,100")];
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("ANC main mode regression failed."); }
}
