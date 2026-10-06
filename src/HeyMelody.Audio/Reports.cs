namespace HeyMelodyWindows.Audio;

/// <summary>Shareable numeric report. No address, instance ID, endpoint ID or device name is exposed.</summary>
public sealed record DriverAudioReport
{
    public bool PeerMatched { get; init; }
    public bool ReadSucceeded { get; init; }
    public uint InterfaceVersion { get; init; }
    public uint Mode { get; init; }
    public uint Codec { get; init; }
    public uint StreamState { get; init; }
    public uint[] SampleRates { get; init; } = [];
    public uint[] BitDepths { get; init; } = [];
    public uint[] Qualities { get; init; } = [];
    public uint ReportedTargetSampleRateHz { get; init; }
    public uint ReportedTargetBitDepth { get; init; }
    public uint ReportedTargetQualityIndex { get; init; }
    public uint? WorkingQualityIndex { get; init; }
    public uint? WireActualSampleRateHz => null;
    public bool WireActualVerified => false;
    public bool LowLatency { get; init; }
    public bool SupportsLowLatency { get; init; }
    public int? Win32Error { get; init; }
    public string? Failure { get; init; }
}

public sealed record DriverRateResult
{
    public uint RequestedSampleRateHz { get; init; }
    public DriverAudioReport? Before { get; init; }
    public DriverAudioReport? After { get; init; }
    public bool FreshCapabilitiesAllowRequest { get; init; }
    public bool SubmissionAttempted { get; init; }
    public bool Accepted { get; init; }
    public bool TargetMatches { get; init; }
    public bool PersistenceRequested { get; init; }
    public bool NeedStreamRestart { get; init; }
    public int? Win32Error { get; init; }
    public bool RequiresElevationBeforeSend => Win32Error == 5 && !SubmissionAttempted;
    public bool MayRetryAutomatically => false;
    public uint? WireActualSampleRateHz => null;
    public bool WireActualVerified => false;
    public string Message { get; init; } = "";
}

public sealed record AudioFormatReport(uint SampleRateHz, ushort Channels, ushort BitsPerSample,
    ushort FormatTag, ushort BlockAlign, Guid? SubFormat = null);

public sealed record PlaybackReport
{
    public string? FailurePhase { get; init; }
    public int EndpointIdentityAttempts { get; init; }
    public int? ActiveRenderEndpointCount { get; init; }
    public int? ExactEndpointMatchCount { get; init; }
    public bool SinkInstanceChangedDuringWait { get; init; }
    public string? EndpointLastNativeFailurePhase { get; init; }
    public int? EndpointLastNativeFailureHResult { get; init; }
    public uint RequestedSampleRateHz { get; init; }
    public bool EndpointMatched { get; init; }
    public string EndpointIdentityMethod { get; init; } = "PnP ancestry to exact Audio Sink instance";
    public AudioFormatReport? EngineMixFormat { get; init; }
    public AudioFormatReport? RequestedPcmFormat { get; init; }
    public bool RequestedPcmSharedSupported { get; init; }
    public bool RequestedPcmExclusiveSupported { get; init; }
    public int? SharedSupportHResult { get; init; }
    public int? ExclusiveSupportHResult { get; init; }
    public AudioFormatReport? SharedClosestFormat { get; init; }
    public AudioFormatReport? WasapiClientFormat { get; init; }
    public string? PlaybackMode { get; init; }
    public bool Initialized { get; init; }
    public bool Started { get; init; }
    public bool Cancelled { get; init; }
    public double SilentPlaybackMilliseconds { get; init; }
    public DriverAudioReport? DriverBefore { get; init; }
    public DriverAudioReport? DriverDuring { get; init; }
    public DriverAudioReport? DriverAfter { get; init; }
    public uint? WireActualSampleRateHz => null;
    public bool WireActualVerified => false;
    public int? HResult { get; init; }
    public string? Failure { get; init; }
}
