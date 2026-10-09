using System.Numerics;
using Ava3D;
using HeyMelodyWindows.App;

internal static class SoftwareMaterialTests
{
    internal static async Task AtlasAsync()
    {
        var model = await EncoFree4DynaudioModelView.LoadModelAsync();
        model.Scene.Children.Clear();
        // The same atlas describes a matte, dark part and a glossy, emitting metal part.
        // Ignoring its maps incorrectly makes both parts fully metallic and white-emitting.
        var mr = Texture.FromPixels([255, 128, 0, 255, 255, 64, 255, 255], 2, 1);
        var emission = Texture.FromPixels([0, 0, 0, 255, 128, 64, 32, 255], 2, 1);
        var baseColour = Texture.FromPixels([70, 80, 90, 255], 1, 1);
        var original = new Material
        {
            Metallic = 1, Roughness = 1, EmissiveColor = Vector3.One,
            MetallicRoughnessTexture = mr, EmissiveTexture = emission, BaseColorTexture = baseColour
        };
        var left = new MeshNode(Triangle(.25f), original);
        var right = new MeshNode(Triangle(.75f), original);
        var untextured = new Material { Metallic = .4f, Roughness = .6f, EmissiveColor = new(.1f, .2f, .3f) };
        var third = new MeshNode(Triangle(.5f), untextured);
        model.Scene.Children.Add(left); model.Scene.Children.Add(right); model.Scene.Children.Add(third);
        var profiles = new SoftwareModelMaterials(model);
        profiles.Apply(true);
        Check(left.Material.Metallic == 0 && Near(left.Material.Roughness, 128 / 255f)
            && left.Material.EmissiveColor == Vector3.Zero,
            "An atlas region with black emission is lit by the scene rather than emitting white.");
        Check(right.Material.Metallic == 1 && Near(right.Material.Roughness, 64 / 255f)
            && Near(right.Material.EmissiveColor.X, MathF.Pow(128 / 255f, 2.2f))
            && right.Material.EmissiveColor.X > right.Material.EmissiveColor.Y
            && right.Material.EmissiveColor.Y > right.Material.EmissiveColor.Z,
            "Another region of the same atlas retains its metal, roughness and linear emission values.");
        Check(ReferenceEquals(left.Material.BaseColorTexture, baseColour)
            && ReferenceEquals(right.Material.MetallicRoughnessTexture, mr)
            && ReferenceEquals(right.Material.EmissiveTexture, emission)
            && original.Metallic == 1 && original.Roughness == 1 && original.EmissiveColor == Vector3.One,
            "Software clones retain the source textures and leave the shared original material intact.");
        Check(third.Material.Metallic == untextured.Metallic && third.Material.Roughness == untextured.Roughness
            && third.Material.EmissiveColor == untextured.EmissiveColor,
            "An intentionally untextured material retains its authored constants.");
        var firstSoftware = left.Material;
        profiles.Apply(false);
        Check(ReferenceEquals(left.Material, original) && ReferenceEquals(right.Material, original)
            && ReferenceEquals(third.Material, untextured), "GPU rendering restores every original material reference.");
        profiles.Apply(true);
        Check(ReferenceEquals(left.Material, firstSoftware) && left.Material.EmissiveColor == Vector3.Zero,
            "Repeated backend switches reuse the prepared material without accumulating conversions.");

        CheckMappedUv1(model);
        CheckWraps(model);
        CheckWorldAreaWeights(model);
        CheckUnavailableMaps(model);
    }

    private static void CheckMappedUv1(GltfModel model)
    {
        model.Scene.Children.Clear();
        var pixels = new byte[4 * 4 * 4];
        for (var texel = 0; texel < 16; texel++)
        {
            pixels[4 * texel] = 255;
            pixels[4 * texel + 1] = (byte)((15 - texel) * 17);
            pixels[4 * texel + 2] = (byte)(texel * 17);
            pixels[4 * texel + 3] = 255;
        }
        var original = new Material
        {
            Metallic = .8f, Roughness = .6f,
            MetallicRoughnessTexture = Texture.FromPixels(pixels, 4, 4),
            MetallicRoughnessMapping = new TextureMapping
            {
                TextureCoordinate = 1, Scale = new(2, 1), Rotation = MathF.PI / 2, Offset = new(1, .25f)
            },
            UvScale = new(2, 1), UvPivot = new(.5f, .5f), UvRotation = MathF.PI / 2,
            UvOffset = new(.25f, -.25f)
        };
        var mesh = Triangle(.125f, uv1: new(.25f, .375f));
        var node = new MeshNode(mesh, original)
        {
            UvScale = new(.5f, 1.5f), UvRotation = -MathF.PI / 2, UvOffset = new(-.875f, -.125f)
        };
        model.Scene.Children.Add(node);
        // Independently worked coordinate path, with no test call into the helper's transform:
        // UV1 (.25,.375) -> per-map (.625,.75) -> material (.5,1)
        // -> node (.625,.625), the centre of texel (2,2), index 10.
        // UV0 is deliberately different and would select another texel.
        var profiles = new SoftwareModelMaterials(model);
        profiles.Apply(true);
        Check(Near(node.Material.Metallic, .8f * 170 / 255f)
            && Near(node.Material.Roughness, .6f * 85 / 255f),
            "UV1 and noncommuting per-map/material/node transforms select the independently expected atlas texel.");
        profiles.Apply(false);
        Check(ReferenceEquals(node.Material, original), "Mapped UV materials also restore their exact source object.");
    }

    private static void CheckWraps(GltfModel model)
    {
        // A two-texel atlas has metallic 0 on its first texel and 1 on its last.
        (TextureWrap Mode, float Coordinate, bool Last, bool Vertical)[] cases =
        [
            (TextureWrap.Repeat, -.25f, true, false),
            (TextureWrap.Repeat, -1f, false, false),
            (TextureWrap.Repeat, 1f, false, false),
            (TextureWrap.MirroredRepeat, -.25f, false, false),
            (TextureWrap.MirroredRepeat, -1.25f, true, false),
            (TextureWrap.MirroredRepeat, -1f, true, false),
            (TextureWrap.ClampToEdge, -.25f, false, false),
            (TextureWrap.ClampToEdge, 1.25f, true, false),
            (TextureWrap.Repeat, -.25f, true, true),
            (TextureWrap.MirroredRepeat, -.25f, false, true),
            (TextureWrap.ClampToEdge, -.25f, false, true)
        ];
        foreach (var sample in cases)
        {
            model.Scene.Children.Clear();
            var texture = new Texture
            {
                Pixels = [255, 128, 0, 255, 255, 64, 255, 255], Format = TextureFormat.Rgba8,
                Width = sample.Vertical ? 1 : 2, Height = sample.Vertical ? 2 : 1,
                WrapU = sample.Vertical ? TextureWrap.ClampToEdge : sample.Mode,
                WrapV = sample.Vertical ? sample.Mode : TextureWrap.ClampToEdge
            };
            var mesh = sample.Vertical ? Triangle(.5f, sample.Coordinate) : Triangle(sample.Coordinate);
            var node = new MeshNode(mesh, new Material { Metallic = .7f, Roughness = .6f, MetallicRoughnessTexture = texture });
            model.Scene.Children.Add(node);
            new SoftwareModelMaterials(model).Apply(true);
            Check(Near(node.Material.Metallic, sample.Last ? .7f : 0)
                && Near(node.Material.Roughness, .6f * (sample.Last ? 64 : 128) / 255f),
                $"{sample.Mode} {(sample.Vertical ? "V" : "U")}={sample.Coordinate} selects the expected texel at negative and tile-edge coordinates.");
        }
    }

    private static void CheckWorldAreaWeights(GltfModel model)
    {
        model.Scene.Children.Clear();
        var mesh = new Mesh
        {
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.Zero, Vector3.UnitY, Vector3.UnitZ],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
            TexCoords = [new(.25f, .5f), new(.25f, .5f), new(.25f, .5f),
                new(.75f, .5f), new(.75f, .5f), new(.75f, .5f)],
            Indices = [0, 1, 2, 3, 4, 5]
        };
        var node = new MeshNode(mesh, new Material
        {
            Metallic = 1, Roughness = 1,
            MetallicRoughnessTexture = Texture.FromPixels([255, 128, 0, 255, 255, 64, 255, 255], 2, 1)
        }) { Scale = new(3, 1, 1) };
        model.Scene.Children.Add(node);
        new SoftwareModelMaterials(model).Apply(true);
        // Equal local XY/YZ areas become 3:1 under X scale 3. Only YZ samples metal 1.
        Check(Near(node.Material.Metallic, .25f) && Near(node.Material.Roughness, 112 / 255f),
            "Nonuniform node scale weights the atlas samples by world surface area rather than triangle count.");
    }

    private static void CheckUnavailableMaps(GltfModel model)
    {
        Texture[] textures =
        [
            Texture.FromEncoded([137, 80, 78, 71, 13, 10, 26, 10]), // Truncated PNG signature.
            new Texture { Pixels = [0, 0, 0, 255], Width = SoftwareModelMaterials.MaximumTextureSide + 1, Height = 1 },
            new Texture { Pixels = [0, 0, 0, 255], Width = 2048, Height = 2049 }, // Pixel budget, within side limit.
            new Texture { Pixels = [0, 0, 0, 255], Width = 2, Height = 1 } // Incomplete RGBA row.
        ];
        foreach (var texture in textures)
        {
            model.Scene.Children.Clear();
            var original = new Material
            {
                Metallic = .4f, Roughness = .6f, EmissiveColor = new(.1f, .2f, .3f),
                MetallicRoughnessTexture = texture, EmissiveTexture = texture
            };
            var node = new MeshNode(Triangle(.5f), original);
            model.Scene.Children.Add(node);
            var profiles = new SoftwareModelMaterials(model);
            profiles.Apply(true);
            Check(node.Material.Metallic == original.Metallic && node.Material.Roughness == original.Roughness
                && node.Material.EmissiveColor == original.EmissiveColor
                && profiles.Diagnostics.Single().MetallicRoughnessSamples == 0
                && profiles.Diagnostics.Single().EmissiveSamples == 0,
                "Malformed or oversized maps retain the authored factors and do not masquerade as successful samples.");
            profiles.Apply(false);
            Check(ReferenceEquals(node.Material, original), "Unavailable maps still restore their exact original material.");
        }
    }

    private static Mesh Triangle(float u, float v = .5f, Vector2? uv1 = null) => new()
    {
        Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        TexCoords = [new(u, v), new(u, v), new(u, v)],
        TexCoords1 = uv1 is { } coordinates ? [coordinates, coordinates, coordinates] : null,
        Indices = [0, 1, 2]
    };

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .0001f;
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
