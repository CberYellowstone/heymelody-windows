using System.Text.Json;

namespace HeyMelodyWindows.App;

internal static class ThemePreferences
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "preferences.json");

    public static string Load() => ThemePreferenceReader.Load();

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
