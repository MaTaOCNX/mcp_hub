// McpHub: one stdio MCP server for Claude Desktop that fans out to several
// remote MCP servers over Streamable HTTP. Tools are exposed as "<prefix>_<tool>".
//
// Usage: McpHub.exe prefix=url [prefix=url ...]
//   e.g. McpHub.exe pve=http://192.168.1.37:3001/mcp ssh=http://192.168.1.37:3002/mcp
//
// stdout carries JSON-RPC only; all diagnostics go to stderr (Claude's MCP log).

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpHub;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var upstreams = new List<Upstream>();
        foreach (var arg in args)
        {
            var eq = arg.IndexOf('=');
            var prefix = eq > 0 ? arg[..eq] : "";
            if (eq <= 0
                || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                || !Uri.TryCreate(arg[(eq + 1)..], UriKind.Absolute, out var url))
            {
                Log.Write($"Invalid argument '{arg}'. Expected prefix=url (prefix: letters, digits, '-').");
                return 2;
            }
            if (upstreams.Exists(u => u.Prefix == prefix))
            {
                Log.Write($"Duplicate prefix '{prefix}'.");
                return 2;
            }
            upstreams.Add(new Upstream(prefix, url));
        }

        if (upstreams.Count == 0)
        {
            Log.Write("Usage: McpHub prefix=url [prefix=url ...]");
            return 2;
        }

        await new Hub(upstreams).RunAsync();
        return 0;
    }
}

/// <summary>The stdio side: talks to Claude Desktop and routes to upstreams.</summary>
internal sealed class Hub
{
    private readonly List<Upstream> _upstreams;
    private readonly Stream _stdout = Console.OpenStandardOutput();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    private readonly ConcurrentDictionary<string, byte> _listed = new();
    private volatile bool _clientReady;
    private volatile bool _listedOnce;
    private Task _warmup = Task.CompletedTask;

    public Hub(List<Upstream> upstreams)
    {
        _upstreams = upstreams;
        foreach (var u in upstreams) u.ForwardNotification = OnUpstreamNotificationAsync;
    }

    public async Task RunAsync()
    {
        // Connect to all upstreams in the background right away.
        _warmup = Task.WhenAll(_upstreams.Select(u => u.TryInitializeAsync(TimeSpan.FromSeconds(10))));
        _ = WatchUnavailableAsync();

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            switch (Json.TryParse(line))
            {
                case JsonObject message:
                    _ = HandleAsync(message);
                    break;
                case JsonArray batch:
                    foreach (var item in batch)
                        if (item is JsonObject m) _ = HandleAsync((JsonObject)m.DeepClone());
                    break;
                default:
                    Log.Write("Ignoring a line that is not JSON-RPC.");
                    break;
            }
        }

        // Claude closed stdin: end upstream sessions and exit.
        await Task.WhenAll(_upstreams.Select(u => u.CloseAsync()));
    }

    private async Task HandleAsync(JsonObject message)
    {
        var method = Json.Str(message["method"]);
        var id = message["id"];
        if (method is null) return; // a response; we never send requests to the client
        var parameters = message["params"] as JsonObject;

        if (id is null)
        {
            HandleNotification(method, parameters);
            return;
        }

        var key = Json.Key(id);
        using var cts = new CancellationTokenSource();
        _inflight[key] = cts;
        try
        {
            var result = await DispatchAsync(method, parameters, cts.Token);
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the client: the spec says not to reply.
        }
        catch (RpcException e)
        {
            await SendErrorAsync(id, e.Code, e.Message, e.RpcData);
        }
        catch (Exception e)
        {
            Log.Write($"{method} failed: {e}");
            await SendErrorAsync(id, -32603, e.Message, null);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private void HandleNotification(string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "notifications/initialized":
                _clientReady = true;
                break;
            case "notifications/cancelled":
                if (parameters?["requestId"] is { } requestId && _inflight.TryGetValue(Json.Key(requestId), out var cts))
                {
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                }
                break;
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject? parameters, CancellationToken ct) => method switch
    {
        "initialize" => await InitializeAsync(parameters),
        "ping" => new JsonObject(),
        "tools/list" => new JsonObject { ["tools"] = await ListToolsAsync(ct) },
        "tools/call" => await CallToolAsync(parameters, ct),
        _ => throw new RpcException(-32601, $"Method not found: {method}"),
    };

    private async Task<JsonNode> InitializeAsync(JsonObject? parameters)
    {
        // Give upstreams a moment so their instructions can be included.
        await Task.WhenAny(_warmup, Task.Delay(TimeSpan.FromSeconds(5)));

        var instructions = string.Join("\n\n", _upstreams
            .Where(u => !string.IsNullOrWhiteSpace(u.Instructions))
            .Select(u => $"Tools prefixed \"{u.Prefix}_\":\n{u.Instructions}"));

        var result = new JsonObject
        {
            ["protocolVersion"] = Json.Str(parameters?["protocolVersion"]) ?? Upstream.ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
            ["serverInfo"] = new JsonObject { ["name"] = "mcp-hub", ["version"] = "1.0.0" },
        };
        if (instructions.Length > 0) result["instructions"] = instructions;
        return result;
    }

    private async Task<JsonArray> ListToolsAsync(CancellationToken ct)
    {
        async Task<(Upstream Upstream, List<JsonObject>? Tools)> FetchAsync(Upstream u)
        {
            try
            {
                return (u, await u.ListToolsAsync(TimeSpan.FromSeconds(15), ct));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Log.Write($"[{u.Prefix}] unavailable, leaving its tools out: {e.Message}");
                return (u, null);
            }
        }

        var perUpstream = await Task.WhenAll(_upstreams.Select(FetchAsync));
        var tools = new JsonArray();
        foreach (var (upstream, list) in perUpstream)
        {
            if (list is null)
            {
                _listed.TryRemove(upstream.Prefix, out _);
                continue;
            }
            _listed[upstream.Prefix] = 0;
            foreach (var tool in list)
            {
                tool["name"] = $"{upstream.Prefix}_{Json.Str(tool["name"])}";
                tools.Add(tool);
            }
        }
        _listedOnce = true;
        return tools;
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? parameters, CancellationToken ct)
    {
        var name = Json.Str(parameters?["name"]) ?? throw new RpcException(-32602, "Missing tool name.");
        var sep = name.IndexOf('_');
        var upstream = sep > 0 ? _upstreams.Find(u => u.Prefix == name[..sep]) : null;
        if (upstream is null) throw new RpcException(-32602, $"Unknown tool: {name}");

        var forwarded = (JsonObject)parameters!.DeepClone();
        forwarded["name"] = name[(sep + 1)..];
        try
        {
            return await upstream.RequestAsync("tools/call", forwarded, ct);
        }
        catch (HttpRequestException e)
        {
            // Report as a tool error so the model sees what happened.
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"MCP server '{upstream.Prefix}' at {upstream.Url} is unreachable: {e.Message}",
                }),
                ["isError"] = true,
            };
        }
    }

    private Task OnUpstreamNotificationAsync(Upstream source, JsonObject notification)
    {
        switch (Json.Str(notification["method"]))
        {
            case "notifications/tools/list_changed":
                return _clientReady ? SendAsync(ListChanged()) : Task.CompletedTask;
            case "notifications/progress":
            case "notifications/message":
                return SendAsync(notification);
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>If an upstream was down when tools were listed, tell the client once it comes back.</summary>
    private async Task WatchUnavailableAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                if (!_clientReady || !_listedOnce) continue;
                foreach (var u in _upstreams.Where(u => !_listed.ContainsKey(u.Prefix)).ToList())
                {
                    if (!await u.TryInitializeAsync(TimeSpan.FromSeconds(10))) continue;
                    Log.Write($"[{u.Prefix}] is back; asking the client to reload tools.");
                    _listed[u.Prefix] = 0;
                    await SendAsync(ListChanged());
                }
            }
            catch (Exception e)
            {
                Log.Write($"Watcher error: {e.Message}");
            }
        }
    }

    private static JsonObject ListChanged() =>
        new() { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" };

    private async Task SendAsync(JsonObject message)
    {
        var bytes = Json.ToUtf8(message);
        await _writeLock.WaitAsync();
        try
        {
            await _stdout.WriteAsync(bytes);
            _stdout.WriteByte((byte)'\n');
            await _stdout.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Task SendErrorAsync(JsonNode id, int code, string message, JsonNode? data)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data.DeepClone();
        return SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = error });
    }
}

/// <summary>One remote MCP server reached over Streamable HTTP.</summary>
internal sealed class Upstream(string prefix, Uri url)
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false, // LAN servers: never route through a system proxy
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // long tool calls are fine; cancellation handles aborts
    };

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _ready;
    private string? _sessionId;
    private string? _negotiatedVersion;
    private long _nextId;

    public string Prefix { get; } = prefix;
    public Uri Url { get; } = url;
    public string? Instructions { get; private set; }
    public Func<Upstream, JsonObject, Task>? ForwardNotification { get; set; }

    public async Task<bool> TryInitializeAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await EnsureInitializedAsync(cts.Token);
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"[{Prefix}] cannot connect to {Url}: {e.Message}");
            return false;
        }
    }

    public async Task<List<JsonObject>> ListToolsAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var tools = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var parameters = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var result = await RequestAsync("tools/list", parameters, cts.Token);
            if (result["tools"] is JsonArray page)
                tools.AddRange(page.OfType<JsonObject>().Select(t => (JsonObject)t.DeepClone()));
            cursor = Json.Str(result["nextCursor"]);
        } while (cursor is not null);
        return tools;
    }

    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        try
        {
            return await SendRequestAsync(method, parameters, ct);
        }
        catch (SessionExpiredException)
        {
            // Server restarted or dropped our session (HTTP 404): reconnect and retry once.
            Log.Write($"[{Prefix}] session expired; reconnecting.");
            _ready = false;
            await EnsureInitializedAsync(ct);
            return await SendRequestAsync(method, parameters, ct);
        }
        catch (HttpRequestException)
        {
            _ready = false; // reconnect on next use; don't retry (the call may have run)
            throw;
        }
    }

    public async Task CloseAsync()
    {
        if (_sessionId is not { } sid) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(HttpMethod.Delete, Url);
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
            (await Http.SendAsync(request, cts.Token)).Dispose();
        }
        catch
        {
            // best effort
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready) return;
            _sessionId = null;
            _negotiatedVersion = null;

            var result = await SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "mcp-hub", ["version"] = "1.0.0" },
            }, ct);

            _negotiatedVersion = Json.Str(result["protocolVersion"]) ?? ProtocolVersion;
            Instructions = Json.Str(result["instructions"]);
            await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
            _ready = true;
            Log.Write($"[{Prefix}] connected to {Url} (protocol {_negotiatedVersion}).");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<JsonObject> SendRequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters.DeepClone();

        using var cancelRegistration = method == "initialize"
            ? default
            : ct.Register(() => _ = PostCancelAsync(id));
        using var httpRequest = BuildPost(request);
        using var response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotFound && _sessionId is not null)
            throw new SessionExpiredException();
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Shorten(text)}");
        }
        if (method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var sid))
            _sessionId = sid.FirstOrDefault();

        JsonObject? reply = null;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            await foreach (var message in ReadEventStreamAsync(response, ct))
            {
                reply = await HandleIncomingAsync(message, id, ct);
                if (reply is not null) break;
            }
        }
        else
        {
            List<JsonObject> messages = Json.TryParse(await response.Content.ReadAsStringAsync(ct)) switch
            {
                JsonArray batch => batch.OfType<JsonObject>().ToList(),
                JsonObject single => [single],
                _ => [],
            };
            foreach (var message in messages)
                reply ??= await HandleIncomingAsync(message, id, ct);
        }

        if (reply is null)
            throw new RpcException(-32603, $"[{Prefix}] ended the response without answering {method}.");
        if (reply["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : -32603;
            throw new RpcException(code, Json.Str(error["message"]) ?? "Upstream error", error["data"]?.DeepClone());
        }
        return reply["result"] is JsonObject result ? (JsonObject)result.DeepClone() : new JsonObject();
    }

    /// <summary>Returns the message if it is the reply to <paramref name="expectedId"/>; handles everything else.</summary>
    private async Task<JsonObject?> HandleIncomingAsync(JsonObject message, long expectedId, CancellationToken ct)
    {
        var method = Json.Str(message["method"]);
        var id = message["id"];

        if (method is null)
            return id is JsonValue v && v.TryGetValue<long>(out var got) && got == expectedId ? message : null;

        if (id is null)
        {
            if (ForwardNotification is { } forward) await forward(this, (JsonObject)message.DeepClone());
            return null;
        }

        // Server-to-client request (ping, sampling, elicitation, roots): answer so the server isn't left waiting.
        var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (method == "ping")
            answer["result"] = new JsonObject();
        else
            answer["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"mcp-hub does not support {method}" };
        await PostAsync(answer, ct);
        return null;
    }

    private async Task PostCancelAsync(long id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PostAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/cancelled",
                ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "Cancelled by client" },
            }, cts.Token);
        }
        catch
        {
            // best effort
        }
    }

    private async Task PostAsync(JsonObject message, CancellationToken ct)
    {
        using var request = BuildPost(message);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            Log.Write($"[{Prefix}] POST of {Json.Str(message["method"]) ?? "response"} returned HTTP {(int)response.StatusCode}.");
    }

    private HttpRequestMessage BuildPost(JsonObject message)
    {
        var content = new ByteArrayContent(Json.ToUtf8(message));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is { } sid) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
        if (_negotiatedVersion is { } version) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
        return request;
    }

    private static async IAsyncEnumerable<JsonObject> ReadEventStreamAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || line.Length == 0)
            {
                // Blank line ends an event.
                if (data.Length > 0)
                {
                    switch (Json.TryParse(data.ToString()))
                    {
                        case JsonObject message:
                            yield return message;
                            break;
                        case JsonArray batch:
                            foreach (var item in batch.OfType<JsonObject>().ToList())
                                yield return (JsonObject)item.DeepClone();
                            break;
                    }
                    data.Clear();
                }
                if (line is null) yield break;
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
            }
            // "event:", "id:", "retry:" and ":" comments are not needed here.
        }
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

internal static class Json
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Thai and other text readable
    };

    public static byte[] ToUtf8(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) node.WriteTo(writer);
        return buffer.WrittenSpan.ToArray();
    }

    public static string Key(JsonNode node) => Encoding.UTF8.GetString(ToUtf8(node));

    public static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}

internal static class Log
{
    public static void Write(string message) =>
        Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} [mcp-hub] {message}");
}

internal sealed class RpcException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? RpcData { get; } = data;
}

internal sealed class SessionExpiredException() : Exception("MCP session expired.");
