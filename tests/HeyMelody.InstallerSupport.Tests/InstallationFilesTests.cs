using System.Text;
using HeyMelodyWindows.InstallerSupport;
using static HeyMelodyWindows.InstallerSupport.Tests.Program;

namespace HeyMelodyWindows.InstallerSupport.Tests;

internal static class InstallationFilesTests
{
    internal static int Run(string output)
    {
        var directory = Path.Combine(output, "installation-files");
        Directory.CreateDirectory(directory);
        UnlockedPayloadAndExtraUserFile(directory);
        MissingRuntime(directory);
        LockedPayload(directory);
        UnsafePaths(directory);
        DuplicatePaths(directory);
        ManifestLimits(directory);
        MissingOrInvalidManifest(directory);
        IncorrectFileTypes(directory);
        Console.WriteLine("PASS installation manifest scope, payload locks, missing-runtime recovery, unsafe paths and manifest limits");
        return 8;
    }

    private static void UnlockedPayloadAndExtraUserFile(string output)
    {
        var root = CreateRoot(output, "unlocked", [@"GUI\client.dll", "backend/host.dll"]);
        WritePayload(root, "GUI/client.dll", "client bytes");
        WritePayload(root, "backend/host.dll", "backend bytes");
        var extra = Path.Combine(root, "user-notes.txt");
        File.WriteAllText(extra, "user data remains untouched");
        var manifest = Path.Combine(root, ".installer", "payload-files.txt");
        var originalManifest = File.ReadAllBytes(manifest);
        using (var lockedExtra = new FileStream(extra, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(InstallationFiles.CheckUnlocked(root) == 2, "Only manifest payload files are checked; an extra locked user file is ignored.");
        Check(File.ReadAllText(extra) == "user data remains untouched", "The extra user file must not be changed or removed.");
        Check(File.ReadAllText(Path.Combine(root, "GUI", "client.dll")) == "client bytes", "Checking a payload file is read-only.");
        Check(File.ReadAllBytes(manifest).SequenceEqual(originalManifest), "Checking an installation must not rewrite its manifest.");
    }

    private static void MissingRuntime(string output)
    {
        var root = CreateRoot(output, "partial", ["GUI/client.dll", "GUI/coreclr.dll", "backend/runtime/hostfxr.dll"]);
        WritePayload(root, "GUI/client.dll", "client remains");
        Check(InstallationFiles.CheckUnlocked(root) == 1, "Missing runtime files and directories must not prevent an uninstall retry.");
        Check(!Directory.Exists(Path.Combine(root, "backend")), "Checking missing files must not create their directories.");
    }

    private static void LockedPayload(string output)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateRoot(output, "locked", ["GUI/client.dll"]);
        var payload = WritePayload(root, "GUI/client.dll", "original bytes");
        using (var locked = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => InstallationFiles.CheckUnlocked(root), "A payload file shared for reads must still block the exclusive read-only check.");
        Check(File.ReadAllText(payload) == "original bytes", "A failed lock check must not change or remove payload bytes.");
        Check(InstallationFiles.CheckUnlocked(root) == 1, "Releasing the file lock allows a subsequent uninstall preflight.");
    }

    private static void UnsafePaths(string output)
    {
        var index = 0;
        foreach (var entry in new[]
        {
            "../outside.txt", "dir/../../outside.txt", "./client.dll", @"C:\outside.txt", "C:relative.txt",
            "/outside.txt", @"\\server\share\file.txt", "client.dll:stream", "dir/file:stream",
            "NUL", "con.txt", "COM1.log", "LPT9", "foo//bar.dll", @"folder.\client.dll", "client.dll ", "bad\0path"
        })
        {
            var root = CreateRoot(output, "unsafe-" + index++, [entry]);
            Throws(() => InstallationFiles.CheckUnlocked(root), "Unsafe manifest entry must be rejected before opening files: " + entry.Replace('\0', '?'));
        }
        var outside = Path.Combine(output, "outside.txt");
        File.WriteAllText(outside, "outside data");
        var escaped = CreateRoot(output, "explicit-escape", ["../outside.txt"]);
        Throws(() => InstallationFiles.CheckUnlocked(escaped), "A path escaping the installation root must be rejected.");
        Check(File.ReadAllText(outside) == "outside data", "An escaped manifest path must not affect outside data.");
        Throws(() => InstallationFiles.CheckUnlocked("relative-root"), "The installation root must be absolute.");
    }

    private static void DuplicatePaths(string output)
    {
        var root = CreateRoot(output, "duplicate", [@"GUI\client.dll", "gui/CLIENT.dll"]);
        Throws(() => InstallationFiles.CheckUnlocked(root), "Case and separator aliases must be recognized as duplicate manifest entries.");
    }

    private static void ManifestLimits(string output)
    {
        var oversized = CreateRoot(output, "oversized", ["client.dll"]);
        using (var file = new FileStream(Path.Combine(oversized, ".installer", "payload-files.txt"), FileMode.Open, FileAccess.Write))
            file.SetLength(InstallationFiles.MaximumManifestBytes + 1L);
        Throws(() => InstallationFiles.CheckUnlocked(oversized), "An oversized manifest must be rejected before parsing.");
        var tooMany = CreateRoot(output, "too-many", Enumerable.Range(0, InstallationFiles.MaximumManifestEntries + 1)
            .Select(index => "missing-" + index + ".dll"));
        Throws(() => InstallationFiles.CheckUnlocked(tooMany), "An excessive entry count must be rejected.");
        var longPath = CreateRoot(output, "long-path", [new string('a', 4097)]);
        Throws(() => InstallationFiles.CheckUnlocked(longPath), "An oversized individual path must be rejected.");
    }

    private static void MissingOrInvalidManifest(string output)
    {
        var missing = Path.Combine(output, "missing-manifest");
        Directory.CreateDirectory(missing);
        Throws(() => InstallationFiles.CheckUnlocked(missing), "Missing payload files are recoverable, but the manifest itself is required.");
        var empty = CreateRoot(output, "empty", []);
        Throws(() => InstallationFiles.CheckUnlocked(empty), "An empty manifest cannot stand in for a generated payload list.");
        var invalid = CreateRoot(output, "invalid-encoding", ["client.dll"]);
        File.WriteAllBytes(Path.Combine(invalid, ".installer", "payload-files.txt"), [0xFF, 0xFF, 0xFF]);
        Throws(() => InstallationFiles.CheckUnlocked(invalid), "Invalid manifest encoding must report a failure.");
        if (!OperatingSystem.IsWindows()) return;
        var locked = CreateRoot(output, "locked-manifest", ["client.dll"]);
        using (var file = new FileStream(Path.Combine(locked, ".installer", "payload-files.txt"),
            FileMode.Open, FileAccess.Read, FileShare.None))
            Throws(() => InstallationFiles.CheckUnlocked(locked), "An unreadable manifest must report a failure.");
        using (var file = new FileStream(Path.Combine(locked, ".installer", "payload-files.txt"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => InstallationFiles.CheckUnlocked(locked), "A shared reader retaining the manifest must also be detected before metadata cleanup.");
    }

    private static void IncorrectFileTypes(string output)
    {
        var directoryEntry = CreateRoot(output, "directory-entry", ["GUI"]);
        Directory.CreateDirectory(Path.Combine(directoryEntry, "GUI"));
        Throws(() => InstallationFiles.CheckUnlocked(directoryEntry), "A directory must not be accepted as a manifest file.");
        var blockedParent = CreateRoot(output, "file-as-parent", ["GUI/client.dll"]);
        File.WriteAllText(Path.Combine(blockedParent, "GUI"), "extra file");
        Throws(() => InstallationFiles.CheckUnlocked(blockedParent), "A file obstructing a payload directory must be reported.");
    }

    private static string CreateRoot(string output, string name, IEnumerable<string> entries)
    {
        var root = Path.Combine(output, name);
        Directory.CreateDirectory(Path.Combine(root, ".installer"));
        File.WriteAllText(Path.Combine(root, ".installer", "payload-files.txt"),
            string.Join("\n", entries) + "\n", new UTF8Encoding(false));
        return root;
    }

    private static string WritePayload(string root, string relativePath, string text)
    {
        var path = Path.Combine(root, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
