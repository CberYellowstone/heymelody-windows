using System.Text.Json;

namespace HeyMelodyWindows.App;

/// <summary>Reads the ordinary UI renderer preference before any native window is initialized.</summary>
public static class UiRenderingPreferenceReader
{
    public const string PreferenceName = "uiBasicRenderMode";

    public static bool Load(string? settingsPath = null)
    {
        try
        {
            settingsPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeyMelody", "preferences.json");
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PreferenceName, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        { return false; }
    }
}
