using System.Reflection;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Protocol;

internal static class NativeCalibrationDescriptionTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("native calibration forwards only qualified result description metadata without device writes", async () =>
        {
            var transport = new Transport();
            var context = new CalibrationDeviceContext { StableKey = "description-fixture", Generation = 1,
                HearingV2 = true, Commands = new HashSet<ushort>(), Events = new HashSet<byte>() };
            var engine = new CalibrationEngine(transport, context);
            int nativeCalls = 0;
            await using var service = new NativeHeadsetService(() =>
            {
                nativeCalls++;
                throw new InvalidOperationException("Result projection enumerated Bluetooth");
            }, new DeviceWhitelistStore(Path.Combine(scratch, "description-" + Guid.NewGuid().ToString("N") + ".json")));
            Field("_calibration").SetValue(service, engine);
            Field("_calibrationState").SetValue(service, new UiCalibrationState { Handle = "description", Generation = 1 });
            var publish = (Action<CalibrationSnapshot>)typeof(NativeHeadsetService)
                .GetMethod("EngineChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Action<CalibrationSnapshot>), service);
            var curve = new HearingFilterCurve(123, 3, 0, [1, 0, 0, 1, 0, 0], [1, 0, 0, 1, 0, 0],
                12, null, CalibrationCodecs.FilterCommand);
            var valid = new CalibrationSnapshot { Generation = 1, Workflow = CalibrationWorkflow.HearingV2,
                Stage = CalibrationStage.ResultReady, Uid = 123, FiltersReady = true, CanPreview = true,
                RestoreDescriptionId = 1, Curves = [curve] };

            for (byte band = 0; band <= 2; band++)
            {
                publish(valid with { Curves = [curve with { EnhanceType = band }] });
                var state = service.GetCalibration("description");
                Check(state.HearingEnhanceType == band && state.RestoreDescriptionId == 1,
                    "Valid protocol metadata did not reach the completion page.");
            }
            foreach (var unknown in new byte?[] { null, 3, 255 })
            {
                publish(valid with { Curves = [curve with { EnhanceType = unknown }] });
                Check(service.GetCalibration("description").HearingEnhanceType is null,
                    "An unknown enhancement type acquired a frequency label.");
            }
            foreach (var unknown in new byte?[] { null, 0, 21, 255 })
            {
                publish(valid with { RestoreDescriptionId = unknown });
                Check(service.GetCalibration("description").RestoreDescriptionId is null,
                    "An unknown result description ID reached personalized copy.");
            }
            foreach (var snapshot in new[]
            {
                valid with { FiltersReady = false },
                valid with { Curves = [] },
                valid with { Curves = [curve with { Uid = 124 }] },
                valid with { Curves = [curve with { SourceCommand = CalibrationCodecs.EarScanFilterCommand }] },
                valid with { Curves = [curve, curve] },
                valid with { Curves = [], EarScanCurves = [curve with { EnhanceType = 2 }] }
            })
            {
                publish(snapshot);
                Check(service.GetCalibration("description").HearingEnhanceType is null,
                    "Unready, stale, ambiguous or non-primary filter metadata produced a completion claim.");
            }
            Check(nativeCalls == 0 && transport.Requests == 0,
                "Projecting result metadata caused a native enumeration or a device command.");
        });

        await test("generated IPC retains completion metadata including low-frequency zero and absent fields", () =>
        {
            foreach (var options in new[] { RpcWire.Json, HostWire.Json })
            {
                for (byte band = 0; band <= 2; band++)
                {
                    var state = new UiCalibrationState { Handle = "description", HearingEnhanceType = band, RestoreDescriptionId = 20 };
                    string json = JsonSerializer.Serialize(state, options);
                    using var document = JsonDocument.Parse(json);
                    Check(document.RootElement.GetProperty("hearingEnhanceType").GetByte() == band
                        && document.RootElement.GetProperty("restoreDescriptionId").GetByte() == 20,
                        "Generated IPC omitted or changed a completion metadata field.");
                    var restored = JsonSerializer.Deserialize<UiCalibrationState>(json, options)!;
                    Check(restored.HearingEnhanceType == band && restored.RestoreDescriptionId == 20,
                        "IPC completion metadata did not survive round-trip serialization.");
                }
                var legacy = JsonSerializer.Deserialize<UiCalibrationState>("{\"handle\":\"legacy\"}", options)!;
                Check(legacy.HearingEnhanceType is null && legacy.RestoreDescriptionId is null,
                    "A legacy IPC result invented missing completion metadata.");
            }
            return Task.CompletedTask;
        });
    }

    private static FieldInfo Field(string name) => typeof(NativeHeadsetService)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Transport : ICalibrationTransport
    {
        public bool IsConnected => true;
        public event Action<Packet>? Notification { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public int Requests { get; private set; }
        public Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
        {
            Requests++;
            throw new InvalidOperationException("Result projection issued a device command");
        }
    }
}
