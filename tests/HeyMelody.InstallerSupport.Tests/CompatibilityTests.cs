using System.Text;
using System.Text.Json;
using HeyMelodyWindows.InstallerSupport;
using static HeyMelodyWindows.InstallerSupport.Tests.Program;

namespace HeyMelodyWindows.InstallerSupport.Tests;

internal static class CompatibilityTests
{
    private static readonly DisplayAdapter Affected = new("Qualcomm Adreno GPU", [@"ACPI\VEN_QCOM&DEV_043A"]);

    internal static int Run(string output)
    {
        HardwareIdentity();
        NonMatchingHardware(output);
        FreshInstall(output);
        ExistingPreferences(output);
        AlreadyEnabled(output);
        InvalidPreferences(output);
        FailedWrite(output);
        Console.WriteLine("PASS exact present-device IDs, conservative nonmatching behavior, JSON preservation and failed-write recovery");
        return 7;
    }

    private static void HardwareIdentity()
    {
        foreach (var identity in new[]
        {
            @"ACPI\VEN_QCOM&DEV_043A", @"acpi\ven_qcom&dev_043a", @"ACPI\QCOM043A",
            @"ACPI\VEN_QCOM&DEV_043A&REV_0001", @"ACPI\QCOM043A\0", @"ACPI\VEN_QCOM&DEV_043A\0"
        })
            Check(CompatibilityPreferences.IsAffected(new DisplayAdapter("any name", [identity])), "Expected affected ID: " + identity);
        foreach (var identity in new[]
        {
            "", @"ACPI\VEN_QCOM&DEV_043A0", @"ACPI\QCOM043A0", @"ACPI\VEN_QCOM&DEV_0636",
            @"ACPI\VEN_QCOM&DEV_0C36", @"PCI\VEN_8086&DEV_043A", @"ACPI\VEN_OTHER&DEV_043A",
            @"ACPI\VEN_QCOM&DEV_043A&REV_", @"ACPI\VEN_QCOM&DEV_043A&REV_000G", @"ACPI\QCOM0636"
        })
            Check(!CompatibilityPreferences.IsAffected(new DisplayAdapter("Qualcomm(R) Adreno(TM) 690 GPU", [identity])),
                "A name or nearby hardware ID must not opt into the workaround: " + identity);
        Check(CompatibilityPreferences.IsAffected(new DisplayAdapter("GPU", [@"unrelated", @"ACPI\QCOM043A"])),
            "The adapter can expose multiple hardware IDs.");
    }

    private static void NonMatchingHardware(string output)
    {
        var path = Path.Combine(output, "nonmatching-missing", "preferences.json");
        var other = new DisplayAdapter("Qualcomm Adreno 690", [@"ACPI\VEN_QCOM&DEV_0636"]);
        Check(CompatibilityPreferences.Apply([other], path) == CompatibilityResult.NotAffected, "Gen3 remains unchanged.");
        Check(!Directory.Exists(Path.GetDirectoryName(path)), "Nonmatching hardware must not create the preference directory.");
        var existing = Path.Combine(output, "nonmatching-existing.json");
        var original = Encoding.UTF8.GetBytes("{ broken JSON\n");
        File.WriteAllBytes(existing, original);
        Check(CompatibilityPreferences.Apply([other], existing) == CompatibilityResult.NotAffected,
            "Nonmatching hardware must not even parse existing preferences.");
        Check(File.ReadAllBytes(existing).SequenceEqual(original), "Nonmatching bytes remain unchanged.");
        Check(CompatibilityPreferences.Apply([], existing) == CompatibilityResult.NotAffected, "No adapters grants no workaround.");
    }

    private static void FreshInstall(string output)
    {
        var path = Path.Combine(output, "fresh", "preferences.json");
        Check(CompatibilityPreferences.Apply([Affected], path) == CompatibilityResult.Enabled, "Affected hardware creates preferences.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Check(document.RootElement.EnumerateObject().Count() == 1 && document.RootElement.GetProperty("uiBasicRenderMode").GetBoolean(),
            "A fresh installation writes only the dedicated JSON boolean.");
    }

    private static void ExistingPreferences(string output)
    {
        var path = Path.Combine(output, "existing.json");
        var deep = new string('[', 80) + "42" + new string(']', 80);
        var original = "{\"uiBasicRenderMode\":false,\"theme\":\"dark\",\"modelCompatibilityMode\":false,"
            + "\"integer\":123456789012345678901234567890,\"decimal\":1.23456789012345678901234567890e+123,"
            + "\"CaseSensitive\":1,\"casesensitive\":2,\"nested\":{\"null\":null,\"text\":\"耳机\"},\"deep\":" + deep + "}";
        File.WriteAllText(path, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Check(CompatibilityPreferences.Apply([Affected], path) == CompatibilityResult.Enabled, "A previous false is explicitly enabled.");
        var options = new JsonDocumentOptions { MaxDepth = 256 };
        using var before = JsonDocument.Parse(original, options);
        using var after = JsonDocument.Parse(File.ReadAllBytes(path), options);
        Check(after.RootElement.GetProperty("uiBasicRenderMode").ValueKind == JsonValueKind.True, "The setting is JSON true.");
        foreach (var property in before.RootElement.EnumerateObject().Where(property => property.Name != "uiBasicRenderMode"))
        {
            var preserved = after.RootElement.GetProperty(property.Name);
            Check(JsonElement.DeepEquals(property.Value, preserved), "Unrelated preference changed: " + property.Name);
            if (property.Value.ValueKind == JsonValueKind.Number)
                Check(property.Value.GetRawText() == preserved.GetRawText(), "Numeric precision or representation changed: " + property.Name);
        }
    }

    private static void AlreadyEnabled(string output)
    {
        var path = Path.Combine(output, "already-enabled.json");
        var bytes = Encoding.UTF8.GetBytes(" { \"uiBasicRenderMode\" : true, \"theme\" : \"light\" }\r\n");
        File.WriteAllBytes(path, bytes);
        Check(CompatibilityPreferences.Apply([Affected], path) == CompatibilityResult.AlreadyEnabled, "An already-enabled setting is a no-op.");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Already-enabled preferences must not be rewritten.");
    }

    private static void InvalidPreferences(string output)
    {
        var path = Path.Combine(output, "invalid.json");
        foreach (var original in new[] { "{", "[]", "true", "null" })
        {
            File.WriteAllText(path, original);
            Throws(() => CompatibilityPreferences.Apply([Affected], path), "Invalid existing preferences must report failure.");
            Check(File.ReadAllText(path) == original, "Invalid existing preferences must be preserved.");
        }
        var invalidUtf8 = new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d };
        File.WriteAllBytes(path, invalidUtf8);
        Throws(() => CompatibilityPreferences.Apply([Affected], path), "Invalid encoding must report failure.");
        Check(File.ReadAllBytes(path).SequenceEqual(invalidUtf8), "Invalid UTF-8 must be preserved.");
    }

    private static void FailedWrite(string output)
    {
        var blocker = Path.Combine(output, "blocked-parent");
        File.WriteAllText(blocker, "preserve me");
        Throws(() => CompatibilityPreferences.Apply([Affected], Path.Combine(blocker, "preferences.json")), "A blocked directory must fail.");
        Check(File.ReadAllText(blocker) == "preserve me", "Failed directory creation must preserve the blocker.");
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(output, "locked.json");
            File.WriteAllText(path, "{\"theme\":\"dark\",\"uiBasicRenderMode\":false}");
            var original = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws(() => CompatibilityPreferences.Apply([Affected], path), "An existing file locked against replacement must report failure.");
            Check(File.ReadAllBytes(path).SequenceEqual(original), "Failed replacement must preserve original preferences.");
            Check(Directory.GetFiles(output, ".preferences-installer-*.tmp").Length == 0, "Failed replacement must remove its temporary file.");
        }
    }
}
