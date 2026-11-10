using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Private loopback Streamable HTTP MCP endpoint. It exports ONLY the tool schema supplied by
// Fusion; calls wait for the controller. No filesystem, process, resource or shell endpoints.
internal sealed class AiHarnessMcpBridge : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly JsonArray tools;
    private readonly Func<string, JsonObject, Task<string>> call;
    private readonly SemaphoreSlim clients = new(8, 8);
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string Url { get; private set; } = "";
    internal AiHarnessMcpBridge(JsonArray definitions, Func<string, JsonObject, Task<string>> call)
    {
        tools = new JsonArray(definitions.OfType<JsonObject>().Select(x => (JsonNode)new JsonObject
        {
            ["name"] = x["function"]?["name"]?.DeepClone(), ["description"] = x["function"]?["description"]?.DeepClone(),
            ["inputSchema"] = x["function"]?["parameters"]?.DeepClone()
        }).ToArray());
        this.call = call;
        listener.Start(8);
        Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/fusion-mcp";
        _ = ListenAsync();
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                if (!clients.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            try
            {
                var stream = client.GetStream();
                var header = new List<byte>(1024); var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) != 1) return;
                    header.Add(one[0]);
                    if (header.Count > 16384) return;
                    int n = header.Count;
                    if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(x => x.Length > 0))
                {
                    int colon = line.IndexOf(':'); if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) return;
                }
                if (lines[0] != "POST /fusion-mcp HTTP/1.1") { await SendAsync(stream, 405, null, timeout.Token).ConfigureAwait(false); return; }
                if (!headers.TryGetValue("Authorization", out string? auth) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth), Encoding.UTF8.GetBytes("Bearer " + Secret)) || headers.ContainsKey("Origin"))
                { await SendAsync(stream, 403, null, timeout.Token).ConfigureAwait(false); return; }
                if (headers.ContainsKey("Transfer-Encoding") || !headers.TryGetValue("Content-Length", out string? lengthText) || !int.TryParse(lengthText, out int length) || length is < 1 or > AiConversationClient.MaxRequestBytes)
                { await SendAsync(stream, 413, null, timeout.Token).ConfigureAwait(false); return; }
                var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                var request = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 16 })?.AsObject() ?? throw new JsonException();
                JsonNode? id = request["id"]?.DeepClone();
                string method = request["method"]?.GetValue<string>() ?? "";
                if (id is null) { await SendAsync(stream, 202, null, timeout.Token).ConfigureAwait(false); return; }
                JsonObject response;
                if (method == "initialize")
                    response = Result(id, new JsonObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "Fusion controlled tools", ["version"] = "1.1.140" } });
                else if (method == "ping") response = Result(id, new JsonObject());
                else if (method == "tools/list") response = Result(id, new JsonObject { ["tools"] = tools.DeepClone() });
                else if (method == "tools/call")
                {
                    string name = request["params"]?["name"]?.GetValue<string>() ?? "";
                    if (!tools.OfType<JsonObject>().Any(x => x["name"]?.GetValue<string>() == name))
                        response = Error(id, "Unknown Fusion tool");
                    else
                    {
                        string value = await call(name, request["params"]?["arguments"]?.AsObject() ?? new JsonObject()).WaitAsync(timeout.Token).ConfigureAwait(false);
                        response = Result(id, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value }), ["isError"] = false });
                    }
                }
                else response = Error(id, "Unsupported MCP method");
                await SendAsync(stream, 200, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException or OperationCanceledException or AiToolsException) { }
            finally { clients.Release(); }
        }
    }
    private static JsonObject Result(JsonNode id, JsonObject value) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = value };
    private static JsonObject Error(JsonNode id, string text) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = text } };
    private static async Task SendAsync(NetworkStream stream, int status, JsonObject? data, CancellationToken token)
    {
        byte[] body = data is null ? [] : JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false); if (body.Length > 0) await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
    public void Dispose() { lifetime.Cancel(); listener.Stop(); }
}
