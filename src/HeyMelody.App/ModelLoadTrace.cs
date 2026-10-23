using System.Diagnostics;
using System.Text.Json;

namespace HeyMelodyWindows.App;

/// <summary>Opt-in local measurements for the actual application process, never a default log.</summary>
internal sealed class ModelLoadTrace : IDisposable
{
    internal const string EnvironmentVariable = "HEYMELODY_MODEL_LOAD_TRACE";
    private readonly StreamWriter _writer;

    private ModelLoadTrace(StreamWriter writer)
    {
        _writer = writer;
        DeviceModelView.LoadMeasured += OnMeasured;
    }

    internal static ModelLoadTrace? Start()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "session", schemaVersion = 1, processId = Environment.ProcessId,
                executable = Path.GetFileName(Environment.ProcessPath),
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                startedUtc = DateTimeOffset.UtcNow
            }));
            return new ModelLoadTrace(writer);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine($"Model load trace unavailable: {error.Message}");
            return null;
        }
    }

    private void OnMeasured(DeviceModelView view, ModelLoadTimings timings)
    {
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "load", processId = Environment.ProcessId, measuredUtc = DateTimeOffset.UtcNow,
                modelSha256 = view.ModelDigest, timings
            }));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { Trace.WriteLine($"Model load trace unavailable: {error.Message}"); }
    }

    public void Dispose()
    {
        DeviceModelView.LoadMeasured -= OnMeasured;
        _writer.Dispose();
    }
}
