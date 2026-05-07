using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.Json;

namespace GrokPY.Services.Auth;

/// <summary>
/// Lấy x-statsig-id từ Grok.com bằng cách intercept network request
/// Tương đương A_workflow_get_token.py (phần statsig) trong Python gốc
/// Flow: Mở grok.com/imagine → Intercept request → Lấy x-statsig-id từ header
/// </summary>
public class StatsigDiscovery
{
    private readonly ILogger<StatsigDiscovery> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;

    // URL để trigger statsig request
    private const string GrokImagineUrl = "https://grok.com/imagine";
    private const string GrokHomeUrl    = "https://grok.com";

    // Cache file
    private string StatsigCachePath =>
        Path.Combine(_settings.DataDir, "statsig_cache.json");

    public StatsigDiscovery(
        ILogger<StatsigDiscovery> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger   = logger;
        _settings = settings;
        _chromeManager = chromeManager;
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Lấy x-statsig-id — dùng cache nếu còn hợp lệ, không thì fetch mới
    /// </summary>
    public async Task<StatsigResult> GetStatsigIdAsync(
        string? profileDir = null,
        Action<string>? onProgress = null,
        bool forceRefresh = false)
    {
        // Thử đọc cache trước
        if (!forceRefresh)
        {
            var cached = LoadFromCache();
            if (cached != null)
            {
                _logger.LogInformation("Dùng statsig từ cache: {Id}", Truncate(cached.StatsigId));
                return cached;
            }
        }

        // Fetch mới từ Chrome
        return await FetchFromChromeAsync(profileDir, onProgress);
    }

    // ─── Fetch từ Chrome ──────────────────────────────────────

    /// <summary>
    /// Mở Chrome, navigate grok.com, intercept request để lấy x-statsig-id
    /// </summary>
    private async Task<StatsigResult> FetchFromChromeAsync(
        string? profileDir,
        Action<string>? onProgress)
    {
        var chromePath = _chromeManager.FindChromePath();
        if (chromePath == null)
            return StatsigResult.Fail("Không tìm thấy Chrome");

        profileDir ??= _settings.GetGrokChromeProfileDir();

        var chrome = new StealthChrome(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StealthChrome>.Instance);

        try
        {
            onProgress?.Invoke("🚀 Đang mở Chrome cho Grok...");
            await chrome.LaunchAsync(chromePath, profileDir, headless: false);

            // Kết quả sẽ được điền bởi event handler
            var tcs = new TaskCompletionSource<string>();

            // Intercept request để bắt x-statsig-id
            chrome.Page!.Request += (_, e) =>
            {
                var headers = e.Request.Headers;
                if (headers.TryGetValue("x-statsig-id", out var statsigId)
                    && !string.IsNullOrEmpty(statsigId)
                    && !tcs.Task.IsCompleted)
                {
                    _logger.LogInformation("✅ Bắt được x-statsig-id từ request");
                    tcs.TrySetResult(statsigId);
                }
            };

            // Cũng thử bắt từ response header
            chrome.Page!.Response += (_, e) =>
            {
                if (tcs.Task.IsCompleted) return;
                var url = e.Response.Url;
                if (!url.Contains("grok.com")) return;

                // Một số request trả x-statsig-id trong response header
                // (PuppeteerSharp không expose response headers trực tiếp nên
                //  ta dùng cách khác bên dưới)
            };

            onProgress?.Invoke("🌐 Đang mở Grok...");
            await chrome.NavigateAsync(GrokHomeUrl, timeoutMs: 20000);
            await Task.Delay(2000);

            onProgress?.Invoke("🔍 Đang tìm x-statsig-id...");
            await chrome.NavigateAsync(GrokImagineUrl, timeoutMs: 20000);

            // Chờ tối đa 15 giây để bắt statsig
            var timeoutTask = Task.Delay(15000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            string? statsigId = null;

            if (completedTask == tcs.Task)
            {
                statsigId = await tcs.Task;
            }
            else
            {
                // Timeout — thử lấy qua JS injection
                onProgress?.Invoke("⚠️ Timeout, thử cách khác...");
                statsigId = await TryGetStatsigViaJsAsync(chrome);
            }

            if (string.IsNullOrEmpty(statsigId))
            {
                // Thử scroll/tương tác để trigger request
                onProgress?.Invoke("🔄 Đang trigger request...");
                await TriggerStatsigRequestAsync(chrome);

                // Chờ thêm 8 giây
                var tcs2 = tcs.Task.IsCompleted ? tcs : new TaskCompletionSource<string>();
                await Task.Delay(8000);

                statsigId = tcs.Task.IsCompleted ? await tcs.Task : null;
            }

            if (string.IsNullOrEmpty(statsigId))
                return StatsigResult.Fail("Không lấy được x-statsig-id. Có thể chưa login Grok.");

            // Lấy cookie của Grok
            var cookie = await chrome.GetCookieStringAsync("https://grok.com");

            var result = new StatsigResult
            {
                Ok        = true,
                StatsigId = statsigId,
                Cookie    = cookie,
                FetchedAt = DateTime.UtcNow
            };

            // Lưu cache
            SaveToCache(result);
            onProgress?.Invoke($"✅ Lấy được x-statsig-id!");
            _logger.LogInformation("StatsigId: {Id}", Truncate(statsigId));

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi lấy statsig");
            return StatsigResult.Fail($"Lỗi: {ex.Message}");
        }
        finally
        {
            await chrome.DisposeAsync();
        }
    }

    // ─── JS / Trigger helpers ─────────────────────────────────

    /// <summary>
    /// Thử lấy statsig ID qua JS — đọc từ window object hoặc cookie
    /// </summary>
    private async Task<string?> TryGetStatsigViaJsAsync(StealthChrome chrome)
    {
        try
        {
            var result = await chrome.EvaluateAsync<string>(@"
                (() => {
                    // Thử lấy từ window.__statsig hoặc các biến global
                    const candidates = [
                        window.__STATSIG_SDK__?.getCurrentUser?.()?.statsigId,
                        window.__statsig?.getCurrentUser?.()?.statsigId,
                        window.__statsig_id,
                        window.statsigId
                    ];
                    for (const c of candidates) {
                        if (c && typeof c === 'string' && c.length > 5) return c;
                    }

                    // Thử đọc từ cookie
                    const cookies = document.cookie.split(';');
                    for (const c of cookies) {
                        const [k, v] = c.trim().split('=');
                        if (k && k.toLowerCase().includes('statsig')) return v;
                    }

                    return null;
                })()
            ");
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tương tác với trang để trigger statsig network call
    /// </summary>
    private async Task TriggerStatsigRequestAsync(StealthChrome chrome)
    {
        try
        {
            // Nhập text vào ô prompt để trigger request
            await chrome.EvaluateAsync<object>(@"
                (() => {
                    // Tìm textarea hoặc input để trigger interaction
                    const inputs = document.querySelectorAll('textarea, input[type=""text""]');
                    if (inputs.length > 0) {
                        inputs[0].focus();
                        inputs[0].dispatchEvent(new Event('focus', { bubbles: true }));
                        inputs[0].dispatchEvent(new Event('click', { bubbles: true }));
                    }

                    // Trigger scroll để load thêm content
                    window.scrollTo(0, 100);
                    setTimeout(() => window.scrollTo(0, 0), 500);
                })()
            ");
            await Task.Delay(2000);
        }
        catch { }
    }

    // ─── Cache ────────────────────────────────────────────────

    /// <summary>
    /// Lưu statsig result vào file cache (hợp lệ 24 giờ)
    /// </summary>
    private void SaveToCache(StatsigResult result)
    {
        try
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(StatsigCachePath, json);
            _logger.LogDebug("Đã lưu statsig cache: {Path}", StatsigCachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lưu được statsig cache");
        }
    }

    /// <summary>
    /// Đọc cache — trả null nếu hết hạn hoặc không tồn tại
    /// </summary>
    private StatsigResult? LoadFromCache()
    {
        try
        {
            if (!File.Exists(StatsigCachePath)) return null;

            var json = File.ReadAllText(StatsigCachePath);
            var cached = JsonSerializer.Deserialize<StatsigResult>(json);
            if (cached == null || !cached.Ok) return null;

            // Cache hợp lệ trong 24 giờ
            if (DateTime.UtcNow - cached.FetchedAt > TimeSpan.FromHours(24))
            {
                _logger.LogDebug("Statsig cache hết hạn");
                return null;
            }

            return cached;
        }
        catch
        {
            return null;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────

    private static string Truncate(string? s, int len = 12)
        => s == null ? "(null)" :
           s.Length <= len ? s :
           s[..len] + "...";
}

// ─── Models ───────────────────────────────────────────────────

/// <summary>
/// Kết quả lấy statsig
/// </summary>
public class StatsigResult
{
    public bool      Ok        { get; set; }
    public string    StatsigId { get; set; } = string.Empty;
    public string    Cookie    { get; set; } = string.Empty;
    public string    Error     { get; set; } = string.Empty;
    public DateTime  FetchedAt { get; set; } = DateTime.UtcNow;

    public static StatsigResult Fail(string error) => new()
    {
        Ok    = false,
        Error = error
    };
}
