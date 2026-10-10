using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HeyMelodyWindows.Calibration;
using static HeyMelodyWindows.Host.WasapiNative;

namespace HeyMelodyWindows.Host;

internal interface ICalibrationAuditionPlayer : IAsyncDisposable
{
    event Action<IReadOnlyList<double>>? Spectrum;
    event Action? Failed;
    Task PrepareAsync(ulong address, CancellationToken token);
    Task PauseAsync(CancellationToken token);
    Task PlayAsync(CancellationToken token);
    Task StopAsync();
}

internal static partial class AuditionEndpointAddress
{
    [GeneratedRegex(@"(?:^|&)([0-9A-Fa-f]{12})(?:_C[0-9A-Fa-f]{8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressTail();
    internal static bool TryParse(string? id, out ulong address)
    {
        address = 0;
        if (id is null || !id.StartsWith(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_", StringComparison.OrdinalIgnoreCase)) return false;
        string[] segments = id.Split('\\');
        if (segments.Length != 3) return false;
        var match = AddressTail().Match(segments[2]);
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
            && address is > 0 and <= 0xFFFFFFFFFFFF;
    }
}

internal sealed class AuditionWave
{
    internal short[] Samples { get; }
    internal int Frames => Samples.Length / 2;
    private AuditionWave(short[] samples) => Samples = samples;
    internal static AuditionWave Read(Stream stream)
    {
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (bytes.Length is < 44 or > 32_000_000 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8)
            throw new InvalidDataException("试听音频不可用。");
        bool format = false; byte[]? pcm = null;
        int offset = 12;
        for (; offset + 8 <= bytes.Length;)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            int available = bytes.Length - offset - 8;
            if (length > available || ((length & 1) != 0 && length == available)) throw new InvalidDataException("试听音频不可用。");
            var chunk = bytes.AsSpan(offset + 8, (int)length);
            if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (format || length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 48000
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != 192000 || BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 4
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16) throw new InvalidDataException("试听音频格式不可用。");
                format = true;
            }
            else if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8))
            { if (pcm is not null || length == 0 || length % 4 != 0) throw new InvalidDataException("试听音频不可用。"); pcm = chunk.ToArray(); }
            offset = checked(offset + 8 + (int)length + ((int)length & 1));
        }
        if (offset != bytes.Length || !format || pcm is null) throw new InvalidDataException("试听音频不可用。");
        short[] samples = new short[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        return new(samples);
    }
}

/// <summary>One private shared-mode stream. First activation and all COM calls stay on a dedicated STA thread.</summary>
internal sealed class WasapiCalibrationAuditionPlayer : ICalibrationAuditionPlayer
{
    private sealed record Command(Action Action, CancellationToken Token, TaskCompletionSource Completion);
    private readonly BlockingCollection<Command> _commands = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Func<int> _initializeCom;
    private readonly Action _uninitializeCom;
    private int _disposed;
    private IDeviceEnumerator? _enumerator;
    private IDevice? _endpoint;
    private object? _clientObject, _renderObject;
    private IAudioClient? _client;
    private IRenderClient? _render;
    private AuditionWave? _wave;
    private uint _capacity;
    private int _cursor;
    private bool _playing;
    private long _lastSpectrum;
    private readonly HearingSpectrumAnalyzer _analyzer = new();
    public event Action<IReadOnlyList<double>>? Spectrum;
    public event Action? Failed;
    internal WasapiCalibrationAuditionPlayer(Func<int>? initializeCom = null, Action? uninitializeCom = null)
    {
        _initializeCom = initializeCom ?? (() => CoInitializeEx(IntPtr.Zero, 2));
        _uninitializeCom = uninitializeCom ?? CoUninitialize;
        _thread = new(Run) { IsBackground = true, Name = "HeyMelody calibration audition" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    public Task PrepareAsync(ulong address, CancellationToken token) => Enqueue(() => Prepare(address, token), token);
    public Task PauseAsync(CancellationToken token) => Enqueue(Pause, token);
    public Task PlayAsync(CancellationToken token) => Enqueue(() =>
    {
        if (_client is null || _render is null || _wave is null) throw new InvalidOperationException("耳机音频输出尚未就绪。");
        if (_playing) return;
        Fill(); Check(_client.Start()); _playing = true; _lastSpectrum = 0;
    }, token);
    public Task StopAsync() => Volatile.Read(ref _disposed) != 0 ? _finished.Task : Enqueue(Cleanup, CancellationToken.None);
    private Task Enqueue(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return Task.FromException(new ObjectDisposedException(nameof(WasapiCalibrationAuditionPlayer)));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _commands.Add(new(action, token, completion)); }
        catch (InvalidOperationException) { completion.TrySetException(new ObjectDisposedException(nameof(WasapiCalibrationAuditionPlayer))); }
        return completion.Task;
    }
    private void Run()
    {
        bool initialized = false;
        try
        {
            Check(_initializeCom()); initialized = true;
            while (!_commands.IsCompleted)
            {
                if (_commands.TryTake(out var command, _playing ? 5 : 50))
                {
                    try { command.Token.ThrowIfCancellationRequested(); command.Action(); command.Completion.TrySetResult(); }
                    catch (OperationCanceledException) { command.Completion.TrySetCanceled(command.Token); }
                    catch (Exception error) { command.Completion.TrySetException(error); }
                }
                if (!_playing) continue;
                try
                {
                    Fill();
                    long now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(_lastSpectrum, now) >= TimeSpan.FromMilliseconds(50))
                    {
                        _lastSpectrum = now; PublishSpectrum();
                    }
                }
                catch { Cleanup(); try { Failed?.Invoke(); } catch { } }
            }
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _disposed, 1); _commands.CompleteAdding();
            while (_commands.TryTake(out var command)) command.Completion.TrySetException(error);
        }
        finally
        {
            try { Cleanup(); if (initialized) _uninitializeCom(); }
            catch { }
            finally { _finished.TrySetResult(); }
        }
    }
    private void Prepare(ulong address, CancellationToken token)
    {
        if (_client is not null) return;
        if (address is 0 or > 0xFFFFFFFFFFFF) throw new ArgumentOutOfRangeException(nameof(address));
        try
        {
            using var stream = typeof(WasapiCalibrationAuditionPlayer).Assembly.GetManifestResourceStream("HeyMelody.Host.Assets.hearing_enhancement_audition.wav")
                ?? throw new InvalidDataException("试听音频不可用。");
            _wave = AuditionWave.Read(stream);
            _enumerator = (IDeviceEnumerator)new DeviceEnumeratorClass();
            var trace = new EndpointTrace();
            _endpoint = EndpointIdentity.WaitForStable(() => EndpointIdentity.Resolve(_enumerator, address, trace), token, trace);
            token.ThrowIfCancellationRequested();
            Check(_endpoint.Activate(AudioClientId, 1, IntPtr.Zero, out _clientObject)); _client = (IAudioClient)_clientObject;
            IntPtr format = Marshal.AllocCoTaskMem(18);
            try
            {
                byte[] data = new byte[18];
                BinaryPrimitives.WriteUInt16LittleEndian(data, 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 48000); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 192000);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 4); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 16);
                Marshal.Copy(data, 0, format, 18);
                // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY lets the shared engine use its existing mix without changing driver/default settings.
                Check(_client.Initialize(0, 0x88000000, 500000, 0, format, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(format); }
            Check(_client.GetBufferSize(out _capacity));
            if (_capacity is 0 or > 96000) throw new InvalidDataException("耳机音频缓冲区不可用。");
            Check(_client.GetService(RenderClientId, out _renderObject)); _render = (IRenderClient)_renderObject;
            _cursor = 0; _analyzer.Reset();
        }
        catch { Cleanup(); throw; }
    }
    private void Fill()
    {
        Check(_client!.GetCurrentPadding(out uint padding));
        if (padding > _capacity) throw new InvalidDataException();
        uint count = _capacity - padding;
        if (count == 0) return;
        Check(_render!.GetBuffer(count, out IntPtr buffer));
        try
        {
            var output = new short[checked((int)count * 2)];
            for (int i = 0; i < count; i++)
            {
                output[i * 2] = _wave!.Samples[_cursor * 2]; output[i * 2 + 1] = _wave.Samples[_cursor * 2 + 1];
                _cursor = (_cursor + 1) % _wave.Frames;
            }
            Marshal.Copy(output, 0, buffer, output.Length);
        }
        finally { Check(_render.ReleaseBuffer(count, 0)); }
    }
    private void Pause()
    {
        if (!_playing || _client is null) return;
        Check(_client.Stop()); _playing = false;
        Check(_client.GetCurrentPadding(out uint padding));
        _cursor = (_cursor - (int)padding % _wave!.Frames + _wave.Frames) % _wave.Frames;
        Check(_client.Reset()); _analyzer.Reset();
    }
    private void PublishSpectrum()
    {
        Check(_client!.GetCurrentPadding(out uint padding));
        int start = (_cursor - (int)padding % _wave!.Frames + _wave.Frames) % _wave.Frames;
        float[] mono = new float[1024];
        for (int i = 0; i < mono.Length; i++)
        {
            int sample = ((start + i) % _wave.Frames) * 2;
            mono[i] = (_wave.Samples[sample] + _wave.Samples[sample + 1]) / 65536f;
        }
        try { Spectrum?.Invoke(_analyzer.Analyze(mono)); } catch { }
    }
    private void Cleanup()
    {
        if (_playing) { try { _client?.Stop(); } catch { } }
        _playing = false;
        foreach (object? value in new object?[] { _renderObject, _clientObject, _endpoint, _enumerator })
            try { Release(value); } catch { }
        _renderObject = _clientObject = null; _render = null; _client = null; _endpoint = null; _enumerator = null;
        _wave = null; _cursor = 0;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _commands.CompleteAdding();
        await _finished.Task;
    }
}
