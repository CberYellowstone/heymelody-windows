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
