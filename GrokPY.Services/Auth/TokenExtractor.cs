using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.Json;

namespace GrokPY.Services.Auth;

/// <summary>
/// Bắt và trích xuất token từ network requests của Chrome
/// Tương đương A_workflow_get_token.py trong Python gốc
/// Lắng nghe: submitBatchLog (sessionId), createProject (projectId), _next/data (accessToken)
/// </summary>
public class TokenExtractor
{
    private readonly ILogger _logger;
    private readonly GoogleTokens _tokens = new();
    private readonly object _lock = new();

    public TokenExtractor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gắn vào page để lắng nghe network requests
    /// </summary>
    public void AttachToPage(IPage page)
    {
        // Bắt response để lấy token từ body
        page.Response += async (_, e) =>
        {
            try { await OnResponseAsync(e.Response); }
            catch { }
        };

        // Bắt request để lấy header (x-statsig-id, authorization)
        page.Request += (_, e) =>
        {
            try { OnRequest(e.Request); }
            catch { }
        };

        _logger.LogDebug("TokenExtractor đã gắn vào page");
    }

    // ─── Request handler ──────────────────────────────────────

    private void OnRequest(IRequest request)
    {
        var url = request.Url;
        var headers = request.Headers;

        // Lấy Authorization header → access_token
        if (headers.TryGetValue("authorization", out var auth) &&
            auth.StartsWith("Bearer "))
        {
            var token = auth.Replace("Bearer ", "").Trim();
            if (!string.IsNullOrEmpty(token))
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_tokens.AccessToken))
                    {
                        _tokens.AccessToken = token;
                        _logger.LogInformation("✅ Bắt được access_token từ request header");
                    }
                }
            }
        }
    }

    // ─── Response handler ─────────────────────────────────────

    private async Task OnResponseAsync(IResponse response)
    {
        var url = response.Url;
        if (!IsRelevantUrl(url)) return;

        try
        {
            var body = await response.TextAsync();
            if (string.IsNullOrWhiteSpace(body)) return;

            // submitBatchLog → lấy sessionId
            if (url.Contains("submitBatchLog") || url.Contains("batchLog"))
            {
                ExtractSessionId(body);
                return;
            }

            // createProject → lấy projectId
            if (url.Contains("createProject") || url.Contains("projects"))
            {
                ExtractProjectId(body, url);
                return;
            }

            // _next/data hoặc token endpoint → lấy accessToken
            if (url.Contains("_next/data") || url.Contains("token") ||
                url.Contains("userinfo"))
            {
                ExtractAccessToken(body);
            }
        }
        catch { }
    }

    // ─── Extractors ───────────────────────────────────────────

    private void ExtractSessionId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Tìm sessionId trong response
            if (TryGetNestedString(root, out var sessionId,
                "sessionId", "session_id", "sid"))
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_tokens.SessionId))
                    {
                        _tokens.SessionId = sessionId!;
                        _logger.LogInformation("✅ Bắt được sessionId: {Id}",
                            sessionId!.Length > 8 ? sessionId[..8] + "..." : sessionId);
                    }
                }
            }
        }
        catch { }
    }

    private void ExtractProjectId(string body, string url)
    {
        try
        {
            // Thử lấy projectId từ URL path trước
            // Pattern: /projects/XXXXXXXX
            var match = System.Text.RegularExpressions.Regex.Match(
                url, @"/projects/([a-zA-Z0-9_-]+)");
            if (match.Success)
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_tokens.ProjectId))
                    {
                        _tokens.ProjectId = match.Groups[1].Value;
                        _logger.LogInformation("✅ Bắt được projectId từ URL: {Id}",
                            _tokens.ProjectId);
                        return;
                    }
                }
            }

            // Thử từ response body
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (TryGetNestedString(root, out var projectId,
                "projectId", "project_id", "id", "name"))
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_tokens.ProjectId))
                    {
                        _tokens.ProjectId = projectId!;
                        _logger.LogInformation("✅ Bắt được projectId từ body: {Id}",
                            projectId);
                    }
                }
            }
        }
        catch { }
    }

    private void ExtractAccessToken(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (TryGetNestedString(root, out var token,
                "access_token", "accessToken", "token", "idToken"))
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_tokens.AccessToken) &&
                        token!.Length > 20) // Token thật thường dài
                    {
                        _tokens.AccessToken = token;
                        _logger.LogInformation("✅ Bắt được access_token từ response body");
                    }
                }
            }
        }
        catch { }
    }

    // ─── Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra URL có liên quan tới auth không
    /// </summary>
    private static bool IsRelevantUrl(string url)
    {
        return url.Contains("googleapis.com") ||
               url.Contains("labs.google") ||
               url.Contains("accounts.google") ||
               url.Contains("submitBatchLog") ||
               url.Contains("createProject") ||
               url.Contains("_next/data") ||
               url.Contains("token") ||
               url.Contains("projects");
    }

    /// <summary>
    /// Tìm string value trong JSON bằng nhiều key khác nhau
    /// </summary>
    private static bool TryGetNestedString(
        JsonElement root, out string? value, params string[] keys)
    {
        // Tìm ở root level
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                if (!string.IsNullOrEmpty(value)) return true;
            }
        }

        // Tìm recursive trong object
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in root.EnumerateObject())
            {
                if (TryGetNestedString(child.Value, out value, keys))
                    return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Lấy tokens đã bắt được
    /// </summary>
    public GoogleTokens GetTokens()
    {
        lock (_lock)
        {
            return new GoogleTokens
            {
                AccessToken = _tokens.AccessToken,
                SessionId = _tokens.SessionId,
                ProjectId = _tokens.ProjectId,
                Cookie = _tokens.Cookie
            };
        }
    }

    /// <summary>
    /// Set cookie từ bên ngoài (sau khi Chrome đã login)
    /// </summary>
    public void SetCookie(string cookie)
    {
        lock (_lock) { _tokens.Cookie = cookie; }
    }
}
