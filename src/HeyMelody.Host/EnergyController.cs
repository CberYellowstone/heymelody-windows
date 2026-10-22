using System.Diagnostics;

namespace HeyMelodyWindows.Host;

internal interface IEnergyApplier
{
    /// <summary>Applies one mode to one process. Failures go to <paramref name="failed"/> (capability, Win32 error); implementations never throw.</summary>
    void Apply(Process process, EnergyMode mode, Action<string, int> failed);
}

/// <summary>Set by the tray only. Worker processes and tests leave it null, which makes every energy call a no-op.</summary>
internal static class HostEnergy
{
    private static EnergyController? _controller;
    internal static EnergyController? Controller { get => Volatile.Read(ref _controller); set => Volatile.Write(ref _controller, value); }
}

/// <summary>
/// Applies the policy's mode to the tray and to every attached child. Re-application runs on the thread pool, never on the
/// caller of the policy (UI thread, supervisor lock). Failures are counted and traced once per process and capability;
/// QoS is an optimization and must neither throw nor surface as backend health.
/// </summary>
internal sealed class EnergyController : IDisposable
{
    private sealed class Attached(Process process)
    {
        internal readonly Process Process = process;
        internal readonly HashSet<string> Reported = [];
        internal Action<string, int> Report = null!;
        internal int ProcessId;
        internal EnergyMode? Applied;
        internal volatile bool Detached;
    }
    private sealed class Detach(EnergyController owner, Attached entry) : IDisposable
    {
        // Takes the apply lock so that disposal waits for an application in flight: the owner may dispose the Process right after.
        // Lock order is always _applySync then _sync, and nothing running under _applySync waits for a detaching caller.
        public void Dispose() { lock (owner._applySync) { entry.Detached = true; lock (owner._sync) owner._attached.Remove(entry); } }
    }

    private readonly EnergyPolicy _policy;
    private readonly IEnergyApplier _applier;
    // _applySync orders applications so the last pass always reads the latest mode; _sync only guards the attached list.
    private readonly object _sync = new(), _applySync = new();
    private readonly List<Attached> _attached = [];
    private int _scheduled, _disposed;
    private long _failures;
    private int _lastError;

    internal EnergyController(EnergyPolicy policy, IEnergyApplier applier)
    {
        _policy = policy; _applier = applier;
        policy.ModeChanged += OnModeChanged;
    }

    /// <summary>Number of distinct process and capability failures recorded so far.</summary>
    internal long Failures => Volatile.Read(ref _failures);
    internal int LastError => Volatile.Read(ref _lastError);

    /// <summary>Applies the current mode now and follows later changes until the result is disposed.</summary>
    internal IDisposable Attach(Process process)
    {
        var entry = new Attached(process);
        try { entry.ProcessId = process.Id; } catch { }
        entry.Report = (capability, error) => Record(entry, capability, error);
        lock (_sync) _attached.Add(entry);
        lock (_applySync) Apply(entry, _policy.Mode);
        return new Detach(this, entry);
    }

    /// <summary>Holds Default and applies it synchronously: a child inherits the creator's priority class at start.</summary>
    internal void PrepareLaunch()
    {
        _policy.HoldForLaunch();
        ApplyAll();
    }

    private void OnModeChanged(EnergyMode _)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static controller => { Volatile.Write(ref controller._scheduled, 0); controller.ApplyAll(); }, this, preferLocal: false);
    }

    private void ApplyAll()
    {
        lock (_applySync)
        {
            Attached[] entries; lock (_sync) entries = [.. _attached];
            var mode = _policy.Mode;
            foreach (var entry in entries) Apply(entry, mode);
        }
    }

    private void Apply(Attached entry, EnergyMode mode)
    {
        if (entry.Applied == mode || entry.Detached) return;
        try
        {
            if (entry.Process.HasExited) return;
            entry.Applied = mode;
            _applier.Apply(entry.Process, mode, entry.Report);
        }
        catch (Exception) { Record(entry, "apply", 0); }
    }

    private void Record(Attached entry, string capability, int error)
    {
        Volatile.Write(ref _lastError, error);
        lock (entry.Reported) if (!entry.Reported.Add(capability)) return;
        Interlocked.Increment(ref _failures);
        Trace.WriteLine($"HeyMelody energy: {capability} failed for process {entry.ProcessId} (Win32 error {error}).");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _policy.ModeChanged -= OnModeChanged;
    }
}
