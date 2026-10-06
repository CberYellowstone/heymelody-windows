using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 0) return 2;
        await using var service = new NativeHeadsetService();
        try
        {
            long lastId = 0;
            while (await RpcWire.ReadLineAsync(Console.In, CancellationToken.None) is { } line)
            {
                var request = JsonSerializer.Deserialize<RpcRequest>(line, RpcWire.Json) ?? throw new InvalidDataException();
                if (request.SchemaVersion != 1 || request.Id <= lastId || request.Operation.Length > 32)
                    throw new InvalidDataException();
                lastId = request.Id;
                var response = new RpcResponse { Id = request.Id };
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(request.Operation == "apply" ? 105 : 65));
                try
                {
                    object data = request.Operation switch
                    {
                        "enumerate" => await service.EnumerateAsync(budget.Token),
                        "connect" => await service.ConnectAsync(request.Identity ?? throw new InvalidDataException(), budget.Token),
                        "refresh" => await service.RefreshAsync(budget.Token),
                        "diagnostics" => service.Diagnostics(),
                        "apply" => await service.ApplyAsync(request.Action.Deserialize<UiAction>(RpcWire.Json) ?? throw new InvalidDataException(), budget.Token),
                        "disconnect" => await Disconnect(),
                        _ => throw new InvalidDataException()
                    };
                    response.Success = true; response.Data = JsonSerializer.SerializeToElement(data, RpcWire.Json);
                    async Task<bool> Disconnect() { await service.DisconnectAsync(budget.Token); return true; }
                }
                catch (Exception)
                {
                    response.Error = "The isolated Bluetooth operation could not be completed.";
                }
                string record = JsonSerializer.Serialize(response, RpcWire.Json);
                if (record.Length > RpcWire.MaximumLineCharacters) throw new InvalidDataException();
                Console.WriteLine(record); await Console.Out.FlushAsync();
            }
            return 0;
        }
        catch (Exception) { return 1; }
    }
}
