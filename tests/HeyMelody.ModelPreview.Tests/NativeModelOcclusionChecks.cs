using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Ava3D;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using HeyMelodyWindows.App;

/// <summary>
/// Uses the SDK's actual renderer to reproduce the original asset's alpha pass and
/// compare it with the app's loaded scene. Reversing mesh insertion order is a functional
/// depth-buffer check: an opaque shell must continue occluding its rear surfaces.
/// No model geometry, texture, camera transform or renderer implementation changes.
/// </summary>
internal static class NativeModelOcclusionChecks
{
    private sealed record CameraCase(string Name, float YawDegrees, float PitchDegrees);
    private sealed record PixelComparison(int ChangedPixels, int TotalPixels, double ChangedFraction);
    private sealed record Shot(byte[] Pixels, int Width, int Height, int ForegroundPixels, string Sha256);

    public static async Task RunAsync(Window window, EncoFree4ModelView original,
        string output, Action<bool, string> check, List<object> receipts)
    {
        var bytes = ReadOriginalBytes(check);
        var raw = await Task.Run(() => GltfLoader.LoadModel(bytes, "Enco Free4 original alpha scene"));
        var fixedModel = await EncoFree4ModelView.LoadModelAsync();
        await RunAsync(window, original, raw, fixedModel, output, check, receipts,
            "Enco Free4", EncoFree4ModelView.ModelSha256, 28);
    }

    internal static async Task RunAsync(Window window, DeviceModelView original,
        GltfModel raw, GltfModel fixedModel, string output, Action<bool, string> check,
        List<object> receipts, string modelName, string modelSha256, int expectedMeshes,
        bool requireGpu = false, bool windowCaptureRequired = true)
    {
        var rawMaterial = raw.Materials["1001"];
        var fixedMaterial = fixedModel.Materials["1001"];
        check(rawMaterial.Blend == BlendMode.Alpha && rawMaterial.BaseColor.W == 1,
            "Original GLB loads into the SDK alpha pass despite fully opaque base color");
        ConfigureLighting(raw.Scene);
        ConfigureLighting(fixedModel.Scene);
        var rawMeshes = CollectMeshes(raw.Root);
        var fixedMeshes = CollectMeshes(fixedModel.Root);
        check(rawMeshes.Count == expectedMeshes && fixedMeshes.Count == expectedMeshes,
            $"Occlusion comparison preserves all {expectedMeshes} original model meshes");
        check(fixedMaterial.Blend == BlendMode.Opaque && fixedMaterial.DepthWrite && fixedMaterial.DepthTest
            && fixedMaterial.Cull == rawMaterial.Cull && fixedMaterial.BaseColor == rawMaterial.BaseColor,
            "Opaque correction enables depth writes while preserving original culling and base color");
        check(raw.Scene.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0)
            == fixedModel.Scene.EnumerateMeshes().Sum(mesh => mesh.Node.Mesh?.TriangleCount ?? 0),
            "Opaque correction preserves the complete source triangle count");
        check(rawMeshes.All(mesh => mesh.RenderOrder == 0) && fixedMeshes.All(mesh => mesh.RenderOrder == 0),
            "Both occlusion scenes keep every original mesh in the same default render pass");
        var rawReversed = false;
        var fixedReversed = false;
        var fixedBounds = fixedModel.Root.WorldBounds;
        var savedContent = window.Content;
        var viewport = new Ava3DView
        {
            AutoFit = false, IsAnimationEnabled = false, IsOrbitEnabled = false,
            IsPanEnabled = false, IsZoomEnabled = false, IsPickingEnabled = false,
            RenderTrigger = RenderTrigger.OnDemand, SampleCount = 4, MaxFrameRate = 30
        };
        if (requireGpu) viewport.PreferredBackend = RenderBackendKind.Automatic;
        var readyScenes = 0;
        viewport.SceneReady += (_, _) => readyScenes++;
        var comparisons = new List<object>();
        var totalRawOrderChanges = 0;
        var totalFixedOrderChanges = 0;
        var maximumFixedOrderChangedFraction = 0d;
        try
        {
            window.Content = new Border { Padding = new Thickness(16), Child = viewport };
            window.Title = modelName + " — native opaque occlusion regression";
            if (!window.IsVisible) window.Show();
            window.Activate();
            check(!original.AnimationActive, "Occlusion comparison detaches and pauses the app's automatic camera");
            var hwnd = window.TryGetPlatformHandle()?.Handle
                ?? throw new InvalidOperationException("Native occlusion window has no HWND.");
            viewport.Camera.Fit(fixedBounds, margin: 1.08f);
            CameraCase[] cameras =
            [
                new("front", 0, 15), new("quarter", 45, -10),
                new("side", 90, 25), new("rear", 180, 40)
            ];
            foreach (var camera in cameras)
            {
                viewport.Camera.Yaw = camera.YawDegrees * MathF.PI / 180;
                viewport.Camera.Pitch = camera.PitchDegrees * MathF.PI / 180;
                viewport.InvalidateCamera();
                if (rawReversed) { ReverseSiblings(raw.Root); rawReversed = false; }
                await AssignSceneAsync(raw.Scene);
                var alpha = await CaptureAsync(viewport, output, "occlusion-" + camera.Name + "-original-alpha", check);
                var originalBackend = viewport.Info.ActiveBackend;
                if (requireGpu) CheckGpu(originalBackend, camera.Name + ": original");
                ReverseSiblings(raw.Root);
                rawReversed = true;
                check(!rawMeshes.SequenceEqual(CollectMeshes(raw.Root)),
                    $"{camera.Name}: sibling reversal changes original mesh submission order within the same pass");
                viewport.InvalidateScene();
                var alphaReversed = await CaptureAsync(viewport, output, "occlusion-" + camera.Name + "-alpha-reversed", check);
                var alphaOrder = Compare(alpha, alphaReversed);

                if (fixedReversed) { ReverseSiblings(fixedModel.Root); fixedReversed = false; }
                await AssignSceneAsync(fixedModel.Scene);
                var corrected = await CaptureAsync(viewport, output, "occlusion-" + camera.Name + "-corrected", check);
                var correctedBackend = viewport.Info.ActiveBackend;
                if (requireGpu) CheckGpu(correctedBackend, camera.Name + ": corrected");
                if (camera.Name is "front" or "quarter")
                {
                    try
                    {
                        NativeWindowCapture.Save(hwnd, Path.Combine(output, "occlusion-" + camera.Name + "-corrected-window.png"));
                    }
                    catch (InvalidOperationException error) when (!windowCaptureRequired)
                    {
                        receipts.Add(new
                        {
                            state = "native-window-capture-warning", camera = camera.Name,
                            source = "Additional Win32 desktop screenshot", required = false,
                            mandatorySdkFrameCaptured = true, error = error.Message
                        });
                    }
                }
                ReverseSiblings(fixedModel.Root);
                fixedReversed = true;
                check(!fixedMeshes.SequenceEqual(CollectMeshes(fixedModel.Root)),
                    $"{camera.Name}: sibling reversal changes corrected mesh submission order within the same pass");
                viewport.InvalidateScene();
                var correctedReversed = await CaptureAsync(viewport, output,
                    "occlusion-" + camera.Name + "-corrected-reversed", check);
                var correctedOrder = Compare(corrected, correctedReversed);
                var repaired = Compare(alpha, corrected);
                check(repaired.ChangedPixels > 500,
                    $"{camera.Name}: actual corrected rendering removes original alpha-pass overdraw");
                // A small tolerance allows raster edge rounding and coplanar source-model seams.
                check(correctedOrder.ChangedFraction <= 0.003,
                    $"{camera.Name}: opaque depth keeps the visible surface stable when sibling mesh insertion order reverses");
                check(fixedModel.Root.WorldBounds.Min == fixedBounds.Min
                    && fixedModel.Root.WorldBounds.Max == fixedBounds.Max,
                    $"{camera.Name}: draw-order stress leaves the source geometry and world bounds unchanged");
                totalRawOrderChanges += alphaOrder.ChangedPixels;
                totalFixedOrderChanges += correctedOrder.ChangedPixels;
                maximumFixedOrderChangedFraction = Math.Max(maximumFixedOrderChangedFraction, correctedOrder.ChangedFraction);
                comparisons.Add(new
                {
                    camera.Name, camera.YawDegrees, camera.PitchDegrees,
                    originalAlphaSha256 = alpha.Sha256, correctedSha256 = corrected.Sha256,
                    correctedReverseSha256 = correctedReversed.Sha256,
                    originalAlphaOrderSensitivity = alphaOrder,
                    correctedOrderSensitivity = correctedOrder,
                    originalVsCorrected = repaired,
                    originalBackend = originalBackend.ToString(), correctedBackend = correctedBackend.ToString(),
                    corrected.ForegroundPixels, corrected.Width, corrected.Height,
                    originalSceneItems = rawMeshes.Count, correctedSceneItems = fixedMeshes.Count
                });
            }
            // Alpha draws are sorted by world-origin distance. With distinct origins,
            // reversing siblings can leave their actual draw order unchanged; its
            // sensitivity is diagnostic, not a required defect in every source model.
            check(comparisons.Count == cameras.Length && maximumFixedOrderChangedFraction <= 0.003,
                "All four camera angles complete with opaque occlusion independent of sibling submission order");
            check(string.IsNullOrEmpty(viewport.Info.Error), "Occlusion renderer reports no error");
            receipts.Add(new
            {
                state = "native-occlusion-regression", modelName, modelSha256, requireGpu, windowCaptureRequired,
                originalMaterial = new { name = "1001", blend = rawMaterial.Blend.ToString(), rawMaterial.DepthTest, rawMaterial.DepthWrite,
                    alpha = rawMaterial.BaseColor.W },
                correctedMaterial = new { name = "1001", blend = fixedMaterial.Blend.ToString(), fixedMaterial.DepthTest, fixedMaterial.DepthWrite,
                    alpha = fixedMaterial.BaseColor.W },
                totalRawOrderChanges, totalFixedOrderChanges, maximumFixedOrderChangedFraction,
                alphaOrderDiagnostic = "Alpha sorting uses node world-origin distance, so sibling reversal need not change its output; opaque stability is checked absolutely at every angle.",
                renderer = viewport.Info.Renderer, device = viewport.Info.Device,
                backend = viewport.Info.ActiveBackend.ToString(), viewport.Info.Triangles, viewport.Info.DrawCalls,
                readyScenes, comparisons
            });
        }
        finally
        {
            if (rawReversed) ReverseSiblings(raw.Root);
            if (fixedReversed) ReverseSiblings(fixedModel.Root);
            window.Content = savedContent;
        }

        async Task AssignSceneAsync(Scene scene)
        {
            var expected = readyScenes + 1;
            viewport.Scene = scene;
            viewport.InvalidateScene();
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (readyScenes < expected)
            {
                if (started.Elapsed > TimeSpan.FromSeconds(12))
                    throw new TimeoutException("Occlusion model textures did not become scene-ready.");
                await Task.Delay(20);
            }
        }

        void CheckGpu(RenderBackendKind backend, string description) => check(
            backend is not RenderBackendKind.Software and not RenderBackendKind.Automatic,
            description + ": actual GPU backend renders the scene without software fallback");
    }

    private static byte[] ReadOriginalBytes(Action<bool, string> check)
    {
        using var source = AssetLoader.Open(new Uri("avares://HeyMelody/Assets/Devices/EncoFree4/model.glb"));
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        check(bytes.Length == 9_727_500 && Convert.ToHexString(SHA256.HashData(bytes)).Equals(
            "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc", StringComparison.OrdinalIgnoreCase),
            "Occlusion reproducer uses the unchanged independently verified Free4 cloud GLB");
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var materials = json.RootElement.GetProperty("materials");
        check(materials.GetArrayLength() == 1 && materials[0].GetProperty("name").GetString() == "1001"
            && materials[0].GetProperty("alphaMode").GetString() == "BLEND",
            "Original asset declares its sole shared material as BLEND");
        return bytes;
    }

    private static void ConfigureLighting(Scene scene)
    {
        scene.Environment = EnvironmentLight.Studio(1.2f);
        scene.Light.Direction = Vector3.Normalize(new Vector3(-1, -2, -3));
        scene.Light.Intensity = 2;
        scene.Light.Ambient = .15f;
        scene.ShadowMapSize = 1024;
        scene.Background = Avalonia.Media.Color.FromRgb(243, 243, 243);
    }

    private static List<MeshNode> CollectMeshes(Node root)
    {
        var result = new List<MeshNode>();
        Visit(root);
        return result;
        void Visit(Node node)
        {
            if (node is MeshNode mesh) result.Add(mesh);
            foreach (var child in node.Children) Visit(child);
        }
    }

    // Preserve every parent relationship and local transform while changing graph
    // traversal order. RenderOrder stays at zero: this exercises a single pass.
    private static void ReverseSiblings(Node node)
    {
        var children = node.Children.ToArray();
        foreach (var child in children) ReverseSiblings(child);
        node.Children.Clear();
        for (var index = children.Length - 1; index >= 0; index--) node.Children.Add(children[index]);
    }

    private static async Task<Shot> CaptureAsync(Ava3DView viewport, string output, string name, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var frame = await viewport.CaptureAsync(new CaptureRequest
        {
            Format = CaptureFormat.Rgba8, Size = new PixelSize(700, 500)
        }, timeout.Token);
        var pixels = frame.Pixels.ToArray();
        check(pixels.Length == 700 * 500 * 4, $"{name}: SDK returns a complete rendered RGBA frame");
        var foreground = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 3] > 0 && Math.Abs(pixels[offset] - pixels[0])
                + Math.Abs(pixels[offset + 1] - pixels[1]) + Math.Abs(pixels[offset + 2] - pixels[2]) > 18) foreground++;
        check(foreground > 15_000, $"{name}: rendered source geometry occupies a visible region");
        await frame.SaveAsync(Path.Combine(output, name + "-3d.png"), timeout.Token);
        return new(pixels, frame.Size.Width, frame.Size.Height, foreground,
            Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant());
    }

    private static PixelComparison Compare(Shot before, Shot after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
            throw new InvalidOperationException("Occlusion comparison dimensions changed.");
        var changed = 0;
        for (var offset = 0; offset < before.Pixels.Length; offset += 4)
            if (Math.Abs(before.Pixels[offset] - after.Pixels[offset])
                + Math.Abs(before.Pixels[offset + 1] - after.Pixels[offset + 1])
                + Math.Abs(before.Pixels[offset + 2] - after.Pixels[offset + 2]) > 12) changed++;
        return new(changed, before.Width * before.Height, (double)changed / (before.Width * before.Height));
    }
}
