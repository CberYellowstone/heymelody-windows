using System.Collections;
using System.Reflection;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

// UiSnapshot.Clone() replaces the Host's JSON round trip, so it must stay observably equal to it.
internal static class SnapshotCloneTests
{
    private static readonly PropertyInfo[] Properties = typeof(UiSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("UiSnapshot fixtures set every property to a non-default value", () =>
        {
            var fixture = FixtureFiller.Full<UiSnapshot>(); var fresh = new UiSnapshot();
            Check(Properties.Length > 40, "The snapshot reflection enumeration found its properties.");
            foreach (var property in Properties)
            {
                object? value = property.GetValue(fixture), initial = property.GetValue(fresh);
                bool isDefault = value is null
                    || (value is IEnumerable items and not string ? !items.Cast<object>().Any() : Equals(value, initial));
                Check(!isDefault, property.Name + " is default in the fixture; teach FixtureFiller or Clone about it.");
            }
            return Task.CompletedTask;
        });

        await test("UiSnapshot.Clone equals the RpcWire.Json round trip byte for byte", () =>
        {
            foreach (var source in new[] { FixtureFiller.Full<UiSnapshot>(), FixtureFiller.Sparse<UiSnapshot>(), new UiSnapshot() })
            {
                var clone = source.Clone();
                var roundTrip = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(source, RpcWire.Json), RpcWire.Json)!;
                Check(JsonSerializer.Serialize(clone, RpcWire.Json) == JsonSerializer.Serialize(roundTrip, RpcWire.Json),
                    "Clone and the JSON round trip serialize differently.");
                foreach (var property in Properties)
                    Check((property.GetValue(clone) is null) == (property.GetValue(roundTrip) is null),
                        property.Name + " nullness differs between Clone and the JSON round trip.");
                Check(!ReferenceEquals(clone, source));
            }
            return Task.CompletedTask;
        });

        await test("UiSnapshot.Clone restores initializer values for null members like the round trip", () =>
        {
            var source = FixtureFiller.Sparse<UiSnapshot>(); var clone = source.Clone();
            Check(clone.BackendInstanceId == "" && clone.Identity == "" && clone.DeviceName == "");
            Check(clone.EqualizerFrequencies is { Count: 0 } && clone.AncModes is { Count: 0 } && clone.FeatureSections is { Count: 0 }
                && clone.HearingProfiles is { Count: 0 } && clone.AncOptions is { Count: 0 } && clone.EqualizerOptions is { Count: 0 }
                && clone.SpatialOptions is { Count: 0 } && clone.EqualizerEntries is { Count: 0 } && clone.Warnings is { Count: 0 });
            Check(source.Identity is null && source.Warnings is null, "The source is left untouched.");
            return Task.CompletedTask;
        });

        await test("changing a UiSnapshot clone never changes the original", () =>
        {
            var source = FixtureFiller.Full<UiSnapshot>();
            string before = JsonSerializer.Serialize(source, RpcWire.Json);
            var clone = source.Clone();
            foreach (var property in Properties.Where(p => p.SetMethod is { IsPublic: true }))
                property.SetValue(clone, Changed(property.PropertyType, property.GetValue(clone)));
            Check(JsonSerializer.Serialize(source, RpcWire.Json) == before, "The original changed.");
            Check(JsonSerializer.Serialize(clone, RpcWire.Json) != before, "The clone mutation had no effect.");
            return Task.CompletedTask;
        });
    }

    private static object? Changed(Type type, object? value) => value switch
    {
        bool flag => !flag, int number => number + 1, string => "changed",
        _ => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null
    };

    private static void Check(bool value, string reason = "Snapshot clone assertion failed.") { if (!value) throw new InvalidOperationException(reason); }
}
