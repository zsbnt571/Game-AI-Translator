using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// A credential boundary, not a sandbox: the native process receives a scoped local
// capability, never the real API key. The capability can spend on this one configured
// model/endpoint while the session is alive. No arbitrary URLs, redirects or headers.
internal sealed class AiOpenCodeModelProxy : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient upstream;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim clients = new(12, 12);
    private readonly AiOpenCodeApiRoute route;
    private readonly string apiKey;
    private readonly Action<string>? failed;
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string BaseUrl { get; }
    internal const int MaxBodyBytes = 16 * 1024 * 1024;

    internal AiOpenCodeModelProxy(Uri endpoint, string apiKey, string model, bool responses, HttpMessageHandler? handler = null)
        : this(new AiOpenCodeApiRoute(responses ? "responses" : "chat", responses ? "@ai-sdk/openai" : "@ai-sdk/openai-compatible",
            new Uri(endpoint.AbsoluteUri[..^(responses ? "/responses".Length : "/chat/completions".Length)]), model), apiKey, handler) { }

    internal AiOpenCodeModelProxy(AiOpenCodeApiRoute route, string apiKey, HttpMessageHandler? handler = null, Action<string>? failed = null)
    {
        if (route.BaseUri.Scheme is not ("http" or "https") || route.BaseUri.UserInfo.Length != 0 || route.BaseUri.Query.Length != 0 || route.BaseUri.Fragment.Length != 0 || !AiAgentOptions.Clean(apiKey, 8192))
            throw new AiToolsException("harness_endpoint");
        this.route = route; this.apiKey = apiKey; this.failed = failed;
        upstream = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        listener.Start(12);
        BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/v1";
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
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = new NetworkIdleTimeout(lifetime.Token))
        {
            try
            {
                var stream = client.GetStream();
                var header = new List<byte>(1024); var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) != 1) return;
                    timeout.Progress();
                    header.Add(one[0]); if (header.Count > 16384) return;
                    int n = header.Count;
                    if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1).Where(x => x.Length > 0))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) return;
                }
                string[] requestLine = lines[0].Split(' ');
                string? resource = requestLine.Length == 3 && requestLine[0] == "POST" && requestLine[2] == "HTTP/1.1" ? route.Resource(requestLine[1]) : null;
                if (resource is null) { await ErrorAsync(stream, 404, timeout.Token); return; }
                if (headers.ContainsKey("Origin") || !headers.TryGetValue(route.AuthHeader, out var auth) ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth), Encoding.UTF8.GetBytes(route.Credential(Secret))))
                { await ErrorAsync(stream, 403, timeout.Token); return; }
                if (headers.ContainsKey("Transfer-Encoding") || !headers.TryGetValue("Content-Length", out var lengthText) ||
                    !int.TryParse(lengthText, out int length) || length is < 1 or > MaxBodyBytes)
                { await ErrorAsync(stream, 413, timeout.Token); return; }
                byte[] body = new byte[length];
                int received = 0;
                while (received < body.Length)
                {
                    int count = await stream.ReadAsync(body.AsMemory(received), timeout.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    received += count; timeout.Progress();
                }
                if (!ValidBody(body, route.ModelId, route.Protocol)) { await ErrorAsync(stream, 400, timeout.Token); return; }
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route.BaseUri.AbsoluteUri.TrimEnd('/') + resource));
                request.Content = new ProgressContent(body, timeout);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (apiKey.Length > 0) request.Headers.TryAddWithoutValidation(route.AuthHeader, route.Credential(apiKey));
                if (route.Protocol == "anthropic")
                    foreach (string name in new[] { "anthropic-version", "anthropic-beta" })
                        if (headers.TryGetValue(name, out var value) && value.Length <= 4096 && !value.Any(char.IsControl)) request.Headers.TryAddWithoutValidation(name, value);
                using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                timeout.Progress();
                if ((int)response.StatusCode is >= 300 and < 400) { await ErrorAsync(stream, 502, timeout.Token); return; }
                string contentType = response.Content.Headers.ContentType?.MediaType == "text/event-stream" ? "text/event-stream" : "application/json";
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                byte[]? validated = null;
                if (route.Protocol == "gemini" && response.IsSuccessStatusCode)
                {
                    // The pinned @ai-sdk/google 3.0.73 silently drops an HTTP-200 SSE
                    // {error:...} event. Buffer this provider response only, before any
                    // tool-call bytes reach the SDK. Successful bytes are unchanged.
                    // This trades Gemini token streaming for fail-closed tool dispatch.
                    try { validated = await ReadBoundedAsync(source, MaxBodyBytes, timeout.Token, timeout.Progress).ConfigureAwait(false); }
                    catch (AiToolsException)
                    { failed?.Invoke("response_limit"); await ErrorAsync(stream, 502, timeout.Token).ConfigureAwait(false); return; }
                    if (!ValidGeminiResponse(validated, contentType == "text/event-stream"))
                    { failed?.Invoke("harness_provider_stream_error"); await ErrorAsync(stream, 502, timeout.Token).ConfigureAwait(false); return; }
                }
                // Close-delimited streaming preserves native SDK SSE backpressure/cancellation.
                byte[] responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)response.StatusCode} Response\r\nContent-Type: {contentType}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
                await stream.WriteAsync(responseHeader, timeout.Token).ConfigureAwait(false);
                timeout.Progress();
                if (validated is not null) await WritePayloadAsync(validated, stream, timeout, timeout.Token).ConfigureAwait(false);
                else await CopyResponseAsync(source, stream, timeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException or OperationCanceledException or HttpRequestException) { }
            finally { clients.Release(); }
        }
    }

    // One reusable timer bounds lack of I/O progress, not the total lifetime of a
    // healthy response. The caller's cancellation still interrupts every read/write.
    internal sealed class NetworkIdleTimeout : IDisposable
    {
        private readonly CancellationTokenSource timeout;
        private readonly CancellationTokenRegistration cancellation;
        private readonly TimeSpan idle;
        internal NetworkIdleTimeout(CancellationToken token, TimeSpan? idleTimeout = null, TimeProvider? clock = null)
        {
            idle = idleTimeout ?? TimeSpan.FromMinutes(15);
            if (idle <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            timeout = new CancellationTokenSource(idle, clock ?? TimeProvider.System);
            cancellation = token.Register(static value => ((CancellationTokenSource)value!).Cancel(), timeout);
        }
        internal CancellationToken Token => timeout.Token;
        internal void Progress() { if (!timeout.IsCancellationRequested) timeout.CancelAfter(idle); }
        public void Dispose() { cancellation.Dispose(); timeout.Dispose(); }
    }

    internal static async Task CopyResponseAsync(Stream source, Stream destination, NetworkIdleTimeout idle)
    {
        byte[] buffer = new byte[16384];
        while (true)
        {
            int count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (count == 0) return;
            idle.Progress();
            await destination.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
            idle.Progress();
        }
    }

    internal static async Task WritePayloadAsync(byte[] bytes, Stream destination, NetworkIdleTimeout idle, CancellationToken token)
    {
        for (int offset = 0; offset < bytes.Length;)
        {
            int count = Math.Min(16384, bytes.Length - offset);
            await destination.WriteAsync(bytes.AsMemory(offset, count), token).ConfigureAwait(false);
            idle.Progress(); offset += count;
        }
    }

    // HttpClient can spend time uploading an attachment before response headers
    // arrive. Completed upload chunks count as progress just like response chunks.
    internal sealed class ProgressContent(byte[] bytes, NetworkIdleTimeout idle) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => WritePayloadAsync(bytes, stream, idle, idle.Token);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
            => WritePayloadAsync(bytes, stream, idle, token);
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    }

    internal static bool ValidBody(byte[] body, string model, string protocol = "chat")
    {
        try
        {
            var json = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 96 }) as JsonObject;
            return protocol == "gemini" ? json?["contents"] is JsonArray && json["model"] is null : json?["model"]?.GetValue<string>() == model;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken token)
        => await ReadBoundedAsync(source, limit, token, null).ConfigureAwait(false);

    internal static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken token, Action? progress)
    {
        using var memory = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            int count = await source.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) return memory.ToArray();
            progress?.Invoke();
            if (memory.Length + count > limit) throw new AiToolsException("response_limit");
            memory.Write(buffer, 0, count);
        }
    }

    internal static bool ValidGeminiResponse(byte[] body, bool streaming)
    {
        bool finished = false, any = false;
        bool Chunk(string json)
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 96 }) is not JsonObject value || value["error"] is not null || value["promptFeedback"]?["blockReason"] is not null) return false;
            any = true;
            if ((value["candidates"] as JsonArray ?? []).OfType<JsonObject>().Any(c => c["finishReason"] is JsonValue reason && reason.TryGetValue<string>(out var text) && text.Length > 0)) finished = true;
            return true;
        }
        try
        {
            string text = new UTF8Encoding(false, true).GetString(body);
            if (!streaming) return Chunk(text) && finished;
            using var reader = new StringReader(text); var data = new StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0) { if (!Chunk(data.ToString())) return false; data.Clear(); }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                { if (data.Length > 0) data.Append('\n'); data.Append(line.AsSpan(5).TrimStart()); }
            }
            // Incomplete trailing SSE records must not manufacture a successful turn.
            return data.Length == 0 && any && finished;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException) { return false; }
    }
    private static Task ErrorAsync(NetworkStream stream, int code, CancellationToken token) => stream.WriteAsync(
        Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Response\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();
    public void Dispose() { lifetime.Cancel(); listener.Stop(); upstream.Dispose(); }
}
