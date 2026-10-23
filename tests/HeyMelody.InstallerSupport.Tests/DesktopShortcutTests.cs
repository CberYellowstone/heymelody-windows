using System.Text;
using System.Text.Json;
using HeyMelodyWindows.InstallerSupport;
using static HeyMelodyWindows.InstallerSupport.Tests.Program;

namespace HeyMelodyWindows.InstallerSupport.Tests;

internal static class DesktopShortcutTests
{
    internal static int Run(string output)
    {
        var directory = Path.Combine(output, "desktop-shortcut");
        Directory.CreateDirectory(directory);
        MissingDefaults(directory);
        ExplicitChoices(directory);
        RepeatedReadsAndWrites(directory);
        PreserveUnrelatedSettings(directory);
        InvalidSettings(directory);
        WriteFailure(directory);
        IndependentPreferences(directory);
        Console.WriteLine("PASS desktop shortcut defaults, both explicit choices, durable false, isolated JSON preservation and failures");
        return 7;
    }

    private static void MissingDefaults(string directory)
    {
        var path = Path.Combine(directory, "not-created", "installer-preferences.json");
        Check(DesktopShortcutPreferences.Read(path), "A missing installer preference defaults to a checked desktop shortcut.");
        Check(!Directory.Exists(Path.GetDirectoryName(path)), "A preference read must not create the current-user directory.");
        var existing = Path.Combine(directory, "missing-field.json");
        var original = " { \"otherPreference\" : 42 }\r\n";
        File.WriteAllText(existing, original);
        Check(DesktopShortcutPreferences.Read(existing), "A missing desktopShortcut field also defaults to true.");
        Check(File.ReadAllText(existing) == original, "Reading the default must not rewrite unrelated settings.");
    }

    private static void ExplicitChoices(string directory)
    {
        var falsePath = Path.Combine(directory, "fresh-false", "installer-preferences.json");
        DesktopShortcutPreferences.Write(falsePath, false);
        Check(!DesktopShortcutPreferences.Read(falsePath), "The user's explicit unchecked choice is persisted.");
        using (var document = JsonDocument.Parse(File.ReadAllBytes(falsePath)))
            Check(document.RootElement.EnumerateObject().Count() == 1
                && document.RootElement.GetProperty("desktopShortcut").ValueKind == JsonValueKind.False,
                "A fresh unchecked choice writes exactly the dedicated JSON boolean false.");
        DesktopShortcutPreferences.Write(falsePath, true);
        Check(DesktopShortcutPreferences.Read(falsePath), "The user can explicitly re-enable the desktop shortcut.");
        DesktopShortcutPreferences.Write(falsePath, false);
        Check(!DesktopShortcutPreferences.Read(falsePath), "The user can subsequently disable the desktop shortcut again.");

        var truePath = Path.Combine(directory, "fresh-true", "installer-preferences.json");
        DesktopShortcutPreferences.Write(truePath, true);
        using var saved = JsonDocument.Parse(File.ReadAllBytes(truePath));
        Check(saved.RootElement.GetProperty("desktopShortcut").ValueKind == JsonValueKind.True,
            "An explicit checked choice must be recorded even when the missing-value default is already true.");
    }

    private static void RepeatedReadsAndWrites(string directory)
    {
        var path = Path.Combine(directory, "durable-false.json");
        var original = Encoding.UTF8.GetBytes(" { \"desktopShortcut\" : false, \"other\" : \"keep\" }\r\n");
        File.WriteAllBytes(path, original);
        for (var install = 0; install < 3; install++)
            Check(!DesktopShortcutPreferences.Read(path), "An unattended installation must retain the saved unchecked choice.");
        DesktopShortcutPreferences.Write(path, false);
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Reads and repeated saves of the same choice must not rewrite bytes.");
    }

    private static void PreserveUnrelatedSettings(string directory)
    {
        var path = Path.Combine(directory, "other-settings.json");
        var nested = new string('[', 80) + "42" + new string(']', 80);
        var original = "{\"desktopShortcut\":true,\"integer\":123456789012345678901234567890,"
            + "\"decimal\":1.23456789012345678901234567890e+123,\"Case\":1,\"case\":2,"
            + "\"other\":{\"text\":\"耳机\",\"empty\":null},\"deep\":" + nested + "}";
        File.WriteAllText(path, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        DesktopShortcutPreferences.Write(path, false);
        var options = new JsonDocumentOptions { MaxDepth = 256 };
        using var before = JsonDocument.Parse(original, options);
        using var after = JsonDocument.Parse(File.ReadAllBytes(path), options);
        Check(after.RootElement.GetProperty("desktopShortcut").ValueKind == JsonValueKind.False,
            "Saving an unchecked choice changes only the desktop preference.");
        foreach (var property in before.RootElement.EnumerateObject().Where(property => property.Name != "desktopShortcut"))
        {
            var value = after.RootElement.GetProperty(property.Name);
            Check(JsonElement.DeepEquals(property.Value, value), "Unrelated installer preference changed: " + property.Name);
            if (property.Value.ValueKind == JsonValueKind.Number)
                Check(property.Value.GetRawText() == value.GetRawText(), "Numeric precision changed: " + property.Name);
        }
    }

    private static void InvalidSettings(string directory)
    {
        var path = Path.Combine(directory, "invalid.json");
        foreach (var original in new[]
        {
            "{", "[]", "null", "true", "{\"desktopShortcut\":null}",
            "{\"desktopShortcut\":\"false\"}", "{\"desktopShortcut\":0}"
        })
        {
            File.WriteAllText(path, original);
            Throws(() => DesktopShortcutPreferences.Read(path), "Malformed installer preferences must not silently default to true.");
            Throws(() => DesktopShortcutPreferences.Write(path, false), "An explicit choice must not silently replace malformed preferences.");
            Check(File.ReadAllText(path) == original, "Malformed installer preferences must be preserved.");
        }
        var invalidUtf8 = new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d };
        File.WriteAllBytes(path, invalidUtf8);
        Throws(() => DesktopShortcutPreferences.Read(path), "Malformed encoding must report a read failure.");
        Throws(() => DesktopShortcutPreferences.Write(path, true), "Malformed encoding must report a write failure.");
        Check(File.ReadAllBytes(path).SequenceEqual(invalidUtf8), "Malformed encoding must be preserved byte for byte.");
    }

    private static void WriteFailure(string directory)
    {
        var blocker = Path.Combine(directory, "blocked-parent");
        File.WriteAllText(blocker, "preserve the blocker");
        var obstructed = Path.Combine(blocker, "installer-preferences.json");
        Throws(() => DesktopShortcutPreferences.Read(obstructed), "An obstructed preference directory is a read failure.");
        Throws(() => DesktopShortcutPreferences.Write(obstructed, false), "An obstructed preference directory is a write failure.");
        Check(File.ReadAllText(blocker) == "preserve the blocker", "The file obstructing the directory must be retained.");
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(directory, "locked.json");
        File.WriteAllText(path, "{\"desktopShortcut\":true,\"other\":42}");
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => DesktopShortcutPreferences.Write(path, false), "A file locked against replacement must fail without corruption.");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "A failed atomic replacement must preserve the original preference.");
        Check(Directory.GetFiles(directory, ".installer-preferences-*.tmp").Length == 0,
            "A failed atomic replacement must remove its own temporary file.");
    }

    private static void IndependentPreferences(string directory)
    {
        var storage = Path.Combine(directory, "separate-storage");
        Directory.CreateDirectory(storage);
        var gpuPath = Path.Combine(storage, "preferences.json");
        var gpuBytes = Encoding.UTF8.GetBytes("{\"uiBasicRenderMode\":false,\"theme\":\"dark\"}");
        File.WriteAllBytes(gpuPath, gpuBytes);
        var installerPath = Path.Combine(storage, "installer-preferences.json");
        DesktopShortcutPreferences.Write(installerPath, false);
        Check(!DesktopShortcutPreferences.Read(installerPath), "The installer preference is available independently of GUI preferences.");
        Check(File.ReadAllBytes(gpuPath).SequenceEqual(gpuBytes), "Saving a desktop choice must not access or alter the GPU preference file.");
    }
}
