using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Chrome;

/// <summary>
/// Raw CDP (Chrome DevTools Protocol) WebSocket session
/// Dùng để giao tiếp trực tiếp với Chrome qua WebSocket
/// Tương đương cách grok_chrome_manager.py dùng CDP trong Python gốc
/// Thường dùng khi PuppeteerSharp không expose đủ CDP methods
/// </summary>
public class CdpSession : IAsyncDisposable
{
    private readonly ILogger<CdpSession> _logger;
    private ClientWebSocket? _ws;
    private int _msgId = 1;
    private CancellationTokenSource _cts = new();

    // Chờ response theo id
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>>
        _pending = new();

    // Event handlers theo method name
    private readonly ConcurrentDictionary<string, List<Action<JsonNode?>>>
        _eventHandlers = new();

    private Task? _receiveLoop;

    public CdpSession(ILogger<CdpSession> logger)
    {
        _logger = logger;
    }

    // ─── Connect ──────────────────────────────────────────────

    /// <summary>
    /// Kết nối tới Chrome CDP endpoint
    /// </summary>
    /// <param name="wsUrl">WebSocket URL từ Chrome
    /// (e.g. ws://localhost:9223/devtools/page/xxx)</param>
    public async Task ConnectAsync(string wsUrl)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        _logger.LogInformation("CDP connected: {Url}", wsUrl);

        // Bắt đầu vòng lặp nhận message
        _receiveLoop = ReceiveLoopAsync();
    }

    /// <summary>
    /// Lấy WebSocket URL của page đầu tiên từ Chrome
    /// Chrome phải chạy với --remote-debugging-port=PORT
    /// </summary>
    public static async Task<string?> GetPageWsUrlAsync(string host, int port)
    {
        try
        {
            using var http = new HttpClient();
            var json = await http.GetStringAsync(
                $"http://{host}:{port}/json");
            var pages = JsonNode.Parse(json)?.AsArray();
            if (pages == null || pages.Count == 0) return null;

            // Lấy page đầu tiên (type = "page")
            foreach (var page in pages)
            {
                var type  = page?["type"]?.GetValue<string>();
                var wsUrl = page?["webSocketDebuggerUrl"]?.GetValue<string>();
                if (type == "page" && !string.IsNullOrEmpty(wsUrl))
                    return wsUrl;
            }
            return null;
        }
        catch (Exception ex)
        {
            throw new Exception($"Không lấy được CDP URL từ Chrome: {ex.Message}", ex);
        }
    }

    // ─── Send Command ─────────────────────────────────────────

    /// <summary>
    /// Gửi CDP command và chờ response
    /// </summary>
    /// <param name="method">CDP method (e.g. "Page.navigate")</param>
    /// <param name="parameters">Parameters dạng object</param>
    /// <param name="timeoutMs">Timeout</param>
    public async Task<JsonNode?> SendAsync(
        string method,
        object? parameters = null,
        int timeoutMs = 30000)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("CDP chưa kết nối.");

        var id  = Interlocked.Increment(ref _msgId);
        var tcs = new TaskCompletionSource<JsonNode?>();
        _pending[id] = tcs;

        // Build message
        var msg = new JsonObject
        {
            ["id"]     = id,
            ["method"] = method
        };

        if (parameters != null)
        {
            var paramsJson = JsonSerializer.Serialize(parameters);
            msg["params"] = JsonNode.Parse(paramsJson);
        }

        var msgBytes = Encoding.UTF8.GetBytes(msg.ToJsonString());

        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(msgBytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: _cts.Token);

            _logger.LogDebug("CDP → {Method} (id={Id})", method, id);

            // Chờ response với timeout
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(
                    $"CDP timeout: {method}")));

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            _logger.LogError(ex, "CDP SendAsync lỗi: {Method}", method);
            throw;
        }
    }

    // ─── Common CDP Commands ──────────────────────────────────

    /// <summary>
    /// Navigate page tới URL
    /// </summary>
    public async Task NavigateAsync(string url, int timeoutMs = 30000)
    {
        await SendAsync("Page.navigate", new { url }, timeoutMs);
        _logger.LogDebug("CDP Navigate: {Url}", url);
    }

    /// <summary>
    /// Thêm script chạy trước khi page load (stealth script)
    /// </summary>
    public async Task AddScriptToEvaluateOnNewDocumentAsync(string source)
    {
        await SendAsync("Page.addScriptToEvaluateOnNewDocument",
            new { source });
    }

    /// <summary>
    /// Chạy JavaScript trong page context
    /// </summary>
    public async Task<string?> EvaluateAsync(string expression)
    {
        var result = await SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue    = true,
            awaitPromise     = true,
            userGesture      = true
        });

        return result?["result"]?["value"]?.ToString();
    }

    /// <summary>
    /// Lấy tất cả cookies
    /// </summary>
    public async Task<JsonArray?> GetCookiesAsync()
    {
        var result = await SendAsync("Network.getCookies");
        return result?["cookies"]?.AsArray();
    }

    /// <summary>
    /// Set User-Agent
    /// </summary>
    public async Task SetUserAgentAsync(string userAgent)
    {
        await SendAsync("Network.setUserAgentOverride", new { userAgent });
    }

    /// <summary>
    /// Bật Network domain để intercept requests
    /// </summary>
    public async Task EnableNetworkAsync()
    {
        await SendAsync("Network.enable");
    }

    /// <summary>
    /// Bật Page domain
    /// </summary>
    public async Task EnablePageAsync()
    {
        await SendAsync("Page.enable");
    }

    // ─── Event Handling ───────────────────────────────────────

    /// <summary>
    /// Đăng ký handler cho CDP event
    /// </summary>
    public void On(string eventName, Action<JsonNode?> handler)
    {
        _eventHandlers.AddOrUpdate(
            eventName,
            _ => new List<Action<JsonNode?>> { handler },
            (_, list) => { list.Add(handler); return list; });
    }

    /// <summary>
    /// Chờ CDP event xảy ra
    /// </summary>
    public async Task<JsonNode?> WaitForEventAsync(
        string eventName, int timeoutMs = 30000)
    {
        var tcs = new TaskCompletionSource<JsonNode?>();

        void Handler(JsonNode? data) => tcs.TrySetResult(data);
        On(eventName, Handler);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        timeoutCts.Token.Register(() =>
            tcs.TrySetException(
                new TimeoutException($"Timeout chờ event: {eventName}")));

        return await tcs.Task;
    }

    // ─── Receive Loop ─────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];

        while (_ws != null && _ws.State == WebSocketState.Open)
        {
            try
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("CDP WebSocket closed");
                        return;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                ProcessMessage(sb.ToString());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CDP receive lỗi");
                break;
            }
        }
    }

    private void ProcessMessage(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            if (node == null) return;

            // Response cho command (có id)
            var id = node["id"]?.GetValue<int>();
            if (id.HasValue && _pending.TryRemove(id.Value, out var tcs))
            {
                var error = node["error"];
                if (error != null)
                    tcs.TrySetException(new Exception(
                        error["message"]?.GetValue<string>() ?? "CDP error"));
                else
                    tcs.TrySetResult(node["result"]);
                return;
            }

            // Event (có method)
            var method = node["method"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(method) &&
                _eventHandlers.TryGetValue(method, out var handlers))
            {
                var @params = node["params"];
                foreach (var handler in handlers.ToList())
                {
                    try { handler(@params); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CDP event handler lỗi: {M}", method);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse CDP message lỗi");
        }
    }

    // ─── Dispose ──────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        // Huỷ tất cả pending tasks
        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; }
            catch { }
        }

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Done",
                        CancellationToken.None);
                _ws.Dispose();
            }
            catch { }
        }

        _cts.Dispose();
        _logger.LogDebug("CdpSession disposed");
    }
}
