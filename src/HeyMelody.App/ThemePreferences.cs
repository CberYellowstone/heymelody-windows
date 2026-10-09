using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyMelodyWindows.App;

internal static class ThemePreferences
{
    internal static string? SettingsPathOverride { get; set; }
    private static string SettingsPath => SettingsPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "preferences.json");

    public static string Load()
    {
        var value = Read()["theme"];
        return value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } theme
            && theme is "light" or "dark" ? theme : "system";
    }

    // Only an explicit saved false opts into software; older or invalid settings use the GPU default.
    internal static bool LoadModelGpuAcceleration() => Read()["modelGpuAcceleration"]?.GetValueKind() != JsonValueKind.False;

    public static bool Save(string theme, bool? modelGpuAcceleration = null)
    {
        string? temporary = null;
        try
        {
            var preferences = Read();
            preferences["theme"] = theme;
            if (modelGpuAcceleration is { } enabled) preferences["modelGpuAcceleration"] = enabled;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, preferences.ToJsonString());
            File.Move(temporary, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JsonObject Read()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(); }
    }
}
