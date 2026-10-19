using System.ComponentModel;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;

internal static class ConnectionDiagnosticTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("failed identity retains its query and retries only reads", async () =>
        {
            var fake = new Fake();
            fake.ReadReplies[0x0103] = [0, 0x10]; // Synthetic truncated product identity.
            await using var client = new DailyClient(fake);
            var snapshot = await client.RefreshAsync();
            Check(!snapshot.Connected && snapshot.Error?.Contains("0x0103") == true
                && snapshot.Error.Contains("身份数据格式无效") && fake.Opens == 2 && fake.Writes.Count == 0);
        });
        await test("rejected and malformed capabilities identify the failing query", async () =>
        {
            foreach (byte[] response in new byte[][] { [1], [0], [] })
            {
                var fake = new Fake(); fake.ReadReplies[0x0100] = response;
                await using var client = new DailyClient(fake);
                var snapshot = await client.RefreshAsync();
                Check(!snapshot.Connected && snapshot.Error?.Contains("0x0100") == true
                    && snapshot.Error.Contains(response.Length == 0 ? "回执缺少状态字节"
                        : response[0] == 1 ? "设备拒绝查询" : "能力数据格式无效")
                    && fake.Opens == 2 && fake.Writes.Count == 0);
            }
        });
        await test("silent required GET retains command and timeout without enabling settings", async () =>
        {
            var fake = new Fake(); fake.Silent.Add(0x0106);
            await using var client = new DailyClient(fake);
            var snapshot = await client.RefreshAsync();
            Check(!snapshot.Connected && !snapshot.ProfileVerified && snapshot.Error?.Contains("0x0106") == true
                && snapshot.Error.Contains("等待回执超时") && fake.Opens == 2 && fake.Writes.Count == 0);
        });
        await test("connection failure exposes only stage and timeout or native code", async () =>
        {
            foreach (var error in new Exception[] { new Win32Exception(10060, "private peer address and raw packet"),
                new OperationCanceledException("private transport detail") })
            {
                var connector = new FailedConnector(error);
                await using var client = new DailyClient(connector);
                var snapshot = await client.RefreshAsync();
                Check(snapshot.Error?.Contains("建立 SPP 控制连接") == true
                    && snapshot.Error.Contains(error is Win32Exception ? "10060" : "超时")
                    && !snapshot.Error.Contains("private") && connector.Opens == 2);
            }
        });
        await test("Only a correlated malformed codec ACK can recover; surrounding bad frames remain failures", async () =>
        {
            foreach (var fixture in new (Func<byte, byte[]> Wire, string Reason)[]
            {
                (sequence => [0, .. CodecWireFixtureConnector.Ack(sequence)], "帧起始标记无效"),
                (sequence => [.. CodecWireFixtureConnector.Ack(sequence), 0], "帧起始标记无效"),
                (sequence =>
                {
                    byte[] wire = CodecWireFixtureConnector.Ack(sequence);
                    wire[7] = 3; // Declared payload length differs from the independent frame.
                    return wire;
                }, "消息长度不匹配")
            })
            {
                var fake = new Fake();
                var connector = new CodecWireFixtureConnector(fake, fixture.Wire);
                await using var client = new DailyClient(connector);
                var snapshot = await client.RefreshAsync();
                if (fixture.Reason == "消息长度不匹配")
                    Check(snapshot.Connected && snapshot.ProfileVerified && snapshot.Error is null && snapshot.CodecRaw is null
                        && snapshot.Warnings.Any(x => x.Contains(fixture.Reason)) && fake.Opens == 2
                        && fake.Requests.Count(x => x.Command == 0x0114) == 1 && fake.Writes.Count == 0);
                else
                    Check(!snapshot.Connected && !snapshot.ProfileVerified && snapshot.Error?.Contains("0x0114") == true
                        && snapshot.Error.Contains("控制数据格式无效") && snapshot.Error.Contains(fixture.Reason)
                        && fake.Opens == 2 && fake.Requests.Count(x => x.Command == 0x0114) == 2 && fake.Writes.Count == 0);
            }
        });
        await test("framing reason diagnostics accept only fixed codes and never exception details", async () =>
        {
            const string reasonKey = "HeyMelody.Protocol.FrameFailure";
            foreach (var (code, expected) in new (object?, string?)[]
            {
                ("frame-start", "帧起始标记无效"), ("frame-length", "帧长度无效"),
                ("fragment-layout", "分片布局无效"), ("fragment-order", "分片顺序无效"),
                ("fragment-sequence", "分片序号无效"), ("inner-length", "消息长度不匹配"),
                ("incomplete-message", "消息未完整接收"),
                ("private peer address and raw packet", null), (123, null), (null, null)
            })
            {
                var error = new FormatException("private peer address and raw packet");
                error.Data[reasonKey] = code;
                await using var client = new DailyClient(new FailedConnector(error));
                var snapshot = await client.RefreshAsync();
                Check(snapshot.Error?.Contains("控制数据格式无效") == true && !snapshot.Error.Contains("private"));
                Check(snapshot.Error is { } failure && (expected is null
                    ? !failure.Contains("控制数据格式无效（") : failure.Contains(expected)));
            }
            var unrelated = new InvalidDataException("private peer address and raw packet");
            unrelated.Data[reasonKey] = "frame-start";
            await using var invalidDataClient = new DailyClient(new FailedConnector(unrelated));
            var invalidDataSnapshot = await invalidDataClient.RefreshAsync();
            Check(invalidDataSnapshot.Error?.Contains("控制数据格式无效") == true
                && !invalidDataSnapshot.Error.Contains("private") && !invalidDataSnapshot.Error.Contains("帧起始标记"));
        });
        await test("initial monitoring failures publish their command and recover without writes", async () =>
        {
            foreach (ushort command in new ushort[] { 0x0200, 0x0205, 0x0201 })
            {
                var fake = new Fake { LegacyRegistration = command == 0x0201 };
                await using var client = new DailyClient(fake);
                Check((await client.RefreshAsync()).Connected);
                var failed = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.SnapshotChanged += snapshot =>
                { if (!snapshot.Connected && snapshot.Error?.Contains($"0x{command:X4}") == true) failed.TrySetResult(snapshot); };
                fake.Silent.Add(command);
                await client.StartMonitoringAsync();
                var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Check(failure.Error?.Contains("等待回执超时") == true && fake.Opens == 1 && fake.Writes.Count == 0);
                fake.Silent.Clear();
                var recovered = await client.RefreshAsync();
                Check(recovered.Connected && recovered.Error is null && fake.Opens == 2 && fake.Writes.Count == 0);
            }
        });
        await test("missing and ambiguous configured services remain distinct failures", async () =>
        {
            foreach (var reason in new[] { ControlTransportFailure.ServiceNotFound, ControlTransportFailure.AmbiguousServices })
            {
                var connector = new FailedConnector(new ControlTransportUnavailableException(reason));
                await using var client = new DailyClient(connector);
                var snapshot = await client.RefreshAsync();
                Check(!snapshot.Connected && snapshot.Error?.Contains(reason == ControlTransportFailure.ServiceNotFound
                    ? "未发现所选配置" : "发现多个 SPP 控制服务") == true && connector.Opens == 2);
            }
        });
    }

    private static void Check(bool value)
    { if (!value) throw new Exception("Connection diagnostic assertion failed."); }

    private sealed class FailedConnector(Exception failure) : IControlConnector
    {
        public int Opens;
        public Task<IControlTransport> OpenAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Opens++; return Task.FromException<IControlTransport>(failure); }
    }
}
