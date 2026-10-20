using System.Security.Cryptography;
using Avalonia.Platform;
using HeyMelody.Rendering.Tests;
using HeyMelodyWindows.App;

/// <summary>Verify the original cloud asset through the real, native-free application loader.</summary>
internal static class DynaudioModelTests
{
    internal const string ExpectedSha256 = GlbSource.DynaudioSha256;
    internal const int ExpectedTriangles = 94_927;
    internal static readonly Uri AssetUri = new("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb");

    public static async Task VerifyAsync()
    {
        using var asset = AssetLoader.Open(AssetUri);
        using var output = new MemoryStream();
        asset.CopyTo(output);
        var bytes = output.ToArray();
        var source = GlbSource.Inspect(bytes);
        Check(bytes.Length == 9_863_456 && source.Sha256 == ExpectedSha256
            && EncoFree4DynaudioModelView.ModelSha256 == ExpectedSha256
            && EncoFree4ModelView.ModelSha256 != ExpectedSha256,
            "The embedded Dynaudio asset remains the distinct audited original GLB.");
        Check(source.Meshes == 20 && source.Primitives == 20 && source.Triangles == ExpectedTriangles
            && source.Textures == 4 && source.Materials == 1,
            "The original Dynaudio source retains all meshes, triangles and four texture maps.");
        Check(source.MaterialName == "1001" && source.AlphaMode == "BLEND",
            "The source keeps authored BLEND; audited corrections belong to the renderer.");
        var first = await EncoFree4DynaudioModelView.LoadModelAsync();
        var second = await EncoFree4DynaudioModelView.LoadModelAsync();
        Check(first.Sha256 == ExpectedSha256 && second.Sha256 == ExpectedSha256
            && first.Bytes.Span.SequenceEqual(bytes) && second.Bytes.Span.SequenceEqual(bytes),
            "Repeated application loads retain every original GLB byte without native rendering.");
        Check(first.MaterialProfile == "opaque-1001" && second.MaterialProfile == "opaque-1001",
            "Only the audited model selects the renderer's opaque material profile.");
        var withoutExpectedHash = await DeviceModelView.LoadModelAsync(new DeviceModelVisual(
            new ArtworkFile(AssetUri, "", ""), DeviceVisuals.GenericProduct));
        Check(withoutExpectedHash.Sha256 == ExpectedSha256 && withoutExpectedHash.Bytes.Span.SequenceEqual(bytes),
            "An artwork entry without a declared hash computes the real digest before choosing its material profile.");
        bool rejected = false;
        try
        {
            await DeviceModelView.LoadModelAsync(new DeviceModelVisual(
                new ArtworkFile(AssetUri, "", new string('0', 64)), DeviceVisuals.GenericProduct));
        }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "An explicitly incorrect artwork digest is never treated as an undeclared hash.");
        Check(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == ExpectedSha256,
            "Loading and selecting material corrections never rewrites the original model.");
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
