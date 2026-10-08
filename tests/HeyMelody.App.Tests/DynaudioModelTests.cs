using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Ava3D;
using Avalonia.Platform;
using HeyMelodyWindows.App;

/// <summary>Verify the exact independently audited Dynaudio asset through the app's real loader.</summary>
internal static class DynaudioModelTests
{
    internal const string ExpectedSha256 = "7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6";
    internal const int ExpectedTriangles = 94_927;
    internal static readonly Uri AssetUri = new("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb");

    public static async Task VerifyAsync()
    {
        byte[] bytes;
        using (var asset = AssetLoader.Open(AssetUri))
        using (var output = new MemoryStream())
        { asset.CopyTo(output); bytes = output.ToArray(); }
        Check(bytes.Length == 9_863_456
            && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == ExpectedSha256
            && EncoFree4DynaudioModelView.ModelSha256 == ExpectedSha256
            && EncoFree4ModelView.ModelSha256 != ExpectedSha256,
            "The embedded Dynaudio model is the audited cloud GLB, distinct from ordinary Free4.");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == 0x46546c67
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) == 2
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) == bytes.Length,
            "The original Dynaudio asset remains a complete glTF 2.0 binary.");
        int jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var source = json.RootElement;
        int triangles = 0;
        foreach (var mesh in source.GetProperty("meshes").EnumerateArray())
        foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
        {
            Check(!primitive.TryGetProperty("mode", out var mode) || mode.GetInt32() == 4,
                "Dynaudio primitives keep their original triangle topology.");
            int count = source.GetProperty("accessors")[primitive.GetProperty("indices").GetInt32()].GetProperty("count").GetInt32();
            Check(count % 3 == 0, "The model has complete index triples.");
            triangles += count / 3;
        }
        Check(source.GetProperty("meshes").GetArrayLength() == 20 && triangles == ExpectedTriangles,
            "The embedded source contains all 20 meshes and 94,927 triangles.");

        var raw = GltfLoader.LoadModel(bytes, "Dynaudio original BLEND");
        var first = await EncoFree4DynaudioModelView.LoadModelAsync();
        var second = await EncoFree4DynaudioModelView.LoadModelAsync();
        Check(raw.Materials["1001"].Blend == BlendMode.Alpha
            && first.Materials["1001"].Blend == BlendMode.Opaque
            && first.Materials["1001"].DepthWrite && first.Materials["1001"].DepthTest
            && first.Materials["1001"].Cull == raw.Materials["1001"].Cull,
            "The exact Dynaudio asset receives Opaque/depth-write correction while preserving culling.");
        Check(raw.Scene.EnumerateMeshes().Count() == 20 && first.Scene.EnumerateMeshes().Count() == 20
            && second.Scene.EnumerateMeshes().Count() == 20,
            "The Ava3D loader preserves every Dynaudio mesh on the first and later loads.");
        Check(first.Scene.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount
                ?? throw new InvalidOperationException("A Dynaudio mesh has no geometry.")) == ExpectedTriangles
            && second.Scene.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount
                ?? throw new InvalidOperationException("A Dynaudio mesh has no geometry.")) == ExpectedTriangles,
            "The first and later loaded scenes preserve all 94,927 triangles.");
        Check(!ReferenceEquals(first.Scene, second.Scene) && !ReferenceEquals(first.Root, second.Root)
            && !ReferenceEquals(first.Materials["1001"], second.Materials["1001"]),
            "Repeated loads have independent mutable scenes and materials.");
        first.Materials["1001"].Blend = BlendMode.Alpha;
        Check(second.Materials["1001"].Blend == BlendMode.Opaque && second.Materials["1001"].DepthWrite,
            "Changing one loaded scene cannot alter a later model instance.");
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
