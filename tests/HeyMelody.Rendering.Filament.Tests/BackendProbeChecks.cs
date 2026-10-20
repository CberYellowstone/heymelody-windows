using System.Diagnostics;
using System.Runtime.InteropServices;
using HeyMelody.Rendering.Filament;

namespace HeyMelody.Rendering.Tests;

internal static class BackendProbeChecks
{
    private const string MarkerVariable = "HEYMELODY_TEST_PROBE_MARKER";

    // The test apphost doubles as an isolated fixture executable. It never loads the rendering DLL.
    internal static int RunFixture(string backend)
    {
        if (backend == "opengl")
        {
            Console.Write("backend=opengl\n" + new string('o', 24_000));
            Console.Error.Write(new string('e', 24_000));
            return 0;
        }
        if (backend == "metal") { Console.Error.Write("fixture backend unavailable"); return 17; }
        if (backend != "vulkan") return 18;
        if (Environment.GetEnvironmentVariable(MarkerVariable) is { Length: > 0 } marker)
            File.WriteAllText(marker, Environment.ProcessId.ToString());
        Thread.Sleep(TimeSpan.FromMinutes(1));
        return 0;
    }

    internal static async Task RunAsync(string fixtureRoot, Action<bool, string> check)
    {
        check((int)FilamentBackend.OpenGL == 1 && (int)FilamentBackend.Metal == 2 && (int)FilamentBackend.Vulkan == 3,
            "Explicit backend values agree with C ABI 2.");
        foreach (var invalid in new[] { (FilamentBackend)0, (FilamentBackend)4, (FilamentBackend)(-1) })
        {
            bool rejected = false;
            try { NativeFilamentRendererFactory.Instance.Create(invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "Automatic and invalid backends fail before loading native components.");
        }
        check(Marshal.SizeOf<NativeCreateOptions>() == 16 && Marshal.SizeOf<NativeDiagnostics>() == 96
            && Marshal.OffsetOf<NativeCreateOptions>(nameof(NativeCreateOptions.RequestedBackend)).ToInt32() == 12
            && Marshal.OffsetOf<NativeDiagnostics>(nameof(NativeDiagnostics.RequestedBackend)).ToInt32() == 44,
            "ABI 2 reuses reserved fields without changing structure size or alignment.");
        NativeBindings.ValidateAbi(2);
        check(NativeBindings.RequiredAbiVersion == 2, "Only ABI 2 is accepted by new managed clients.");
        foreach (uint oldAbi in new uint[] { 0, 1, 3 })
        {
            bool rejected = false;
            try { NativeBindings.ValidateAbi(oldAbi); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Old and unknown native ABIs are component failures, before probing.");
        }
        foreach (var backend in Enum.GetValues<FilamentBackend>())
        {
            FilamentBackendPolicy.ValidateDiagnostics(backend, (uint)backend, (uint)backend, FilamentFailureStage.Rendering);
            check(FilamentBackendPolicy.Argument(backend) == backend.ToString().ToLowerInvariant(), "The child receives the exact requested backend.");
        }
        foreach (var mismatch in new[] { (1u, 1u), (3u, 1u), (0u, 3u), (3u, 0u) })
        {
            bool rejected = false;
            try { FilamentBackendPolicy.ValidateDiagnostics(FilamentBackend.Vulkan, mismatch.Item1, mismatch.Item2, FilamentFailureStage.Creation); }
            catch (FilamentRendererException error)
            { rejected = error.Backend == FilamentBackend.Vulkan && error.Kind == FilamentFailureKind.RuntimeFailure; }
            check(rejected, "Native fallback and malformed backend diagnostics cannot be accepted silently.");
        }

        var payload = new NativeProbePayload(Path.Combine(fixtureRoot, "fixture-probe"), "payload-a");
        var successRunner = new FixtureRunner((_, _, _) => Task.FromResult(new NativeProbeResult(0, "ready", "")));
        var successProbe = new NativeBackendProbe(successRunner);
        successProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
        successProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
        check(successRunner.Calls == 1, "A completed success is cached for the exact native payload and backend.");
        successProbe.EnsureAvailable(payload, FilamentBackend.OpenGL, default);
        successProbe.EnsureAvailable(payload with { Identity = "payload-b" }, FilamentBackend.Vulkan, default);
        check(successRunner.Calls == 3, "A different backend or native payload gets a new probe.");
        successProbe.Invalidate(payload, FilamentBackend.Vulkan);
        successProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
        check(successRunner.Calls == 4, "A renderer failure invalidates a previously successful probe.");

        var retryRunner = new FixtureRunner((call, _, _) => Task.FromResult(
            call == 1 ? new NativeProbeResult(7, "", "no Vulkan device") : new NativeProbeResult(0, "ready", "")));
        var retryProbe = new NativeBackendProbe(retryRunner);
        bool backendFailure = false;
        try { retryProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default); }
        catch (FilamentRendererException error)
        {
            backendFailure = error.Kind == FilamentFailureKind.BackendUnavailable && error.Stage == FilamentFailureStage.Probe
                && error.Backend == FilamentBackend.Vulkan && error.Message.Contains("no Vulkan device");
        }
        check(backendFailure, "An unsuccessful Vulkan child is a backend failure suitable for a manual compatibility hint.");
        retryProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
        retryProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
        check(retryRunner.Calls == 2 && retryRunner.Backends.All(value => value == FilamentBackend.Vulkan),
            "A failed probe is retryable and never tries OpenGL automatically.");

        var missingRunner = new FixtureRunner((_, _, _) => throw new FileNotFoundException("missing test component"));
        var missingProbe = new NativeBackendProbe(missingRunner);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool componentFailure = false;
            try { missingProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default); }
            catch (FilamentRendererException error) { componentFailure = error.Kind == FilamentFailureKind.ComponentUnavailable; }
            check(componentFailure, "A missing child is a component error rather than a Vulkan driver error.");
        }
        check(missingRunner.Calls == 2, "Component failures are not cached.");
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            bool cancelled = false;
            try { successProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, alreadyCancelled.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && successRunner.Calls == 4, "A cancelled view cannot reuse or launch a probe.");
        }
        using (var cancelFirst = new CancellationTokenSource())
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var independentRunner = new FixtureRunner(async (call, _, token) =>
            {
                if (call == 1) { firstStarted.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                return new(0, "ready", "");
            });
            var independentProbe = new NativeBackendProbe(independentRunner);
            var first = Task.Run(() => independentProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, cancelFirst.Token));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            independentProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
            cancelFirst.Cancel();
            bool cancelled = false;
            try { await first; } catch (OperationCanceledException) { cancelled = true; }
            independentProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
            check(cancelled && independentRunner.Calls == 2, "Cancelling one in-flight caller leaves another caller and its completed success intact.");
        }
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var raceRunner = new FixtureRunner(async (call, _, _) =>
            {
                if (call == 1) { started.SetResult(); await release.Task; }
                return new(0, "ready", "");
            });
            var raceProbe = new NativeBackendProbe(raceRunner);
            var inFlight = Task.Run(() => raceProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            raceProbe.Invalidate(payload, FilamentBackend.Vulkan);
            release.SetResult(); await inFlight;
            raceProbe.EnsureAvailable(payload, FilamentBackend.Vulkan, default);
            check(raceRunner.Calls == 2, "An older in-flight probe cannot resurrect a cache entry invalidated by a runtime failure.");
        }

        string apphost = Path.Combine(AppContext.BaseDirectory, "HeyMelody.Rendering.Filament.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(apphost)) throw new FileNotFoundException("Portable process checks require the test apphost.", apphost);
        var processRunner = new NativeBackendProbeProcess(TimeSpan.FromSeconds(10));
        var bounded = await processRunner.RunAsync(apphost, FilamentBackend.OpenGL, default);
        check(bounded.Succeeded && bounded.StandardOutput.StartsWith("backend=opengl\n")
            && bounded.StandardOutput.Length == NativeBackendProbeProcess.MaximumLogCharacters
            && bounded.StandardError.Length == NativeBackendProbeProcess.MaximumLogCharacters,
            "The actual child gets explicit arguments and both redirected streams drain with bounded retained text.");
        var failed = await processRunner.RunAsync(apphost, FilamentBackend.Metal, default);
        check(failed.ExitCode == 17 && !failed.Succeeded && failed.StandardError.Contains("fixture backend unavailable"),
            "An actual child failure preserves its bounded diagnostic and nonzero exit status.");
        string? previousMarker = Environment.GetEnvironmentVariable(MarkerVariable);
        try
        {
            string timeoutMarker = Path.Combine(fixtureRoot, "timeout-child.pid");
            Environment.SetEnvironmentVariable(MarkerVariable, timeoutMarker);
            var timed = await new NativeBackendProbeProcess(TimeSpan.FromSeconds(2)).RunAsync(apphost, FilamentBackend.Vulkan, default);
            check(timed.TimedOut && !timed.Succeeded && File.Exists(timeoutMarker) && ChildExited(timeoutMarker),
                "A timed-out child is terminated and cannot continue running in the background.");
            string cancelMarker = Path.Combine(fixtureRoot, "cancel-child.pid");
            Environment.SetEnvironmentVariable(MarkerVariable, cancelMarker);
            using var cancellation = new CancellationTokenSource();
            var running = processRunner.RunAsync(apphost, FilamentBackend.Vulkan, cancellation.Token);
            await WaitForFileAsync(cancelMarker);
            cancellation.Cancel();
            bool cancelled = false;
            try { await running; } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && ChildExited(cancelMarker), "Cancelling a running child kills only the owned probe and surfaces cancellation.");
        }
        finally { Environment.SetEnvironmentVariable(MarkerVariable, previousMarker); }

        var probeMissingPayload = new NativeProbePayload(Path.Combine(fixtureRoot, "missing-probe"), "missing-executable");
        bool startFailure = false;
        try { new NativeBackendProbe(processRunner).EnsureAvailable(probeMissingPayload, FilamentBackend.Vulkan, default); }
        catch (FilamentRendererException error) { startFailure = error.Kind == FilamentFailureKind.ComponentUnavailable; }
        check(startFailure, "An actual executable start failure remains a component error.");
    }

    private static bool ChildExited(string marker)
    {
        try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(marker))); return child.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task WaitForFileAsync(string marker)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker), out _)) await Task.Delay(20, limit.Token);
    }

    private sealed class FixtureRunner(Func<int, FilamentBackend, CancellationToken, Task<NativeProbeResult>> run) : INativeBackendProbeRunner
    {
        private int _calls;
        internal int Calls => _calls;
        internal List<FilamentBackend> Backends { get; } = [];
        public Task<NativeProbeResult> RunAsync(string executablePath, FilamentBackend backend, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (Backends) Backends.Add(backend);
            return run(call, backend, cancellationToken);
        }
    }
}
