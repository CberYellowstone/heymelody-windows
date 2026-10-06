using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeyMelodyWindows.Windows;

namespace HeyMelodyWindows.Probe;

public static class Program
{
    private static readonly Guid MelodyService = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Invalid arguments. Use --help for the supported read-only commands.");
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine("HeyMelody X4 read-only probe 0.2.0\n" +
                "--list [--show-addresses]    List remembered Bluetooth devices without inquiry.\n" +
                "--probe [--name NAME] [--address HEX12] [--output FILE]\n" +
                "    Query an already connected Enco X4; default name is OPPO Enco X4.\n" +
                "Only eight fixed GET commands are permitted. No setters, registration, host capability, OTA, or pairing commands.\n" +
                "Bluetooth addresses are excluded from probe reports. --output uses CreateNew and will not overwrite a file.");
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This transport requires Windows.");
            return 2;
        }
        if (options.List)
            return ListDevices(options.ShowAddresses);
        if (options.Worker)
        {
            var result = await RunWorkerAsync(options);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.Success ? 0 : 1;
        }

        var report = await RunSupervisedAsync(options);
        if (options.Output is not null)
        {
            try
            {
                var fullPath = Path.GetFullPath(options.Output);
                var directory = Path.GetDirectoryName(fullPath)!;
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
                Console.WriteLine($"Report: {fullPath}");
            }
            catch (Exception)
            {
                Console.Error.WriteLine("The report could not be created. Existing files are not overwritten.");
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return 2;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return report.Success ? 0 : 1;
    }

    private static int ListDevices(bool includeAddresses)
    {
        try
        {
            var devices = WindowsBluetooth.EnumeratePaired();
            var output = devices.Select(device => new
            {
                name = device.Name,
                connected = device.Connected,
                remembered = device.Remembered,
                address = includeAddresses ? device.Address.ToString("X12") : null
            });
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Remembered Bluetooth devices could not be enumerated.");
            return 1;
        }
    }

    private static async Task<ProbeResult> RunSupervisedAsync(Options options)
    {
        var failure = NewResult(options);
        failure.Phase = "worker-start";
        Process? process = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var host = Environment.ProcessPath ?? throw new InvalidOperationException();
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--worker");
            start.ArgumentList.Add("--name");
            start.ArgumentList.Add(options.Name);
            if (options.Address is not null)
            {
                start.ArgumentList.Add("--address");
                start.ArgumentList.Add(options.Address.Value.ToString("X12"));
            }
            process = Process.Start(start) ?? throw new InvalidOperationException();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                // Native SDP discovery cannot be cancelled reliably on every Bluetooth stack.
                // Isolate it in a helper process and cap the entire operation at 70 seconds.
                await process.WaitForExitAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(70), cancellation.Token);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                failure.Phase = exception is TimeoutException ? "worker-timeout" : "cancelled";
                failure.Error = exception is TimeoutException
                    ? "The isolated Bluetooth query process exceeded its operation budget. Its connection was closed."
                    : "The isolated Bluetooth query process was cancelled. Its connection was closed.";
                failure.Warnings.Add("No complete worker receipt is available. These results do not certify a query outcome.");
                return Finish(failure);
            }
            var text = await stdout;
            _ = await stderr; // Helpers emit only fixed progress labels; do not publish arbitrary native messages.
            if (text.Length > 512 * 1024)
                throw new InvalidDataException();
            var report = JsonSerializer.Deserialize<ProbeResult>(text, JsonOptions) ?? throw new InvalidDataException();
            if (report.SchemaVersion != 2 || report.Operation != "x4-readonly-session"
                || report.ReadOnly.SettingCommandsSent != 0)
                throw new InvalidDataException();
            if (report.Success && (!report.Target.IdentityVerified || report.DeviceModel is null))
                throw new InvalidDataException();
            return report;
        }
        catch (Exception)
        {
            failure.Error = "The isolated query process failed or returned an invalid receipt.";
            return Finish(failure);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    private static async Task<ProbeResult> RunWorkerAsync(Options options)
    {
        var result = NewResult(options);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        try
        {
            Console.Error.WriteLine("phase:discovery");
            var candidates = WindowsBluetooth.EnumeratePaired().Where(device =>
                device.Remembered && (options.Address is not null
                    ? device.Address == options.Address.Value
                    : string.Equals(device.Name.Trim(), options.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (candidates.Count != 1)
            {
                result.Error = candidates.Count == 0 ? "No unique paired Enco X4 target was found."
                    : "Multiple matching paired devices were found. Select one explicitly by Bluetooth address.";
                return Finish(result);
            }
            var candidate = candidates[0];
            result.Target.Name = candidate.Name;
            result.Target.ConnectedToWindowsBefore = candidate.Connected;
            if (!candidate.Connected)
            {
                result.Error = "The paired target is not currently connected to Windows. Connect it in Windows Bluetooth settings first.";
                return Finish(result);
            }

            result.Phase = "service-discovery";
            Console.Error.WriteLine("phase:service-discovery");
            var services = WindowsSdp.FindServices(candidate.Address, MelodyService)
                .Where(service => service.ServiceId == MelodyService && service.Channel is > 0 and <= 30)
                .DistinctBy(service => service.Channel).ToList();
            result.Target.DiscoveredChannels = services.Select(service => service.Channel).ToList();
            if (services.Count == 0)
            {
                result.Error = "The target's Melody RFCOMM service did not yield a usable SDP channel.";
                return Finish(result);
            }

            foreach (var service in services)
            {
                budget.Token.ThrowIfCancellationRequested();
                result.Phase = "connect";
                Console.Error.WriteLine("phase:connect");
                using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                connectBudget.CancelAfter(TimeSpan.FromSeconds(5));
                RfcommConnection connection;
                try { connection = await RfcommConnection.ConnectAsync(candidate.Address, MelodyService, service.Channel, connectBudget.Token); }
                catch (Win32Exception exception) { result.NativeError = exception.NativeErrorCode; continue; }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested) { continue; }
                await using var transport = new NativeQueryTransport(connection);
                result.Target.SelectedChannel = service.Channel;
                await new ReadOnlySession(transport).RunQueriesAsync(result, budget.Token);
                var queryPhase = result.Phase;
                result.Phase = "model-resolution";
                Phase2ModelBuilder.Populate(result);
                result.Phase = queryPhase;
                // A transport connection and matching ID response are the protocol proof.
                // Never try a different channel after a verified identity or a submitted query.
                return Finish(result);
            }
            result.Error = "The discovered Melody service could not be connected within the connection budget.";
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "The read-only operation budget expired.";
        }
        catch (Win32Exception exception)
        {
            result.Success = false;
            result.NativeError = exception.NativeErrorCode;
            result.Error = "Windows rejected the Bluetooth discovery or transport operation.";
        }
        catch (Exception)
        {
            result.Success = false;
            result.Error = "The read-only Bluetooth operation could not be completed.";
        }
        return Finish(result);
    }

    private static ProbeResult NewResult(Options options) => new() { Target = new TargetInfo { Name = options.Name } };
    private static ProbeResult Finish(ProbeResult result) { result.FinishedAtUtc = DateTimeOffset.UtcNow; return result; }

    private sealed class NativeQueryTransport(RfcommConnection connection) : IQueryTransport
    {
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
            => connection.SendAsync(bytes, cancellationToken);
        public Task<int> ReadAsync(Memory<byte> bytes, CancellationToken cancellationToken)
            => connection.ReadAsync(bytes, cancellationToken);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Options
    {
        public bool Help { get; set; }
        public bool List { get; set; }
        public bool ShowAddresses { get; set; }
        public bool Worker { get; set; }
        public string Name { get; set; } = "OPPO Enco X4";
        public ulong? Address { get; set; }
        public string? Output { get; set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--help": case "-h": options.Help = true; break;
                    case "--list": options.List = true; break;
                    case "--show-addresses": options.ShowAddresses = true; break;
                    case "--probe": break;
                    case "--worker": options.Worker = true; break;
                    case "--name": options.Name = Value(); break;
                    case "--output": options.Output = Value(); break;
                    case "--address":
                        var address = Value().Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
                        if (address.Length != 12 || !ulong.TryParse(address, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                            throw new ArgumentException();
                        options.Address = parsed;
                        break;
                    default: throw new ArgumentException();
                }
                string Value()
                {
                    if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException();
                    return args[index];
                }
            }
            if (string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 248
                || (options.ShowAddresses && !options.List)
                || (options.List && (options.Worker || options.Output is not null)))
                throw new ArgumentException();
            return options;
        }
    }
}

