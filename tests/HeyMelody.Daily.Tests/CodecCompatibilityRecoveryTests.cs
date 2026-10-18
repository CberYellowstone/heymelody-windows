using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Resources;

internal static class CodecCompatibilityRecoveryTests
{
    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Codec recovery assertion failed.");
    }

    private static byte[] UnknownCodecLayout(byte sequence) => CodecWireFixtureConnector.Air4ProV125Ack(sequence, 3);

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Normal unknown codec values remain raw and never require length compatibility", async () =>
        {
            var fake = new Fake();
            await using var client = new DailyClient(new CodecWireFixtureConnector(fake, sequence =>
            {
                byte[] wire = CodecWireFixtureConnector.Ack(sequence); wire[10] = 0xFE; return wire;
            }));
            var snapshot = await client.RefreshAsync();
            Check(snapshot.Connected && snapshot.ProfileVerified && snapshot.CodecRaw == 0xFE && snapshot.Error is null
                && !snapshot.Warnings.Any(x => x.Contains("兼容格式") || x.Contains("已跳过")) && fake.Opens == 1);
        });
        await test("Codec recovery uses one fresh reader and survives monitoring scoped reads and SET readback", async () =>
        {
            var fake = new Fake();
            await using var client = new DailyClient(new CodecWireFixtureConnector(fake, UnknownCodecLayout));
            var snapshot = await client.RefreshAsync();
            Check(snapshot.Connected && snapshot.ProfileVerified && snapshot.Error is null && snapshot.CodecRaw is null
                && snapshot.Capabilities.Preset && snapshot.Warnings.Any(x => x.Contains("已跳过")));
            await client.StartMonitoringAsync();
            var scoped = await client.RefreshAsync(DailyRefreshScope.Information);
            var result = await client.ApplyPresetAsync(7);
            Check(scoped.Connected && scoped.CodecRaw is null && scoped.Warnings.Any(x => x.Contains("已跳过"))
                && result.Success && result.After?.CodecRaw is null && fake.Opens == 2 && fake.MaximumReaders == 1
                && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 1);
        });
        await test("A new client after explicit reconnection retries codec and clears the suppression warning", async () =>
        {
            var fake = new Fake();
            await using (var first = new DailyClient(new CodecWireFixtureConnector(fake, UnknownCodecLayout)))
                Check((await first.RefreshAsync()).Warnings.Any(x => x.Contains("已跳过")));
            await using var next = new DailyClient(new CodecWireFixtureConnector(fake, CodecWireFixtureConnector.Ack));
            var snapshot = await next.RefreshAsync();
            Check(snapshot.Connected && snapshot.CodecRaw == 8 && !snapshot.Warnings.Any(x => x.Contains("已跳过"))
                && fake.Opens == 3 && fake.Requests.Count(x => x.Command == 0x0114) == 2 && fake.Writes.Count == 0);
        });
        await test("Codec recovery refuses a different product or resource provenance", async () =>
        {
            var bundles = new BundledResourceStore();
            foreach (bool changeCatalog in new[] { false, true })
            {
                var fake = new Fake();
                string catalogDigest = bundles.GetCatalog().Sha256;
                var connector = new CodecWireFixtureConnector(fake, UnknownCodecLayout, onOpen: number =>
                {
                    if (number != 2) return;
                    if (changeCatalog) catalogDigest = new string('0', 64);
                    else fake.ProductId = "067C10";
                });
                await using var client = new DailyClient(connector, id =>
                {
                    var catalog = bundles.GetCatalog();
                    return catalog.Find(id) is { } resource ? DeviceProfile.FromResource(resource.Raw, catalogDigest) : null;
                });
                var snapshot = await client.RefreshAsync();
                Check(!snapshot.Connected && !snapshot.ProfileVerified && snapshot.Error is not null && fake.Opens == 2
                    && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0);
            }
        });
        await test("A codec failure on the second read attempt cannot allocate a third connection", async () =>
        {
            var fake = new Fake();
            var connector = new CodecWireFixtureConnector(fake, UnknownCodecLayout, onOpen: number =>
            {
                if (number == 1) fake.ReadReplies[0x0103] = [0, 0x10];
                else fake.ReadReplies.Remove(0x0103);
            });
            await using var client = new DailyClient(connector);
            var snapshot = await client.RefreshAsync();
            Check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true && fake.Opens == 2
                && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0);
        });
        await test("A required read failure on the recovery connection still ends the refresh", async () =>
        {
            var fake = new Fake();
            var connector = new CodecWireFixtureConnector(fake, UnknownCodecLayout, onOpen: number =>
            {
                if (number == 2) fake.ReadReplies[0x0106] = [];
            });
            await using var client = new DailyClient(connector);
            var snapshot = await client.RefreshAsync();
            Check(!snapshot.Connected && snapshot.Error?.Contains("0x0106") == true && fake.Opens == 2
                && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0);
        });
        await test("A codec timeout degrades only encoding information after fresh identity verification", async () =>
        {
            var fake = new Fake(); fake.Silent.Add(0x0114);
            await using var client = new DailyClient(fake);
            var snapshot = await client.RefreshAsync();
            Check(snapshot.Connected && snapshot.ProfileVerified && snapshot.Error is null && snapshot.CodecRaw is null
                && snapshot.Warnings.Any(x => x.Contains("等待回执超时")) && fake.Opens == 2
                && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0);
        });
        await test("A missing matched codec status recovers while IO EOF and cancellation remain failures", async () =>
        {
            var missing = new Fake(); missing.ReadReplies[0x0114] = [];
            await using (var client = new DailyClient(missing))
            {
                var snapshot = await client.RefreshAsync();
                Check(snapshot.Connected && snapshot.CodecRaw is null && snapshot.Warnings.Any(x => x.Contains("回执缺少状态"))
                    && missing.Opens == 2 && missing.Requests.Count(x => x.Command == 0x0114) == 1);
            }
            foreach (bool eof in new[] { false, true })
            {
                var fake = new Fake();
                await using var client = new DailyClient(new CodecWireFixtureConnector(fake, _ =>
                    eof ? [] : throw new IOException("private transport detail")));
                var snapshot = await client.RefreshAsync();
                Check(!snapshot.Connected && snapshot.Error?.Contains("控制通道已中断") == true
                    && !snapshot.Error.Contains("private") && fake.Opens == 2
                    && fake.Requests.Count(x => x.Command == 0x0114) == 2 && fake.Writes.Count == 0);
            }
            var cancelled = new Fake { PauseNextRequest = 0x0114 };
            await using var cancelledClient = new DailyClient(cancelled);
            using var cancel = new CancellationTokenSource();
            var refresh = cancelledClient.RefreshAsync(cancel.Token);
            await cancelled.RequestPaused.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            Check(!(await refresh).Connected && cancelled.Opens == 1 && cancelled.Writes.Count == 0);
        });
        await test("Codec compatibility cannot reconnect an active calibration", async () =>
        {
            var fake = new Fake(); bool malformed = false;
            await using var client = new DailyClient(new CodecWireFixtureConnector(fake,
                sequence => malformed ? UnknownCodecLayout(sequence) : CodecWireFixtureConnector.Ack(sequence)));
            Check((await client.RefreshAsync()).Connected);
            client.SetCalibrationActive(true); malformed = true;
            bool failed = false;
            try { await client.RefreshAsync(DailyRefreshScope.Information); }
            catch (IOException) { failed = true; }
            Check(failed && fake.Opens == 1 && fake.Writes.Count == 0);
            client.SetCalibrationActive(false);
        });
        await test("A decoded codec ACK followed by a bad or duplicate ACK in the same chunk cannot trigger recovery", async () =>
        {
            foreach (bool duplicate in new[] { false, true })
            {
                var fake = new Fake();
                await using var client = new DailyClient(new CodecWireFixtureConnector(fake, sequence => duplicate
                    ? [.. CodecWireFixtureConnector.Air4ProV125Ack(sequence), .. CodecWireFixtureConnector.Air4ProV125Ack(sequence)]
                    : [.. CodecWireFixtureConnector.Ack(sequence), .. UnknownCodecLayout(sequence)]));
                var snapshot = await client.RefreshAsync();
                Check(!snapshot.Connected && snapshot.Error?.Contains("0x0114") == true && fake.Opens == 2
                    && fake.Requests.Count(x => x.Command == 0x0114) == 2 && fake.Writes.Count == 0);
            }
        });
        await test("An uncertain SET ACK remains unsuccessful after a degraded readback and is never replayed", async () =>
        {
            var fake = new Fake();
            await using var client = new DailyClient(new CodecWireFixtureConnector(fake, UnknownCodecLayout));
            Check((await client.RefreshAsync()).Connected);
            fake.EmptySetAck = true;
            var result = await client.ApplyPresetAsync(7);
            Check(!result.Success && result.AckAccepted is null && result.ReadbackVerified && fake.Writes.Count == 1
                && result.After?.Warnings.Any(x => x.Contains("已跳过")) == true);
        });
    }
}
