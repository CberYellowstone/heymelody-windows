using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyMelodyWindows.InstallerSupport;

internal sealed record DisplayAdapter(string Description, IReadOnlyList<string> HardwareIds);
internal enum CompatibilityResult { NotAffected, AlreadyEnabled, Enabled }

internal static class CompatibilityPreferences
{
    private const string PreferenceName = "uiBasicRenderMode";
    private const int MaximumJsonDepth = 256;

    // Chromium's Windows ARM hardware map identifies QCOM043A as the older Adreno 680/685/690 family.
    // QCOM0636 also uses the name Adreno 690, so names cannot safely select this workaround.
    // https://chromium.googlesource.com/chromium/src/+/6cce000efb9f288a9d51d42c7ab38b38beb5d77c/content/test/gpu/gpu_tests/util/host_information.py
    internal static bool IsAffected(DisplayAdapter adapter) => adapter.HardwareIds.Any(IsAffectedHardwareId);

    internal static bool IsAffectedHardwareId(string hardwareId)
    {
        var parts = hardwareId.Split('\\');
        if (parts.Length is < 2 or > 3 || !parts[0].Equals("ACPI", StringComparison.OrdinalIgnoreCase))
            return false;
        var identity = parts[1];
        if (identity.Equals("QCOM043A", StringComparison.OrdinalIgnoreCase)) return true;
        const string expanded = "VEN_QCOM&DEV_043A";
        return identity.Equals(expanded, StringComparison.OrdinalIgnoreCase)
            || identity.StartsWith(expanded + "&REV_", StringComparison.OrdinalIgnoreCase)
                && identity[(expanded.Length + 5)..].Length > 0
                && identity[(expanded.Length + 5)..].All(Uri.IsHexDigit);
    }

    internal static CompatibilityResult Apply(IEnumerable<DisplayAdapter> adapters, string settingsPath)
    {
        // Do not even open the preferences file on unrecognized hardware.
        if (!adapters.Any(IsAffected)) return CompatibilityResult.NotAffected;

        JsonObject preferences;
        try
        {
            var text = File.ReadAllText(settingsPath, new UTF8Encoding(false, true));
            preferences = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaximumJsonDepth })
                as JsonObject ?? throw new InvalidDataException("The existing preferences root is not a JSON object; it was preserved.");
        }
        catch (FileNotFoundException) { preferences = new JsonObject(); }
        catch (DirectoryNotFoundException) { preferences = new JsonObject(); }

        if (preferences[PreferenceName]?.GetValueKind() == JsonValueKind.True)
            return CompatibilityResult.AlreadyEnabled;
        preferences[PreferenceName] = true;
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, ".preferences-installer-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, MaxDepth = MaximumJsonDepth }))
                    preferences.WriteTo(writer);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, settingsPath, overwrite: true);
            return CompatibilityResult.Enabled;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
