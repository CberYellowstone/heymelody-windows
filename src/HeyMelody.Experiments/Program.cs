using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeyMelodyWindows.Experiments;

public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string operation = "", output = "";
            string? targetFile = null;
            uint rate = 0;
            bool worker = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--operation": operation = Value(); break;
                    case "--output": output = Value(); break;
                    case "--target-file": targetFile = Value(); break;
                    case "--rate": rate = uint.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--worker": worker = true; break;
                    case "--help":
                        Console.WriteLine("HeyMelody controlled experiments 0.3.0\n" +
                            "--operation enable-hires|driver-read|rate|play --output NEW_FILE [--rate 96000|192000]\n" +
                            "--target-file PRIVATE_FILE optionally writes the selected peer address for private capture matching.\n" +
                            "enable-hires handles spatial mutex and may disconnect playback. rate requests are volatile. play is short silent playback.");
                        return 0;
                    default: throw new ArgumentException();
                }
                string Value() { if (++index >= args.Length) throw new ArgumentException(); return args[index]; }
            }
            if (operation is not ("enable-hires" or "driver-read" or "rate" or "play") || string.IsNullOrWhiteSpace(output))
                throw new ArgumentException();
            var fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            if (File.Exists(fullOutput)) throw new IOException("Output already exists.");
            if (targetFile is not null)
            {
                targetFile = Path.GetFullPath(targetFile);
                if (!targetFile.Contains(Path.DirectorySeparatorChar + "private" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Peer address output must be inside the private artifacts directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            }
            ExperimentReport report;
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(110));
            if (worker)
            {
                report = await ExperimentRunner.RunAsync(operation, rate, targetFile, token.Token);
                using var stream = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await JsonSerializer.SerializeAsync(stream, report, Json);
            }
            else
            {
                var host = Environment.ProcessPath ?? throw new InvalidOperationException();
                var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
                if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (var value in new[] { "--worker", "--operation", operation, "--output", fullOutput, "--rate", rate.ToString() })
                    start.ArgumentList.Add(value);
                if (targetFile is not null) { start.ArgumentList.Add("--target-file"); start.ArgumentList.Add(targetFile); }
                using var process = Process.Start(start) ?? throw new InvalidOperationException();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(125)); }
                catch (TimeoutException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Console.Error.WriteLine("The isolated operation timed out. A submitted setting may have taken effect; read state independently before any further action.");
                    return 1;
                }
                if (!File.Exists(fullOutput)) throw new InvalidDataException();
                report = JsonSerializer.Deserialize<ExperimentReport>(await File.ReadAllTextAsync(fullOutput), Json) ?? throw new InvalidDataException();
                if (report.SchemaVersion != 1 || report.Operation != operation) throw new InvalidDataException();
            }
            if (!worker)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    report.SchemaVersion, report.Operation, report.Success, report.Error,
                    Report = fullOutput, report.NativeError,
                    HiResAfter = report.HiRes?.After?.HiRes,
                    HiResAck = report.HiRes?.HiResAckAccepted,
                    SettingsAttempted = report.HiRes?.SettingsAttempted,
                    DriverRates = report.Driver?.SampleRates,
                    DriverTarget = report.Driver?.ReportedTargetSampleRateHz,
                    RateAccepted = report.Rate?.Accepted,
                    SubmissionAttempted = report.Rate?.SubmissionAttempted,
                    PlaybackStarted = report.Playback?.Started
                }, Json));
            return report.Success ? 0 : 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Invalid arguments or experiment receipt failure. Use --help. No operation is retried automatically.");
            return 2;
        }
    }
}
