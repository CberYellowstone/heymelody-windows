using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HeyMelodyWindows.Host;

public sealed class RpcRequest
{
    public int SchemaVersion { get; set; } = 1;
    public long Id { get; set; }
    public string Operation { get; set; } = "";
    public string? Identity { get; set; }
    public string[]? DeviceIdentities { get; set; }
    public HeyMelodyWindows.App.RefreshScope? RefreshScope { get; set; }
    public JsonElement Action { get; set; }
}

public sealed class RpcResponse
{
    public int SchemaVersion { get; set; } = 1;
    public long Id { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
}

public static class RpcWire
{
    public const int MaximumLineCharacters = 524288;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Set here: options lock on first use. The reflection resolver stays as the fallback for unlisted types.
        TypeInfoResolver = JsonTypeInfoResolver.Combine(RpcJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };

    // Limit before deserialization; a read deadline is enforced by the parent process.
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var next = new char[1];
        while (true)
        {
            int count = await reader.ReadAsync(next.AsMemory(), token);
            if (count == 0)
                return text.Length == 0 ? null : throw new InvalidDataException("Incomplete RPC record.");
            if (next[0] == '\n') return text.ToString();
            if (next[0] == '\r') continue;
            if (text.Length >= MaximumLineCharacters) throw new InvalidDataException("RPC record exceeds the size limit.");
            text.Append(next[0]);
        }
    }
}
