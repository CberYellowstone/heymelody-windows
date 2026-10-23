using System.Text;

namespace HeyMelodyWindows.InstallerSupport;

internal static class InstallationFiles
{
    internal const int MaximumManifestBytes = 1024 * 1024;
    internal const int MaximumManifestEntries = 10000;
    private const int MaximumRelativePathLength = 4096;

    internal static int CheckUnlocked(string installationRoot)
    {
        if (!Path.IsPathFullyQualified(installationRoot))
            throw new ArgumentException("The installation directory must be an absolute path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot));
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The installation root must be an ordinary directory.");

        var entries = ReadManifest(root);
        var checkedFiles = 0;
        foreach (var entry in entries)
        {
            if (!IsPresentOrdinaryFile(root, entry.Parts)) continue;
            try
            {
                // Read-only access with no sharing detects locks without changing or deleting the payload.
                using var file = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                checkedFiles++;
            }
            catch (FileNotFoundException) { } // A partial installation or a concurrent deletion is recoverable.
            catch (DirectoryNotFoundException) { }
        }
        return checkedFiles;
    }

    private static IReadOnlyList<ManifestEntry> ReadManifest(string root)
    {
        var manifestParts = new[] { ".installer", "payload-files.txt" };
        if (!IsPresentOrdinaryFile(root, manifestParts))
            throw new FileNotFoundException("The installation payload manifest is missing.");
        using var file = new FileStream(Path.Combine(root, ".installer", "payload-files.txt"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        if (file.Length > MaximumManifestBytes)
            throw new InvalidDataException("The installation payload manifest exceeds 1 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var entries = new List<ManifestEntry>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (entries.Count >= MaximumManifestEntries)
                throw new InvalidDataException("The installation payload manifest contains too many entries.");
            var entry = ParseEntry(root, line);
            if (!unique.Add(entry.FullPath))
                throw new InvalidDataException("The installation payload manifest contains a duplicate path.");
            entries.Add(entry);
        }
        if (entries.Count == 0) throw new InvalidDataException("The installation payload manifest is empty.");
        return entries;
    }

    private static ManifestEntry ParseEntry(string root, string relativePath)
    {
        if (relativePath.Length > MaximumRelativePathLength || Path.IsPathRooted(relativePath)
            || relativePath[0] is '/' or '\\')
            throw new InvalidDataException("The payload manifest contains an absolute or oversized path.");
        var parts = relativePath.Split(['/', '\\']);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(character => character < ' ' || "<>:\"|?*".Contains(character)) || IsDeviceName(part))
                throw new InvalidDataException("The payload manifest contains an unsafe relative path.");
        }
        var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The payload manifest contains a path outside the installation directory.");
        return new ManifestEntry(fullPath, parts);
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(name[3]);
    }

    private static bool IsPresentOrdinaryFile(string root, IReadOnlyList<string> parts)
    {
        var current = root;
        for (var index = 0; index < parts.Count; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A payload manifest path contains a reparse point.");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (index == parts.Count - 1 ? isDirectory : !isDirectory)
                throw new InvalidDataException("A payload manifest path has an unexpected file type.");
        }
        return true;
    }

    private sealed record ManifestEntry(string FullPath, string[] Parts);
}
