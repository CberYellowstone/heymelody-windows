using System.Reflection;
using System.Text.Json;

// Deterministic reflective fixture builder: every settable member gets a distinct non-default value (or null when sparse),
// so a member added later to a worker wire type is covered by the byte checks without editing them; an unsupported type throws.
internal sealed class FixtureFiller(bool sparse)
{
    private static readonly string[] Texts =
    [
        "耳机「欢律」降噪", "quote\" back\\slash\n\ttab <b>&'x'", "emoji 😀 é \u2028 end", "\u0001\u001f ctl", "plain"
    ];
    private static readonly TimeSpan[] Offsets =
        [TimeSpan.FromMinutes(330), TimeSpan.FromHours(-3), TimeSpan.Zero, TimeSpan.FromHours(14), TimeSpan.FromMinutes(-570)];
    private int _next = 10;

    public static T Full<T>() => (T)new FixtureFiller(false).Build(typeof(T), root: true)!;
    public static T Sparse<T>() => (T)new FixtureFiller(true).Build(typeof(T), root: true)!;
    public static object Full(Type type) => new FixtureFiller(false).Build(type, root: true)!;
    public static object Sparse(Type type) => new FixtureFiller(true).Build(type, root: true)!;

    private object? Build(Type type, bool root)
    {
        int n = _next++;
        if (Nullable.GetUnderlyingType(type) is { } inner) return sparse ? null : Build(inner, root: false);
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return n;
        if (type == typeof(long)) return 9_007_199_254_740_000L + n;
        if (type == typeof(uint)) return uint.MaxValue - (uint)n;
        if (type == typeof(ulong)) return ulong.MaxValue - (ulong)n;
        if (type == typeof(byte)) return (byte)(100 + n);
        if (type == typeof(double)) return n + 0.125;
        if (type == typeof(DateTimeOffset))
            return new DateTimeOffset(2026, 10, 1 + n % 28, 8, 30, 15, Offsets[n % Offsets.Length]).AddTicks(1234567 + n);
        if (type == typeof(JsonElement))
            return JsonDocument.Parse(sparse ? "null" : "{\"k\":[1,2.5,\"中文\\n\"],\"n\":null}").RootElement.Clone();
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().Max()!;
        if (sparse && !root) return null;
        if (type == typeof(string)) return Texts[n % Texts.Length] + n;
        if (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() is var d
            && (d == typeof(List<>) || d == typeof(IReadOnlyList<>)))
        {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var items = new object?[2]; for (int i = 0; i < items.Length; i++) items[i] = Build(element, root: true);
            if (type.IsArray || n % 2 == 1)
            {
                var array = Array.CreateInstance(element, items.Length);
                for (int i = 0; i < items.Length; i++) array.SetValue(items[i], i);
                return array;
            }
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in items) list.Add(item);
            return list;
        }
        if (!type.IsClass) throw new NotSupportedException("Fixture builder cannot create " + type);
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = constructor.GetParameters();
        var instance = constructor.Invoke(parameters.Select(p => Build(p.ParameterType, root: false)).ToArray());
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0
                && !parameters.Any(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
                property.SetValue(instance, Build(property.PropertyType, root: false));
        return instance;
    }
}
