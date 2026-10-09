using System.Text.Json;

namespace HeyMelodyWindows.App;

/// <summary>The read-only theme preference shared by the main app and device manager.</summary>
public static class ThemePreferenceReader
{
    public static string Load()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeyMelody", "preferences.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("theme", out var value)
                || value.ValueKind != JsonValueKind.String) return "system";
            return value.GetString() is "light" or "dark" ? value.GetString()! : "system";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return "system"; }
    }
}
