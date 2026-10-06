using System.Text.Json;

namespace HeyMelodyWindows.App;

internal static class ThemePreferences
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "preferences.json");

    public static string Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("theme", out var value) || value.ValueKind != JsonValueKind.String)
                return "system";
            var theme = value.GetString();
            return theme is "light" or "dark" ? theme : "system";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return "system"; }
    }

    public static bool Save(string theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { theme }));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
