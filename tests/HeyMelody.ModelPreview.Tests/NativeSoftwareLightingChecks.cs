using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Ava3D;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using HeyMelodyWindows.App;

/// <summary>Checks actual CPU pixels at one fixed pose, rather than counting texture colours.</summary>
internal static class NativeSoftwareLightingChecks
{
    private sealed record Shot(string Name, byte[] Pixels, int Width, int Height, string Sha256);
    private sealed record Difference(int InteriorPixels, int ChangedPixels, double ChangedFraction,
        double MeanAbsoluteLumaDifference, double MaximumAbsoluteLumaDifference);
    private sealed record Contrast(int Pixels, int ForegroundPixels, double ForegroundFraction,
        double P10, double P90, double Span, double Mean);

    internal static async Task<object> RunAsync(EncoFree4DynaudioModelView view, string output,
        Action<bool, string> check)
    {
        var evidence = new Dictionary<string, object?>
        {
            ["scope"] = "Real native CPU capture. Normal and metallic/roughness map hashes prove loading; light and base-colour comparisons prove rendered effects. This does not claim per-pixel normal-map support from the software SDK.",
            ["modelSha256"] = EncoFree4DynaudioModelView.ModelSha256
        };
        var shots = new List<object>();
        evidence["captures"] = shots;
        var viewport = view.Viewport;
        var scene = viewport.Scene ?? throw new InvalidOperationException("CPU lighting scene is missing.");
        var materials = scene.EnumerateMeshes().Select(mesh => mesh.Node.Material).Distinct().ToArray();
        var savedTextures = materials.Select(material => material.BaseColorTexture).ToArray();
        var direction = scene.Light.Direction;
        var intensity = scene.Light.Intensity;
        var ambient = scene.Light.Ambient;
        var environment = scene.Environment;
        var theme = Application.Current!.RequestedThemeVariant;
        var motionActive = view.Motion.IsActive;
        view.Motion.SetActive(false);
        try
        {
            var sourceBytes = ReadSourceBytes();
            check(Hash(sourceBytes).Equals(EncoFree4DynaudioModelView.ModelSha256, StringComparison.OrdinalIgnoreCase),
                "Software lighting comparison uses the unchanged original Dynaudio GLB");
            var original = await EncoFree4DynaudioModelView.LoadModelAsync();
            evidence["sourceMapsAndMaterialRestoration"] = VerifySourceMapsAndRestoration(sourceBytes, original, check);
            var sourceBaseHash = Hash(original.Materials["1001"].BaseColorTexture!.Encoded!);
            check(materials.Length > 0 && materials.All(material => material.BaseColorTexture?.Encoded is { } bytes
                && Hash(bytes) == sourceBaseHash),
                "Every displayed software material retains the original encoded base-colour texture");

            // CaptureRequest snapshots this separate camera on the UI thread. The view's
            // rotation timer can continue ticking without changing any comparison pose.
            var camera = new Camera();
            camera.Fit(original.Root.WorldBounds, margin: 1.08f);
            camera.Target = viewport.Camera.Target;
            camera.Distance = viewport.Camera.Distance;
            camera.FieldOfView = viewport.Camera.FieldOfView;
            camera.NearPlane = viewport.Camera.NearPlane;
            camera.FarPlane = viewport.Camera.FarPlane;
            camera.Yaw = 0;
            camera.Pitch = (float)ModelOrbitMotion.InitialPitch;
            evidence["camera"] = new { yaw = camera.Yaw, pitch = camera.Pitch, camera.Distance,
                target = new[] { camera.Target.X, camera.Target.Y, camera.Target.Z }, camera.FieldOfView,
                width = 640, height = 580 };
            evidence["defaultLighting"] = new { direction = new[] { direction.X, direction.Y, direction.Z }, intensity, ambient };

            var baseline = await CaptureAsync("dynaudio-software");
            // The lower front of the charging case excludes its logo, seams and silhouette.
            // The previous clipped rendering had a P90-P10 span of only five here.
            var caseContrast = RegionContrast(baseline, .34, .62, .66, .70);
            evidence["frontCase"] = new { roi = new[] { .34, .62, .66, .70 }, contrast = caseContrast,
                minimumRequiredSpan = 12, previousFlatRenderingSpan = 5 };

            scene.Light.Direction = Vector3.Normalize(new Vector3(-3, -2, -3));
            viewport.InvalidateScene();
            var left = await CaptureAsync("dynaudio-software-key-left");
            scene.Light.Direction = Vector3.Normalize(new Vector3(3, -2, -3));
            viewport.InvalidateScene();
            var right = await CaptureAsync("dynaudio-software-key-right");
            var lightDifference = CompareInterior(left, right, lumaThreshold: 8);
            evidence["keyLightDifference"] = lightDifference;

            scene.Light.Direction = direction;
            foreach (var material in materials) material.BaseColorTexture = null;
            viewport.InvalidateScene();
            var withoutBase = await CaptureAsync("dynaudio-software-without-base-colour");
            var baseDifference = CompareInterior(baseline, withoutBase, lumaThreshold: 8);
            evidence["baseColourDifference"] = baseDifference;
            for (var index = 0; index < materials.Length; index++) materials[index].BaseColorTexture = savedTextures[index];
            viewport.InvalidateScene();

            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            var dark = await CaptureAsync("dynaudio-software-dark");
            var darkContrast = RegionContrast(dark, .34, .62, .66, .70);
            evidence["darkFrontCase"] = darkContrast;
            evidence["darkBackground"] = dark.Pixels[..4];
            // Record every image and metric before the assertions so a failed renderer
            // still leaves reviewable evidence; thresholds are independent of its output.
            WriteEvidence();
            check(caseContrast.ForegroundFraction > .95 && caseContrast.Span >= 12,
                $"CPU front-case curvature remains visible outside logos and edges: P90-P10={caseContrast.Span:F2}");
            check(lightDifference.InteriorPixels > 15_000 && lightDifference.ChangedFraction >= .05,
                $"Moving the key light changes fixed-pose interior pixels: changed={lightDifference.ChangedFraction:P2}, mean luma delta={lightDifference.MeanAbsoluteLumaDifference:F2}");
            check(baseDifference.InteriorPixels > 15_000 && baseDifference.ChangedPixels > 500
                && baseDifference.ChangedFraction >= .005,
                $"Removing the base-colour map changes actual CPU pixels: changed={baseDifference.ChangedFraction:P2}");
            check(dark.Pixels[0] == 32 && dark.Pixels[1] == 32 && dark.Pixels[2] == 32
                && darkContrast.ForegroundFraction > .95 && darkContrast.Span >= 12,
                $"Dark theme keeps the CPU case shaded against its background: P90-P10={darkContrast.Span:F2}");
            check(viewport.Info.ActiveBackend == RenderBackendKind.Software && string.IsNullOrEmpty(viewport.Info.Error),
                "Lighting and texture comparisons stay on the real software backend without renderer errors");
            return evidence;

            async Task<Shot> CaptureAsync(string name)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var frame = await viewport.CaptureAsync(new CaptureRequest
                {
                    Format = CaptureFormat.Rgba8, Size = new PixelSize(640, 580), Camera = camera
                }, timeout.Token);
                var pixels = frame.Pixels.ToArray();
                check(pixels.Length == 640 * 580 * 4, $"{name}: native CPU capture returns every RGBA pixel");
                var shot = new Shot(name, pixels, frame.Size.Width, frame.Size.Height, Hash(pixels));
                await frame.SaveAsync(Path.Combine(output, name + ".png"), timeout.Token);
                shots.Add(new { shot.Name, shot.Width, shot.Height, shot.Sha256,
                    foregroundPixels = CountForeground(shot), backend = viewport.Info.ActiveBackend.ToString() });
                WriteEvidence();
                return shot;
            }
        }
        finally
        {
            for (var index = 0; index < materials.Length; index++) materials[index].BaseColorTexture = savedTextures[index];
            scene.Light.Direction = direction;
            scene.Light.Intensity = intensity;
            scene.Light.Ambient = ambient;
            scene.Environment = environment;
            Application.Current!.RequestedThemeVariant = theme;
            view.Motion.SetActive(motionActive);
            viewport.InvalidateScene();
            WriteEvidence();
        }

        void WriteEvidence() => File.WriteAllText(Path.Combine(output, "software-lighting-receipts.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object VerifySourceMapsAndRestoration(byte[] glb, GltfModel model, Action<bool, string> check)
    {
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12, 4));
        using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var binaryStart = 28 + jsonLength;
        check(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(24 + jsonLength, 4)) == 0x004e4942,
            "Original GLB has an embedded binary chunk for independently extracting its PNG maps");
        var sourceMaterial = root.GetProperty("materials")[0];
        var loaded = model.Materials["1001"];
        var mapCases = new (string Name, JsonElement Reference, Texture? Texture)[]
        {
            ("base-colour", sourceMaterial.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture"), loaded.BaseColorTexture),
            ("metallic-roughness", sourceMaterial.GetProperty("pbrMetallicRoughness").GetProperty("metallicRoughnessTexture"), loaded.MetallicRoughnessTexture),
            ("normal", sourceMaterial.GetProperty("normalTexture"), loaded.NormalTexture),
            ("emissive", sourceMaterial.GetProperty("emissiveTexture"), loaded.EmissiveTexture)
        };
        var maps = new List<object>();
        var sourceHashes = new Dictionary<string, string>();
        foreach (var map in mapCases)
        {
            var textureIndex = map.Reference.GetProperty("index").GetInt32();
            var imageIndex = root.GetProperty("textures")[textureIndex].GetProperty("source").GetInt32();
            var image = root.GetProperty("images")[imageIndex];
            var bufferView = root.GetProperty("bufferViews")[image.GetProperty("bufferView").GetInt32()];
            var offset = bufferView.TryGetProperty("byteOffset", out var property) ? property.GetInt32() : 0;
            var length = bufferView.GetProperty("byteLength").GetInt32();
            var source = glb.AsSpan(binaryStart + offset, length);
            var encoded = map.Texture?.Encoded;
            check(image.GetProperty("mimeType").GetString() == "image/png" && source[..8].SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), $"{map.Name}: source map is an embedded PNG");
            var width = BinaryPrimitives.ReadInt32BigEndian(source.Slice(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(source.Slice(20, 4));
            var sourceHash = Hash(source);
            sourceHashes.Add(map.Name, sourceHash);
            check(encoded is not null && source.SequenceEqual(encoded) && width == 2048 && height == 2048,
                $"{map.Name}: loaded texture exactly matches every byte of the original 2048×2048 GLB PNG");
            maps.Add(new { map.Name, textureIndex, imageIndex, width, height, length, sha256 = sourceHash,
                loadedSha256 = encoded is null ? null : Hash(encoded) });
        }

        var meshes = model.Scene.EnumerateMeshes().Select(mesh => mesh.Node).ToArray();
        var originals = meshes.Select(mesh => mesh.Material).ToArray();
        var sourceScalars = originals.Select(material => (material.Metallic, material.Roughness, material.EmissiveColor)).ToArray();
        var adaptation = new SoftwareModelMaterials(model);
        adaptation.Apply(true);
        var software = meshes.Select(mesh => mesh.Material).ToArray();
        check(software.Zip(originals).All(pair => !ReferenceEquals(pair.First, pair.Second))
            && software.All(material => material.BaseColorTexture?.Encoded is { } bytes
                && bytes.AsSpan().SequenceEqual(loaded.BaseColorTexture!.Encoded!)),
            "Software materials are separate instances and preserve the original base-colour PNG");
        check(software.All(material => float.IsFinite(material.Metallic) && material.Metallic is >= 0 and <= 1
            && float.IsFinite(material.Roughness) && material.Roughness is >= 0 and <= 1)
            && software.Any(material => material.Metallic < loaded.Metallic)
            && software.Any(material => material.Roughness < loaded.Roughness)
            && software.Any(material => material.EmissiveColor.LengthSquared() < loaded.EmissiveColor.LengthSquared()),
            "Software fallback derives finite metallic, roughness and emissive scalars from the original maps");
        adaptation.Apply(false);
        check(meshes.Select((mesh, index) => ReferenceEquals(mesh.Material, originals[index])
                && (mesh.Material.Metallic, mesh.Material.Roughness, mesh.Material.EmissiveColor) == sourceScalars[index]).All(value => value),
            "Returning to automatic/GPU restores every exact original material instance and scalar");
        adaptation.Apply(true);
        check(meshes.Select((mesh, index) => ReferenceEquals(mesh.Material, software[index])).All(value => value),
            "Returning to software reuses the derived materials without modifying the source material");
        adaptation.Apply(false);
        check(meshes.Select((mesh, index) => ReferenceEquals(mesh.Material, originals[index])).All(value => value),
            "The final software-to-automatic cycle leaves the original model intact without opening a GPU backend");
        check(mapCases.All(map => map.Texture?.Encoded is { } bytes && Hash(bytes) == sourceHashes[map.Name]),
            "Software/automatic cycles leave all four original encoded maps unchanged");
        return new { maps, meshCount = meshes.Length,
            softwareMaterials = software.Select((material, index) => new { mesh = index, material.Metallic, material.Roughness,
                emissive = new[] { material.EmissiveColor.X, material.EmissiveColor.Y, material.EmissiveColor.Z } }).ToArray(),
            sampling = adaptation.Diagnostics.Select(diagnostic => new
            {
                diagnostic.MeshName, diagnostic.MetallicRoughnessSamples, diagnostic.EmissiveSamples,
                diagnostic.MetallicRoughnessStatus, diagnostic.EmissiveStatus,
                metallicRoughnessMean = new[] { diagnostic.MetallicRoughnessMean.X, diagnostic.MetallicRoughnessMean.Y, diagnostic.MetallicRoughnessMean.Z },
                emissiveMean = new[] { diagnostic.EmissiveMean.X, diagnostic.EmissiveMean.Y, diagnostic.EmissiveMean.Z }
            }).ToArray(),
            restoredExactOriginalReferences = true };
    }

    private static byte[] ReadSourceBytes()
    {
        using var source = AssetLoader.Open(new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"));
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Difference CompareInterior(Shot before, Shot after, double lumaThreshold)
    {
        var interior = 0;
        var changed = 0;
        var sum = 0d;
        var maximum = 0d;
        for (var y = 3; y < before.Height - 3; y++)
        for (var x = 3; x < before.Width - 3; x++)
        {
            // Erode the common foreground by three pixels: antialiasing and changed
            // silhouette pixels cannot masquerade as a surface lighting response.
            var valid = true;
            for (var dy = -3; dy <= 3 && valid; dy++)
            for (var dx = -3; dx <= 3; dx++)
                if (!Foreground(before, x + dx, y + dy) || !Foreground(after, x + dx, y + dy)) { valid = false; break; }
            if (!valid) continue;
            interior++;
            var difference = Math.Abs(Luma(before, x, y) - Luma(after, x, y));
            if (difference > lumaThreshold) changed++;
            sum += difference;
            maximum = Math.Max(maximum, difference);
        }
        return new(interior, changed, interior == 0 ? 0 : (double)changed / interior,
            interior == 0 ? 0 : sum / interior, maximum);
    }

    private static Contrast RegionContrast(Shot shot, double left, double top, double right, double bottom)
    {
        var values = new List<double>();
        var foreground = 0;
        for (var y = (int)(top * shot.Height); y < (int)(bottom * shot.Height); y++)
        for (var x = (int)(left * shot.Width); x < (int)(right * shot.Width); x++)
        {
            values.Add(Luma(shot, x, y));
            if (Foreground(shot, x, y)) foreground++;
        }
        values.Sort();
        var p10 = values[(int)((values.Count - 1) * .10)];
        var p90 = values[(int)((values.Count - 1) * .90)];
        return new(values.Count, foreground, (double)foreground / values.Count,
            p10, p90, p90 - p10, values.Average());
    }

    private static int CountForeground(Shot shot)
    {
        var count = 0;
        for (var y = 0; y < shot.Height; y++)
        for (var x = 0; x < shot.Width; x++) if (Foreground(shot, x, y)) count++;
        return count;
    }

    private static bool Foreground(Shot shot, int x, int y)
    {
        var offset = (y * shot.Width + x) * 4;
        return shot.Pixels[offset + 3] > 0 && Math.Abs(shot.Pixels[offset] - shot.Pixels[0])
            + Math.Abs(shot.Pixels[offset + 1] - shot.Pixels[1]) + Math.Abs(shot.Pixels[offset + 2] - shot.Pixels[2]) > 24;
    }

    private static double Luma(Shot shot, int x, int y)
    {
        var offset = (y * shot.Width + x) * 4;
        return shot.Pixels[offset] * .2126 + shot.Pixels[offset + 1] * .7152 + shot.Pixels[offset + 2] * .0722;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
