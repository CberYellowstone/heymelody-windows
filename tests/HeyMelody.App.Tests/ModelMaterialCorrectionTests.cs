using System.Numerics;
using Ava3D;
using HeyMelodyWindows.App;

internal static class ModelMaterialCorrectionTests
{
    internal static Task RunAsync()
    {
        foreach (var hash in new[]
        {
            EncoFree4ModelView.ModelSha256, EncoFree4DynaudioModelView.ModelSha256,
            ModelMaterialCorrections.EncoX3BlackSha256, ModelMaterialCorrections.EncoX3WhiteSha256
        })
        {
            var texture = Texture.FromPixels([70, 80, 90, 255], 1, 1);
            var target = new Material
            {
                Blend = BlendMode.Alpha, DepthWrite = false, DepthTest = false,
                BaseColor = new Vector4(.3f, .4f, .5f, 1), BaseColorTexture = texture,
                Metallic = .2f, Roughness = .7f, EmissiveColor = new(.1f, .2f, .3f)
            };
            var before = target.Clone();
            var other = new Material { Blend = BlendMode.Alpha, DepthWrite = false };
            var materials = new Dictionary<string, Material> { ["1001"] = target, ["glass"] = other };
            ModelMaterialCorrections.Apply(hash, name => materials.GetValueOrDefault(name));
            Check(target.Blend == BlendMode.Opaque && target.DepthWrite,
                "Each audited GLB receives opaque rendering and depth writes.");
            Check(ReferenceEquals(materials["1001"], target) && target.DepthTest == before.DepthTest
                && target.Cull == before.Cull && target.BaseColor == before.BaseColor
                && ReferenceEquals(target.BaseColorTexture, texture) && target.Metallic == before.Metallic
                && target.Roughness == before.Roughness && target.EmissiveColor == before.EmissiveColor,
                "Correction preserves the original material, culling, depth test, textures and authored shading.");
            Check(other.Blend == BlendMode.Alpha && !other.DepthWrite,
                "Other materials retain their authored transparency even in an audited model.");
            ModelMaterialCorrections.Apply(hash, name => materials.GetValueOrDefault(name));
            Check(target.Blend == BlendMode.Opaque && target.DepthWrite,
                "Applying a correction again preserves opaque rendering.");
            ModelMaterialCorrections.Apply(hash, name => name == "glass" ? other : null);
            Check(other.Blend == BlendMode.Alpha && !other.DepthWrite,
                "A missing target material does not broaden the correction to another material.");
        }

        foreach (var alpha in new[] { 1f, .4f })
        {
            var unknown = new Material { Blend = BlendMode.Alpha, DepthWrite = false, BaseColor = new(1, 1, 1, alpha) };
            ModelMaterialCorrections.Apply(new string('a', 64), name => name == "1001" ? unknown : null);
            Check(unknown.Blend == BlendMode.Alpha && !unknown.DepthWrite && unknown.BaseColor.W == alpha,
                "An unknown GLB keeps its transparency even when its material name or base-color alpha matches.");
        }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
