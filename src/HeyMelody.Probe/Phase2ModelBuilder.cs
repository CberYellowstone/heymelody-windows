using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Probe;

/// <summary>Projects verified identity, command metadata and observed states without sending packets.</summary>
public static class Phase2ModelBuilder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Populate(ProbeResult report)
    {
        report.DeviceModel = null;
        if (!report.Target.IdentityVerified || report.Target.ProductId != ReadOnlySession.X4ProductId)
            return;

        var profile = DeviceModelResolver.FindByProductId(report.Target.ProductId)
            ?? throw new InvalidDataException("The verified product has no matching device profile.");
        var capability = report.Queries.SingleOrDefault(query => query.RequestCommand == "0x0100" && query.Success);
        if (capability is null || !capability.Data.TryGetValue("capabilityBitmapHex", out var rawBitmap)
            || rawBitmap is not string bitmapHex)
        {
            if (report.Success)
                throw new InvalidDataException("A successful receipt requires verified command metadata.");
            return;
        }
        var commands = CommandCapabilities.Parse(Convert.FromHexString(bitmapHex));
        var states = new Dictionary<byte, byte>();
        var query = report.Queries.SingleOrDefault(item => item.RequestCommand == "0x010D" && item.Success);
        if (query is not null && query.Data.TryGetValue("featureValues", out var rawStates)
            && rawStates is IReadOnlyDictionary<string, byte> featureValues)
        {
            foreach (var item in featureValues)
            {
                if (item.Key.Length != 2 || !byte.TryParse(item.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var feature))
                    throw new InvalidDataException("Invalid feature-state projection.");
                // Retain all returned values in query evidence, but project only the requested feature.
                if (feature == 0x18 && !states.TryAdd(feature, item.Value))
                    throw new InvalidDataException("Duplicate feature-state projection.");
            }
        }

        var capabilities = CapabilityResolver.Resolve(profile, commands, states, versions: null);
        report.DeviceModel = JsonSerializer.SerializeToElement(new
        {
            identityMatch = "product-id-exact",
            profile,
            capabilities,
            settingsTransmissionEnabled = false,
            stateObservationScope = "feature-18-only",
            versionConditionContext = "not-supplied",
            capabilitySource = "this-session-0x0100-response",
            note = "Command support and configured options are metadata; they do not certify successful settings or unqueried feature states."
        }, Options);
    }
}

