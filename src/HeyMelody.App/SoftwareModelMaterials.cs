using System.Numerics;
using Ava3D;
using SkiaSharp;

namespace HeyMelodyWindows.App;

/// <summary>
/// Supplies the maps' representative values to Ava3D's vertex-lit software backend,
/// which samples base colour but does not sample metallic/roughness or emissive maps.
/// The source materials and encoded textures remain available for GPU rendering.
/// </summary>
internal sealed class SoftwareModelMaterials
{
    internal const int MaximumTexturePixels = 2048 * 2048;
    internal const int MaximumTextureSide = 8192;
    internal const int MaximumEncodedTextureBytes = 32 * 1024 * 1024;
    internal const int MaximumDecodedBytes = 32 * 1024 * 1024;
    internal const int MaximumSampledTriangles = 2048;
    private const int MaximumSampledMeshes = 256;

    private readonly List<Binding> _bindings = [];

    internal IReadOnlyList<SoftwareMaterialDiagnostic> Diagnostics { get; }
    internal bool IsSoftware { get; private set; }

    // Run this once while the fresh model is still off the UI/render threads.
    internal SoftwareModelMaterials(GltfModel model)
    {
        var diagnostics = new List<SoftwareMaterialDiagnostic>();
        var textures = new TextureSamples();
        try
        {
            foreach (var entry in model.Scene.EnumerateMeshes())
            {
                var node = entry.Node;
                if (node.Mesh is not { } mesh) continue;
                var original = node.Material;
                var software = original.Clone();
                var maySample = _bindings.Count < MaximumSampledMeshes;
                var mr = SampleMap(mesh, node, original, original.MetallicRoughnessTexture,
                    original.MetallicRoughnessMapping, false, maySample, textures);
                var emission = SampleMap(mesh, node, original, original.EmissiveTexture,
                    original.EmissiveMapping, true, maySample, textures);
                if (mr.Count > 0)
                {
                    // glTF uses linear G for roughness and B for metallic; factors multiply maps.
                    software.Metallic = original.Metallic * mr.Mean.Z;
                    software.Roughness = original.Roughness * mr.Mean.Y;
                }
                if (emission.Count > 0)
                    software.EmissiveColor = original.EmissiveColor * emission.Mean;

                _bindings.Add(new Binding(node, original, software));
                diagnostics.Add(new SoftwareMaterialDiagnostic(
                    node.Name ?? mesh.Name ?? string.Empty,
                    original.Metallic, original.Roughness, original.EmissiveColor,
                    software.Metallic, software.Roughness, software.EmissiveColor,
                    mr.Mean, emission.Mean, mr.Count, emission.Count, mr.Status, emission.Status));
            }
        }
        finally { textures.Dispose(); }
        Diagnostics = diagnostics.AsReadOnly();
    }

    /// <summary>Swaps references rather than changing a material shared by other nodes.</summary>
    internal void Apply(bool software)
    {
        foreach (var binding in _bindings)
        {
            var material = software ? binding.Software : binding.Original;
            if (!ReferenceEquals(binding.Node.Material, material)) binding.Node.Material = material;
        }
        IsSoftware = software;
    }

    private static MapSample SampleMap(Mesh mesh, MeshNode node, Material material, Texture? texture,
        TextureMapping mapping, bool colour, bool maySample, TextureSamples textures)
    {
        if (texture is null) return new MapSample(default, 0, "no-map");
        if (!maySample) return new MapSample(default, 0, "mesh-budget");
        if (mapping is null) return new MapSample(default, 0, "missing-uv-mapping");
        // Projected/procedural coordinates cannot be represented by a static mesh-UV average.
        if (material.UvSource != UvSource.Mesh) return new MapSample(default, 0, "projected-uv");
        var coordinates = mapping.TextureCoordinate switch
        {
            0 => mesh.TexCoords,
            1 => mesh.TexCoords1,
            _ => null
        };
        if (coordinates is null || coordinates.Length != mesh.Positions.Length)
            return new MapSample(default, 0, "missing-uv");

        // This is the documented SDK order: per-map KHR transform, material, then node.
        // Both shared rotations use the material pivot; shared scaling is about the origin.
        var transform = Matrix3x2.CreateScale(mapping.Scale)
            * Matrix3x2.CreateRotation(mapping.Rotation)
            * Matrix3x2.CreateTranslation(mapping.Offset)
            * SharedTransform(material.UvScale, material.UvRotation, material.UvOffset, material.UvPivot)
            * SharedTransform(node.UvScale, node.UvRotation, node.UvOffset, material.UvPivot);
        if (!Finite(transform)) return new MapSample(default, 0, "invalid-uv-transform");
        var pixels = textures.Get(texture);
        if (pixels is null) return new MapSample(default, 0, "unsupported-or-bounded-map");
        var indices = mesh.Indices;
        var triangleCount = indices.Length / 3;
        if (triangleCount == 0) return new MapSample(default, 0, "no-triangles");
        var selectedCount = Math.Min(triangleCount, MaximumSampledTriangles);
        var world = node.WorldTransform;
        double weight = 0, r = 0, g = 0, b = 0;
        var sampleCount = 0;
        var decodeSrgb = texture.ColorSpace == TextureColorSpace.Srgb
            || (colour && texture.ColorSpace == TextureColorSpace.Auto);
        for (var selected = 0; selected < selectedCount; selected++)
        {
            // Cover the entire mesh evenly instead of biasing the beginning of its index array.
            var triangle = (int)((long)selected * triangleCount / selectedCount);
            var index = triangle * 3;
            var a = indices[index];
            var c = indices[index + 1];
            var d = indices[index + 2];
            if (a >= mesh.Positions.Length || c >= mesh.Positions.Length || d >= mesh.Positions.Length)
                continue;
            // Nonuniform scale changes the relative areas of differently oriented faces.
            var edge1 = Vector3.TransformNormal(mesh.Positions[c] - mesh.Positions[a], world);
            var edge2 = Vector3.TransformNormal(mesh.Positions[d] - mesh.Positions[a], world);
            var cross = Vector3.Cross(edge1, edge2);
            var area = cross.Length(); // Twice the area; the common factor cancels in the mean.
            if (!float.IsFinite(area) || area <= 0) continue;
            var uvA = coordinates[a];
            var uvC = coordinates[c];
            var uvD = coordinates[d];
            if (!Finite(uvA) || !Finite(uvC) || !Finite(uvD)) continue;

            // A centroid and three interior samples cover variation within larger triangles.
            for (var point = 0; point < 4; point++)
            {
                var uv = point switch
                {
                    0 => (uvA + uvC + uvD) / 3,
                    1 => (4 * uvA + uvC + uvD) / 6,
                    2 => (uvA + 4 * uvC + uvD) / 6,
                    _ => (uvA + uvC + 4 * uvD) / 6
                };
                uv = Vector2.Transform(uv, transform);
                if (!Finite(uv)) continue;
                var value = pixels.Sample(uv, texture.WrapU, texture.WrapV);
                if (decodeSrgb) value = new Vector3(Linear(value.X), Linear(value.Y), Linear(value.Z));
                r += value.X * area;
                g += value.Y * area;
                b += value.Z * area;
                weight += area;
                sampleCount++;
            }
        }
        return weight > 0
            ? new MapSample(new Vector3((float)(r / weight), (float)(g / weight), (float)(b / weight)), sampleCount, "sampled")
            : new MapSample(default, 0, "invalid-or-empty-geometry");
    }

    private static Matrix3x2 SharedTransform(Vector2 scale, float rotation, Vector2 offset, Vector2 pivot)
        => Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(-pivot)
            * Matrix3x2.CreateRotation(rotation) * Matrix3x2.CreateTranslation(pivot + offset);

    // Ava3D's pinned software/GPU lighting arithmetic uses this same gamma approximation.
    private static float Linear(float value) => MathF.Pow(value, 2.2f);
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Matrix3x2 value) => float.IsFinite(value.M11) && float.IsFinite(value.M12)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M31) && float.IsFinite(value.M32);

    private sealed record Binding(MeshNode Node, Material Original, Material Software);
    private readonly record struct MapSample(Vector3 Mean, int Count, string Status);

    private sealed class TextureSamples : IDisposable
    {
        private readonly Dictionary<Texture, DecodedTexture?> _cache = new(ReferenceEqualityComparer.Instance);
        private int _decodedBytes;

        internal DecodedTexture? Get(Texture texture)
        {
            if (_cache.TryGetValue(texture, out var cached)) return cached;
            var decoded = Decode(texture);
            _cache.Add(texture, decoded);
            return decoded;
        }

        private DecodedTexture? Decode(Texture texture)
        {
            if (texture.Pixels is { } raw)
            {
                if (texture.Format != TextureFormat.Rgba8 || !Bounded(texture.Width, texture.Height)) return null;
                var bytes = checked(texture.Width * texture.Height * 4);
                if (raw.Length < bytes || (texture.MipOffsets is { Length: > 0 } mips && mips[0] != 0)) return null;
                return new DecodedTexture(raw, texture.Width, texture.Height);
            }
            if (texture.Encoded is not { Length: > 0 } encoded || encoded.Length > MaximumEncodedTextureBytes) return null;
            SKBitmap? bitmap = null;
            try
            {
                using var data = SKData.CreateCopy(encoded);
                using var codec = SKCodec.Create(data);
                if (codec is null || !Bounded(codec.Info.Width, codec.Info.Height)) return null;
                var bytes = checked(codec.Info.Width * codec.Info.Height * 4);
                if (bytes > MaximumDecodedBytes - _decodedBytes) return null;
                bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                    return null;
                _decodedBytes += bytes;
                var decoded = new DecodedTexture(bitmap);
                bitmap = null; // Ownership passed to the cache, disposed after all mesh samples.
                return decoded;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            finally { bitmap?.Dispose(); }
        }

        private static bool Bounded(int width, int height) => width is > 0 and <= MaximumTextureSide
            && height is > 0 and <= MaximumTextureSide && (long)width * height <= MaximumTexturePixels;

        public void Dispose()
        {
            foreach (var decoded in _cache.Values) decoded?.Dispose();
        }
    }

    private sealed class DecodedTexture : IDisposable
    {
        private readonly byte[]? _raw;
        private readonly SKBitmap? _bitmap;
        private readonly int _width;
        private readonly int _height;

        internal DecodedTexture(byte[] raw, int width, int height) => (_raw, _width, _height) = (raw, width, height);
        internal DecodedTexture(SKBitmap bitmap) => (_bitmap, _width, _height) = (bitmap, bitmap.Width, bitmap.Height);

        internal Vector3 Sample(Vector2 uv, TextureWrap wrapU, TextureWrap wrapV)
        {
            var x = Texel(uv.X, wrapU, _width);
            var y = Texel(uv.Y, wrapV, _height);
            if (_raw is { } raw)
            {
                var offset = (y * _width + x) * 4;
                return new Vector3(raw[offset], raw[offset + 1], raw[offset + 2]) / 255;
            }
            var pixel = _bitmap!.GetPixel(x, y);
            return new Vector3(pixel.Red, pixel.Green, pixel.Blue) / 255;
        }

        private static int Texel(float coordinate, TextureWrap wrap, int size)
        {
            var value = (double)coordinate;
            if (wrap == TextureWrap.Repeat) value -= Math.Floor(value);
            else if (wrap == TextureWrap.MirroredRepeat)
            {
                value -= 2 * Math.Floor(value / 2);
                if (value > 1) value = 2 - value;
            }
            else value = Math.Clamp(value, 0, 1);
            return Math.Clamp((int)(value * size), 0, size - 1);
        }

        public void Dispose() => _bitmap?.Dispose();
    }
}

internal readonly record struct SoftwareMaterialDiagnostic(
    string MeshName,
    float OriginalMetallic, float OriginalRoughness, Vector3 OriginalEmissiveColor,
    float Metallic, float Roughness, Vector3 EmissiveColor,
    Vector3 MetallicRoughnessMean, Vector3 EmissiveMean,
    int MetallicRoughnessSamples, int EmissiveSamples,
    string MetallicRoughnessStatus, string EmissiveStatus);
