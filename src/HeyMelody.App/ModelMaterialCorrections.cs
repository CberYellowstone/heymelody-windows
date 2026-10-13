using Ava3D;

namespace HeyMelodyWindows.App;

/// <summary>Corrects audited opaque OEM materials without rewriting the original artwork.</summary>
internal static class ModelMaterialCorrections
{
    internal const string EncoX3BlackSha256 = "6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363";
    internal const string EncoX3WhiteSha256 = "b2d5d94fabee06718eaf7378fe6bdbf3db2824db23656a080251df92a04a5060";

    internal static void Apply(string verifiedSha256, Func<string, Material?> findMaterial)
    {
        // These exact assets declare BLEND despite fully opaque base-color factors
        // and RGB maps without transparency. The alpha pass disables depth writes,
        // allowing internal geometry to overwrite the shell. Unknown assets keep
        // their authored transparency; names, colours and product IDs are not proof.
        if (verifiedSha256 is not (EncoFree4ModelView.ModelSha256
            or EncoFree4DynaudioModelView.ModelSha256 or EncoX3BlackSha256 or EncoX3WhiteSha256)) return;
        var material = findMaterial("1001");
        if (material is null) return;
        material.Blend = BlendMode.Opaque;
        material.DepthWrite = true;
    }
}
