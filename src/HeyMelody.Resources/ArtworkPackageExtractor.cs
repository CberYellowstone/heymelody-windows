using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyMelodyWindows.Resources;

/// <summary>
/// Extracts a vendor artwork archive and indexes the resources its config.json declares.
/// Unsafe archive members or configured paths reject the archive. A configured resource that is
/// missing, unsupported or (for an animation) references images outside the package is skipped,
/// so the remaining artwork stays usable.
/// </summary>
internal static class ArtworkPackageExtractor
{
    internal const int SchemaVersion = 2;
    internal const string MarkerFile = "package.json";
    internal const int AnimationBytes = 24 * 1024 * 1024;
    private static readonly string[] ImageExtensions = [".png", ".webp", ".jpg", ".jpeg"];
    private static readonly JsonDocumentOptions AnimationJson = new() { MaxDepth = 128 };

    internal static ResourceArtwork[] Extract(byte[] bytes, string root, ResourceSource source, string productId, int color)
    {
        ExtractArchive(bytes, root);
        string configPath = Path.Combine(root, "config.json");
        if (!File.Exists(configPath)) throw new InvalidDataException("Resource package lacks config.json.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(configPath), new JsonDocumentOptions { MaxDepth = 64 });
        ResourceJson.ValidateUniqueProperties(json.RootElement);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Resource configuration must be an object.");
        var package = new Package(root, source, productId, color);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            string? role = RoleFor(property.Name);
            if (role is null) continue;
            if (role.StartsWith("lottie", StringComparison.Ordinal)) AddAnimations(package, property.Name, property.Value, role);
            else AddResources(package, property.Value, role, 0);
        }
        return package.Entries.ToArray();
    }

    /// <summary>Maps official control/model config fields. The three Lottie lists keep separate roles.</summary>
    internal static string? RoleFor(string name) => name switch
    {
        "capsuleImageRes" => "capsule", "detailImageRes" => "detail", "leftImageRes" => "left", "rightImageRes" => "right",
        "boxImageRes" => "box", "model3D" => "model",
        "lottieResList" => "lottie", "leftLottieResList" => "lottie-left", "rightLottieResList" => "lottie-right",
        _ when name.Contains("lottie", StringComparison.OrdinalIgnoreCase) => "lottie-extra",
        _ => null
    };

    internal static void ValidateGlb(byte[] bytes)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("glTF"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != bytes.Length
            || !bytes.AsSpan(16, 4).SequenceEqual("JSON"u8)) throw new InvalidDataException("Invalid GLB structure.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        if (length > ResourceLimits.ConfigBytes || 20L + length > bytes.Length) throw new InvalidDataException("Invalid GLB JSON segment.");
        using var json = JsonDocument.Parse(bytes.AsMemory(20, checked((int)length)), new JsonDocumentOptions { MaxDepth = 96 });
        ResourceJson.ValidateUniqueProperties(json.RootElement);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid GLB JSON structure.");
        // The existing desktop GLB loader consumes self-contained models only.
        foreach (string field in new[] { "buffers", "images" })
            if (json.RootElement.TryGetProperty(field, out var rows))
            {
                if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid GLB resources.");
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid GLB resource.");
                    if (row.TryGetProperty("uri", out var uri) && (uri.ValueKind != JsonValueKind.String || !uri.GetString()!.StartsWith("data:", StringComparison.Ordinal)))
                        throw new InvalidDataException("External model URI is not supported.");
                }
            }
    }

    private static void ExtractArchive(byte[] bytes, string root)
    {
        using var memory = new MemoryStream(bytes); using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > ResourceLimits.Members || zip.Entries.Sum(e => e.Length) > ResourceLimits.ExpandedBytes) throw new InvalidDataException("Resource archive exceeds limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!names.Add(entry.FullName) || entry.Length > ResourceLimits.MemberBytes || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Invalid resource archive member.");
            string path = ResourcePaths.Resolve(root, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = File.Create(path);
            byte[] buffer = new byte[81920]; int read; long total = 0;
            while ((read = input.Read(buffer)) > 0) { total += read; if (total > ResourceLimits.MemberBytes || total > entry.Length) throw new InvalidDataException("Archive member exceeds declared length."); output.Write(buffer, 0, read); }
            if (total != entry.Length) throw new InvalidDataException("Truncated archive member.");
        }
    }

    private static void AddResources(Package package, JsonElement node, string role, int depth)
    {
        if (depth > 8) return;
        if (node.ValueKind == JsonValueKind.Array) { foreach (var item in node.EnumerateArray()) AddResources(package, item, role, depth + 1); return; }
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String)
        { AddFile(package, value.GetString()!, role); return; }
        // Variants such as day/night objects nest their own paths.
        foreach (var child in node.EnumerateObject())
            if (child.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) AddResources(package, child.Value, role, depth + 1);
    }

    private static void AddFile(Package package, string relative, string role)
    {
        string? declared = Resolve(package.Root, relative), path = declared;
        // Android image references may omit the extension.
        if (declared is not null && !File.Exists(declared))
            path = Path.GetExtension(declared).Length == 0 ? ImageExtensions.Select(extension => declared + extension).FirstOrDefault(File.Exists) : null;
        if (path is null) return;
        string kind;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".vfxms":
                byte[] container = File.ReadAllBytes(path);
                if (container.Length < 40) return;
                int start = BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(8, 4)), length = BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(12, 4));
                if (start < 40 || length < 12 || (long)start + length > container.Length || !container.AsSpan(start, 4).SequenceEqual("glTF"u8)) return;
                byte[] glb = container.AsSpan(start, length).ToArray();
                try { ValidateGlb(glb); } catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
                path = UniquePath(package.Root, "model", ".glb"); File.WriteAllBytes(path, glb); kind = "model";
                break;
            case ".glb":
                try { ValidateGlb(File.ReadAllBytes(path)); } catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
                kind = "model";
                break;
            case ".png" or ".webp" or ".jpg" or ".jpeg": kind = "bitmap"; break;
            default: return;
        }
        package.Add(new() { Kind = kind, Role = role }, path);
    }

    private static void AddAnimations(Package package, string listName, JsonElement node, string role)
    {
        var items = node.ValueKind == JsonValueKind.Array ? node.EnumerateArray().ToArray()
            : node.ValueKind is JsonValueKind.Object or JsonValueKind.String ? [node] : [];
        for (int index = 0; index < items.Length && index < 64; index++)
        {
            var item = items[index];
            string? animation = null;
            var folders = new List<string>();
            foreach (var (relative, declared) in Paths(item, 0))
            {
                // A path naming a folder may also be the item's images folder. Declared path fields
                // follow the archive's traversal rule; other path-like values are only candidates.
                string? full;
                try { full = Resolve(package.Root, relative); }
                catch (InvalidDataException) when (!declared) { continue; }
                if (full is null) continue;
                if (File.Exists(full)) animation ??= IsAnimation(full) ? full : null;
                else if (Directory.Exists(full)) { folders.Add(full); animation ??= FindAnimation(full); }
            }
            if (animation is null) continue;
            try { package.Add(IndexAnimation(package, animation, folders, out string? original) with
                { Role = role, Kind = "lottie", Order = index, Title = listName + "[" + index + "]", Metadata = Scalars(item) }, animation, original); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or ArgumentException) { }
        }
    }

    /// <summary>
    /// lottie-android resolves images through an images folder rather than each asset's "u".
    /// When an image only resolves through such a folder, write a derived copy whose references point
    /// at the files actually present; the original JSON keeps its bytes and provenance.
    /// </summary>
    private static ResourceArtwork IndexAnimation(Package package, string animation, IReadOnlyList<string> folders, out string? original)
    {
        original = null;
        byte[] bytes = File.ReadAllBytes(animation);
        if (bytes.Length is 0 or > AnimationBytes) throw new InvalidDataException("Animation file exceeds its limit.");
        var node = JsonNode.Parse(bytes, documentOptions: AnimationJson) as JsonObject ?? throw new InvalidDataException("Invalid animation.");
        string directory = Path.GetDirectoryName(animation)!;
        bool rewritten = false;
        if (node["assets"] is JsonArray assets)
            foreach (var asset in assets.OfType<JsonObject>())
            {
                if (asset["p"] is not JsonValue pathValue || !pathValue.TryGetValue(out string? name) || name.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                string prefix = asset["u"] is JsonValue prefixValue && prefixValue.TryGetValue(out string? text) ? text : "";
                string found = FindImage(package.Root, directory, prefix, name, folders) ?? throw new InvalidDataException("Missing animation image.");
                if (Inside(package.Root, Combine(directory, prefix, name)) is { } declared && declared.Equals(found, StringComparison.OrdinalIgnoreCase)) continue;
                string folder = Path.GetRelativePath(directory, Path.GetDirectoryName(found)!).Replace('\\', '/');
                asset["u"] = folder == "." ? "" : folder + "/"; asset["p"] = Path.GetFileName(found); asset["e"] = 0;
                rewritten = true;
            }
        if (!rewritten) return new();
        string derived = UniquePath(directory, Path.GetFileNameWithoutExtension(animation) + ".heymelody", ".json");
        File.WriteAllBytes(derived, JsonSerializer.SerializeToUtf8Bytes(node));
        original = animation;
        return new() { Path = derived };
    }

    private static string? FindImage(string root, string directory, string prefix, string name, IReadOnlyList<string> folders)
    {
        if (prefix.Contains(':', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal)) return null;
        var candidates = new List<string> { Combine(directory, prefix, name), Combine(directory, "images", name) };
        foreach (string folder in folders) { candidates.Add(Combine(folder, "", name)); candidates.Add(Combine(folder, prefix, name)); candidates.Add(Combine(folder, "images", name)); }
        candidates.Add(Combine(directory, "", name));
        return candidates.Select(candidate => Inside(root, candidate)).FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
    }

    private static bool IsAnimation(string path)
    {
        try
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length is 0 or > AnimationBytes) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), AnimationJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array
                && Positive(root, "w") && Positive(root, "h") && Positive(root, "fr")
                && root.TryGetProperty("ip", out var ip) && ip.TryGetDouble(out double start)
                && root.TryGetProperty("op", out var op) && op.TryGetDouble(out double end) && end > start;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException) { return false; }
        static bool Positive(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out double number) && number > 0;
    }

    private static string? FindAnimation(string directory)
    {
        string preferred = Path.Combine(directory, "data.json");
        if (IsAnimation(preferred)) return preferred;
        return Directory.EnumerateFiles(directory, "*.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
            .Order(StringComparer.Ordinal).FirstOrDefault(IsAnimation);
    }

    /// <summary>Path fields first by document order; other values that look like package paths follow.</summary>
    private static IEnumerable<(string Value, bool Declared)> Paths(JsonElement node, int depth)
    {
        if (depth > 4) yield break;
        if (node.ValueKind == JsonValueKind.String) { yield return (node.GetString()!, true); yield break; }
        if (node.ValueKind == JsonValueKind.Array) { foreach (var item in node.EnumerateArray()) foreach (var path in Paths(item, depth + 1)) yield return path; yield break; }
        if (node.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in node.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                string value = property.Value.GetString()!;
                if (IsPathName(property.Name)) yield return (value, true);
                else if (LooksLikePath(value)) yield return (value, false);
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                foreach (var path in Paths(property.Value, depth + 1)) yield return path;
        }
        static bool IsPathName(string name) => name.EndsWith("path", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("folder", StringComparison.OrdinalIgnoreCase) || name.EndsWith("dir", StringComparison.OrdinalIgnoreCase);
        static bool LooksLikePath(string value) => value.Length is > 0 and <= 512 && !value.Contains("://", StringComparison.Ordinal)
            && (value.Contains('/') || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string>? Scalars(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            if (values.Count == 32) break;
            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(), JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null
            };
            if (value is not null && property.Name.Length <= 64) values[property.Name] = value.Length <= 256 ? value : value[..256];
        }
        return values.Count == 0 ? null : values;
    }

    /// <summary>Normalizes harmless spelling ("./a", "a/", "a//b"); traversal still rejects the archive.</summary>
    private static string? Resolve(string root, string relative)
    {
        string value = relative.Trim();
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        while (value.Contains("//", StringComparison.Ordinal)) value = value.Replace("//", "/", StringComparison.Ordinal);
        value = value.TrimEnd('/');
        return value.Length == 0 ? null : ResourcePaths.Resolve(root, value);
    }

    private static string Combine(string directory, string prefix, string name) => Path.Combine(directory, prefix.Replace('\\', '/'), name.Replace('\\', '/'));
    private static string? Inside(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
    private static string UniquePath(string directory, string stem, string extension)
    {
        string path = Path.Combine(directory, stem + extension);
        for (int index = 1; File.Exists(path); index++) path = Path.Combine(directory, stem + "-" + index + extension);
        return path;
    }

    private sealed class Package(string root, ResourceSource source, string productId, int color)
    {
        public string Root { get; } = root;
        public List<ResourceArtwork> Entries { get; } = [];
        public void Add(ResourceArtwork entry, string path, string? original = null)
        {
            string file = entry.Path.Length != 0 ? entry.Path : path;
            byte[] raw = File.ReadAllBytes(file);
            Entries.Add(entry with
            {
                Source = source, ProductIds = [productId], Color = color, Path = Relative(file), Length = raw.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(raw)),
                OriginalPath = original is null ? null : Relative(original),
                OriginalSha256 = original is null ? null : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(original)))
            });
        }
        private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    }
}
