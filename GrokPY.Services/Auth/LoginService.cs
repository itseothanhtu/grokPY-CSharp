using GrokPY.Core.Models;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Text.Json;

namespace GrokPY.Services.Auth;

/// <summary>
/// Tự động login Google qua Chrome và lấy token
/// Tương đương login.py trong Python gốc
/// Flow: Mở labs.google → Click "Tạo bằng Flow" → Nhập email/pass → Lấy token
/// </summary>
public class LoginService
{
    private readonly ILogger<LoginService> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;

    // Các URL quan trọng
    private const string FlowUrl = "https://labs.google/fx/vi/tools/flow";
    private const string GoogleLoginUrl = "https://accounts.google.com";

    public LoginService(
        ILogger<LoginService> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger = logger;
        _settings = settings;
        _chromeManager = chromeManager;
    }

    // ─── Login chính ───────────────────────────────────────────

    /// <summary>
    /// Thực hiện login Google và lấy token tự động
    /// </summary>
    /// <param name="email">Email Google</param>
    /// <param name="plainPassword">Mật khẩu (sẽ được mã hóa trước khi lưu)</param>
    /// <param name="profileDir">Thư mục Chrome profile</param>
    /// <param name="onProgress">Callback báo tiến độ</param>
    public async Task<LoginResult> LoginAsync(
        string email,
        string plainPassword,
        string? profileDir = null,
        Action<string>? onProgress = null)
    {
        // Tìm Chrome
        var chromePath = _chromeManager.FindChromePath();
        if (chromePath == null)
        {
            var msg = "Không tìm thấy Chrome trên máy!";
            _logger.LogError(msg);
            return LoginResult.Fail(msg);
        }

        // Profile dir mặc định
        profileDir ??= _settings.GetChromeProfileDir();

        var chrome = new StealthChrome(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StealthChrome>.Instance);

        try
        {
            onProgress?.Invoke("🚀 Đang khởi động Chrome...");
            _logger.LogInformation("Bắt đầu login: {Email}", email);

            // Khởi động Chrome với profile riêng
            await chrome.LaunchAsync(chromePath, profileDir, headless: false);

            // Thiết lập intercept để bắt token
            var tokenExtractor = new TokenExtractor(_logger);
            tokenExtractor.AttachToPage(chrome.Page!);

            onProgress?.Invoke("🌐 Đang mở Google Flow...");

            // Mở trang Flow
            await chrome.NavigateAsync(FlowUrl, timeoutMs: 20000);
            await Task.Delay(2000);

            // Kiểm tra đã login chưa (có thể session còn)
            if (await IsAlreadyLoggedInAsync(chrome))
            {
                onProgress?.Invoke("✅ Đã login sẵn, đang lấy token...");
                _logger.LogInformation("Phát hiện session còn tồn tại");

                // Reload để trigger token requests
                await chrome.NavigateAsync(FlowUrl);
                await Task.Delay(3000);

                var existingTokens = tokenExtractor.GetTokens();
                if (existingTokens.IsValid)
                {
                    await SaveTokensAsync(email, plainPassword, existingTokens);
                    onProgress?.Invoke("✅ Lấy token thành công!");
                    return LoginResult.Success(existingTokens);
                }
            }

            // Chưa login → thực hiện login flow
            onProgress?.Invoke("🔐 Đang thực hiện đăng nhập...");

            var loginOk = await DoGoogleLoginAsync(chrome, email, plainPassword, onProgress);
            if (!loginOk)
                return LoginResult.Fail("Đăng nhập thất bại. Kiểm tra email/password.");

            // Chờ redirect về Flow sau khi login
            onProgress?.Invoke("⏳ Đang chờ redirect về Flow...");
            await WaitForFlowRedirectAsync(chrome, timeoutMs: 30000);
            await Task.Delay(3000);

            // Lấy tokens đã bắt được
            var tokens = tokenExtractor.GetTokens();

            // Nếu chưa có projectId → thử navigate lại
            if (string.IsNullOrEmpty(tokens.ProjectId))
            {
                onProgress?.Invoke("🔄 Đang lấy Project ID...");
                await chrome.NavigateAsync(FlowUrl);
                await Task.Delay(3000);
                tokens = tokenExtractor.GetTokens();
            }

            if (!tokens.IsValid)
            {
                // Thử lấy token qua JS trực tiếp
                onProgress?.Invoke("🔄 Đang trích xuất token từ page...");
                tokens = await ExtractTokensFromPageAsync(chrome);
            }

            if (!tokens.IsValid)
                return LoginResult.Fail("Không lấy được token. Thử login lại.");

            await SaveTokensAsync(email, plainPassword, tokens);
            onProgress?.Invoke("✅ Đăng nhập thành công!");
            _logger.LogInformation("Login thành công. ProjectId={Id}", tokens.ProjectId);

            return LoginResult.Success(tokens);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi login");
            return LoginResult.Fail($"Lỗi: {ex.Message}");
        }
        finally
        {
            await chrome.DisposeAsync();
        }
    }

    // ─── Google Login Flow ────────────────────────────────────

    /// <summary>
    /// Thực hiện các bước nhập email và password vào trang Google Login
    /// </summary>
    private async Task<bool> DoGoogleLoginAsync(
        StealthChrome chrome,
        string email,
        string password,
        Action<string>? onProgress)
    {
        try
        {
            // Bước 1: Tìm và click nút "Tạo bằng Flow" hoặc "Sign in"
            onProgress?.Invoke("🖱️ Đang tìm nút đăng nhập...");

            // Thử click nút sign-in trên labs.google
            var signInClicked = await TryClickSignInButtonAsync(chrome);
            if (!signInClicked)
            {
                // Navigate thẳng tới Google login nếu không tìm thấy nút
                await chrome.NavigateAsync($"{GoogleLoginUrl}/signin/v2/identifier?hl=vi");
                await Task.Delay(2000);
            }

            // Bước 2: Nhập email
            onProgress?.Invoke("📧 Đang nhập email...");
            var emailOk = await TypeInFieldAsync(chrome, "input[type='email']", email);
            if (!emailOk)
            {
                _logger.LogWarning("Không tìm thấy field email");
                return false;
            }

            // Click Next sau email
            await ClickNextButtonAsync(chrome);
            await Task.Delay(2500);

            // Bước 3: Nhập password
            onProgress?.Invoke("🔑 Đang nhập mật khẩu...");
            var passOk = await TypeInFieldAsync(chrome, "input[type='password']", password);
            if (!passOk)
            {
                _logger.LogWarning("Không tìm thấy field password");
                return false;
            }

            // Click Next sau password
            await ClickNextButtonAsync(chrome);
            await Task.Delay(3000);

            _logger.LogInformation("Đã submit credentials");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi trong quá trình login");
            return false;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra đã login Google chưa dựa trên URL hoặc DOM
    /// </summary>
    private async Task<bool> IsAlreadyLoggedInAsync(StealthChrome chrome)
    {
        try
        {
            var url = await chrome.EvaluateAsync<string>("window.location.href");
            // Nếu URL chứa /flow hoặc /tools → đã login
            if (url != null && (url.Contains("/flow") || url.Contains("/tools")))
                return true;

            // Kiểm tra không có form login
            var hasLoginForm = await chrome.EvaluateAsync<bool>(
                "document.querySelector('input[type=\"email\"]') !== null");
            return !hasLoginForm;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Click nút Sign In / Tạo bằng Flow trên trang labs.google
    /// </summary>
    private async Task<bool> TryClickSignInButtonAsync(StealthChrome chrome)
    {
        // Thử các selector phổ biến
        var selectors = new[]
        {
            "a[href*='sign_in']",
            "button[data-action*='sign']",
            "a[href*='accounts.google']",
            "[aria-label*='Sign in']",
            "button:contains('Sign in')"
        };

        foreach (var selector in selectors)
        {
            try
            {
                var el = await chrome.WaitForSelectorAsync(selector, timeoutMs: 2000);
                if (el != null)
                {
                    await el.ClickAsync();
                    await Task.Delay(2000);
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Nhập text vào field
    /// </summary>
    private async Task<bool> TypeInFieldAsync(StealthChrome chrome, string selector, string text)
    {
        var el = await chrome.WaitForSelectorAsync(selector, timeoutMs: 10000);
        if (el == null) return false;

        await el.ClickAsync();
        await Task.Delay(300);
        await el.TypeAsync(text);
        return true;
    }

    /// <summary>
    /// Click nút Next / Tiếp theo trên trang Google Login
    /// </summary>
    private async Task ClickNextButtonAsync(StealthChrome chrome)
    {
        var nextSelectors = new[]
        {
            "#identifierNext",
            "#passwordNext",
            "button[jsname='LgbsSe']",
            "div[jsname='Njthtb']",
            "button[type='submit']"
        };

        foreach (var selector in nextSelectors)
        {
            try
            {
                var el = await chrome.WaitForSelectorAsync(selector, timeoutMs: 3000);
                if (el != null)
                {
                    await el.ClickAsync();
                    return;
                }
            }
            catch { }
        }

        // Fallback: nhấn Enter
        await chrome.EvaluateAsync<object>("document.activeElement.form?.submit()");
    }

    /// <summary>
    /// Chờ redirect về trang Flow sau khi login xong
    /// </summary>
    private async Task WaitForFlowRedirectAsync(StealthChrome chrome, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var url = await chrome.EvaluateAsync<string>("window.location.href");
                if (url != null && (
                    url.Contains("labs.google") ||
                    url.Contains("flow") ||
                    url.Contains("fx")))
                {
                    _logger.LogInformation("Đã redirect về: {Url}", url);
                    return;
                }
            }
            catch { }
            await Task.Delay(500);
        }
        _logger.LogWarning("Timeout chờ redirect về Flow");
    }

    /// <summary>
    /// Trích xuất token trực tiếp từ page context qua JS
    /// Dùng khi intercept không bắt được
    /// </summary>
    private async Task<GoogleTokens> ExtractTokensFromPageAsync(StealthChrome chrome)
    {
        try
        {
            // Lấy cookie
            var cookieStr = await chrome.GetCookieStringAsync("https://labs.google");

            // Thử lấy access_token từ localStorage hoặc session storage
            var tokenFromStorage = await chrome.EvaluateAsync<string>(@"
                (() => {
                    // Tìm trong sessionStorage
                    for (let i = 0; i < sessionStorage.length; i++) {
                        const key = sessionStorage.key(i);
                        const val = sessionStorage.getItem(key);
                        if (val && val.includes('access_token')) {
                            try { const obj = JSON.parse(val); return obj.access_token || ''; }
                            catch {}
                        }
                    }
                    return '';
                })()
            ");

            return new GoogleTokens
            {
                Cookie = cookieStr ?? string.Empty,
                AccessToken = tokenFromStorage ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lấy được token từ page");
            return new GoogleTokens();
        }
    }

    /// <summary>
    /// Lưu tokens và credentials vào SettingsManager
    /// </summary>
    private async Task SaveTokensAsync(string email, string plainPassword, GoogleTokens tokens)
    {
        // Lưu account credentials (password được mã hóa DPAPI trong SettingsManager)
        _settings.SaveAccount(email, plainPassword);

        // Lưu tokens
        _settings.SaveTokens(
            tokens.SessionId,
            tokens.ProjectId,
            tokens.AccessToken,
            tokens.Cookie);

        await Task.CompletedTask;
    }
}

// ─── Result / Token Models ────────────────────────────────────

/// <summary>
/// Kết quả login
/// </summary>
public class LoginResult
{
    public bool Ok { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public GoogleTokens? Tokens { get; private set; }

    public static LoginResult Success(GoogleTokens tokens) => new()
    {
        Ok = true,
        Message = "Đăng nhập thành công",
        Tokens = tokens
    };

    public static LoginResult Fail(string message) => new()
    {
        Ok = false,
        Message = message
    };
}

/// <summary>
/// Token Google AI (access_token, sessionId, projectId, cookie)
/// </summary>
public class GoogleTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;

    /// <summary>
    /// Token hợp lệ khi có ít nhất AccessToken hoặc Cookie
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrEmpty(AccessToken) ||
        !string.IsNullOrEmpty(Cookie);
}
