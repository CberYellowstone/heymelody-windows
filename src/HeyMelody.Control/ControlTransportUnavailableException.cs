namespace HeyMelodyWindows.Control;

public enum ControlTransportFailure
{
    ServiceNotFound,
    AmbiguousServices
}

/// <summary>A safe, structured reason for failing to select a configured control transport.</summary>
public sealed class ControlTransportUnavailableException(ControlTransportFailure reason) : IOException(
    reason == ControlTransportFailure.ServiceNotFound
        ? "No configured control service was found."
        : "The configured control service could not be selected uniquely.")
{
    public ControlTransportFailure Reason { get; } = reason;
}
