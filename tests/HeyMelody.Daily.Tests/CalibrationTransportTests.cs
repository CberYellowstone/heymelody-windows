using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

internal static class CalibrationTransportTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("X4 wear GET 0109 is independent of OTA GET 0107", async () =>
        {
            var fake = new Fake();
            fake.ReadReplies[0x0107] = Convert.FromHexString("000201030200");
            fake.ReadReplies[0x0109] = Convert.FromHexString("0003010702070304");
            await using var client = new DailyClient(fake);
            var state = await client.RefreshAsync();
            Check(state.LeftInEar == true && state.RightInEar == true && state.LeftInCase == false
                && state.RightInCase == false && state.CaseOpen == true);
            Check(fake.Requests.Any(x => x.Command == 0x0109) && !fake.Requests.Any(x => x.Command == 0x0107));
        });
        await test("placement push after GET 0109 starts wins over stale GET", async () =>
        {
            var fake = new Fake(); await using var client = new DailyClient(fake); await client.RefreshAsync();
            fake.PauseNextRequest = 0x0109;
            var read = client.RefreshAsync(DailyRefreshScope.Information);
            await fake.RequestPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SnapshotChanged += s => { if (s.LeftInEar == true && s.RightInEar == true) seen.TrySetResult(); };
            fake.Push(0x0204, [2,3,1,7,2,7,3,4]);
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(2)); fake.ReleaseRequest.TrySetResult();
            var result = await read;
            Check(result.LeftInEar == true && result.RightInEar == true && result.CaseOpen == true);
        });
        await test("earScan chooses slider workflow independently of hearingJudgeDb", async () =>
        {
            foreach (bool earScan in new[] { false, true })
            {
                var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject();
                var functions = source["function"]!.AsObject();
                functions["earScan"] = earScan ? 1 : 0;
                functions["hearingJudgeDb"] = earScan ? 0 : 1;
                functions["hearingEnhancement"] = 1;
                var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
                await using var client = new DailyClient(new Fake(), _ => profile); await client.RefreshAsync();
                var context = client.CalibrationContext("device", 1);
                Check(context.EarScan == earScan && context.HearingV2 == earScan && context.HearingV1 != earScan);
                Check(context.ProductId == "06EC10" && context.HearingJudgeDb == (earScan ? 0 : 1));
            }
        });
        await test("X4 rejected subscriptions preserve advertised capabilities and passive calibration events", async () =>
        {
            var fake = new Fake();
            fake.ReadReplies[0x0200] = Convert.FromHexString("000901020304080BF1F2F3");
            fake.ReadReplies[0x0205] = Convert.FromHexString("000501000200030004020802");
            await using var client = new DailyClient(fake); await client.RefreshAsync(); await client.StartMonitoringAsync();
            var context = client.CalibrationContext("device", 1);
            Check(context.Events.Contains(4) && context.Events.Contains(8) && !context.Events.Contains(14));
            Check(context.HearingV2 && context.EarScan);
            Check(fake.Requests.Single(x => x.Command == 0x0205).Payload.SequenceEqual(new byte[] {5,1,2,3,4,8}));
            Check((await client.RefreshAsync(DailyRefreshScope.Battery)).Warnings.Count == 0);
            Check((await client.RefreshAsync()).Warnings.Count == 0);
            var fit = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scan = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Notification += p => { if (p.Payload.FirstOrDefault() == 4) fit.TrySetResult(p); if (p.Payload.FirstOrDefault() == 14) scan.TrySetResult(p); };
            fake.Push(0x0204, Convert.FromHexString("0401010201"));
            fake.Push(0x0204, Convert.FromHexString("0E0301007F12345678"));
            await fit.Task.WaitAsync(TimeSpan.FromSeconds(2)); await scan.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(fake.Writes.Count == 0 && fake.MaximumReaders == 1);
        });
        await test("both filter responses reach calibration on matched and subsequent frames", async () =>
        {
            var fake = new Fake();
            await using var client = new DailyClient(fake); await client.RefreshAsync();
            foreach (ushort command in new ushort[] {0x0116,0x011F})
            {
                byte[] header = Convert.FromHexString(command == 0x0116 ? "0012345678043C000101" : "00123456780444AC480001");
                // Preserve X4's actual 250/299-byte layouts, with synthetic UID and coefficients.
                byte[] first = [..header, ..Enumerable.Repeat(new byte[] {0,0,128,63}, command == 0x0116 ? 60 : 72).SelectMany(x => x)];
                fake.ReadReplies[command] = first;
                var firstSeen = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
                var laterSeen = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
                int received = 0;
                void Receive(Packet packet)
                {
                    if (packet.Command != (command | 0x8000)) return;
                    if (Interlocked.Increment(ref received) == 1) firstSeen.TrySetResult(packet); else laterSeen.TrySetResult(packet);
                }
                client.Notification += Receive;
                var reply = await client.RequestAsync(command, [], default);
                await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(reply.Sequence == fake.Requests.Last().Sequence);
                fake.Push((ushort)(command | 0x8000), first, reply.Sequence);
                Check((await laterSeen.Task.WaitAsync(TimeSpan.FromSeconds(2))).Payload.SequenceEqual(first));
                client.Notification -= Receive;
            }
            Check(fake.MaximumReaders == 1 && fake.Opens == 1);
        });
        await test("filter retries ignore earlier sequence successes and failures while retaining current fragments", async () =>
        {
            var fake = new Fake(); await using var client = new DailyClient(fake); await client.RefreshAsync();
            foreach (ushort command in new ushort[] {0x0116,0x011F})
            {
                fake.ReadReplies[command] = [0];
                var old = await client.RequestAsync(command, [], default);
                var current = await client.RequestAsync(command, [], default);
                var packets = new List<Packet>(); var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Receive(Packet packet)
                {
                    lock (packets) packets.Add(packet);
                    if (packet.Command == 0x0204) marker.TrySetResult();
                }
                client.Notification += Receive;
                fake.Push((ushort)(command | 0x8000), [1], old.Sequence);
                fake.Push((ushort)(command | 0x8000), [0], old.Sequence);
                fake.Push((ushort)(command | 0x8000), [0], current.Sequence);
                fake.Push(0x0204, [8,2,9]);
                await marker.Task.WaitAsync(TimeSpan.FromSeconds(2)); client.Notification -= Receive;
                lock (packets) Check(packets.Count(x => x.Command == (command | 0x8000)) == 1
                    && packets.Single(x => x.Command == (command | 0x8000)).Sequence == current.Sequence);
            }
        });
        await test("description writes obey changed firmware conditions while preview stop stays available", async () =>
        {
            var source = JsonNode.Parse(X4Profile.Load().RawSource.GetRawText())!.AsObject(); source["minFirmVersion"] = 100;
            var profile = DeviceProfile.FromResource(JsonSerializer.SerializeToElement(source), "HeyMelody");
            var fake = new Fake(); await using var client = new DailyClient(fake, _ => profile); await client.RefreshAsync();
            fake.ReadReplies[0x0105] = [0,3,..Encoding.UTF8.GetBytes("1,2,90,2,2,90,3,2,100")];
            await client.RefreshAsync(DailyRefreshScope.Information);
            try { await client.RequestAsync(0x0411, [1,1,1,0,7], default); throw new Exception("Description write was submitted."); }
            catch (InvalidOperationException) { }
            Check(fake.Writes.Count == 0);
            await client.RequestAsync(0x0415, [2], default);
            Check(fake.Writes.Single().Payload.SequenceEqual(new byte[] {2}));
        });
        await test("new hearing-filter request invalidates previous ear-scan fragments before its new query", async () =>
        {
            var fake = new Fake(); await using var client = new DailyClient(fake); await client.RefreshAsync();
            fake.ReadReplies[0x0116] = fake.ReadReplies[0x011F] = [0];
            var oldScan = await client.RequestAsync(0x011F, [], default);
            await client.RequestAsync(0x0116, [], default);
            var packets = new List<Packet>(); var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Notification += p => { lock (packets) packets.Add(p); if (p.Command == 0x0204) marker.TrySetResult(); };
            fake.Push(0x811F, [1], oldScan.Sequence); fake.Push(0x811F, [0], oldScan.Sequence); fake.Push(0x0204, [8,2,9]);
            await marker.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lock (packets) Check(packets.All(x => x.Command != 0x811F));
        });
        await test("calibration keeps its reader across low sequence counts and reserves STOP requests", async () =>
        {
            var fake = new Fake(); await using var client = new DailyClient(fake); await client.RefreshAsync();
            client.SetCalibrationActive(true);
            while (fake.SequenceSets[1].Count < 240) await client.RefreshAsync(DailyRefreshScope.Battery);
            Check(fake.Opens == 1);
            await client.RequestAsync(0x0109, [], default); // 15 remain: insufficient for save plus cleanup.
            try { await client.RequestAsync(0x0411, [1,1,1,0,7], default); throw new Exception("Save began without readback budget."); }
            catch (InvalidOperationException) { }
            while (fake.SequenceSets[1].Count < 248) await client.RequestAsync(0x0109, [], default);
            try { await client.RequestAsync(0x040E, [3,1,1,1,237], default); throw new Exception("Reserved sequence consumed by tone."); }
            catch (InvalidOperationException) { }
            try { await client.RefreshAsync(DailyRefreshScope.Battery); throw new Exception("Refresh consumed reserved sequence."); }
            catch (IOException) { }
            Check(fake.Opens == 1 && client.IsConnected && fake.Writes.Count == 0);
            await client.RequestAsync(0x040E, [4], default); await client.RequestAsync(0x040D, [2,0], default);
            Check(fake.Writes.Count == 2 && fake.Opens == 1);
            client.SetCalibrationActive(false); await client.RefreshAsync(DailyRefreshScope.Battery);
            Check(fake.Opens == 2 && fake.SequenceSets.Values.All(x => x.Distinct().Count() == x.Count));
        });
    }
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("Calibration transport regression failed."); }
}
