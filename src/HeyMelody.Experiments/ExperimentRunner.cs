using HeyMelodyWindows.Audio;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Windows;

namespace HeyMelodyWindows.Experiments;

public sealed class ExperimentReport
{
    public int SchemaVersion { get; set; } = 1;
    public string Operation { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int? NativeError { get; set; }
    public HiResWorkflowResult? HiRes { get; set; }
    public DriverAudioReport? Driver { get; set; }
    public DriverRateResult? Rate { get; set; }
    public PlaybackReport? Playback { get; set; }
    public bool BluetoothDisconnectObserved { get; set; }
    public List<ConnectionObservation> ConnectionObservations { get; set; } = [];
}

public sealed record ConnectionObservation(DateTimeOffset TimeUtc, bool Connected);

public sealed class NativeConnector(ulong address, List<ConnectionObservation> observations) : IControlConnector
{
    private static readonly Guid MelodyService = new("0000079A-D102-11E1-9B23-00025B00A5A5");

    public async Task<IControlTransport> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var matches = WindowsBluetooth.EnumeratePaired().Where(device => device.Address == address && device.Remembered).ToList();
        if (matches.Count != 1)
            throw new IOException("The pinned Bluetooth peer is unavailable.");
        observations.Add(new(DateTimeOffset.UtcNow, matches[0].Connected));
        if (!matches[0].Connected)
            throw new IOException("The pinned Bluetooth peer is not connected yet.");
        var services = WindowsSdp.FindServices(address, MelodyService)
            .Where(service => service.ServiceId == MelodyService && service.Channel is > 0 and <= 30)
            .DistinctBy(service => service.Channel).ToList();
        if (services.Count != 1)
            throw new IOException("A unique Melody control service is required.");
        using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectBudget.CancelAfter(TimeSpan.FromSeconds(5));
        return new Adapter(await RfcommConnection.ConnectAsync(address, MelodyService, services[0].Channel, connectBudget.Token));
    }

    private sealed class Adapter(RfcommConnection connection) : IControlTransport
    {
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => connection.SendAsync(bytes, token);
        public Task<int> ReadAsync(Memory<byte> bytes, CancellationToken token) => connection.ReadAsync(bytes, token);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

public static class ExperimentRunner
{
    public static async Task<ExperimentReport> RunAsync(string operation, uint rate, string? privateTargetFile, CancellationToken token)
    {
        var result = new ExperimentReport { Operation = operation };
        try
        {
            var matches = WindowsBluetooth.EnumeratePaired().Where(device =>
                device.Remembered && device.Connected && string.Equals(device.Name.Trim(), "OPPO Enco X4", StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new IOException("A unique already-connected OPPO Enco X4 target is required.");
            var address = matches[0].Address;
            if (privateTargetFile is not null)
            {
                using var file = new FileStream(Path.GetFullPath(privateTargetFile), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
                writer.Write(address.ToString("X12"));
            }
            switch (operation)
            {
                case "enable-hires":
                    result.HiRes = await HiResWorkflow.EnableAsync(new NativeConnector(address, result.ConnectionObservations), token);
                    result.BluetoothDisconnectObserved = result.ConnectionObservations.Any(item => !item.Connected);
                    result.Success = result.HiRes.Success;
                    result.Error = result.HiRes.Error;
                    break;
                case "driver-read":
                    result.Driver = DriverAudio.ReadForPeer(address);
                    result.Success = result.Driver.PeerMatched && result.Driver.ReadSucceeded;
                    result.Error = result.Driver.Failure;
                    break;
                case "rate":
                    if (rate is not (96000 or 192000))
                        throw new ArgumentException("Only the 96/192 kHz experiment rates are allowed.");
                    result.Rate = DriverAudio.RequestRate(address, rate);
                    result.Success = result.Rate.Accepted && result.Rate.TargetMatches;
                    result.NativeError = result.Rate.Win32Error;
                    result.Error = result.Success ? null : result.Rate.Message;
                    break;
                case "play":
                    if (rate is not (96000 or 192000))
                        throw new ArgumentException("Only the 96/192 kHz experiment rates are allowed.");
                    result.Playback = await AudioPlaybackProbe.RunAsync(address, rate, token);
                    result.Success = result.Playback.Started && !result.Playback.Cancelled && result.Playback.Failure is null;
                    result.Error = result.Playback.Failure;
                    break;
                default: throw new ArgumentException("Unknown experiment operation.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            result.Success = false;
            result.NativeError = exception.NativeErrorCode;
            result.Error = "Windows rejected the target experiment operation.";
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "The experiment was cancelled or exceeded its operation budget; any submitted setting requires independent readback.";
        }
        catch (Exception)
        {
            result.Success = false;
            result.Error = "The target experiment could not be completed.";
        }
        result.FinishedAtUtc = DateTimeOffset.UtcNow;
        return result;
    }
}
