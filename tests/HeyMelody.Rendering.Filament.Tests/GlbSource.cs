using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeyMelody.Rendering.Tests;

/// <summary>Independent source-fixture measurements, never renderer submission counts.</summary>
internal sealed record GlbSourceInfo(string Sha256, int Meshes, int Nodes, int Primitives,
    int Materials, ulong Triangles, int Textures, string? MaterialName, string? AlphaMode);

internal static class GlbSource
{
    internal const string Free4Sha256 = "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc";
    internal const string DynaudioSha256 = "7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6";
    internal const string X3BlackSha256 = "6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363";
    internal const string X3WhiteSha256 = "b2d5d94fabee06718eaf7378fe6bdbf3db2824db23656a080251df92a04a5060";

    internal static GlbSourceInfo Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546c67
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != 0x4e4f534a)
            throw new InvalidDataException("Fixture is not a complete glTF 2.0 binary.");
        int jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        using var json = JsonDocument.Parse(bytes.Slice(20, jsonLength).ToArray());
        var root = json.RootElement;
        var accessors = root.GetProperty("accessors");
        ulong triangles = 0;
        int primitives = 0;
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
        {
            if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                throw new InvalidDataException("Audited fixture contains a non-triangle primitive.");
            var count = accessors[primitive.GetProperty("indices").GetInt32()].GetProperty("count").GetUInt64();
            if (count % 3 != 0) throw new InvalidDataException("Fixture has incomplete index triples.");
            triangles += count / 3;
            primitives++;
        }
        foreach (string property in new[] { "buffers", "images" })
        foreach (var row in root.GetProperty(property).EnumerateArray())
            if (row.TryGetProperty("uri", out var uri) && !uri.GetString()!.StartsWith("data:", StringComparison.Ordinal))
                throw new InvalidDataException("Audited fixture has an external resource URI.");
        var materials = root.GetProperty("materials");
        return new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            root.GetProperty("meshes").GetArrayLength(), root.GetProperty("nodes").GetArrayLength(),
            primitives, materials.GetArrayLength(), triangles, root.GetProperty("textures").GetArrayLength(),
            materials[0].GetProperty("name").GetString(),
            materials[0].TryGetProperty("alphaMode", out var alpha) ? alpha.GetString() : "OPAQUE");
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "HeyMelody.App", "HeyMelody.App.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root unavailable for source fixture validation.");
    }

    internal static string BundledModel(string name) => Path.Combine(RepositoryRoot(), "src", "HeyMelody.App", "Assets", "Devices", name, "model.glb");
}
