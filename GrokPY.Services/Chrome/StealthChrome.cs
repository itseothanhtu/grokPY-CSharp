using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace GrokPY.Services.Chrome;

/// <summary>
/// Khởi động Chrome thật + patch để ẩn automation flags
/// Tương đương grok_chrome_manager.py trong Python gốc
/// Kỹ thuật: PuppeteerSharp kết nối CDP trực tiếp, không dùng WebDriver
/// </summary>
public class StealthChrome : IAsyncDisposable
{
    private readonly ILogger<StealthChrome> _logger;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _disposed;

    // JS patch để ẩn automation — inject trước khi page load
    private const string StealthScript = """
        // Ẩn navigator.webdriver
        Object.defineProperty(navigator, 'webdriver', {
            get: () => undefined,
            configurable: true
        });

        // Fake plugins list (browser thật có plugins)
        Object.defineProperty(navigator, 'plugins', {
            get: () => {
                return [
                    { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer' },
                    { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai' },
                    { name: 'Native Client', filename: 'internal-nacl-plugin' }
                ];
            }
        });

        // Fake languages
        Object.defineProperty(navigator, 'languages', {
            get: () => ['vi-VN', 'vi', 'en-US', 'en']
        });

        // Fake chrome runtime (browser thật có window.chrome)
        if (!window.chrome) {
            window.chrome = {
                runtime: {
                    connect: () => {},
                    sendMessage: () => {}
                },
                loadTimes: () => {},
                csi: () => {}
            };
        }

        // Ẩn Notification.permission automation artifact
        const originalQuery = window.navigator.permissions.query;
        window.navigator.permissions.query = (parameters) => (
            parameters.name === 'notifications' ?
                Promise.resolve({ state: Notification.permission }) :
                originalQuery(parameters)
        );
    """;

    public StealthChrome(ILogger<StealthChrome> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Browser instance (sau khi LaunchAsync)
    /// </summary>
    public IBrowser? Browser => _browser;

    /// <summary>
    /// Page mặc định (sau khi LaunchAsync)
    /// </summary>
    public IPage? Page => _page;

    // ─── Launch ────────────────────────────────────────────────────

    /// <summary>
    /// Khởi động Chrome với stealth patches
    /// </summary>
    /// <param name="chromePath">Đường dẫn chrome.exe</param>
    /// <param name="profileDir">Thư mục profile (giữ session đăng nhập)</param>
    /// <param name="startUrl">URL mở lúc khởi động</param>
    /// <param name="headless">Chạy ẩn hay không</param>
    public async Task LaunchAsync(
        string chromePath,
        string profileDir,
        string startUrl = "about:blank",
        bool headless = false)
    {
        _logger.LogInformation("Đang khởi động Chrome: {Path}", chromePath);
        _logger.LogInformation("Profile: {Dir}", profileDir);

        Directory.CreateDirectory(profileDir);

        var args = new List<string>
        {
            // ── Stealth flags ──
            "--disable-blink-features=AutomationControlled",
            "--exclude-switches=enable-automation",

            // ── Tắt các thứ không cần thiết ──
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",

            // ── Performance ──
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",

            // ── Remote allow origins cho CDP ──
            "--remote-allow-origins=*",

            // ── Ngôn ngữ ──
            "--lang=vi-VN",

            // ── Window size ──
            "--window-size=1280,860"
        };

        if (headless)
        {
            // headless=new ít bị detect hơn --headless cũ
            args.Add("--headless=new");
            args.Add("--disable-gpu");
            args.Add("--no-sandbox");
            args.Add("--disable-dev-shm-usage");
        }

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = chromePath,
            UserDataDir = profileDir,
            Headless = false,       // PuppeteerSharp Headless property (tách biệt với --headless arg)
            Args = args.ToArray(),
            DefaultViewport = null  // Dùng kích thước window thật
        });

        _logger.LogInformation("Chrome đã khởi động. Endpoints: {Ep}", _browser.WebSocketEndpoint);

        // Lấy page đầu tiên hoặc tạo mới
        var pages = await _browser.PagesAsync();
        _page = pages.Length > 0 ? pages[0] : await _browser.NewPageAsync();

        // Inject stealth script trước khi page nào load
        // PuppeteerSharp v20+: dùng AddScriptToEvaluateOnNewDocumentAsync
        await _page.Client.SendAsync("Page.addScriptToEvaluateOnNewDocument",
            new { source = StealthScript });

        // Set realistic User-Agent
        await _page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36"
        );

        if (startUrl != "about:blank")
        {
            await NavigateAsync(startUrl);
        }

        _logger.LogInformation("StealthChrome sẵn sàng");
    }

    // ─── Navigation ────────────────────────────────────────────────

    /// <summary>
    /// Navigate đến URL
    /// </summary>
    public async Task NavigateAsync(string url, int timeoutMs = 30000)
    {
        EnsureReady();
        _logger.LogDebug("Navigate: {Url}", url);
        try
        {
            await _page!.GoToAsync(url, new NavigationOptions
            {
                Timeout = timeoutMs,
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Navigate timeout hoặc lỗi: {Url}", url);
        }
    }

    // ─── JavaScript Execution ─────────────────────────────────────

    /// <summary>
    /// Chạy JS trong context của browser (có cookie/session tự đính kèm)
    /// Đây là cách gọi Grok API từ C# — giống Python gốc
    /// </summary>
    public async Task<T?> EvaluateAsync<T>(string jsExpression)
    {
        EnsureReady();
        return await _page!.EvaluateExpressionAsync<T>(jsExpression);
    }

    /// <summary>
    /// Chạy JS function với tham số
    /// </summary>
    public async Task<T?> EvaluateFunctionAsync<T>(string jsFunction, params object[] args)
    {
        EnsureReady();
        return await _page!.EvaluateFunctionAsync<T>(jsFunction, args);
    }

    /// <summary>
    /// Gọi fetch() trong browser context — dùng session/cookie hiện tại
    /// </summary>
    public async Task<string?> FetchAsync(
        string url,
        string method = "POST",
        object? payload = null,
        Dictionary<string, string>? extraHeaders = null)
    {
        EnsureReady();

        var payloadJson = payload != null
            ? JsonSerializer.Serialize(payload)
            : "null";

        var headersJson = "{}";
        if (extraHeaders != null)
            headersJson = JsonSerializer.Serialize(extraHeaders);

        var js = $$"""
            async () => {
                try {
                    const headers = Object.assign(
                        { 'content-type': 'application/json' },
                        {{headersJson}}
                    );
                    const opts = {
                        method: '{{method}}',
                        headers: headers,
                        credentials: 'include'
                    };
                    if ('{{method}}' !== 'GET' && {{payloadJson}} !== null) {
                        opts.body = JSON.stringify({{payloadJson}});
                    }
                    const res = await fetch('{{url}}', opts);
                    return JSON.stringify({
                        ok: res.ok,
                        status: res.status,
                        body: await res.text()
                    });
                } catch (e) {
                    return JSON.stringify({ ok: false, status: 0, error: String(e) });
                }
            }
            """;

        return await _page!.EvaluateFunctionAsync<string>(js);
    }

    // ─── Cookie & Headers ─────────────────────────────────────────

    /// <summary>
    /// Lấy tất cả cookie của một domain
    /// </summary>
    public async Task<CookieParam[]> GetCookiesAsync(string url)
    {
        EnsureReady();
        return await _page!.GetCookiesAsync(url);
    }

    /// <summary>
    /// Lấy cookie dạng string "name=value; name2=value2"
    /// </summary>
    public async Task<string> GetCookieStringAsync(string url)
    {
        var cookies = await GetCookiesAsync(url);
        return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>
    /// Lắng nghe request để lấy headers (dùng cho StatsigDiscovery)
    /// </summary>
    public void OnRequest(Action<IRequest> handler)
    {
        EnsureReady();
        _page!.Request += (_, e) => handler(e.Request);
    }

    /// <summary>
    /// Lắng nghe response
    /// </summary>
    public void OnResponse(Action<IResponse> handler)
    {
        EnsureReady();
        _page!.Response += (_, e) => handler(e.Response);
    }

    // ─── Page interaction ─────────────────────────────────────────

    /// <summary>
    /// Chờ selector xuất hiện
    /// </summary>
    public async Task<IElementHandle?> WaitForSelectorAsync(string selector, int timeoutMs = 15000)
    {
        EnsureReady();
        try
        {
            return await _page!.WaitForSelectorAsync(selector, new WaitForSelectorOptions
            {
                Timeout = timeoutMs,
                Visible = true
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tạo page mới (tab mới)
    /// </summary>
    public async Task<IPage> NewPageAsync()
    {
        EnsureReady();
        var page = await _browser!.NewPageAsync();
        // PuppeteerSharp v20+: dùng CDP trực tiếp
        await page.Client.SendAsync("Page.addScriptToEvaluateOnNewDocument",
            new { source = StealthScript });
        return page;
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private void EnsureReady()
    {
        if (_browser == null || _page == null)
            throw new InvalidOperationException(
                "StealthChrome chưa được khởi động. Gọi LaunchAsync() trước.");
    }

    // ─── Dispose ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi đóng Chrome");
        }
    }
}