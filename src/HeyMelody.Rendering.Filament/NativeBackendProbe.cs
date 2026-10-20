using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HeyMelody.Rendering.Filament;

internal static class FilamentBackendPolicy
{
    internal static void Validate(FilamentBackend backend)
    {
        if (backend is not (FilamentBackend.OpenGL or FilamentBackend.Metal or FilamentBackend.Vulkan))
            throw new ArgumentOutOfRangeException(nameof(backend), "A specific compiled Filament backend is required.");
    }

    internal static string Argument(FilamentBackend backend) => backend switch
    {
        FilamentBackend.OpenGL => "opengl", FilamentBackend.Metal => "metal", FilamentBackend.Vulkan => "vulkan",
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    internal static void ValidateDiagnostics(FilamentBackend expected, uint requested, uint actual, FilamentFailureStage stage)
    {
        if (requested != (uint)expected || actual != (uint)expected)
            throw new FilamentRendererException(
                $"The native renderer returned backend {actual} for requested backend {expected} (native request {requested}).",
                FilamentFailureKind.RuntimeFailure, stage, expected);
    }
}

internal readonly record struct NativeProbePayload(string ExecutablePath, string Identity)
{
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal readonly record struct NativeProbeResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    internal bool Succeeded => !TimedOut && ExitCode == 0;
}

internal interface INativeBackendProbeRunner
{
    Task<NativeProbeResult> RunAsync(string executablePath, FilamentBackend backend, CancellationToken cancellationToken);
}

/// <summary>Each caller owns its own child, so cancelling one view cannot cancel another view's probe.</summary>
internal sealed class NativeBackendProbe(INativeBackendProbeRunner runner)
{
    internal static NativeBackendProbe Shared { get; } = new(new NativeBackendProbeProcess());
    private readonly object _gate = new();
    private readonly HashSet<(string Payload, FilamentBackend Backend)> _successes = [];
    private readonly Dictionary<(string Payload, FilamentBackend Backend), long> _generations = [];

    internal void EnsureAvailable(NativeProbePayload payload, FilamentBackend backend, CancellationToken cancellationToken)
    {
        FilamentBackendPolicy.Validate(backend);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (payload.Identity, backend);
        long generation;
        lock (_gate)
        {
            if (_successes.Contains(key)) return;
            generation = _generations.GetValueOrDefault(key);
        }
        NativeProbeResult result;
        try
        {
            result = runner.RunAsync(payload.ExecutablePath, backend, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new FilamentRendererException("The Filament backend probe component could not be started.",
                FilamentFailureKind.ComponentUnavailable, FilamentFailureStage.Probe, backend, innerException: error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded)
        {
            var reason = result.TimedOut ? "timed out after 30 seconds" : $"exited with code {result.ExitCode}";
            var detail = string.Join("\n", new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }
                .Where(value => value.Length != 0));
            throw new FilamentRendererException($"The {backend} backend probe {reason}. {detail}".Trim(),
                FilamentFailureKind.BackendUnavailable, FilamentFailureStage.Probe, backend);
        }
        // Only a completed successful child is reusable. Failures and cancelled work remain retryable.
        lock (_gate)
            if (_generations.GetValueOrDefault(key) == generation) _successes.Add(key);
    }

    internal void Invalidate(NativeProbePayload payload, FilamentBackend backend)
    {
        var key = (payload.Identity, backend);
        lock (_gate)
        {
            _successes.Remove(key);
            _generations[key] = _generations.GetValueOrDefault(key) + 1;
        }
    }
}

internal sealed class NativeBackendProbeProcess(TimeSpan? timeout = null) : INativeBackendProbeRunner
{
    internal const int MaximumLogCharacters = 8192;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<NativeProbeResult> RunAsync(string executablePath, FilamentBackend backend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executablePath)) throw new IOException("The backend probe path must be absolute.");
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        start.ArgumentList.Add("--backend");
        start.ArgumentList.Add(FilamentBackendPolicy.Argument(backend));
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The backend probe process did not start.");
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        using var deadline = new CancellationTokenSource(_timeout);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillOwnedProcess(process);
            await DrainAfterKillAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await ObserveLogsAsync(output, error).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            timedOut = true;
        }
        var logs = await ObserveLogsAsync(output, error).ConfigureAwait(false);
        return new(process.HasExited ? process.ExitCode : -1, logs.Output, logs.Error, timedOut);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            var count = Math.Min(read, MaximumLogCharacters - text.Length);
            if (count > 0) text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task DrainAfterKillAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static async Task<(string Output, string Error)> ObserveLogsAsync(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return (output.Result, error.Result);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
            // Only owned probe streams are involved. Observe any late failures after their handles close.
            _ = Task.WhenAll(output, error).ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return (output.IsCompletedSuccessfully ? output.Result : "", error.IsCompletedSuccessfully ? error.Result : "");
        }
    }
}
