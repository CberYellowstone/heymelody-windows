using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static HeyMelodyWindows.Audio.WasapiNative;

namespace HeyMelodyWindows.Audio;

public static class AudioPlaybackProbe
{
    /// <summary>Plays only SILENT buffers on one uniquely matched peer endpoint, for at most two seconds.
    /// Does not request driver settings, convert formats, change defaults, or stop another application's stream.</summary>
    public static Task<PlaybackReport> RunAsync(ulong bluetoothAddress, uint sampleRateHz, CancellationToken token)
    {
        if (sampleRateHz is not (96000 or 192000))
            return Task.FromResult(new PlaybackReport { RequestedSampleRateHz = sampleRateHz, FailurePhase = "ValidateRate", Failure = "Only 96000 or 192000 Hz may be probed." });
        if (token.IsCancellationRequested)
            return Task.FromResult(new PlaybackReport { RequestedSampleRateHz = sampleRateHz, FailurePhase = "BeforeStart", Cancelled = true });
        return Task.Factory.StartNew(() => Run(bluetoothAddress, sampleRateHz, token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static PlaybackReport Run(ulong address, uint rate, CancellationToken token)
    {
        var report = new PlaybackReport { RequestedSampleRateHz = rate };
        IDeviceEnumerator? enumerator = null; IDevice? endpoint = null;
        object? clientObject = null; object? renderObject = null;
        IAudioClient? client = null;
        IntPtr mix = IntPtr.Zero, requested = IntPtr.Zero;
        bool comInitialized = false, started = false;
        var clock = new Stopwatch();
        Task<(DriverAudioReport Report, long Finished)>? duringRead = null;
        long stopped = 0;
        var trace = new EndpointTrace { Phase = "Platform" };
        bool sinkChanged = false;
        PlaybackReport WithIdentityDiagnostics(PlaybackReport value) => value with
        {
            EndpointIdentityAttempts = trace.Attempts, ActiveRenderEndpointCount = trace.EndpointCount,
            ExactEndpointMatchCount = trace.MatchCount, SinkInstanceChangedDuringWait = sinkChanged,
            EndpointLastNativeFailurePhase = trace.LastNativeFailurePhase,
            EndpointLastNativeFailureHResult = trace.LastNativeFailureHResult
        };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            trace.Phase = "Com.Initialize";
            Check(CoInitializeEx(IntPtr.Zero, 0 /* MTA */)); comInitialized = true;
            var driver = new DriverAudioService(new NativeDriverCore());
            trace.Phase = "Peer.ResolveSink";
            string sink = driver.Resolve(address);
            trace.Phase = "Driver.ReadBefore";
            report = report with { DriverBefore = driver.ReadResolved(sink) };
            trace.Phase = "Com.CreateDeviceEnumerator";
            enumerator = (IDeviceEnumerator)new DeviceEnumeratorClass();
            endpoint = EndpointIdentity.WaitForStable(() =>
            {
                trace.Phase = "Peer.RefreshSink";
                string current = driver.Resolve(address);
                sinkChanged |= !string.Equals(sink, current, StringComparison.OrdinalIgnoreCase);
                sink = current;
                return EndpointIdentity.Resolve(enumerator, sink, trace);
            }, token, trace);
            report = report with { EndpointMatched = true };
            token.ThrowIfCancellationRequested();
            trace.Phase = "Client.Activate";
            Check(endpoint.Activate(AudioClientId, 1 /* INPROC_SERVER */, IntPtr.Zero, out clientObject));
            client = (IAudioClient)clientObject;
            trace.Phase = "Format.GetMixFormat";
            Check(client.GetMixFormat(out mix));
            trace.Phase = "Format.ParseMixFormat";
            report = report with { EngineMixFormat = WaveFormat.Parse(mix) };
            trace.Phase = "Format.CreateRequestedPcm";
            requested = WaveFormat.PcmStereo16(rate);
            report = report with { RequestedPcmFormat = WaveFormat.Parse(requested) };
            trace.Phase = "Format.IsSharedSupported";
            int shared = client.IsFormatSupported(0, requested, out var closest);
            try
            {
                report = report with { SharedSupportHResult = shared, RequestedPcmSharedSupported = shared == 0,
                    SharedClosestFormat = closest == IntPtr.Zero ? null : WaveFormat.Parse(closest) };
            }
            finally { if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest); }
            trace.Phase = "Format.IsExclusiveSupported";
            int exclusive = client.IsFormatSupported(1, requested, out closest);
            try { report = report with { ExclusiveSupportHResult = exclusive, RequestedPcmExclusiveSupported = exclusive == 0 }; }
            finally { if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest); }

            // Shared initialization uses the requested PCM only if the endpoint accepts it exactly.
            // Otherwise initialize the engine's own native mix (often 48 kHz), with no AUTOCONVERTPCM.
            // Exclusive support is inspected; we do not seize another application's endpoint.
            IntPtr selectedFormat = shared == 0 ? requested : mix;
            trace.Phase = "Format.ValidateSelected";
            var selected = WaveFormat.Parse(selectedFormat);
            if (!WaveFormat.IsSafeToRender(selected)) throw new InvalidOperationException("Unsafe native format.");
            token.ThrowIfCancellationRequested();
            trace.Phase = "Client.Initialize";
            Check(client.Initialize(0, 0, 500000 /* 50 ms requested buffer */, 0, selectedFormat, IntPtr.Zero));
            report = report with { Initialized = true, WasapiClientFormat = selected,
                PlaybackMode = shared == 0 ? "SharedRequestedPcm" : "SharedNativeEngineMix" };
            trace.Phase = "Buffer.GetSize";
            Check(client.GetBufferSize(out var capacity));
            if (capacity is 0 or > 2000000) throw new InvalidOperationException("Invalid render capacity.");
            trace.Phase = "Client.GetRenderService";
            Check(client.GetService(RenderClientId, out renderObject));
            var render = (IRenderClient)renderObject;
            // Bound each write to 20 ms of silence, even if the endpoint allocates a larger buffer.
            uint quantum = Math.Max(1, selected.SampleRateHz / 50);
            trace.Phase = "Buffer.PrimeGet";
            Check(render.GetBuffer(Math.Min(capacity, quantum), out _));
            trace.Phase = "Buffer.PrimeRelease";
            Check(render.ReleaseBuffer(Math.Min(capacity, quantum), Silent));
            token.ThrowIfCancellationRequested();
            trace.Phase = "Client.Start";
            Check(client.Start()); started = true; clock.Start();
            report = report with { Started = true };
            duringRead = Task.Run(() => (driver.ReadResolved(sink), Stopwatch.GetTimestamp()));
            while (clock.Elapsed < TimeSpan.FromMilliseconds(1850))
            {
                token.ThrowIfCancellationRequested();
                trace.Phase = "Buffer.GetPadding";
                Check(client.GetCurrentPadding(out var padding));
                if (padding > capacity) throw new InvalidOperationException("Invalid render padding.");
                uint available = Math.Min(capacity - padding, quantum);
                if (available != 0)
                {
                    trace.Phase = "Buffer.RenderGet";
                    Check(render.GetBuffer(available, out _));
                    trace.Phase = "Buffer.RenderRelease";
                    Check(render.ReleaseBuffer(available, Silent));
                }
                double remaining = 1850 - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0 && token.WaitHandle.WaitOne(Math.Min(10, (int)Math.Ceiling(remaining))))
                    token.ThrowIfCancellationRequested();
            }
            trace.Phase = "Client.Stop";
            Check(client.Stop()); stopped = Stopwatch.GetTimestamp(); started = false; clock.Stop();
            report = report with { SilentPlaybackMilliseconds = clock.Elapsed.TotalMilliseconds };
            if (duringRead is not null)
            {
                trace.Phase = "Driver.AwaitDuringRead";
                var sample = duringRead.GetAwaiter().GetResult();
                if (sample.Finished <= stopped) report = report with { DriverDuring = sample.Report };
            }
            trace.Phase = "Driver.ReadAfter";
            report = report with { DriverAfter = driver.ReadResolved(sink) };
            return WithIdentityDiagnostics(report);
        }
        catch (OperationCanceledException)
        {
            return WithIdentityDiagnostics(report with { FailurePhase = trace.Phase, Cancelled = true, SilentPlaybackMilliseconds = clock.Elapsed.TotalMilliseconds });
        }
        catch (Exception e)
        {
            return WithIdentityDiagnostics(report with { FailurePhase = e is EndpointMatchException match
                    ? match.MatchCount == 0 ? "Identity.NoExactMatch" : "Identity.AmbiguousExactMatch" : trace.Phase,
                HResult = e.HResult, SilentPlaybackMilliseconds = clock.Elapsed.TotalMilliseconds,
                Failure = e is EndpointMatchException identity
                    ? identity.MatchCount == 0 ? "No active render endpoint has the exact sink ancestor after the bounded wait."
                        : "Multiple active render endpoints have the exact sink ancestor; playback refused."
                    : e is InvalidOperationException ? "Endpoint identity, format or buffer could not be safely verified."
                    : e is PlatformNotSupportedException ? "Windows is required." : "WASAPI probe failed; identifiers withheld." });
        }
        finally
        {
            if (started && client is not null) { client.Stop(); clock.Stop(); }
            // Complete a read-only sample before returning, after stopping audio, including cancellation.
            if (duringRead is not null) duringRead.GetAwaiter().GetResult();
            Release(renderObject); Release(clientObject); Release(endpoint); Release(enumerator);
            if (requested != IntPtr.Zero) Marshal.FreeCoTaskMem(requested);
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            if (comInitialized) CoUninitialize();
        }
    }
}

internal static class WaveFormat
{
    internal static IntPtr PcmStereo16(uint rate)
    {
        var data = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), checked(rate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 16);
        var pointer = Marshal.AllocCoTaskMem(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return pointer;
    }

    internal static AudioFormatReport Parse(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Missing audio format.");
        ushort tag = (ushort)Marshal.ReadInt16(pointer, 0);
        ushort extra = (ushort)Marshal.ReadInt16(pointer, 16);
        if (tag == 0xFFFE && extra < 22) throw new InvalidOperationException("Truncated extensible format.");
        Guid? subFormat = tag == 0xFFFE ? Marshal.PtrToStructure<Guid>(IntPtr.Add(pointer, 24)) : null;
        return new((uint)Marshal.ReadInt32(pointer, 4), (ushort)Marshal.ReadInt16(pointer, 2),
            (ushort)Marshal.ReadInt16(pointer, 14), tag, (ushort)Marshal.ReadInt16(pointer, 12), subFormat);
    }

    internal static bool IsSafeToRender(AudioFormatReport format) => format.SampleRateHz is > 0 and <= 768000
        && format.Channels is > 0 and <= 32 && format.BlockAlign is > 0 and <= 512
        && (format.FormatTag is 1 or 3 || (format.FormatTag == 0xFFFE &&
            (format.SubFormat == WasapiNative.PcmSubFormat || format.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))));
}
