using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyMelodyWindows.InstallerSupport;

internal static class DesktopShortcutPreferences
{
    private const string PreferenceName = "desktopShortcut";
    private const int MaximumJsonDepth = 256;

    // Reads never create a file: unattended installs can inspect this value without resetting a saved choice.
    internal static bool Read(string settingsPath) => ReadValue(ReadObject(settingsPath)) ?? true;

    internal static void Write(string settingsPath, bool enabled)
    {
        var preferences = ReadObject(settingsPath);
        if (ReadValue(preferences) == enabled) return;
        preferences[PreferenceName] = enabled;
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".installer-preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, MaxDepth = MaximumJsonDepth }))
                    preferences.WriteTo(writer);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool? ReadValue(JsonObject preferences)
    {
        if (!preferences.TryGetPropertyValue(PreferenceName, out var value)) return null;
        return value?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("The existing desktopShortcut preference is not a JSON boolean; it was preserved.")
        };
    }

    private static JsonObject ReadObject(string settingsPath)
    {
        try
        {
            var text = File.ReadAllText(settingsPath, new UTF8Encoding(false, true));
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaximumJsonDepth })
                as JsonObject ?? throw new InvalidDataException("The existing installer preferences root is not a JSON object; it was preserved.");
        }
        catch (FileNotFoundException) { return new JsonObject(); }
        catch (DirectoryNotFoundException)
        {
            // A missing directory is a first install; a file obstructing that path is a read failure.
            for (var parent = Path.GetDirectoryName(Path.GetFullPath(settingsPath)); parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new IOException("A file obstructs the installer preferences directory.");
            return new JsonObject();
        }
    }
}
